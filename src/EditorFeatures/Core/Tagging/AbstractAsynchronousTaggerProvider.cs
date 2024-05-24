﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
#if DEBUG
using System.Diagnostics;
#endif
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Tagging;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Tagging;

/// <summary>
/// Base type of all asynchronous tagger providers (<see cref="ITaggerProvider"/> and <see cref="IViewTaggerProvider"/>). 
/// </summary>
internal abstract partial class AbstractAsynchronousTaggerProvider<TTag> where TTag : ITag
{
    private readonly object _uniqueKey = new();

    protected readonly IAsynchronousOperationListener AsyncListener;
    protected readonly IThreadingContext ThreadingContext;
    protected readonly IGlobalOptionService GlobalOptions;

    private readonly ITextBufferVisibilityTracker? _visibilityTracker;
    private readonly TaggerMainThreadManager _mainThreadManager;

    /// <summary>
    /// The behavior the tagger engine will have when text changes happen to the subject buffer it is attached to.  Most
    /// taggers can simply use <see cref="TaggerTextChangeBehavior.None"/>. However, advanced taggers that want to
    /// perform specialized behavior depending on what has actually changed in the file can specify that here.
    /// </summary>
    protected virtual TaggerTextChangeBehavior TextChangeBehavior => TaggerTextChangeBehavior.None;

    /// <summary>
    /// The behavior the tagger will have when changes happen to the caret.
    /// </summary>
    protected virtual TaggerCaretChangeBehavior CaretChangeBehavior => TaggerCaretChangeBehavior.None;

    /// <summary>
    /// The behavior of tags that are created by the async tagger.  This will matter for tags
    /// created for a previous version of a document that are mapped forward by the async
    /// tagging architecture.  This value cannot be <see cref="SpanTrackingMode.Custom"/>.
    /// </summary>
    protected virtual SpanTrackingMode SpanTrackingMode => SpanTrackingMode.EdgeExclusive;

    /// <summary>
    /// Global options controlling if the tagger should tag or not.  These correspond to user facing options to
    /// completely disable a feature or not.
    /// <para>
    /// An empty enumerable can be returned to indicate that this tagger should run unconditionally.</para>
    /// </summary>
    /// <remarks>All values must either be an <see cref="Option2{T}"/> or a <see cref="PerLanguageOption2{T}"/>.</remarks>
    protected virtual ImmutableArray<IOption2> Options => [];

    /// <summary>
    /// Options controlling the feature that should be used to determine if the feature should recompute tags.
    /// These generally correspond to user facing options to change how a feature behaves if it is running.
    /// </summary>
    protected virtual ImmutableArray<IOption2> FeatureOptions => [];

    protected virtual bool ComputeInitialTagsSynchronously(ITextBuffer subjectBuffer) => false;

    /// <summary>
    /// How long the tagger should wait after hearing about an event before recomputing tags.
    /// </summary>
    protected abstract TaggerDelay EventChangeDelay { get; }

    /// <summary>
    /// This controls what delay tagger will use to let editor know about newly inserted tags
    /// </summary>
    protected virtual TaggerDelay AddedTagNotificationDelay => TaggerDelay.NearImmediate;

    /// <summary>
    /// Whether or not events from the <see cref="ITaggerEventSource"/> should cancel in-flight tag-computation.
    /// </summary>
    protected virtual bool CancelOnNewWork { get; }

    /// <summary>
    /// Whether or not this tagger would like to use frozen-partial snapshots to compute tags. If <see
    /// langword="false"/>, tagging behaves normally, with a single call to <see
    /// cref="ProduceTagsAsync(TaggerContext{TTag}, CancellationToken)"/> after a batch of events comes in.  If <see
    /// langword="true"/> then tagging will happen in two passes.  A first pass operating with frozen documents,
    /// allowing the tagger to actually compute tags quickly, without waiting on skeleton references or source generated
    /// documents to be up to date.  Followed by a second, slower, pass on non-frozen documents that will then produce
    /// the final accurate tags.  Because this second pass is more expensive, it will be aggressively canceled and
    /// pushed to the end when new normal work comes in.  That way, when the user is doing things like typing, they'll
    /// continuously be getting frozen-partial results quickly, but always with the final, full, correct results coming
    /// at the end once enough idle time has passed.
    /// </summary>
    protected virtual bool SupportsFrozenPartialSemantics => false;

    protected virtual void BeforeTagsChanged(ITextSnapshot snapshot)
    {
    }

    /// <summary>
    /// Comparer used to check if two tags are the same.  Used so that when new tags are produced, they can be
    /// appropriately 'diffed' to determine what changes to actually report in <see cref="ITagger{T}.TagsChanged"/>.
    /// <para>
    /// Subclasses should always override this.  It is only virtual for binary compat.
    /// </para>
    /// </summary>
    protected virtual bool TagEquals(TTag tag1, TTag tag2)
        => EqualityComparer<TTag>.Default.Equals(tag1, tag2);

    // Prevent accidental usage of object.Equals instead of TagEquals when comparing tags.
    [Obsolete("Did you mean to call TagEquals(TTag tag1, TTag tag2) instead", error: true)]
    public static new bool Equals(object objA, object objB)
        => throw ExceptionUtilities.Unreachable();

#if DEBUG
    public readonly string StackTrace;
#endif

    protected AbstractAsynchronousTaggerProvider(
        IThreadingContext threadingContext,
        IGlobalOptionService globalOptions,
        ITextBufferVisibilityTracker? visibilityTracker,
        IAsynchronousOperationListenerProvider asyncListenerProvider,
        string featureName)
    {
        ThreadingContext = threadingContext;
        GlobalOptions = globalOptions;
        AsyncListener = asyncListenerProvider.GetListener(featureName);

        _visibilityTracker = visibilityTracker;
        _mainThreadManager = TaggerMainThreadManager.GetManager(threadingContext, asyncListenerProvider);

#if DEBUG
        StackTrace = new StackTrace().ToString();
#endif
    }

    protected EfficientTagger<TTag>? CreateEfficientTagger(ITextView? textView, ITextBuffer subjectBuffer)
    {
        if (!GlobalOptions.GetOption(EditorComponentOnOffOptions.Tagger))
            return null;

        var tagSource = GetOrCreateTagSource(textView, subjectBuffer);
        var tagger = new Tagger(tagSource);

        return tagger;
    }

    private TagSource GetOrCreateTagSource(ITextView? textView, ITextBuffer subjectBuffer)
    {
        if (!this.TryRetrieveTagSource(textView, subjectBuffer, out var tagSource))
        {
            tagSource = new TagSource(textView, subjectBuffer, _visibilityTracker, this, AsyncListener);
            this.StoreTagSource(textView, subjectBuffer, tagSource);
        }

        return tagSource;
    }

    private bool TryRetrieveTagSource(ITextView? textView, ITextBuffer subjectBuffer, [NotNullWhen(true)] out TagSource? tagSource)
    {
        return textView != null
            ? textView.TryGetPerSubjectBufferProperty(subjectBuffer, _uniqueKey, out tagSource)
            : subjectBuffer.Properties.TryGetProperty(_uniqueKey, out tagSource);
    }

    private void RemoveTagSource(ITextView? textView, ITextBuffer subjectBuffer)
    {
        if (textView != null)
        {
            textView.RemovePerSubjectBufferProperty<TagSource, ITextView>(subjectBuffer, _uniqueKey);
        }
        else
        {
            subjectBuffer.Properties.RemoveProperty(_uniqueKey);
        }
    }

    private void StoreTagSource(ITextView? textView, ITextBuffer subjectBuffer, TagSource tagSource)
    {
        if (textView != null)
        {
            textView.AddPerSubjectBufferProperty(subjectBuffer, _uniqueKey, tagSource);
        }
        else
        {
            subjectBuffer.Properties.AddProperty(_uniqueKey, tagSource);
        }
    }

    /// <summary>
    /// Called by the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/> infrastructure to 
    /// determine the caret position.  This value will be passed in as the value to 
    /// <see cref="TaggerContext{TTag}.CaretPosition"/> in the call to
    /// <see cref="ProduceTagsAsync(TaggerContext{TTag}, CancellationToken)"/>.
    /// </summary>
    protected virtual SnapshotPoint? GetCaretPoint(ITextView? textView, ITextBuffer subjectBuffer)
        => textView?.GetCaretPoint(subjectBuffer);

    /// <summary>
    /// Called by the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/> infrastructure to determine
    /// the set of spans that it should asynchronously tag.  This will be called in response to
    /// notifications from the <see cref="ITaggerEventSource"/> that something has changed, and
    /// will only be called from the UI thread.  The tagger infrastructure will then determine
    /// the <see cref="DocumentSnapshotSpan"/>s associated with these <see cref="SnapshotSpan"/>s
    /// and will asynchronously call into <see cref="ProduceTagsAsync(TaggerContext{TTag}, CancellationToken)"/> at some point in
    /// the future to produce tags for these spans.
    /// </summary>
    protected virtual IEnumerable<SnapshotSpan> GetSpansToTag(ITextView? textView, ITextBuffer subjectBuffer)
    {
        // For a standard tagger, the spans to tag is the span of the entire snapshot.
        return [subjectBuffer.CurrentSnapshot.GetFullSpan()];
    }

    /// <summary>
    /// Creates the <see cref="ITaggerEventSource"/> that notifies the <see cref="AbstractAsynchronousTaggerProvider{TTag}"/>
    /// that it should recompute tags for the text buffer after an appropriate <see cref="TaggerDelay"/>.
    /// </summary>
    protected abstract ITaggerEventSource CreateEventSource(ITextView? textView, ITextBuffer subjectBuffer);

    /// <summary>
    /// Produce tags for the given context.
    /// </summary>
    protected virtual async Task ProduceTagsAsync(
        TaggerContext<TTag> context, CancellationToken cancellationToken)
    {
        foreach (var spanToTag in context.SpansToTag)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProduceTagsAsync(
                context, spanToTag,
                GetCaretPosition(context.CaretPosition, spanToTag.SnapshotSpan),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static int? GetCaretPosition(SnapshotPoint? caretPosition, SnapshotSpan snapshotSpan)
    {
        return caretPosition.HasValue && caretPosition.Value.Snapshot == snapshotSpan.Snapshot
            ? caretPosition.Value.Position : null;
    }

    protected virtual Task ProduceTagsAsync(TaggerContext<TTag> context, DocumentSnapshotSpan spanToTag, int? caretPosition, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public bool SpanEquals(ITextSnapshot snapshot1, TextSpan? span1, ITextSnapshot snapshot2, TextSpan? span2)
        => SpanEquals(snapshot1, span1?.ToSpan(), snapshot2, span2?.ToSpan());

    public bool SpanEquals(ITextSnapshot snapshot1, Span? span1, ITextSnapshot snapshot2, Span? span2)
        => SpanEquals(span1 is null ? null : new SnapshotSpan(snapshot1, span1.Value), span2 is null ? null : new SnapshotSpan(snapshot2, span2.Value));

    public bool SpanEquals(SnapshotSpan? span1, SnapshotSpan? span2)
        => TaggerUtilities.SpanEquals(span1, span2, this.SpanTrackingMode);

    internal TestAccessor GetTestAccessor()
        => new(this);

    private readonly struct DiffResult(NormalizedSnapshotSpanCollection? added, NormalizedSnapshotSpanCollection? removed)
    {
        public readonly NormalizedSnapshotSpanCollection Added = added ?? NormalizedSnapshotSpanCollection.Empty;
        public readonly NormalizedSnapshotSpanCollection Removed = removed ?? NormalizedSnapshotSpanCollection.Empty;

        public int Count => Added.Count + Removed.Count;
    }

    internal readonly struct TestAccessor(AbstractAsynchronousTaggerProvider<TTag> provider)
    {
        private readonly AbstractAsynchronousTaggerProvider<TTag> _provider = provider;

        internal Task ProduceTagsAsync(TaggerContext<TTag> context)
            => _provider.ProduceTagsAsync(context, CancellationToken.None);
    }
}
