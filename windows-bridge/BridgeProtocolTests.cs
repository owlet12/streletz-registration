// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
#if BRIDGE_CORE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml;

namespace StreletzBridge
{
    internal static class BridgeProtocolTests
    {
        private const string Code = "1234567890ABCDEF";
        private const string FirstRecord = "11111111-1111-4111-8111-111111111111";
        private const string SecondRecord = "22222222-2222-4222-8222-222222222222";
        private const string Sensor = "FA0000000001";

        private sealed class FakeDriver : IPanelRegistrationDriver
        {
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            public int Calls;
            public string Journal;
            public bool JournalSeenBeforeHardware;
            public bool Available = true;
            public bool ThrowAvailability;
            public bool IgnoreCancellation;
            public bool ConfirmStop = true;
            public bool ConfirmRegistration = true;
            public string ResultState = "registered";
            public string ResultHardwareId;
            public string ResultEvidence = "test-native-success-and-readback";
            public string ResultMessage = "Test driver result";
            public string ResultErrorCode;
            public string ResultErrorPhase;
            public Dictionary<string, object> ResultDiagnosticDetails;
            public bool IsAvailable { get { if (ThrowAvailability) throw new IOException("test availability failure"); return Available; } }
            public string UnavailableReason { get { return "Test driver unavailable"; } }

            public RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target,
                CancellationToken cancellation, Action<RegistrationDriverProgress> progress)
            {
                JournalSeenBeforeHardware = File.Exists(Journal) && File.ReadAllText(Journal).Contains(target.OperationId);
                Interlocked.Increment(ref Calls);
                progress(new RegistrationDriverProgress("waiting_for_sensor", "Test waiting for sensor"));
                while (!Release.Wait(10))
                {
                    if (!IgnoreCancellation && cancellation.IsCancellationRequested)
                    {
                        if (!ConfirmStop) throw new OperationCanceledException();
                        return new RegistrationDriverResult { State = "cancelled", StopConfirmed = true, Message = "Test stop acknowledged" };
                    }
                }
                return new RegistrationDriverResult {
                    State = ResultState, RegistrationConfirmed = ConfirmRegistration,
                    HardwareId = ResultHardwareId ?? target.ExpectedHardwareId,
                    Evidence = ResultEvidence, StopConfirmed = true, Message = ResultMessage,
                    ErrorCode = ResultErrorCode, ErrorPhase = ResultErrorPhase,
                    DiagnosticDetails = ResultDiagnosticDetails
                };
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "streletz-bridge-test-" + Guid.NewGuid().ToString("N"));
            public readonly FakeDriver Driver = new FakeDriver();
            public readonly PanelProfileStore Profiles;
            public readonly Dictionary<string, object> Project;
            public readonly string PanelId;
            public readonly string Fingerprint;
            public readonly RegistrationCoordinator Coordinator;
            public long Clock = BridgeValues.NowMs();
            public int Saves, ProjectReads;
            public Action OnSave;
            public bool SaveSucceeds = true;

            public Fixture(bool unavailable = false, bool blockedJournal = false)
            {
                Directory.CreateDirectory(DirectoryPath);
                Profiles = new PanelProfileStore(Path.Combine(DirectoryPath, "panels.json"));
                PanelId = Profiles.Save(new PanelProfile { Name = "Test panel", Host = "192.168.0.240", Port = 6344, ModuleId = "FB0000000003" }, "");
                Project = MakeProject();
                Fingerprint = BridgeValues.ProjectFingerprint(Project);
                Driver.Available = !unavailable;
                Driver.Journal = Path.Combine(DirectoryPath, blockedJournal ? "blocked-directory" : "journal.json");
                if (blockedJournal) Directory.CreateDirectory(Driver.Journal);
                Coordinator = NewCoordinator(Driver.Journal, Driver);
            }

            public RegistrationCoordinator NewCoordinator(string journal, FakeDriver driver)
            {
                return new RegistrationCoordinator(Profiles, () => { Interlocked.Increment(ref ProjectReads); return BridgeValues.Copy(Project); },
                    (target, result) => { Interlocked.Increment(ref Saves); if (OnSave != null) OnSave(); return SaveSucceeds; },
                    driver, journal, ProtocolV3.JournalIdentityKey(Code), () => Clock);
            }

            public Dictionary<string, object> Request(string type)
            {
                return new Dictionary<string, object> { { "protocol", 3 }, { "type", type }, { "requestId", Guid.NewGuid().ToString("D") }, { "sentAt", Clock } };
            }

            public Dictionary<string, object> Begin()
            {
                Dictionary<string, object> request = Request("begin_registration");
                request["panelId"] = PanelId; request["projectFingerprint"] = Fingerprint;
                request["recordId"] = FirstRecord; request["expectedHardwareId"] = Sensor;
                request["deviceKey"] = "1234ABCD"; request["timeoutSeconds"] = 90;
                return request;
            }

            public Dictionary<string, object> Status(string operationId, bool cancel = false)
            {
                Dictionary<string, object> request = Request(cancel ? "cancel_registration" : "registration_status");
                request["panelId"] = PanelId; request["operationId"] = operationId;
                return Coordinator.Handle(request);
            }

            public Dictionary<string, object> WaitTerminal(string operationId)
            {
                Dictionary<string, object> operation = null;
                Assert(SpinWait.SpinUntil(() => {
                    operation = BridgeValues.Object(Status(operationId), "operation");
                    return BridgeValues.Flag(operation, "terminal");
                }, 5000), "operation did not finish");
                return operation;
            }

            public void Dispose()
            {
                Driver.Release.Set(); Coordinator.Dispose();
                SpinWait.SpinUntil(() => !Coordinator.HasActiveOperations, 3000);
                try { Directory.Delete(DirectoryPath, true); } catch { }
            }
        }

        private static Dictionary<string, object> MakeProject()
        {
            Func<string, Dictionary<string, object>> record = id => new Dictionary<string, object> {
                { "id", id }, { "recordKind", "device" }, { "name", "Test detector" }, { "initialized", "-" },
                { "commissioning", new Dictionary<string, object> { { "stage", 0 }, { "serial", "" }, { "deviceKey", "" } } },
                { "bfx", new Dictionary<string, object> { { "address", "3R.1.1" }, { "classType", "test" } } }
            };
            return new Dictionary<string, object> { { "schemaVersion", 1 }, { "siteName", "Test project" }, { "records", new object[] { record(FirstRecord), record(SecondRecord) } } };
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception(message);
        }
        private static string State(Dictionary<string, object> response) { return BridgeValues.Text(BridgeValues.Object(response, "operation"), "state"); }

        private static void CryptoRoundTripAndTamper()
        {
            using (Fixture f = new Fixture())
            {
                Dictionary<string, object> request = f.Begin();
                Dictionary<string, object> envelope = ProtocolV3.Encode(request, Code, "request");
                Dictionary<string, object> decoded = ProtocolV3.Decode(envelope, Code, "request");
                Assert(BridgeValues.Text(decoded, "requestId") == BridgeValues.Text(request, "requestId"), "request ID lost");
                Dictionary<string, object> response = ProtocolV3.Response(BridgeValues.Text(request, "requestId"), true, "checked");
                Dictionary<string, object> protectedResponse = ProtocolV3.Encode(response, Code, "response");
                Assert(BridgeValues.Flag(ProtocolV3.Decode(protectedResponse, Code, "response"), "ok"), "response authentication failed");
                protectedResponse["type"] = "bridge-request";
                Throws<UnauthorizedAccessException>(() => ProtocolV3.Decode(protectedResponse, Code, "request"), "response could be reflected as a request");
                byte[] changed = Convert.FromBase64String(BridgeValues.Text(envelope, "data")); changed[0] ^= 1;
                envelope["data"] = Convert.ToBase64String(changed);
                Throws<UnauthorizedAccessException>(() => ProtocolV3.Decode(envelope, Code, "request"), "tampered ciphertext accepted");
                envelope = ProtocolV3.Encode(request, Code, "request");
                Throws<UnauthorizedAccessException>(() => ProtocolV3.Decode(envelope, "FFFFFFFFFFFFFFFF", "request"), "wrong pairing code accepted");
                Throws<InvalidDataException>(() => ProtocolV3.Encode(request, "0000000000000000", "request"), "zero pairing code accepted");
            }
        }

        private static void ProjectFingerprintAndPanels()
        {
            using (Fixture f = new Fixture())
            {
                Assert(f.Fingerprint == "adeb9fafd4c5967d9d5a8b91503f5d2c9c4c87f55ac9e56037747e2a6f748747", "cross-language project fingerprint changed");
                Dictionary<string, object> response = f.Coordinator.Handle(f.Request("list_panels"));
                Assert(BridgeValues.Flag(response, "ok"), "list panels failed");
                Dictionary<string, object> panel = BridgeValues.Array(response, "panels")[0] as Dictionary<string, object>;
                Assert(BridgeValues.Text(panel, "panelId") == f.PanelId, "panel ID not stable");
                string publicText = BridgeJson.Serialize(response).ToLowerInvariant();
                Assert(!publicText.Contains("password") && !publicText.Contains("md5") && !publicText.Contains("dpapi"), "secret fields leaked to phone");
                Assert(f.Profiles.Get(f.PanelId).PasswordMd5 == null, "native Master profile requires an unnecessary password");
                Dictionary<string, object> request = f.Begin(); request.Remove("panelId");
                Assert(!BridgeValues.Flag(f.Coordinator.Handle(request), "ok") && f.Driver.Calls == 0, "missing panel silently fell back to a default");
            }
        }

        private static void IdempotencyAndJournal()
        {
            using (Fixture f = new Fixture())
            {
                Dictionary<string, object> request = f.Begin();
                string id = BridgeValues.Text(request, "requestId");
                Assert(BridgeValues.Flag(f.Coordinator.Handle(request), "ok"), "begin rejected");
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                Assert(f.Driver.JournalSeenBeforeHardware, "hardware ran before durable journal");
                Assert(BridgeValues.Flag(f.Coordinator.Handle(BridgeValues.Copy(request)), "ok"), "identical retry rejected");
                Assert(f.Driver.Calls == 1, "identical retry ran hardware twice");
                Dictionary<string, object> conflict = BridgeValues.Copy(request); conflict["expectedHardwareId"] = "FA0000000002";
                Dictionary<string, object> conflictResponse = f.Coordinator.Handle(conflict);
                Assert(BridgeValues.Text(conflictResponse, "errorCode") == "request_conflict", "request-ID conflict accepted");
                Assert(BridgeValues.Object(conflictResponse, "operation") != null, "known operation missing from conflict response");
                f.Driver.Release.Set();
                Dictionary<string, object> terminal = f.WaitTerminal(id);
                Assert(BridgeValues.Text(terminal, "state") == "registered" && BridgeValues.Flag(terminal, "registrationConfirmed"), "confirmed success not returned");
                Assert(BridgeValues.Text(terminal, "confirmedHardwareId") == Sensor, "actual hardware ID missing");
                Assert(BridgeValues.Flag(terminal, "projectSaved") && f.Saves == 1, "confirmed project not saved exactly once");
                Assert(State(f.Coordinator.Handle(request)) == "registered" && f.Driver.Calls == 1, "terminal retry re-ran driver");
            }
        }

        private static void BusyAndProfileGuards()
        {
            using (Fixture f = new Fixture())
            {
                Dictionary<string, object> first = f.Begin(); f.Coordinator.Handle(first);
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                Dictionary<string, object> second = f.Begin(); second["recordId"] = SecondRecord; second["expectedHardwareId"] = "FA0000000002";
                Assert(BridgeValues.Text(f.Coordinator.Handle(second), "errorCode") == "panel_busy", "parallel operation on one panel accepted");
                Throws<InvalidOperationException>(() => f.Coordinator.RunPanelMutation(f.PanelId, () => f.Profiles.Delete(f.PanelId)), "active panel could be deleted");
                Throws<InvalidOperationException>(() => f.Coordinator.RunProjectMutation(() => { }), "active project could be replaced");
                Dictionary<string, object> wrongPanel = f.Request("registration_status");
                wrongPanel["operationId"] = BridgeValues.Text(first, "requestId"); wrongPanel["panelId"] = Guid.NewGuid().ToString("D");
                Assert(BridgeValues.Text(f.Coordinator.Handle(wrongPanel), "errorCode") == "panel_mismatch", "status crossed panel identity");
            }
        }

        private static void BadInputNeverRunsHardware()
        {
            using (Fixture f = new Fixture())
            {
                Dictionary<string, object> stale = f.Begin(); stale["sentAt"] = f.Clock - 300001;
                Assert(BridgeValues.Text(f.Coordinator.Handle(stale), "errorCode") == "stale_request", "stale request accepted");
                Dictionary<string, object> wrongProject = f.Begin(); wrongProject["projectFingerprint"] = new string('a', 64);
                Assert(BridgeValues.Text(f.Coordinator.Handle(wrongProject), "errorCode") == "project_mismatch", "wrong project accepted");
                Dictionary<string, object> wrongRecord = f.Begin(); wrongRecord["recordId"] = Guid.NewGuid().ToString("D");
                Assert(BridgeValues.Text(f.Coordinator.Handle(wrongRecord), "errorCode") == "record_missing", "unknown device accepted");
                Dictionary<string, object> wrongKey = f.Begin(); wrongKey["deviceKey"] = "1234567890ABCDEF";
                Assert(!BridgeValues.Flag(f.Coordinator.Handle(wrongKey), "ok"), "non-4-byte registration KEY accepted");
                Assert(f.Driver.Calls == 0, "invalid request reached hardware");
            }
        }

        private static void RejectUnconfirmedSuccess(bool confirmed, string hardware, string evidence)
        {
            using (Fixture f = new Fixture())
            {
                f.Driver.ConfirmRegistration = confirmed; f.Driver.ResultHardwareId = hardware; f.Driver.ResultEvidence = evidence;
                Dictionary<string, object> request = f.Begin(); f.Coordinator.Handle(request); f.Driver.Release.Set();
                Dictionary<string, object> result = f.WaitTerminal(BridgeValues.Text(request, "requestId"));
                Assert(BridgeValues.Text(result, "state") == "unknown" && !BridgeValues.Flag(result, "registrationConfirmed"), "unverified success reported as registered");
                Assert(String.IsNullOrEmpty(BridgeValues.Text(result, "confirmedHardwareId")) && f.Saves == 0, "unverified success changed project");
            }
        }

        private static void Cancellation(bool acknowledged)
        {
            using (Fixture f = new Fixture())
            {
                f.Driver.ConfirmStop = acknowledged;
                Dictionary<string, object> request = f.Begin(); string id = BridgeValues.Text(request, "requestId");
                f.Coordinator.Handle(request); Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                f.Status(id, true); Dictionary<string, object> result = f.WaitTerminal(id);
                Assert(BridgeValues.Text(result, "state") == (acknowledged ? "cancelled" : "unknown"), "cancellation result was misrepresented");
                Assert(!BridgeValues.Flag(result, "registrationConfirmed") && f.Saves == 0, "cancelled operation changed initialization stage");
            }
        }

        private static void TimeoutAndBusyAfterUnconfirmedStop(bool ignoreCancellation)
        {
            using (Fixture f = new Fixture())
            {
                f.Driver.IgnoreCancellation = ignoreCancellation;
                Dictionary<string, object> request = f.Begin(); string id = BridgeValues.Text(request, "requestId");
                f.Coordinator.Handle(request); Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                f.Clock += 91000; f.Coordinator.CheckDeadlines();
                if (ignoreCancellation)
                {
                    f.Clock += 11000; f.Coordinator.CheckDeadlines();
                    Assert(State(f.Status(id)) == "unknown" && f.Coordinator.HasActiveOperations, "unresponsive driver released hardware slot");
                    Assert(BridgeValues.Text(f.Coordinator.Handle(f.Begin()), "errorCode") == "panel_busy", "new operation started before old worker stopped");
                }
                else Assert(BridgeValues.Text(f.WaitTerminal(id), "state") == "timed_out", "confirmed deadline stop not reported as timed_out");
            }
        }

        private static void RestartDoesNotReplay()
        {
            using (Fixture f = new Fixture())
            {
                Dictionary<string, object> request = f.Begin(); f.Coordinator.Handle(request);
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                string recoveredPath = Path.Combine(f.DirectoryPath, "recovered-journal.json");
                File.Copy(f.Driver.Journal, recoveredPath);
                FakeDriver secondDriver = new FakeDriver { Journal = recoveredPath };
                using (RegistrationCoordinator recovered = f.NewCoordinator(recoveredPath, secondDriver))
                {
                    Dictionary<string, object> response = recovered.Handle(request);
                    Assert(State(response) == "unknown", "restart guessed unfinished result");
                    Assert(secondDriver.Calls == 0 && !recovered.HasActiveOperations, "restart reissued physical operation");
                }
            }
        }

        private static void StorageAndAvailability()
        {
            using (Fixture f = new Fixture(blockedJournal: true))
            {
                Dictionary<string, object> response = f.Coordinator.Handle(f.Begin());
                Assert(BridgeValues.Text(response, "errorCode") == "journal_unavailable" && f.Driver.Calls == 0, "journal failure still activated hardware");
            }
            using (Fixture f = new Fixture(unavailable: true))
            {
                Dictionary<string, object> response = f.Coordinator.Handle(f.Begin());
                Assert(BridgeValues.Text(response, "errorCode") == "driver_unavailable" && f.Driver.Calls == 0, "unavailable driver reported success");
            }
            using (Fixture f = new Fixture())
            {
                f.SaveSucceeds = false;
                Dictionary<string, object> request = f.Begin(); f.Coordinator.Handle(request); f.Driver.Release.Set();
                Dictionary<string, object> result = f.WaitTerminal(BridgeValues.Text(request, "requestId"));
                Assert(BridgeValues.Flag(result, "registrationConfirmed") && !BridgeValues.Flag(result, "projectSaved"), "physical success and save failure were conflated");
            }
        }

        private static void AcceptedFlagDoesNotHideUnknownEffects()
        {
            using (Fixture f = new Fixture(unavailable: true))
            {
                Dictionary<string, object> rejection = f.Coordinator.Handle(f.Begin());
                Assert(rejection.ContainsKey("accepted") && !BridgeValues.Flag(rejection, "accepted") && !rejection.ContainsKey("operation"), "proven pre-dispatch rejection was not explicit");
                Assert(f.Driver.Calls == 0, "rejected request reached hardware");
            }
            using (Fixture f = new Fixture(blockedJournal: true))
            {
                Dictionary<string, object> rejection = f.Coordinator.Handle(f.Begin());
                Assert(rejection.ContainsKey("accepted") && !BridgeValues.Flag(rejection, "accepted") && f.Driver.Calls == 0, "journal refusal did not prove no dispatch");
            }
            using (Fixture f = new Fixture())
            {
                f.Driver.ThrowAvailability = true;
                Dictionary<string, object> error = f.Coordinator.Handle(f.Begin());
                Assert(BridgeValues.Text(error, "errorCode") == "bridge_error", "unexpected error was hidden");
                Assert(!error.ContainsKey("accepted") || BridgeValues.Flag(error, "accepted"), "unexpected exception falsely proved no dispatch");
                f.Driver.ThrowAvailability = false;
                Dictionary<string, object> missing = f.Status(Guid.NewGuid().ToString("D"));
                Assert(!missing.ContainsKey("accepted") || BridgeValues.Flag(missing, "accepted"), "missing status record falsely proved no effect");

                Dictionary<string, object> request = f.Begin();
                string id = BridgeValues.Text(request, "requestId");
                Assert(BridgeValues.Flag(f.Coordinator.Handle(request), "accepted"), "begin acceptance missing");
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "driver did not start");
                Dictionary<string, object> staleRetry = BridgeValues.Copy(request); staleRetry["sentAt"] = f.Clock - 300001;
                Dictionary<string, object> staleError = f.Coordinator.Handle(staleRetry);
                Assert(BridgeValues.Flag(staleError, "accepted") && BridgeValues.Object(staleError, "operation") != null, "rejected retry hid an existing physical operation");

                Dictionary<string, object> differentPanel = BridgeValues.Copy(request); differentPanel["panelId"] = Guid.NewGuid().ToString("D");
                Dictionary<string, object> conflict = f.Coordinator.Handle(differentPanel);
                Assert(BridgeValues.Flag(conflict, "accepted"), "panel-conflicting retry falsely reported not accepted");
                Assert(BridgeValues.Text(BridgeValues.Object(conflict, "operation"), "panelId") == f.PanelId, "known operation identity was discarded");
                Assert(f.Driver.Calls == 1, "error handling replayed hardware");
            }
        }

        // Exercises the production driver with a contract-only local transport.
        // No native application, named pipe, or panel is opened by these tests.
        private sealed class NativeHarness
        {
            internal const string NativeProtocol = "streletz-master-plugin-v1";
            internal const string NativePanelId = "33333333-3333-4333-8333-333333333333";
            internal readonly string NativeFingerprint = new string('f', 64);
            internal readonly PanelProfile Profile = new PanelProfile {
                Id = "44444444-4444-4444-8444-444444444444", Name = "Test native panel",
                Host = "192.168.0.240", Port = 6344, ModuleId = "FB0000000003"
            };
            internal readonly RegistrationTarget Target = new RegistrationTarget {
                OperationId = "55555555-5555-4555-8555-555555555555", ProjectFingerprint = new string('a', 64),
                RecordId = FirstRecord, ExpectedHardwareId = Sensor, DeviceKey = "1234ABCD", TimeoutSeconds = 90,
                RecordSnapshot = new Dictionary<string, object>()
            };
            internal readonly List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
            internal readonly List<string> Progress = new List<string>();
            internal readonly Dictionary<string, object> Hello = new Dictionary<string, object> { { "ready", true }, { "version", "12.4" } };
            internal object[] Panels;
            internal Func<string, Dictionary<string, object>> OperationReply;
            internal readonly PanelRegistrationDriver Driver;

            internal NativeHarness()
            {
                Panels = new object[] { new Dictionary<string, object> {
                    { "nativeNodeGuid", NativePanelId }, { "host", Profile.Host }, { "port", Profile.Port },
                    { "moduleId", Profile.ModuleId }, { "recordIds", new object[] { FirstRecord } }
                } };
                Driver = new PanelRegistrationDriver(Exchange, 0);
            }

            internal static Dictionary<string, object> Reply(Dictionary<string, object> data)
            { return new Dictionary<string, object> { { "protocol", NativeProtocol }, { "ok", true }, { "data", data } }; }

            internal static Dictionary<string, object> Error(string code)
            { return new Dictionary<string, object> { { "protocol", NativeProtocol }, { "ok", false }, { "error", new Dictionary<string, object> { { "code", code } } } }; }

            internal Dictionary<string, object> Snapshot(string status)
            {
                bool confirmed = status == "registered";
                return new Dictionary<string, object> {
                    { "operationId", Target.OperationId }, { "status", status }, { "recordId", Target.RecordId },
                    { "projectFingerprint", Target.ProjectFingerprint }, { "nativeProjectFingerprint", NativeFingerprint },
                    { "panelId", Profile.Id }, { "nativePanelId", NativePanelId }, { "host", Profile.Host },
                    { "port", Profile.Port }, { "moduleId", Profile.ModuleId }, { "expectedHardwareId", Target.ExpectedHardwareId },
                    { "confirmedHardwareId", confirmed ? Sensor : null }, { "registrationConfirmed", confirmed },
                    { "confirmationSent", confirmed }, { "nativeSuccessObserved", confirmed },
                    { "nativeRunFinished", true }, { "dialogClosed", true },
                    { "validatedBy", "native-fingerprint-record-and-panel" }, { "finishedUtc", "2026-01-01T00:00:00.000Z" }
                };
            }

            private Dictionary<string, object> Exchange(Dictionary<string, object> request, int timeout)
            {
                Assert(timeout > 0, "unbounded native transport timeout");
                Calls.Add(BridgeValues.Copy(request));
                string type = BridgeValues.Text(request, "type");
                if (type == "hello") return Reply(Hello);
                if (type == "list_panels") return Reply(new Dictionary<string, object> { { "nativeProjectFingerprint", NativeFingerprint }, { "panels", Panels } });
                Assert(type == "start" || type == "status" || type == "cancel", "unexpected native operation type");
                Assert(BridgeValues.Text(request, "operationId") == Target.OperationId, "operation ID changed between native requests");
                return OperationReply == null ? Reply(Snapshot("registered")) : OperationReply(type);
            }

            internal int Count(string type) { return Calls.Count(call => BridgeValues.Text(call, "type") == type); }
            internal RegistrationDriverResult Run(CancellationToken token = default(CancellationToken))
            { return Driver.Register(Profile, Target, token, p => Progress.Add(p.State)); }
        }

        private static void NativeRoutingAndSelection()
        {
            var good = new NativeHarness();
            RegistrationDriverResult result = good.Run();
            Assert(result.State == "registered" && result.RegistrationConfirmed && result.HardwareId == Sensor, "native confirmed result lost");
            Assert(good.Count("start") == 1, "registration did not send exactly one native start");
            Dictionary<string, object> start = good.Calls.Single(call => BridgeValues.Text(call, "type") == "start");
            Dictionary<string, object> panel = BridgeValues.Object(start, "panel");
            Assert(BridgeValues.Text(panel, "panelId") == good.Profile.Id && BridgeValues.Text(panel, "nativePanelId") == NativeHarness.NativePanelId, "bridge panel was confused with native panel");
            Assert(BridgeValues.Text(panel, "host") == good.Profile.Host && BridgeValues.Number(panel, "port") == good.Profile.Port && BridgeValues.Text(panel, "moduleId") == good.Profile.ModuleId, "native route changed");
            Assert(BridgeValues.Text(start, "projectFingerprint") == good.Target.ProjectFingerprint && BridgeValues.Text(start, "nativeProjectFingerprint") == good.NativeFingerprint, "fingerprint namespace was conflated");
            Assert(BridgeValues.Text(start, "deviceKey") == good.Target.DeviceKey && BridgeValues.Text(start, "recordId") == FirstRecord, "selected device parameters were not forwarded");
            Assert(BridgeValues.Text(BridgeJson.ParseObject(result.Evidence), "hardwareId") == Sensor, "evidence did not retain actual confirmed ID");

            var mismatches = new Dictionary<string, object> {
                { "host", "192.168.0.241" }, { "port", 6345 }, { "moduleId", "FB0000000002" },
                { "recordIds", new object[] { SecondRecord } }, { "nativeNodeGuid", "not-a-guid" }
            };
            foreach (KeyValuePair<string, object> mismatch in mismatches)
            {
                var bad = new NativeHarness();
                ((Dictionary<string, object>)bad.Panels[0])[mismatch.Key] = mismatch.Value;
                result = bad.Run();
                Assert(result.State == "failed" && bad.Count("start") == 0, "route mismatch reached native start: " + mismatch.Key);
            }
            var ambiguous = new NativeHarness(); ambiguous.Panels = new object[] { ambiguous.Panels[0], ambiguous.Panels[0] };
            Assert(ambiguous.Run().State == "failed" && ambiguous.Count("start") == 0, "ambiguous panel route was guessed");
            var bound = new NativeHarness();
            bound.Profile.NativePanelId = NativeHarness.NativePanelId;
            bound.Profile.SegmentId = "b2222222-2222-4222-8222-000000000001";
            ((Dictionary<string, object>)bound.Panels[0])["segmentGuid"] = bound.Profile.SegmentId;
            Assert(bound.Run().RegistrationConfirmed, "automatic native identity binding rejected the matching route");
            var wrongNative = new NativeHarness();
            wrongNative.Profile.NativePanelId = "33333333-3333-4333-8333-333333333339";
            Assert(wrongNative.Run().State == "failed" && wrongNative.Count("start") == 0, "automatic profile fell back to a different native panel on the same MK-IP");
            var wrongSegment = new NativeHarness();
            wrongSegment.Profile.SegmentId = "b2222222-2222-4222-8222-000000000002";
            ((Dictionary<string, object>)wrongSegment.Panels[0])["segmentGuid"] = "b2222222-2222-4222-8222-000000000001";
            Assert(wrongSegment.Run().State == "failed" && wrongSegment.Count("start") == 0, "automatic profile fell back to a different project segment");
        }

        private static void NativeLostStartNeverReplays()
        {
            foreach (string mode in new[] { "lost_reply", "native_outcome_unknown", "native_internal_error", "native_adapter_mismatch", "wrong_protocol" })
            {
                var native = new NativeHarness();
                native.OperationReply = type => {
                    if (type != "start") return NativeHarness.Reply(native.Snapshot("registered"));
                    if (mode == "lost_reply") throw new IOException("test reply lost after dispatch");
                    if (mode == "wrong_protocol") return new Dictionary<string, object> { { "protocol", "old-plugin" }, { "ok", true } };
                    return NativeHarness.Error(mode);
                };
                RegistrationDriverResult result = native.Run();
                Assert(result.State == "registered" && native.Count("start") == 1 && native.Count("status") == 1, "uncertain start did not recover by status only: " + mode);
            }
            var lost = new NativeHarness();
            lost.OperationReply = type => { throw new IOException("test transport lost"); };
            Assert(lost.Run().State == "unknown" && lost.Count("start") == 1 && lost.Count("status") == 3, "communication loss guessed a result or replayed start");
        }

        private static void NativeOldOperationNeverConfirms()
        {
            foreach (string code in new[] { "operation_id_conflict", "operation_id_retired" })
            {
                var native = new NativeHarness();
                native.OperationReply = type => type == "start" ? NativeHarness.Error(code) : NativeHarness.Reply(native.Snapshot("registered"));
                RegistrationDriverResult result = native.Run();
                Assert(result.State == "unknown" && !result.RegistrationConfirmed && native.Count("start") == 1 && native.Count("status") == 0, "old/conflicting operation was treated as new failure or success: " + code);
            }
            foreach (string code in new[] { "operation_not_found", "operation_result_expired", "operation_history_expired", "operation_id_conflict", "operation_id_retired" })
            {
                var native = new NativeHarness();
                native.OperationReply = type => {
                    if (type == "start") throw new IOException("test lost start reply");
                    return NativeHarness.Error(code);
                };
                Assert(native.Run().State == "unknown" && native.Count("start") == 1 && native.Count("status") == 1, "lost operation record was reported definitively failed: " + code);
            }
            foreach (string code in new[] { "native_ui_busy", "native_registration_busy", "native_dialog_busy",
                "native_result_closing", "native_result_acknowledgement_blocked", "native_result_mode_unavailable", "panel_mismatch", "native_project_changed" })
            {
                var native = new NativeHarness(); native.OperationReply = type => NativeHarness.Error(code);
                RegistrationDriverResult result = native.Run();
                Assert(result.State == "failed" && result.StopConfirmed && !result.RegistrationConfirmed &&
                    native.Count("start") == 1 && native.Count("status") == 0 && native.Count("cancel") == 0,
                    "proven native pre-dispatch rejection was not definitive or replayed a command: " + code);
            }
        }

        private static void NativeRegistrationFailureDiagnostics()
        {
            const string privateText = "SYNTHETIC_NATIVE_SECRET_MUST_NOT_BE_LOGGED";
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (string code in new[] { "record_not_available", "panel_mismatch", "native_adapter_mismatch",
                "native_registration_busy", "native_dialog_busy", "native_result_closing", "native_result_acknowledgement_blocked", "native_result_mode_unavailable" })
            {
                var native = new NativeHarness();
                string phase = code == "native_adapter_mismatch" ? "native_operation" : "native_start";
                bool previousSuccess = code == "native_result_closing" || code == "native_result_acknowledgement_blocked";
                string completionState = code == "native_result_closing" ? "waiting" : "blocked";
                string completionCheck = code == "native_result_closing" ? "awaiting_success_window" : "success_window_unverified";
                if (previousSuccess)
                {
                    native.Hello["capabilities"] = new Dictionary<string, object> { { "autoConfirmSuccessResult", true },
                        { "suppressNativeSuccessDialog", true } };
                    native.Hello["diagnostics"] = new Dictionary<string, object> {
                        { "nativeCompletionState", completionState }, { "nativeCompletionCheck", completionCheck },
                        { "nativeCompletionLastCheck", "success_window_not_found" }, { "nativeSuccessDialogSuppressed", false },
                        { "windowText", privateText }, { "windowHandle", privateText }, { "deviceKey", privateText }
                    };
                }
                native.OperationReply = type => {
                    if (code != "native_adapter_mismatch")
                    {
                        var failure = NativeHarness.Error(code);
                        BridgeValues.Object(failure, "error")["message"] = privateText;
                        return failure;
                    }
                    var stopped = native.Snapshot("failed");
                    stopped["error"] = new Dictionary<string, object> { { "code", code }, { "message", privateText } };
                    stopped["adapterDiagnostic"] = new Dictionary<string, object> {
                        { "check", "transport_usb" }, { "actualConnectionKind", "usb" },
                        { "rawEndpoint", privateText }, { "deviceKey", privateText }
                    };
                    return NativeHarness.Reply(stopped);
                };
                RegistrationDriverResult result = native.Run();
                Assert(result.State == "failed" && result.StopConfirmed && !result.RegistrationConfirmed,
                    "diagnostics changed a proven failure's registration semantics: " + code);
                Assert(result.ErrorCode == code && result.ErrorPhase == phase,
                    "precise native failure code/phase was discarded: " + code);
                Assert(result.Message.Contains(code) && result.Message.Contains(phase),
                    "installed Android 0.5.5 cannot display the precise failure: " + code);
                Assert(!result.Message.Contains("Запись датчика или подключение не соответствует выбранной панели"),
                    "different native failures still use the old ambiguous message: " + code);
                Assert(!result.Message.Contains(privateText), "raw native error text leaked to the phone");
                if (code == "native_adapter_mismatch")
                    Assert(result.Message.Contains("USB") && BridgeValues.Text(result.DiagnosticDetails, "check") == "transport_usb",
                        "verified native USB diagnostic was lost or still described as record ownership");
                if (previousSuccess)
                {
                    Assert(result.Message.Contains("Новое задание не запускалось") &&
                        !result.Message.Contains("нажмите «ОК»") && String.IsNullOrEmpty(result.Evidence) &&
                        String.IsNullOrEmpty(result.HardwareId),
                        "previous successful native cleanup was confused with current registration evidence: " + code);
                    Assert(BridgeValues.Text(result.DiagnosticDetails, "nativeCompletionState") == completionState &&
                        BridgeValues.Text(result.DiagnosticDetails, "nativeCompletionCheck") == completionCheck &&
                        BridgeValues.Text(result.DiagnosticDetails, "nativeCompletionLastCheck") == "success_window_not_found" &&
                        result.DiagnosticDetails.ContainsKey("nativeSuccessDialogSuppressed") &&
                        !BridgeValues.Flag(result.DiagnosticDetails, "nativeSuccessDialogSuppressed") &&
                        BridgeValues.Flag(result.DiagnosticDetails, "suppressNativeSuccessDialog") &&
                        BridgeValues.Flag(result.DiagnosticDetails, "autoConfirmSuccessResult"),
                        "reviewed previous-window cleanup diagnostics were discarded: " + code);
                    Assert(code == "native_result_closing"
                        ? result.Message.Contains("Предыдущий датчик зарегистрирован") && result.Message.Contains("Подождите несколько секунд") &&
                            result.Message.Contains("снова нажмите") && result.Message.Contains("автоматического повтора нет")
                        : result.Message.Contains("Предыдущая регистрация завершилась успешно") && result.Message.Contains("Сохраните диагностику"),
                        "closing/blocked cause did not produce distinct actionable phone guidance: " + code);
                }
                if (code == "native_registration_busy" || code == "native_dialog_busy")
                    Assert(!result.Message.Contains("нажмите «ОК»"), "generic native busy still requires acknowledging every success on the PC");
                messages.Add(result.Message);
                Assert(native.Count("start") == 1 && native.Count("status") == 0 && native.Count("cancel") == 0,
                    "diagnostics replayed or polled a proven finished failure: " + code);
                string report = native.Driver.LastDiagnosticReport;
                Assert(report.Contains(code) && report.Contains(phase), "registration failure missing from the saved report");
                Assert(!report.Contains(privateText) && !report.Contains(native.Target.DeviceKey), "secret entered registration diagnostics");
                if (previousSuccess)
                    Assert(report.Contains("Автоматическое подтверждение успешного результата: да") &&
                        report.Contains("Состояние завершения окна результата: " + completionState) &&
                        report.Contains("Проверка завершения окна результата: " + completionCheck) &&
                        report.Contains("Последняя проверка до завершения ожидания: success_window_not_found"),
                        "known cleanup capability/state/check missing from connection diagnostics");
            }
            Assert(messages.Count == 8, "distinct native refusal causes have indistinguishable messages");

            foreach (string known in new[] { "native_result_closing", "native_result_acknowledgement_blocked" })
            {
                var injected = new NativeHarness();
                injected.Hello["capabilities"] = new Dictionary<string, object> { { "autoConfirmSuccessResult", true } };
                injected.Hello["diagnostics"] = new Dictionary<string, object> {
                    { "nativeCompletionState", "completed" }, { "nativeCompletionCheck", "native_completion_finished" }
                };
                string malformedCode = known + "\n" + privateText;
                injected.OperationReply = type => {
                    if (type == "start") return NativeHarness.Error(malformedCode);
                    var state = injected.Snapshot("unknown");
                    state["confirmationSent"] = true;
                    state["error"] = new Dictionary<string, object> { { "code", malformedCode }, { "message", privateText } };
                    return NativeHarness.Reply(state);
                };
                var unrecognized = injected.Run();
                Assert(unrecognized.State == "unknown" && !unrecognized.StopConfirmed && !unrecognized.RegistrationConfirmed &&
                    unrecognized.ErrorCode == "unrecognized_native_error", "a malformed completion code falsely proved safe pre-dispatch refusal");
                Assert(injected.Count("start") == 1 && injected.Count("status") == 1 && injected.Count("cancel") == 0 &&
                    !unrecognized.Message.Contains(privateText) && !injected.Driver.LastDiagnosticReport.Contains(privateText),
                    "malformed completion code replayed registration, cancelled another operation or leaked native text");
            }

            const string expectedNode = "41111111-1111-4111-8111-111111111111";
            const string otherOwner = "42222222-2222-4222-8222-222222222222";
            foreach (string check in new[] { "endpoint_owner_missing", "endpoint_owner_mismatch" })
            {
                var native = new NativeHarness();
                native.OperationReply = type => {
                    var state = native.Snapshot("failed");
                    state["error"] = new Dictionary<string, object> { { "code", "native_adapter_mismatch" } };
                    state["adapterDiagnostic"] = new Dictionary<string, object> {
                        { "check", check }, { "actualConnectionKind", "direct_mk_ip" },
                        { "expectedAdapterNodeId", expectedNode },
                        { "actualAdapterOwnerId", check == "endpoint_owner_mismatch" ? otherOwner : "" }
                    };
                    return NativeHarness.Reply(state);
                };
                var failure = native.Run();
                Assert(failure.State == "failed" && failure.StopConfirmed && !failure.RegistrationConfirmed &&
                    native.Count("start") == 1 && native.Count("status") == 0,
                    "owner diagnostics changed a completed unconfirmed operation or replayed start");
                Assert(failure.Message.Contains(check) && BridgeValues.Text(failure.DiagnosticDetails, "check") == check &&
                    BridgeValues.Text(failure.DiagnosticDetails, "expectedAdapterNodeId") == expectedNode,
                    "the exact adapter-owner rejection did not reach the installed Android's message and details");
                Assert(check == "endpoint_owner_mismatch"
                    ? BridgeValues.Text(failure.DiagnosticDetails, "actualAdapterOwnerId") == otherOwner
                    : !failure.DiagnosticDetails.ContainsKey("actualAdapterOwnerId"),
                    "a known owner was lost or a missing owner was invented");
            }
            var malformedOwners = RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                { "expectedAdapterNodeId", privateText }, { "actualAdapterOwnerId", Guid.Empty.ToString("D") },
                { "expectedRouteOwnerId", Guid.Empty.ToString("D") }, { "actualRouteOwnerId", privateText }
            });
            Assert(!malformedOwners.ContainsKey("expectedAdapterNodeId") && !malformedOwners.ContainsKey("actualAdapterOwnerId") &&
                !malformedOwners.ContainsKey("expectedRouteOwnerId") && !malformedOwners.ContainsKey("actualRouteOwnerId"),
                "malformed or empty adapter GUIDs entered persistent diagnostics");
            foreach (string check in new[] { "terminal_interface_owner_missing", "terminal_interface_owner_mismatch",
                "terminal_interface_tunnel_missing", "terminal_interface_tunnel_mismatch", "terminal_interface_parse_failed" })
            {
                var detail = RegistrationDiagnosticData.Details(new Dictionary<string, object> { { "check", check } });
                Assert(BridgeValues.Text(detail, "check") == check, "the exact terminal interface rejection was discarded: " + check);
            }

            var untrusted = new NativeHarness();
            untrusted.OperationReply = type => {
                var state = untrusted.Snapshot("failed");
                state["error"] = new Dictionary<string, object> { { "code", privateText }, { "message", privateText } };
                state["adapterDiagnostic"] = new Dictionary<string, object> { { "rawEndpoint", privateText }, { "password", privateText } };
                return NativeHarness.Reply(state);
            };
            RegistrationDriverResult rejected = untrusted.Run();
            Assert(rejected.State == "failed" && rejected.StopConfirmed && !rejected.RegistrationConfirmed,
                "unrecognized diagnostic payload changed stopped/unconfirmed outcome");
            Assert(rejected.ErrorCode == "unrecognized_native_error", "untrusted native error code was not allowlisted");
            Assert(!rejected.Message.Contains(privateText) && !untrusted.Driver.LastDiagnosticReport.Contains(privateText),
                "untrusted native diagnostic text leaked into message/report");

            var uncertain = new NativeHarness();
            uncertain.OperationReply = type => {
                var state = uncertain.Snapshot("unknown");
                state["confirmationSent"] = true;
                state["error"] = new Dictionary<string, object> { { "code", "native_result_project_changed" }, { "message", privateText } };
                return NativeHarness.Reply(state);
            };
            RegistrationDriverResult unknown = uncertain.Run();
            Assert(unknown.State == "unknown" && !unknown.StopConfirmed && !unknown.RegistrationConfirmed &&
                unknown.ErrorCode == "native_result_project_changed" && unknown.ErrorPhase == "native_operation",
                "adding a diagnostic converted an unconfirmed post-dispatch outcome into a safe-to-retry failure");
            Assert(uncertain.Count("start") == 1 && uncertain.Count("status") == 0 && uncertain.Count("cancel") == 0 &&
                !unknown.Message.Contains(privateText), "post-dispatch diagnostics replayed hardware or leaked native text");
        }

        private static void MissingModuleDiagnosticsNeverImplyRegistration()
        {
            const string secret = "SYNTHETIC_MODULE_SECRET_MUST_NOT_SURVIVE";
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (string check in new[] { "endpoint_module_missing", "endpoint_module_invalid", "endpoint_module_mismatch", "endpoint_native_route_mismatch" })
            {
                foreach (bool confirmationSent in new[] { false, true })
                {
                    var native = new NativeHarness();
                    native.OperationReply = type => {
                        var state = native.Snapshot(confirmationSent ? "unknown" : "failed");
                        state["confirmationSent"] = confirmationSent;
                        state["error"] = new Dictionary<string, object> { { "code", "native_adapter_mismatch" }, { "message", secret } };
                        state["adapterDiagnostic"] = new Dictionary<string, object> {
                            { "check", check }, { "actualModuleIdState", check == "endpoint_module_invalid" ? "invalid" : "missing" },
                            { "moduleIdentitySource", "project_adapter_and_native_route" }, { "projectModuleId", "fb0000000003" },
                            { "nativeRouteCacheMatched", true }, { "nativeCloudEnabled", false },
                            { "nativeDirectEnabled", true }, { "nativeRoutePasswordMatched", true },
                            { "actualModuleId", "" }, { "rawSerialNumber", secret }, { "rawEndpoint", secret }, { "password", secret }
                        };
                        return NativeHarness.Reply(state);
                    };
                    RegistrationDriverResult result = native.Run();
                    Assert(result.State == (confirmationSent ? "unknown" : "failed") && !result.RegistrationConfirmed &&
                        result.StopConfirmed == !confirmationSent, "module route evidence changed a refusal or unknown into confirmed registration");
                    Assert(result.ErrorCode == "native_adapter_mismatch" && result.ErrorPhase == "native_operation" &&
                        BridgeValues.Text(result.DiagnosticDetails, "check") == check,
                        "exact missing/invalid module rejection was lost");
                    Assert(BridgeValues.Text(result.DiagnosticDetails, "projectModuleId") == "FB0000000003" &&
                        !result.DiagnosticDetails.ContainsKey("actualModuleId"), "project module ID was fabricated as an observed endpoint ID");
                    Assert(native.Count("start") == 1 && native.Count("status") == 0 && native.Count("cancel") == 0 &&
                        !BridgeJson.Serialize(result.DiagnosticDetails).Contains(secret), "module diagnostics replayed registration or disclosed raw connection data");
                    if (!confirmationSent)
                    {
                        Assert(result.Message.Contains("МК-IP") && result.Message.Contains(check), "specific module failure is not readable by existing Android");
                        messages.Add(result.Message);
                    }
                }
            }
            Assert(messages.Count == 4, "missing, invalid, mismatching and unverified native-route failures have indistinguishable messages");
            foreach (string state in new[] { "present", "missing", "invalid" })
                Assert(BridgeValues.Text(RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                    { "actualModuleIdState", state } }), "actualModuleIdState") == state, "reviewed module-state enum was lost");
            foreach (string source in new[] { "endpoint_and_project", "project_adapter_and_native_route" })
                Assert(BridgeValues.Text(RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                    { "moduleIdentitySource", source } }), "moduleIdentitySource") == source, "reviewed module evidence source was lost");
            var bad = RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                { "actualModuleIdState", secret }, { "moduleIdentitySource", new Dictionary<string, object> { { "secret", secret } } },
                { "projectModuleId", secret }, { "nativeRouteCacheMatched", "true" }, { "nativeCloudEnabled", 0 },
                { "nativeDirectEnabled", null }, { "nativeRoutePasswordMatched", secret }, { "rawSerialNumber", secret }
            });
            Assert(bad.Count == 0, "unreviewed enum text, non-boolean evidence or raw ID entered diagnostics");
        }

        private static void RegistrationDiagnosticsSurviveJournalRestart()
        {
            using (Fixture f = new Fixture())
            {
                f.Driver.ResultState = "failed";
                f.Driver.ConfirmRegistration = false;
                f.Driver.ResultErrorCode = "native_adapter_mismatch";
                f.Driver.ResultErrorPhase = "native_operation";
                f.Driver.ResultMessage = "Подключение штатного окна не совпало [native_adapter_mismatch; native_operation].";
                const string secret = "SYNTHETIC_PRIVATE_ROUTE_MUST_NOT_SURVIVE";
                const string expectedAdapter = "41111111-1111-4111-8111-111111111111";
                const string actualOwner = "42222222-2222-4222-8222-222222222222";
                const string expectedRouteOwner = "43333333-3333-4333-8333-333333333333";
                const string actualRouteOwner = "44444444-4444-4444-8444-444444444444";
                f.Driver.ResultDiagnosticDetails = new Dictionary<string, object> {
                    { "check", "endpoint_owner_mismatch" }, { "routeKind", "direct_mk_ip" },
                    { "actualConnectionKind", "direct_mk_ip" }, { "expectedHost", "192.168.0.240" },
                    { "expectedPort", 6344 }, { "expectedModuleId", "fb0000000003" },
                    { "pluginVersion", "1.0.5.0" }, { "forceUsbScheme", true },
                    { "nativeCompletionState", "blocked" }, { "nativeCompletionCheck", "success_window_unverified" },
                    { "nativeCompletionLastCheck", "success_window_not_found" }, { "nativeSuccessDialogSuppressed", false },
                    { "suppressNativeSuccessDialog", true },
                    { "autoConfirmSuccessResult", true }, { "windowText", secret }, { "windowHandle", secret },
                    { "actualModuleIdState", "missing" }, { "moduleIdentitySource", "project_adapter_and_native_route" },
                    { "projectModuleId", "fb0000000003" }, { "nativeRouteCacheMatched", true },
                    { "nativeCloudEnabled", false }, { "nativeDirectEnabled", true }, { "nativeRoutePasswordMatched", false },
                    { "expectedAdapterNodeId", expectedAdapter }, { "actualAdapterOwnerId", actualOwner },
                    { "expectedRouteOwnerId", expectedRouteOwner }, { "actualRouteOwnerId", actualRouteOwner },
                    { "actualHost", "tcp://user:" + secret + "@192.168.0.241" },
                    { "actualModuleId", secret }, { "rawEndpoint", secret }, { "password", secret },
                    { "deviceKey", secret }, { "unknownNested", new Dictionary<string, object> { { "key", secret } } }
                };
                Dictionary<string, object> request = f.Begin();
                string operationId = BridgeValues.Text(request, "requestId");
                f.Coordinator.Handle(request);
                f.Driver.Release.Set();
                Dictionary<string, object> completed = f.WaitTerminal(operationId);
                Assert(BridgeValues.Text(completed, "errorCode") == "native_adapter_mismatch" &&
                    BridgeValues.Text(completed, "errorPhase") == "native_operation", "public operation discarded precise diagnostic fields");
                var details = BridgeValues.Object(completed, "diagnosticDetails");
                Assert(BridgeValues.Text(details, "nativeCompletionState") == "blocked" &&
                    BridgeValues.Text(details, "nativeCompletionCheck") == "success_window_unverified" &&
                    BridgeValues.Text(details, "nativeCompletionLastCheck") == "success_window_not_found" &&
                    details.ContainsKey("nativeSuccessDialogSuppressed") && !BridgeValues.Flag(details, "nativeSuccessDialogSuppressed") &&
                    BridgeValues.Flag(details, "suppressNativeSuccessDialog") &&
                    BridgeValues.Flag(details, "autoConfirmSuccessResult"), "reviewed native cleanup diagnostic did not survive public operation");
                Assert(BridgeValues.Text(details, "check") == "endpoint_owner_mismatch" &&
                    BridgeValues.Text(details, "actualConnectionKind") == "direct_mk_ip" &&
                    BridgeValues.Text(details, "expectedHost") == "192.168.0.240" &&
                    BridgeValues.Number(details, "expectedPort") == 6344 &&
                    BridgeValues.Text(details, "expectedModuleId") == "FB0000000003" &&
                    BridgeValues.Text(details, "pluginVersion") == "1.0.5.0" &&
                    BridgeValues.Text(details, "expectedAdapterNodeId") == expectedAdapter &&
                    BridgeValues.Text(details, "actualAdapterOwnerId") == actualOwner &&
                    BridgeValues.Text(details, "expectedRouteOwnerId") == expectedRouteOwner &&
                    BridgeValues.Text(details, "actualRouteOwnerId") == actualRouteOwner,
                    "reviewed route diagnostic fields did not survive the public operation");
                Assert(!BridgeJson.Serialize(completed).Contains(secret), "untrusted route data leaked into the phone response");
                Assert(BridgeValues.Text(details, "actualModuleIdState") == "missing" &&
                    BridgeValues.Text(details, "moduleIdentitySource") == "project_adapter_and_native_route" &&
                    BridgeValues.Text(details, "projectModuleId") == "FB0000000003" &&
                    BridgeValues.Flag(details, "nativeRouteCacheMatched") && !BridgeValues.Flag(details, "nativeCloudEnabled") &&
                    BridgeValues.Flag(details, "nativeDirectEnabled") && details.ContainsKey("nativeRoutePasswordMatched") &&
                    !BridgeValues.Flag(details, "nativeRoutePasswordMatched") && !details.ContainsKey("actualModuleId"),
                    "public operation lost missing module evidence, a false boolean or fabricated actual module identity");
                Assert(!BridgeValues.Flag(completed, "registrationConfirmed") && f.Saves == 0,
                    "diagnostic failure was saved as successful commissioning");
                string savedPath = Path.Combine(f.DirectoryPath, "completed-diagnostic-journal.json");
                File.Copy(f.Driver.Journal, savedPath);
                string persisted = File.ReadAllText(savedPath);
                Assert(persisted.Contains("native_adapter_mismatch") && persisted.Contains("native_operation"),
                    "durable operation journal lost failure details");
                Assert(!persisted.Contains("1234ABCD") && !persisted.Contains(secret), "registration KEY or untrusted route data entered persistent diagnostics");
                var nextDriver = new FakeDriver { Journal = savedPath };
                using (RegistrationCoordinator reopened = f.NewCoordinator(savedPath, nextDriver))
                {
                    var status = f.Request("registration_status");
                    status["operationId"] = operationId;
                    status["panelId"] = f.PanelId;
                    Dictionary<string, object> recovered = BridgeValues.Object(reopened.Handle(status), "operation");
                    Assert(BridgeValues.Text(recovered, "state") == "failed" && BridgeValues.Flag(recovered, "terminal"),
                        "restart changed a completed refusal into a different state");
                    Assert(BridgeValues.Text(recovered, "errorCode") == "native_adapter_mismatch" &&
                        BridgeValues.Text(recovered, "errorPhase") == "native_operation" &&
                        BridgeValues.Text(recovered, "message") == f.Driver.ResultMessage,
                        "restart lost the phone-compatible diagnostic message or structured fields");
                    Assert(nextDriver.Calls == 0 && !reopened.HasActiveOperations,
                        "reading a recovered diagnostic replayed registration");
                    Assert(reopened.ConnectionDiagnosticReport.Contains("native_adapter_mismatch") &&
                        reopened.ConnectionDiagnosticReport.Contains("native_operation"),
                        "the recovered operation's diagnostic is missing when connection diagnostics are unavailable");
                    var restoredDetails = BridgeValues.Object(recovered, "diagnosticDetails");
                    Assert(BridgeValues.Text(restoredDetails, "nativeCompletionState") == "blocked" &&
                        BridgeValues.Text(restoredDetails, "nativeCompletionCheck") == "success_window_unverified" &&
                        BridgeValues.Text(restoredDetails, "nativeCompletionLastCheck") == "success_window_not_found" &&
                        restoredDetails.ContainsKey("nativeSuccessDialogSuppressed") && !BridgeValues.Flag(restoredDetails, "nativeSuccessDialogSuppressed") &&
                        BridgeValues.Flag(restoredDetails, "suppressNativeSuccessDialog") &&
                        BridgeValues.Flag(restoredDetails, "autoConfirmSuccessResult") &&
                        reopened.ConnectionDiagnosticReport.Contains("success_window_unverified"),
                        "journal restart discarded the bounded cleanup state, check or capability");
                    Assert(BridgeValues.Text(restoredDetails, "actualModuleIdState") == "missing" &&
                        BridgeValues.Text(restoredDetails, "moduleIdentitySource") == "project_adapter_and_native_route" &&
                        BridgeValues.Text(restoredDetails, "projectModuleId") == "FB0000000003" &&
                        BridgeValues.Flag(restoredDetails, "nativeRouteCacheMatched") && !BridgeValues.Flag(restoredDetails, "nativeCloudEnabled") &&
                        BridgeValues.Flag(restoredDetails, "nativeDirectEnabled") && restoredDetails.ContainsKey("nativeRoutePasswordMatched") &&
                        !BridgeValues.Flag(restoredDetails, "nativeRoutePasswordMatched") && !restoredDetails.ContainsKey("actualModuleId"),
                        "journal restart lost explicit missing-ID route evidence or invented actual module identity");
                    Assert(BridgeValues.Text(restoredDetails, "check") == "endpoint_owner_mismatch" &&
                        BridgeValues.Text(restoredDetails, "expectedModuleId") == "FB0000000003" &&
                        BridgeValues.Text(restoredDetails, "expectedAdapterNodeId") == expectedAdapter &&
                        BridgeValues.Text(restoredDetails, "actualAdapterOwnerId") == actualOwner &&
                        BridgeValues.Text(restoredDetails, "expectedRouteOwnerId") == expectedRouteOwner &&
                        BridgeValues.Text(restoredDetails, "actualRouteOwnerId") == actualRouteOwner &&
                        reopened.ConnectionDiagnosticReport.Contains(expectedAdapter) &&
                        reopened.ConnectionDiagnosticReport.Contains(actualOwner) &&
                        reopened.ConnectionDiagnosticReport.Contains(expectedRouteOwner) &&
                        reopened.ConnectionDiagnosticReport.Contains(actualRouteOwner) &&
                        !reopened.ConnectionDiagnosticReport.Contains(secret),
                        "reviewed route fields were lost or private input entered the recovered report");
                }
            }
        }

        private static void RegistrationReportSurvivesLaterConnectionCheck()
        {
            using (Fixture f = new Fixture())
            {
                var native = new NativeHarness();
                var request = f.Begin();
                native.Profile.Id = f.PanelId;
                native.Target.OperationId = BridgeValues.Text(request, "requestId");
                native.Target.ProjectFingerprint = f.Fingerprint;
                native.OperationReply = type => {
                    var failed = native.Snapshot("failed");
                    failed["error"] = new Dictionary<string, object> { { "code", "native_adapter_mismatch" } };
                    return NativeHarness.Reply(failed);
                };
                string journal = Path.Combine(f.DirectoryPath, "native-diagnostics.json");
                using (var coordinator = new RegistrationCoordinator(f.Profiles, () => BridgeValues.Copy(f.Project),
                    (target, result) => { f.Saves++; return true; }, native.Driver, journal, ProtocolV3.JournalIdentityKey(Code), () => f.Clock))
                {
                    Assert(BridgeValues.Flag(coordinator.Handle(request), "accepted"), "diagnostic test operation was rejected unexpectedly");
                    var status = f.Request("registration_status");
                    status["operationId"] = native.Target.OperationId;
                    status["panelId"] = f.PanelId;
                    Assert(SpinWait.SpinUntil(() => BridgeValues.Flag(BridgeValues.Object(coordinator.Handle(status), "operation"), "terminal"), 5000),
                        "diagnostic test operation did not finish");
                    Assert(coordinator.ConnectionDiagnosticReport.Contains("native_adapter_mismatch"), "completed registration is absent from the combined report");
                    native.Driver.ReadProjectPanels();
                    string report = coordinator.ConnectionDiagnosticReport;
                    Assert(report.Contains("native_adapter_mismatch") && report.Contains("native_operation") && report.Contains("list_panels"),
                        "a later successful hello/catalog check erased the completed registration's diagnostic");
                    int requests = native.Calls.Count;
                    string reportPath = Path.Combine(f.DirectoryPath, "registration-diagnostics.txt");
                    coordinator.SaveConnectionDiagnostics(reportPath);
                    Assert(File.ReadAllText(reportPath) == report, "SaveConnectionDiagnostics omitted the completed-operation section");
                    Assert(native.Calls.Count == requests && native.Count("start") == 1 && native.Count("status") == 0 &&
                        native.Count("cancel") == 0 && f.Saves == 0,
                        "viewing/saving diagnostics initiated hardware, repeated registration, or saved a commissioning result");
                }
            }
        }

        private static void NativeSuccessRequiresFullEvidenceAndIdentity()
        {
            var omissions = new Dictionary<string, object> {
                { "registrationConfirmed", false }, { "confirmationSent", false }, { "nativeSuccessObserved", false },
                { "validatedBy", "unverified" }, { "confirmedHardwareId", "FA0000000002" },
                { "operationId", "66666666-6666-4666-8666-666666666666" }, { "recordId", SecondRecord },
                { "projectFingerprint", new string('b', 64) }, { "nativeProjectFingerprint", new string('c', 64) },
                { "panelId", "77777777-7777-4777-8777-777777777777" },
                { "nativePanelId", "88888888-8888-4888-8888-888888888888" },
                { "host", "192.168.0.241" }, { "port", 6345 }, { "moduleId", "FB0000000002" },
                { "expectedHardwareId", "FA0000000002" }
            };
            foreach (KeyValuePair<string, object> omission in omissions)
            {
                var native = new NativeHarness();
                native.OperationReply = type => { var state = native.Snapshot("registered"); state[omission.Key] = omission.Value; return NativeHarness.Reply(state); };
                RegistrationDriverResult result = native.Run();
                Assert(result.State == "unknown" && !result.RegistrationConfirmed && result.HardwareId == null, "incomplete/mismatched native evidence became success: " + omission.Key);
                Assert(native.Count("start") == 1, "invalid result replayed hardware");
            }
        }

        private static void NativeCancellationRequiresStoppedUnconfirmedRun()
        {
            foreach (string missing in new[] { "none", "nativeRunFinished", "dialogClosed", "confirmationSent" })
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    var native = new NativeHarness();
                    native.OperationReply = type => {
                        if (type == "start") { cancellation.Cancel(); return NativeHarness.Reply(native.Snapshot("waiting_sensor")); }
                        if (type == "status") return NativeHarness.Error("operation_not_found");
                        var state = native.Snapshot("cancelled");
                        if (missing == "confirmationSent") state[missing] = true;
                        else if (missing != "none") state[missing] = false;
                        return NativeHarness.Reply(state);
                    };
                    RegistrationDriverResult result = native.Run(cancellation.Token);
                    Assert(native.Count("start") == 1 && native.Count("cancel") == 1, "cancellation repeated start or cancel");
                    if (missing == "none") Assert(result.State == "cancelled" && result.StopConfirmed && native.Count("status") == 0, "confirmed native stop was not accepted");
                    else Assert(result.State == "unknown" && !result.StopConfirmed && native.Count("status") == 1, "incomplete cancellation proof was accepted: " + missing);
                }
            }
        }

        private static void NativeProgressMapsToPhoneStates()
        {
            var native = new NativeHarness();
            var states = new Queue<string>(new[] { "waiting_sensor", "detected", "registering", "registered" });
            native.OperationReply = type => NativeHarness.Reply(native.Snapshot(states.Dequeue()));
            Assert(native.Run().State == "registered", "native state sequence failed");
            Assert(native.Progress.SequenceEqual(new[] { "connecting", "waiting_for_sensor", "detected", "registering" }), "native states did not map to the public protocol");
            Assert(native.Count("start") == 1 && native.Count("status") == 3, "state polling repeated native start");
        }

        private static void LegacyProfileSecretDoesNotBlockNativePath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "streletz-profile-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                int decryptCalls = 0;
                var store = new PanelProfileStore(Path.Combine(directory, "panels.json"),
                    value => (byte[])value.Clone(), value => { decryptCalls++; throw new InvalidOperationException("legacy DPAPI inaccessible"); });
                string id = store.Save(new PanelProfile { Name = "Legacy", Host = "192.168.0.240", Port = 6344, ModuleId = "FB0000000003" }, "test-only");
                Assert(store.Get(id).PasswordMd5 == null && decryptCalls == 0, "native path unnecessarily decrypted legacy credentials");
                store.Save(new PanelProfile { Id = id, Name = "Renamed", Host = "192.168.0.240", Port = 6344, ModuleId = "FB0000000003" }, null);
                Assert(store.Get(id).Name == "Renamed" && decryptCalls == 0, "profile edit required an unused secret");
            }
            finally { Directory.Delete(directory, true); }
        }

        private sealed class CatalogDriver : IPanelRegistrationDriver, IProjectPanelSource
        {
            public Dictionary<string, object> Listing;
            public bool Offline;
            public Action BeforeReturn;
            public int Reads, Starts;
            public bool IsAvailable { get { return !Offline; } }
            public string UnavailableReason { get { return "Test native project offline"; } }
            public Dictionary<string, object> ReadProjectPanels()
            {
                Reads++;
                if (Offline) throw new IOException("Test native project offline");
                if (BeforeReturn != null) BeforeReturn();
                return BridgeValues.Copy(Listing);
            }
            public RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target,
                CancellationToken cancellation, Action<RegistrationDriverProgress> progress)
            { Starts++; throw new InvalidOperationException("Catalog reads must not start hardware"); }
        }

        private static Dictionary<string, object> NativeCatalogPanel(int number, params string[] recordIds)
        {
            return new Dictionary<string, object> {
                { "nativeNodeGuid", "a1111111-1111-4111-8111-" + number.ToString("x12") },
                { "segmentGuid", "b2222222-2222-4222-8222-" + number.ToString("x12") },
                { "displayName", "Панель " + number }, { "host", "192.168.0.240" }, { "port", 6344 },
                { "moduleId", "FB0000000003" }, { "recordIds", recordIds }
            };
        }

        private static Dictionary<string, object> NativeCatalog(params Dictionary<string, object>[] panels)
        {
            return new Dictionary<string, object> { { "nativeProjectFingerprint", new string('e', 64) }, { "panels", panels } };
        }

        private static void ProjectCatalogMembershipAndRoundTrip()
        {
            var project = MakeProject();
            var one = (Dictionary<string, object>)BridgeValues.Array(project, "records")[0];
            one["description"] = "Исходное место";
            var commissioning = BridgeValues.Object(one, "commissioning");
            commissioning["description"] = "3R.ВТН1 — описание монтажника";
            commissioning["note"] = "Отдельная заметка";
            var native = NativeCatalog(NativeCatalogPanel(1, FirstRecord), NativeCatalogPanel(2, SecondRecord));
            var catalog = ProjectPanelCatalog.FromNative(native, project);
            Assert(catalog.Length == 2, "panels sharing one MK-IP were merged");
            string firstId = BridgeValues.Text(catalog.Single(p => ProjectPanelCatalog.Contains(p, FirstRecord)), "projectPanelId");
            Assert(firstId == ProjectPanelCatalog.StableId("a1111111-1111-4111-8111-000000000001", "b2222222-2222-4222-8222-000000000001"), "panel identity was not deterministic");
            ProjectPanelCatalog.Apply(project, catalog);
            var restored = BridgeJson.ParseObject(BridgeJson.Serialize(project));
            var first = (Dictionary<string, object>)BridgeValues.Array(restored, "records")[0];
            Assert(BridgeValues.Text(first, "projectPanelId") == firstId, "record membership lost on project round trip");
            Assert(ProjectPanelCatalog.EffectiveDescription(first) == "3R.ВТН1 — описание монтажника", "description override lost");
            Assert(BridgeValues.Text(BridgeValues.Object(first, "commissioning"), "note") == "Отдельная заметка", "description overwrote mounting note");
            BridgeValues.Object(first, "commissioning")["description"] = "";
            Assert(ProjectPanelCatalog.EffectiveDescription(first) == "", "explicitly cleared description restored an old value");
            Assert(ProjectPanelCatalog.Cached(restored).Length == 2, "offline cache did not round trip");
            restored["panelMappingFingerprint"] = new string('0', 64);
            Assert(ProjectPanelCatalog.Cached(restored).Length == 0, "foreign cached membership accepted");
            Throws<InvalidDataException>(() => ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(9, Guid.NewGuid().ToString("D"))), project), "unrelated native project became a live empty catalog");
            Throws<InvalidDataException>(() => ProjectPanelCatalog.FromNative(NativeCatalog(), project), "no native routes became a verified empty catalog");
            var ambiguous = ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(1, FirstRecord), NativeCatalogPanel(2, FirstRecord)), project);
            ProjectPanelCatalog.Apply(project, ambiguous);
            Assert(BridgeValues.Text(one, "projectPanelId") == "", "ambiguous membership was guessed");
        }

        private static void AutomaticProfilesPreserveRoutesAndManualProfiles()
        {
            string directory = Path.Combine(Path.GetTempPath(), "streletz-auto-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new PanelProfileStore(Path.Combine(directory, "panels.json"));
                string manual = store.Save(new PanelProfile { Name = "Manual profile", Host = "192.168.0.240", Port = 6344, ModuleId = "FB0000000003" }, "");
                var catalog = ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(1, FirstRecord), NativeCatalogPanel(2, SecondRecord)), MakeProject());
                store.SynchronizeProjectPanels(catalog, new HashSet<string>());
                string id = BridgeValues.Text(catalog[0], "projectPanelId");
                string revision = store.Get(id).Revision;
                Assert(store.List().Length == 3 && store.Get(manual).Name == "Manual profile", "automatic catalog replaced a manual profile");
                store.SynchronizeProjectPanels(catalog, new HashSet<string>());
                Assert(store.Get(id).Revision == revision, "identical native catalog changed profile revision");
                catalog[0]["host"] = "192.168.0.241";
                store.SynchronizeProjectPanels(catalog, new HashSet<string> { id });
                Assert(store.Get(id).Host == "192.168.0.240" && !PanelProfileStore.MatchesCatalog(store.Get(id), catalog[0]), "active operation route was overwritten");
                store.SynchronizeProjectPanels(catalog, new HashSet<string>());
                var reopened = new PanelProfileStore(Path.Combine(directory, "panels.json"));
                Assert(reopened.Get(id).Host == "192.168.0.241" && reopened.Get(id).NativePanelId.Length == 36, "native route binding was not persisted");
                Throws<InvalidDataException>(() => reopened.Save(new PanelProfile { Id = id, Name = "Manual edit", Host = "192.168.0.242", Port = 6344, ModuleId = "FB0000000003F" }, ""), "auto profile could be silently repurposed");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void CatalogCoordinatorNeverStartsHardware()
        {
            string directory = Path.Combine(Path.GetTempPath(), "streletz-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var project = MakeProject();
                var driver = new CatalogDriver { Listing = NativeCatalog(NativeCatalogPanel(1, FirstRecord), NativeCatalogPanel(2, SecondRecord)) };
                int saves = 0;
                bool rejectSave = false;
                var store = new PanelProfileStore(Path.Combine(directory, "panels.json"));
                using (var coordinator = new RegistrationCoordinator(store, () => BridgeValues.Copy(project), (target, result) => false,
                    driver, Path.Combine(directory, "journal.json"), ProtocolV3.JournalIdentityKey(Code),
                    savePanelCatalog: (fingerprint, catalog, nativeFingerprint) => {
                        saves++;
                        if (rejectSave) throw new IOException("test catalog write failure");
                        ProjectPanelCatalog.Apply(project, catalog, nativeFingerprint); return true;
                    }))
                {
                    Func<Dictionary<string, object>> request = () => new Dictionary<string, object> {
                        { "protocol", 3 }, { "type", "list_panels" }, { "requestId", Guid.NewGuid().ToString("D") },
                        { "sentAt", BridgeValues.NowMs() }, { "projectFingerprint", BridgeValues.ProjectFingerprint(project) }, { "recordId", FirstRecord }
                    };
                    var check = request(); check["type"] = "check_connection";
                    string originalProject = BridgeJson.Serialize(project);
                    var checkedReply = coordinator.Handle(check);
                    Assert(BridgeValues.Flag(checkedReply, "ok") && BridgeValues.Flag(checkedReply, "nativeAvailable"), "read-only connection check failed");
                    Assert(saves == 0 && driver.Starts == 0 && !File.Exists(Path.Combine(directory, "panels.json")), "connection check wrote a profile or project or started hardware");
                    Assert(BridgeJson.Serialize(project) == originalProject && !BridgeValues.Flag(checkedReply, "projectSaved"), "check mutated project or pretended to save it");
                    var response = coordinator.Handle(request());
                    byte[] profilesBefore = File.ReadAllBytes(Path.Combine(directory, "panels.json"));
                    check = request(); check["type"] = "check_connection";
                    checkedReply = coordinator.Handle(check);
                    Assert(saves == 1 && profilesBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "panels.json"))) && driver.Starts == 0, "repeat connection check mutated profiles or saved project");
                    Assert(BridgeValues.Flag(response, "ok") && BridgeValues.Number(response, "panelMappingVersion") == 1, "new catalog contract missing");
                    Assert(BridgeValues.Array(response, "projectPanels").Length == 2 && BridgeValues.Array(response, "panels").Length == 1, "device catalog did not restrict membership");
                    Assert(BridgeValues.Text(BridgeValues.Object(response, "record"), "resolution") == "unique", "unique project panel not selected");
                    Assert(BridgeValues.Flag(response, "projectSaved") && saves == 1 && driver.Starts == 0, "catalog read dispatched hardware or did not persist");
                    int reads = driver.Reads;
                    var wrong = request(); wrong["projectFingerprint"] = new string('0', 64);
                    Assert(!BridgeValues.Flag(coordinator.Handle(wrong), "ok") && driver.Reads == reads && saves == 1, "foreign phone project changed panel map");
                    wrong = request(); wrong["recordId"] = Guid.NewGuid().ToString("D");
                    Assert(!BridgeValues.Flag(coordinator.Handle(wrong), "ok") && driver.Reads == reads, "unknown record reached native source");
                    rejectSave = true;
                    Assert(!BridgeValues.Flag(coordinator.Handle(request()), "projectSaved"), "failed catalog save reported success");
                    Assert(!BridgeValues.Flag(coordinator.Handle(request()), "projectSaved") && saves == 3, "failed save was not retried");
                    rejectSave = false;
                    driver.Offline = true;
                    response = coordinator.Handle(request());
                    Assert(BridgeValues.Text(response, "catalogSource") == "cached_project" && BridgeValues.Array(response, "projectPanels").Length == 2, "offline map was not retained");
                    Assert(!BridgeValues.Flag((Dictionary<string, object>)BridgeValues.Array(response, "panels")[0], "available"), "cached route became live availability");
                    driver.Offline = false;
                    driver.Listing = NativeCatalog(NativeCatalogPanel(9, Guid.NewGuid().ToString("D")));
                    response = coordinator.Handle(request());
                    Assert(BridgeValues.Text(response, "catalogSource") == "cached_project" && ProjectPanelCatalog.Cached(project).Length == 2 && saves == 3, "unrelated Master project erased cached map");
                    Assert(driver.Starts == 0, "viewing a catalog started registration");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void FullProjectCatalogFitsAuthenticatedResponse()
        {
            string file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "project.json");
            var project = BridgeJson.ParseObject(File.ReadAllText(file));
            var records = BridgeValues.Array(project, "records").OfType<Dictionary<string, object>>().ToArray();
            Assert(records.Length == 453, "synthetic project fixture size changed; review transport budget");
            var nativePanels = new List<Dictionary<string, object>>();
            for (int i = 0; i < records.Length; i += 10)
                nativePanels.Add(NativeCatalogPanel(i / 10 + 1, records.Skip(i).Take(10).Select(row => BridgeValues.Text(row, "id")).ToArray()));
            var catalog = ProjectPanelCatalog.FromNative(NativeCatalog(nativePanels.ToArray()), project);
            var response = ProtocolV3.Response(Guid.NewGuid().ToString("D"), true, "Панели проекта");
            response["projectPanels"] = catalog; response["panels"] = catalog;
            response["recordMetadata"] = ProjectPanelCatalog.RecordMetadata(project);
            response["projectFingerprint"] = BridgeValues.ProjectFingerprint(project);
            response["panelMappingVersion"] = 1; response["catalogSource"] = "native_project";
            var encoded = ProtocolV3.Encode(response, Code, "response");
            string envelope = BridgeJson.Serialize(encoded);
            Assert(envelope.Length > ProtocolV3.MaximumLineLength, "fixture no longer covers old response line limit");
            Assert(envelope.Length <= ProtocolV3.MaximumResponseLineLength, "full project exceeded bounded response limit");
            var decoded = ProtocolV3.Decode(BridgeJson.ParseObject(envelope), Code, "response");
            Assert(BridgeValues.Array(decoded, "projectPanels").Length == nativePanels.Count && BridgeValues.Array(decoded, "recordMetadata").Length == 453, "full catalog lost records in authenticated transport");
            Throws<InvalidDataException>(() => ProtocolV3.Encode(response, Code, "request"), "catalog increased inbound request budget");
            var tooLarge = ProtocolV3.Response("", true, new string('x', ProtocolV3.MaximumResponsePlaintext));
            Throws<InvalidDataException>(() => ProtocolV3.Encode(tooLarge, Code, "response"), "unbounded response accepted");
            Console.WriteLine("INFO synthetic 453-record catalog encrypted response: " + envelope.Length + " bytes");
        }

        private static void OfflineCatalogDisplayedWithoutProfilesOrNativeConnection()
        {
            var project = MakeProject();
            var catalog = ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(1, FirstRecord, SecondRecord)), project);
            catalog[0]["host"] = ""; catalog[0]["port"] = 0; catalog[0]["moduleId"] = ""; catalog[0]["origin"] = "bfx";
            var emptyPanel = BridgeValues.Copy(catalog[0]);
            emptyPanel["projectPanelId"] = emptyPanel["panelId"] = Guid.NewGuid().ToString("D");
            emptyPanel["name"] = "Пустая панель из проекта"; emptyPanel["recordIds"] = new object[0];
            ProjectPanelCatalog.Apply(project, new[] { catalog[0], emptyPanel });
            var cached = ProjectPanelCatalog.Cached(BridgeJson.ParseObject(BridgeJson.Serialize(project)));
            var rows = PanelCatalogRow.Build(cached, new PanelProfile[0]);
            Assert(rows.Length == 2 && rows.All(row => row.ReadOnly), "offline project panels require an operational profile to be visible");
            Assert(rows[0].Port == 0 && rows[0].Host == "" && rows[0].RecordCount == 2, "display invented an unavailable connection route");
            Assert(rows.Any(row => row.RecordCount == 0), "empty project panel disappeared from the selector");
            Assert(cached.All(panel => !BridgeValues.Flag(panel, "available")), "displaying project data enabled registration");
            var live = ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(1, FirstRecord)), project);
            var merged = ProjectPanelCatalog.Merge(cached, live);
            var owner = merged.Single(panel => ProjectPanelCatalog.Contains(panel, SecondRecord));
            Assert(ProjectPanelCatalog.Contains(owner, FirstRecord) && !ProjectPanelCatalog.VerifiedContains(owner, SecondRecord)
                && ProjectPanelCatalog.VerifiedContains(owner, FirstRecord), "live subset either erased offline membership or verified an unobserved record");
            Assert(merged.Length == 2, "live subset removed an offline-only panel");
        }

        private static XmlElement BfxElement(XmlDocument document, XmlNode parent, string name, string text = null)
        {
            var element = document.CreateElement(name);
            if (text != null) element.InnerText = text;
            parent.AppendChild(element); return element;
        }

        private static XmlElement BfxItem(XmlDocument document, XmlNode parent, string id, string classType, string name)
        {
            var item = BfxElement(document, parent, "Item");
            item.SetAttribute("id", id); item.SetAttribute("CustomName", name); item.SetAttribute("addres", "1");
            var resource = BfxElement(document, document.SelectSingleNode("/Root/Resources"), "Resource");
            resource.SetAttribute("id", id);
            var info = BfxElement(document, resource, VendorSerializedRoot(classType));
            BfxElement(document, info, "ClassType", classType); BfxElement(document, info, "Name", name);
            return item;
        }

        private static string VendorSerializedRoot(string classType)
        {
            // Measured with XmlReflectionImporter + XmlSerializer.Serialize(null)
            // on the official 12.4 assemblies, result-11.log (11/11). These are
            // deliberately independent of the production payload reader.
            switch (classType)
            {
                case "CorePlugins.segment.CustomInfo": case "RROP_LON.KS.CustomInfo": return "CustomInfo";
                case "MostIPI.Similar.MK_IP.MkIpInfo": return "MkIpInfo";
                case "RROP_LON.Panel_1.Info": case "RROP_LON.Panel_2.Info": case "RROP_LON.Panel_3.Info":
                case "RROP_LON.RRI_KS.Info": case "RROP_LON.RRI2_KS.Info": case "PanelPT.Info":
                case "StreletcPro.RR.Info": case "StreletcPro.RRI.Info": return "Info";
                default: throw new InvalidOperationException("Fixture XML root has not been measured against the vendor serializer.");
            }
        }

        private static void BfxReplaceResourceType(XmlDocument document, string id, string classType)
        {
            var resource = document.SelectSingleNode("/Root/Resources/Resource[@id='" + id + "']");
            var previous = resource.ChildNodes.OfType<XmlElement>().Single();
            var payload = document.CreateElement(VendorSerializedRoot(classType));
            foreach (XmlNode child in previous.ChildNodes) payload.AppendChild(child.CloneNode(true));
            payload.SelectSingleNode("ClassType").InnerText = classType;
            resource.ReplaceChild(payload, previous);
        }

        private static void BfxTreeMembershipDoesNotRequireNativeConnection()
        {
            const string segmentOne = "b2222222-2222-4222-8222-000000000001";
            const string segmentTwo = "b2222222-2222-4222-8222-000000000002";
            const string panelOne = "a1111111-1111-4111-8111-000000000001";
            const string panelTwo = "a1111111-1111-4111-8111-000000000002";
            const string adapterId = "c3333333-3333-4333-8333-000000000001";
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml("<Root><Items/><Resources/></Root>");
            var itemRoot = document.SelectSingleNode("/Root/Items");
            BfxItem(document, itemRoot, adapterId, "MostIPI.Similar.MK_IP.MkIpInfo", "МК-IP");
            var segment = BfxItem(document, itemRoot, segmentOne, "CorePlugins.segment.CustomInfo", "Сегмент один");
            var controller = BfxItem(document, segment, panelOne, "RROP_LON.Panel_3.Info", "Одинаковое имя");
            var expander = BfxItem(document, controller, "d4444444-4444-4444-8444-000000000001", "StreletcPro.RRI.Info", "Не контроллер");
            BfxItem(document, expander, FirstRecord, "StreletcPro.RR.Info", "Панель два — неверная подсказка в имени");
            var nestedSegment = BfxItem(document, segment, segmentTwo, "CorePlugins.segment.CustomInfo", "Вложенный сегмент");
            var nestedController = BfxItem(document, nestedSegment, panelTwo, "RROP_LON.RRI_KS.Info", "Одинаковое имя");
            BfxItem(document, nestedController, SecondRecord, "StreletcPro.RR.Info", "Панель один — неверная подсказка в имени");
            var emptySegment = BfxItem(document, itemRoot, "b2222222-2222-4222-8222-000000000003", "CorePlugins.segment.CustomInfo", "Пустой сегмент");
            BfxItem(document, emptySegment, "a1111111-1111-4111-8111-000000000003", "PanelPT.Info", "Панель без датчиков");
            BfxElement(document, document.SelectSingleNode("/Root/Resources/Resource[@id='" + segmentOne + "']/CustomInfo"), "LonInterface", adapterId);
            var adapter = document.SelectSingleNode("/Root/Resources/Resource[@id='" + adapterId + "']/MkIpInfo");
            var address = BfxElement(document, adapter, "ConnectionIpAddress");
            int[] octets = { 192, 168, 0, 240 };
            for (int i = 0; i < octets.Length; i++) BfxElement(document, address, "A" + (i + 1), octets[i].ToString());
            BfxElement(document, adapter, "ConnectionPort", "0"); BfxElement(document, adapter, "EthernetConnectionPort", "6345");
            BfxElement(document, adapter, "ID", Convert.ToBase64String(new byte[] { 0xFB, 0x00, 0x00, 0x00, 0x00, 0x03 }));
            BfxElement(document, adapter, "Password", "must-not-export");
            var project = MakeProject();
            var originalCommissioning = BridgeValues.Object((Dictionary<string, object>)BridgeValues.Array(project, "records")[0], "commissioning");
            originalCommissioning["stage"] = 2; originalCommissioning["serial"] = Sensor;
            originalCommissioning["note"] = "Сохранённая заметка"; originalCommissioning["description"] = "Описание монтажника";
            string savedCommissioning = BridgeJson.Serialize(originalCommissioning);
            Assert(document.SelectNodes("/Root/Resources/Resource/Info[ClassType='CorePlugins.segment.CustomInfo']").Count == 0,
                "regression fixture still hides the real CustomInfo payload behind a synthetic Info wrapper");
            Assert(document.SelectNodes("/Root/Resources/Resource/Info[ClassType='MostIPI.Similar.MK_IP.MkIpInfo']").Count == 0,
                "regression fixture still hides the real MkIpInfo payload behind a synthetic Info wrapper");
            var panels = BfxPanelCatalog.Read(document, project);
            Assert(panels.Length == 3 && ProjectPanelCatalog.IsComplete(project), "serialized BFX tree did not produce its complete offline catalog");
            var first = panels.Single(panel => ProjectPanelCatalog.Contains(panel, FirstRecord));
            var second = panels.Single(panel => ProjectPanelCatalog.Contains(panel, SecondRecord));
            Assert(BridgeValues.Text(first, "nativePanelId") == panelOne && BridgeValues.Text(second, "nativePanelId") == panelTwo,
                "nested segments or misleading names changed sensor ownership");
            Assert(BridgeValues.Text(first, "projectPanelId") == ProjectPanelCatalog.StableId(panelOne, segmentOne), "BFX and live native panel identities diverged");
            Assert(BridgeValues.Text(first, "host") == "192.168.0.240" && BridgeValues.Number(first, "port") == 6345 && BridgeValues.Text(first, "moduleId") == "FB0000000003",
                "serialized EthernetAddress, port fallback or six-byte MK-IP ID was read incorrectly");
            Assert(BridgeValues.Text(second, "host") == "" && BridgeValues.Number(second, "port") == 0, "panel without a route borrowed another panel's MK-IP");
            Assert(panels.All(panel => !BridgeValues.Flag(panel, "available")) && !BridgeJson.Serialize(project).Contains("must-not-export"),
                "offline BFX import authorized hardware or exported authentication material");
            Assert(PanelCatalogRow.Build(ProjectPanelCatalog.Cached(project), new PanelProfile[0]).Length == 3, "BFX panels still need a native profile to be visible");
            Assert(BridgeJson.Serialize(BridgeValues.Object((Dictionary<string, object>)BridgeValues.Array(project, "records")[0], "commissioning")) == savedCommissioning,
                "reading BFX panel membership replaced the commissioning journal");
            var subset = MakeProject();
            subset["records"] = new object[] { BridgeValues.Array(subset, "records")[0] };
            BfxPanelCatalog.Read(document, subset);
            Assert(BridgeValues.Array(subset, "records").Length == 1 && ProjectPanelCatalog.IsComplete(subset),
                "additional BFX records were imported into the existing journal or rejected a valid subset");
            var missing = MakeProject();
            ((Dictionary<string, object>)BridgeValues.Array(missing, "records")[1])["id"] = Guid.NewGuid().ToString("D");
            string beforeMissing = BridgeJson.Serialize(missing);
            Throws<InvalidDataException>(() => BfxPanelCatalog.Read(document, missing), "BFX missing a current record GUID was accepted");
            Assert(BridgeJson.Serialize(missing) == beforeMissing, "missing GUID rejection partially modified the project");
            foreach (string controllerType in new[] { "RROP_LON.Panel_1.Info", "RROP_LON.Panel_2.Info", "RROP_LON.Panel_3.Info", "PanelPT.Info",
                "RROP_LON.RRI_KS.Info", "RROP_LON.RRI2_KS.Info", "RROP_LON.KS.CustomInfo" })
            {
                BfxReplaceResourceType(document, panelOne, controllerType);
                var typedPanels = BfxPanelCatalog.Read(document, MakeProject());
                var typedOwner = typedPanels.Single(panel => ProjectPanelCatalog.Contains(panel, FirstRecord));
                Assert(BridgeValues.Text(typedOwner, "nativePanelId") == panelOne && BridgeValues.Text(typedOwner, "host") == "192.168.0.240",
                    "real serializer payload root lost a supported controller or route: " + controllerType);
            }
            document.SelectSingleNode("/Root/Resources/Resource[@id='" + panelOne + "']/CustomInfo/ClassType").InnerText = "KS.CustomInfo";
            Assert(!BfxPanelCatalog.Read(document, MakeProject()).Any(panel => ProjectPanelCatalog.Contains(panel, FirstRecord)),
                "an unverified shortened controller alias was accepted");
            BfxReplaceResourceType(document, panelOne, "RROP_LON.Panel_3.Info");
            BfxItem(document, segment, "a1111111-1111-4111-8111-000000000004", "RROP_LON.Panel_1.Info", "Второй контроллер");
            panels = BfxPanelCatalog.Read(document, MakeProject());
            Assert(!panels.Any(panel => ProjectPanelCatalog.Contains(panel, FirstRecord)), "ambiguous segment chose an arbitrary controller");
            Assert(panels.Any(panel => ProjectPanelCatalog.Contains(panel, SecondRecord)), "ambiguous parent segment swallowed a valid nested segment");
            Console.WriteLine("INFO vendor-root regression: old Resource/Info segment count 0; fixed reader 3 panels and exact MK-IP route; all 7 controller types passed");
        }

        private static void BfxResourceShapeAndEmptyCatalogDiagnostics()
        {
            foreach (string xml in new[] {
                "<Resource/>",
                "<Resource><Info><ClassType>PanelPT.Info</ClassType></Info><CustomInfo><ClassType>CorePlugins.segment.CustomInfo</ClassType></CustomInfo></Resource>",
                "<Resource><CustomInfo><Nested><ClassType>CorePlugins.segment.CustomInfo</ClassType></Nested></CustomInfo></Resource>",
                "<Resource><CustomInfo><ClassType>CorePlugins.segment.CustomInfo</ClassType><ClassType>PanelPT.Info</ClassType></CustomInfo></Resource>",
                "<Resource><CustomInfo><ClassType><Value>CorePlugins.segment.CustomInfo</Value></ClassType></CustomInfo></Resource>"
            })
            {
                var malformed = new XmlDocument { XmlResolver = null };
                malformed.LoadXml(xml);
                Throws<InvalidDataException>(() => BfxPanelCatalog.ReadResourcePayload(malformed.DocumentElement),
                    "missing, nested or ambiguous resource payload was accepted");
            }

            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml("<Root><Items/><Resources/></Root>");
            var items = document.SelectSingleNode("/Root/Items");
            BfxItem(document, items, FirstRecord, "StreletcPro.RR.Info", "private-project-name");
            BfxItem(document, items, SecondRecord, "StreletcPro.RR.Info", "private-device-name");
            BfxElement(document, document.SelectSingleNode("/Root/Resources/Resource/Info"), "Password", "private-password");
            var project = MakeProject();
            string before = BridgeJson.Serialize(project);
            string diagnostic = "";
            try { BfxPanelCatalog.Read(document, project); }
            catch (InvalidDataException error) { diagnostic = error.Message; }
            Assert(diagnostic.Contains("Не найден сегмент поддержанного типа.")
                && diagnostic.Contains("Узлов: 2; ресурсов: 2; сегментов: 0")
                && diagnostic.Contains("поддержанных контроллеров: 0")
                && diagnostic.Contains("записей текущего проекта: 2"),
                "empty catalog did not explain its structural counts and reason");
            Assert(!diagnostic.Contains("private-") && !diagnostic.Contains(FirstRecord) && !diagnostic.Contains(SecondRecord),
                "empty catalog diagnostic disclosed project field values");
            Assert(BridgeJson.Serialize(project) == before, "empty catalog failure modified the current project");
            Console.WriteLine("INFO malformed resource payloads rejected; empty catalog reports structural counts without field values and leaves project unchanged");
        }

        private static void StaleCatalogCompletenessAndConcurrentProjectReplacement()
        {
            var project = MakeProject();
            ProjectPanelCatalog.Apply(project, ProjectPanelCatalog.FromNative(NativeCatalog(NativeCatalogPanel(1, FirstRecord, SecondRecord)), project));
            project["panelCatalogComplete"] = true;
            project["panelMappingFingerprint"] = new string('0', 64);
            Assert(!ProjectPanelCatalog.IsComplete(project), "foreign catalog advertised authoritative completeness");
            ProjectPanelCatalog.Apply(project, ProjectPanelCatalog.Cached(project));
            Assert(!ProjectPanelCatalog.IsComplete(project), "empty fallback laundered foreign catalog completeness");
            string directory = Path.Combine(Path.GetTempPath(), "streletz-catalog-revision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                project = MakeProject();
                var driver = new CatalogDriver { Listing = NativeCatalog(NativeCatalogPanel(1, FirstRecord, SecondRecord)) };
                int saves = 0;
                using (var coordinator = new RegistrationCoordinator(new PanelProfileStore(Path.Combine(directory, "panels.json")),
                    () => BridgeValues.Copy(project), (target, result) => false, driver, Path.Combine(directory, "journal.json"), ProtocolV3.JournalIdentityKey(Code),
                    savePanelCatalog: (fingerprint, panels, nativeFingerprint) => { saves++; return true; }))
                {
                    driver.BeforeReturn = () => coordinator.RunProjectMutation(() => project["siteName"] = "Same GUIDs, newly imported metadata");
                    var response = coordinator.Handle(new Dictionary<string, object> {
                        { "protocol", 3 }, { "type", "list_panels" }, { "requestId", Guid.NewGuid().ToString("D") }, { "sentAt", BridgeValues.NowMs() },
                        { "projectFingerprint", BridgeValues.ProjectFingerprint(project) }
                    });
                    Assert(!BridgeValues.Flag(response, "ok") && BridgeValues.Text(response, "errorCode") == "project_mismatch" && saves == 0,
                        "stale native read overwrote a concurrently imported project with the same record GUIDs");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void SnapshotCacheInvalidation()
        {
            using (var f = new Fixture())
            {
                var first = f.Coordinator.Handle(f.Request("get_project"));
                string revision = BridgeValues.Text(first, "projectRevision");
                int reads = f.ProjectReads;
                var request = f.Request("get_project");
                request["knownProjectFingerprint"] = f.Fingerprint;
                request["knownProjectRevision"] = revision;
                for (int i = 0; i < 25; i++)
                    Assert(BridgeValues.Flag(f.Coordinator.Handle(request), "notModified"), "cached unchanged result lost");
                Assert(f.ProjectReads == reads, "unchanged polls still copy/hash the live project");
                BridgeValues.Object(first, "project")["siteName"] = "mutated reply";
                var again = f.Coordinator.Handle(f.Request("get_project"));
                Assert(BridgeValues.Text(BridgeValues.Object(again, "project"), "siteName") == "Test project", "caller mutated the prepared cache");
                try { f.Coordinator.RunProjectMutation(() => { f.Project["siteName"] = "partial write"; throw new IOException("test"); }); }
                catch (IOException) { }
                var changed = f.Coordinator.Handle(request);
                Assert(!BridgeValues.Flag(changed, "notModified") && f.ProjectReads == reads + 1, "failed writer retained a stale cache");
                Assert(BridgeValues.Text(BridgeValues.Object(changed, "project"), "siteName") == "partial write", "new snapshot missed partial metadata");
                f.OnSave = () => f.Project["siteName"] = "native completion";
                var begin = f.Begin();
                Assert(BridgeValues.Flag(f.Coordinator.Handle(begin), "ok"), "synthetic begin failed");
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000), "synthetic worker did not start");
                f.Coordinator.Handle(f.Request("get_project"));
                int activeReads = f.ProjectReads;
                f.Coordinator.Handle(f.Request("get_project"));
                Assert(f.ProjectReads > activeReads, "active writer reused prepared cache");
                f.Driver.Release.Set();
                f.WaitTerminal(BridgeValues.Text(begin, "requestId"));
                var completed = f.Coordinator.Handle(f.Request("get_project"));
                Assert(BridgeValues.Text(BridgeValues.Object(completed, "project"), "siteName") == "native completion", "completion failed to invalidate prepared snapshot");
            }
            string directory = Path.Combine(Path.GetTempPath(), "streletz-cache-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var project = MakeProject();
                var driver = new CatalogDriver { Listing = NativeCatalog(NativeCatalogPanel(1, FirstRecord, SecondRecord)) };
                using (var coordinator = new RegistrationCoordinator(new PanelProfileStore(Path.Combine(directory, "panels.json")),
                    () => BridgeValues.Copy(project), (target, result) => false, driver, Path.Combine(directory, "journal.json"), ProtocolV3.JournalIdentityKey(Code),
                    savePanelCatalog: (fingerprint, panels, nativeFingerprint) => { project["siteName"] = "catalog saved"; return true; }))
                {
                    Func<string, Dictionary<string, object>> request = type => new Dictionary<string, object> {
                        { "protocol", 3 }, { "type", type }, { "requestId", Guid.NewGuid().ToString("D") }, { "sentAt", BridgeValues.NowMs() },
                        { "projectFingerprint", BridgeValues.ProjectFingerprint(project) }
                    };
                    coordinator.Handle(request("get_project"));
                    Assert(BridgeValues.Flag(coordinator.Handle(request("list_panels")), "ok"), "catalog refresh failed");
                    var refreshed = coordinator.Handle(request("get_project"));
                    Assert(BridgeValues.Text(BridgeValues.Object(refreshed, "project"), "siteName") == "catalog saved", "catalog save retained stale snapshot");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void AutomaticProjectDownloadIsReadOnlyAndVersioned()
        {
            using (var f = new Fixture())
            {
                f.Driver.ThrowAvailability = true;
                var request = f.Request("get_project");
                var response = f.Coordinator.Handle(request);
                Assert(BridgeValues.Flag(response, "ok") && !BridgeValues.Flag(response, "notModified"), "first project download failed without Master");
                Assert(BridgeValues.Text(response, "projectSource") == "windows_bridge" && BridgeValues.Text(response, "projectFingerprint") == f.Fingerprint,
                    "download selected a bundled or unrelated project");
                var snapshot = BridgeValues.Object(response, "project");
                Assert(snapshot != null && BridgeValues.Array(snapshot, "records").Length == 2, "project records were not transferred");
                string revision = BridgeValues.Text(response, "projectRevision");
                Assert(revision.Length == 64 && f.Driver.Calls == 0 && f.Saves == 0, "download called hardware or changed the journal");
                snapshot["siteName"] = "changed on phone";
                Assert(BridgeValues.Text(f.Project, "siteName") == "Test project", "download handed out the mutable bridge project");
                request = f.Request("get_project"); request["knownProjectFingerprint"] = f.Fingerprint; request["knownProjectRevision"] = revision;
                response = f.Coordinator.Handle(request);
                Assert(BridgeValues.Flag(response, "notModified") && !response.ContainsKey("project"), "unchanged project was retransferred");
                f.Coordinator.RunProjectMutation(() => {
                    var row = (Dictionary<string, object>)BridgeValues.Array(f.Project, "records")[0];
                    BridgeValues.Object(row, "commissioning")["description"] = "Новое описание";
                    BridgeValues.Object(row, "commissioning")["note"] = "Журнал на Windows";
                    BridgeValues.Object(row, "commissioning")["serial"] = Sensor;
                });
                response = f.Coordinator.Handle(request);
                Assert(!BridgeValues.Flag(response, "notModified") && BridgeValues.Text(response, "projectRevision") != revision,
                    "same-GUID description/journal change was hidden by the project fingerprint");
                var encoded = ProtocolV3.Encode(response, Code, "response");
                string envelope = BridgeJson.Serialize(encoded);
                Assert(!envelope.Contains(Sensor) && !envelope.Contains("Журнал"), "project journal escaped the encrypted transport");
                var decoded = ProtocolV3.Decode(encoded, Code, "response");
                Assert(BridgeValues.Text(decoded, "requestId") == BridgeValues.Text(request, "requestId"), "project response lost request binding");
                var reversed = new Dictionary<string, object>();
                foreach (var field in f.Project.Reverse()) reversed.Add(field.Key, field.Value);
                Assert(ProjectSyncSnapshot.ContentRevision(reversed) == ProjectSyncSnapshot.ContentRevision(f.Project), "revision depends on dictionary insertion order");
            }
            string directory = Path.Combine(Path.GetTempPath(), "streletz-project-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using (var coordinator = new RegistrationCoordinator(new PanelProfileStore(Path.Combine(directory, "panels.json")),
                    () => null, (target, result) => false, new FakeDriver { ThrowAvailability = true },
                    Path.Combine(directory, "journal.json"), ProtocolV3.JournalIdentityKey(Code)))
                {
                    var response = coordinator.Handle(new Dictionary<string, object> { { "protocol", 3 }, { "type", "get_project" },
                        { "requestId", Guid.NewGuid().ToString("D") }, { "sentAt", BridgeValues.NowMs() } });
                    Assert(BridgeValues.Text(response, "errorCode") == "project_missing" && !response.ContainsKey("project"),
                        "missing bridge project fell back to bundled records");
                }
            }
            finally { Directory.Delete(directory, true); }

            var large = BridgeJson.ParseObject(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "project.json")));
            var full = ProjectSyncSnapshot.Reply(large, Guid.NewGuid().ToString("D"), "", "");
            var protectedFull = ProtocolV3.Encode(full, Code, "response");
            Assert(BridgeJson.Serialize(protectedFull).Length <= ProtocolV3.MaximumResponseLineLength, "full project does not fit the bounded transport");
            var recovered = BridgeValues.Object(ProtocolV3.Decode(protectedFull, Code, "response"), "project");
            Assert(BridgeValues.Array(recovered, "records").Length == 453 && BridgeValues.ProjectFingerprint(recovered) == BridgeValues.ProjectFingerprint(large),
                "full project download lost records or changed their GUIDs");
            large["siteName"] = new string('x', ProtocolV3.MaximumResponsePlaintext);
            try { ProjectSyncSnapshot.Reply(large, Guid.NewGuid().ToString("D"), "", ""); throw new Exception("oversized project accepted"); }
            catch (RegistrationRequestException error) { Assert(error.Code == "project_too_large", "oversized project failed without a useful bounded error"); }
            Console.WriteLine("INFO full 453-record project encrypted download: " + BridgeJson.Serialize(protectedFull).Length + " bytes; no native call");
        }

        private static void TerminalRoutesUseExactProjectPair()
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml("<Root><Items/><Resources/></Root>");
            var root = document.SelectSingleNode("/Root/Items");
            var system = BfxElement(document, root, "Item"); system.SetAttribute("id", "e5555555-5555-4555-8555-000000000001");
            var otherSystem = BfxElement(document, root, "Item"); otherSystem.SetAttribute("id", "e5555555-5555-4555-8555-000000000002");
            Func<string, XmlNode> info = id => document.SelectSingleNode("/Root/Resources/Resource[@id='" + id + "']").ChildNodes.OfType<XmlElement>().Single();
            var segmentIds = new Dictionary<int, string>();
            var panelIds = new Dictionary<int, string>();
            var adapterIds = new Dictionary<int, string>();
            foreach (int number in new[] { 33, 3, 32, 2 }) // Deliberately not in pair/name order.
            {
                string segmentId = "b2222222-2222-4222-8222-" + number.ToString("x12"); segmentIds[number] = segmentId;
                string panelId = "a1111111-1111-4111-8111-" + number.ToString("x12"); panelIds[number] = panelId;
                string adapterId = "c3333333-3333-4333-8333-" + number.ToString("x12"); adapterIds[number] = adapterId;
                var segment = BfxItem(document, system, segmentId, "CorePlugins.segment.CustomInfo", "Сегмент " + number);
                var panel = BfxItem(document, segment, panelId, "RROP_LON.Panel_3.Info", number == 32 ? "32.1 Панель-Терминал" : "Нарочно одинаковое имя");
                BfxElement(document, info(segmentId), "Number", number.ToString());
                BfxElement(document, info(segmentId), "IsViaPanel3DuplicateEnabled", number < 30 ? "true" : "false");
                BfxElement(document, info(panelId), "PanelMode", number < 30 ? "Main" : "Duplicate");
                BfxElement(document, info(panelId), "NumberMainSegment", "29"); // Not the vendor pairing field.
                if (number == 2 || number == 3) BfxItem(document, panel, number == 2 ? FirstRecord : SecondRecord, "StreletcPro.RR.Info", "Датчик");
                BfxItem(document, segment, adapterId, "MostIPI.Similar.MK_IP.MkIpInfo", "МК-IP");
                BfxElement(document, info(segmentId), "LonInterface", adapterId);
                var address = BfxElement(document, info(adapterId), "ConnectionIpAddress");
                int[] octets = { 192, 0, 2, number };
                for (int n = 0; n < 4; n++) BfxElement(document, address, "A" + (n + 1), octets[n].ToString());
                BfxElement(document, info(adapterId), "ConnectionPort", "6344");
                BfxElement(document, info(adapterId), "ID", Convert.ToBase64String(new byte[] { 0x32, 0xE8, 0x20, 0, 0, (byte)number }));
            }
            Func<Dictionary<string, object>> owner = () => BfxPanelCatalog.Read(document, MakeProject()).Single(p => ProjectPanelCatalog.Contains(p, FirstRecord));
            var routed = owner();
            Assert(BridgeValues.Text(routed, "nativePanelId") == panelIds[2] && BridgeValues.Text(routed, "segmentId") == segmentIds[2], "terminal replaced the sensor's target panel");
            Assert(BridgeValues.Text(routed, "terminalPanelId") == panelIds[32] && BridgeValues.Text(routed, "terminalSegmentId") == segmentIds[32]
                && BridgeValues.Text(routed, "host") == "192.0.2.32" && BridgeValues.Text(routed, "adapterNodeId") == adapterIds[32],
                "terminal route borrowed another terminal or the main panel's own adapter");
            Assert(BridgeValues.Flag(routed, "routeResolved") && !BridgeValues.Flag(routed, "available") && BridgeValues.Text(routed, "routeKind") == "panel_terminal", "offline terminal route authorized hardware");
            var terminalXml = document.SelectSingleNode("/Root/Items//Item[@id='" + segmentIds[32] + "']");
            otherSystem.AppendChild(terminalXml);
            Assert(BridgeValues.Text(owner(), "host") == "", "pair lookup crossed system boundaries");
            system.AppendChild(terminalXml);
            info(panelIds[32]).SelectSingleNode("PanelMode").InnerText = "Main";
            Assert(BridgeValues.Text(owner(), "host") == "", "a main panel at number32 was mistaken for a terminal");
            info(panelIds[32]).SelectSingleNode("PanelMode").InnerText = "Duplicate";
            info(segmentIds[33]).SelectSingleNode("Number").InnerText = "32";
            Assert(BridgeValues.Text(owner(), "host") == "", "ambiguous terminal number chose the first node");
            info(segmentIds[33]).SelectSingleNode("Number").InnerText = "33";
            info(segmentIds[32]).SelectSingleNode("IsViaPanel3DuplicateEnabled").InnerText = "true";
            Assert(BridgeValues.Text(owner(), "host") == "", "recursive terminal route accepted");
            info(segmentIds[32]).SelectSingleNode("IsViaPanel3DuplicateEnabled").InnerText = "false";
            info(segmentIds[2]).SelectSingleNode("IsViaPanel3DuplicateEnabled").InnerText = "false";
            Assert(BridgeValues.Text(owner(), "host") == "192.0.2.2" && BridgeValues.Text(owner(), "routeKind") == "direct_mk_ip", "disabled terminal flag redirected a direct panel");
        }

        private static void TerminalDriverIdentityCannotDrift()
        {
            Func<NativeHarness> terminalHarness = () => {
                var native = new NativeHarness();
                var panel = (Dictionary<string, object>)native.Panels[0];
                panel["segmentGuid"] = "b2222222-2222-4222-8222-000000000002";
                panel["routeKind"] = "panel_terminal"; panel["viaPanelTerminal"] = true;
                panel["adapterNodeId"] = "c3333333-3333-4333-8333-000000000032";
                panel["adapterSegmentId"] = panel["terminalSegmentId"] = "b2222222-2222-4222-8222-000000000032";
                panel["terminalPanelId"] = "a1111111-1111-4111-8111-000000000032";
                panel["terminalDisplayName"] = "32.1 Панель-Терминал";
                native.Profile.Source = "project"; native.Profile.NativePanelId = NativeHarness.NativePanelId;
                native.Profile.SegmentId = BridgeValues.Text(panel, "segmentGuid"); native.Profile.RouteKind = "panel_terminal";
                native.Profile.AdapterNodeId = BridgeValues.Text(panel, "adapterNodeId");
                native.Profile.AdapterSegmentId = native.Profile.TerminalSegmentId = BridgeValues.Text(panel, "terminalSegmentId");
                native.Profile.TerminalPanelId = BridgeValues.Text(panel, "terminalPanelId");
                native.OperationReply = type => {
                    var state = native.Snapshot("registered"); PanelRouteMetadata.CopyIdentity(panel, state); return NativeHarness.Reply(state);
                };
                return native;
            };
            var good = terminalHarness();
            Assert(good.Run().RegistrationConfirmed && good.Count("start") == 1, "exact terminal route did not reach the existing native registration command");
            var sent = BridgeValues.Object(good.Calls.Single(r => BridgeValues.Text(r, "type") == "start"), "panel");
            Assert(BridgeValues.Text(sent, "nativePanelId") == NativeHarness.NativePanelId && BridgeValues.Text(sent, "terminalPanelId") == good.Profile.TerminalPanelId,
                "start confused the main panel and terminal identities");
            var drift = terminalHarness(); drift.Profile.TerminalPanelId = Guid.NewGuid().ToString("D");
            Assert(drift.Run().State == "failed" && drift.Count("start") == 0, "terminal changed behind an unchanged IP/module and still dispatched");
            var unknown = terminalHarness(); unknown.Profile.Source = null;
            Assert(unknown.Run().State == "failed" && unknown.Count("start") == 0, "manual direct profile silently selected a terminal");
            var replyDrift = terminalHarness();
            replyDrift.OperationReply = type => {
                var state = replyDrift.Snapshot("registered"); PanelRouteMetadata.CopyIdentity((Dictionary<string, object>)replyDrift.Panels[0], state);
                state["terminalSegmentId"] = Guid.NewGuid().ToString("D"); return NativeHarness.Reply(state);
            };
            Assert(replyDrift.Run().State == "unknown" && replyDrift.Count("start") == 1, "a different terminal's reply marked a device initialized");
            var catalog = ProjectPanelCatalog.FromNative(NativeCatalog((Dictionary<string, object>)good.Panels[0]), MakeProject());
            Assert(BridgeValues.Text(catalog[0], "terminalDisplayName") == "32.1 Панель-Терминал", "terminal label lost in phone catalog");
            ((Dictionary<string, object>)good.Panels[0])["adapterSegmentId"] = good.Profile.SegmentId;
            Throws<InvalidDataException>(() => ProjectPanelCatalog.FromNative(NativeCatalog((Dictionary<string, object>)good.Panels[0]), MakeProject()),
                "contradictory terminal route was accepted by the native catalog");
            var directPreflight = new NativeHarness();
            ((Dictionary<string, object>)directPreflight.Panels[0])["viaPanelTerminal"] = true;
            Assert(directPreflight.Run().State == "failed" && directPreflight.Count("start") == 0,
                "direct profile accepted a contradictory terminal flag before dispatch");
            var directReply = new NativeHarness();
            directReply.OperationReply = type => {
                var state = directReply.Snapshot("registered"); state["terminalPanelId"] = Guid.NewGuid().ToString("D");
                return NativeHarness.Reply(state);
            };
            Assert(directReply.Run().State == "unknown" && directReply.Count("start") == 1,
                "direct operation accepted a result carrying a terminal identity");
        }

        private static void PortablePlansRoundTrip()
        {
            var project = MakeProject();
            string image = new string('A', 2 * 1024 * 1024);
            project["fieldwork"] = new Dictionary<string, object> {
                { "version", 1 }, { "scans", new object[0] },
                { "plans", new object[] { new Dictionary<string, object> { { "id", "plan" }, { "image", image } } } }
            };
            var reply = ProjectSyncSnapshot.Reply(project, Guid.NewGuid().ToString("D"), "", "");
            var envelope = ProtocolV3.Encode(reply, Code, "response");
            Assert(BridgeJson.Serialize(envelope).Length < ProtocolV3.MaximumResponseLineLength, "plan transfer exceeds envelope cap");
            var decoded = ProtocolV3.Decode(envelope, Code, "response");
            var fields = BridgeValues.Object(BridgeValues.Object(decoded, "project"), "fieldwork");
            var plan = (Dictionary<string, object>)BridgeValues.Array(fields, "plans")[0];
            Assert(BridgeValues.Text(plan, "image") == image, "Windows snapshot discarded portable plan");
            var refreshed = BfxProjectUpdate.Merge(project, MakeProject());
            Assert(BridgeValues.Object(refreshed, "fieldwork") != null, "BFX refresh discarded fieldwork");
            Assert(ProtocolV3.MaximumRequestPlaintext == 48000 && ProtocolV3.MaximumLineLength == 65536, "response increase enlarged command requests");
        }

        private static void EmbeddedHttpUsesCoordinator()
        {
            using (Fixture f = new Fixture())
            using (HttpGateway gateway = new HttpGateway(Code, f.Coordinator.Handle))
            {
                Func<Dictionary<string, object>, Dictionary<string, object>> call = request => ProtocolV3.Decode(
                    BridgeJson.ParseObject(System.Text.Encoding.UTF8.GetString(gateway.Process(System.Text.Encoding.UTF8.GetBytes(
                        BridgeJson.Serialize(ProtocolV3.Encode(request, Code, "request")))))), Code, "response");
                var project = call(f.Request("get_project"));
                Assert(BridgeValues.Flag(project, "ok") && f.Driver.Calls == 0 && f.Saves == 0, "HTTP project download altered hardware/project");
                var stale = f.Begin(); stale["sentAt"] = f.Clock - 400000;
                Assert(BridgeValues.Text(call(stale), "errorCode") == "stale_request" && f.Driver.Calls == 0, "HTTP bypassed coordinator freshness guard");
                var begin = f.Begin();
                Assert(BridgeValues.Flag(call(begin), "ok"), "HTTP begin rejected");
                Assert(SpinWait.SpinUntil(() => f.Driver.Calls == 1, 3000) && f.Driver.JournalSeenBeforeHardware, "HTTP bypassed durable-before-dispatch barrier");
                Assert(BridgeValues.Flag(f.Coordinator.Handle(begin), "ok") && f.Driver.Calls == 1, "TCP retry of HTTP operation dispatched again");
                Assert(BridgeValues.Flag(call(begin), "ok") && f.Driver.Calls == 1, "HTTP retry dispatched again");
                f.Driver.Release.Set(); f.WaitTerminal(BridgeValues.Text(begin, "requestId"));
                Assert(State(call(begin)) == "registered" && f.Driver.Calls == 1 && f.Saves == 1, "HTTP terminal retry repeated work");
            }
        }

        private static int Main()
        {
            var tests = new Dictionary<string, Action> {
                { "HTTP encrypted boundary, invalid input, bounded body, no retry", HttpGatewaySelfTests.Envelope },
                { "HTTP portable settings, no secrets, explicit enable and valid ports", HttpGatewaySelfTests.Settings },
                { "HTTP uses shared coordinator: read-only project, stale rejection, cross-transport dedup", EmbeddedHttpUsesCoordinator },
                { "portable plans survive encrypted snapshots and BFX refresh; request limits unchanged", PortablePlansRoundTrip },
                { "authenticated request/response, direction binding, tamper, wrong/zero key", CryptoRoundTripAndTamper },
                { "project fingerprint, public panel selection, no default/no secret", ProjectFingerprintAndPanels },
                { "durable-before-action, retry dedup, payload conflict, terminal retry", IdempotencyAndJournal },
                { "single operation, active-profile/project guards, panel-bound status", BusyAndProfileGuards },
                { "stale/project/device/key validation prevents hardware access", BadInputNeverRunsHardware },
                { "unconfirmed result never marks initialized", () => RejectUnconfirmedSuccess(false, Sensor, "evidence") },
                { "mismatched actual ID never marks initialized", () => RejectUnconfirmedSuccess(true, "FA0000000002", "evidence") },
                { "missing readback evidence never marks initialized", () => RejectUnconfirmedSuccess(true, Sensor, "") },
                { "acknowledged cancellation", () => Cancellation(true) },
                { "unacknowledged cancellation becomes unknown", () => Cancellation(false) },
                { "confirmed timeout", () => TimeoutAndBusyAfterUnconfirmedStop(false) },
                { "unresponsive timeout retains hardware lock", () => TimeoutAndBusyAfterUnconfirmedStop(true) },
                { "restart recovers unfinished as unknown without replay", RestartDoesNotReplay },
                { "journal/availability failure and physical-success/save-failure separation", StorageAndAvailability },
                { "accepted=false only for proven pre-dispatch rejection; retry errors preserve known effects", AcceptedFlagDoesNotHideUnknownEffects },
                { "native driver routes the selected record/panel; mismatches never dispatch", NativeRoutingAndSelection },
                { "native lost/error start replies recover only by status; start never replayed", NativeLostStartNeverReplays },
                { "native retired/conflicting/missing operations stay unknown; proven refusal stays failed", NativeOldOperationNeverConfirms },
                { "native refusal codes and phases remain distinct without leaking secrets", NativeRegistrationFailureDiagnostics },
                { "missing optional module metadata remains distinct, redacted and never confirms or replays registration", MissingModuleDiagnosticsNeverImplyRegistration },
                { "registration refusal diagnostics survive journal restart without replay or commissioning", RegistrationDiagnosticsSurviveJournalRestart },
                { "registration report survives later hello/catalog checks; saving is read-only and complete", RegistrationReportSurvivesLaterConnectionCheck },
                { "native success requires actual ID, native evidence and full operation/panel/project identity", NativeSuccessRequiresFullEvidenceAndIdentity },
                { "native cancellation requires finished run, closed dialog and no confirmation", NativeCancellationRequiresStoppedUnconfirmedRun },
                { "native progress maps to agreed phone states", NativeProgressMapsToPhoneStates },
                { "native profiles do not require or decrypt legacy connection secrets", LegacyProfileSecretDoesNotBlockNativePath },
                { "project membership, offline cache, description/empty/note round trip, ambiguity", ProjectCatalogMembershipAndRoundTrip },
                { "automatic native profiles preserve manual profiles and active routes", AutomaticProfilesPreserveRoutesAndManualProfiles },
                { "catalog loading never dispatches hardware; foreign/offline projects and save errors", CatalogCoordinatorNeverStartsHardware },
                { "synthetic 453-record catalog authenticated response and unchanged request size limit", FullProjectCatalogFitsAuthenticatedResponse },
                { "offline project panels are visible without profiles; live subset preserves unverified membership", OfflineCatalogDisplayedWithoutProfilesOrNativeConnection },
                { "classified native connection failures, diagnostic redaction and read-only reporting", PanelDiagnosticsSelfTests.Run },
                { "read-only orphan recovery, exact identity/evidence, absent/active/manual, no replay, state lease and safe journal backup", RegistrationRecoverySelfTests.Run },
                { "BFX update adds IB1 while preserving all current GUID work, histories and conservative stage/ID guards", BfxProjectUpdateSelfTests.Run },
                { "BFX nested tree/routes, offline panels, subset import, missing GUID rejection and journal preservation", BfxTreeMembershipDoesNotRequireNativeConnection },
                { "BFX single direct payload/ClassType validation and private empty-catalog diagnostics", BfxResourceShapeAndEmptyCatalogDiagnostics },
                { "foreign completeness and same-GUID concurrent project import cannot replace catalog", StaleCatalogCompletenessAndConcurrentProjectReplacement },
                { "snapshot cache: unchanged polling, isolated replies, failing writes, catalog and native completion", SnapshotCacheInvalidation },
                { "automatic project download: no Master call, exact records, content revisions, encrypted bounded transfer", AutomaticProjectDownloadIsReadOnlyAndVersioned },
                { "BFX terminal pairing: same system, Main+30/Duplicate, exact adapter, ambiguity and disabled flag", TerminalRoutesUseExactProjectPair },
                { "terminal identity survives native start/result; route drift never dispatches or confirms", TerminalDriverIdentityCannotDrift }
            };
            int passed = 0;
            foreach (KeyValuePair<string, Action> test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); passed++; }
                catch (Exception error) { Console.WriteLine("FAIL " + test.Key + ": " + error); return 1; }
            }
            Console.WriteLine("PASS " + passed + " meaningful test groups; fake hardware only.");
            return 0;
        }
    }
}
#endif
