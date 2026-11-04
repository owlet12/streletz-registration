// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StreletzBridge
{
    internal interface IConnectionDiagnostics
    {
        string LastDiagnosticReport { get; }
        void SaveConnectionDiagnostics(string path);
    }

    // Runs only through the authenticated, open native Master project. This class
    // neither constructs panel packets nor replays a captured registration.
    internal sealed class PanelRegistrationDriver : IPanelRegistrationDriver, IPanelRegistrationRecoveryDriver, IProjectPanelSource, IConnectionDiagnostics
    {
        private const string NativeProtocol = "streletz-master-plugin-v1";
        private readonly Func<Dictionary<string, object>, int, Dictionary<string, object>> exchange;
        private readonly int pollMilliseconds;
        private readonly object availabilityGate = new object();
        private readonly object diagnosticGate = new object();
        private string diagnosticReport = "Связь со Стрелец-Мастером ещё не проверялась. Нажмите «Проверить связь» в окне панелей.\r\n";
        private DateTime availabilityChecked = DateTime.MinValue;
        private bool available;
        private string unavailableReason = "Откройте Стрелец-Мастер 12.4 с модулем регистрации и войдите в проект.";

        public PanelRegistrationDriver() : this(NativePipe.Exchange, 400) { }

        // An injected transport permits contract tests without a panel or WinForms.
        internal PanelRegistrationDriver(Func<Dictionary<string, object>, int, Dictionary<string, object>> transport, int pollMilliseconds)
        {
            exchange = transport ?? throw new ArgumentNullException("transport");
            this.pollMilliseconds = Math.Max(0, pollMilliseconds);
        }

        public bool IsAvailable { get { CheckAvailability(); lock (availabilityGate) return available; } }
        public string UnavailableReason { get { CheckAvailability(); lock (availabilityGate) return unavailableReason; } }
        public string LastDiagnosticReport { get { lock (diagnosticGate) return diagnosticReport; } }
        public void SaveConnectionDiagnostics(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Выберите файл диагностического отчёта.", "path");
            BridgeFiles.WriteAtomic(path, LastDiagnosticReport);
        }

        public Dictionary<string, object> ReadProjectPanels()
        {
            string phase = "hello";
            Dictionary<string, object> hello = null;
            try
            {
                hello = Call(Request("hello"), 3500);
                RequireReady(hello);
                phase = "list_panels";
                var result = Call(Request(phase), 20000);
                RecordDiagnostic(phase, null, hello, "Связь подтверждена; каталог панелей получен.");
                return result;
            }
            catch (Exception error)
            {
                string message = DiagnosticMessage(error);
                RecordDiagnostic(phase, error, hello, message);
                throw new IOException(message + " Откройте «Диагностика связи» для подробностей и сохранения отчёта.", error);
            }
        }

        private void CheckAvailability()
        {
            lock (availabilityGate)
            {
                if (DateTime.UtcNow - availabilityChecked < TimeSpan.FromSeconds(3)) return;
                Dictionary<string, object> hello = null;
                try
                {
                    hello = Call(Request("hello"), 3500);
                    RequireReady(hello);
                    available = true;
                    unavailableReason = "";
                    RecordDiagnostic("hello", null, hello, "Модуль ответил и подтвердил готовность проекта.");
                }
                catch (Exception error)
                {
                    available = false;
                    unavailableReason = DiagnosticMessage(error);
                    RecordDiagnostic("hello", error, hello, unavailableReason);
                }
                availabilityChecked = DateTime.UtcNow;
            }
        }

        public RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target,
            CancellationToken cancellation, Action<RegistrationDriverProgress> progress)
        {
            if (cancellation.IsCancellationRequested) return Stopped(false);
            Dictionary<string, object> nativePanel;
            string nativeFingerprint;
            Dictionary<string, object> hello = null;
            try
            {
                Report(progress, "connecting", "Проверка выбранной панели в открытом проекте Стрелец-Мастера");
                hello = Call(Request("hello"), 20000);
                RequireReady(hello);
                Dictionary<string, object> listing = Call(Request("list_panels"), 20000);
                nativeFingerprint = BridgeValues.Text(listing, "nativeProjectFingerprint");
                if (nativeFingerprint.Length == 0) nativeFingerprint = BridgeValues.Text(listing, "projectFingerprint");
                if (nativeFingerprint.Length != 64) return NativeFailure("native_fingerprint_invalid", "registration_preflight", profile, hello);
                object[] panels = BridgeValues.Array(listing, "panels") ?? new object[0];
                var matching = panels.OfType<Dictionary<string, object>>().Where(p =>
                    Host(BridgeValues.Text(p, "host")) == Host(profile.Host) && Host(profile.Host).Length > 0 &&
                    BridgeValues.Number(p, "port") == profile.Port &&
                    String.Equals(BridgeValues.Text(p, "moduleId"), profile.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                    (String.IsNullOrEmpty(profile.NativePanelId) || SameGuid(BridgeValues.Text(p, "nativeNodeGuid"), profile.NativePanelId)) &&
                    (String.IsNullOrEmpty(profile.SegmentId) || SameGuid(BridgeValues.Text(p, "segmentGuid"), profile.SegmentId)) &&
                    PanelRouteMetadata.Matches(profile, p) &&
                    (BridgeValues.Array(p, "recordIds") ?? new object[0]).Any(id =>
                        SameGuid(Convert.ToString(id), target.RecordId))).ToArray();
                if (matching.Length != 1)
                    return NativeFailure(matching.Length == 0 ? "native_panel_record_mismatch" : "native_panel_ambiguous",
                        "registration_preflight", profile, hello);
                nativePanel = matching[0];
                Guid nativeId;
                if (!Guid.TryParse(BridgeValues.Text(nativePanel, "nativeNodeGuid"), out nativeId) || nativeId == Guid.Empty)
                    return NativeFailure("native_panel_identity_invalid", "registration_preflight", profile, hello);
            }
            catch (Exception error)
            {
                string message = DiagnosticMessage(error);
                RecordDiagnostic("registration_preflight", error, null, message);
                return NativeFailure(DiagnosticCode(error), "registration_preflight", profile, hello, null, message);
            }

            if (cancellation.IsCancellationRequested) return Stopped(false);
            var start = Request("start");
            start["operationId"] = target.OperationId;
            start["projectFingerprint"] = target.ProjectFingerprint;
            start["nativeProjectFingerprint"] = nativeFingerprint;
            start["recordId"] = target.RecordId;
            start["expectedHardwareId"] = target.ExpectedHardwareId;
            start["deviceKey"] = target.DeviceKey ?? "";
            start["timeoutSeconds"] = target.TimeoutSeconds;
            start["panel"] = new Dictionary<string, object> {
                { "panelId", profile.Id }, { "nativePanelId", BridgeValues.Text(nativePanel, "nativeNodeGuid") },
                { "host", profile.Host }, { "port", profile.Port }, { "moduleId", profile.ModuleId }
            };
            PanelRouteMetadata.CopyIdentity(nativePanel, BridgeValues.Object(start, "panel"));

            Stopwatch elapsed = Stopwatch.StartNew();
            Dictionary<string, object> state = null;
            bool timedOut = false, cancelSent = false;
            long cancelAt = -1;
            int communicationFailures = 0;
            try { state = Call(start, 22000); }
            catch (NativeCallException e)
            {
                if (e.Code == "operation_id_conflict" || e.Code == "operation_id_retired")
                    return Unknown("Идентификатор уже использован другой или завершённой штатной операцией. Проверьте результат в Стрелец-Мастере; повтор не выполняется.");
                // Only these errors prove that the plugin rejected the operation
                // before dispatch. An arbitrary error is not proof of no side effect.
                if (IsBeforeDispatchRejection(e.Code)) return NativeFailure(e.Code, "native_start", profile, hello);
            }
            catch { /* A lost start reply never causes another start request. */ }
            finally { start["deviceKey"] = null; }

            for (;;)
            {
                if (state != null)
                {
                    if (!Matches(state, target, profile, BridgeValues.Text(nativePanel, "nativeNodeGuid"), nativeFingerprint)
                        || !PanelRouteMetadata.Same(nativePanel, state))
                        return Unknown("Получен результат другой операции или проекта. Автоматический повтор остановлен; проверьте Стрелец-Мастер.");
                    string status = BridgeValues.Text(state, "status");
                    bool confirmationSent = BridgeValues.Flag(state, "confirmationSent");
                    bool stopped = BridgeValues.Flag(state, "nativeRunFinished") && BridgeValues.Flag(state, "dialogClosed");
                    if (status == "registered")
                    {
                        var confirmed = ConfirmedNativeResult(state, target, profile, nativePanel, nativeFingerprint);
                        return confirmed ?? Unknown("Стрелец-Мастер не предоставил полный результат регистрации с ожидаемым ID датчика.");
                    }
                    if (status == "cancelled" && stopped && !confirmationSent) return Stopped(timedOut);
                    if (status == "failed" && stopped && !confirmationSent)
                    {
                        Dictionary<string, object> error = BridgeValues.Object(state, "error");
                        return NativeFailure(BridgeValues.Text(error, "code"), "native_operation", profile, hello,
                            BridgeValues.Object(state, "adapterDiagnostic"));
                    }
                    if (status == "unknown" && stopped)
                    {
                        var result = Unknown("Подтверждение регистрации было отправлено, но итог не установлен. Проверьте датчик в Стрелец-Мастере; повтор автоматически не выполняется.");
                        string code = BridgeValues.Text(BridgeValues.Object(state, "error"), "code");
                        if (code.Length > 0)
                        {
                            var diagnostic = NativeFailure(code, "native_operation", profile, hello, BridgeValues.Object(state, "adapterDiagnostic"));
                            result.ErrorCode = diagnostic.ErrorCode; result.ErrorPhase = diagnostic.ErrorPhase;
                            result.DiagnosticDetails = diagnostic.DiagnosticDetails;
                            if (code == "native_device_ready_timeout") result.Message = diagnostic.Message;
                            else result.Message += " [" + result.ErrorCode + "; " + result.ErrorPhase + "]";
                        }
                        return result;
                    }
                    if (status != "queued" && status != "connecting" && status != "waiting_sensor" && status != "detected" &&
                        status != "registering" && status != "cancelled" && status != "failed" && status != "unknown")
                        return Unknown("Получено неизвестное состояние штатной регистрации. Проверьте результат в Стрелец-Мастере.");
                    if (!cancelSent) ReportNative(progress, status);
                }

                if (elapsed.ElapsedMilliseconds >= target.TimeoutSeconds * 1000L) timedOut = true;
                if ((timedOut || cancellation.IsCancellationRequested) && cancelAt < 0) cancelAt = elapsed.ElapsedMilliseconds;
                if (cancelAt >= 0 && elapsed.ElapsedMilliseconds - cancelAt > 10000L)
                    return Unknown("Остановка штатной регистрации не подтверждена. Проверьте окно и состояние устройства в Стрелец-Мастере.");

                bool sendCancel = cancelAt >= 0 && !cancelSent;
                if (sendCancel)
                {
                    Report(progress, "cancelling", "Отмена запрошена; ожидается завершение штатной операции");
                    cancelSent = true;
                }
                else if (pollMilliseconds > 0) Thread.Sleep(pollMilliseconds);

                Dictionary<string, object> request = Request(sendCancel ? "cancel" : "status");
                request["operationId"] = target.OperationId;
                try
                {
                    state = Call(request, sendCancel ? 8000 : 3000);
                    communicationFailures = 0;
                }
                catch (NativeCallException e)
                {
                    state = null;
                    if (e.Code == "operation_not_found" || e.Code == "operation_result_expired" || e.Code == "operation_history_expired" ||
                        e.Code == "operation_id_conflict" || e.Code == "operation_id_retired")
                        return Unknown("Стрелец-Мастер не сохранил результат этой операции или был перезапущен. Проверьте фактическую регистрацию; команда повторно не отправлялась.");
                    if (++communicationFailures >= 3)
                        return Unknown("Стрелец-Мастер не отвечает на запрос состояния. Проверьте фактический результат регистрации на компьютере.");
                }
                catch
                {
                    state = null;
                    if (++communicationFailures >= 3)
                        return Unknown("Связь со Стрелец-Мастером потеряна после отправки задания. Результат неизвестен; повтор автоматически не выполняется.");
                }
            }
        }

        public RegistrationRecoveryResult Recover(PanelProfile profile, RegistrationTarget target)
        {
            Dictionary<string, object> hello = null;
            string phase = "recovery_hello";
            try
            {
                // The recovery path has its own, deliberately read-only sequence.
                // It never calls Register, start, cancel or native UI automation.
                hello = Call(Request("hello"), 5000);
                RequireReady(hello);
                long processId = BridgeValues.Number(hello, "processId");
                if (processId < 1 || !IsFingerprint(RecoveryFingerprint(hello)))
                    throw RecoveryError("recovery_native_invalid", "Модуль не подтвердил процесс и текущий проект для безопасной сверки.");
                phase = "recovery_list_panels";
                var listing = Call(Request("list_panels"), 20000);
                string currentFingerprint = RecoveryFingerprint(listing);
                var panels = BridgeValues.Array(listing, "panels");
                if (!IsFingerprint(currentFingerprint) || panels == null || panels.Any(p => !(p is Dictionary<string, object>)))
                    throw RecoveryError("recovery_native_invalid", "Не получен достоверный текущий каталог проекта Стрелец-Мастера.");
                var owners = panels.OfType<Dictionary<string, object>>().Where(p =>
                    (BridgeValues.Array(p, "recordIds") ?? new object[0]).Any(id => SameGuid(Convert.ToString(id), target.RecordId))).ToArray();
                if (owners.Length != 1)
                    throw RecoveryError("recovery_context_mismatch", "Текущий проект Стрелец-Мастера не подтвердил однозначную принадлежность записи прежней операции к панели.");
                var nativePanel = owners[0];
                Guid node, segment;
                if (!Guid.TryParse(BridgeValues.Text(nativePanel, "nativeNodeGuid"), out node) || node == Guid.Empty ||
                    !Guid.TryParse(BridgeValues.Text(nativePanel, "segmentGuid"), out segment) || segment == Guid.Empty ||
                    Host(BridgeValues.Text(nativePanel, "host")) != Host(profile.Host) || Host(profile.Host).Length == 0 ||
                    BridgeValues.Number(nativePanel, "port") != profile.Port ||
                    !String.Equals(BridgeValues.Text(nativePanel, "moduleId"), profile.ModuleId, StringComparison.OrdinalIgnoreCase) ||
                    (!String.IsNullOrEmpty(profile.NativePanelId) && !SameGuid(profile.NativePanelId, node.ToString("D"))) ||
                    (!String.IsNullOrEmpty(profile.SegmentId) && !SameGuid(profile.SegmentId, segment.ToString("D"))) ||
                    !PanelRouteMetadata.Matches(profile, nativePanel) ||
                    (profile.Source == "project" && !SameGuid(profile.Id, ProjectPanelCatalog.StableId(node.ToString("D"), segment.ToString("D")))))
                    throw RecoveryError("recovery_context_mismatch", "Маршрут текущего проекта Стрелец-Мастера не совпал с выбранной панелью прежней операции.");
                // Validate internally consistent direct/terminal metadata even
                // when a legacy explicit profile does not carry tree GUIDs.
                PanelRouteMetadata.CopyNative(nativePanel, new Dictionary<string, object> {
                    { "nativePanelId", node.ToString("D") }, { "segmentId", segment.ToString("D") }
                });
                phase = "recovery_status";
                var statusRequest = Request("status");
                statusRequest["operationId"] = target.OperationId;
                Dictionary<string, object> state = null;
                bool missing = false;
                try { state = Call(statusRequest, 5000); }
                catch (NativeCallException error)
                {
                    if (error.Code != "operation_not_found" && error.Code != "operation_result_expired" && error.Code != "operation_history_expired") throw;
                    missing = true;
                }
                // A Master restart or project change across the separate IPC
                // exchanges invalidates this probe. The retained ORIGINAL native
                // fingerprint may differ after a successful subtype mutation;
                // only the current listing is compared to the fresh hello.
                phase = "recovery_recheck";
                var after = Call(Request("hello"), 5000);
                RequireReady(after);
                if (BridgeValues.Number(after, "processId") != processId || RecoveryFingerprint(after) != currentFingerprint)
                    throw RecoveryError("recovery_context_changed", "Стрелец-Мастер или текущий проект изменился во время сверки. Повторите проверку прежней операции.");
                if (missing)
                {
                    RecordDiagnostic("recovery_status", null, after, "Живой проект и маршрут проверены; сохранённый результат прежней операции отсутствует. Требуется ручная сверка.");
                    return new RegistrationRecoveryResult { Outcome = "manual_review_required", ManualReviewAllowed = true,
                        Message = "Стрелец-Мастер не сохранил результат этой операции. Проект, запись и маршрут проверены. На компьютере проверьте фактическую привязку нужного датчика и убедитесь, что прежний поиск или окно регистрации остановлено. Отсутствие записи не означает, что датчик не зарегистрирован." };
                }
                phase = "recovery_validation";
                string originalFingerprint = RecoveryFingerprint(state);
                if (!IsFingerprint(originalFingerprint) || !Matches(state, target, profile, node.ToString("D"), originalFingerprint) ||
                    !PanelRouteMetadata.Same(nativePanel, state))
                    throw RecoveryError("recovery_identity_mismatch", "Сохранённый штатный результат относится к другой операции, записи, панели или маршруту. Ручное завершение этой проверкой не разрешено.");
                foreach (string key in new[] { "registrationConfirmed", "confirmationSent", "nativeSuccessObserved", "nativeRunFinished", "dialogClosed" })
                {
                    object value;
                    if (!state.TryGetValue(key, out value) || !(value is bool))
                        throw RecoveryError("recovery_native_invalid", "Сохранённый штатный результат имеет неполные признаки подтверждения и остановки.");
                }
                object dialogOpen;
                if (state.TryGetValue("nativeDialogOpen", out dialogOpen) && !(dialogOpen is bool))
                    throw RecoveryError("recovery_native_invalid", "Сохранённый штатный результат содержит некорректный признак открытого окна.");
                string status = BridgeValues.Text(state, "status");
                if (!new[] { "queued", "connecting", "waiting_sensor", "detected", "registering", "registered", "cancelled", "failed", "unknown" }.Contains(status))
                    throw RecoveryError("recovery_native_invalid", "Стрелец-Мастер вернул неизвестное состояние прежней операции.");
                bool stopped = BridgeValues.Flag(state, "nativeRunFinished") && BridgeValues.Flag(state, "dialogClosed") && !BridgeValues.Flag(state, "nativeDialogOpen");
                if (!stopped)
                {
                    RecordDiagnostic("recovery_status", null, after, "Прежняя штатная операция или её окно ещё не завершены; команды запуска и отмены не отправлялись.");
                    return new RegistrationRecoveryResult { Outcome = "native_active", ManualReviewAllowed = false,
                        Message = "Стрелец-Мастер ещё выполняет прежнюю операцию или завершает её окно. Подождите несколько секунд и повторите сверку. Если окно не закрывается автоматически, сохраните диагностику регистрации. Новая регистрация этой проверкой не запускалась." };
                }
                if (status == "registered")
                {
                    var result = ConfirmedNativeResult(state, target, profile, nativePanel, originalFingerprint);
                    if (result == null)
                        throw RecoveryError("recovery_evidence_missing", "Сохранённый результат не содержит полного подтверждения регистрации ожидаемого аппаратного ID.");
                    RecordDiagnostic("recovery_status", null, after, "Сохранённый штатный результат регистрации проверен без повторного запуска.");
                    return new RegistrationRecoveryResult { Outcome = "restored", Message = result.Message, Result = result };
                }
                bool confirmationSent = BridgeValues.Flag(state, "confirmationSent");
                if (status == "cancelled" || status == "failed")
                {
                    if (confirmationSent || BridgeValues.Flag(state, "registrationConfirmed") || BridgeValues.Flag(state, "nativeSuccessObserved"))
                        throw RecoveryError("recovery_evidence_missing", "Сохранённый отказ или отмена противоречит признакам подтверждения регистрации. Требуется проверка результата в Стрелец-Мастере.");
                    RegistrationDriverResult result = status == "cancelled" ? Stopped(false)
                        : NativeFailure(BridgeValues.Text(BridgeValues.Object(state, "error"), "code"), "native_operation", profile, after,
                            BridgeValues.Object(state, "adapterDiagnostic"));
                    return new RegistrationRecoveryResult { Outcome = "restored", Message = result.Message, Result = result };
                }
                if (status == "unknown" && !BridgeValues.Flag(state, "registrationConfirmed"))
                {
                    RecordDiagnostic("recovery_status", null, after, "Прежняя операция остановлена, но её физический результат остаётся неизвестным.");
                    return new RegistrationRecoveryResult { Outcome = "manual_review_required", ManualReviewAllowed = true,
                        Message = "Прежнее штатное окно закрыто и выполнение завершено, но результат привязки не подтверждён. На компьютере проверьте фактическую регистрацию полного ID датчика. Ручная сверка сохранит прежний результат как неизвестный; она не отменяет выполненную привязку." };
                }
                throw RecoveryError("recovery_native_invalid", "Состояние прежней операции не согласуется с признаками её завершения. Карточка остаётся заблокированной.");
            }
            catch (RegistrationRequestException error)
            {
                RecordDiagnostic(phase, error, hello, error.Message);
                throw;
            }
            catch (Exception error)
            {
                string code = SafeDiagnosticCode(DiagnosticCode(error));
                string message = DiagnosticMessage(error);
                RecordDiagnostic(phase, error, hello, message);
                throw RecoveryError("recovery_native_unavailable", "Безопасная сверка не завершена: " + message + " [" + code + "]. Ручное завершение не разрешено; повторный запуск не выполнялся.");
            }
        }

        private static RegistrationRequestException RecoveryError(string code, string message)
        { return new RegistrationRequestException(code, message); }

        private static bool IsFingerprint(string value)
        { return Regex.IsMatch(value ?? "", "^[0-9a-fA-F]{64}$"); }

        private static string RecoveryFingerprint(Dictionary<string, object> value)
        {
            string fingerprint = BridgeValues.Text(value, "nativeProjectFingerprint");
            return fingerprint.Length > 0 ? fingerprint : BridgeValues.Text(value, "projectFingerprint");
        }

        private static RegistrationDriverResult ConfirmedNativeResult(Dictionary<string, object> state, RegistrationTarget target,
            PanelProfile profile, Dictionary<string, object> nativePanel, string nativeFingerprint)
        {
            if (!BridgeValues.Flag(state, "registrationConfirmed") || !BridgeValues.Flag(state, "confirmationSent") ||
                !BridgeValues.Flag(state, "nativeSuccessObserved") ||
                !String.Equals(BridgeValues.Text(state, "confirmedHardwareId"), target.ExpectedHardwareId, StringComparison.Ordinal) ||
                BridgeValues.Text(state, "validatedBy") != "native-fingerprint-record-and-panel") return null;
            return new RegistrationDriverResult {
                State = "registered", RegistrationConfirmed = true,
                HardwareId = BridgeValues.Text(state, "confirmedHardwareId"),
                Message = "Штатная регистрация подтверждена Стрелец-Мастером",
                Evidence = BridgeJson.Serialize(new Dictionary<string, object> {
                    { "source", NativeProtocol }, { "operationId", target.OperationId },
                    { "panelId", profile.Id }, { "nativePanelId", BridgeValues.Text(nativePanel, "nativeNodeGuid") },
                    { "moduleId", profile.ModuleId }, { "host", profile.Host }, { "port", profile.Port },
                    { "recordId", target.RecordId }, { "hardwareId", BridgeValues.Text(state, "confirmedHardwareId") },
                    { "nativeProjectFingerprint", nativeFingerprint },
                    { "validation", "native-success-and-record-id" }, { "finishedUtc", BridgeValues.Text(state, "finishedUtc") }
                })
            };
        }

        private Dictionary<string, object> Call(Dictionary<string, object> request, int timeout)
        {
            Dictionary<string, object> response = exchange(request, timeout);
            if (response == null || BridgeValues.Text(response, "protocol") != NativeProtocol)
                throw new NativeTransportException("protocol", "native_protocol_mismatch", null);
            if (!BridgeValues.Flag(response, "ok"))
                throw new NativeCallException(BridgeValues.Text(BridgeValues.Object(response, "error"), "code"));
            return BridgeValues.Object(response, "data") ?? throw new NativeTransportException("protocol", "native_data_missing", null);
        }

        private static void RequireReady(Dictionary<string, object> hello)
        {
            if (BridgeValues.Text(hello, "version") != "12.4") throw new NativeCallException("unsupported_master_version");
            if (!BridgeValues.Flag(hello, "ready"))
            {
                string reason = BridgeValues.Text(hello, "unavailableReason");
                throw new NativeCallException(String.IsNullOrEmpty(reason) ? "native_context_unavailable" : reason);
            }
        }

        private static bool Matches(Dictionary<string, object> state, RegistrationTarget target, PanelProfile profile,
            string nativePanelId, string fingerprint)
        {
            return BridgeValues.Text(state, "operationId") == target.OperationId &&
                String.Equals(BridgeValues.Text(state, "projectFingerprint"), target.ProjectFingerprint, StringComparison.OrdinalIgnoreCase) &&
                SameGuid(BridgeValues.Text(state, "recordId"), target.RecordId) &&
                BridgeValues.Text(state, "expectedHardwareId") == target.ExpectedHardwareId &&
                SameGuid(BridgeValues.Text(state, "panelId"), profile.Id) &&
                SameGuid(BridgeValues.Text(state, "nativePanelId"), nativePanelId) &&
                Host(BridgeValues.Text(state, "host")) == Host(profile.Host) &&
                BridgeValues.Number(state, "port") == profile.Port &&
                String.Equals(BridgeValues.Text(state, "moduleId"), profile.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                BridgeValues.Text(state, "nativeProjectFingerprint") == fingerprint;
        }

        private static bool SameGuid(string left, string right)
        {
            Guid a, b;
            return Guid.TryParse(left, out a) && Guid.TryParse(right, out b) && a != Guid.Empty && a == b;
        }

        private static string Host(string input)
        {
            IPAddress value;
            return IPAddress.TryParse((input ?? "").Trim(), out value) ? value.ToString().ToLowerInvariant() : "";
        }

        private static Dictionary<string, object> Request(string type) { return new Dictionary<string, object> { { "type", type } }; }
        private static void Report(Action<RegistrationDriverProgress> progress, string state, string message)
        { if (progress != null) progress(new RegistrationDriverProgress(state, message)); }
        private static void ReportNative(Action<RegistrationDriverProgress> progress, string state)
        {
            if (state == "waiting_sensor") Report(progress, "waiting_for_sensor", "Поиск включён. Переведите нужный датчик в режим регистрации");
            else if (state == "detected") Report(progress, "detected", "Обнаружен датчик с ожидаемым ID");
            else if (state == "registering") Report(progress, "registering", "Стрелец-Мастер выполняет штатную регистрацию датчика");
            else if (state == "queued" || state == "connecting") Report(progress, "connecting", "Стрелец-Мастер открывает подключение выбранной панели");
        }

        private static RegistrationDriverResult Failed(string message)
        { return new RegistrationDriverResult { State = "failed", StopConfirmed = true, Message = message }; }

        private RegistrationDriverResult NativeFailure(string code, string phase, PanelProfile profile,
            Dictionary<string, object> hello = null, Dictionary<string, object> nativeDetails = null, string message = null)
        {
            code = SafeDiagnosticCode(code);
            phase = RegistrationDiagnosticData.Phase(phase);
            var details = RegistrationDiagnosticData.Details(nativeDetails);
            details["expectedHost"] = profile.Host;
            details["expectedPort"] = profile.Port;
            details["expectedModuleId"] = profile.ModuleId;
            details["nativePanelId"] = profile.NativePanelId ?? "";
            details["targetSegmentId"] = profile.SegmentId ?? "";
            details["routeKind"] = profile.RouteKind ?? "";
            details["terminalPanelId"] = profile.TerminalPanelId ?? "";
            details["terminalSegmentId"] = profile.TerminalSegmentId ?? "";
            details["adapterNodeId"] = profile.AdapterNodeId ?? "";
            if (!details.ContainsKey("expectedAdapterNodeId") && !String.IsNullOrEmpty(profile.AdapterNodeId))
                details["expectedAdapterNodeId"] = profile.AdapterNodeId;
            details["pluginVersion"] = BridgeValues.Text(hello, "pluginVersion");
            CopyCompletionDiagnostics(hello, details);
            details = RegistrationDiagnosticData.Details(details);
            if (String.IsNullOrEmpty(message)) message = Explain(code);
            if (code == "native_adapter_mismatch" && BridgeValues.Text(details, "actualConnectionKind") == "usb")
                message = "Мастер открыл USB-соединение, а выбранный маршрут панели требует Ethernet. Проверьте способ подключения МК-IP в Стрелец-Мастере.";
            else if (code == "native_adapter_mismatch" && BridgeValues.Text(details, "actualConnectionKind") == "cloud")
                message = "Мастер открыл соединение через облако, а выбранный маршрут панели требует Ethernet. Проверьте способ подключения МК-IP в Стрелец-Мастере.";
            string check = BridgeValues.Text(details, "check");
            if (code == "native_adapter_mismatch")
            {
                if (check == "endpoint_module_missing")
                    message = "В параметрах штатного подключения отсутствует ID МК-IP. Дополнительная проверка принадлежности подключения проекту не подтверждена; сохраните диагностику регистрации.";
                else if (check == "endpoint_module_invalid")
                    message = "ID МК-IP в параметрах штатного подключения не удалось разобрать как полный шестибайтный номер. Сохраните диагностику регистрации.";
                else if (check == "endpoint_module_mismatch")
                    message = "ID МК-IP в параметрах подключения или живом проекте не совпал с ID выбранного маршрута. Точные значения указаны в диагностике регистрации.";
                else if (check == "endpoint_native_route_mismatch")
                    message = "Прямое подключение без ID МК-IP не прошло дополнительную проверку по живому проекту. Результаты проверки маршрута указаны в диагностике регистрации.";
            }
            message += " [" + code + "; " + phase + (check.Length > 0 ? "; " + check : "") + "]";
            RecordDiagnostic(phase, new NativeCallException(code), hello, message);
            return new RegistrationDriverResult { State = "failed", StopConfirmed = true, Message = message,
                ErrorCode = code, ErrorPhase = phase, DiagnosticDetails = details };
        }
        private static RegistrationDriverResult Unknown(string message)
        { return new RegistrationDriverResult { State = "unknown", Message = message }; }
        private static RegistrationDriverResult Stopped(bool timedOut)
        { return new RegistrationDriverResult { State = timedOut ? "timed_out" : "cancelled", StopConfirmed = true,
            Message = timedOut ? "Время поиска истекло; штатная операция остановлена до подтверждения привязки." : "Штатная операция остановлена до подтверждения привязки." }; }

        private static bool IsBeforeDispatchRejection(string code)
        {
            return new[] { "native_ui_busy", "native_registration_busy", "native_dialog_busy",
                "native_result_closing", "native_result_acknowledgement_blocked", "native_result_mode_unavailable", "record_id_invalid", "operation_id_invalid",
                "hardware_id_invalid", "device_key_invalid", "timeout_invalid", "record_not_available", "panel_mismatch", "native_project_changed",
                "record_already_assigned", "hardware_id_already_assigned", "device_key_required", "operation_history_full" }.Contains(code);
        }

        private static Exception DiagnosticCause(Exception error)
        {
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.Flatten().InnerExceptions.Count == 1)
                return DiagnosticCause(aggregate.Flatten().InnerExceptions[0]);
            return error;
        }

        internal static string ConnectionFailureCode(Exception error) { return SafeDiagnosticCode(DiagnosticCode(error)); }

        private static string DiagnosticCode(Exception original)
        {
            Exception error = DiagnosticCause(original);
            var native = error as NativeCallException;
            if (native != null) return native.Code;
            var recovery = error as RegistrationRequestException;
            if (recovery != null) return SafeDiagnosticCode(recovery.Code);
            var transport = error as NativeTransportException;
            if (transport != null) return transport.Code;
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "native_access_denied";
            if (error is TimeoutException) return "native_transport_timeout";
            if (error is EndOfStreamException) return "native_response_closed";
            if (error is DecoderFallbackException) return "native_utf8_invalid";
            if (error is PlatformNotSupportedException) return "native_platform_unsupported";
            if (error is InvalidDataException) return "native_data_invalid";
            if (error is IOException) return "native_io_error";
            return "native_unexpected_error";
        }

        private static string DiagnosticMessage(Exception error)
        {
            switch (DiagnosticCode(error))
            {
                case "native_pipe_connect_timeout":
                    return "Локальный канал модуля регистрации не открылся вовремя. Модуль может быть не загружен, запущен в другом сеансе Windows или занят другим запросом; один тайм-аут не устанавливает причину.";
                case "native_response_timeout":
                    return "Канал модуля открыт, но ответ Стрелец-Мастера не получен вовремя. Проверьте состояние его окна; повторный запуск регистрации не выполнялся.";
                case "native_transport_timeout":
                    return "Истекло время ожидания локального модуля. Проверьте запущенный Стрелец-Мастер, модуль регистрации и сеанс Windows.";
                case "native_access_denied":
                    return "Windows отказала в доступе к локальному каналу. Запустите мост и Стрелец-Мастер под одной учётной записью в одном обычном интерактивном сеансе.";
                case "native_response_closed":
                    return "Модуль принял соединение и закрыл его без полного ответа. Это может быть ошибка модуля или слишком большой ответ старой версии. Обновите Plugin.StreletzRegistration.dll из текущего комплекта и перезапустите Стрелец-Мастер.";
                case "native_protocol_mismatch": case "native_data_missing": case "native_json_invalid": case "native_utf8_invalid": case "native_data_invalid": case "native_response_serialization_failed":
                    return "Локальный модуль вернул ответ неподдерживаемого формата. Сверьте версии Windows-моста и Plugin.StreletzRegistration.dll; подробный код сохранён в диагностике.";
                case "native_response_too_large":
                    return "Ответ локального модуля превышает допустимый размер. Каталог не принят; регистрация не запускалась.";
                case "native_platform_unsupported":
                    return "Связь с локальным Стрелец-Мастером поддерживается только в Windows.";
                case "native_io_error":
                    return "Ошибка обмена по локальному каналу Стрелец-Мастера. Откройте диагностику: там указан этап и системный код ошибки.";
                case "native_unexpected_error":
                    return "При проверке локального модуля произошла непредвиденная ошибка. Сохраните диагностику связи; причина пока не установлена.";
                default: return Explain(DiagnosticCode(error));
            }
        }

        private void RecordDiagnostic(string phase, Exception original, Dictionary<string, object> hello, string message)
        {
            // Only explicit diagnostic fields are recorded. Never serialize requests,
            // replies, project records, credentials, exception messages or stack traces.
            var report = new StringBuilder();
            report.AppendLine("Стрелец — диагностика локальной связи с Мастером");
            report.AppendLine("Время UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            report.AppendLine("Этап: " + phase);
            report.AppendLine("Результат: " + (original == null ? "успешно" : "ошибка"));
            report.AppendLine("Сообщение: " + message);
            report.AppendLine("Протокол: " + NativeProtocol);
            report.AppendLine("Windows-мост: 0.5.10");
            report.AppendLine("Платформа: " + Environment.OSVersion.Platform + "; процесс " + (Environment.Is64BitProcess ? "64" : "32") + " бит");
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    report.AppendLine("PID моста: " + current.Id);
                    report.AppendLine("Сеанс моста: " + current.SessionId);
                    report.AppendLine("Ожидаемый локальный канал: StreletzMasterBridgeV1-" + current.SessionId);
                }
            }
            catch { report.AppendLine("Сеанс моста: не удалось прочитать"); }
            if (hello != null)
            {
                report.AppendLine("hello получен: да; ready=" + BridgeValues.Flag(hello, "ready"));
                report.AppendLine("PID ответившего Мастера: " + DiagnosticNumber(hello, "processId"));
                // Do not include arbitrary text fields returned by a mismatched endpoint.
                report.AppendLine("Версия 12.4 подтверждена: " + (BridgeValues.Text(hello, "version") == "12.4"));
                string pluginVersion = BridgeValues.Text(RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                    { "pluginVersion", BridgeValues.Text(hello, "pluginVersion") } }), "pluginVersion");
                report.AppendLine("Версия загруженного дополнения: " + (pluginVersion.Length > 0 ? pluginVersion : "старое дополнение не сообщило версию"));
                var detail = BridgeValues.Object(hello, "diagnostics");
                string component = BridgeValues.Text(detail, "buildFailureComponent");
                if (KnownMasterComponents.Contains(component)) report.AppendLine("Компонент проверки сборки: " + component);
                if (detail != null) report.AppendLine("Максимум ответа модуля (символов JSON): " + DiagnosticNumber(detail, "maximumResponseJsonCharacters"));
                var completion = new Dictionary<string, object>();
                CopyCompletionDiagnostics(hello, completion);
                if (completion.ContainsKey("autoConfirmSuccessResult"))
                    report.AppendLine("Автоматическое подтверждение успешного результата: " +
                        ((bool)completion["autoConfirmSuccessResult"] ? "да" : "нет"));
                if (completion.ContainsKey("suppressNativeSuccessDialog"))
                    report.AppendLine("Подавление окна успешного результата для регистрации с телефона: " +
                        ((bool)completion["suppressNativeSuccessDialog"] ? "да" : "нет"));
                if (completion.ContainsKey("nativeSuccessDialogSuppressed"))
                    report.AppendLine("Окно успешного результата подавлено для текущей операции: " +
                        ((bool)completion["nativeSuccessDialogSuppressed"] ? "да" : "нет"));
                if (completion.ContainsKey("nativeCompletionState"))
                    report.AppendLine("Состояние завершения окна результата: " + completion["nativeCompletionState"]);
                if (completion.ContainsKey("nativeCompletionCheck"))
                    report.AppendLine("Проверка завершения окна результата: " + completion["nativeCompletionCheck"]);
                if (completion.ContainsKey("nativeCompletionLastCheck"))
                    report.AppendLine("Последняя проверка до завершения ожидания: " + completion["nativeCompletionLastCheck"]);
            }
            if (original != null)
            {
                Exception error = DiagnosticCause(original);
                var transport = error as NativeTransportException;
                report.AppendLine("Код: " + SafeDiagnosticCode(DiagnosticCode(error)));
                if (transport != null) report.AppendLine("Этап транспорта: " + transport.Phase);
                Exception system = transport != null && transport.InnerException != null ? DiagnosticCause(transport.InnerException) : error;
                report.AppendLine("Тип исключения: " + system.GetType().Name);
                report.AppendLine("HRESULT: 0x" + system.HResult.ToString("X8", CultureInfo.InvariantCulture));
                var win32 = system as Win32Exception;
                if (win32 != null) report.AppendLine("Win32: " + win32.NativeErrorCode);
            }
            AppendMasterProcesses(report);
            report.AppendLine(phase == "hello" || phase == "list_panels"
                ? "Проверка только читает состояние. Она не запускает регистрацию и не меняет проект или оборудование."
                : "Отчёт фиксирует результат операции; его открытие и сохранение не запускают регистрацию.");
            report.AppendLine("Пароли, PIN, KEY датчиков, содержимое проекта и исходные ответы в отчёт не включены.");
            lock (diagnosticGate) diagnosticReport = report.ToString();
        }

        private static readonly string[] KnownMasterComponents = { "Master.exe", "Core.dll", "Engine.dll", "Argus.Configuration.dll",
            "Argus.GUI.Common.dll", "Plugin.StreletcProGUI.dll", "Plugin.StreletcPro.dll", "Plugin.MostIPI.dll", "Plugin.CorePlugins.dll",
            "Plugin.RROP-LON.dll", "BaseAdapter.dll", "UsbEthernetAdapter.dll", "HardwareAdaptersFactory.dll", "TunnelAdapter.dll",
            "BaseAdapterTransport.dll" };

        private static string DiagnosticNumber(Dictionary<string, object> value, string name)
        {
            try { return BridgeValues.Number(value, name).ToString(CultureInfo.InvariantCulture); }
            catch { return "не подтверждено"; }
        }

        private static void CopyCompletionDiagnostics(Dictionary<string, object> hello, Dictionary<string, object> destination)
        {
            // Completion telemetry describes the previously owned native window.
            // It is diagnostic only and never proves registration or permits a replay.
            var clean = RegistrationDiagnosticData.Details(BridgeValues.Object(hello, "diagnostics"));
            foreach (string field in new[] { "nativeCompletionState", "nativeCompletionCheck", "nativeCompletionLastCheck", "nativeSuccessDialogSuppressed" })
                if (clean.ContainsKey(field)) destination[field] = clean[field];
            var capabilities = BridgeValues.Object(hello, "capabilities");
            object value;
            foreach (string field in new[] { "autoConfirmSuccessResult", "suppressNativeSuccessDialog" })
                if (capabilities != null && capabilities.TryGetValue(field, out value) && value is bool)
                    destination[field] = value;
        }

        private static readonly HashSet<string> KnownDiagnosticCodes = new HashSet<string>((
            "master_login_required master_project_required master_closing unsupported_master_version unsupported_master_build version_check_failed " +
            "native_execution_context_unsupported native_component_unavailable native_api_mismatch native_context_unavailable project_too_large " +
            "recovery_native_invalid recovery_context_mismatch recovery_context_changed recovery_identity_mismatch recovery_evidence_missing " +
            "native_ui_busy native_registration_busy native_dialog_busy native_result_closing native_result_acknowledgement_blocked native_result_mode_unavailable " +
            "record_not_available panel_mismatch native_adapter_mismatch " +
            "native_project_changed native_result_project_changed record_already_assigned hardware_id_already_assigned device_key_required " +
            "device_key_invalid native_key_not_applied native_dialog_not_opened native_command_failed native_registration_unconfirmed " +
            "native_device_ready_timeout native_dialog_closed operation_id_conflict operation_history_expired native_pipe_connect_timeout native_response_timeout " +
            "native_transport_timeout native_access_denied native_response_closed native_protocol_mismatch native_data_missing " +
            "native_json_invalid native_utf8_invalid native_data_invalid native_response_too_large native_response_serialization_failed native_platform_unsupported native_io_error " +
            "native_unexpected_error request_invalid request_not_supported native_operation_failed operation_not_found operation_result_expired " +
            "operation_id_retired operation_history_full native_outcome_unknown record_id_invalid operation_id_invalid hardware_id_invalid " +
            "timeout_invalid native_dialog_error native_grid_shape native_duplicate_discovery native_handler_shape native_dialog_ambiguous " +
            "native_fingerprint_invalid native_panel_record_mismatch native_panel_ambiguous native_panel_identity_invalid " +
            "native_result_id_mismatch native_result_key_mismatch invalid_device_key native_device_info_missing native_key_setter_missing " +
            "native_command_unavailable native_dialog_mismatch registration_timeout").Split(' '), StringComparer.Ordinal);

        internal static string SafeDiagnosticCode(string value)
        {
            // Native codes are identifiers defined by the inspected plugin. A malformed
            // code must not turn an error response into an arbitrary log payload.
            return value != null && KnownDiagnosticCodes.Contains(value) ? value : "unrecognized_native_error";
        }

        private static void AppendMasterProcesses(StringBuilder report)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            { report.AppendLine("Процессы Master.exe: проверка доступна в Windows"); return; }
            Process[] processes;
            try { processes = Process.GetProcessesByName("Master"); }
            catch { report.AppendLine("Процессы Master.exe: не удалось перечислить без изменения прав"); return; }
            report.AppendLine("Найдено процессов Master.exe: " + processes.Length);
            for (int i = 0; i < processes.Length; i++) using (Process process = processes[i])
            {
                if (i >= 8) continue;
                try
                {
                    report.AppendLine("Master PID=" + process.Id + "; сеанс=" + process.SessionId);
                    string directory = Path.GetDirectoryName(process.MainModule.FileName);
                    report.AppendLine("Папка Master: " + directory);
                    report.AppendLine("Plugin.StreletzRegistration.dll рядом с Master.exe: " +
                        (File.Exists(Path.Combine(directory, "Plugin.StreletzRegistration.dll")) ? "есть; загрузка подтверждается только ответом hello" : "файл не виден по этому пути: отсутствует либо недоступен"));
                }
                catch { report.AppendLine("Часть сведений о процессе недоступна; права не повышались"); }
            }
            if (processes.Length == 0) report.AppendLine("Master.exe не обнаружен при этой проверке; процесс мог ещё не запуститься или уже закрыться.");
        }

        private static string Explain(string code)
        {
            switch (code)
            {
                case "master_login_required": return "Модуль связи найден. В Стрелец-Мастере требуется вход пользователя в открытый проект.";
                case "master_project_required": return "Модуль связи найден. Откройте базу проекта в Стрелец-Мастере.";
                case "unsupported_master_version": return "Модуль ответил, но не подтвердил поддерживаемую версию Стрелец-Мастера 12.4.";
                case "unsupported_master_build": return "Модуль найден, но файлы Стрелец-Мастера не совпадают с проверенной сборкой 12.4. Защита сохранена; сведения о компоненте доступны в диагностике.";
                case "version_check_failed": return "Модуль найден, но проверка файлов Стрелец-Мастера не завершилась. Проверьте установку и доступ на чтение файлов программы.";
                case "native_execution_context_unsupported": return "Модуль найден, но открыт неподдержанный контекст выполнения. Нужен локальный Стрелец-Мастер с обычным открытым проектом.";
                case "native_component_unavailable": case "native_api_mismatch": return "Модуль найден, но необходимые компоненты Стрелец-Мастера не загрузились либо имеют другую структуру. Проверьте комплект версии 12.4.";
                case "native_context_unavailable": return "Модуль найден, но проект Стрелец-Мастера ещё не готов. Дождитесь загрузки проекта и завершите вход пользователя.";
                case "master_closing": return "Стрелец-Мастер закрывается; локальный модуль останавливается вместе с ним.";
                case "project_too_large": return "Размер проекта превышает поддержанный предел обхода дерева; модуль остановил чтение.";
                case "native_ui_busy": return "Стрелец-Мастер занят. Запрос отозван до запуска; закройте лишние окна и повторите действие.";
                case "native_result_closing": return "Предыдущий датчик зарегистрирован. Стрелец-Мастер завершает окно результата. Подождите несколько секунд, затем снова нажмите «Начать регистрацию на панели». Новое задание не запускалось; автоматического повтора нет.";
                case "native_result_acknowledgement_blocked": return "Предыдущая регистрация завершилась успешно, но окно результата не удалось подтвердить автоматически. Новое задание не запускалось. Сохраните диагностику регистрации для проверки причины.";
                case "native_result_mode_unavailable": return "Не удалось подготовить завершение регистрации без окна подтверждения. Новое задание не запускалось. Сохраните диагностику регистрации для проверки дополнения Мастера.";
                case "native_registration_busy": return "В Стрелец-Мастере ещё выполняется прежняя регистрация. Дождитесь её результата или запросите отмену прежней операции. Новое задание не запускалось. После завершения прежней операции его можно отдельно начать с телефона.";
                case "native_dialog_busy": return "В Стрелец-Мастере открыто другое окно регистрации или диалог. Мост не может подтвердить, что это окно принадлежит прежнему заданию телефона. Новое задание не запускалось; закройте это окно в Стрелец-Мастере и отдельно повторите действие.";
                case "record_not_available": return "Запись датчика не найдена в открытом проекте Мастера на проверенном маршруте МК-IP. Обновите проект и каталог панелей.";
                case "panel_mismatch": return "Переданная панель или её маршрут не совпали с принадлежностью датчика в открытом проекте Мастера. Обновите каталог панелей.";
                case "native_adapter_mismatch": return "Штатное окно Мастера открыло подключение, которое не удалось подтвердить для выбранной панели. Точная проверка указана в диагностике регистрации.";
                case "native_fingerprint_invalid": return "Не удалось проверить идентификатор открытого проекта Стрелец-Мастера.";
                case "native_panel_record_mismatch": return "Запись датчика не относится к выбранной панели в открытом проекте Стрелец-Мастера. Сверьте проект, адрес, порт и ID МК-IP.";
                case "native_panel_ambiguous": return "В проекте найдено несколько соответствующих подключений. Уточните маршрут панели в Стрелец-Мастере.";
                case "native_panel_identity_invalid": return "Модуль не вернул однозначный идентификатор выбранной панели.";
                case "native_project_changed": case "native_result_project_changed": return "Проект или маршрут панели изменился во время проверки. Откройте согласованные проекты на телефоне и компьютере.";
                case "record_already_assigned": case "hardware_id_already_assigned": return "Запись или аппаратный ID уже используется другой привязкой в Стрелец-Мастере.";
                case "device_key_required": case "device_key_invalid": case "native_key_not_applied": return "Для этого режима нужен KEY датчика: 8 шестнадцатеричных символов. Проверьте ключ перед запуском.";
                case "native_dialog_not_opened": case "native_command_failed": case "native_registration_unconfirmed": return "Стрелец-Мастер не подтвердил запуск или результат. Проверьте его сообщение на компьютере.";
                case "native_device_ready_timeout": return "Истекло время ожидания готовности устройства. Повтор автоматически не выполняется. Регистрация не подтверждена; проверьте устройство в Стрелец-Мастере перед новой попыткой.";
                case "native_dialog_closed": return "Штатное окно закрыто без отправки подтверждения привязки.";
                case "operation_id_conflict": case "operation_history_expired": return "Этот идентификатор операции уже использован. Проверьте сохранённый результат в Стрелец-Мастере.";
                default: return "Стрелец-Мастер не готов к регистрации (" + SafeDiagnosticCode(code) + "). Проверьте версию 12.4, вход в проект и прямое подключение МК-IP.";
            }
        }

        private sealed class NativeCallException : Exception
        {
            internal readonly string Code;
            internal NativeCallException(string code) { Code = code; }
        }

        private sealed class NativeTransportException : IOException
        {
            internal readonly string Phase;
            internal readonly string Code;
            internal NativeTransportException(string phase, string code, Exception inner) : base(code, inner)
            { Phase = phase; Code = code; }
        }

        private static class NativePipe
        {
            internal static Dictionary<string, object> Exchange(Dictionary<string, object> request, int timeout)
            {
                string name = "StreletzMasterBridgeV1-" + Process.GetCurrentProcess().SessionId;
                Stopwatch deadline = Stopwatch.StartNew();
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    try { pipe.Connect(Math.Min(timeout, 1000)); }
                    catch (TimeoutException error) { throw new NativeTransportException("connect", "native_pipe_connect_timeout", error); }
                    catch (UnauthorizedAccessException error) { throw new NativeTransportException("connect", "native_access_denied", error); }
                    catch (IOException error) { throw new NativeTransportException("connect", "native_io_error", error); }
                    byte[] payload = new UTF8Encoding(false, true).GetBytes(BridgeJson.Serialize(request) + "\n");
                    try
                    {
                        Task<string> pending = Transfer(pipe, payload);
                        int remaining = Math.Max(1, timeout - (int)deadline.ElapsedMilliseconds);
                        if (!pending.Wait(remaining))
                        {
                            pipe.Dispose();
                            pending.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            throw new NativeTransportException("response", "native_response_timeout", null);
                        }
                        string received = pending.GetAwaiter().GetResult();
                        try { return BridgeJson.ParseObject(received); }
                        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is FormatException)
                        { throw new NativeTransportException("json", "native_json_invalid", error); }
                    }
                    catch (AggregateException error)
                    {
                        Exception cause = DiagnosticCause(error);
                        if (cause is NativeTransportException) throw cause;
                        throw new NativeTransportException("response", DiagnosticCode(cause), cause);
                    }
                    finally { Array.Clear(payload, 0, payload.Length); }
                }
            }

            private static async Task<string> Transfer(Stream pipe, byte[] request)
            {
                await pipe.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
                using (var response = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    for (;;)
                    {
                        int count = await pipe.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (count == 0) throw new NativeTransportException("response", "native_response_closed", null);
                        int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                        int append = newline < 0 ? count : newline;
                        if (response.Length + append > 4 * 1024 * 1024) throw new NativeTransportException("response", "native_response_too_large", null);
                        response.Write(buffer, 0, append);
                        if (newline >= 0) return new UTF8Encoding(false, true).GetString(response.ToArray()).TrimEnd('\r');
                    }
                }
            }
        }
    }
}
