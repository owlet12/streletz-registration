// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Streletz.MasterBridge
{
    internal sealed class PanelRequest
    {
        public string host { get; set; }
        public int port { get; set; }
        public string moduleId { get; set; }
        public string panelId { get; set; }
        public string nativePanelId { get; set; }
        public string routeKind { get; set; }
        public bool viaPanelTerminal { get; set; }
        public string terminalPanelId { get; set; }
        public string terminalSegmentId { get; set; }
        public string adapterNodeId { get; set; }
        public string adapterSegmentId { get; set; }
    }

    internal sealed class StartRequest
    {
        public string type { get; set; }
        public string operationId { get; set; }
        public string projectFingerprint { get; set; }
        public string nativeProjectFingerprint { get; set; }
        public string recordId { get; set; }
        public string expectedHardwareId { get; set; }
        public string deviceKey { get; set; }
        public int timeoutSeconds { get; set; }
        public PanelRequest panel { get; set; }
    }

    internal sealed class OperationState
    {
        internal readonly object Sync = new object();
        public StartRequest Request;
        public object TargetNode;
        public object PanelNode;
        public object AdapterNode;
        public object TerminalNode;
        public string RouteKind;
        public string TerminalPanelId;
        public string TerminalSegmentId;
        public string AdapterNodeId;
        public string AdapterSegmentId;
        public string TunnelStartMmpiAddress;
        public string TunnelEndMmpiAddress;
        public Form Dialog;
        public volatile bool CancellationRequested;
        public volatile bool ConfirmationSent;
        public volatile bool NativeSuccessObserved;
        public volatile bool RecordConfirmed;
        public string Status = "queued";
        public string Stage = "validated";
        public string ConfirmedHardwareId;
        public string ErrorCode;
        public string ErrorMessage;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime? FinishedUtc;
        public string NativeProjectFingerprint;
        public string ValidationMode;
        public bool NativeRunFinished;
        public bool NativeCommandStarted;
        internal bool NativeSuccessAcknowledgementAttempted;
        internal bool NativeTimeoutAcknowledgementAttempted;
        internal bool NativeTimeoutDeclined;
        internal DateTime? NativeCompletionStartedUtc;
        internal string NativeCompletionState = "not_started";
        internal string NativeCompletionCheck = "";
        internal string NativeCompletionLastCheck = "";
        internal bool NativeSuccessDialogSuppressed;
        public bool NativeDialogOpen;
        public bool NativeDialogClosed;
        public bool KeyWasSupplied;
        public string InitialKey;
        public string InitialHardwareId;
        public int InitialInfoSubtype;
        public NativeAdapterDiagnostic AdapterDiagnostic;

        public bool IsTerminal
        {
            get { lock (Sync) return Status == "registered" || Status == "failed" || Status == "cancelled" || Status == "unknown"; }
        }

        public void SetStatus(string status, string stage)
        {
            lock (Sync)
            {
                if (Status == "registered" || Status == "failed" || Status == "cancelled") return;
                Status = status;
                Stage = stage;
                if (IsTerminal) FinishedUtc = DateTime.UtcNow;
            }
        }

        public void Fail(string code, string message)
        {
            lock (Sync)
            {
                if (Status == "registered") return;
                ErrorCode = code;
                ErrorMessage = message;
                Status = ConfirmationSent ? "unknown" : "failed";
                Stage = ConfirmationSent ? "confirmation_outcome_unknown" : "rejected";
                FinishedUtc = DateTime.UtcNow;
            }
        }

        public void MarkRegistered(string hardwareId)
        {
            lock (Sync)
            {
                if (!ConfirmationSent || !NativeSuccessObserved || !RecordConfirmed ||
                    !String.Equals(Hex.Normalize(hardwareId), Request.expectedHardwareId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Registration evidence is incomplete.");
                ConfirmedHardwareId = Request.expectedHardwareId;
                ErrorCode = null;
                ErrorMessage = null;
                Status = "registered";
                Stage = "native_registration_confirmed";
                FinishedUtc = DateTime.UtcNow;
            }
        }

        public object Snapshot()
        {
            lock (Sync)
            {
                return new {
                    operationId = Request.operationId, status = Status, stage = Stage,
                    registrationConfirmed = Status == "registered", projectSaved = false,
                    expectedHardwareId = Request.expectedHardwareId,
                    confirmedHardwareId = ConfirmedHardwareId, recordId = Request.recordId,
                    projectFingerprint = Request.projectFingerprint,
                    panelId = Request.panel.panelId, nativePanelId = Request.panel.nativePanelId,
                    host = Request.panel.host, port = Request.panel.port, moduleId = Request.panel.moduleId,
                    routeKind = RouteKind, viaPanelTerminal = RouteKind == NativeRoutePolicy.Terminal,
                    terminalPanelId = TerminalPanelId, terminalSegmentId = TerminalSegmentId,
                    adapterNodeId = AdapterNodeId, adapterSegmentId = AdapterSegmentId,
                    nativeProjectFingerprint = NativeProjectFingerprint, validatedBy = ValidationMode,
                    nativeSuccessObserved = NativeSuccessObserved, nativeRunFinished = NativeRunFinished,
                    nativeDialogOpen = NativeDialogOpen, dialogClosed = NativeDialogClosed,
                    nativeCompletionState = NativeCompletionState, nativeCompletionCheck = NativeCompletionCheck,
                    nativeCompletionLastCheck = NativeCompletionLastCheck,
                    nativeSuccessDialogSuppressed = NativeSuccessDialogSuppressed,
                    nativeTimeoutAcknowledgementAttempted = NativeTimeoutAcknowledgementAttempted,
                    nativeTimeoutDeclined = NativeTimeoutDeclined,
                    confirmationSent = ConfirmationSent, cancellationRequested = CancellationRequested,
                    adapterDiagnostic = AdapterDiagnostic,
                    createdUtc = CreatedUtc.ToString("o"), finishedUtc = FinishedUtc?.ToString("o"),
                    error = ErrorCode == null ? null : new { code = ErrorCode, message = ErrorMessage }
                };
            }
        }
    }

    internal sealed class BridgeException : Exception
    {
        public string Code { get; private set; }
        public BridgeException(string code, string message) : base(message) { Code = code; }
    }

    internal static class Hex
    {
        internal static string Normalize(string text)
        {
            return (text ?? String.Empty).Trim().Replace(" ", "").Replace("-", "").Replace(":", "").ToUpperInvariant();
        }

        internal static string Bytes(byte[] value)
        {
            return value == null ? String.Empty : BitConverter.ToString(value).Replace("-", "");
        }

        internal static bool IsDeviceId(string value)
        {
            return Regex.IsMatch(value ?? String.Empty, "\\A[0-9A-F]{12}\\z") &&
                value != "000000000000" && value != "FFFFFFFFFFFF";
        }
    }
}
