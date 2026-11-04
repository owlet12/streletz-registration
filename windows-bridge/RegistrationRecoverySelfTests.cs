// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StreletzBridge
{
    // Contract tests execute the production recovery driver/coordinator against
    // synthetic read-only replies. No named pipe, panel, native UI or credentials.
    internal static class RegistrationRecoverySelfTests
    {
        private const string Record = "11111111-1111-4111-8111-111111111111";
        private const string OtherRecord = "22222222-2222-4222-8222-222222222222";
        private const string Operation = "55555555-5555-4555-8555-555555555555";
        private const string NativePanel = "33333333-3333-4333-8333-333333333333";
        private const string Segment = "66666666-6666-4666-8666-666666666666";
        private const string Adapter = "77777777-7777-4777-8777-777777777777";
        private const string Hardware = "FA0000000001";
        private const string Secret = "SYNTHETIC_PRIVATE_DATA_MUST_NOT_APPEAR";
        private const string Pairing = "1234567890ABCDEF";
        private static int checks;

        private sealed class Fixture : IDisposable
        {
            internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "streletz-recovery-" + Guid.NewGuid().ToString("N"));
            internal readonly string Journal;
            internal readonly Dictionary<string, object> Project;
            internal readonly Dictionary<string, object> Panel;
            internal readonly PanelProfileStore Profiles;
            internal readonly string PanelId;
            internal readonly string Fingerprint;
            internal readonly PanelRegistrationDriver Driver;
            internal RegistrationCoordinator Coordinator;
            internal readonly List<string> Calls = new List<string>();
            internal Func<Dictionary<string, object>> StatusResponse;
            internal Action<string> BeforeReply;
            internal string NativeFingerprint = new string('f', 64);
            internal string AfterFingerprint;
            internal long ProcessId = 100, AfterProcessId = 100;
            internal bool Ready = true, SaveSucceeds = true, SaveThrows, CatalogSaveSucceeds = true, CatalogSaveThrows;
            internal Exception CatalogSaveFailure;
            internal bool DurableBeforeSave;
            internal string SavePath;
            internal int Saves, HelloCalls;

            internal Fixture(string initialState = null, bool blockedJournal = false)
            {
                Directory.CreateDirectory(DirectoryPath);
                Journal = Path.Combine(DirectoryPath, "registration-journal.json");
                if (blockedJournal) Directory.CreateDirectory(Journal);
                Project = new Dictionary<string, object> { { "schemaVersion", 1 }, { "siteName", "Synthetic recovery project" },
                    { "records", new object[] {
                        new Dictionary<string, object> { { "id", Record }, { "recordKind", "device" }, { "initialized", "-" },
                            { "commissioning", new Dictionary<string, object> { { "stage", 0 }, { "serial", "" } } } },
                        new Dictionary<string, object> { { "id", OtherRecord }, { "recordKind", "device" }, { "initialized", "-" } }
                    } } };
                Fingerprint = BridgeValues.ProjectFingerprint(Project);
                Panel = new Dictionary<string, object> {
                    { "nativeNodeGuid", NativePanel }, { "segmentGuid", Segment }, { "displayName", "Synthetic panel" },
                    { "host", "192.168.0.240" }, { "port", 6344 }, { "moduleId", "FB0000000003" },
                    { "routeKind", "direct_mk_ip" }, { "viaPanelTerminal", false }, { "adapterNodeId", Adapter },
                    { "adapterSegmentId", Segment }, { "terminalPanelId", "" }, { "terminalSegmentId", "" },
                    { "recordIds", new object[] { Record, OtherRecord } }
                };
                var catalog = ProjectPanelCatalog.FromNative(Listing(), Project);
                ProjectPanelCatalog.Apply(Project, catalog);
                PanelId = BridgeValues.Text(catalog[0], "panelId");
                Profiles = new PanelProfileStore(Path.Combine(DirectoryPath, "panels.json"));
                Profiles.SynchronizeProjectPanels(catalog, new HashSet<string>());
                Driver = new PanelRegistrationDriver(Exchange, 0);
                if (initialState != null) File.WriteAllText(Journal, Seed(initialState));
                Coordinator = CreateCoordinator();
            }

            internal RegistrationCoordinator CreateCoordinator()
            {
                return new RegistrationCoordinator(Profiles, () => BridgeValues.Copy(Project), (target, result) => {
                    Saves++;
                    DurableBeforeSave = File.Exists(Journal) && File.ReadAllText(Journal).Contains("registered") && File.ReadAllText(Journal).Contains(Operation);
                    if (SaveThrows) throw new IOException(Secret);
                    if (SavePath != null)
                    {
                        var copy = BridgeValues.Copy(Project);
                        var selected = ((Dictionary<string, object>)BridgeValues.Array(copy, "records")[0]);
                        selected["initialized"] = "+";
                        selected["commissioning"] = new Dictionary<string, object> { { "stage", 2 }, { "serial", target.ExpectedHardwareId },
                            { "registrationOperationId", target.OperationId }, { "registrationEvidence", result.Evidence } };
                        BridgeFiles.WriteAtomic(SavePath, BridgeJson.Serialize(copy));
                    }
                    return SaveSucceeds;
                }, Driver, Journal, ProtocolV3.JournalIdentityKey(Pairing), savePanelCatalog: (fingerprint, panels, nativeFingerprint) => {
                    if (CatalogSaveFailure != null) throw CatalogSaveFailure;
                    if (CatalogSaveThrows) throw new UnauthorizedAccessException(Secret);
                    return CatalogSaveSucceeds;
                });
            }

            internal Dictionary<string, object> Listing()
            { return new Dictionary<string, object> { { "nativeProjectFingerprint", NativeFingerprint }, { "panels", new object[] { Panel } } }; }

            private Dictionary<string, object> Exchange(Dictionary<string, object> request, int timeout)
            {
                string type = BridgeValues.Text(request, "type");
                Calls.Add(type);
                Check(timeout > 0, "unbounded native recovery call");
                Check(type == "hello" || type == "list_panels" || type == "status", "recovery sent a mutating command: " + type);
                Check(!BridgeJson.Serialize(request).Contains("deviceKey"), "recovery sent a device key");
                if (BeforeReply != null) BeforeReply(type);
                if (type == "hello")
                {
                    HelloCalls++;
                    return Reply(new Dictionary<string, object> { { "ready", Ready }, { "version", "12.4" }, { "pluginVersion", "1.0.5.0" },
                        { "processId", HelloCalls == 1 ? ProcessId : AfterProcessId },
                        { "nativeProjectFingerprint", HelloCalls == 1 ? NativeFingerprint : (AfterFingerprint ?? NativeFingerprint) },
                        { "unavailableReason", Ready ? "" : "master_login_required" } });
                }
                if (type == "list_panels") return Reply(Listing());
                Check(BridgeValues.Text(request, "operationId") == Operation, "recovery changed original operation ID");
                Check(request.Count == 2, "native status received fields beyond original ID");
                return StatusResponse == null ? Reply(State("registered")) : StatusResponse();
            }

            internal Dictionary<string, object> State(string status)
            {
                bool registered = status == "registered";
                var result = new Dictionary<string, object> {
                    { "operationId", Operation }, { "projectFingerprint", Fingerprint }, { "recordId", Record },
                    { "panelId", PanelId }, { "expectedHardwareId", Hardware }, { "status", status },
                    { "nativePanelId", NativePanel }, { "host", "192.168.0.240" }, { "port", 6344 }, { "moduleId", "FB0000000003" },
                    { "nativeProjectFingerprint", new string('e', 64) }, { "validatedBy", "native-fingerprint-record-and-panel" },
                    { "registrationConfirmed", registered }, { "confirmedHardwareId", registered ? Hardware : null },
                    { "confirmationSent", registered }, { "nativeSuccessObserved", registered }, { "nativeRunFinished", true },
                    { "dialogClosed", true }, { "nativeDialogOpen", false }, { "finishedUtc", "2026-10-28T00:00:00Z" },
                    { "error", status == "failed" ? new Dictionary<string, object> { { "code", "native_adapter_mismatch" } } : null }
                };
                PanelRouteMetadata.CopyIdentity(Panel, result);
                return result;
            }

            internal Dictionary<string, object> Request(string type = "recover_registration")
            {
                return new Dictionary<string, object> { { "protocol", 3 }, { "type", type }, { "requestId", Guid.NewGuid().ToString("D") },
                    { "sentAt", BridgeValues.NowMs() }, { "operationId", Operation }, { "projectFingerprint", Fingerprint },
                    { "recordId", Record }, { "expectedHardwareId", Hardware }, { "panelId", PanelId } };
            }

            internal Dictionary<string, object> Recover()
            {
                var request = Request();
                // Authenticated protocol round trip with the same production API.
                return ProtocolV3.Decode(ProtocolV3.Encode(Coordinator.Handle(ProtocolV3.Decode(ProtocolV3.Encode(request, Pairing, "request"), Pairing, "request")),
                    Pairing, "response"), Pairing, "response");
            }

            private string Seed(string state)
            {
                return BridgeJson.Serialize(new Dictionary<string, object> { { "version", 1 }, { "operations", new object[] {
                    new Dictionary<string, object> {
                        { "operationId", Operation }, { "panelId", PanelId }, { "panelName", "Synthetic panel" }, { "moduleId", "FB0000000003" },
                        { "parametersHash", new string('a', 64) }, { "projectFingerprint", Fingerprint }, { "recordId", Record },
                        { "expectedHardwareId", Hardware }, { "timeoutSeconds", 90 }, { "state", state }, { "terminal", true },
                        { "registrationConfirmed", false }, { "projectSaved", false }, { "workerRunning", false },
                        { "startedAt", BridgeValues.NowMs() - 300000 }, { "updatedAt", BridgeValues.NowMs() }, { "expiresAt", BridgeValues.NowMs() - 210000 },
                        { "message", "Synthetic previous unknown result" }
                    }
                } } });
            }

            public void Dispose()
            {
                if (Coordinator != null) Coordinator.Dispose();
                Directory.Delete(DirectoryPath, true);
            }
        }

        private static Dictionary<string, object> Reply(Dictionary<string, object> data)
        { return new Dictionary<string, object> { { "protocol", "streletz-master-plugin-v1" }, { "ok", true }, { "data", data } }; }
        private static Dictionary<string, object> Error(string code)
        { return new Dictionary<string, object> { { "protocol", "streletz-master-plugin-v1" }, { "ok", false }, { "error", new Dictionary<string, object> { { "code", code }, { "message", Secret } } } }; }
        private static void Check(bool condition, string message)
        { checks++; if (!condition) throw new Exception(message); }
        private static void Failed(Dictionary<string, object> response, string context)
        {
            Check(!BridgeValues.Flag(response, "ok") && !response.ContainsKey("recovery") && !response.ContainsKey("operation"), context + " authorized manual review or masked error with old operation");
            Check(BridgeValues.Text(response, "operationId") == Operation, context + " did not echo canonical original operation ID");
        }
        private static void Recovery(Fixture f, Dictionary<string, object> response, string outcome, bool manual)
        {
            var recovery = BridgeValues.Object(response, "recovery");
            Check(BridgeValues.Flag(response, "ok") && recovery != null, outcome + " recovery missing: " + BridgeJson.Serialize(response));
            Check(BridgeValues.Text(response, "operationId") == Operation, "top-level operation ID lost");
            Check(BridgeValues.Text(recovery, "operationId") == Operation && BridgeValues.Text(recovery, "projectFingerprint") == f.Fingerprint &&
                BridgeValues.Text(recovery, "recordId") == Record && BridgeValues.Text(recovery, "expectedHardwareId") == Hardware &&
                BridgeValues.Text(recovery, "panelId") == f.PanelId, "recovery identity echo changed");
            Check(BridgeValues.Text(recovery, "outcome") == outcome && BridgeValues.Flag(recovery, "manualReviewAllowed") == manual, "unexpected manual-review authorization");
            if (outcome != "restored") Check(!response.ContainsKey("operation") && !response.ContainsKey("accepted"), "nondefinitive recovery fabricated operation acceptance");
            Check(!BridgeJson.Serialize(response).Contains(Secret), "private native message leaked in recovery response");
        }

        private static void RetainedSuccessAndNoReplay()
        {
            foreach (string previous in new[] { (string)null, "unknown" })
            using (var f = new Fixture(previous))
            {
                var response = f.Recover();
                Recovery(f, response, "restored", false);
                var op = BridgeValues.Object(response, "operation");
                Check(BridgeValues.Text(op, "state") == "registered" && BridgeValues.Flag(op, "registrationConfirmed") && BridgeValues.Flag(op, "projectSaved"), "retained native success not restored");
                Check(f.DurableBeforeSave && f.Saves == 1, "portable project changed before durable native evidence");
                Check(String.Join(",", f.Calls) == "hello,list_panels,status,hello", "recovery did more than the reviewed read-only probe");
                Check(BridgeValues.Text(BridgeJson.ParseObject(BridgeValues.Text(op, "registrationEvidence")), "nativeProjectFingerprint") == new string('e', 64),
                    "original native fingerprint was overwritten by changed current native fingerprint");
                Check(File.Exists(f.Journal + ".bak") && BridgeValues.Number(BridgeJson.ParseObject(File.ReadAllText(f.Journal + ".bak")), "version") == 1, "previous valid journal backup missing");
                int calls = f.Calls.Count;
                Recovery(f, f.Recover(), "restored", false);
                Check(f.Calls.Count == calls && f.Saves == 1, "known definitive result triggered a new native probe or repeat project write");
                var begin = f.Request("begin_registration"); begin["requestId"] = Operation; begin["deviceKey"] = "1234ABCD"; begin["timeoutSeconds"] = 90;
                Check(BridgeValues.Text(f.Coordinator.Handle(begin), "errorCode") == "recovered_operation_read_only", "restored operation ID accepted start replay");
                Check(f.Calls.Count == calls, "begin replay reached native transport");
                f.Coordinator.Dispose(); f.Coordinator = f.CreateCoordinator();
                Recovery(f, f.Recover(), "restored", false);
                Check(BridgeValues.Text(f.Coordinator.Handle(begin), "errorCode") == "recovered_operation_read_only" && f.Calls.Count == calls, "restart lost restored no-replay guard");
                string diagnostics = f.Coordinator.ConnectionDiagnosticReport;
                Check(diagnostics.Contains("Восстановленных только чтением: 1") && diagnostics.Contains("Операций в журнале: 1") && !diagnostics.Contains(Pairing), "journal/recovery diagnostics missing or secret leaked");
            }
        }

        private static void AbsentActiveAndUnknown()
        {
            foreach (string missing in new[] { "operation_not_found", "operation_result_expired", "operation_history_expired" })
            using (var f = new Fixture())
            {
                f.StatusResponse = () => Error(missing);
                // An already initialized current record is not proof of the old
                // operation, but must not block a verified read-only review.
                var row = (Dictionary<string, object>)BridgeValues.Array(f.Project, "records")[0];
                row["initialized"] = "+";
                row["commissioning"] = new Dictionary<string, object> { { "stage", 2 }, { "serial", Hardware } };
                Recovery(f, f.Recover(), "manual_review_required", true);
                Check(f.Saves == 0 && !File.Exists(f.Journal), "absent result invented journal success or initialization");
                Check(String.Join(",", f.Calls) == "hello,list_panels,status,hello", "missing-result manual review skipped fresh context verification");
            }
            foreach (string status in new[] { "waiting_sensor", "registered", "unknown" })
            using (var f = new Fixture("unknown"))
            {
                f.StatusResponse = () => { var state = f.State(status); state["nativeRunFinished"] = false; state["dialogClosed"] = false; return Reply(state); };
                Recovery(f, f.Recover(), "native_active", false);
                Check(f.Saves == 0 && File.ReadAllText(f.Journal).Contains("unknown"), "active native operation overwrote unknown journal result");
            }
            using (var f = new Fixture("unknown"))
            {
                f.StatusResponse = () => Reply(f.State("unknown"));
                Recovery(f, f.Recover(), "manual_review_required", true);
                Check(f.Saves == 0, "stopped unknown result was fabricated as registration");
            }
            foreach (string state in new[] { "cancelled", "failed" })
            using (var f = new Fixture())
            {
                f.StatusResponse = () => Reply(f.State(state));
                var response = f.Recover(); Recovery(f, response, "restored", false);
                Check(BridgeValues.Text(BridgeValues.Object(response, "operation"), "state") == state && f.Saves == 0, "definitive native stopped state misclassified");
            }
        }

        private static void RejectIdentityAndEvidenceDrift()
        {
            var changes = new Dictionary<string, object> {
                { "operationId", Guid.NewGuid().ToString("D") }, { "projectFingerprint", new string('b', 64) }, { "recordId", OtherRecord },
                { "expectedHardwareId", "FA0000000002" }, { "panelId", Guid.NewGuid().ToString("D") },
                { "nativePanelId", Guid.NewGuid().ToString("D") }, { "host", "192.168.0.241" }, { "port", 6345 }, { "moduleId", "FB0000000004" },
                { "adapterNodeId", Guid.NewGuid().ToString("D") }, { "adapterSegmentId", Guid.NewGuid().ToString("D") },
                { "terminalPanelId", Guid.NewGuid().ToString("D") }, { "nativeProjectFingerprint", "malformed" },
                { "registrationConfirmed", false }, { "confirmationSent", false }, { "nativeSuccessObserved", false },
                { "confirmedHardwareId", "FA0000000002" }, { "validatedBy", "unverified" }, { "status", "surprise" }, { "nativeDialogOpen", "not-a-boolean" }
            };
            foreach (var field in changes)
            using (var f = new Fixture("unknown"))
            {
                f.StatusResponse = () => { var state = f.State("registered"); state[field.Key] = field.Value; return Reply(state); };
                Failed(f.Recover(), "retained " + field.Key);
                Check(f.Saves == 0, "unverified retained result saved project");
            }
            foreach (string flag in new[] { "registrationConfirmed", "confirmationSent", "nativeSuccessObserved", "nativeRunFinished", "dialogClosed" })
            using (var f = new Fixture())
            {
                f.StatusResponse = () => { var state = f.State("failed"); state.Remove(flag); return Reply(state); };
                Failed(f.Recover(), "missing explicit boolean " + flag);
            }
            using (var f = new Fixture())
            {
                f.StatusResponse = () => { var state = f.State("failed"); state["confirmationSent"] = true; return Reply(state); };
                Failed(f.Recover(), "failed after confirmation");
            }
        }

        private static void RejectWrongOrUnavailableContext()
        {
            foreach (string key in new[] { "projectFingerprint", "recordId", "expectedHardwareId", "panelId" })
            using (var f = new Fixture("unknown"))
            {
                var request = f.Request();
                request[key] = key == "projectFingerprint" ? new string('b', 64) : key == "expectedHardwareId" ? "FA0000000002" : Guid.NewGuid().ToString("D");
                Failed(f.Coordinator.Handle(request), "request identity " + key);
                Check(f.Calls.Count == 0, "mismatched stored identity probed native process");
            }
            using (var f = new Fixture()) { f.Ready = false; Failed(f.Recover(), "not logged in"); }
            using (var f = new Fixture()) { f.ProcessId = 0; Failed(f.Recover(), "missing process identity"); }
            using (var f = new Fixture()) { f.NativeFingerprint = "bad"; Failed(f.Recover(), "missing current fingerprint"); }
            using (var f = new Fixture()) { f.AfterProcessId = 101; Failed(f.Recover(), "Master restarted during probe"); }
            using (var f = new Fixture()) { f.AfterFingerprint = new string('c', 64); Failed(f.Recover(), "current native project changed during probe"); }
            using (var f = new Fixture())
            {
                f.Panel["recordIds"] = new object[] { OtherRecord };
                Failed(f.Recover(), "missing current native membership");
            }
            using (var f = new Fixture())
            {
                f.Panel["moduleId"] = "FB0000000004";
                Failed(f.Recover(), "current MK-IP changed");
            }
            using (var f = new Fixture())
            {
                f.StatusResponse = () => { throw new IOException(Secret); };
                Failed(f.Recover(), "status IPC unavailable");
                Check(!f.Coordinator.ConnectionDiagnosticReport.Contains(Secret), "transport error text leaked private content");
            }
            using (var f = new Fixture())
            {
                f.StatusResponse = () => Error("unrecognized_error_with_private_data");
                Failed(f.Recover(), "unrecognized native failure");
            }
            using (var f = new Fixture())
            {
                f.BeforeReply = type => { if (type == "status") f.Project["records"] = new object[] { BridgeValues.Array(f.Project, "records")[0] }; };
                Failed(f.Recover(), "portable project changed during probe");
                Check(f.Saves == 0, "changed project received restored initialization");
            }
        }

        private static void JournalLeaseBackupAndSaveFailures()
        {
            using (var f = new Fixture(blockedJournal: true))
            {
                var response = f.Recover(); Failed(response, "journal creation refused");
                Check(BridgeValues.Text(response, "errorCode") == "recovery_journal_unavailable" && f.Saves == 0, "project mutated before retained result could be persisted");
            }
            using (var f = new Fixture("unknown"))
            {
                string altered = File.ReadAllText(f.Journal) + " external-change";
                File.WriteAllText(f.Journal, altered);
                Failed(f.Recover(), "external journal modification");
                Check(f.Saves == 0 && File.ReadAllText(f.Journal) == altered && !File.Exists(f.Journal + ".bak"), "external or damaged journal overwritten/backed up as valid");
            }
            using (var f = new Fixture("unknown"))
            {
                File.Delete(f.Journal);
                Failed(f.Recover(), "externally removed journal");
                Check(!File.Exists(f.Journal) && f.Saves == 0, "deleted journal silently recreated after recovery");
            }
            using (var f = new Fixture())
            {
                f.SaveThrows = true;
                var response = f.Recover(); Recovery(f, response, "restored", false);
                Check(BridgeValues.Flag(BridgeValues.Object(response, "operation"), "registrationConfirmed") && !BridgeValues.Flag(BridgeValues.Object(response, "operation"), "projectSaved"), "project save error erased physical confirmation");
                Check(f.Coordinator.LastStorageProblem != null && f.Coordinator.ConnectionDiagnosticReport.Contains("io_error") && !f.Coordinator.ConnectionDiagnosticReport.Contains(Secret), "project storage diagnostics absent or leaked exception message");
                Check(!String.IsNullOrEmpty(f.Coordinator.LastStorageProblem), "successful journal persistence erased project storage failure");
            }
            using (var f = new Fixture())
            {
                f.SavePath = Path.Combine(f.DirectoryPath, "blocked-project-target");
                Directory.CreateDirectory(f.SavePath);
                var response = f.Recover(); Recovery(f, response, "restored", false);
                Check(BridgeValues.Flag(BridgeValues.Object(response, "operation"), "registrationConfirmed") && !BridgeValues.Flag(BridgeValues.Object(response, "operation"), "projectSaved"),
                    "unwritable project target erased hardware confirmation");
                int nativeCalls = f.Calls.Count;
                f.SavePath = Path.Combine(f.DirectoryPath, "writable-copy.s3project");
                response = f.Recover(); Recovery(f, response, "restored", false);
                Check(BridgeValues.Flag(BridgeValues.Object(response, "operation"), "projectSaved") && f.Saves == 2 && f.Calls.Count == nativeCalls,
                    "explicit project-save retry probed/re-registered hardware or failed to save copied project");
                var saved = BridgeJson.ParseObject(File.ReadAllText(f.SavePath));
                var commissioned = BridgeValues.Object((Dictionary<string, object>)BridgeValues.Array(saved, "records")[0], "commissioning");
                Check(BridgeValues.Number(commissioned, "stage") == 2 && BridgeValues.Text(commissioned, "serial") == Hardware && BridgeValues.Text(commissioned, "registrationOperationId") == Operation,
                    "writable project copy did not receive authoritative exact original result");
                Check(f.Coordinator.LastStorageProblem == null, "successful confirmed-project retry left stale save failure");
            }
            using (var f = new Fixture())
            {
                Recovery(f, f.Recover(), "restored", false);
                string backup = File.ReadAllText(f.Journal + ".bak");
                f.Coordinator.Dispose(); f.Coordinator = null;
                File.WriteAllText(f.Journal, "{");
                bool failed = false;
                try { using (var rejected = f.CreateCoordinator()) { } } catch { failed = true; }
                Check(failed && File.ReadAllText(f.Journal) == "{" && File.ReadAllText(f.Journal + ".bak") == backup, "corrupt current journal silently loaded backup or cleared history");
            }
            string directory = Path.Combine(Path.GetTempPath(), "streletz-lease-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var first = BridgeStateLease.Acquire(directory))
                {
                    bool rejected = false;
                    try { using (var second = BridgeStateLease.Acquire(directory)) { } } catch (IOException) { rejected = true; }
                    Check(rejected, "second writer acquired same state folder");
                    using (var other = BridgeStateLease.Acquire(Path.Combine(directory, "other"))) { }
                }
                using (var reopened = BridgeStateLease.Acquire(directory)) { }
                Check(File.Exists(Path.Combine(directory, "bridge-state.lock")), "lock file deleted after release; pathname race possible");
            }
            finally { Directory.Delete(directory, true); }
            foreach (bool throws in new[] { false, true })
            using (var f = new Fixture())
            {
                f.CatalogSaveSucceeds = false; f.CatalogSaveThrows = throws;
                var response = f.Coordinator.Handle(f.Request("list_panels"));
                Check(BridgeValues.Flag(response, "ok") && !BridgeValues.Flag(response, "projectSaved"), "catalog save refusal hid read-only route result");
                Check(f.Coordinator.LastStorageProblem != null && f.Coordinator.LastStorageProblem.Contains("Сохранить копию") && f.Coordinator.LastStorageProblem.Contains("Открыть проект"), "storage failure lacks concrete save-copy/reopen instruction");
                string report = f.Coordinator.ConnectionDiagnosticReport;
                Check(report.Contains(throws ? "access_denied" : "save_not_committed") && !report.Contains(Secret), "safe catalog diagnostic category missing or secret included");
            }
            foreach (int code in new[] { unchecked((int)0x80070497), unchecked((int)0x80070020) })
            using (var f = new Fixture())
            {
                string original = BridgeJson.Serialize(f.Project);
                f.CatalogSaveFailure = new IOException(Secret, code);
                var response = f.Coordinator.Handle(f.Request("list_panels"));
                bool targetReplacement = code == unchecked((int)0x80070497);
                Check(BridgeValues.Flag(response, "ok") && !BridgeValues.Flag(response, "projectSaved") && f.Saves == 0,
                    "catalog replacement failure changed a registration or reported a committed project");
                Check(BridgeJson.Serialize(f.Project) == original, "catalog replacement failure mutated the opened project");
                string report = f.Coordinator.ConnectionDiagnosticReport;
                Check(report.Contains(targetReplacement ? "replace_target_remove_failed" : "io_error") && !report.Contains(Secret),
                    "specific replacement HRESULT was misclassified or exposed private exception text");
                Check(!targetReplacement || f.Coordinator.LastStorageProblem.Contains("под новым именем"),
                    "replacement refusal lacks an explicit new-copy recovery instruction");
                Check(f.Calls.All(type => type == "hello" || type == "list_panels"),
                    "catalog replacement failure sent hardware or cancellation commands");
            }
        }

        private static void MissingStatusDoesNotProveCancellation()
        {
            using (var f = new Fixture())
            foreach (string type in new[] { "registration_status", "cancel_registration" })
            {
                var response = f.Coordinator.Handle(f.Request(type));
                Check(!BridgeValues.Flag(response, "ok") && BridgeValues.Text(response, "errorCode") == "operation_not_found", "missing history guessed terminal result");
                Check(BridgeValues.Text(response, "operationId") == Operation && !response.ContainsKey("accepted") && !response.ContainsKey("operation") && !response.ContainsKey("recovery"), "missing history lost correlation or authorized release");
                Check(f.Calls.Count == 0 && f.Saves == 0, "missing local cancel affected native process");
            }
        }

        internal static void Run()
        {
            checks = 0;
            RetainedSuccessAndNoReplay();
            AbsentActiveAndUnknown();
            RejectIdentityAndEvidenceDrift();
            RejectWrongOrUnavailableContext();
            JournalLeaseBackupAndSaveFailures();
            MissingStatusDoesNotProveCancellation();
            Console.WriteLine("PASS recovery/storage contract checks: " + checks + "; synthetic read-only transport, no hardware");
        }
    }
}
