// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreletzBridge
{
    public interface IPanelRegistrationDriver
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target,
            CancellationToken cancellation, Action<RegistrationDriverProgress> progress);
    }

    // A separate interface keeps recovery read-only and makes it impossible to
    // accidentally reuse Register (which sends the native start command).
    public interface IPanelRegistrationRecoveryDriver
    {
        RegistrationRecoveryResult Recover(PanelProfile profile, RegistrationTarget target);
    }

    public sealed class RegistrationRecoveryResult
    {
        public string Outcome { get; set; }
        public string Message { get; set; }
        public bool ManualReviewAllowed { get; set; }
        public RegistrationDriverResult Result { get; set; }
    }

    public sealed class RegistrationTarget
    {
        public string OperationId { get; set; }
        public string ProjectFingerprint { get; set; }
        public string RecordId { get; set; }
        public string ExpectedHardwareId { get; set; }
        public string DeviceKey { get; set; }
        public int TimeoutSeconds { get; set; }
        public Dictionary<string, object> RecordSnapshot { get; set; }
    }

    public sealed class RegistrationDriverProgress
    {
        public string State { get; set; }
        public string Message { get; set; }
        public RegistrationDriverProgress() { }
        public RegistrationDriverProgress(string state, string message) { State = state; Message = message; }
    }

    public sealed class RegistrationDriverResult
    {
        public string State { get; set; }
        public bool RegistrationConfirmed { get; set; }
        public bool StopConfirmed { get; set; }
        public string HardwareId { get; set; }
        public string Message { get; set; }
        public string Evidence { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorPhase { get; set; }
        public Dictionary<string, object> DiagnosticDetails { get; set; }
    }

    public sealed class UnavailablePanelRegistrationDriver : IPanelRegistrationDriver
    {
        public bool IsAvailable { get { return false; } }
        public string UnavailableReason { get { return "Не подключён модуль регистрации Стрелец-Мастера."; } }
        public RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target, CancellationToken cancellation, Action<RegistrationDriverProgress> progress)
        {
            return new RegistrationDriverResult { State = "failed", Message = UnavailableReason, StopConfirmed = true };
        }
    }

    internal sealed class RegistrationRequestException : Exception
    {
        public readonly string Code;
        public RegistrationRequestException(string code, string message) : base(message) { Code = code; }
    }

    internal sealed class RegistrationCoordinator : IDisposable
    {
        private sealed class Operation
        {
            public string Id, PanelId, PanelName, ModuleId, ParametersHash;
            public RegistrationTarget Target;
            public string State = "connecting", Message = "Подключение к Стрелец-Мастеру", ConfirmedHardwareId, Evidence;
            public string ErrorCode, ErrorPhase;
            public Dictionary<string, object> DiagnosticDetails;
            public bool Terminal, RegistrationConfirmed, ProjectSaved, WorkerRunning, RecoveredReadOnly;
            public long StartedAt, UpdatedAt, ExpiresAt, CancelRequestedAt;
            public bool TimeoutCancellation;
            public CancellationTokenSource Cancellation;
        }

        private readonly object gate = new object();
        private readonly PanelProfileStore profiles;
        private readonly Func<Dictionary<string, object>> getProject;
        private readonly Func<RegistrationTarget, RegistrationDriverResult, bool> saveConfirmed;
        private readonly IPanelRegistrationDriver driver;
        private readonly string journalPath;
        private readonly byte[] identityKey;
        private readonly Func<long> clock;
        private readonly Func<string, Dictionary<string, object>[], string, bool> savePanelCatalog;
        private readonly Dictionary<string, Operation> operations = new Dictionary<string, Operation>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer watchdog;
        private bool disposed;
        private bool recoveryRunning;
        private string journalDigest;
        private bool journalMissingAtStartup;
        private int recoveryProbeCount, missingRecoveryCount, restoredRecoveryCount;
        private string lastRecoveryOutcome = "не выполнялась";
        private string journalStorageDiagnostic = "";
        private string catalogStorageDiagnostic = "";
        private string projectStorageDiagnostic = "";
        private string journalStorageProblem, catalogStorageProblem, projectStorageProblem;
        private string displayFingerprint = "";
        private long projectRevision;
        private ProjectSyncSnapshot.Prepared preparedProject;
        private Dictionary<string, object>[] displayCatalog = new Dictionary<string, object>[0];
        public event Action Changed;
        public string LastStorageProblem { get { return journalStorageProblem ?? projectStorageProblem ?? catalogStorageProblem; } }
        public string LastPanelCatalogProblem { get; private set; }

        public Dictionary<string, object>[] DisplayPanels()
        {
            lock (gate)
            {
                var project = getProject();
                if (project == null) return new Dictionary<string, object>[0];
                return displayFingerprint == BridgeValues.ProjectFingerprint(project)
                    ? displayCatalog.Select(BridgeValues.Copy).ToArray() : ProjectPanelCatalog.Cached(project);
            }
        }

        public string ConnectionDiagnosticReport
        {
            get
            {
                var diagnostics = driver as IConnectionDiagnostics;
                string connection = diagnostics == null ? "Диагностика соединения недоступна для этого драйвера.\r\n" : diagnostics.LastDiagnosticReport;
                lock (gate)
                {
                    Operation operation = operations.Values.Where(o => o.Terminal && !String.IsNullOrEmpty(o.ErrorCode))
                        .OrderByDescending(o => o.UpdatedAt).FirstOrDefault();
                    var report = new StringBuilder(connection);
                    report.AppendLine();
                    report.AppendLine("Стрелец — журнал Windows-моста 0.5.10");
                    report.AppendLine("Хранилище: профиль текущего пользователя Windows / StreletzBridge");
                    report.AppendLine("Файл журнала: registration-journal.json");
                    report.AppendLine("Журнал отсутствовал при запуске: " + (journalMissingAtStartup ? "да" : "нет"));
                    report.AppendLine("Журнал существует сейчас: " + (File.Exists(journalPath) ? "да" : "нет"));
                    report.AppendLine("Резервная копия предыдущего журнала существует: " + (File.Exists(journalPath + ".bak") ? "да" : "нет"));
                    report.AppendLine("Операций в журнале: " + operations.Count);
                    report.AppendLine("Выполняющихся операций: " + operations.Values.Count(o => o.WorkerRunning));
                    report.AppendLine("С неизвестным результатом: " + operations.Values.Count(o => o.State == "unknown"));
                    report.AppendLine("Восстановленных только чтением: " + operations.Values.Count(o => o.RecoveredReadOnly));
                    report.AppendLine("Проверок прежних операций в этом запуске: " + recoveryProbeCount);
                    report.AppendLine("Отсутствующих локально при проверке: " + missingRecoveryCount);
                    report.AppendLine("Восстановленных в этом запуске: " + restoredRecoveryCount);
                    report.AppendLine("Последняя сверка: " + lastRecoveryOutcome);
                    if (journalStorageDiagnostic.Length > 0) report.AppendLine("Диагностика записи журнала: " + journalStorageDiagnostic);
                    if (catalogStorageDiagnostic.Length > 0) report.AppendLine("Диагностика сохранения каталога: " + catalogStorageDiagnostic);
                    if (projectStorageDiagnostic.Length > 0) report.AppendLine("Диагностика сохранения подтверждённого результата: " + projectStorageDiagnostic);
                    report.AppendLine("Сверка читает прежний результат; start и cancel не отправляются. Отсутствие записи не доказывает отсутствие регистрации.");
                    if (operation == null) return report.ToString();
                    report.AppendLine();
                    report.AppendLine("Стрелец — последняя сохранённая операция с диагностикой");
                    report.AppendLine("Windows-мост: 0.5.10");
                    report.AppendLine("Состояние: " + (operation.State == "failed" ? "отказ без подтверждения регистрации" : "результат не установлен"));
                    try { report.AppendLine("Время результата UTC: " + DateTimeOffset.FromUnixTimeMilliseconds(operation.UpdatedAt).ToString("O", System.Globalization.CultureInfo.InvariantCulture)); }
                    catch (ArgumentOutOfRangeException) { report.AppendLine("Время результата: не подтверждено"); }
                    report.AppendLine("Операция: " + operation.Id);
                    report.AppendLine("GUID записи: " + operation.Target.RecordId);
                    report.AppendLine("Панель моста: " + operation.PanelId);
                    report.AppendLine("Код: " + RegistrationDiagnosticData.Code(operation.ErrorCode));
                    report.AppendLine("Этап: " + RegistrationDiagnosticData.Phase(operation.ErrorPhase));
                    foreach (var field in RegistrationDiagnosticData.Details(operation.DiagnosticDetails).OrderBy(x => x.Key, StringComparer.Ordinal))
                        report.AppendLine(field.Key + ": " + Convert.ToString(field.Value, System.Globalization.CultureInfo.InvariantCulture));
                    report.AppendLine("Это сохранённый результат указанной операции; проверка связи его не заменяет.");
                    report.AppendLine("Сохранение отчёта не запускает регистрацию. Пароли, PIN, KEY и полные строки подключения не включены.");
                    return report.ToString();
                }
            }
        }

        public void SaveConnectionDiagnostics(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Выберите файл диагностического отчёта.", "path");
            BridgeFiles.WriteAtomic(path, ConnectionDiagnosticReport);
        }

        public RegistrationCoordinator(PanelProfileStore profiles, Func<Dictionary<string, object>> getProject,
            Func<RegistrationTarget, RegistrationDriverResult, bool> saveConfirmed, IPanelRegistrationDriver driver,
            string journalPath, byte[] identityKey, Func<long> clock = null,
            Func<string, Dictionary<string, object>[], string, bool> savePanelCatalog = null)
        {
            this.profiles = profiles; this.getProject = getProject; this.saveConfirmed = saveConfirmed;
            this.driver = driver ?? new UnavailablePanelRegistrationDriver(); this.journalPath = journalPath;
            this.identityKey = (byte[])identityKey.Clone(); this.clock = clock ?? BridgeValues.NowMs;
            this.savePanelCatalog = savePanelCatalog;
            LoadJournal();
            watchdog = new Timer(delegate { CheckDeadlines(); }, null, 500, 500);
        }

        public bool HasActiveOperations { get { lock (gate) return operations.Values.Any(o => o.WorkerRunning); } }

        public string Summary
        {
            get
            {
                lock (gate)
                {
                    Operation value = operations.Values.OrderByDescending(o => o.UpdatedAt).FirstOrDefault();
                    return value == null ? "Регистрация с телефона: операций пока нет"
                        : value.PanelName + " · " + value.Target.ExpectedHardwareId + " · " + value.Message;
                }
            }
        }

        public void RunPanelMutation(string panelId, Action action)
        {
            lock (gate)
            {
                if (recoveryRunning || (!String.IsNullOrEmpty(panelId) && operations.Values.Any(o => o.WorkerRunning && o.PanelId == panelId)))
                    throw new InvalidOperationException("Сначала завершите регистрацию на этой панели.");
                action();
            }
            Notify();
        }

        public void RunProjectMutation(Action action)
        {
            RunProjectMutation(-1, action);
        }

        public Dictionary<string, object> ReadProjectSnapshot(out long revision)
        {
            lock (gate) { revision = projectRevision; return getProject(); }
        }

        public void RunProjectMutation(long expectedRevision, Action action)
        {
            lock (gate)
            {
                if (expectedRevision >= 0 && projectRevision != expectedRevision)
                    throw new InvalidOperationException("Проект изменился во время чтения BFX. Повторите чтение для текущего проекта.");
                if (recoveryRunning || operations.Values.Any(o => o.WorkerRunning))
                    throw new InvalidOperationException("Сначала завершите регистрацию или сверку прежней операции; проект используется активной операцией.");
                try { action(); }
                finally
                {
                    // A failing writer may already have changed in-memory metadata.
                    projectRevision++;
                    preparedProject = null;
                    displayFingerprint = "";
                    displayCatalog = new Dictionary<string, object>[0];
                }
            }
        }

        public Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            string requestId = BridgeValues.Text(request, "requestId");
            try
            {
                requestId = BridgeValues.GuidValue(requestId, "запрос");
                if (BridgeValues.Number(request, "protocol") != 3) throw new RegistrationRequestException("unsupported_protocol", "Требуется защищённый протокол версии 3.");
                long sentAt = BridgeValues.Number(request, "sentAt", -1);
                long now = clock();
                if (sentAt < now - 300000L || sentAt > now + 300000L)
                    throw new RegistrationRequestException("stale_request", "Проверьте время на телефоне и компьютере: срок запроса истёк.");
                string type = BridgeValues.Text(request, "type");
                lock (gate) if (disposed) throw new RegistrationRequestException("bridge_stopping", "Windows-мост останавливается.");
                if (type == "get_project")
                {
                    ProjectSyncSnapshot.Prepared snapshot;
                    lock (gate)
                    {
                        // Native completion saves outside this gate. Never retain a snapshot
                        // while a worker/recovery can change the project concurrently.
                        bool writing = recoveryRunning || operations.Values.Any(o => o.WorkerRunning);
                        snapshot = !writing ? preparedProject : null;
                        if (snapshot == null) snapshot = new ProjectSyncSnapshot.Prepared(getProject());
                        preparedProject = writing ? null : snapshot;
                    }
                    return snapshot.Reply(requestId,
                        BridgeValues.Text(request, "knownProjectFingerprint"), BridgeValues.Text(request, "knownProjectRevision"));
                }
                if (type == "list_panels") return ListPanels(request, requestId);
                if (type == "check_connection") return ListPanels(request, requestId, true);
                if (type == "begin_registration") return Begin(request, requestId);
                if (type == "recover_registration") return Recover(request, requestId);
                if (type == "registration_status") return Status(request, requestId, false);
                if (type == "cancel_registration") return Status(request, requestId, true);
                throw new RegistrationRequestException("unsupported_command", "Неподдерживаемая команда телефона.");
            }
            catch (Exception error)
            {
                RegistrationRequestException known = error as RegistrationRequestException;
                string code = known != null ? known.Code : error is InvalidDataException ? "invalid_request" : "bridge_error";
                string message = known != null || error is InvalidDataException || error is InvalidOperationException
                    ? error.Message : "Windows-мост не смог обработать запрос.";
                Dictionary<string, object> response = ProtocolV3.Response(requestId, false, message, code);
                bool isBegin = BridgeValues.Text(request, "type") == "begin_registration";
                string opId = isBegin ? requestId : BridgeValues.Text(request, "operationId");
                Guid parsedOperation;
                if (Guid.TryParseExact(opId, "D", out parsedOperation) && parsedOperation != Guid.Empty)
                    response["operationId"] = parsedOperation.ToString("D");
                lock (gate)
                {
                    Operation existing;
                    if (BridgeValues.Text(request, "type") != "recover_registration" && operations.TryGetValue(opId, out existing))
                    {
                        response["accepted"] = true;
                        response["operation"] = Public(existing);
                    }
                    else if (isBegin && IsProvenPreDispatchRejection(code)) response["accepted"] = false;
                }
                return response;
            }
        }

        private Dictionary<string, object> LegacyPanels(string requestId)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            bool available = driver.IsAvailable;
            string reason = available ? "" : driver.UnavailableReason;
            foreach (PanelProfile profile in profiles.List())
            {
                Dictionary<string, object> item = new Dictionary<string, object> {
                    { "panelId", profile.Id }, { "name", profile.Name }, { "host", profile.Host },
                    { "port", profile.Port }, { "moduleId", profile.ModuleId }, { "available", available }, { "availableReason", reason }
                };
                lock (gate)
                {
                    Operation active = operations.Values.FirstOrDefault(o => o.WorkerRunning && o.PanelId == profile.Id);
                    item["activeOperationId"] = active == null ? null : active.Id;
                }
                list.Add(item);
            }
            Dictionary<string, object> response = ProtocolV3.Response(requestId, true, list.Count == 0 ? "Добавьте панель в Windows-мосте: Панели…" : "Панели Windows-моста");
            response["panels"] = list.ToArray();
            lock (gate)
            {
                Dictionary<string, object> project = getProject();
                response["projectFingerprint"] = project == null ? "" : BridgeValues.ProjectFingerprint(project);
                response["projectName"] = BridgeValues.Text(project, "siteName");
            }
            return response;
        }

        public void EnrichProject(Dictionary<string, object> project)
        {
            var source = driver as IProjectPanelSource;
            if (source == null || project == null) return;
            try
            {
                var native = source.ReadProjectPanels();
                var verified = ProjectPanelCatalog.FromNative(native, project);
                var catalog = ProjectPanelCatalog.Merge(ProjectPanelCatalog.Cached(project), verified);
                lock (gate)
                {
                    profiles.SynchronizeProjectPanels(verified, ActivePanelIds());
                    ProjectPanelCatalog.Apply(project, catalog, NativeFingerprint(native));
                }
                LastPanelCatalogProblem = "";
            }
            catch (Exception error)
            {
                int known = ProjectPanelCatalog.Cached(project).Length;
                LastPanelCatalogProblem = (known > 0 ? "Панелей из файла проекта: " + known + ". Связь для регистрации не подтверждена. "
                    : "В переносимом проекте нет каталога панелей. Прочитайте исходный BFX кнопкой «Панели… → Прочитать BFX…». ") + error.Message;
                // A portable project can still be opened offline. Existing
                // membership stays visible, but never becomes live availability.
                ProjectPanelCatalog.Apply(project, ProjectPanelCatalog.Cached(project));
            }
        }

        private Dictionary<string, object> ListPanels(Dictionary<string, object> request, string requestId, bool readOnly = false)
        {
            var source = driver as IProjectPanelSource;
            if (source == null) return LegacyPanels(requestId);
            Dictionary<string, object> project;
            string fingerprint;
            long revision;
            string recordId = BridgeValues.Text(request, "recordId");
            if (recordId.Length > 0) recordId = BridgeValues.GuidValue(recordId, "запись проекта");
            lock (gate)
            {
                project = getProject();
                if (project == null) throw new RegistrationRequestException("project_missing", "Откройте проект в Windows-мосте.");
                fingerprint = BridgeValues.ProjectFingerprint(project);
                revision = projectRevision;
                string requested = BridgeValues.Text(request, "projectFingerprint");
                if (requested.Length > 0 && !String.Equals(requested, fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new RegistrationRequestException("project_mismatch", "На телефоне и в Windows-мосте открыты разные проекты.");
                if (recordId.Length > 0 && !(BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>()
                    .Any(row => String.Equals(BridgeValues.Text(row, "id"), recordId, StringComparison.OrdinalIgnoreCase)))
                    throw new RegistrationRequestException("record_missing", "Выбранная запись отсутствует в проекте Windows-моста.");
            }
            Dictionary<string, object>[] catalog;
            Dictionary<string, object>[] verified = new Dictionary<string, object>[0];
            bool live = false;
            string reason = "", nativeFingerprint = "", connectionErrorCode = "";
            try
            {
                var native = source.ReadProjectPanels();
                verified = ProjectPanelCatalog.FromNative(native, project);
                catalog = ProjectPanelCatalog.Merge(ProjectPanelCatalog.Cached(project), verified);
                nativeFingerprint = NativeFingerprint(native);
                live = true;
            }
            catch (Exception error)
            {
                catalog = ProjectPanelCatalog.Cached(project);
                reason = error.Message;
                connectionErrorCode = PanelRegistrationDriver.ConnectionFailureCode(error);
            }
            bool saved = false;
            lock (gate)
            {
                var current = getProject();
                if (current == null || BridgeValues.ProjectFingerprint(current) != fingerprint || projectRevision != revision)
                    throw new RegistrationRequestException("project_mismatch", "Проект Windows-моста изменился во время получения панелей.");
                project = current;
                if (live && !readOnly)
                {
                    profiles.SynchronizeProjectPanels(verified, ActivePanelIds());
                    if (savePanelCatalog != null)
                    {
                        try
                        {
                            preparedProject = null;
                            saved = savePanelCatalog(fingerprint, catalog, nativeFingerprint);
                            if (saved) projectRevision++;
                            if (saved) { catalogStorageProblem = null; catalogStorageDiagnostic = ""; }
                            else CatalogStorageFailed(null);
                        }
                        catch (Exception error) { CatalogStorageFailed(error); }
                    }
                }
                foreach (var panel in catalog)
                {
                    string id = BridgeValues.Text(panel, "panelId");
                    PanelProfile profile = null;
                    try { profile = profiles.Get(id); } catch (InvalidDataException) { }
                    bool matches = PanelProfileStore.MatchesCatalog(profile, panel)
                        && (BridgeValues.Array(panel, "verifiedRecordIds") ?? new object[0]).Length > 0;
                    panel["available"] = live && matches;
                    panel["availableReason"] = !live ? "Нет контрольной связи с проектом. " + reason
                        : matches ? "" : "Панель прочитана из файла проекта; её маршрут и принадлежность не подтверждены живым проектом Master.";
                    Operation active = operations.Values.FirstOrDefault(o => o.WorkerRunning && o.PanelId == id);
                    panel["activeOperationId"] = active == null ? null : active.Id;
                }
                if (!readOnly) {
                    displayFingerprint = fingerprint;
                    displayCatalog = catalog.Select(BridgeValues.Copy).ToArray();
                }
            }
            var matching = (recordId.Length == 0 ? catalog : catalog.Where(p => ProjectPanelCatalog.Contains(p, recordId)))
                .Select(BridgeValues.Copy).ToArray();
            if (recordId.Length > 0)
            {
                foreach (var panel in matching)
                {
                    if (matching.Length > 1 || !ProjectPanelCatalog.VerifiedContains(panel, recordId))
                    {
                        panel["available"] = false;
                        panel["availableReason"] = matching.Length > 1 ? "В проекте неоднозначная принадлежность записи. Проверьте её в Master."
                            : "Принадлежность датчика прочитана из файла, но ещё не подтверждена живым проектом Master.";
                    }
                }
            }
            string message = live
                ? catalog.Length == 0 ? "Для записей проекта не найдены настроенные маршруты панелей МК-IP." : "Панели загружены из проекта Стрелец-Мастера."
                : catalog.Length > 0 ? "Панелей из файла проекта: " + catalog.Length + ". Связь для регистрации не подтверждена. " + reason
                : "В переносимом проекте нет каталога панелей. Прочитайте исходный BFX через «Панели… → Прочитать BFX…». " + reason;
            var response = ProtocolV3.Response(requestId, true, message);
            response["panelMappingVersion"] = 1;
            response["catalogSource"] = live ? "native_project" : "cached_project";
            response["catalogMessage"] = message;
            response["catalogComplete"] = ProjectPanelCatalog.IsComplete(project);
            response["nativeAvailable"] = live;
            response["nativeReason"] = reason;
            if (readOnly) response["connectionErrorCode"] = connectionErrorCode;
            response["projectPanels"] = catalog;
            response["panels"] = matching;
            response["projectFingerprint"] = fingerprint;
            response["projectName"] = BridgeValues.Text(project, "siteName");
            response["recordMetadata"] = ProjectPanelCatalog.RecordMetadata(project);
            response["projectSaved"] = saved;
            if (recordId.Length > 0)
            {
                var record = (BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>()
                    .First(row => String.Equals(BridgeValues.Text(row, "id"), recordId, StringComparison.OrdinalIgnoreCase));
                response["record"] = new Dictionary<string, object> {
                    { "recordId", recordId }, { "description", ProjectPanelCatalog.EffectiveDescription(record) },
                    { "projectPanelId", matching.Length == 1 ? BridgeValues.Text(matching[0], "projectPanelId") : "" },
                    { "panelIds", matching.Select(p => BridgeValues.Text(p, "panelId")).ToArray() },
                    { "resolution", matching.Length == 1 ? "unique" : matching.Length == 0 ? "missing" : "ambiguous" }
                };
            }
            LastPanelCatalogProblem = live ? "" : message;
            Notify();
            return response;
        }

        private HashSet<string> ActivePanelIds()
        {
            return new HashSet<string>(operations.Values.Where(o => o.WorkerRunning).Select(o => o.PanelId), StringComparer.OrdinalIgnoreCase);
        }

        private static string NativeFingerprint(Dictionary<string, object> native)
        {
            string value = BridgeValues.Text(native, "nativeProjectFingerprint");
            return value.Length > 0 ? value : BridgeValues.Text(native, "projectFingerprint");
        }

        private Dictionary<string, object> Begin(Dictionary<string, object> request, string requestId)
        {
            string panelId = BridgeValues.GuidValue(BridgeValues.Text(request, "panelId"), "выбранная панель");
            string recordId = BridgeValues.GuidValue(BridgeValues.Text(request, "recordId"), "запись проекта");
            string fingerprint = BridgeValues.Text(request, "projectFingerprint").Trim().ToLowerInvariant();
            string hardware = BridgeValues.Text(request, "expectedHardwareId").Trim().ToUpperInvariant();
            string key = BridgeValues.Text(request, "deviceKey").Trim().ToUpperInvariant();
            long timeoutValue = BridgeValues.Number(request, "timeoutSeconds", 90);
            if (!Regex.IsMatch(fingerprint, "^[0-9a-f]{64}$")) throw new InvalidDataException("Некорректный отпечаток проекта.");
            if (!Regex.IsMatch(hardware, "^[0-9A-F]{12}$")) throw new InvalidDataException("ID датчика должен содержать 12 шестнадцатеричных символов.");
            if (key.Length > 0 && !Regex.IsMatch(key, "^[0-9A-F]{8}$")) throw new InvalidDataException("KEY датчика должен содержать ровно 8 шестнадцатеричных символов либо быть пустым для использования ключа проекта.");
            if (timeoutValue < 30 || timeoutValue > 120) throw new InvalidDataException("Время регистрации должно быть от 30 до 120 секунд.");
            int timeout = (int)timeoutValue;
            string parameters = ParametersHash(panelId, fingerprint, recordId, hardware, key, timeout);
            Operation operation;
            PanelProfile panel;
            Dictionary<string, object> response;
            lock (gate)
            {
                if (operations.TryGetValue(requestId, out operation))
                {
                    if (operation.RecoveredReadOnly) throw new RegistrationRequestException("recovered_operation_read_only", "Прежняя операция восстановлена только для чтения результата. Её идентификатор нельзя повторно использовать для запуска регистрации.");
                    if (operation.ParametersHash != parameters) throw new RegistrationRequestException("request_conflict", "Этот идентификатор запроса уже использован с другими параметрами.");
                    return WithOperation(requestId, operation, true, "Состояние ранее принятой операции");
                }
                if (recoveryRunning) throw new RegistrationRequestException("recovery_busy", "Выполняется сверка прежней операции. Дождитесь её результата перед новым заданием.");
                if (!driver.IsAvailable) throw new RegistrationRequestException("driver_unavailable", driver.UnavailableReason);
                panel = profiles.Get(panelId);
                if (operations.Values.Any(o => o.WorkerRunning && (o.PanelId == panelId || o.ModuleId == panel.ModuleId)))
                    throw new RegistrationRequestException("panel_busy", "На выбранной панели уже выполняется регистрация.");
                if (operations.Values.Any(o => o.WorkerRunning && ((o.Target.ProjectFingerprint == fingerprint && o.Target.RecordId == recordId) || o.Target.ExpectedHardwareId == hardware)))
                    throw new RegistrationRequestException("device_busy", "Для этого устройства уже выполняется регистрация.");
                Dictionary<string, object> project = getProject();
                if (project == null) throw new RegistrationRequestException("project_missing", "Откройте проект в Windows-мосте.");
                if (BridgeValues.ProjectFingerprint(project) != fingerprint)
                    throw new RegistrationRequestException("project_mismatch", "На телефоне и в Windows-мосте открыты разные проекты.");
                Dictionary<string, object> selected = null;
                foreach (object value in BridgeValues.Array(project, "records"))
                {
                    Dictionary<string, object> row = value as Dictionary<string, object>;
                    Dictionary<string, object> commissioning = BridgeValues.Object(row, "commissioning");
                    if (String.Equals(BridgeValues.Text(row, "id"), recordId, StringComparison.OrdinalIgnoreCase)) selected = row;
                    else if (String.Equals(BridgeValues.Text(commissioning, "serial"), hardware, StringComparison.OrdinalIgnoreCase))
                        throw new RegistrationRequestException("duplicate_hardware_id", "Этот аппаратный ID уже назначен другому устройству проекта.");
                }
                if (selected == null || BridgeValues.Text(selected, "recordKind") != "device") throw new RegistrationRequestException("record_missing", "Выбранное устройство не найдено в проекте Windows-моста.");
                Dictionary<string, object> current = BridgeValues.Object(selected, "commissioning");
                int stage = current == null ? (BridgeValues.Text(selected, "initialized") == "+" ? 2 : 0) : (int)BridgeValues.Number(current, "stage");
                if (stage >= 2) throw new RegistrationRequestException("already_initialized", "Устройство уже отмечено как инициализированное. Повторная привязка требует отдельной операции в Стрелец-Мастере.");
                long now = clock();
                operation = new Operation {
                    Id = requestId, PanelId = panelId, PanelName = panel.Name, ModuleId = panel.ModuleId, ParametersHash = parameters,
                    StartedAt = now, UpdatedAt = now, ExpiresAt = now + timeout * 1000L, WorkerRunning = true,
                    Cancellation = new CancellationTokenSource(),
                    Target = new RegistrationTarget { OperationId = requestId, ProjectFingerprint = fingerprint, RecordId = recordId,
                        ExpectedHardwareId = hardware, DeviceKey = key, TimeoutSeconds = timeout, RecordSnapshot = BridgeValues.Copy(selected) }
                };
                operations.Add(operation.Id, operation);
                try { PersistLocked(); }
                catch (Exception error)
                {
                    operations.Remove(operation.Id); operation.Cancellation.Dispose();
                    JournalStorageFailed(error);
                    throw new RegistrationRequestException("journal_unavailable", "Не удалось сохранить журнал операции. Регистрация не запускалась.");
                }
                response = WithOperation(requestId, operation, true, "Задание принято; ожидайте состояние панели");
                Thread worker = new Thread(new ThreadStart(delegate { Run(operation, panel); })) { IsBackground = true, Name = "Streletz registration " + operation.Id };
                worker.Start();
            }
            Notify();
            return response;
        }

        private Dictionary<string, object> Recover(Dictionary<string, object> request, string requestId)
        {
            string panelId = BridgeValues.GuidValue(BridgeValues.Text(request, "panelId"), "панель прежней операции");
            var target = new RegistrationTarget {
                OperationId = BridgeValues.GuidValue(BridgeValues.Text(request, "operationId"), "прежняя операция"),
                ProjectFingerprint = BridgeValues.Text(request, "projectFingerprint").Trim().ToLowerInvariant(),
                RecordId = BridgeValues.GuidValue(BridgeValues.Text(request, "recordId"), "запись прежней операции"),
                ExpectedHardwareId = BridgeValues.Text(request, "expectedHardwareId").Trim().ToUpperInvariant(), TimeoutSeconds = 90
            };
            if (!Regex.IsMatch(target.ProjectFingerprint, "^[0-9a-f]{64}$") || !Regex.IsMatch(target.ExpectedHardwareId, "^[0-9A-F]{12}$"))
                throw new InvalidDataException("Некорректный отпечаток проекта или полный ID прежней операции.");
            PanelProfile panel;
            Operation previous;
            long revision;
            lock (gate)
            {
                panel = ValidateRecoveryContextLocked(panelId, target);
                if (operations.TryGetValue(target.OperationId, out previous) && !SameRecoveryIdentity(previous, panelId, target))
                    throw new RegistrationRequestException("recovery_identity_mismatch", "Идентификатор прежней операции относится к другой записи, панели, проекту или аппаратному ID. Сверка остановлена.");
                if (previous != null && previous.WorkerRunning)
                    return RecoveryReply(requestId, panelId, target, "native_active", "Windows-мост ещё выполняет эту операцию. Дождитесь завершения или используйте отдельную отмену регистрации.", false, null);
                if (previous != null && IsDefinitiveOperation(previous))
                {
                    if (previous.RegistrationConfirmed && !previous.ProjectSaved)
                    {
                        if (recoveryRunning || operations.Values.Any(o => o.WorkerRunning))
                            throw new RegistrationRequestException("recovery_busy", "Дождитесь завершения текущей операции перед сохранением прежнего результата.");
                        EnsureNoOtherHardwareLocked(target);
                        // The trusted physical result already exists. Revalidate
                        // its durable journal before a separate local save retry.
                        try { PersistLocked(); }
                        catch (Exception error)
                        {
                            JournalStorageFailed(error);
                            throw new RegistrationRequestException("recovery_journal_unavailable", "Подтверждённый итог сохранён в памяти, но журнал сейчас недоступен. Проверьте хранение перед повторным сохранением проекта.");
                        }
                        SaveConfirmedProjectLocked(previous, target, new RegistrationDriverResult {
                            State = "registered", RegistrationConfirmed = true, HardwareId = previous.ConfirmedHardwareId,
                            Evidence = previous.Evidence, Message = previous.Message
                        });
                        Notify();
                    }
                    return RecoveryReply(requestId, panelId, target, "restored", previous.Message, false, previous);
                }
                if (recoveryRunning || operations.Values.Any(o => o.WorkerRunning))
                    throw new RegistrationRequestException("recovery_busy", "Дождитесь завершения текущей регистрации или сверки перед проверкой прежней операции.");
                if (!(driver is IPanelRegistrationRecoveryDriver))
                    throw new RegistrationRequestException("recovery_unavailable", "Этот модуль не поддерживает безопасную сверку прежней операции. Обновите Windows-мост.");
                recoveryRunning = true;
                recoveryProbeCount++;
                if (previous == null) missingRecoveryCount++;
                lastRecoveryOutcome = "проверка выполняется";
                revision = projectRevision;
            }
            try
            {
                // This dedicated interface has no start/cancel arguments. It
                // reads only the retained result for the original operation ID.
                RegistrationRecoveryResult probe = ((IPanelRegistrationRecoveryDriver)driver).Recover(panel, target);
                lock (gate)
                {
                    if (disposed) throw new RegistrationRequestException("bridge_stopping", "Windows-мост останавливается; сверка не завершена.");
                    PanelProfile current = ValidateRecoveryContextLocked(panelId, target);
                    if (projectRevision != revision || !SameProfileRoute(panel, current))
                        throw new RegistrationRequestException("recovery_context_changed", "Проект или маршрут панели изменился во время сверки. Повторите проверку прежней операции.");
                    if (probe == null || (probe.Outcome != "restored" && probe.Outcome != "native_active" && probe.Outcome != "manual_review_required") ||
                        probe.ManualReviewAllowed != (probe.Outcome == "manual_review_required") ||
                        (probe.Outcome != "restored" && probe.Result != null))
                        throw new RegistrationRequestException("recovery_invalid_result", "Модуль не предоставил однозначный результат безопасной сверки.");
                    if (probe.Outcome != "restored")
                    {
                        lastRecoveryOutcome = probe.Outcome;
                        return RecoveryReply(requestId, panelId, target, probe.Outcome, probe.Message, probe.ManualReviewAllowed, null);
                    }
                    RegistrationDriverResult result = probe.Result;
                    bool registered = IsConfirmedResult(result, target);
                    if (result == null || (!registered && !((result.State == "cancelled" || result.State == "timed_out" || result.State == "failed")
                        && result.StopConfirmed && !result.RegistrationConfirmed)))
                        throw new RegistrationRequestException("recovery_evidence_missing", "Прежний результат не содержит полного подтверждения регистрации или остановки. Карточка остаётся заблокированной.");
                    if (registered) EnsureNoOtherHardwareLocked(target);
                    long now = clock();
                    var restored = new Operation {
                        Id = target.OperationId, PanelId = panelId, PanelName = panel.Name, ModuleId = panel.ModuleId,
                        ParametersHash = ParametersHash(panelId, target.ProjectFingerprint, target.RecordId, target.ExpectedHardwareId, "read-only-recovery", 0),
                        RecoveredReadOnly = true, Target = target, Terminal = true, WorkerRunning = false,
                        StartedAt = previous == null ? now : previous.StartedAt, UpdatedAt = now,
                        ExpiresAt = previous == null ? now : previous.ExpiresAt,
                        State = registered ? "registered" : result.State, RegistrationConfirmed = registered,
                        ConfirmedHardwareId = registered ? target.ExpectedHardwareId : null,
                        Evidence = registered ? Limited(result.Evidence, 3000) : null,
                        ErrorCode = registered ? "" : RegistrationDiagnosticData.Code(result.ErrorCode),
                        ErrorPhase = registered ? "" : RegistrationDiagnosticData.Phase(result.ErrorPhase),
                        DiagnosticDetails = registered ? new Dictionary<string, object>() : RegistrationDiagnosticData.Details(result.DiagnosticDetails),
                        Message = registered ? "Прежняя регистрация подтверждена сохранённым результатом Стрелец-Мастера; запись в проект ещё не подтверждена."
                            : Limited(result.Message, 1500)
                    };
                    if (String.IsNullOrWhiteSpace(restored.Message)) restored.Message = "Сохранённый итог прежней операции восстановлен без повторного запуска.";
                    operations[target.OperationId] = restored;
                    try { PersistLocked(); }
                    catch (Exception error)
                    {
                        if (previous == null) operations.Remove(target.OperationId); else operations[target.OperationId] = previous;
                        JournalStorageFailed(error);
                        throw new RegistrationRequestException("recovery_journal_unavailable", "Прежний результат получен, но журнал Windows-моста сохранить не удалось. Повторный запуск не выполнялся; сохраните диагностику и повторите сверку после исправления хранения.");
                    }
                    // Persist the definitive native result before attempting to
                    // update a portable project. A save failure cannot erase or
                    // turn physical confirmation into a retryable failure.
                    if (registered) SaveConfirmedProjectLocked(restored, target, result);
                    restoredRecoveryCount++;
                    lastRecoveryOutcome = "restored";
                    return RecoveryReply(requestId, panelId, target, "restored", restored.Message, false, restored);
                }
            }
            catch
            {
                lock (gate) lastRecoveryOutcome = "ошибка; ручное завершение не разрешено";
                throw;
            }
            finally
            {
                lock (gate) recoveryRunning = false;
                Notify();
            }
        }

        private void EnsureNoOtherHardwareLocked(RegistrationTarget target)
        {
            var project = getProject();
            foreach (var row in (BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>())
            {
                string assigned = BridgeValues.Text(BridgeValues.Object(row, "commissioning"), "serial");
                if (!String.Equals(BridgeValues.Text(row, "id"), target.RecordId, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(assigned, target.ExpectedHardwareId, StringComparison.OrdinalIgnoreCase))
                    throw new RegistrationRequestException("recovery_record_changed", "Ожидаемый аппаратный ID указан у другой записи проекта. Сначала выполните сверку в Стрелец-Мастере.");
            }
        }

        private void SaveConfirmedProjectLocked(Operation operation, RegistrationTarget target, RegistrationDriverResult result)
        {
            if (!IsConfirmedResult(result, target))
                throw new RegistrationRequestException("recovery_evidence_missing", "Подтверждённый результат не соответствует записи проекта; сохранение не выполнено.");
            bool saved = false, threw = false;
            preparedProject = null;
            try { saved = saveConfirmed != null && saveConfirmed(target, result); }
            catch (Exception error) { threw = true; ProjectStorageFailed(error); }
            if (saved) { projectStorageProblem = null; projectStorageDiagnostic = ""; projectRevision++; }
            else if (!threw) ProjectStorageFailed(null);
            operation.ProjectSaved = saved;
            operation.UpdatedAt = clock();
            operation.Message = saved ? "Прежняя регистрация подтверждена; результат восстановлен и сохранён в проекте."
                : "Прежняя регистрация подтверждена, но сохранение проекта Windows-моста не подтверждено. Сохраните копию, откройте её и повторите сохранение результата.";
            try { PersistLocked(); }
            catch (Exception error)
            {
                operation.ProjectSaved = false;
                operation.Message = "Прежняя регистрация подтверждена и записана в журнал. Подтверждение сохранения проекта записать не удалось; сохраните диагностику и сверьте копию проекта.";
                JournalStorageFailed(error);
            }
        }

        private PanelProfile ValidateRecoveryContextLocked(string panelId, RegistrationTarget target)
        {
            var project = getProject();
            if (project == null) throw new RegistrationRequestException("project_missing", "Откройте исходный переносимый проект в Windows-мосте для сверки прежней операции.");
            if (!String.Equals(BridgeValues.ProjectFingerprint(project), target.ProjectFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new RegistrationRequestException("project_mismatch", "На телефоне и в Windows-мосте открыты разные проекты; прежняя операция не проверялась.");
            var record = (BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>()
                .SingleOrDefault(row => String.Equals(BridgeValues.Text(row, "id"), target.RecordId, StringComparison.OrdinalIgnoreCase));
            if (record == null || BridgeValues.Text(record, "recordKind") != "device")
                throw new RegistrationRequestException("record_missing", "Запись прежней операции отсутствует в текущем проекте Windows-моста.");
            var commissioning = BridgeValues.Object(record, "commissioning");
            string assigned = BridgeValues.Text(commissioning, "serial").Trim();
            if (BridgeValues.Number(commissioning, "stage") >= 2 && assigned.Length > 0 &&
                !String.Equals(assigned, target.ExpectedHardwareId, StringComparison.OrdinalIgnoreCase))
                throw new RegistrationRequestException("recovery_record_changed", "У записи проекта уже указан другой зарегистрированный аппаратный ID. Сверьте исходную операцию в Стрелец-Мастере.");
            PanelProfile profile = profiles.Get(panelId);
            var owners = ProjectPanelCatalog.Cached(project).Where(p => ProjectPanelCatalog.Contains(p, target.RecordId)).ToArray();
            if (profile.Source == "project" && owners.Length > 0 && (owners.Length != 1 || !PanelProfileStore.MatchesCatalog(profile, owners[0])))
                throw new RegistrationRequestException("recovery_context_mismatch", "Сохранённая принадлежность записи и выбранный маршрут панели не совпадают. Сначала сверьте проект и обновите каталог панелей.");
            target.RecordSnapshot = BridgeValues.Copy(record);
            return profile;
        }

        private static bool SameProfileRoute(PanelProfile first, PanelProfile second)
        {
            return first.Id == second.Id && first.Host == second.Host && first.Port == second.Port && first.ModuleId == second.ModuleId &&
                first.NativePanelId == second.NativePanelId && first.SegmentId == second.SegmentId && first.Source == second.Source &&
                first.RouteKind == second.RouteKind && first.AdapterNodeId == second.AdapterNodeId && first.AdapterSegmentId == second.AdapterSegmentId &&
                first.TerminalPanelId == second.TerminalPanelId && first.TerminalSegmentId == second.TerminalSegmentId;
        }

        private static bool SameRecoveryIdentity(Operation operation, string panelId, RegistrationTarget target)
        {
            return String.Equals(operation.Id, target.OperationId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(operation.PanelId, panelId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(operation.Target.ProjectFingerprint, target.ProjectFingerprint, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(operation.Target.RecordId, target.RecordId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(operation.Target.ExpectedHardwareId, target.ExpectedHardwareId, StringComparison.Ordinal);
        }

        private static bool IsConfirmedResult(RegistrationDriverResult result, RegistrationTarget target)
        {
            return result != null && result.State == "registered" && result.RegistrationConfirmed &&
                String.Equals(result.HardwareId, target.ExpectedHardwareId, StringComparison.Ordinal) &&
                Regex.IsMatch(result.HardwareId ?? "", "^[0-9A-F]{12}$") && !String.IsNullOrWhiteSpace(result.Evidence);
        }

        private static bool IsDefinitiveOperation(Operation operation)
        {
            return operation.Terminal && !operation.WorkerRunning &&
                ((operation.State == "registered" && operation.RegistrationConfirmed &&
                    operation.ConfirmedHardwareId == operation.Target.ExpectedHardwareId && !String.IsNullOrWhiteSpace(operation.Evidence)) ||
                 (!operation.RegistrationConfirmed && (operation.State == "failed" || operation.State == "cancelled" || operation.State == "timed_out")));
        }

        private static Dictionary<string, object> RecoveryReply(string requestId, string panelId, RegistrationTarget target,
            string outcome, string message, bool manualReviewAllowed, Operation operation)
        {
            var response = ProtocolV3.Response(requestId, true, Limited(message, 1800));
            response["operationId"] = target.OperationId;
            response["recovery"] = new Dictionary<string, object> {
                { "operationId", target.OperationId }, { "projectFingerprint", target.ProjectFingerprint },
                { "recordId", target.RecordId }, { "expectedHardwareId", target.ExpectedHardwareId }, { "panelId", panelId },
                { "outcome", outcome }, { "message", Limited(message, 1800) }, { "manualReviewAllowed", manualReviewAllowed }
            };
            if (operation != null) { response["accepted"] = true; response["operation"] = Public(operation); }
            return response;
        }

        private Dictionary<string, object> Status(Dictionary<string, object> request, string requestId, bool cancel)
        {
            string panelId = BridgeValues.GuidValue(BridgeValues.Text(request, "panelId"), "панель");
            string operationId = BridgeValues.GuidValue(BridgeValues.Text(request, "operationId"), "операция");
            CancellationTokenSource token = null;
            Dictionary<string, object> response;
            lock (gate)
            {
                Operation operation;
                if (!operations.TryGetValue(operationId, out operation)) throw new RegistrationRequestException("operation_not_found", "Операция не найдена в журнале Windows-моста.");
                if (operation.PanelId != panelId) throw new RegistrationRequestException("panel_mismatch", "Операция относится к другой панели.");
                if (cancel && operation.WorkerRunning && !operation.Terminal && operation.CancelRequestedAt == 0)
                {
                    operation.CancelRequestedAt = clock(); operation.State = "cancelling";
                    operation.Message = "Отмена запрошена; ожидается подтверждение остановки"; operation.UpdatedAt = clock();
                    TryPersistLocked(); token = operation.Cancellation;
                }
                response = WithOperation(requestId, operation, true, operation.Message);
            }
            if (token != null) { TryCancel(token); Notify(); }
            return response;
        }

        private void Run(Operation operation, PanelProfile panel)
        {
            RegistrationDriverResult result;
            try
            {
                result = operation.Cancellation.IsCancellationRequested
                    ? new RegistrationDriverResult { State = "cancelled", StopConfirmed = true, Message = "Задание отменено до обращения к оборудованию." }
                    : driver.Register(panel, operation.Target, operation.Cancellation.Token, p => Progress(operation, p));
            }
            catch (OperationCanceledException) { result = new RegistrationDriverResult { State = "unknown", Message = "Остановка запрошена, но её результат не подтверждён." }; }
            catch { result = new RegistrationDriverResult { State = "unknown", Message = "Связь с модулем регистрации прервана; результат требует проверки в Стрелец-Мастере." }; }
            if (result == null) result = new RegistrationDriverResult { State = "unknown", Message = "Модуль регистрации не вернул результат." };
            string confirmed = (result.HardwareId ?? "").Trim().ToUpperInvariant();
            bool registered = result.State == "registered" && result.RegistrationConfirmed
                && confirmed == operation.Target.ExpectedHardwareId && Regex.IsMatch(confirmed, "^[0-9A-F]{12}$") && !String.IsNullOrWhiteSpace(result.Evidence);
            bool projectSaved = false;
            if (registered)
            {
                try
                {
                    projectSaved = saveConfirmed != null && saveConfirmed(operation.Target, result);
                    lock (gate)
                    {
                        if (projectSaved) { projectStorageProblem = null; projectStorageDiagnostic = ""; }
                        else ProjectStorageFailed(null);
                    }
                }
                catch (Exception error) { projectSaved = false; lock (gate) ProjectStorageFailed(error); }
            }
            lock (gate)
            {
                if (registered) { preparedProject = null; projectRevision++; }
                operation.WorkerRunning = false; operation.Terminal = true; operation.UpdatedAt = clock();
                operation.RegistrationConfirmed = registered; operation.ProjectSaved = projectSaved;
                operation.ConfirmedHardwareId = registered ? confirmed : null;
                operation.Evidence = registered ? Limited(result.Evidence, 3000) : null;
                operation.ErrorCode = registered ? "" : RegistrationDiagnosticData.Code(result.ErrorCode);
                operation.ErrorPhase = registered ? "" : RegistrationDiagnosticData.Phase(result.ErrorPhase);
                operation.DiagnosticDetails = registered ? new Dictionary<string, object>() : RegistrationDiagnosticData.Details(result.DiagnosticDetails);
                if (registered)
                {
                    operation.State = "registered";
                    operation.Message = projectSaved ? "Датчик зарегистрирован; результат сохранён в проекте" : "Датчик зарегистрирован, но проект Windows-моста не сохранён. Выполните сверку с BFX.";
                }
                else if (result.State == "registered" || result.RegistrationConfirmed)
                {
                    operation.State = "unknown"; operation.Message = "Получен неполный результат регистрации или другой аппаратный ID. Проверьте устройство в Стрелец-Мастере.";
                }
                else if (result.State == "cancelled" || result.State == "timed_out")
                {
                    operation.State = result.StopConfirmed ? (operation.TimeoutCancellation ? "timed_out" : result.State) : "unknown";
                    operation.Message = result.StopConfirmed ? Limited(result.Message, 1500) : "Остановка регистрации не подтверждена. Проверьте Стрелец-Мастер.";
                }
                else
                {
                    operation.State = result.State == "failed" ? "failed" : "unknown";
                    operation.Message = Limited(result.Message, 1500);
                }
                if (String.IsNullOrWhiteSpace(operation.Message)) operation.Message = "Операция завершена без подтверждения регистрации.";
                TryPersistLocked();
            }
            if (panel.PasswordMd5 != null) System.Array.Clear(panel.PasswordMd5, 0, panel.PasswordMd5.Length);
            operation.Target.DeviceKey = null;
            Notify();
        }

        private void Progress(Operation operation, RegistrationDriverProgress progress)
        {
            if (progress == null || !new[] { "connecting", "waiting_for_sensor", "detected", "registering", "cancelling" }.Contains(progress.State)) return;
            lock (gate)
            {
                if (operation.Terminal || (operation.CancelRequestedAt != 0 && progress.State != "cancelling")) return;
                operation.State = progress.State; operation.Message = Limited(progress.Message, 1500); operation.UpdatedAt = clock();
                TryPersistLocked();
            }
            Notify();
        }

        internal void CheckDeadlines()
        {
            List<CancellationTokenSource> cancel = new List<CancellationTokenSource>();
            bool changed = false;
            lock (gate)
            {
                if (disposed) return;
                long now = clock();
                foreach (Operation operation in operations.Values.Where(o => o.WorkerRunning && !o.Terminal))
                {
                    if (operation.CancelRequestedAt == 0 && now >= operation.ExpiresAt)
                    {
                        operation.TimeoutCancellation = true; operation.CancelRequestedAt = now;
                        operation.State = "cancelling"; operation.Message = "Время ожидания истекло; ожидается остановка регистрации";
                        operation.UpdatedAt = now; cancel.Add(operation.Cancellation); changed = true;
                    }
                    else if (operation.CancelRequestedAt != 0 && now - operation.CancelRequestedAt >= 10000)
                    {
                        operation.State = "unknown"; operation.Terminal = true; operation.UpdatedAt = now;
                        operation.Message = "Остановка не подтверждена. Панель остаётся занятой до ответа модуля регистрации."; changed = true;
                    }
                }
                if (changed) TryPersistLocked();
            }
            foreach (CancellationTokenSource token in cancel) TryCancel(token);
            if (changed) Notify();
        }

        public void CancelAll()
        {
            List<CancellationTokenSource> cancel = new List<CancellationTokenSource>();
            lock (gate)
            {
                foreach (Operation operation in operations.Values.Where(o => o.WorkerRunning))
                {
                    if (operation.CancelRequestedAt == 0) operation.CancelRequestedAt = clock();
                    operation.State = "cancelling"; operation.Message = "Запрошена отмена выполняющихся регистраций; ожидается подтверждение остановки"; operation.UpdatedAt = clock();
                    cancel.Add(operation.Cancellation);
                }
                if (cancel.Count > 0) TryPersistLocked();
            }
            foreach (CancellationTokenSource token in cancel) TryCancel(token);
            Notify();
        }

        private static void TryCancel(CancellationTokenSource cancellation)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        }

        private Dictionary<string, object> WithOperation(string requestId, Operation operation, bool ok, string message)
        {
            Dictionary<string, object> response = ProtocolV3.Response(requestId, ok, message);
            response["accepted"] = true;
            response["operation"] = Public(operation);
            return response;
        }

        private static bool IsProvenPreDispatchRejection(string code)
        {
            // Only explicit pre-dispatch guards qualify. Unknown exceptions, malformed
            // encrypted traffic and a missing status record cannot prove no side effect.
            return new[] {
                "driver_unavailable", "panel_busy", "device_busy", "project_missing", "project_mismatch",
                "record_missing", "already_initialized", "duplicate_hardware_id", "journal_unavailable",
                "stale_request", "unsupported_protocol", "recovery_busy"
            }.Contains(code);
        }

        private static Dictionary<string, object> Public(Operation operation)
        {
            return new Dictionary<string, object> {
                { "operationId", operation.Id }, { "panelId", operation.PanelId }, { "panelName", operation.PanelName },
                { "recordId", operation.Target.RecordId }, { "expectedHardwareId", operation.Target.ExpectedHardwareId },
                { "projectFingerprint", operation.Target.ProjectFingerprint }, { "recoveredReadOnly", operation.RecoveredReadOnly },
                { "state", operation.State }, { "terminal", operation.Terminal }, { "registrationConfirmed", operation.RegistrationConfirmed },
                { "confirmedHardwareId", operation.ConfirmedHardwareId }, { "registrationEvidence", operation.Evidence },
                { "message", operation.Message }, { "startedAt", operation.StartedAt }, { "updatedAt", operation.UpdatedAt },
                { "errorCode", RegistrationDiagnosticData.Code(operation.ErrorCode) },
                { "errorPhase", RegistrationDiagnosticData.Phase(operation.ErrorPhase) },
                { "diagnosticDetails", RegistrationDiagnosticData.Details(operation.DiagnosticDetails) },
                { "expiresAt", operation.ExpiresAt }, { "projectSaved", operation.ProjectSaved }
            };
        }

        private string ParametersHash(string panelId, string fingerprint, string record, string hardware, string key, int timeout)
        {
            string canonical = String.Join("\n", new[] { panelId, fingerprint, record, hardware, key, timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            using (HMACSHA256 hmac = new HMACSHA256(identityKey)) return BridgeValues.Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        private void PersistLocked()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Operation operation in operations.Values.OrderBy(o => o.StartedAt))
            {
                Dictionary<string, object> row = Public(operation);
                row["parametersHash"] = operation.ParametersHash; row["moduleId"] = operation.ModuleId;
                row["projectFingerprint"] = operation.Target.ProjectFingerprint;
                row["timeoutSeconds"] = operation.Target.TimeoutSeconds; row["workerRunning"] = operation.WorkerRunning;
                rows.Add(row);
            }
            string content = BridgeJson.Serialize(new Dictionary<string, object> { { "version", 1 }, { "operations", rows.ToArray() } });
            // The previous content is either fully validated at load or was
            // written by this process. Never back up or replace unexpected data.
            if (journalDigest != null)
            {
                if (!File.Exists(journalPath) || JournalDigest(File.ReadAllText(journalPath, Encoding.UTF8)) != journalDigest)
                    throw new InvalidDataException("Журнал регистрации изменён или удалён другим процессом. Запись остановлена; сохраните существующие файлы и закройте лишние экземпляры моста.");
            }
            else if (File.Exists(journalPath))
                throw new InvalidDataException("Файл журнала появился после запуска моста. Запись остановлена для сохранения истории.");
            BridgeFiles.WriteAtomic(journalPath, content, journalPath + ".bak");
            journalDigest = JournalDigest(content);
            journalStorageProblem = null;
            journalStorageDiagnostic = "";
        }

        private void TryPersistLocked()
        {
            try { PersistLocked(); }
            catch (Exception error) { JournalStorageFailed(error); }
        }

        private void LoadJournal()
        {
            journalMissingAtStartup = !File.Exists(journalPath);
            if (journalMissingAtStartup) return;
            string content = File.ReadAllText(journalPath, Encoding.UTF8);
            Dictionary<string, object> root = BridgeJson.ParseObject(content);
            object[] rows = BridgeValues.Array(root, "operations");
            if (BridgeValues.Number(root, "version") != 1 || rows == null) throw new InvalidDataException("Повреждён журнал регистрации Windows-моста.");
            bool recovered = false;
            foreach (object value in rows)
            {
                Dictionary<string, object> row = value as Dictionary<string, object>;
                Operation operation = new Operation {
                    Id = BridgeValues.GuidValue(BridgeValues.Text(row, "operationId"), "операция журнала"),
                    PanelId = BridgeValues.GuidValue(BridgeValues.Text(row, "panelId"), "панель журнала"),
                    PanelName = BridgeValues.Text(row, "panelName"), ModuleId = BridgeValues.Text(row, "moduleId"), ParametersHash = BridgeValues.Text(row, "parametersHash"),
                    State = BridgeValues.Text(row, "state"), Terminal = BridgeValues.Flag(row, "terminal"), Message = BridgeValues.Text(row, "message"),
                    ErrorCode = RegistrationDiagnosticData.Code(BridgeValues.Text(row, "errorCode")),
                    ErrorPhase = RegistrationDiagnosticData.Phase(BridgeValues.Text(row, "errorPhase")),
                    DiagnosticDetails = RegistrationDiagnosticData.Details(BridgeValues.Object(row, "diagnosticDetails")),
                    RegistrationConfirmed = BridgeValues.Flag(row, "registrationConfirmed"), ProjectSaved = BridgeValues.Flag(row, "projectSaved"),
                    RecoveredReadOnly = BridgeValues.Flag(row, "recoveredReadOnly"),
                    ConfirmedHardwareId = BridgeValues.Text(row, "confirmedHardwareId"), Evidence = BridgeValues.Text(row, "registrationEvidence"),
                    StartedAt = BridgeValues.Number(row, "startedAt"), UpdatedAt = BridgeValues.Number(row, "updatedAt"), ExpiresAt = BridgeValues.Number(row, "expiresAt"),
                    Target = new RegistrationTarget { OperationId = BridgeValues.Text(row, "operationId"), RecordId = BridgeValues.Text(row, "recordId"),
                        ExpectedHardwareId = BridgeValues.Text(row, "expectedHardwareId"), ProjectFingerprint = BridgeValues.Text(row, "projectFingerprint"),
                        TimeoutSeconds = (int)BridgeValues.Number(row, "timeoutSeconds", 90) }
                };
                BridgeValues.GuidValue(operation.Target.RecordId, "запись проекта в журнале");
                if (!Regex.IsMatch(operation.Target.ProjectFingerprint, "^[0-9a-fA-F]{64}$") ||
                    !Regex.IsMatch(operation.Target.ExpectedHardwareId, "^[0-9A-F]{12}$") ||
                    !Regex.IsMatch(operation.ParametersHash, "^[0-9a-f]{64}$") ||
                    !new[] { "connecting", "waiting_for_sensor", "detected", "registering", "cancelling", "registered", "failed", "cancelled", "timed_out", "unknown" }.Contains(operation.State))
                    throw new InvalidDataException("Повреждена запись журнала регистрации Windows-моста.");
                bool inconsistent = operation.State == "registered" && (!operation.RegistrationConfirmed || operation.ConfirmedHardwareId != operation.Target.ExpectedHardwareId || String.IsNullOrWhiteSpace(operation.Evidence));
                if (!operation.Terminal || BridgeValues.Flag(row, "workerRunning") || inconsistent)
                {
                    operation.State = "unknown"; operation.Terminal = true; operation.RegistrationConfirmed = false;
                    operation.ConfirmedHardwareId = null; operation.Evidence = null; operation.ProjectSaved = false;
                    operation.Message = "Мост был перезапущен. Незавершённая регистрация не повторялась; проверьте результат в Стрелец-Мастере.";
                    operation.UpdatedAt = clock(); recovered = true;
                }
                operations.Add(operation.Id, operation);
            }
            journalDigest = JournalDigest(content);
            if (recovered) PersistLocked();
        }

        private static string JournalDigest(string text)
        {
            using (SHA256 hash = SHA256.Create()) return BridgeValues.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string StorageDiagnostic(Exception error)
        {
            if (error == null) return "save_not_committed; тип: нет исключения; HRESULT: не получен";
            string category = error is UnauthorizedAccessException || error is System.Security.SecurityException ? "access_denied"
                : error is DirectoryNotFoundException ? "directory_missing"
                : error is PathTooLongException ? "path_too_long"
                : error is InvalidDataException ? "data_invalid_or_changed"
                : IsReplaceTargetRemovalFailure(error) ? "replace_target_remove_failed"
                : error is IOException ? "io_error" : "save_error";
            // Runtime type name and HRESULT are useful, but exception messages,
            // file paths and arbitrary data can contain project secrets.
            return category + "; тип: " + error.GetType().Name + "; HRESULT: 0x" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void CatalogStorageFailed(Exception error)
        {
            catalogStorageDiagnostic = StorageDiagnostic(error);
            catalogStorageProblem = "Каталог получен; проект не сохранён. " + StorageReason(error) + " «Сохранить копию» → «Открыть проект».";
        }

        private void ProjectStorageFailed(Exception error)
        {
            projectStorageDiagnostic = StorageDiagnostic(error);
            projectStorageProblem = "Регистрация подтверждена; проект не сохранён. " + StorageReason(error) + " «Сохранить копию» → «Открыть проект», затем повторите сохранение результата на телефоне.";
        }

        private static string StorageReason(Exception error)
        {
            if (error == null) return "Сохранение не подтверждено.";
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "Нет доступа к файлу.";
            if (error is DirectoryNotFoundException) return "Папка недоступна.";
            if (error is PathTooLongException) return "Слишком длинный путь.";
            if (error is InvalidDataException) return "Данные изменены или повреждены.";
            if (IsReplaceTargetRemovalFailure(error)) return "Windows не смогла заменить существующий файл. Сохраните копию под новым именем в доступной локальной папке и откройте её.";
            if (error is IOException) return "Ошибка записи файла.";
            return "Ошибка сохранения.";
        }

        private static bool IsReplaceTargetRemovalFailure(Exception error)
        {
            // ERROR_UNABLE_TO_REMOVE_REPLACED (1175): do not delete the old
            // file or fall back to a non-atomic overwrite. The reviewed path
            // is an explicit new copy, followed by opening that copy.
            return error is IOException && error.HResult == unchecked((int)0x80070497);
        }

        private void JournalStorageFailed(Exception error)
        {
            journalStorageDiagnostic = StorageDiagnostic(error);
            journalStorageProblem = "Журнал операций не обновлён. " + StorageReason(error) + " Сохраните диагностику; проверьте доступ к профилю Windows и закройте лишние экземпляры моста.";
        }

        private static string Limited(string text, int max) { return text == null ? "" : text.Length <= max ? text : text.Substring(0, max); }
        private void Notify() { Action handler = Changed; if (handler != null) { try { handler(); } catch { } } }

        public void Dispose()
        {
            CancelAll();
            lock (gate) disposed = true;
            watchdog.Dispose();
        }
    }
}
