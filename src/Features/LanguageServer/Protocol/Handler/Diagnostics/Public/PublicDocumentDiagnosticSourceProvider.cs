﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.Public;

[ExportDiagnosticSourceProvider, Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class PublicDocumentDiagnosticSourceProvider(
    [Import] IDiagnosticAnalyzerService diagnosticAnalyzerService)
    : AbstractDocumentDiagnosticSourceProvider(All)
{
    public const string All = "All_B69807DB-28FB-4846-884A-1152E54C8B62";

    public override ValueTask<ImmutableArray<IDiagnosticSource>> CreateDiagnosticSourcesAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var textDocument = AbstractDocumentDiagnosticSourceProvider.GetOpenDocument(context);
        if (textDocument is null)
            return new([]);

        var source = new DocumentDiagnosticSource(diagnosticAnalyzerService, DiagnosticKind.All /* IS THIS RIGHT ???*/, textDocument);
        return new([source]);
    }
}
