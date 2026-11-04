// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;

namespace Streletz.MasterBridge
{
    internal sealed class NativeSuccessWindow
    {
        internal IntPtr Handle, Owner, Parent, OkButton;
        internal uint ProcessId, ThreadId;
        internal bool Exists, Visible, Enabled;
        internal string ClassName, Text;
        internal int ControlId, ButtonKind;
        internal IList<NativeSuccessWindow> Children = new List<NativeSuccessWindow>();
    }

    internal interface INativeSuccessWindowApi
    {
        uint CurrentProcessId { get; }
        uint CurrentThreadId { get; }
        NativeSuccessWindow ReadIdentity(IntPtr handle);
        IList<IntPtr> EnumerateThreadWindows(uint threadId);
        NativeSuccessWindow ReadMessage(IntPtr handle);
        bool SendOk(IntPtr message, IntPtr button);
    }

    // This gate acknowledges an already proved result. It never sends a
    // registration command or changes registration/native-finish evidence.
    internal sealed class NativeSuccessAcknowledgement
    {
        private readonly INativeSuccessWindowApi windows;
        private readonly Func<DateTime> utcNow;
        internal NativeSuccessAcknowledgement(INativeSuccessWindowApi windows, Func<DateTime> utcNow = null)
        {
            this.windows = windows;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal static bool HasRegistrationProof(OperationState op)
        {
            return op != null && op.Request != null && op.Status == "registered" && op.NativeCommandStarted &&
                !op.NativeRunFinished && op.ConfirmationSent && op.NativeSuccessObserved && op.RecordConfirmed &&
                Hex.IsDeviceId(op.Request.expectedHardwareId) &&
                op.Request.expectedHardwareId != "000000000000" && op.Request.expectedHardwareId != "FFFFFFFFFFFF" &&
                String.Equals(op.ConfirmedHardwareId, op.Request.expectedHardwareId, StringComparison.Ordinal);
        }

        internal string TryAcknowledge(OperationState op, IntPtr owner, string expectedText, string expectedCaption, Func<bool> verifyCurrentProof)
        {
            if (!HasRegistrationProof(op) || owner == IntPtr.Zero || verifyCurrentProof == null || !verifyCurrentProof())
                return "success_target_changed";
            if (DeadlineReached(op, utcNow())) return TimeoutCheck(op);
            if (op.NativeSuccessAcknowledgementAttempted) return "success_acknowledgement_requested";
            if (String.IsNullOrWhiteSpace(expectedText) || String.IsNullOrWhiteSpace(expectedCaption) ||
                expectedText.StartsWith("@+", StringComparison.Ordinal) || expectedCaption.StartsWith("@+", StringComparison.Ordinal))
                return "success_window_unverified";
            uint process = windows.CurrentProcessId, thread = windows.CurrentThreadId;
            NativeSuccessWindow ownerIdentity = windows.ReadIdentity(owner);
            if (!SameOwner(ownerIdentity, owner, process, thread)) return "success_window_unverified";
            IList<IntPtr> handles = windows.EnumerateThreadWindows(thread);
            if (handles == null) return "native_completion_unavailable";
            var owned = new List<NativeSuccessWindow>();
            foreach (IntPtr handle in handles)
            {
                NativeSuccessWindow identity = windows.ReadIdentity(handle);
                if (identity != null && identity.Exists && identity.Visible && identity.Owner == owner)
                    owned.Add(windows.ReadMessage(handle));
            }
            if (owned.Count == 0) return "success_window_not_found";
            if (owned.Count != 1) return "success_window_ambiguous";
            NativeSuccessWindow candidate = owned[0];
            if (!Matches(candidate, owner, process, thread, expectedText, expectedCaption)) return "success_window_unverified";

            // The live node/project/ID/KEY proof and the OS facts are checked
            // again on this same UI thread immediately before one exact send.
            if (!HasRegistrationProof(op) || !verifyCurrentProof()) return "success_target_changed";
            if (!SameOwner(windows.ReadIdentity(owner), owner, process, thread)) return "success_window_unverified";
            NativeSuccessWindow current = windows.ReadMessage(candidate.Handle);
            if (!Matches(current, owner, process, thread, expectedText, expectedCaption) ||
                current.Handle != candidate.Handle || current.OkButton != candidate.OkButton)
                return "success_window_unverified";
            lock (op.Sync)
            {
                if (DeadlineReached(op, utcNow())) return TimeoutCheck(op);
                if (op.NativeSuccessAcknowledgementAttempted) return "success_acknowledgement_requested";
                // Set before synchronous dispatch: a nested UI callback must
                // never send twice or later act on a recycled HWND.
                op.NativeSuccessAcknowledgementAttempted = true;
            }
            return windows.SendOk(current.Handle, current.OkButton) ?
                "success_acknowledgement_requested" : "success_acknowledgement_failed";
        }

        internal static bool BeginCompletionPass(OperationState op, DateTime now)
        {
            lock (op.Sync)
            {
                if (op.Status != "registered") return false;
                if (!op.NativeCompletionStartedUtc.HasValue) op.NativeCompletionStartedUtc = now;
                if (DeadlineReached(op, now))
                {
                    op.NativeCompletionState = "blocked";
                    op.NativeCompletionCheck = TimeoutCheck(op);
                    return false;
                }
                if (op.NativeSuccessAcknowledgementAttempted)
                {
                    if (op.NativeCompletionCheck == "success_acknowledgement_failed") op.NativeCompletionState = "blocked";
                    else
                    {
                        op.NativeCompletionState = "acknowledged";
                        op.NativeCompletionCheck = "success_acknowledgement_requested";
                    }
                    return false;
                }
                op.NativeCompletionState = "waiting";
                op.NativeCompletionCheck = op.NativeSuccessDialogSuppressed ? "awaiting_native_completion" : "awaiting_success_window";
                return true;
            }
        }

        private static bool DeadlineReached(OperationState op, DateTime now)
        {
            return op.NativeCompletionCheck == "success_acknowledgement_timeout" || op.NativeCompletionCheck == "native_completion_timeout" ||
                (op.NativeCompletionStartedUtc.HasValue && now >= op.NativeCompletionStartedUtc.Value.AddSeconds(10));
        }

        private static string TimeoutCheck(OperationState op)
        {
            return op.NativeSuccessDialogSuppressed ? "native_completion_timeout" : "success_acknowledgement_timeout";
        }

        private static bool SameOwner(NativeSuccessWindow value, IntPtr owner, uint process, uint thread)
        {
            return value != null && value.Exists && value.Visible && value.Handle == owner &&
                value.ProcessId == process && value.ThreadId == thread;
        }

        internal static bool Matches(NativeSuccessWindow value, IntPtr owner, uint process, uint thread, string text, string caption)
        {
            if (value == null || !value.Exists || !value.Visible || !value.Enabled || value.Handle == IntPtr.Zero ||
                value.Handle == owner || value.Owner != owner || value.ProcessId != process || value.ThreadId != thread ||
                value.ClassName != "#32770" || !String.Equals(value.Text, caption, StringComparison.Ordinal) ||
                value.OkButton == IntPtr.Zero || value.Children == null) return false;
            NativeSuccessWindow[] children = value.Children.ToArray();
            if (children.Any(c => c == null || !c.Exists || c.Parent != value.Handle || c.ProcessId != process || c.ThreadId != thread ||
                !(String.Equals(c.ClassName, "Button", StringComparison.OrdinalIgnoreCase) || String.Equals(c.ClassName, "Static", StringComparison.OrdinalIgnoreCase)))) return false;
            // Win32 templates may retain hidden buttons/labels. Only visible
            // controls define the offered result; every child still passed
            // the same process/thread/parent/class checks above.
            NativeSuccessWindow[] buttons = children.Where(c => c.Visible && String.Equals(c.ClassName, "Button", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (buttons.Length != 1 || buttons[0].Handle != value.OkButton || buttons[0].ControlId != 1 ||
                !buttons[0].Visible || !buttons[0].Enabled || (buttons[0].ButtonKind != 0 && buttons[0].ButtonKind != 1)) return false;
            NativeSuccessWindow[] labels = children.Where(c => c.Visible && String.Equals(c.ClassName, "Static", StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrEmpty(c.Text)).ToArray();
            return labels.Length == 1 && labels[0].Visible && String.Equals(labels[0].Text, text, StringComparison.Ordinal);
        }

        internal static void RecordCheck(OperationState op, string check, DateTime now)
        {
            lock (op.Sync)
            {
                if (!op.NativeCompletionStartedUtc.HasValue) op.NativeCompletionStartedUtc = now;
                // Keep the actual gate/send result when the overall deadline
                // expires. A terminal timeout must not erase its diagnosis.
                if (!String.IsNullOrEmpty(check) && check != "awaiting_success_window" &&
                    check != "awaiting_native_completion" && check != "success_acknowledgement_timeout" &&
                    check != "native_completion_timeout") op.NativeCompletionLastCheck = check;
                string state = check == "success_acknowledgement_requested" ? "acknowledged" :
                    (check == "success_window_not_found" || check == "awaiting_success_window" || check == "awaiting_native_completion") ? "waiting" : "blocked";
                if (DeadlineReached(op, now))
                {
                    state = "blocked";
                    check = TimeoutCheck(op);
                }
                op.NativeCompletionState = state;
                op.NativeCompletionCheck = check;
            }
        }
    }
}
