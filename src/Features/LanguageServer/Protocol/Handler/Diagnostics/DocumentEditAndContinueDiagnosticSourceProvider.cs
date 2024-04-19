﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;

[ExportDiagnosticSourceProvider, Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class DocumentEditAndContinueDiagnosticSourceProvider()
    : AbstractDocumentDiagnosticSourceProvider(PullDiagnosticCategories.EditAndContinue)
{
    public override ValueTask<ImmutableArray<IDiagnosticSource>> CreateDiagnosticSourcesAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (context.GetTrackedDocument<Document>() is not Document document)
            return new([]);

        var source = EditAndContinueDiagnosticSource.CreateOpenDocumentSource(document);
        return new([source]);
    }
}
