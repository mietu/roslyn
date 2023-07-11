﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol
{
    using System.Runtime.Serialization;

    using Newtonsoft.Json;

    /// <summary>
    /// Class representing a diagnostic pull request parameter used.
    /// </summary>
    [DataContract]
    public class VSInternalDiagnosticParams
    {
        /// <summary>
        /// Gets or sets the document for which diagnostics are desired.
        /// </summary>
        [DataMember(Name = "_vs_textDocument", IsRequired = true)]
        public TextDocumentIdentifier? TextDocument { get; set; }

        /// <summary>
        /// Gets or sets a value indicating what kind of diagnostic this request is querying for.
        /// </summary>
        [DataMember(Name = "_vs_queryingDiagnosticKind")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public VSInternalDiagnosticKind? QueryingDiagnosticKind { get; set; }

        /// <summary>
        /// Gets or sets the server-generated version number for the diagnostics.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is treated as a black box by the client: it is stored on the client
        /// for each textDocument and sent back to the server when requesting
        /// diagnostics. The server can use this result ID to avoid resending
        /// diagnostics that had previously been sent.</para>
        ///
        /// <para>Note that if a client does request diagnostics that haven’t changed, the
        /// language server should not reply with any diagnostics for that document.
        /// If the client requests diagnostics for a file that has been renamed or
        /// deleted, then the language service should respond with null for the
        /// diagnostics.
        /// Also, if a service is reporting multiple DiagnosticReports for the same
        /// document, then all reports are expected to have the same
        /// previousResultId.</para>
        /// </remarks>
        [DataMember(Name = "_vs_previousResultId")]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? PreviousResultId { get; set; }
    }
}
