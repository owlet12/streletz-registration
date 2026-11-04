// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Streletz.MasterBridge
{
    internal sealed class BridgeHost : IDisposable
    {
        internal const string Protocol = "streletz-master-plugin-v1";
        private readonly Form main;
        private readonly NativeContext native = new NativeContext();
        private readonly Dictionary<string, OperationState> operations = new Dictionary<string, OperationState>(StringComparer.Ordinal);
        private readonly HashSet<string> retiredOperationIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object sync = new object();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        private NativeDialogAutomation automation;
        private LocalPipeServer pipe;
        private OperationState active;
        private BridgeException startupError;
        private bool disposed;

        internal BridgeHost(Form mainForm) { main = mainForm; }

        internal void Start()
        {
            try { VersionGuard.Verify(AppDomain.CurrentDomain.BaseDirectory); }
            catch (BridgeException e) { startupError = e; }
            catch { startupError = new BridgeException("version_check_failed", "The Master build could not be verified."); }
            automation = new NativeDialogAutomation(() => active, native);
            timer.Tick += OnTick;
            timer.Start();
            pipe = new LocalPipeServer(HandleLine);
            pipe.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (disposed) return;
            OperationState op = active;
            try
            {
                if (op != null && op.Status != "registered" && !op.NativeRunFinished && !op.CancellationRequested &&
                    DateTime.UtcNow > op.CreatedUtc.AddSeconds(op.Request.timeoutSeconds))
                {
                    op.CancellationRequested = true;
                    lock (op.Sync) { op.ErrorCode = "registration_timeout"; op.ErrorMessage = "Registration timed out; a sent native confirmation may still have taken effect."; }
                    automation.Cancel(op);
                }
                automation.Tick();
            }
            catch (BridgeException error) { op?.Fail(error.Code, error.Message); }
            catch { op?.Fail("native_dialog_error", "The native dialog did not match the inspected workflow."); }
            finally { UpdateDialogState(op); }
        }

        private async Task<string> HandleLine(string line)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = NativeReplyJson.MaximumRequestCharacters, RecursionLimit = 16 };
            object reply;
            try
            {
                var envelope = json.Deserialize<Dictionary<string, object>>(line);
                object value;
                if (envelope == null || !envelope.TryGetValue("type", out value) || !(value is string))
                    throw new BridgeException("request_invalid", "A request type is required.");
                string type = (string)value;
                if (type == "hello") reply = Ok(await OnUi(Hello));
                else if (type == "list_panels") reply = Ok(await OnUi(() => {
                    EnsureReady();
                    NativeSnapshot snapshot = native.Snapshot();
                    return new { projectFingerprint = snapshot.Fingerprint, nativeProjectFingerprint = snapshot.Fingerprint,
                        panels = snapshot.Panels.Select(p => p.PublicView()).ToArray() };
                }));
                else if (type == "start")
                {
                    StartRequest request = json.Deserialize<StartRequest>(line);
                    reply = Ok(await OnUi(() => StartOperation(request)));
                }
                else if (type == "status" || type == "cancel")
                {
                    string operationId = envelope.TryGetValue("operationId", out value) ? value as string : null;
                    OperationState op;
                    lock (sync) if (operationId == null || !operations.TryGetValue(operationId, out op))
                    {
                        if (operationId != null && retiredOperationIds.Contains(operationId))
                            throw new BridgeException("operation_result_expired", "The retained result has expired; this operation ID cannot be started again.");
                        throw new BridgeException("operation_not_found", "The operation is not known to this Master process.");
                    }
                    if (type == "cancel" && op.Status != "registered")
                        await OnUi(() => { RequestCancel(op); return true; });
                    reply = Ok(op.Snapshot());
                }
                else throw new BridgeException("request_not_supported", "The request type is not supported.");
            }
            catch (BridgeException error) { reply = Error(error.Code, error.Message); }
            catch (ArgumentException) { reply = Error("request_invalid", "The JSON request is invalid."); }
            catch (InvalidOperationException) { reply = Error("request_invalid", "The JSON request or native state is invalid."); }
            catch { reply = Error("native_operation_failed", "The native operation could not be completed; no internal data was returned."); }
            return NativeReplyJson.Serialize(reply);
        }

        private object Hello()
        {
            string fingerprint = null;
            string code = startupError?.Code;
            if (code == null)
            {
                try { fingerprint = native.Snapshot().Fingerprint; }
                catch (BridgeException error) { code = error.Code; }
                catch { code = "native_context_unavailable"; }
            }
            return new {
                ready = code == null, version = "12.4", pluginVersion = "1.0.9.0", projectFingerprint = fingerprint,
                nativeProjectFingerprint = fingerprint, processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                capabilities = new { listPanels = true, start = true, status = true, cancel = true,
                    autoConfirmSuccessResult = true, suppressNativeSuccessDialog = true },
                unavailableReason = code,
                diagnostics = new {
                    maximumResponseJsonCharacters = NativeReplyJson.MaximumResponseJsonCharacters,
                    nativeCompletionState = active == null ? "not_started" : active.NativeCompletionState,
                    nativeCompletionCheck = active == null ? "" : active.NativeCompletionCheck,
                    nativeCompletionLastCheck = active == null ? "" : active.NativeCompletionLastCheck,
                    nativeSuccessDialogSuppressed = active != null && active.NativeSuccessDialogSuppressed,
                    buildFailureComponent = startupError == null ? null : VersionGuard.Expected.Keys.FirstOrDefault(
                        name => startupError.Message.EndsWith(": " + name, StringComparison.Ordinal))
                },
                limitations = new [] { "Native Master project must be open and authenticated.", "Configured direct MK-IP and verified paired panel-terminal MK-IP routes are supported.",
                    "Registration does not automatically save or program the project.", "Outcome after a sent confirmation can be unknown if Master closes or communication fails." }
            };
        }

        private object StartOperation(StartRequest request)
        {
            EnsureReady();
            if (request == null) throw new BridgeException("request_invalid", "A start request is required.");
            OperationState existing;
            lock (sync)
            {
                if (request.operationId != null && retiredOperationIds.Contains(request.operationId))
                    throw new BridgeException("operation_id_retired", "This operation ID was already used; the retained result has expired.");
                if (request.operationId != null && operations.TryGetValue(request.operationId, out existing))
                {
                    if (!SameRequest(existing.Request, request))
                        throw new BridgeException("operation_id_conflict", "The operation ID already belongs to a different request.");
                    return existing.Snapshot();
                }
            }
            if (active != null && (!active.NativeRunFinished || (active.Dialog != null && !active.Dialog.IsDisposed && active.Dialog.Visible)))
            {
                if (active.Status == "registered")
                {
                    if (active.NativeCompletionState == "blocked")
                        throw new BridgeException("native_result_acknowledgement_blocked", "The previous device is registered, but its success window could not be acknowledged safely. No new registration was started.");
                    throw new BridgeException("native_result_closing", "The previous device is registered; its native result window is finishing automatically. No new registration was started.");
                }
                throw new BridgeException("native_registration_busy", "Another native registration operation is still open.");
            }
            if (Application.OpenForms.Cast<Form>().Any(f => !f.IsDisposed &&
                (f.GetType().FullName == "StreletcProGUI.Dialogs.InitDu" || (f.Visible && f.Modal))))
                throw new BridgeException("native_dialog_busy", "Close the current native dialog before starting registration.");
            OperationState op = native.Prepare(request);
            lock (sync)
            {
                if (operations.Count >= 128)
                {
                    var old = operations.Values.Where(x => x.NativeRunFinished).OrderBy(x => x.CreatedUtc).FirstOrDefault();
                    if (old != null && retiredOperationIds.Count < 100000)
                    {
                        retiredOperationIds.Add(old.Request.operationId);
                        operations.Remove(old.Request.operationId);
                    }
                    else throw new BridgeException("operation_history_full", "Too many active operations are retained.");
                }
                operations.Add(op.Request.operationId, op);
                active = op;
            }
            // Return queued immediately; the real native modal workflow runs on Master's UI thread.
            main.BeginInvoke(new Action(() => Execute(op)));
            return op.Snapshot();
        }

        private static bool SameRequest(StartRequest a, StartRequest b)
        {
            return String.Equals(a.recordId, b.recordId, StringComparison.OrdinalIgnoreCase) &&
                a.expectedHardwareId == Hex.Normalize(b.expectedHardwareId) &&
                a.deviceKey == Hex.Normalize(b.deviceKey) && a.projectFingerprint == b.projectFingerprint &&
                a.nativeProjectFingerprint == b.nativeProjectFingerprint && a.panel != null && b.panel != null &&
                a.panel.host == b.panel.host && a.panel.port == b.panel.port &&
                Hex.Normalize(a.panel.moduleId) == Hex.Normalize(b.panel.moduleId) &&
                a.panel.nativePanelId == b.panel.nativePanelId && a.panel.panelId == b.panel.panelId &&
                a.panel.routeKind == b.panel.routeKind && a.panel.viaPanelTerminal == b.panel.viaPanelTerminal &&
                a.panel.terminalPanelId == b.panel.terminalPanelId && a.panel.terminalSegmentId == b.panel.terminalSegmentId &&
                a.panel.adapterNodeId == b.panel.adapterNodeId && a.panel.adapterSegmentId == b.panel.adapterSegmentId;
        }

        private void Execute(OperationState op)
        {
            try
            {
                if (op.CancellationRequested) op.SetStatus("cancelled", "cancelled_before_native_start");
                else
                {
                    if (!native.VerifyTargetStillMatches(op))
                        throw new BridgeException("native_project_changed", "The native target changed before registration.");
                    automation.PrepareBeforeNativeRun(op);
                    native.RunInitDevice(op);
                }
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                var bridge = error as BridgeException;
                op.Fail(bridge?.Code ?? "native_command_failed", bridge?.Message ?? "The native command did not complete successfully.");
            }
            finally
            {
                try { automation.RestoreIfUnconfirmed(op); }
                catch { op.Fail("native_key_restore_unconfirmed", "The original project KEY could not be restored automatically."); }
                finally
                {
                    op.NativeRunFinished = true;
                    UpdateDialogState(op);
                }
                if (op.Status != "registered" && !op.IsTerminal)
                {
                    if (op.CancellationRequested && !op.ConfirmationSent) op.SetStatus("cancelled", "native_dialog_cancelled");
                    else op.Fail("native_registration_unconfirmed", "Native registration was not confirmed.");
                }
            }
        }

        private void RequestCancel(OperationState op)
        {
            if (op.Status == "registered" || (op.NativeRunFinished &&
                (op.Dialog == null || op.Dialog.IsDisposed || !op.Dialog.Visible))) return;
            op.CancellationRequested = true;
            if (op.Dialog == null && !op.ConfirmationSent)
                op.SetStatus("cancelled", "cancelled_before_native_confirmation");
            automation.Cancel(op);
            UpdateDialogState(op);
        }

        private static void UpdateDialogState(OperationState op)
        {
            if (op == null) return;
            // Called only on Master UI thread; pipe readers receive cached evidence.
            lock (op.Sync)
            {
                Form dialog = op.Dialog;
                op.NativeDialogOpen = dialog != null && !dialog.IsDisposed && dialog.Visible;
                op.NativeDialogClosed = dialog != null ? (dialog.IsDisposed || !dialog.Visible) : op.NativeRunFinished;
                if (op.Status == "registered" && op.NativeRunFinished && op.NativeDialogClosed && !op.NativeDialogOpen)
                {
                    op.NativeCompletionState = "completed";
                    op.NativeCompletionCheck = "native_completion_finished";
                }
            }
        }

        private void EnsureReady()
        {
            if (disposed) throw new BridgeException("master_closing", "Master is closing.");
            if (startupError != null) throw startupError;
            native.EnsureAuthenticated();
        }

        private async Task<object> OnUi(Func<object> action)
        {
            if (disposed || main.IsDisposed) throw new BridgeException("master_closing", "Master is closing.");
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            int dispatchState = 0; // 0 queued, 1 executing, 2 withdrawn before execution, 3 completed
            main.BeginInvoke(new Action(() => {
                if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0) return;
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Interlocked.Exchange(ref dispatchState, 3); }
            }));
            if (await Task.WhenAny(completion.Task, Task.Delay(15000)).ConfigureAwait(false) != completion.Task)
            {
                if (Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0)
                    throw new BridgeException("native_ui_busy", "Master did not start the queued request in time; it was withdrawn.");
                if (!completion.Task.IsCompleted)
                    throw new BridgeException("native_outcome_unknown", "Master began processing the request but did not finish in time. Check operation status; do not create a new operation.");
            }
            return await completion.Task.ConfigureAwait(false);
        }

        private static object Ok(object data) { return new { ok = true, protocol = Protocol, data }; }
        private static object Error(string code, string message) { return new { ok = false, protocol = Protocol, error = new { code, message } }; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
            automation?.Dispose();
            pipe?.Dispose();
            if (active != null && active.Status != "registered" && !active.NativeRunFinished)
                active.Fail("master_closed", "Master closed before registration could be confirmed.");
        }
    }
}
