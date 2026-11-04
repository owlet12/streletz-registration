// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;

// Runs only the official embedded device catalog, its string-key decoder and
// plain identity getters on synthetic info. Registration is verified from IL;
// no native plugin, UI command, connection or hardware operation is invoked.
internal static class VendorIb1Probe
{
    public sealed class Node { public object Info { get; set; } }
    private static int checks;
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
        .ToDictionary(op => unchecked((ushort)op.Value));

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: VendorIb1.Probe.exe <official Master directory> <built plugin DLL>"); return 2; }
        try
        {
            string dir = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, resolution) => {
                string file = Path.Combine(dir, new AssemblyName(resolution.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            Assembly bridge = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            bridge.GetType("Streletz.MasterBridge.VersionGuard", true).GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { dir });
            Check(true, "All 15 official Master components match the production SHA-256 pins.");
            Assembly core = Assembly.LoadFrom(Path.Combine(dir, "Core.dll"));
            Assembly radio = Assembly.LoadFrom(Path.Combine(dir, "Plugin.StreletcPro.dll"));
            Assembly gui = Assembly.LoadFrom(Path.Combine(dir, "Plugin.StreletcProGUI.dll"));
            Type catalogType = core.GetType("Core.DeviceTypes.DevicesCollection", true);
            object catalog = Activator.CreateInstance(catalogType);
            MethodInfo lookup = catalogType.GetMethod("GetDeviceDescription", new[] { typeof(string) });
            object description = lookup.Invoke(catalog, new object[] { "ИБ1 ПРО" });
            Check(Convert.ToInt32(Get(description, "Type")) == 128 && Convert.ToInt32(Get(description, "SubType")) == 98,
                "Official embedded catalog maps exact key ИБ1 ПРО to (128,98).");
            Check((string)Get(description, "Key") == "ИБ1 ПРО" && (string)Get(description, "NameLocKey") == "@+ИБ1-ПРО",
                "The catalog identity is the ordinary ИБ1-ПРО, without execution-2 substitution.");
            Check((string)Get(description, "Description") == "@+ИБ1-ПРО(DevDC)" && (string)Get(description, "Group") == "Устройство оповещения ПРО",
                "Official catalog retains the IB1 description and its vendor notification-device group.");
            object alternative = lookup.Invoke(catalog, new object[] { "ИБ1 ПРО Исп2" });
            Check(Convert.ToInt32(Get(alternative, "Type")) == 128 && Convert.ToInt32(Get(alternative, "SubType")) == 95,
                "Official execution 2 is a separate (128,95) catalog entry.");

            Type ib1 = radio.GetType("StreletcPro.IB1.Info", true);
            MethodBase register = radio.ManifestModule.ResolveMethod(0x0600003b);
            byte[] registrationIl = register.GetMethodBody().GetILAsByteArray();
            Check(registrationIl[0x227] == OpCodes.Callvirt.Value, "Pinned IB1 registration instruction remains a factory call at IL_0227.");
            MethodInfo registerInfo = (MethodInfo)radio.ManifestModule.ResolveMethod(BitConverter.ToInt32(registrationIl, 0x228));
            Check(registerInfo.Name == "RegisterCustomInfo" && registerInfo.DeclaringType.FullName == "Core.IDevFactory" &&
                registerInfo.GetGenericArguments().SequenceEqual(new[] { ib1 }) && registerInfo.GetParameters().Length == 1 &&
                registerInfo.GetParameters()[0].ParameterType == typeof(string), "Official factory registers the exact IB1.Info class by one catalog key.");
            Check(registrationIl[0x21d] == OpCodes.Ldc_I4.Value && registrationIl[0x222] == OpCodes.Call.Value,
                "Pinned registration key is generated directly by the inspected string decoder.");
            MethodInfo decode = (MethodInfo)radio.ManifestModule.ResolveMethod(BitConverter.ToInt32(registrationIl, 0x223));
            Check(decode.IsStatic && decode.ReturnType == typeof(string) && decode.GetParameters().Length == 1 &&
                decode.GetParameters()[0].ParameterType == typeof(uint), "Only the pure constant-string decoder is invoked for catalog-key evidence.");
            string registeredKey = (string)decode.Invoke(null, new object[] { unchecked((uint)BitConverter.ToInt32(registrationIl, 0x21e)) });
            Check(registeredKey == "ИБ1 ПРО", "IB1.Info registration key equals the authoritative (128,98) catalog key.");

            Type init = gui.GetType("StreletcProGUI.Commands.InitDevice", true);
            bool standardInit = CallsFrom(gui.GetType("StreletcProGUI.Plugin", true)).OfType<MethodInfo>().Any(m =>
                m.Name == "RegisterCustomCommand" && m.IsGenericMethod &&
                m.GetGenericArguments().SequenceEqual(new[] { ib1, init }));
            Check(standardInit, "Official GUI registers the ordinary InitDevice command for exact IB1.Info; inspected through IL only.");
            bool standardNode = CallsFrom(radio.GetType("StreletcPro.Plugin", true)).OfType<MethodInfo>().Any(m =>
                m.Name == "RegisterTreeNodeForInfo" && m.IsGenericMethod && m.GetGenericArguments()[0] == ib1 &&
                m.GetGenericArguments()[1].FullName == "Core.tree.StreletcProDeviceInOutExtinguisherTreeNode");
            Check(standardNode, "Official IB1.Info uses its ordinary radio in/out tree node.");

            PropertyInfo id = ib1.GetProperty("ID");
            Check(id != null && id.PropertyType == typeof(byte[]) && id.DeclaringType.FullName == "Core.StreletcProDeviceInOutInfo",
                "IB1 exposes the same full-byte-array ID contract consumed by the production identity check.");
            byte[] getter = id.GetGetMethod().GetMethodBody().GetILAsByteArray();
            Check(getter.Length == 7 && getter[0] == OpCodes.Ldarg_0.Value && getter[1] == OpCodes.Ldfld.Value && getter[6] == OpCodes.Ret.Value,
                "The inherited IB1 ID getter reads only its backing field.");
            object info = FormatterServices.GetUninitializedObject(ib1);
            id.Module.ResolveField(BitConverter.ToInt32(getter, 2)).SetValue(info, new byte[] { 0x11, 0x22, 0x33, 0xaa, 0xbb, 0xcc });
            Type nativeType = bridge.GetType("Streletz.MasterBridge.NativeContext", true);
            object native = Activator.CreateInstance(nativeType, true);
            Check((string)nativeType.GetMethod("GetHardwareId").Invoke(native, new object[] { new Node { Info = info } }) == "112233AABBCC",
                "Production GetHardwareId reads every byte of a synthetic official IB1 identity.");
            MethodInfo eligibility = bridge.GetType("Streletz.MasterBridge.NativeDeviceCatalog", true).GetMethod("IsSupported", BindingFlags.Static | BindingFlags.NonPublic);
            Func<Type> exact = () => ib1;
            Func<int, int, object, bool> eligible = (type, subtype, value) => (bool)eligibility.Invoke(null, new object[] { type, subtype, value, exact });
            Check(eligible(128, 98, info), "Actual production DLL accepts exact official IB1.Info at 128:98.");
            Check(!eligible(128, 95, info), "Actual production DLL does not admit execution 2 by the IB1 name.");
            Check(!eligible(128, 98, FormatterServices.GetUninitializedObject(radio.GetType("StreletcPro.IB1_isp2.Info", true))),
                "Actual production DLL rejects execution-2 class relabeled with ordinary IB1 subtype.");
            Check(!eligible(18, 98, info) && !eligible(128, 99, info), "Actual production DLL rejects another family and an unverified radio subtype.");
            Check(!eligible(128, 98, null), "Actual production DLL rejects absent IB1 info.");
            Check(Calls(nativeType.GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)).Any(m => m.DeclaringType == eligibility.DeclaringType && m.Name == eligibility.Name),
                "Production Snapshot delegates eligibility to the verified catalog gate.");
            Check(bridge.GetName().Version.ToString() == "1.0.0.0" && System.Diagnostics.FileVersionInfo.GetVersionInfo(args[1]).FileVersion == "1.0.6.0",
                "Plugin assembly identity stays 1.0.0.0 and file version advances to 1.0.6.0.");
            Console.WriteLine("PASS " + checks + " official IB1 catalog, class, command and production-DLL assertions; no GUI, connection, registration or hardware.");
            return 0;
        }
        catch (Exception error)
        {
            while (error.InnerException != null) error = error.InnerException;
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1;
        }
    }

    private static object Get(object instance, string property) { return instance.GetType().GetProperty(property).GetValue(instance, null); }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static IEnumerable<MethodBase> CallsFrom(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).SelectMany(Calls);
    }
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        MethodBody body = method.GetMethodBody();
        if (body == null) yield break;
        byte[] il = body.GetILAsByteArray();
        for (int offset = 0; offset < il.Length;)
        {
            ushort value = il[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | il[offset++]);
            OpCode op = Opcodes[value];
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), method.DeclaringType.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: offset++; break;
                case OperandType.InlineVar: offset += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: offset += 8; break;
                case OperandType.InlineSwitch: offset += 4 + 4 * BitConverter.ToInt32(il, offset); break;
                default: offset += 4; break;
            }
        }
    }
}
