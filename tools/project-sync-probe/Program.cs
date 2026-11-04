// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using StreletzBridge;

internal static class Program
{
    // Synthetic pairing data. No production Master or hardware is contacted.
    private const string PairingCode = "1234567890ABCDEF";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 256 };
    private static Assembly bridge;
    private static int checks, handlerCalls, savedResults, savedCatalogs;
    private static int lastResponseBytes;

    private sealed class NoHardwareDriver : IPanelRegistrationDriver
    {
        public int Calls;
        public bool IsAvailable { get { Calls++; throw new InvalidOperationException("No native availability checks allowed in project transfer."); } }
        public string UnavailableReason { get { Calls++; throw new InvalidOperationException("No native diagnostics allowed in project transfer."); } }
        public RegistrationDriverResult Register(PanelProfile profile, RegistrationTarget target,
            CancellationToken cancellation, Action<RegistrationDriverProgress> progress)
        {
            Calls++;
            throw new InvalidOperationException("No registration allowed in project transfer.");
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: ProjectSyncProbe.exe <453-record project.json fixture> <built StreletzBridge.exe>");
            return 2;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "streletz-project-sync-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            bridge = typeof(IPanelRegistrationDriver).Assembly;
            string actualHash = Sha(File.ReadAllBytes(bridge.Location));
            Check(actualHash == Sha(File.ReadAllBytes(args[1])), "loaded production bridge equals requested EXE");
            Console.WriteLine("Bridge EXE SHA256: " + actualHash);
            Console.WriteLine("Bridge file version: " + System.Diagnostics.FileVersionInfo.GetVersionInfo(bridge.Location).FileVersion);
            Console.WriteLine("Runtime: " + (System.Type.GetType("Mono.Runtime") == null ? ".NET Framework" : "Mono") + "; serializer: System.Web.Script.Serialization.JavaScriptSerializer");
            VerifyDiagnosticSerialization();

            Dictionary<string, object> current = Parse(File.ReadAllText(args[0], Encoding.UTF8));
            Check(Array(current, "records").Length == 453, "released 453-record regression fixture");
            string before = Json.Serialize(current);
            var driver = new NoHardwareDriver();
            var store = Activator.CreateInstance(Type("PanelProfileStore"), new object[] {
                Path.Combine(temporary, "profiles.json"), null, null });
            Func<Dictionary<string, object>> getProject = delegate { return current; };
            Func<RegistrationTarget, RegistrationDriverResult, bool> saveResult = delegate { savedResults++; return false; };
            Func<string, Dictionary<string, object>[], string, bool> saveCatalog = delegate { savedCatalogs++; return false; };
            var coordinator = Activator.CreateInstance(Type("RegistrationCoordinator"), new object[] {
                store, getProject, saveResult, driver, Path.Combine(temporary, "journal.json"), new byte[32], null, saveCatalog });
            try
            {
                MethodInfo handle = Type("RegistrationCoordinator").GetMethod("Handle");
                MethodInfo mutate = Type("RegistrationCoordinator").GetMethod("RunProjectMutation", new[] { typeof(Action) });
                Action<Action> updateProject = action => mutate.Invoke(coordinator, new object[] { action });
                Func<Dictionary<string, object>, Dictionary<string, object>> handler = delegate(Dictionary<string, object> incoming) {
                    handlerCalls++;
                    return (Dictionary<string, object>)handle.Invoke(coordinator, new object[] { incoming });
                };
                var request = Request();
                var first = Exchange(request, PairingCode, handler);
                Check(Flag(first, "ok"), "full project transfer successful");
                Check(Text(first, "requestId") == Text(request, "requestId"), "authenticated request identity preserved");
                Check(!Flag(first, "notModified"), "first transfer contains snapshot");
                Check(Text(first, "projectSource") == "windows_bridge", "snapshot source explicitly bound");
                var snapshot = (Dictionary<string, object>)first["project"];
                Check(Array(snapshot, "records").Length == 453, "all 453 records survived actual TCP + net48 serializer");
                Check(lastResponseBytes > 65536, "real wire response exceeds former small serializer ceiling");
                Console.WriteLine("Full project protected TCP response: " + lastResponseBytes + " UTF-8 bytes");
                Check(Json.Serialize(current) == before, "transfer did not mutate source project");
                string fingerprint = Text(first, "projectFingerprint"), revision = Text(first, "projectRevision");
                Check(fingerprint.Length == 64 && revision.Length == 64, "fingerprint and revision present");

                request = Request();
                request["knownProjectFingerprint"] = fingerprint;
                request["knownProjectRevision"] = revision;
                var unchanged = Exchange(request, PairingCode, handler);
                Check(Flag(unchanged, "ok") && Flag(unchanged, "notModified"), "unchanged project acknowledged");
                Check(!unchanged.ContainsKey("project"), "unchanged response omits project body");
                Check(lastResponseBytes < 8192, "unchanged response compact on actual wire");

                updateProject(() => current["siteName"] = Text(current, "siteName") + " — regression update");
                var changed = Exchange(request, PairingCode, handler);
                Check(Flag(changed, "ok") && !Flag(changed, "notModified"), "content edit causes fresh download");
                Check(Text(changed, "projectFingerprint") == fingerprint, "record identity stable for content-only edit");
                Check(Text(changed, "projectRevision") != revision, "content revision changes after edit");

                int beforeUnauthenticated = handlerCalls;
                var unauthorized = Exchange(Request(), "FEDCBA0987654321", handler);
                Check(!Flag(unauthorized, "ok") && Text(unauthorized, "errorCode") == "unauthorized", "wrong pairing key rejected by production receiver");
                Check(!unauthorized.ContainsKey("project") && handlerCalls == beforeUnauthenticated, "unauthenticated request never reaches project handler");

                int beforeDelayed = handlerCalls;
                var delayedRequest = Request();
                var delayed = Exchange(delayedRequest, PairingCode, handler, 9000);
                Check(Flag(delayed, "ok") && Array((Dictionary<string, object>)delayed["project"], "records").Length == 453,
                    "authenticated fragmented VPN request survives a nine-second network pause");
                Check(Text(delayed, "requestId") == Text(delayedRequest, "requestId"), "delayed response retains exact authenticated request identity");
                Check(handlerCalls == beforeDelayed + 1, "delayed frame dispatches exactly once without retry");
                Console.WriteLine("VPN fragmented-request pause: 9000 ms (> former 7000 ms timeout), authenticated response and single dispatch: PASS.");

                updateProject(() => current["testLargeDescription"] = new string('\u042f', 600000));
                var tooLarge = Exchange(Request(), PairingCode, handler);
                Check(!Flag(tooLarge, "ok") && Text(tooLarge, "errorCode") == "project_too_large", "oversize project produces authenticated typed error");
                Check(!tooLarge.ContainsKey("project") && lastResponseBytes < 8192, "oversize failure does not leak partial project");
                updateProject(() => current = null);
                var missing = Exchange(Request(), PairingCode, handler);
                Check(!Flag(missing, "ok") && Text(missing, "errorCode") == "project_missing", "no open project produces authenticated typed error");

                Check(driver.Calls == 0, "project transfer never probes native driver or registers hardware");
                Check(savedResults == 0 && savedCatalogs == 0, "project transfer never writes commissioning state or panel catalog");
                Check(!File.Exists(Path.Combine(temporary, "journal.json")), "project transfer creates no registration journal");
                VerifyRecoveryRoundTrip(temporary);
                VerifyIb1ImportAndNewCopy(temporary);
                Console.WriteLine("PASS " + checks + " assertions: actual net48 EXE, diagnostic sanitization/serialization, production loopback receiver, delayed VPN frame, authenticated project transfer and readonly operation recovery; no GUI/Master/hardware.");
            }
            finally { ((IDisposable)coordinator).Dispose(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void VerifyIb1ImportAndNewCopy(string temporary)
    {
        const string ib1Id = "61111111-1111-4111-8111-111111111111";
        const string detectorId = "62222222-2222-4222-8222-222222222222";
        const string segmentId = "63333333-3333-4333-8333-333333333333";
        const string panelId = "64444444-4444-4444-8444-444444444444";
        const string unsupportedId = "65555555-5555-4555-8555-555555555555";
        const string serial = "FB0000000001";
        const string syntheticKey = "A1B2C3D4";
        string directory = Path.Combine(temporary, "ib1-project-update");
        Directory.CreateDirectory(directory);
        string encodedId = Convert.ToBase64String(new byte[] { 0x32, 0x7C, 0x20, 0x03, 0xAB, 0xCD });
        // Synthetic serialized tree. The radio pair 128:98 and runtime class
        // are independently pinned by the official catalog/InitDevice probe.
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml("<Root><Resources>"
            + "<Resource id='" + segmentId + "'><CustomInfo><ClassType>CorePlugins.segment.CustomInfo</ClassType></CustomInfo></Resource>"
            + "<Resource id='" + panelId + "'><Info><ClassType>RROP_LON.Panel_3.Info</ClassType></Info></Resource>"
            + "<Resource id='" + ib1Id + "'><Info><ClassType>StreletcPro.IB1.Info</ClassType><ID>" + encodedId
            + "</ID><PartitionNumber>512</PartitionNumber><FirmwareVersion>3</FirmwareVersion></Info></Resource>"
            + "<Resource id='" + detectorId + "'><Info><ClassType>StreletcPro.IPR.Info</ClassType></Info></Resource>"
            + "<Resource id='" + unsupportedId + "'><Info><ClassType>StreletcPro.IB1_isp2.Info</ClassType></Info></Resource>"
            + "</Resources><Items><Item id='" + segmentId + "' type='1' subType='0'>"
            + "<Item id='" + panelId + "' type='1' subType='0' addres='1.1'/>"
            + "<Item id='" + ib1Id + "' type='128' subType='98' addres='1' state='WasProgrammed' CustomName='Test IB1 place'/>"
            + "<Item id='" + detectorId + "' type='128' subType='32' addres='2'/>"
            + "<Item id='" + unsupportedId + "' type='128' subType='95' addres='3'/>"
            + "</Item></Items></Root>");
        string fixture = Path.Combine(directory, "synthetic-tree.xml");
        File.WriteAllText(fixture, document.OuterXml, Encoding.UTF8);
        var fresh = (Dictionary<string, object>)Invoke("BfxReader", "Convert", document, fixture, "Synthetic IB1 project");
        var records = Array(fresh, "records");
        Check(records.Length == 2, "production BFX converter includes exact IB1 subtype98 and retains existing detector");
        Dictionary<string, object> ib1 = null, detector = null;
        foreach (Dictionary<string, object> row in records) {
            if (Text(row, "id") == ib1Id) ib1 = row;
            if (Text(row, "id") == detectorId) detector = row;
            Check(Text(row, "id") != unsupportedId, "IB1 version2 subtype95 is not silently treated as supported IB1");
        }
        Check(ib1 != null && Text(ib1, "type") == "ИБ1-ПРО" && Text(ib1, "name") == "1 ИБ1-ПРО" &&
            Text(ib1, "group") == "Исполнительные устройства" && Text(ib1, "recordKind") == "device",
            "IB1 exports as a normal device with exact model, visible name and group");
        Check(Text(ib1, "description") == "Test IB1 place" && Text(ib1, "zone") == "512" && Text(ib1, "version") == "в. 3",
            "IB1 description, zone and firmware survive BFX conversion");
        var ib1Work = (Dictionary<string, object>)ib1["commissioning"];
        Check(Text(ib1Work, "serial") == serial && Convert.ToInt32(ib1Work["stage"]) == 2,
            "IB1 inherited six-byte ID and source initialization state are retained");
        Check(!String.IsNullOrEmpty(Text(ib1, "projectPanelId")), "IB1 is assigned to its containing project panel");
        var panels = Array(fresh, "panels");
        Check(panels.Length == 1 && System.Array.Exists(Array((Dictionary<string, object>)panels[0], "recordIds"), value => Convert.ToString(value) == ib1Id),
            "panel filter membership includes the formerly excluded IB1 GUID");
        var previous = Parse(Json.Serialize(fresh));
        previous["records"] = new object[] { Parse(Json.Serialize(detector)) };
        var previousRow = (Dictionary<string, object>)Array(previous, "records")[0];
        var work = (Dictionary<string, object>)previousRow["commissioning"];
        work["stage"] = 3; work["serial"] = "FA0000000005"; work["deviceKey"] = syntheticKey;
        work["note"] = "Keep operator note"; work["description"] = "Keep operator description";
        string oldFingerprint = (string)Invoke("BridgeValues", "ProjectFingerprint", previous);
        work["registration"] = new Dictionary<string, object> { { "operationId", Guid.NewGuid().ToString("D") },
            { "state", "unknown" }, { "projectFingerprint", oldFingerprint }, { "recordId", detectorId } };
        string original = Json.Serialize(previous);
        string incoming = Json.Serialize(fresh);
        var updated = (Dictionary<string, object>)Invoke("BfxProjectUpdate", "Merge", previous, fresh);
        Check(Array(updated, "records").Length == 2 && Json.Serialize(previous) == original && Json.Serialize(fresh) == incoming,
            "actual production update adds IB1 without mutating either input snapshot");
        Dictionary<string, object> retained = null;
        foreach (Dictionary<string, object> row in Array(updated, "records")) if (Text(row, "id") == detectorId) retained = row;
        var kept = (Dictionary<string, object>)retained["commissioning"];
        Check(Convert.ToInt32(kept["stage"]) == 3 && Text(kept, "serial") == "FA0000000005" && Text(kept, "deviceKey") == syntheticKey &&
            Text(kept, "note") == "Keep operator note" && Text(kept, "description") == "Keep operator description",
            "actual production update preserves stage, full ID, KEY, note and explicit description by GUID");
        Check(Text((Dictionary<string, object>)kept["registration"], "projectFingerprint") == oldFingerprint &&
            (string)Invoke("BridgeValues", "ProjectFingerprint", updated) != oldFingerprint,
            "adding IB1 changes project identity without rewriting the original operation fingerprint");
        string oldPath = Path.Combine(directory, "original.s3project");
        string newPath = Path.Combine(directory, "updated.s3project");
        File.WriteAllText(oldPath, original, new UTF8Encoding(false));
        string oldHash = Sha(File.ReadAllBytes(oldPath));
        Invoke("ProjectFiles", "SaveNewCopy", newPath, updated);
        var reloaded = (Dictionary<string, object>)Invoke("ProjectFiles", "Load", newPath);
        Check(Array(reloaded, "records").Length == 2 && Sha(File.ReadAllBytes(oldPath)) == oldHash,
            "new project copy is loadable and original project bytes are preserved");
        string newHash = Sha(File.ReadAllBytes(newPath));
        bool refused = false;
        try { Invoke("ProjectFiles", "SaveNewCopy", newPath, previous); }
        catch (TargetInvocationException error) { refused = error.InnerException is IOException; }
        Check(refused && Sha(File.ReadAllBytes(newPath)) == newHash,
            "atomic new-copy writer refuses to overwrite an existing destination");
        refused = false;
        try { Invoke("ProjectFiles", "SaveNewCopy", oldPath, updated); }
        catch (TargetInvocationException error) { refused = error.InnerException is IOException; }
        Check(refused && Sha(File.ReadAllBytes(oldPath)) == oldHash,
            "atomic new-copy writer refuses to overwrite the current source project");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "new-copy save leaves no temporary file on success or refusal");
        Console.WriteLine("IB1 BFX conversion, panel membership, GUID-preserving project update and atomic new-copy save: PASS.");
    }

    private static void VerifyRecoveryRoundTrip(string temporary)
    {
        const string recordId = "11111111-1111-4111-8111-111111111111";
        const string nativePanel = "33333333-3333-4333-8333-333333333333";
        const string segment = "44444444-4444-4444-8444-444444444444";
        const string adapter = "55555555-5555-4555-8555-555555555555";
        const string sensor = "FA0000000001";
        string nativeFingerprint = new string('a', 64);
        string directory = Path.Combine(temporary, "readonly-recovery");
        Directory.CreateDirectory(directory);
        var project = new Dictionary<string, object> {
            { "schemaVersion", 1 }, { "siteName", "Synthetic recovery fixture" },
            { "records", new object[] { new Dictionary<string, object> {
                { "id", recordId }, { "recordKind", "device" }, { "name", "Synthetic detector" },
                { "initialized", "-" }, { "commissioning", new Dictionary<string, object> { { "stage", 1 }, { "serial", sensor } } }
            } } }
        };
        string fingerprint = (string)Invoke("BridgeValues", "ProjectFingerprint", project);
        var rawPanel = new Dictionary<string, object> {
            { "nativeNodeGuid", nativePanel }, { "segmentGuid", segment }, { "displayName", "Synthetic panel" },
            { "host", "127.0.0.1" }, { "port", 6344 }, { "moduleId", "FB0000000003" },
            { "routeKind", "direct_mk_ip" }, { "viaPanelTerminal", false },
            { "adapterNodeId", adapter }, { "adapterSegmentId", segment },
            { "terminalPanelId", "" }, { "terminalSegmentId", "" }, { "recordIds", new object[] { recordId } }
        };
        var listing = new Dictionary<string, object> { { "nativeProjectFingerprint", nativeFingerprint }, { "panels", new object[] { rawPanel } } };
        var catalog = (Dictionary<string, object>[])Invoke("ProjectPanelCatalog", "FromNative", listing, project);
        Invoke("ProjectPanelCatalog", "Apply", project, catalog, nativeFingerprint);
        string panelId = Text(catalog[0], "projectPanelId");
        var profiles = Activator.CreateInstance(Type("PanelProfileStore"), new object[] { Path.Combine(directory, "panels.json"), null, null });
        Type("PanelProfileStore").GetMethod("SynchronizeProjectPanels").Invoke(profiles, new object[] { catalog, new HashSet<string>() });
        string operationId = Guid.NewGuid().ToString("D");
        string nativeState = "missing";
        int readonlyCalls = 0, mutatingCalls = 0, saves = 0;
        Func<Dictionary<string, object>, Dictionary<string, object>> reply = data => new Dictionary<string, object> {
            { "protocol", "streletz-master-plugin-v1" }, { "ok", true }, { "data", data }
        };
        Func<Dictionary<string, object>, int, Dictionary<string, object>> native = delegate(Dictionary<string, object> request, int timeout) {
            string type = Text(request, "type");
            if (type == "hello") {
                readonlyCalls++;
                return reply(new Dictionary<string, object> {
                    { "ready", true }, { "version", "12.4" }, { "pluginVersion", "1.0.5.0" },
                    { "processId", 43210 }, { "projectFingerprint", nativeFingerprint },
                    { "capabilities", new object[] { "list_panels", "start", "status", "cancel" } }
                });
            }
            if (type == "list_panels") { readonlyCalls++; return reply(listing); }
            if (type != "status") { mutatingCalls++; throw new InvalidOperationException("Recovery attempted native mutation: " + type); }
            readonlyCalls++;
            Check(Text(request, "operationId") == operationId, "readonly native lookup retains the original operation ID");
            if (nativeState == "missing") return new Dictionary<string, object> {
                { "protocol", "streletz-master-plugin-v1" }, { "ok", false },
                { "error", new Dictionary<string, object> { { "code", "operation_not_found" } } }
            };
            return reply(new Dictionary<string, object> {
                { "operationId", operationId }, { "projectFingerprint", fingerprint }, { "recordId", recordId },
                { "expectedHardwareId", sensor }, { "panelId", panelId }, { "nativePanelId", nativePanel },
                { "host", "127.0.0.1" }, { "port", 6344 }, { "moduleId", "FB0000000003" },
                { "routeKind", "direct_mk_ip" }, { "viaPanelTerminal", false },
                { "adapterNodeId", adapter }, { "adapterSegmentId", segment },
                { "terminalPanelId", "" }, { "terminalSegmentId", "" },
                // The old operation fingerprint can differ after a successful native mutation.
                { "nativeProjectFingerprint", new string('b', 64) }, { "status", nativeState },
                { "registrationConfirmed", nativeState == "registered" },
                { "confirmedHardwareId", nativeState == "registered" ? sensor : "" },
                { "confirmationSent", nativeState == "registered" }, { "nativeSuccessObserved", nativeState == "registered" },
                { "nativeRunFinished", nativeState != "waiting_sensor" }, { "dialogClosed", nativeState != "waiting_sensor" },
                { "nativeDialogOpen", nativeState == "waiting_sensor" }, { "validatedBy", "native-fingerprint-record-and-panel" },
                { "createdUtc", "2026-01-01T00:00:00Z" }, { "finishedUtc", nativeState == "registered" ? "2026-01-01T00:00:10Z" : "" }
            });
        };
        var driver = (IPanelRegistrationDriver)Activator.CreateInstance(Type("PanelRegistrationDriver"),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { native, 0 }, null);
        Func<Dictionary<string, object>> getProject = () => project;
        Func<RegistrationTarget, RegistrationDriverResult, bool> save = delegate(RegistrationTarget target, RegistrationDriverResult result) {
            saves++;
            Check(target.OperationId == operationId && result.RegistrationConfirmed && result.HardwareId == sensor,
                "restored project save receives only the exact confirmed native result");
            return true;
        };
        object coordinator = Activator.CreateInstance(Type("RegistrationCoordinator"), new object[] {
            profiles, getProject, save, driver, Path.Combine(directory, "journal.json"), new byte[32], null, null });
        try {
            MethodInfo handle = Type("RegistrationCoordinator").GetMethod("Handle");
            Func<Dictionary<string, object>, Dictionary<string, object>> handler = incoming =>
                (Dictionary<string, object>)handle.Invoke(coordinator, new object[] { incoming });
            Func<Dictionary<string, object>> request = delegate {
                var value = Request(); value["type"] = "recover_registration";
                value["operationId"] = operationId; value["projectFingerprint"] = fingerprint;
                value["recordId"] = recordId; value["panelId"] = panelId; value["expectedHardwareId"] = sensor;
                return value;
            };
            var missing = Exchange(request(), PairingCode, handler);
            Check(Flag(missing, "ok"), "actual net48 readonly recovery handles missing history");
            var recovery = (Dictionary<string, object>)missing["recovery"];
            Check(Text(recovery, "outcome") == "manual_review_required" && Flag(recovery, "manualReviewAllowed"),
                "explicit missing native history permits manual review only, with a real boolean");
            Check(Text(recovery, "operationId") == operationId && Text(recovery, "projectFingerprint") == fingerprint &&
                Text(recovery, "recordId") == recordId && Text(recovery, "panelId") == panelId && Text(recovery, "expectedHardwareId") == sensor,
                "authenticated recovery preserves all five immutable identities");
            Check(!missing.ContainsKey("operation") && saves == 0 && mutatingCalls == 0,
                "missing history is not fabricated as cancelled/registered and never calls native start/cancel");

            nativeState = "waiting_sensor";
            var active = Exchange(request(), PairingCode, handler);
            recovery = (Dictionary<string, object>)active["recovery"];
            Check(Flag(active, "ok") && Text(recovery, "outcome") == "native_active" &&
                recovery.ContainsKey("manualReviewAllowed") && !Flag(recovery, "manualReviewAllowed"),
                "active native result retains false and blocks manual retirement on actual wire");
            Check(saves == 0 && mutatingCalls == 0, "active native lookup does not write commissioning state or cancel");

            int beforeWrongProject = readonlyCalls;
            var wrongProject = request(); wrongProject["projectFingerprint"] = new string('f', 64);
            var rejected = Exchange(wrongProject, PairingCode, handler);
            Check(!Flag(rejected, "ok") && !rejected.ContainsKey("operation") && readonlyCalls == beforeWrongProject,
                "wrong portable project is rejected before native reads and cannot expose an old operation");

            nativeState = "registered";
            var restored = Exchange(request(), PairingCode, handler);
            Check(Flag(restored, "ok") && restored.ContainsKey("operation"), "retained native registration restored over actual net48 transport");
            var operation = (Dictionary<string, object>)restored["operation"];
            Check(Text(operation, "state") == "registered" && Flag(operation, "registrationConfirmed") &&
                Text(operation, "confirmedHardwareId") == sensor && Flag(operation, "projectSaved"),
                "restored registration has exact hardware identity and separately acknowledged project save");
            Check(saves == 1 && File.Exists(Path.Combine(directory, "journal.json")), "restored result is durable and saved exactly once");
            int beforeKnown = readonlyCalls;
            var known = Exchange(request(), PairingCode, handler);
            Check(Flag(known, "ok") && readonlyCalls == beforeKnown && saves == 1,
                "second recovery reads durable bridge result without replaying native calls or project save");
            Check(mutatingCalls == 0, "entire production readonly recovery path sends zero native start/cancel commands");
            var report = (string)Type("RegistrationCoordinator").GetProperty("ConnectionDiagnosticReport").GetValue(coordinator);
            Check(!report.Contains(PairingCode), "new journal/recovery diagnostics do not reveal the pairing secret");
            Console.WriteLine("Read-only recovery through production EXE and ProtocolV3: PASS; original identities, missing/active/restored, no start/cancel.");
        }
        finally { ((IDisposable)coordinator).Dispose(); }
    }

    private static void VerifyDiagnosticSerialization()
    {
        const string secret = "SYNTHETIC_ROUTE_SECRET_NOT_FOR_DIAGNOSTICS";
        const string expectedAdapter = "41111111-1111-4111-8111-111111111111";
        const string actualOwner = "42222222-2222-4222-8222-222222222222";
        const string expectedRouteOwner = "43333333-3333-4333-8333-333333333333";
        const string actualRouteOwner = "44444444-4444-4444-8444-444444444444";
        var supplied = new Dictionary<string, object> {
            { "check", "endpoint_owner_mismatch" }, { "routeKind", "direct_mk_ip" },
            { "actualConnectionKind", "direct_mk_ip" }, { "activeTransportType", "WebPort" },
            { "expectedHost", "192.168.0.240" }, { "expectedPort", 6344 },
            { "expectedModuleId", "fb0000000003" }, { "pluginVersion", "1.0.5.0" }, { "forceUsbScheme", true },
            { "nativeCompletionState", "blocked" }, { "nativeCompletionCheck", "success_acknowledgement_timeout" },
            { "nativeCompletionLastCheck", "success_window_not_found" }, { "nativeSuccessDialogSuppressed", false },
            { "suppressNativeSuccessDialog", true },
            { "actualModuleIdState", "missing" }, { "moduleIdentitySource", "project_adapter_and_native_route" },
            { "projectModuleId", "fb0000000003" }, { "nativeRouteCacheMatched", true },
            { "nativeCloudEnabled", false }, { "nativeDirectEnabled", true }, { "nativeRoutePasswordMatched", false },
            { "expectedAdapterNodeId", expectedAdapter }, { "actualAdapterOwnerId", actualOwner },
            { "expectedRouteOwnerId", expectedRouteOwner }, { "actualRouteOwnerId", actualRouteOwner },
            { "actualHost", "tcp://user:" + secret + "@192.168.0.241" },
            { "actualModuleId", secret }, { "actualPort", 65536 }, { "password", secret },
            { "rawEndpoint", secret }, { "nested", new Dictionary<string, object> { { "deviceKey", secret } } }
        };
        // Both crossings use the production EXE's net48 JavaScriptSerializer,
        // then the same allowlist used by its journal and operation responses.
        var decoded = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", Invoke("BridgeJson", "Serialize", supplied));
        var safe = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", decoded);
        string cleanJson = (string)Invoke("BridgeJson", "Serialize", safe);
        var restored = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", cleanJson);
        Check(Text(restored, "check") == "endpoint_owner_mismatch", "actual net48 serializer retains the exact owner check");
        Check(Text(restored, "actualConnectionKind") == "direct_mk_ip" && Text(restored, "activeTransportType") == "WebPort",
            "actual net48 serializer retains reviewed native transport identity");
        Check(Text(restored, "expectedHost") == "192.168.0.240" && Convert.ToInt32(restored["expectedPort"]) == 6344,
            "actual net48 serializer retains the bounded IP endpoint");
        Check(Text(restored, "expectedModuleId") == "FB0000000003" && Text(restored, "pluginVersion") == "1.0.5.0",
            "actual net48 serializer preserves normalized module ID and loaded plugin version");
        Check(Text(restored, "actualModuleIdState") == "missing" && Text(restored, "moduleIdentitySource") == "project_adapter_and_native_route" &&
            Text(restored, "projectModuleId") == "FB0000000003" && !restored.ContainsKey("actualModuleId"),
            "actual net48 serializer keeps project ID distinct from the absent URI ID");
        Check(Flag(restored, "nativeRouteCacheMatched") && restored.ContainsKey("nativeCloudEnabled") && !Flag(restored, "nativeCloudEnabled") &&
            Flag(restored, "nativeDirectEnabled") && restored.ContainsKey("nativeRoutePasswordMatched") && !Flag(restored, "nativeRoutePasswordMatched"),
            "actual net48 serializer retains true and false native-route evidence without inventing confirmation");
        Check(Flag(restored, "forceUsbScheme"), "actual net48 serializer preserves boolean diagnostic metadata");
        Check(Text(restored, "nativeCompletionState") == "blocked" &&
            Text(restored, "nativeCompletionCheck") == "success_acknowledgement_timeout" &&
            Text(restored, "nativeCompletionLastCheck") == "success_window_not_found",
            "actual net48 serializer keeps the last specific success-window check separate from timeout");
        Check(restored.ContainsKey("nativeSuccessDialogSuppressed") && !Flag(restored, "nativeSuccessDialogSuppressed") &&
            Flag(restored, "suppressNativeSuccessDialog"),
            "actual net48 serializer preserves distinct false per-operation and true capability flags");
        Check(Text(restored, "expectedAdapterNodeId") == expectedAdapter && Text(restored, "actualAdapterOwnerId") == actualOwner,
            "actual net48 serializer preserves expected project adapter and actual endpoint owner separately");
        Check(Text(restored, "expectedRouteOwnerId") == expectedRouteOwner && Text(restored, "actualRouteOwnerId") == actualRouteOwner,
            "actual net48 serializer keeps terminal route owners distinct from the physical MK-IP owners");
        Check(!cleanJson.Contains(secret) && !restored.ContainsKey("actualHost") && !restored.ContainsKey("actualModuleId") &&
            !restored.ContainsKey("actualPort") && !restored.ContainsKey("password") && !restored.ContainsKey("rawEndpoint") &&
            !restored.ContainsKey("nested"), "actual net48 sanitizer drops secrets, raw URI, nested fields and out-of-range ports");
        var invalidOwnerInput = new Dictionary<string, object> {
            { "expectedAdapterNodeId", secret }, { "actualAdapterOwnerId", Guid.Empty.ToString("D") },
            { "expectedRouteOwnerId", Guid.Empty.ToString("D") }, { "actualRouteOwnerId", secret }
        };
        var invalidOwnerRoundtrip = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", Invoke("BridgeJson", "Serialize", invalidOwnerInput));
        var invalidOwners = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", invalidOwnerRoundtrip);
        Check(!invalidOwners.ContainsKey("expectedAdapterNodeId") && !invalidOwners.ContainsKey("actualAdapterOwnerId") &&
            !invalidOwners.ContainsKey("expectedRouteOwnerId") && !invalidOwners.ContainsKey("actualRouteOwnerId"),
            "actual net48 sanitizer drops malformed and empty owner GUIDs");
        var invalidModuleInput = new Dictionary<string, object> {
            { "actualModuleIdState", secret }, { "moduleIdentitySource", secret }, { "projectModuleId", secret },
            { "nativeRouteCacheMatched", "true" }, { "nativeCloudEnabled", 0 },
            { "nativeDirectEnabled", null }, { "nativeRoutePasswordMatched", secret }, { "rawSerialNumber", secret }
        };
        var invalidModuleRoundtrip = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", Invoke("BridgeJson", "Serialize", invalidModuleInput));
        var invalidModule = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", invalidModuleRoundtrip);
        Check(invalidModule.Count == 0, "actual net48 sanitizer rejects arbitrary module enum text, raw serial and non-boolean route evidence");
        foreach (object malformed in new object[] { "true", 0, secret, new Dictionary<string, object> { { "secret", secret } }, null })
        {
            var invalidCompletion = new Dictionary<string, object> {
                { "nativeCompletionLastCheck", malformed }, { "nativeSuccessDialogSuppressed", malformed },
                { "suppressNativeSuccessDialog", malformed }
            };
            var completionRoundtrip = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", Invoke("BridgeJson", "Serialize", invalidCompletion));
            var cleanedCompletion = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", completionRoundtrip);
            Check(cleanedCompletion.Count == 0, "actual net48 sanitizer rejects malformed success-window check and boolean flags");
        }
        foreach (string completionCheck in new[] { "awaiting_native_completion", "native_completion_timeout" })
        {
            var completion = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", new Dictionary<string, object> {
                { "nativeCompletionCheck", completionCheck }, { "nativeCompletionLastCheck", completionCheck }
            });
            Check(Text(completion, "nativeCompletionCheck") == completionCheck && Text(completion, "nativeCompletionLastCheck") == completionCheck,
                "actual net48 sanitizer retains the bounded native-completion gate independently of success-window acknowledgement");
        }
        foreach (string check in new[] { "endpoint_module_missing", "endpoint_module_invalid", "endpoint_native_route_mismatch" })
        {
            var checkDetail = (Dictionary<string, object>)Invoke("RegistrationDiagnosticData", "Details", new Dictionary<string, object> { { "check", check } });
            Check(Text(checkDetail, "check") == check, "production EXE retains precise optional-module check: " + check);
        }

        var response = new Dictionary<string, object> {
            { "protocol", 3 }, { "ok", true }, { "requestId", Guid.NewGuid().ToString("D") },
            { "operation", new Dictionary<string, object> {
                { "state", "failed" }, { "errorCode", "native_adapter_mismatch" }, { "errorPhase", "native_operation" },
                { "registrationConfirmed", false }, { "diagnosticDetails", restored }
            } }
        };
        var protectedResponse = Invoke("ProtocolV3", "Encode", response, PairingCode, "response");
        string wire = (string)Invoke("BridgeJson", "Serialize", protectedResponse);
        var wireObject = (Dictionary<string, object>)Invoke("BridgeJson", "ParseObject", wire);
        var received = (Dictionary<string, object>)Invoke("ProtocolV3", "Decode", wireObject, PairingCode, "response");
        var operation = (Dictionary<string, object>)received["operation"];
        var receivedDetails = (Dictionary<string, object>)operation["diagnosticDetails"];
        Check(Text(receivedDetails, "nativeCompletionCheck") == "success_acknowledgement_timeout" &&
            Text(receivedDetails, "nativeCompletionLastCheck") == "success_window_not_found" &&
            receivedDetails.ContainsKey("nativeSuccessDialogSuppressed") && !Flag(receivedDetails, "nativeSuccessDialogSuppressed") &&
            Flag(receivedDetails, "suppressNativeSuccessDialog") && !Flag(operation, "registrationConfirmed"),
            "authenticated net48 diagnostic response preserves previous completion telemetry without confirming this operation");
        Check(Text(operation, "errorCode") == "native_adapter_mismatch" && Text(operation, "errorPhase") == "native_operation",
            "authenticated net48 response preserves exact registration code and phase");
        Check(Text(receivedDetails, "check") == "endpoint_owner_mismatch" && Text(receivedDetails, "pluginVersion") == "1.0.5.0" &&
            !Flag(operation, "registrationConfirmed") && !Json.Serialize(received).Contains(secret),
            "authenticated net48 diagnostic response preserves the refusal without secrets or false confirmation");
        Check(Text(receivedDetails, "expectedAdapterNodeId") == expectedAdapter && Text(receivedDetails, "actualAdapterOwnerId") == actualOwner &&
            Text(receivedDetails, "expectedRouteOwnerId") == expectedRouteOwner && Text(receivedDetails, "actualRouteOwnerId") == actualRouteOwner,
            "authenticated net48 response retains the exact distinct owner GUIDs");
        Check(Text(receivedDetails, "actualModuleIdState") == "missing" && Text(receivedDetails, "moduleIdentitySource") == "project_adapter_and_native_route" &&
            Text(receivedDetails, "projectModuleId") == "FB0000000003" && !receivedDetails.ContainsKey("actualModuleId") &&
            Flag(receivedDetails, "nativeRouteCacheMatched") && !Flag(receivedDetails, "nativeCloudEnabled") && Flag(receivedDetails, "nativeDirectEnabled") &&
            receivedDetails.ContainsKey("nativeRoutePasswordMatched") && !Flag(receivedDetails, "nativeRoutePasswordMatched"),
            "authenticated response preserves explicit missing module and route evidence without fabricating endpoint ID");
        Console.WriteLine("Diagnostic scalar allowlist and ProtocolV3 response: PASS; no native driver call");
    }

    private static Dictionary<string, object> Exchange(Dictionary<string, object> request, string requestKey,
        Func<Dictionary<string, object>, Dictionary<string, object>> handler, int fragmentDelayMilliseconds = 0)
    {
        int port;
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var receiver = Activator.CreateInstance(Type("LanReceiver"), new object[] { port, PairingCode, null, handler });
        Exception serverError = null;
        var thread = new Thread(delegate() {
            try { Type("LanReceiver").GetMethod("ReceiveOnce").Invoke(receiver, null); }
            catch (Exception error) { serverError = error; }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            using (var client = new TcpClient())
            {
                Exception connectError = null;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try { client.Connect(IPAddress.Loopback, port); connectError = null; break; }
                    catch (SocketException error) { connectError = error; Thread.Sleep(25); }
                }
                if (connectError != null) throw connectError;
                client.ReceiveTimeout = fragmentDelayMilliseconds > 0 ? 20000 : 10000; client.SendTimeout = 10000;
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    var envelope = Invoke("ProtocolV3", "Encode", request, requestKey, "request");
                    string encoded = Json.Serialize(envelope);
                    if (fragmentDelayMilliseconds > 0)
                    {
                        int split = encoded.Length / 2;
                        writer.Write(encoded.Substring(0, split));
                        writer.Flush();
                        Thread.Sleep(fragmentDelayMilliseconds);
                        writer.WriteLine(encoded.Substring(split));
                    }
                    else writer.WriteLine(encoded);
                    string line = reader.ReadLine();
                    Check(!String.IsNullOrEmpty(line), "production receiver returned complete line");
                    lastResponseBytes = Encoding.UTF8.GetByteCount(line);
                    Check(lastResponseBytes <= 1500000, "response obeys wire bound");
                    return (Dictionary<string, object>)Invoke("ProtocolV3", "Decode", Parse(line), PairingCode, "response");
                }
            }
        }
        finally
        {
            ((IDisposable)receiver).Dispose();
            if (!thread.Join(3000)) throw new TimeoutException("Loopback receiver did not finish.");
            if (serverError != null) throw serverError;
        }
    }

    private static Dictionary<string, object> Request()
    {
        return new Dictionary<string, object> {
            { "protocol", 3 }, { "type", "get_project" }, { "requestId", Guid.NewGuid().ToString("D") },
            { "sentAt", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds }
        };
    }
    private static Type Type(string name) { return bridge.GetType("StreletzBridge." + name, true); }
    private static object Invoke(string type, string method, params object[] args)
    { return Type(type).GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, args); }
    private static Dictionary<string, object> Parse(string value) { return (Dictionary<string, object>)Json.DeserializeObject(value); }
    private static object[] Array(Dictionary<string, object> value, string key) { return (object[])value[key]; }
    private static string Text(Dictionary<string, object> value, string key) { object v; return value.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
    private static bool Flag(Dictionary<string, object> value, string key) { object v; return value.TryGetValue(key, out v) && v is bool && (bool)v; }
    private static string Sha(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
}
