// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Reflection;
using Streletz.MasterBridge;

internal static class NativeProjectRouteSelfTest
{
    private const string Adapter = "11111111-2222-4333-8444-555555555555";
    private const string Segment = "22222222-3333-4444-8555-666666666666";
    private const string Other = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string Module = "112233AABBCC";
    private const string Endpoint = "synthetic-native-route-with-PRIVATE-credentials";
    private static int checks;

    internal static int Run()
    {
        var module = new ModuleInfo { ID = new byte[] { 0x11, 0x22, 0x33, 0xaa, 0xbb, 0xcc }, ConnectionPasswordMD5Code = new byte[16] };
        var segmentInfo = new SegmentInfo { LonInterface = Guid.Parse(Adapter), IsCloudEnabled = false, IsDirectConnectionEnabled = true };
        segmentInfo.Cache(Endpoint);
        var node = new Node { Guid = Guid.Parse(Adapter), SegmentGuid = Guid.Parse(Segment), Info = module };
        var segment = new Node { Guid = Guid.Parse(Segment), Info = segmentInfo };
        NativeAdapterDiagnostic diagnostic;
        Check(Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A native cached direct-only route binds the live project module when cloud serial is omitted.");
        Check(diagnostic.actualModuleId == "" && diagnostic.projectModuleId == Module && diagnostic.moduleIdentitySource == "project_adapter_and_native_route",
            "Project module identity is explicit and never fabricated as an observed URI ID.");
        Check(diagnostic.nativeRouteCacheMatched == true && diagnostic.nativeRoutePasswordMatched == true && diagnostic.nativeCloudEnabled == false && diagnostic.nativeDirectEnabled == true,
            "Only parsed route-binding booleans become evidence.");
        Check(Verify(node, node, node, segment, "", new byte[16], out diagnostic), "Both vendor missing serial representations are supported.");
        segmentInfo.IsCloudEnabled = true;
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "Cloud-enabled native routes must retain the full URI module ID.");
        segmentInfo.IsCloudEnabled = null;
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "Unknown cloud setting cannot authorize omission.");
        segmentInfo.IsCloudEnabled = false; segmentInfo.IsDirectConnectionEnabled = false;
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "Direct connection must be explicitly enabled in the live segment.");
        segmentInfo.IsDirectConnectionEnabled = null;
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "Unknown direct setting cannot authorize omission.");
        segmentInfo.IsDirectConnectionEnabled = true; segmentInfo.Cache(null);
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A cleared native route cache is not silently regenerated.");
        segmentInfo.Cache(Endpoint + "stale");
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A different cached native route cannot authorize the opened endpoint.");
        segmentInfo.Cache(Endpoint);
        var wrongPassword = new byte[16]; wrongPassword[15] = 1;
        Check(!Verify(node, node, node, segment, null, wrongPassword, out diagnostic) && diagnostic.nativeRoutePasswordMatched == false,
            "Credentials changed after URI generation cannot authorize a stale native route.");
        Check(!Verify(node, node, node, segment, null, null, out diagnostic), "Missing native credential proof rejects the omitted serial.");
        Check(!Verify(node, node, node, segment, null, new byte[15], out diagnostic), "Invalid credential hash shape is rejected without disclosure.");
        module.ID[5] = 0xcd;
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic) && diagnostic.check == "endpoint_module_mismatch", "The current full project module ID must still match the requested module.");
        module.ID = new byte[4];
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A short project or USB ID never establishes six-byte module identity.");
        module.ID = new byte[] { 0x11, 0x22, 0x33, 0xaa, 0xbb, 0xcc };
        var copiedNode = new Node { Guid = node.Guid, SegmentGuid = node.SegmentGuid, Info = module };
        Check(!Verify(node, copiedNode, node, segment, null, new byte[16], out diagnostic), "A same-GUID detached node is not the live resolved project node.");
        Check(!Verify(node, node, copiedNode, segment, null, new byte[16], out diagnostic), "The segment's selected adapter must be the exact operation node.");
        node.Guid = Guid.Parse(Other);
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "Another adapter GUID cannot use the route proof.");
        node.Guid = Guid.Parse(Adapter); node.SegmentGuid = Guid.Parse(Other);
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A module in another project segment is rejected.");
        node.SegmentGuid = Guid.Parse(Segment); segment.Guid = Guid.Parse(Other);
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "The route's segment object must have the expected GUID.");
        segment.Guid = Guid.Parse(Segment); segmentInfo.LonInterface = Guid.Parse(Other);
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "A changed native LonInterface cannot authorize the old module.");
        segmentInfo.LonInterface = Guid.Parse(Adapter);
        node.Info = new object();
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "An uninspected device type cannot impersonate MK-IP.");
        node.Info = module; segment.Info = new object();
        Check(!Verify(node, node, node, segment, null, new byte[16], out diagnostic), "An uninspected segment type cannot supply a trusted route cache.");
        segment.Info = segmentInfo;
        foreach (string malformed in new[] { "11223344", " ", "PRIVATE", Module })
            Check(!Verify(node, node, node, segment, malformed, new byte[16], out diagnostic), "A present serial never enters the omitted-cloud-serial proof.");
        Check(!Verify(node, node, node, null, null, new byte[16], out diagnostic), "Missing live segment context rejects the fallback.");
        Check(Verify(node, node, node, segment, null, new byte[16], out diagnostic) && SegmentInfo.GetterCalls == 0,
            "Readonly proof never invokes public AdapterName or native route generation.");
        string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(diagnostic);
        Check(!json.Contains("PRIVATE") && !json.Contains(Endpoint) && !json.Contains("PasswordMD5Code"), "Neither cached URI nor credential hash leaks in module evidence.");
        return checks;
    }

    private static bool Verify(Node node, Node resolved, Node selected, Node segment, string serial, byte[] password, out NativeAdapterDiagnostic diagnostic)
    {
        diagnostic = new NativeAdapterDiagnostic { actualModuleIdState = String.IsNullOrEmpty(serial) ? "missing" : "invalid" };
        return NativeProjectRouteProof.Verify(node, resolved, selected, segment, Adapter, Segment, Module, Endpoint, serial, password,
            (name, assembly) => name == "MostIPI.Similar.MK_IP.MkIpInfo" ? typeof(ModuleInfo) : name == "CorePlugins.segment.CustomInfo" ? typeof(SegmentInfo) : null,
            (instance, name) => instance.GetType().GetProperty(name).GetValue(instance, null), diagnostic);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private sealed class Node { public Guid Guid { get; set; } public Guid SegmentGuid { get; set; } public object Info { get; set; } }
    private sealed class ModuleInfo { public byte[] ID { get; set; } public byte[] ConnectionPasswordMD5Code { get; set; } }
    private sealed class SegmentInfo
    {
        private string adapterNameValue;
        public static int GetterCalls;
        public Guid LonInterface { get; set; }
        public object IsCloudEnabled { get; set; }
        public object IsDirectConnectionEnabled { get; set; }
        public string AdapterName { get { GetterCalls++; throw new Exception("Native route generation must not run during proof."); } }
        public void Cache(string value) { adapterNameValue = value; }
    }
}
