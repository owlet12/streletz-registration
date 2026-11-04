// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Streletz.MasterBridge;

internal static class NativeSuccessAcknowledgementSelfTest
{
    private static readonly IntPtr Owner = new IntPtr(10), Popup = new IntPtr(20), Button = new IntPtr(21);
    private const string Body = "ДУ успешно добавлено в систему", Caption = "Результат";
    private static int checks;

    private static int Main()
    {
        try
        {
            var op = Operation(); var api = new FakeWindows(); var gate = new NativeSuccessAcknowledgement(api);
            int proofs = 0;
            string result = gate.TryAcknowledge(op, Owner, Body, Caption, () => { proofs++; return true; });
            Check(result == "success_acknowledgement_requested" && api.Sends == 1 && proofs == 2 && api.MessageReads == 2,
                "Success requires two current proofs and two OS snapshots before one exact send.");
            Check(api.SentPopup == Popup && api.SentButton == Button, "Acknowledgement targets only the validated message and its exact IDOK button.");
            Check(op.Status == "registered" && op.RecordConfirmed && !op.NativeRunFinished && !op.NativeDialogClosed,
                "Acknowledgement does not invent native completion or change the proved registration.");

            Action<OperationState>[] removeProof = {
                x => x.NativeCommandStarted = false, x => x.ConfirmationSent = false,
                x => x.NativeSuccessObserved = false, x => x.RecordConfirmed = false,
                x => x.NativeRunFinished = true, x => x.Status = "registering", x => x.Status = "unknown",
                x => x.Status = "cancelled", x => x.Status = "failed", x => x.ConfirmedHardwareId = "112233AABBDD",
                x => { x.Request.expectedHardwareId = "AABBCC"; x.ConfirmedHardwareId = "AABBCC"; },
                x => { x.Request.expectedHardwareId = "000000000000"; x.ConfirmedHardwareId = "000000000000"; },
                x => { x.Request.expectedHardwareId = "FFFFFFFFFFFF"; x.ConfirmedHardwareId = "FFFFFFFFFFFF"; }
            };
            foreach (Action<OperationState> remove in removeProof)
            {
                op = Operation(); remove(op); api = new FakeWindows();
                result = new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true);
                Check(result == "success_target_changed" && api.Reads == 0 && api.Sends == 0, "Incomplete operation proof cannot inspect or dismiss a native message.");
            }
            op = Operation(); api = new FakeWindows();
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => false) == "success_target_changed" && api.Reads == 0,
                "Changed target, GUID, ID, KEY or project route blocks before window inspection.");
            op = Operation(); api = new FakeWindows(); proofs = 0;
            result = new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => ++proofs == 1);
            Check(result == "success_target_changed" && proofs == 2 && api.Sends == 0,
                "Changing the live proof between discovery and dispatch prevents acknowledgement.");

            Action<FakeWindows>[] malformed = {
                x => x.OwnerWindow.ProcessId++, x => x.OwnerWindow.ThreadId++, x => x.OwnerWindow.Visible = false,
                x => x.Message.ProcessId++, x => x.Message.ThreadId++, x => x.Message.ClassName = "CustomDialog",
                x => x.Message.Owner = new IntPtr(99), x => x.Message.Text = "Ошибка", x => x.Message.Enabled = false,
                x => x.Message.Children[0].Text = "Подтвердить изменение?", x => x.Message.Children[0].Visible = false,
                x => x.Message.Children[1].Enabled = false, x => x.Message.Children[1].Visible = false,
                x => x.Message.Children[1].ControlId = 6, x => x.Message.Children[1].ButtonKind = 3,
                x => x.Message.Children[1].Parent = new IntPtr(99), x => x.Message.Children[1].ProcessId++,
                x => x.Message.Children[1].ThreadId++, x => x.Message.OkButton = IntPtr.Zero,
                x => x.Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, ProcessId = 5, ThreadId = 7, Exists = true, Visible = true, Enabled = false, ClassName = "Button", ControlId = 2 }),
                x => x.Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, ProcessId = 5, ThreadId = 7, Exists = true, Visible = true, Enabled = true, ClassName = "Edit", Text = "extra" }),
                x => x.Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, ProcessId = 5, ThreadId = 7, Exists = true, Visible = true, ClassName = "Static", Text = "extra" })
            };
            foreach (Action<FakeWindows> alter in malformed)
            {
                op = Operation(); api = new FakeWindows(); alter(api);
                result = new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true);
                Check(result != "success_acknowledgement_requested" && api.Sends == 0,
                    "Foreign owner/thread/process, changed text, unknown control or non-OK button is not acknowledged.");
            }

            op = Operation(); api = new FakeWindows();
            api.Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, ProcessId = 5, ThreadId = 7,
                Exists = true, Visible = false, Enabled = false, ClassName = "Button", ControlId = 2 });
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_acknowledgement_requested" && api.Sends == 1,
                "A hidden template button does not change the single visible enabled IDOK contract.");
            op = Operation(); api = new FakeWindows();
            api.Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, ProcessId = 5, ThreadId = 7,
                Exists = true, Visible = false, Enabled = false, ClassName = "Static", Text = "hidden template label" });
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_acknowledgement_requested" && api.Sends == 1,
                "A hidden static label does not replace or duplicate the exact visible success body.");

            op = Operation(); api = new FakeWindows(); api.ExtraOwnedWindow = true;
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_window_ambiguous" && api.Sends == 0,
                "Two visible owned messages are ambiguous even when one has success text.");
            op = Operation(); api = new FakeWindows(); api.Enumerated = new List<IntPtr>();
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_window_not_found" && api.Sends == 0,
                "Printing/no-popup branch never creates or closes an unrelated message.");
            op = Operation(); api = new FakeWindows(); api.Enumerated = null;
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "native_completion_unavailable" && api.Sends == 0,
                "Unavailable thread-window enumeration fails closed.");
            op = Operation(); api = new FakeWindows(); api.BeforeMessageRead = count => { if (count == 2) api.Message.Owner = new IntPtr(99); };
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_window_unverified" && api.Sends == 0,
                "Ownership is reread immediately before send; a changed/reused popup is rejected.");
            op = Operation(); api = new FakeWindows(); api.BeforeMessageRead = count => { if (count == 2) { api.Message.OkButton = new IntPtr(77); api.Message.Children[1].Handle = new IntPtr(77); } };
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_window_unverified" && api.Sends == 0,
                "Replacing the valid-looking IDOK button between reads prevents dispatch.");

            op = Operation(); api = new FakeWindows(); gate = new NativeSuccessAcknowledgement(api);
            api.DuringSend = () => Check(gate.TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_acknowledgement_requested" && op.NativeSuccessAcknowledgementAttempted,
                "A nested UI callback sees the attempt flag already set and cannot send twice.");
            result = gate.TryAcknowledge(op, Owner, Body, Caption, () => true);
            Check(result == "success_acknowledgement_requested" && api.Sends == 1, "Synchronous message-loop reentrancy sends exactly once.");
            api.DuringSend = null; api.Message.Handle = new IntPtr(999);
            gate.TryAcknowledge(op, Owner, Body, Caption, () => true);
            Check(api.Sends == 1, "A later tick cannot send to a recycled or replacement window after an attempt.");
            op = Operation(); api = new FakeWindows(); api.SendSucceeds = false; gate = new NativeSuccessAcknowledgement(api);
            result = gate.TryAcknowledge(op, Owner, Body, Caption, () => true);
            NativeSuccessAcknowledgement.RecordCheck(op, result, DateTime.UtcNow);
            gate.TryAcknowledge(op, Owner, Body, Caption, () => true);
            Check(result == "success_acknowledgement_failed" && api.Sends == 1 && op.NativeCompletionState == "blocked" && op.Status == "registered",
                "A failed UI send preserves registration, reports blocked and is never replayed.");

            op = Operation(); op.CancellationRequested = true; api = new FakeWindows();
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_acknowledgement_requested" &&
                op.CancellationRequested && op.Status == "registered", "Late proved success after a cancellation request is acknowledged without reporting a cancellation.");
            op = Operation(); api = new FakeWindows(); api.Message.Text = "Result"; api.Message.Children[0].Text = "Device added successfully";
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, "Device added successfully", "Result", () => true) == "success_acknowledgement_requested",
                "Current localized success strings work without hardcoded Russian matching.");
            op = Operation(); api = new FakeWindows();
            Check(new NativeSuccessAcknowledgement(api).TryAcknowledge(op, Owner, "@+IntDU.Succesfull", "@+iresult", () => true) == "success_window_unverified" && api.Sends == 0,
                "Unresolved localization keys are not used as success-message evidence.");

            op = Operation(); op.CreatedUtc = DateTime.UtcNow.AddHours(-1); DateTime first = DateTime.UtcNow;
            NativeSuccessAcknowledgement.RecordCheck(op, "success_window_not_found", first);
            Check(op.NativeCompletionState == "waiting", "Completion timeout begins with proved-success cleanup, not the original registration request.");
            NativeSuccessAcknowledgement.RecordCheck(op, "success_acknowledgement_requested", first.AddSeconds(9));
            Check(op.NativeCompletionState == "acknowledged" && !op.NativeRunFinished && !op.NativeDialogClosed,
                "A sent acknowledgement still waits for real vendor completion.");
            NativeSuccessAcknowledgement.RecordCheck(op, "success_acknowledgement_requested", first.AddSeconds(10));
            Check(op.NativeCompletionState == "blocked" && op.NativeCompletionCheck == "success_acknowledgement_timeout" && op.Status == "registered",
                "Missing native completion becomes an explicit bounded block, never an endless closing claim or false failure.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(11)),
                "The production completion pass stops retries permanently after its deadline.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(1)),
                "An observed completion timeout remains blocked even if the wall clock later moves backward.");
            api = new FakeWindows();
            Check(new NativeSuccessAcknowledgement(api, () => first.AddSeconds(11)).TryAcknowledge(op, Owner, Body, Caption, () => true) == "success_acknowledgement_timeout" && api.Reads == 0 && api.Sends == 0,
                "A newly appearing valid-looking success popup after timeout is never acknowledged by the old operation.");

            op = Operation(); op.NativeDialogClosed = true; op.NativeRunFinished = false; op.CreatedUtc = first.AddHours(-1);
            Check(NativeSuccessAcknowledgement.BeginCompletionPass(op, first) && op.NativeCompletionState == "waiting",
                "A closed/missing form with its native command still running enters bounded completion bookkeeping.");
            NativeSuccessAcknowledgement.RecordCheck(op, "success_window_not_found", first);
            Check(NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(9)),
                "Transient no-popup completion may be rechecked during the initial ten-second window.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(10)) && op.NativeCompletionState == "blocked" && !op.NativeRunFinished,
                "A hidden/disposed form cannot leave an unfinished native command in an endless closing state.");

            op = Operation(); api = new FakeWindows(); DateTime clock = first;
            Check(NativeSuccessAcknowledgement.BeginCompletionPass(op, clock), "A newly proved operation opens its own completion interval.");
            api.BeforeMessageRead = count => { if (count == 2) clock = first.AddSeconds(10); };
            result = new NativeSuccessAcknowledgement(api, () => clock).TryAcknowledge(op, Owner, Body, Caption, () => true);
            NativeSuccessAcknowledgement.RecordCheck(op, result, clock);
            Check(result == "success_acknowledgement_timeout" && api.Sends == 0 && !op.NativeSuccessAcknowledgementAttempted,
                "The deadline is checked again after the final OS read immediately before dispatch.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, clock.AddSeconds(1)) && op.Status == "registered" && !op.NativeRunFinished,
                "The production lifecycle preserves proved success and keeps native busy after a blocked late send.");

            op = Operation(); api = new FakeWindows();
            NativeSuccessAcknowledgement.BeginCompletionPass(op, first);
            result = new NativeSuccessAcknowledgement(api, () => first).TryAcknowledge(op, Owner, Body, Caption, () => true);
            NativeSuccessAcknowledgement.RecordCheck(op, result, first);
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(1)) && op.NativeCompletionState == "acknowledged" && api.Sends == 1,
                "The lifecycle waits for natural native completion after one successful send without another window search.");

            op = Operation();
            NativeSuccessAcknowledgement.RecordCheck(op, "success_window_unverified", first);
            Check(op.NativeCompletionLastCheck == "success_window_unverified", "The exact failed window gate is retained separately from lifecycle state.");
            Check(NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(1)) &&
                op.NativeCompletionLastCheck == "success_window_unverified",
                "Starting another bounded inspection does not erase the preceding gate diagnosis.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(10)) &&
                op.NativeCompletionCheck == "success_acknowledgement_timeout" && op.NativeCompletionLastCheck == "success_window_unverified",
                "The terminal timeout preserves the window-verification cause instead of replacing all evidence with a generic timeout.");
            NativeSuccessAcknowledgement.RecordCheck(op, "success_acknowledgement_timeout", first.AddSeconds(11));
            Check(op.NativeCompletionLastCheck == "success_window_unverified", "Repeated timeout reports cannot erase the actual failed check.");

            op = Operation(); op.NativeSuccessDialogSuppressed = true;
            Check(NativeSuccessAcknowledgement.BeginCompletionPass(op, first) && op.NativeCompletionCheck == "awaiting_native_completion",
                "A constructor-verified no-result-dialog operation waits for actual native return, not a nonexistent OK button.");
            NativeSuccessAcknowledgement.RecordCheck(op, "awaiting_native_completion", first.AddSeconds(1));
            Check(op.NativeCompletionState == "waiting" && !op.NativeRunFinished && !op.NativeDialogClosed && op.Status == "registered",
                "Suppressing the informational result does not invent completion, alter successful registration or release native busy early.");
            Check(!NativeSuccessAcknowledgement.BeginCompletionPass(op, first.AddSeconds(10)) &&
                op.NativeCompletionCheck == "native_completion_timeout" && !op.NativeSuccessAcknowledgementAttempted,
                "A stalled native return is diagnosed distinctly without attempting window acknowledgement.");
            string json = new JavaScriptSerializer().Serialize(op.Snapshot());
            Check(json.Contains("nativeCompletionState") && json.Contains("nativeCompletionCheck") &&
                json.Contains("nativeCompletionLastCheck") && json.Contains("\"nativeSuccessDialogSuppressed\":true") &&
                !json.Contains(Body) && !json.Contains("Handle") && !json.Contains("deviceKey"),
                "Snapshot exposes only bounded completion state/check and no native text, HWND or KEY.");
            Console.WriteLine("PASS " + checks + " success acknowledgement assertions; synthetic window API only, no UI, IPC, phone request or hardware.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }

    private static OperationState Operation()
    {
        var op = new OperationState { Request = new StartRequest { operationId = "synthetic-operation", recordId = "11111111-2222-4333-8444-555555555555",
            expectedHardwareId = "112233AABBCC", panel = new PanelRequest() }, NativeCommandStarted = true,
            ConfirmationSent = true, NativeSuccessObserved = true, RecordConfirmed = true };
        op.MarkRegistered("112233AABBCC"); return op;
    }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }

    private sealed class FakeWindows : INativeSuccessWindowApi
    {
        internal NativeSuccessWindow OwnerWindow = new NativeSuccessWindow { Handle = Owner, Exists = true, Visible = true, ProcessId = 5, ThreadId = 7 };
        internal NativeSuccessWindow Message = new NativeSuccessWindow { Handle = Popup, Owner = Owner, Exists = true, Visible = true, Enabled = true,
            ProcessId = 5, ThreadId = 7, ClassName = "#32770", Text = Caption, OkButton = Button, Children = new List<NativeSuccessWindow> {
                new NativeSuccessWindow { Handle = new IntPtr(22), Parent = Popup, Exists = true, Visible = true, Enabled = true, ProcessId = 5, ThreadId = 7, ClassName = "Static", Text = Body },
                new NativeSuccessWindow { Handle = Button, Parent = Popup, Exists = true, Visible = true, Enabled = true, ProcessId = 5, ThreadId = 7, ClassName = "Button", ControlId = 1, ButtonKind = 1 }
            } };
        internal IList<IntPtr> Enumerated = new List<IntPtr> { Owner, Popup };
        internal int Sends, Reads, MessageReads;
        internal IntPtr SentPopup, SentButton;
        internal bool SendSucceeds = true, ExtraOwnedWindow;
        internal Action DuringSend;
        internal Action<int> BeforeMessageRead;
        public uint CurrentProcessId { get { Reads++; return 5; } }
        public uint CurrentThreadId { get { Reads++; return 7; } }
        public NativeSuccessWindow ReadIdentity(IntPtr handle)
        {
            Reads++;
            if (handle == Owner) return Clone(OwnerWindow);
            if (handle == Popup) return Clone(Message);
            if (handle == new IntPtr(30) && ExtraOwnedWindow) return new NativeSuccessWindow { Handle = handle, Exists = true, Visible = true, Owner = Owner, ProcessId = 5, ThreadId = 7 };
            return new NativeSuccessWindow { Handle = handle };
        }
        public IList<IntPtr> EnumerateThreadWindows(uint thread)
        {
            Reads++; if (Enumerated == null) return null;
            var values = Enumerated.ToList(); if (ExtraOwnedWindow) values.Add(new IntPtr(30)); return values;
        }
        public NativeSuccessWindow ReadMessage(IntPtr handle)
        {
            Reads++; MessageReads++; if (BeforeMessageRead != null) BeforeMessageRead(MessageReads);
            return handle == Popup ? Clone(Message) : ReadIdentity(handle);
        }
        public bool SendOk(IntPtr message, IntPtr button)
        {
            Sends++; SentPopup = message; SentButton = button; if (DuringSend != null) DuringSend(); return SendSucceeds;
        }
        private static NativeSuccessWindow Clone(NativeSuccessWindow value)
        {
            return new NativeSuccessWindow { Handle = value.Handle, Owner = value.Owner, Parent = value.Parent, OkButton = value.OkButton,
                ProcessId = value.ProcessId, ThreadId = value.ThreadId, Exists = value.Exists, Visible = value.Visible, Enabled = value.Enabled,
                ClassName = value.ClassName, Text = value.Text, ControlId = value.ControlId, ButtonKind = value.ButtonKind,
                Children = value.Children == null ? null : value.Children.Select(Clone).ToList() };
        }
    }
}
