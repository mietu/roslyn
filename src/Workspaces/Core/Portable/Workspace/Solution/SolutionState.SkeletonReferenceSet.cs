﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis;

internal partial class SolutionState
{
    private sealed class SkeletonReferenceSet
    {
        private readonly ITemporaryStreamStorage _storage;
        private readonly string? _assemblyName;

        /// <summary>
        /// The documentation provider used to lookup xml docs for any metadata reference we pass out.  See
        /// docs on <see cref="DeferredDocumentationProvider"/> for why this is safe to hold onto despite it
        /// rooting a compilation internally.
        /// </summary>
        private readonly DeferredDocumentationProvider _documentationProvider;

        /// <summary>
        /// The actual assembly metadata produced from the data pointed to in <see cref="_storage"/>.
        /// </summary>
        private readonly AsyncLazy<AssemblyMetadata> _lazyMetadata;

        /// <summary>
        /// Lock this object while reading/writing from it.  Used so we can return the same reference for the same
        /// properties.  While this is isn't strictly necessary (as the important thing to keep the same is the
        /// AssemblyMetadata), this allows higher layers to see that reference instances are the same which allow
        /// reusing the same higher level objects (for example, the set of references a compilation has).
        /// </summary>
        private readonly Dictionary<MetadataReferenceProperties, PortableExecutableReference> _referenceMap = new();

        public SkeletonReferenceSet(
            ITemporaryStreamStorage storage,
            string? assemblyName,
            DeferredDocumentationProvider documentationProvider)
        {
            _storage = storage;
            _assemblyName = assemblyName;
            _documentationProvider = documentationProvider;

            // note: computing the assembly metadata is actually synchronous.  However, this ensures we don't have N
            // threads blocking on a lazy to compute the work.  Instead, we'll only occupy one thread, while any
            // concurrent requests asynchronously wait for that work to be done.
            _lazyMetadata = new AsyncLazy<AssemblyMetadata>(
                c => Task.FromResult(ComputeMetadata(_storage, c)), cacheResult: true);
        }

        private static AssemblyMetadata ComputeMetadata(ITemporaryStreamStorage storage, CancellationToken cancellationToken)
        {
            // first see whether we can use native memory directly.
            var stream = storage.ReadStream(cancellationToken);

            if (stream is ISupportDirectMemoryAccess supportNativeMemory)
            {
                // this is unfortunate that if we give stream, compiler will just re-copy whole content to 
                // native memory again. this is a way to get around the issue by we getting native memory ourselves and then
                // give them pointer to the native memory. also we need to handle lifetime ourselves.
                return AssemblyMetadata.Create(ModuleMetadata.CreateFromImage(supportNativeMemory.GetPointer(), (int)stream.Length, owner: supportNativeMemory));
            }
            else
            {
                // Otherwise, we just let it use stream. Unfortunately, if we give stream, compiler will
                // internally copy it to native memory again. since compiler owns lifetime of stream,
                // it would be great if compiler can be little bit smarter on how it deals with stream.

                // We don't deterministically release the resulting metadata since we don't know 
                // when we should. So we leave it up to the GC to collect it and release all the associated resources.
                return AssemblyMetadata.CreateFromStream(stream, leaveOpen: false);
            }
        }

        public PortableExecutableReference? TryGetAlreadyBuiltMetadataReference(MetadataReferenceProperties properties)
        {
            _lazyMetadata.TryGetValue(out var metadata);
            return CreateMetadataReference(properties, metadata);
        }

        public async Task<PortableExecutableReference?> GetMetadataReferenceAsync(MetadataReferenceProperties properties, CancellationToken cancellationToken)
        {
            var metadata = await _lazyMetadata.GetValueAsync(cancellationToken).ConfigureAwait(false);
            return CreateMetadataReference(properties, metadata);
        }

        private PortableExecutableReference? CreateMetadataReference(
            MetadataReferenceProperties properties, AssemblyMetadata? metadata)
        {
            if (metadata == null)
                return null;

            lock (_referenceMap)
            {
                if (!_referenceMap.TryGetValue(properties, out var value))
                {
                    value = metadata.GetReference(
                        _documentationProvider,
                        aliases: properties.Aliases,
                        embedInteropTypes: properties.EmbedInteropTypes,
                        display: _assemblyName);
                    _referenceMap.Add(properties, value);
                }

                return value;
            }
        }
    }
}
