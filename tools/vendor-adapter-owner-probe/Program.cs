// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.Serialization;

// Synthetic URI/DTO and endpoint-policy probe. Executes the real vendor URI
// converter and production plugin endpoint checks, without creating a window,
// opening a transport, connecting to hardware, or invoking registration.
internal static class Program
{
    private const string MkIp = "11111111-2222-4333-8444-555555555555";
    private const string MainPanel = "22222222-3333-4444-8555-666666666666";
    private const string MainSegment = "33333333-4444-4555-8666-777777777777";
    private const string TerminalSegment = "44444444-5555-4666-8777-888888888888";
    private const string Foreign = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string Hardware = "112233AABBCC";
    private static Type dtoType, converterType, tunnelType, diagnosticType;
    private static object native, panel;
    private static MethodInfo directCheck, terminalCheck;
    private static int checks;

    public sealed class DeviceInfo { public byte[] ID { get; set; } }
    public sealed class Target { public Guid SegmentGuid { get; set; } }
    public sealed class ProjectNode { public Guid Guid { get; set; } public Guid SegmentGuid { get; set; } public object Info { get; set; } }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: VendorAdapterOwnerProbe.exe <official Master directory> <built plugin DLL>"); return 2; }
        try
        {
            string dir = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, resolution) => {
                string path = Path.Combine(dir, new AssemblyName(resolution.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Assembly core = Assembly.LoadFrom(Path.Combine(dir, "Core.dll"));
            Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            plugin.GetType("Streletz.MasterBridge.VersionGuard", true).GetMethod("Verify", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { dir });
            dtoType = core.GetType("Core.Adapters.AdapterConnectionDto", true);
            converterType = core.GetType("Core.Adapters.AdapterUrlConverter", true);
            tunnelType = core.GetType("Core.Adapters.TunnelDto", true);
            Type nativeType = plugin.GetType("Streletz.MasterBridge.NativeContext", true);
            diagnosticType = plugin.GetType("Streletz.MasterBridge.NativeAdapterDiagnostic", true);
            native = Activator.CreateInstance(nativeType, true);
            panel = Activator.CreateInstance(plugin.GetType("Streletz.MasterBridge.PanelRequest", true), true);
            Set(panel, "host", "127.0.0.1"); Set(panel, "port", 6344); Set(panel, "moduleId", Hardware); Set(panel, "nativePanelId", MainPanel);
            directCheck = nativeType.GetMethod("VerifyDirectEndpoint", BindingFlags.NonPublic | BindingFlags.Instance);
            terminalCheck = nativeType.GetMethod("VerifyTerminalInterface", BindingFlags.NonPublic | BindingFlags.Instance);
            Check(directCheck != null && terminalCheck != null, "Production endpoint checks exist.");

            string valid = Direct(MkIp, Hardware, "127.0.0.1", 6344, true, true);
            object parsed = Activator.CreateInstance(converterType, new object[] { new Uri(valid) });
            object parameters = Get(parsed, "ConnectionParams");
            Check((Guid)Get(parsed, "AdapterOwnerGuid") == Guid.Parse(MkIp), "Official URI roundtrip retains the exact project MK-IP owner GUID.");
            Check((string)Get(parameters, "ConnectionIpAddr") == "127.0.0.1" && Convert.ToInt32(Get(parameters, "ConnectionIpPort")) == 6344, "Official URI retains IP and port.");
            Check((string)Get(parameters, "SerialNumber") == Hardware, "Official URI retains the full six-byte module ID.");
            Check((bool)Get(parameters, "ForceUsbScheme") && (bool)Get(parsed, "IsDirectConnectionEnabled") && Get(parameters, "Tunnel") == null, "Official URI retains MK-IP framing, direct flag and absence of tunnel.");
            Check(DirectResult(valid, "", MkIp) == "verified", "Correct project owner authorizes endpoint with the native adapter segment hint unset.");
            Check(DirectResult(valid, Foreign, MkIp) == "verified", "Mutable segment hint does not override the exact URI project owner.");
            Check(DirectResult(Direct(Foreign, Hardware, "127.0.0.1", 6344, true, true), "", MkIp) == "endpoint_owner_mismatch", "Same physical endpoint with foreign project owner is rejected.");
            Check(DirectResult(Direct(Guid.Empty.ToString(), Hardware, "127.0.0.1", 6344, true, true), "", MkIp) == "endpoint_owner_missing", "Missing URI owner is rejected.");
            Check(DirectResult(valid, "", "") == "endpoint_owner_missing", "Missing expected project adapter identity is rejected.");
            Check(DirectResult(Direct(MkIp, "112233445577", "127.0.0.1", 6344, true, true), "", MkIp) == "endpoint_module_mismatch", "Correct owner cannot authorize another full hardware ID.");
            Check(DirectResult(Direct(MkIp, Hardware, "127.0.0.2", 6344, true, true), "", MkIp) == "endpoint_host_mismatch", "Correct owner cannot authorize another IP.");
            Check(DirectResult(Direct(MkIp, Hardware, "127.0.0.1", 6345, true, true), "", MkIp) == "endpoint_port_mismatch", "Correct owner cannot authorize another TCP port.");
            Check(DirectResult(Direct(MkIp, Hardware, "127.0.0.1", 6344, false, true), "", MkIp) == "endpoint_direct_disabled", "Cloud-only URI cannot authorize direct route.");
            string missingSerial = Direct(MkIp, null, "127.0.0.1", 6344, true, true);
            Check(DirectResult(missingSerial, "", MkIp) == "endpoint_module_missing", "Official direct-only DTO without cloud serial requires native project-route proof.");
            foreach (string invalid in new[] { "11223344", " ", "000000000000", "FFFFFFFFFFFF", "11223344ZZ66", "112233AABBCCDD" })
                Check(DirectResult(Direct(MkIp, invalid, "127.0.0.1", 6344, true, true), "", MkIp) == "endpoint_module_invalid",
                    "Present malformed or short URI serial remains a rejection in the production endpoint check.");
            Type arrays = core.GetType("Core.Utils.Arrays", true);
            string canonicalSerial = (string)arrays.GetMethod("ArrayToString", new[] { typeof(byte[]) }).Invoke(null,
                new object[] { new byte[] { 0x11, 0x22, 0x33, 0xaa, 0xbb, 0xcc } });
            Check(canonicalSerial == "11 22 33 AA BB CC", "Official full-ID formatting uses ordinary ASCII spaces.");
            Check(DirectResult(Direct(MkIp, canonicalSerial, "127.0.0.1", 6344, true, true), "", MkIp) == "verified",
                "The production full-ID gate accepts the real vendor canonical SerialNumber without relaxing normalization.");
            Check(DirectResult(Direct(Foreign, null, "127.0.0.1", 6344, true, true), "", MkIp) == "endpoint_owner_mismatch",
                "An omitted cloud serial never bypasses the exact project adapter owner.");
            Check(DirectResult(Direct(MkIp, null, "127.0.0.2", 6344, true, true), "", MkIp) == "endpoint_host_mismatch",
                "An omitted cloud serial never bypasses the exact IP address.");
            Check(DirectResult(Direct(MkIp, null, "127.0.0.1", 6345, true, true), "", MkIp) == "endpoint_port_mismatch",
                "An omitted cloud serial never bypasses the exact TCP port.");

            VerifyRealNativeRouteReader(dir, plugin, nativeType, canonicalSerial);

            object operation = Activator.CreateInstance(plugin.GetType("Streletz.MasterBridge.OperationState", true), true);
            object request = Activator.CreateInstance(plugin.GetType("Streletz.MasterBridge.StartRequest", true), true);
            Set(request, "panel", panel); Field(operation, "Request", request);
            Field(operation, "TargetNode", new Target { SegmentGuid = Guid.Parse(MainSegment) });
            Field(operation, "AdapterNodeId", MkIp); Field(operation, "TerminalSegmentId", TerminalSegment);
            Field(operation, "TunnelStartMmpiAddress", "32.2"); Field(operation, "TunnelEndMmpiAddress", "2.4");
            Field(operation, "AdapterNode", new ProjectNode { Guid = Guid.Parse(MkIp), SegmentGuid = Guid.Parse(TerminalSegment),
                Info = new DeviceInfo { ID = new byte[] { 0x11, 0x22, 0x33, 0xaa, 0xbb, 0xcc } } });
            Check(TerminalResult(operation, valid) == "verified", "Physical terminal interface belongs to the MK-IP, not the main panel.");
            Check(TerminalResult(operation, Direct(MainPanel, Hardware, "127.0.0.1", 6344, true, true)) == "terminal_interface_owner_mismatch", "Main-panel GUID cannot impersonate the physical MK-IP.");
            Check(TerminalResult(operation, Direct(Foreign, Hardware, "127.0.0.1", 6344, true, true)) == "terminal_interface_owner_mismatch", "Foreign physical module owner is rejected.");
            Check(TerminalResult(operation, Direct(Guid.Empty.ToString(), Hardware, "127.0.0.1", 6344, true, true)) == "terminal_interface_owner_missing", "Missing physical owner is rejected.");
            Check(TerminalResult(operation, Direct(MkIp, "112233AABBDD", "127.0.0.1", 6344, true, true)) != "verified", "Changed module ID is rejected.");
            Check(TerminalResult(operation, Direct(MkIp, Hardware, "127.0.0.2", 6344, true, true)) != "verified", "Changed physical host is rejected.");
            Check(TerminalResult(operation, Direct(MkIp, Hardware, "127.0.0.1", 6345, true, true)) != "verified", "Changed physical port is rejected.");
            Check(TerminalResult(operation, Outer(MkIp, valid, TerminalSegment, MainSegment)) != "verified", "Outer tunnel URI cannot masquerade as the physical interface.");
            VerifyRealPhysicalTerminal(dir, plugin, nativeType, operation, valid);
            Console.WriteLine("Plugin file version: " + System.Diagnostics.FileVersionInfo.GetVersionInfo(args[1]).FileVersion);
            Console.WriteLine("PASS " + checks + " official converter and production endpoint/route-reader assertions; synthetic data only; real vendor converter and readonly info getters executed, no GUI/transport/hardware/registration.");
            return 0;
        }
        catch (Exception error)
        {
            while (error.InnerException != null) error = error.InnerException;
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1;
        }
    }

    private static object Get(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    private static void Field(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
    private static string Code(object diagnostic) { return (string)diagnosticType.GetField("check").GetValue(diagnostic); }
    private static string Encode(object dto, string owner, bool direct, byte[] password = null) {
        object converter = Activator.CreateInstance(converterType, new object[] { dto, password ?? new byte[16], Guid.Parse(owner), direct });
        return ((Uri)converterType.GetMethod("GetAdapterUri").Invoke(converter, new object[] { false })).OriginalString;
    }
    private static string Direct(string owner, string module, string host, int port, bool direct, bool framing, byte[] password = null) {
        object dto = Activator.CreateInstance(dtoType);
        Set(dto, "ConnectionIpAddr", host); Set(dto, "ConnectionIpPort", port); Set(dto, "SerialNumber", module);
        Set(dto, "ForceUsbScheme", framing); Set(dto, "UsbDevice", "synthetic-usb-endpoint");
        return Encode(dto, owner, direct, password);
    }
    private static string Outer(string owner, string inner, string start, string end, string from = "32.2", string to = "2.4") {
        object tunnel = Activator.CreateInstance(tunnelType), dto = Activator.CreateInstance(dtoType);
        Set(tunnel, "StartPointSegment", start); Set(tunnel, "EndPointSegment", end);
        Set(tunnel, "StartPointMmpiAddress", from); Set(tunnel, "EndPointMmpiAddress", to);
        Set(tunnel, "StartPointAdapterName", inner); Set(dto, "Tunnel", tunnel); return Encode(dto, owner, false);
    }
    private static string DirectResult(string uri, string segmentHint, string expectedOwner) {
        object diagnostic = Activator.CreateInstance(diagnosticType, true); Field(diagnostic, "adapterSegmentId", segmentHint);
        bool result = (bool)directCheck.Invoke(native, new[] { panel, expectedOwner, uri, diagnostic });
        return result ? "verified" : Code(diagnostic);
    }
    private static string TerminalResult(object operation, string uri) {
        object diagnostic = Activator.CreateInstance(diagnosticType, true);
        bool result = (bool)terminalCheck.Invoke(native, new[] { operation, uri, diagnostic });
        return result ? "verified" : Code(diagnostic);
    }

    private static void VerifyRealPhysicalTerminal(string dir, Assembly plugin, Type nativeType, object operation, string endpoint)
    {
        Assembly ethernet = Assembly.LoadFrom(Path.Combine(dir, "UsbEthernetAdapter.dll"));
        Type proxy = ethernet.GetType("UsbEthernetAdapter.UsbProxyAdapter", true), physicalType = ethernet.GetType("UsbEthernetAdapter.EthernetUSB", true);
        MethodInfo getName = proxy.GetMethod("GetInterfaceName");
        byte[] il = getName.GetMethodBody().GetILAsByteArray();
        // The statically inspected getter begins ldarg.0 / ldfld and returns
        // this non-null field unchanged. Never call Open or a constructor.
        Check(il[0] == 0x02 && il[1] == 0xfe && il[2] == 0x13 && il[3] == 0x7b, "Real physical GetInterfaceName starts with its backing field.");
        FieldInfo name = proxy.Module.ResolveField(BitConverter.ToInt32(il, 4));
        Check(name.DeclaringType == proxy && name.FieldType == typeof(string) && !name.IsStatic, "Native interface-name field identity is exact.");
        object physical = FormatterServices.GetUninitializedObject(physicalType); GC.SuppressFinalize(physical);
        name.SetValue(physical, endpoint);
        Check((string)getName.Invoke(physical, null) == endpoint, "Real vendor getter returns the physical URI unchanged.");
        object tunnel = Activator.CreateInstance(tunnelType);
        Set(tunnel, "StartPointSegment", TerminalSegment); Set(tunnel, "EndPointSegment", MainSegment);
        Set(tunnel, "StartPointMmpiAddress", "32.2"); Set(tunnel, "EndPointMmpiAddress", "2.4"); Set(tunnel, "StartPointAdapterName", endpoint);
        FieldInfo tunnelField = proxy.Module.ResolveField(0x040000DB);
        Check(tunnelField.DeclaringType == proxy && tunnelField.IsInitOnly && tunnelField.FieldType == tunnelType, "Actual readonly tunnel field is retained.");
        tunnelField.SetValue(physical, tunnel);
        MethodInfo verify = nativeType.GetMethod("VerifyTerminalAdapter", BindingFlags.NonPublic | BindingFlags.Instance);
        Func<string> result = () => {
            object diagnostic = Activator.CreateInstance(diagnosticType, true);
            bool ok = (bool)verify.Invoke(native, new object[] { operation, physical, MainSegment, diagnostic });
            return ok ? "verified" : Code(diagnostic);
        };
        Check(result() == "verified", "Production terminal check accepts a real physical adapter with separate matching readonly tunnel; no transport opened.");
        Set(tunnel, "EndPointSegment", Foreign); Check(result() == "terminal_tunnel_mismatch", "Wrong destination segment is still rejected."); Set(tunnel, "EndPointSegment", MainSegment);
        Set(tunnel, "StartPointSegment", Foreign); Check(result() == "terminal_tunnel_mismatch", "Wrong source segment is still rejected."); Set(tunnel, "StartPointSegment", TerminalSegment);
        Set(tunnel, "StartPointMmpiAddress", "32.9"); Check(result() == "terminal_tunnel_mismatch", "Wrong source MMPI is still rejected."); Set(tunnel, "StartPointMmpiAddress", "32.2");
        Set(tunnel, "EndPointMmpiAddress", "2.9"); Check(result() == "terminal_tunnel_mismatch", "Wrong destination MMPI is still rejected."); Set(tunnel, "EndPointMmpiAddress", "2.4");
        Set(tunnel, "StartPointAdapterName", Direct(Foreign, Hardware, "127.0.0.1", 6344, true, true)); Check(result() != "verified", "Foreign endpoint inside readonly tunnel is rejected."); Set(tunnel, "StartPointAdapterName", endpoint);
        name.SetValue(physical, Direct(Foreign, Hardware, "127.0.0.1", 6344, true, true)); Check(result() == "terminal_interface_owner_mismatch", "Correct tunnel cannot authorize another physical adapter owner.");
        name.SetValue(physical, Direct(MkIp, Hardware, "127.0.0.2", 6344, true, true)); Check(result() != "verified", "Correct tunnel cannot authorize another physical host.");
        name.SetValue(physical, endpoint); Check(result() == "verified", "Restored matching route remains accepted.");
    }

    private static void VerifyRealNativeRouteReader(string dir, Assembly plugin, Type nativeType, string serial)
    {
        Type moduleType = Assembly.LoadFrom(Path.Combine(dir, "Plugin.MostIPI.dll")).GetType("MostIPI.Similar.MK_IP.MkIpInfo", true);
        Type segmentType = Assembly.LoadFrom(Path.Combine(dir, "Plugin.CorePlugins.dll")).GetType("CorePlugins.segment.CustomInfo", true);
        // Allocate synthetic info records without calling native constructors,
        // settings setters, the application manager or route-generation getters.
        object module = FormatterServices.GetUninitializedObject(moduleType), segmentInfo = FormatterServices.GetUninitializedObject(segmentType);
        byte[] password = new byte[16]; for (int i = 0; i < password.Length; i++) password[i] = (byte)(i + 1);
        PrivateField(module, "serialNumber", serial); PrivateField(module, "passwordMd5", password);
        PrivateField(segmentInfo, "lonInterface", Guid.Parse(MkIp));
        PrivateField(segmentInfo, "isCloudEnabled", false); PrivateField(segmentInfo, "isDirectConnectionEnabled", true);
        string endpoint = Direct(MkIp, null, "127.0.0.1", 6344, true, true, password);
        PrivateField(segmentInfo, "adapterNameValue", endpoint);
        var node = new ProjectNode { Guid = Guid.Parse(MkIp), SegmentGuid = Guid.Parse(MainSegment), Info = module };
        var segment = new ProjectNode { Guid = Guid.Parse(MainSegment), Info = segmentInfo };
        object converter = Activator.CreateInstance(converterType, new object[] { new Uri(endpoint) });
        byte[] decodedPassword = (byte[])Get(converter, "ConnectionPasswordMd5Code");
        MethodInfo reader = plugin.GetType("Streletz.MasterBridge.NativeProjectRouteProof", true).GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo load = nativeType.GetMethod("LoadedType", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo getProperty = nativeType.GetMethod("GetProperty", BindingFlags.Public | BindingFlags.Instance);
        Func<string, string, Type> loaded = (name, assembly) => (Type)load.Invoke(null, new object[] { name, assembly });
        Func<object, string, object> property = (value, name) => getProperty.Invoke(native, new object[] { value, name });
        Func<byte[], object> proof = credential => {
            object diagnostic = Activator.CreateInstance(diagnosticType, true);
            bool ok = (bool)reader.Invoke(null, new object[] { node, node, node, segment, MkIp, MainSegment, Hardware, endpoint, null,
                credential, loaded, property, diagnostic });
            return new object[] { ok, diagnostic };
        };
        Check(DirectResult(endpoint, "", MkIp) == "endpoint_module_missing", "Real omitted-serial URI reaches only the dedicated missing-ID branch.");
        object[] result = (object[])proof(decodedPassword);
        Check((bool)result[0] && (string)diagnosticType.GetField("moduleIdentitySource").GetValue(result[1]) == "project_adapter_and_native_route",
            "Production readonly reader accepts exact vendor segment cache, current six-byte ID and live credential binding.");
        Check((string)diagnosticType.GetField("actualModuleId").GetValue(result[1]) == "" &&
            (string)diagnosticType.GetField("projectModuleId").GetValue(result[1]) == Hardware,
            "Real native info proof keeps project identity separate from the absent URI serial.");
        PrivateField(segmentInfo, "isCloudEnabled", true);
        Check(!(bool)((object[])proof(decodedPassword))[0], "Real native cloud-enabled segment rejects an omitted cloud serial.");
        PrivateField(segmentInfo, "isCloudEnabled", false); PrivateField(segmentInfo, "isDirectConnectionEnabled", false);
        Check(!(bool)((object[])proof(decodedPassword))[0], "Real native direct-disabled segment rejects the direct route.");
        PrivateField(segmentInfo, "isDirectConnectionEnabled", true); PrivateField(segmentInfo, "adapterNameValue", null);
        Check(!(bool)((object[])proof(decodedPassword))[0] && segmentType.GetField("adapterNameValue", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(segmentInfo) == null,
            "A cleared real native cache is rejected without invoking AdapterName generation.");
        PrivateField(segmentInfo, "adapterNameValue", endpoint);
        byte[] wrongPassword = (byte[])decodedPassword.Clone(); wrongPassword[0] ^= 1;
        Check(!(bool)((object[])proof(wrongPassword))[0], "The real native password getter cannot authorize changed URI credentials.");
        PrivateField(module, "serialNumber", "11 22 33 AA BB DD");
        Check(!(bool)((object[])proof(decodedPassword))[0], "A changed full module ID in real native info rejects the cached route.");
    }
    private static void PrivateField(object value, string name, object fieldValue) {
        FieldInfo field = value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (field == null) throw new InvalidOperationException("Missing inspected synthetic-record field: " + name);
        field.SetValue(value, fieldValue);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
}
