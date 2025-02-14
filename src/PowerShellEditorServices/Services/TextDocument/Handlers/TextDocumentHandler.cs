// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Services.TextDocument;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;

namespace Microsoft.PowerShell.EditorServices.Handlers
{
    class PsesTextDocumentHandler : TextDocumentSyncHandlerBase
    {
        private static readonly Uri s_fakeUri = new Uri("Untitled:fake");

        private readonly ILogger _logger;
        private readonly AnalysisService _analysisService;
        private readonly WorkspaceService _workspaceService;
        private readonly RemoteFileManagerService _remoteFileManagerService;

        public static TextDocumentSyncKind Change => TextDocumentSyncKind.Incremental;

        public PsesTextDocumentHandler(
            ILoggerFactory factory,
            AnalysisService analysisService,
            WorkspaceService workspaceService,
            RemoteFileManagerService remoteFileManagerService)
        {
            _logger = factory.CreateLogger<PsesTextDocumentHandler>();
            _analysisService = analysisService;
            _workspaceService = workspaceService;
            _remoteFileManagerService = remoteFileManagerService;
        }

        public override Task<Unit> Handle(DidChangeTextDocumentParams notification, CancellationToken token)
        {
            ScriptFile changedFile = _workspaceService.GetFile(notification.TextDocument.Uri);

            // A text change notification can batch multiple change requests
            foreach (TextDocumentContentChangeEvent textChange in notification.ContentChanges)
            {
                changedFile.ApplyChange(
                    GetFileChangeDetails(
                        textChange.Range,
                        textChange.Text));
            }

            // Kick off script diagnostics without blocking the response
            // TODO: Get all recently edited files in the workspace
            _analysisService.StartScriptDiagnostics(new ScriptFile[] { changedFile });
            return Unit.Task;
        }

        protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(SynchronizationCapability capability, ClientCapabilities clientCapabilities) => new TextDocumentSyncRegistrationOptions()
        {
            DocumentSelector = LspUtils.PowerShellDocumentSelector,
            Change = Change,
            Save = new SaveOptions { IncludeText = true }
        };

        public override Task<Unit> Handle(DidOpenTextDocumentParams notification, CancellationToken token)
        {
            ScriptFile openedFile =
                _workspaceService.GetFileBuffer(
                    notification.TextDocument.Uri,
                    notification.TextDocument.Text);

            if (LspUtils.PowerShellDocumentSelector.IsMatch(new TextDocumentAttributes(
                // We use a fake Uri because we only want to test the LanguageId here and not if the
                // file ends in ps*1.
                s_fakeUri,
                notification.TextDocument.LanguageId)))
            {
                // Kick off script diagnostics if we got a PowerShell file without blocking the response
                // TODO: Get all recently edited files in the workspace
                _analysisService.StartScriptDiagnostics(new ScriptFile[] { openedFile });
            }

            _logger.LogTrace("Finished opening document.");
            return Unit.Task;
        }

        public override Task<Unit> Handle(DidCloseTextDocumentParams notification, CancellationToken token)
        {
            // Find and close the file in the current session
            var fileToClose = _workspaceService.GetFile(notification.TextDocument.Uri);

            if (fileToClose != null)
            {
                _workspaceService.CloseFile(fileToClose);
                _analysisService.ClearMarkers(fileToClose);
            }

            _logger.LogTrace("Finished closing document.");
            return Unit.Task;
        }

        public override async Task<Unit> Handle(DidSaveTextDocumentParams notification, CancellationToken token)
        {
            ScriptFile savedFile = _workspaceService.GetFile(notification.TextDocument.Uri);

            if (savedFile != null)
            {
                if (_remoteFileManagerService.IsUnderRemoteTempPath(savedFile.FilePath))
                {
                    await _remoteFileManagerService.SaveRemoteFileAsync(savedFile.FilePath).ConfigureAwait(false);
                }
            }
            return Unit.Value;
        }

        public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) => new TextDocumentAttributes(uri, "powershell");

        private static FileChange GetFileChangeDetails(Range changeRange, string insertString)
        {
            // The protocol's positions are zero-based so add 1 to all offsets

            if (changeRange == null) return new FileChange { InsertString = insertString, IsReload = true };

            return new FileChange
            {
                InsertString = insertString,
                Line = changeRange.Start.Line + 1,
                Offset = changeRange.Start.Character + 1,
                EndLine = changeRange.End.Line + 1,
                EndOffset = changeRange.End.Character + 1,
                IsReload = false
            };
        }
    }
}
