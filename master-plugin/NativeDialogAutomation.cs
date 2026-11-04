// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Streletz.MasterBridge
{
    // VersionGuard verifies the exact 12.4 assemblies before this adapter is used.
    // The only registration trigger below is the vendor's existing SelectRow handler,
    // also called by its QR/ID Enter handler. No captured packet is replayed.
    internal sealed class NativeDialogAutomation : IDisposable
    {
        private const string DialogType = "StreletcProGUI.Dialogs.InitDu";
        private const string DiscoveryRowType = "StreletcPro.RRI.TableParams.RowParamsInit";
        private const int GridField = 0x04000049;
        private const int TargetField = 0x04000035;
        private const int CancellationField = 0x0400003D;
        private const int ShowSuccessResultField = 0x04000040;
        private const int SelectAndContinueMethod = 0x0600005F;
        private readonly Func<OperationState> getActive;
        private readonly NativeContext native;
        private readonly HashSet<Form> observed = new HashSet<Form>();
        private readonly HashSet<string> keyPrepared = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> originalKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly NativeSuccessAcknowledgement successAcknowledgement = new NativeSuccessAcknowledgement(new NativeSuccessWindowApi());
        private bool insideTick;
        private bool insideCompletionTick;
        private bool insideTimeoutTick;
        private readonly NativeTimeoutAcknowledgement timeoutAcknowledgement = new NativeTimeoutAcknowledgement(new NativeSuccessWindowApi());
        private bool disposed;

        internal NativeDialogAutomation(Func<OperationState> getActive, NativeContext native)
        {
            this.getActive = getActive ?? throw new ArgumentNullException("getActive");
            this.native = native ?? throw new ArgumentNullException("native");
        }

        internal void PrepareBeforeNativeRun(OperationState op)
        {
            if (op == null) throw new ArgumentNullException("op");
            PrepareKey(op);
        }

        internal void RestoreIfUnconfirmed(OperationState op)
        {
            if (op != null) RestorePreparedKey(op);
        }

        internal void Tick()
        {
            if (disposed) return;
            OperationState op = getActive();
            if (op == null || op.Status == "failed" || op.Status == "cancelled") return;
            // SelectAndContinue is async but may complete synchronously. Its
            // nested MessageBox then pumps this timer before the outer
            // select.Invoke returns. Only result proof/ack or timeout decline may reenter;
            // discovery, key preparation and confirmation remain nonreentrant.
            if (insideTick || op.Status == "registered") { CompleteSuccessfulOperation(op); HandleReadinessTimeout(op); return; }
            if (op.Status == "unknown" && op.NativeRunFinished && (op.Dialog == null || op.Dialog.IsDisposed)) return;
            insideTick = true;
            try
            {
                if (op.Dialog == null) AttachDialog(op);
                Form dialog = op.Dialog;
                if (dialog == null)
                {
                    if (op.NativeRunFinished)
                    {
                        if (op.CancellationRequested && !op.ConfirmationSent)
                            op.SetStatus("cancelled", "native_command_ended_before_confirmation");
                        else op.Fail("native_dialog_not_opened", "Штатная команда не открыла окно регистрации. Проверьте сообщение Стрелец-Мастера на компьютере.");
                    }
                    return;
                }

                if (ObserveRegistered(op)) { CompleteSuccessfulOperation(op); return; }
                bool timeoutWasHandled = op.NativeTimeoutAcknowledgementAttempted;
                HandleReadinessTimeout(op);
                if (op.NativeTimeoutAcknowledgementAttempted)
                {
                    // After NO returns, allow the vendor continuation to finish.
                    // Only close our own remaining InitDu on a later, nonnested tick.
                    if (timeoutWasHandled && op.NativeTimeoutDeclined && VerifyTimeoutOwner(op, dialog) &&
                        !timeoutAcknowledgement.HasVisibleOwnedWindow(dialog.Handle)) CloseOwnedDialog(op);
                    return;
                }
                if (op.CancellationRequested) { Cancel(op); return; }
                if (dialog.IsDisposed || !dialog.Visible)
                {
                    if (op.NativeRunFinished && op.ErrorCode == null) op.Fail("native_dialog_closed", "Окно регистрации закрыто без подтверждённого результата.");
                    return;
                }
                if (op.ConfirmationSent) return;
                if (!native.VerifyTargetStillMatches(op))
                    throw new BridgeException("native_project_changed", "Проект или выбранная панель изменились. Регистрация остановлена.");

                // A visible form alone is insufficient: wait until the native poller exists.
                CancellationTokenSource scan = native.GetField(dialog, CancellationField) as CancellationTokenSource;
                if (scan == null || scan.IsCancellationRequested) return;
                op.SetStatus("waiting_sensor", "native_discovery_running");

                object grid = native.GetField(dialog, GridField);
                IEnumerable rows = native.GetProperty(grid, "Rows") as IEnumerable;
                if (rows == null) throw new BridgeException("native_grid_shape", "Не удалось прочитать штатный список обнаруженных датчиков.");
                object selected = null;
                foreach (object row in rows)
                {
                    object tag = native.GetProperty(row, "Tag");
                    if (tag == null || tag.GetType().FullName != DiscoveryRowType) continue;
                    string id = Hex.Bytes(native.GetProperty(tag, "ID") as byte[]);
                    if (!String.Equals(id, op.Request.expectedHardwareId, StringComparison.Ordinal)) continue;
                    if (selected != null) throw new BridgeException("native_duplicate_discovery", "Один ID обнаружен в нескольких строках. Автоматический выбор остановлен.");
                    selected = row;
                }
                if (selected == null) return;
                op.SetStatus("detected", "expected_sensor_detected");
                if (!AsBoolean(native.GetProperty(selected, "Selectable"))) return;

                if (!native.VerifyDialogAdapter(op, dialog))
                    throw new BridgeException("native_adapter_mismatch", "Не удалось подтвердить маршрут штатного окна регистрации. Причина проверки сохранена в диагностике операции.");
                PrepareKey(op);
                object currentTag = native.GetProperty(selected, "Tag");
                if (!String.Equals(Hex.Bytes(native.GetProperty(currentTag, "ID") as byte[]), op.Request.expectedHardwareId, StringComparison.Ordinal)) return;
                int index = Convert.ToInt32(native.GetProperty(selected, "Index"), CultureInfo.InvariantCulture);
                MethodInfo select = dialog.GetType().Module.ResolveMethod(SelectAndContinueMethod) as MethodInfo;
                if (select == null || select.DeclaringType != dialog.GetType() || select.IsStatic ||
                    select.ReturnType != typeof(void) || select.GetParameters().Length != 1 || select.GetParameters()[0].ParameterType != typeof(int))
                    throw new BridgeException("native_handler_shape", "Версия обработчика регистрации не соответствует проверенной версии 12.4.");

                // Record intent before invoking the native async operation. After this point,
                // a timeout/closed dialog is UNKNOWN unless actual success was observed.
                op.ConfirmationSent = true;
                op.SetStatus("registering", "native_confirmation_requested");
                select.Invoke(dialog, new object[] { index });
            }
            catch (Exception error)
            {
                Exception actual = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                BridgeException known = actual as BridgeException;
                op.Fail(known == null ? "native_automation_error" : known.Code,
                    known == null ? "Штатная регистрация завершилась ошибкой. Проверьте сообщение Стрелец-Мастера." : known.Message);
                if (!op.ConfirmationSent) { RestorePreparedKey(op); CloseOwnedDialog(op); }
            }
            finally { insideTick = false; }
        }

        private void HandleReadinessTimeout(OperationState op)
        {
            if (insideTimeoutTick || !NativeTimeoutAcknowledgement.Eligible(op) || op.NativeTimeoutAcknowledgementAttempted) return;
            insideTimeoutTick = true;
            try
            {
                Form dialog = op.Dialog;
                if (!VerifyTimeoutOwner(op, dialog)) return;
                Type localization = NativeContext.LoadedType("Localization.LocString", "Localization");
                MethodInfo translate = localization.GetMethod("KeyToString", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (translate == null || translate.ReturnType != typeof(string)) return;
                string text = (string)translate.Invoke(null, new object[] { "@+IntDU.TimeError" });
                string caption = (string)translate.Invoke(null, new object[] { "@+GetProgrammVersionID806" });
                timeoutAcknowledgement.TryDecline(op, dialog.Handle, text, caption, () => VerifyTimeoutOwner(op, dialog));
            }
            catch { /* Unknown UI/localization stays untouched; the normal operation timeout remains active. */ }
            finally { insideTimeoutTick = false; }
        }

        private bool VerifyTimeoutOwner(OperationState op, Form dialog)
        {
            if (!Object.ReferenceEquals(getActive(), op) || !NativeTimeoutAcknowledgement.Eligible(op) ||
                !Object.ReferenceEquals(op.Dialog, dialog) || dialog == null || dialog.IsDisposed || !dialog.Visible ||
                !dialog.IsHandleCreated || dialog.GetType() != NativeContext.LoadedType(DialogType, "Plugin.StreletcProGUI")) return false;
            object target = native.GetField(dialog, TargetField);
            return Object.ReferenceEquals(target, op.TargetNode) &&
                String.Equals(native.GuidOf(target), op.Request.recordId, StringComparison.OrdinalIgnoreCase) &&
                !AsBoolean(native.GetProperty(dialog, "Successfully")) && native.VerifyTargetStillMatches(op);
        }

        private void AttachDialog(OperationState op)
        {
            if (!op.NativeCommandStarted || op.NativeRunFinished || !Object.ReferenceEquals(getActive(), op)) return;
            Form[] forms = Application.OpenForms.Cast<Form>().Where(f => f.GetType().FullName == DialogType).ToArray();
            foreach (Form form in forms)
            {
                if (form.GetType() != NativeContext.LoadedType(DialogType, "Plugin.StreletcProGUI"))
                    throw new BridgeException("native_result_mode_unavailable", "The native result dialog type does not match the inspected version.");
                object target = native.GetField(form, TargetField);
                if (!Object.ReferenceEquals(target, op.TargetNode) ||
                    !String.Equals(native.GuidOf(target), op.Request.recordId, StringComparison.OrdinalIgnoreCase)) continue;
                if (op.Dialog != null && op.Dialog != form)
                    throw new BridgeException("native_dialog_ambiguous", "Открыто несколько окон регистрации для устройства.");
                op.Dialog = form;
                // The pinned InitDevice passes !SilentMode into InitDu's fourth
                // constructor argument. Read its readonly field; never change it.
                // This guard runs before selecting/confirming any sensor.
                FieldInfo showResult = form.GetType().Module.ResolveField(ShowSuccessResultField);
                if (showResult == null || showResult.DeclaringType != form.GetType() ||
                    showResult.IsStatic || !showResult.IsInitOnly || showResult.FieldType != typeof(bool) ||
                    !Object.Equals(showResult.GetValue(form), false))
                    throw new BridgeException("native_result_mode_unavailable", "The native command did not create the verified unattended result mode. Registration was not confirmed.");
                op.NativeSuccessDialogSuppressed = true;
                if (observed.Add(form))
                {
                    form.FormClosed += delegate
                    {
                        try
                        {
                            if (!ObserveRegistered(op) && op.CancellationRequested && !op.ConfirmationSent)
                            {
                                RestorePreparedKey(op);
                                // NativeRunFinished is checked by the pipe driver as well.
                                op.SetStatus("cancelled", "native_dialog_closed_before_confirmation");
                            }
                        }
                        catch { op.Fail("native_result_unreadable", "Окно закрыто, но результат регистрации не удалось проверить."); }
                        finally { observed.Remove(form); }
                    };
                }
            }
        }

        private bool ObserveRegistered(OperationState op)
        {
            if (op.Status == "registered") return true;
            Form dialog = op.Dialog;
            if (dialog == null || !op.ConfirmationSent) return false;
            object success = native.GetProperty(dialog, "Successfully");
            if (AsBoolean(success)) op.NativeSuccessObserved = true;
            if (!op.NativeSuccessObserved) return false;
            if (!native.VerifyTargetStillMatches(op))
                throw new BridgeException("native_result_project_changed", "Получен результат, но соответствие устройства выбранной панели изменилось.");
            string actualId = native.GetHardwareId(op.TargetNode);
            if (!String.Equals(actualId, op.Request.expectedHardwareId, StringComparison.Ordinal))
                throw new BridgeException("native_result_id_mismatch", "Стрелец-Мастер сообщил результат для другого аппаратного ID.");
            if (!String.IsNullOrEmpty(op.Request.deviceKey) &&
                !String.Equals(native.GetKey(op.TargetNode), op.Request.deviceKey, StringComparison.Ordinal))
                throw new BridgeException("native_result_key_mismatch", "Не совпал ключ зарегистрированного устройства.");
            op.RecordConfirmed = true;
            op.MarkRegistered(actualId);
            ForgetOriginalKey(op);
            return true;
        }

        private void CompleteSuccessfulOperation(OperationState op)
        {
            if (insideCompletionTick || op == null || !Object.ReferenceEquals(getActive(), op)) return;
            insideCompletionTick = true;
            try
            {
                if (!ObserveRegistered(op)) return;
                Form dialog = op.Dialog;
                if (op.NativeRunFinished && (dialog == null || dialog.IsDisposed || !dialog.Visible)) return;
                // No retry after one dispatch or the bounded completion
                // deadline, including a missing/disposed InitDu whose native
                // command has not yet returned. Only real finish clears busy.
                if (!NativeSuccessAcknowledgement.BeginCompletionPass(op, DateTime.UtcNow)) return;
                if (op.NativeSuccessDialogSuppressed)
                {
                    // Native code skips only its successful result UI and
                    // closes InitDu itself. Do not search for or click windows.
                    NativeSuccessAcknowledgement.RecordCheck(op, "awaiting_native_completion", DateTime.UtcNow);
                    return;
                }
                if (op.NativeRunFinished || dialog == null || dialog.IsDisposed || !dialog.Visible)
                {
                    NativeSuccessAcknowledgement.RecordCheck(op, "success_window_not_found", DateTime.UtcNow);
                    return;
                }
                Type localization = NativeContext.LoadedType("Localization.LocString", "Localization");
                MethodInfo translate = localization.GetMethod("KeyToString", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (translate == null || translate.ReturnType != typeof(string))
                    throw new InvalidOperationException("The inspected localization method is unavailable.");
                string text = (string)translate.Invoke(null, new object[] { "@+IntDU.Succesfull" });
                string caption = (string)translate.Invoke(null, new object[] { "@+iresult" });
                string check = successAcknowledgement.TryAcknowledge(op, dialog.IsHandleCreated ? dialog.Handle : IntPtr.Zero,
                    text, caption, () => VerifyCurrentSuccessOwner(op, dialog));
                NativeSuccessAcknowledgement.RecordCheck(op, check, DateTime.UtcNow);
            }
            catch (Exception error)
            {
                if (op.Status == "registered")
                    NativeSuccessAcknowledgement.RecordCheck(op, "native_completion_unavailable", DateTime.UtcNow);
                else
                {
                    BridgeException known = error as BridgeException;
                    op.Fail(known == null ? "native_result_unreadable" : known.Code,
                        known == null ? "Результат штатной регистрации не удалось проверить." : known.Message);
                }
            }
            finally { insideCompletionTick = false; }
        }

        private bool VerifyCurrentSuccessOwner(OperationState op, Form dialog)
        {
            if (!Object.ReferenceEquals(getActive(), op) || !NativeSuccessAcknowledgement.HasRegistrationProof(op) ||
                !Object.ReferenceEquals(op.Dialog, dialog) || dialog == null || dialog.IsDisposed || !dialog.Visible ||
                !dialog.IsHandleCreated || dialog.GetType() != NativeContext.LoadedType(DialogType, "Plugin.StreletcProGUI")) return false;
            object currentTarget = native.GetField(dialog, TargetField);
            return Object.ReferenceEquals(currentTarget, op.TargetNode) &&
                String.Equals(native.GuidOf(currentTarget), op.Request.recordId, StringComparison.OrdinalIgnoreCase) &&
                AsBoolean(native.GetProperty(dialog, "Successfully")) && native.VerifyTargetStillMatches(op) &&
                String.Equals(native.GetHardwareId(currentTarget), op.Request.expectedHardwareId, StringComparison.Ordinal) &&
                (String.IsNullOrEmpty(op.Request.deviceKey) || String.Equals(native.GetKey(currentTarget), op.Request.deviceKey, StringComparison.Ordinal));
        }

        private void PrepareKey(OperationState op)
        {
            if (String.IsNullOrEmpty(op.Request.deviceKey) || keyPrepared.Contains(op.Request.operationId)) return;
            if (op.Request.deviceKey.Length != 8) throw new BridgeException("invalid_device_key", "KEY должен содержать 8 шестнадцатеричных символов.");
            object info = native.GetProperty(op.TargetNode, "Info");
            PropertyInfo property = KeyProperty(info);
            byte[] original = native.GetProperty(info, "Key") as byte[];
            originalKeys[op.Request.operationId] = original == null ? null : (byte[])original.Clone();
            byte[] key = Enumerable.Range(0, 4).Select(i => Byte.Parse(op.Request.deviceKey.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            keyPrepared.Add(op.Request.operationId);
            property.SetValue(info, key, null);
            if (!String.Equals(native.GetKey(op.TargetNode), op.Request.deviceKey, StringComparison.Ordinal))
                throw new BridgeException("native_key_not_applied", "Не удалось передать KEY в штатный объект устройства.");
        }

        private void RestorePreparedKey(OperationState op)
        {
            if (op.ConfirmationSent || !keyPrepared.Remove(op.Request.operationId)) return;
            try
            {
                if (!String.Equals(native.GetKey(op.TargetNode), op.Request.deviceKey, StringComparison.Ordinal)) return;
                byte[] previous;
                if (!originalKeys.TryGetValue(op.Request.operationId, out previous)) return;
                KeyProperty(native.GetProperty(op.TargetNode, "Info")).SetValue(native.GetProperty(op.TargetNode, "Info"), previous == null ? null : (byte[])previous.Clone(), null);
            }
            catch { /* Never overwrite a concurrent native change or a confirmed registration. */ }
            finally { ForgetOriginalKey(op); }
        }

        private void ForgetOriginalKey(OperationState op)
        {
            byte[] original;
            if (originalKeys.TryGetValue(op.Request.operationId, out original) && original != null) Array.Clear(original, 0, original.Length);
            originalKeys.Remove(op.Request.operationId);
            keyPrepared.Remove(op.Request.operationId);
        }

        private static PropertyInfo KeyProperty(object info)
        {
            if (info == null) throw new BridgeException("native_device_info_missing", "Не найден штатный объект датчика.");
            PropertyInfo property = info.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
                property = info.GetType().GetInterfaces().Select(t => t.GetProperty("Key")).FirstOrDefault(p => p != null && p.CanWrite);
            if (property == null || property.PropertyType != typeof(byte[]) || property.GetSetMethod() == null)
                throw new BridgeException("native_key_setter_missing", "Штатный объект не поддерживает передачу KEY этого типа датчика.");
            return property;
        }

        internal void Cancel(OperationState op)
        {
            if (op == null || op.Status == "registered") return;
            op.CancellationRequested = true;
            if (ObserveRegistered(op)) return;
            if (!op.ConfirmationSent) RestorePreparedKey(op);
            CloseOwnedDialog(op);
            if (op.NativeRunFinished && (op.Dialog == null || op.Dialog.IsDisposed || !op.Dialog.Visible))
            {
                if (op.ConfirmationSent) op.Fail("native_cancel_outcome_unknown", "Регистрация была запущена; отмена не подтверждает её результат. Проверьте состояние устройства.");
                else op.SetStatus("cancelled", "native_operation_stopped_before_confirmation");
            }
        }

        private static void CloseOwnedDialog(OperationState op)
        {
            Form dialog = op.Dialog;
            if (dialog == null || dialog.IsDisposed) return;
            // Closing the vendor form cancels/disposes its own discovery token.
            // No unknown confirmation dialogs or unrelated forms are acted upon.
            dialog.Close();
        }

        private static bool AsBoolean(object value) { return value is bool && (bool)value; }

        public void Dispose()
        {
            if (disposed) return;
            OperationState active = getActive();
            if (active != null && !active.IsTerminal) Cancel(active);
            disposed = true;
            observed.Clear();
            foreach (byte[] original in originalKeys.Values) if (original != null) Array.Clear(original, 0, original.Length);
            originalKeys.Clear();
        }
    }
}
