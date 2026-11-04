// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;

// Read-only proof for the pinned official Master 12.4 success-message contract.
// Apart from VersionGuard and two constant-string decoders, no vendor code runs.
// Flattened control flow is evaluated with synthetic call/field results below;
// InitDu, MessageBox, labels, registration, adapters and hardware are never run.
internal static class VendorSuccessProbe
{
    private const string BodyKey = "@+IntDU.Succesfull";
    private const string CaptionKey = "@+iresult";
    private static int checks;
    private static readonly object InitInstance = new object();
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
        .ToDictionary(op => unchecked((ushort)op.Value));

    private static int Main(string[] args)
    {
        if (args.Length != 2 && !(args.Length == 3 && args[2] == "--metadata-only")) { Console.Error.WriteLine("Usage: VendorSuccess.Probe.exe <official Master directory> <built plugin DLL> [--metadata-only]"); return 2; }
        try
        {
            string dir = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, resolution) => {
                string file = Path.Combine(dir, new AssemblyName(resolution.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            Assembly bridge = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            Type guard = bridge.GetType("Streletz.MasterBridge.VersionGuard", true);
            Check(((IDictionary)guard.GetField("Expected", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count == 15,
                "Production guard has all 15 official Master component pins.");
            guard.GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { dir });
            Check(true, "All 15 official files match the production SHA-256 pins before metadata/decoder inspection.");
            Check(bridge.GetName().Version.ToString() == "1.0.0.0" && System.Diagnostics.FileVersionInfo.GetVersionInfo(args[1]).FileVersion == "1.0.9.0",
                "The inspected bridge keeps assembly 1.0.0.0 and file version 1.0.9.0.");

            Assembly gui = Assembly.LoadFrom(Path.Combine(dir, "Plugin.StreletcProGUI.dll"));
            Type init = gui.GetType("StreletcProGUI.Dialogs.InitDu", true);
            MethodInfo getter = init.GetProperty("Successfully").GetGetMethod();
            Instruction[] getterIl = Read(getter);
            Check(getter.MetadataToken == 0x06000055 && getter.ReturnType == typeof(bool) &&
                getterIl.Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ret }),
                "Successfully getter 06000055 reads only its Boolean backing field.");
            MethodBase completion = gui.ManifestModule.ResolveMethod(0x060001b2);
            MethodBase outer = gui.ManifestModule.ResolveMethod(0x060001bf);
            Check(completion.Name == "MoveNext" && completion.DeclaringType.DeclaringType == init &&
                outer.Name == "MoveNext" && outer.DeclaringType.DeclaringType == init,
                "060001B2 and 060001BF are the two inspected InitDu async continuation bodies.");
            var body = Read(completion).ToDictionary(x => x.Offset);
            var outerBody = Read(outer).ToDictionary(x => x.Offset);
            MethodBase setter = Called(completion, body[0x18a]);
            Instruction[] setterIl = Read(setter);
            Check(setter.IsPrivate && setter.DeclaringType == init && setter.GetParameters().Length == 1 && setter.GetParameters()[0].ParameterType == typeof(bool) &&
                setterIl.Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Stfld, OpCodes.Ret }) &&
                setterIl[2].Token == getterIl[1].Token,
                "The success continuation writes the same Successfully backing field, through its private Boolean setter.");
            Check(body[0x188].Op == OpCodes.Ldloc_1 && body[0x189].Op == OpCodes.Ldc_I4_1,
                "IL_0188/0189 passes this exact InitDu and true to Successfully at IL_018A.");
            var constructorWrites = init.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(Read).SelectMany(a => a.Select((x, n) => new { Body = a, Instruction = x, Index = n }))
                .Where(x => IsCall(x.Instruction) && x.Instruction.Token == setter.MetadataToken).ToArray();
            Check(constructorWrites.Length == 1 && constructorWrites[0].Index >= 2 &&
                constructorWrites[0].Body[constructorWrites[0].Index - 1].Op == OpCodes.Ldc_I4_0 &&
                constructorWrites[0].Body[constructorWrites[0].Index - 2].Op == OpCodes.Ldarg_0,
                "Construction initializes Successfully to false; it does not pre-authorize success.");

            MethodBase showFlag = Called(completion, body[0x190]);
            Instruction[] showIl = Read(showFlag);
            Check(showIl.Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ret }) &&
                showFlag.Module.ResolveField(showIl[1].Token).IsInitOnly,
                "The optional success-UI flag is a constructor-set readonly Boolean field.");
            Check(Called(completion, body[0x31e]).MetadataToken == 0x060001be && body[0x31d].Op == OpCodes.Ldc_I4_0,
                "The success call at IL_031E uses MessageBoxButtons.OK (0).");
            MethodBase message = SoleCall(gui.ManifestModule.ResolveMethod(0x060001be));
            Check(message.DeclaringType.FullName == "System.Windows.Forms.MessageBox" && message.Name == "Show" &&
                message.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] {
                    "System.String", "System.String", "System.Windows.Forms.MessageBoxButtons" }),
                "Success wrapper 060001BE is precisely Show(text, caption, buttons), with no explicit owner argument.");
            Check(body[0x323].Op == OpCodes.Pop,
                "Vendor consumes the success MessageBox result after it returns; the bridge need only acknowledge OK.");
            MethodBase localize = SoleCall(gui.ManifestModule.ResolveMethod(0x060001b5));
            Check(localize.DeclaringType.FullName == "Localization.LocString" && localize.Name == "KeyToString" &&
                Called(completion, body[0x309]).MetadataToken == 0x060001b5 && Called(completion, body[0x318]).MetadataToken == 0x060001b5,
                "Both success strings are localized by the official LocString.KeyToString contract.");
            if (args.Length == 2)
            {
                Check(Decode(completion, body, 0x2ff, 0x304) == BodyKey, "The actual success-body decoder yields exact key @+IntDU.Succesfull.");
                Check(Decode(completion, body, 0x30e, 0x313) == CaptionKey, "The actual success-caption decoder yields exact key @+iresult.");
            }
            else Console.WriteLine("SKIP two vendor string-decoder invocations; metadata/control-flow only, not full decoder proof.");
            Check(Localization(dir, "ru.xml", "@+IntDU.TimeError") == "Истекло время ожидания готовности ДУ. Повторить инициализацию?" &&
                Localization(dir, "ru.xml", "@+GetProgrammVersionID806") == "Ошибка", "Exact screenshot timeout body and caption match official Russian resources.");
            Check(Localization(dir, "ru.xml", BodyKey) == "ДУ успешно добавлено в систему" && Localization(dir, "ru.xml", CaptionKey) == "Результат",
                "Official Russian resources resolve the success body and caption exactly.");
            Check(Localization(dir, "en-gb.xml", BodyKey) == "Device added successfully" && Localization(dir, "en-gb.xml", CaptionKey) == "Result",
                "Official English resources resolve the same success keys exactly.");

            MethodBase printFlag = SoleCall(gui.ManifestModule.ResolveMethod(0x060001b9));
            MethodBase print = SoleCall(gui.ManifestModule.ResolveMethod(0x060001bd));
            Check(printFlag.DeclaringType.FullName == "Engine.Utils.Print.LabelsPrint" && printFlag.Name == "GetPrintEnabled" &&
                print.DeclaringType == printFlag.DeclaringType && print.Name == "PrintLabel",
                "The alternate successful branch is the native label-print path; no printer code is invoked by this probe.");
            MethodBase close = SoleCall(gui.ManifestModule.ResolveMethod(0x060001c5));
            Check(close.DeclaringType.FullName == "System.Windows.Forms.Form" && close.Name == "Close" && close.GetParameters().Length == 0 &&
                outerBody[0x1f7].Op == OpCodes.Ldloc_1 && Called(outer, outerBody[0x1f8]).MetadataToken == 0x060001c5,
                "Outer continuation IL_01F7/01F8 closes that exact InitDu through native Form.Close.");
            MethodBase idWrite = SoleCall(gui.ManifestModule.ResolveMethod(0x06000187));
            MethodBase keyWrite = SoleCall(gui.ManifestModule.ResolveMethod(0x06000189));
            Check(idWrite.DeclaringType.FullName == "Core.IUniqueDevice" && idWrite.Name == "set_ID" && idWrite.GetParameters()[0].ParameterType == typeof(byte[]) &&
                keyWrite.DeclaringType.FullName == "Core.ICommonPropertyStreletcPro" && keyWrite.Name == "set_Key" && keyWrite.GetParameters()[0].ParameterType == typeof(byte[]),
                "The vendor registration worker has full byte-array ID/KEY setters; no short display serial is substituted.");

            if (args.Length == 3)
            {
                MethodBase retryMessage = SoleCall(gui.ManifestModule.ResolveMethod(0x060001b6));
                Check(retryMessage.DeclaringType.FullName == "System.Windows.Forms.MessageBox" && retryMessage.Name == "Show" &&
                    retryMessage.GetParameters().Any(p => p.ParameterType.FullName == "System.Windows.Forms.MessageBoxButtons"),
                    "Retry wrapper is the native WinForms MessageBox with explicit buttons.");
                Console.WriteLine("PASS " + checks + " metadata/resource assertions only. SKIP native string decoders and CFG simulation (decoder requires initialized vendor runtime). No hardware.");
                return 0;
            }
            var standard = new Scenario();
            TraceResult success = Trace(completion, 0x25e, standard, setter.MetadataToken, showFlag.MetadataToken);
            Check(success.Events.SequenceEqual(new[] { "success:true", "success-box", "return:2" }),
                "Real flattened CFG: result 2 + empty error -> Successfully=true -> success OK -> return 2, in that order.");
            Check(success.SuccessSetterUsesInit && success.SuccessBoxes == 1,
                "The success setter targets the exact InitDu local and the success path reaches just one success popup.");
            TraceResult afterOk = Trace(outer, 0xb9, standard, setter.MetadataToken, showFlag.MetadataToken);
            Check(afterOk.Events.SequenceEqual(new[] { "close-init", "return:void" }) && afterOk.ClosedExactInit,
                "After OK, returned result 2 makes the outer continuation close its own InitDu; no second bridge Close is needed.");
            foreach (int result in new[] { 0, 1, 3 })
            {
                var scenario = new Scenario { Result = result };
                TraceResult trace = Trace(completion, 0x25e, scenario, setter.MetadataToken, showFlag.MetadataToken);
                Check(trace.Events.SequenceEqual(new[] { "return:" + result }),
                    "Native result " + result + " does not set Successfully and does not enter the success popup.");
            }
            TraceResult retry = Trace(outer, 0xb9, new Scenario { Result = 3 }, setter.MetadataToken, showFlag.MetadataToken);
            TraceResult declined = Trace(outer, 0xb9, new Scenario { Result = 0 }, setter.MetadataToken, showFlag.MetadataToken);
            Check(!declined.Events.Contains("restart-discovery"), "Declined timeout result 0 never restarts discovery.");
            Check(retry.Events.SequenceEqual(new[] { "restart-discovery", "return:void" }) && !retry.ClosedExactInit,
                "Result 3 restarts native discovery; it is not the successful close path.");
            TraceResult error = Trace(completion, 0x25e, new Scenario { Result = 0, Error = "synthetic error" }, setter.MetadataToken, showFlag.MetadataToken);
            Check(error.Events.SequenceEqual(new[] { "error-box", "return:0" }),
                "The separate error dialog at IL_016F never sets Successfully or becomes a success popup.");
            TraceResult printed = Trace(completion, 0x25e, new Scenario { PrintEnabled = true }, setter.MetadataToken, showFlag.MetadataToken);
            Check(printed.Events.SequenceEqual(new[] { "success:true", "print-label", "return:2" }),
                "Printing enabled with a name: native success and label path return 2 without any success popup.");
            TraceResult blankPrint = Trace(completion, 0x25e, new Scenario { PrintEnabled = true, DeviceName = " " }, setter.MetadataToken, showFlag.MetadataToken);
            Check(blankPrint.Events.SequenceEqual(new[] { "success:true", "success-box", "return:2" }),
                "Printing enabled with no usable name falls back to the ordinary success popup, then returns 2.");
            TraceResult silent = Trace(completion, 0x25e, new Scenario { ShowResult = false }, setter.MetadataToken, showFlag.MetadataToken);
            Check(silent.Events.SequenceEqual(new[] { "success:true", "return:2" }),
                "The native UI-disabled success branch returns 2 without any dialog to acknowledge.");
            foreach (bool repeat in new[] { false, true })
            {
                TraceResult timeout = Trace(completion, 0x25e, new Scenario { Counter = 31, RetryAnswer = repeat ? 6 : 7 }, setter.MetadataToken, showFlag.MetadataToken);
                Check(timeout.Events.SequenceEqual(new[] { "retry-question", "return:" + (repeat ? 3 : 0) }),
                    "The retry question (answer " + (repeat ? "Yes" : "No") + ") does not set Successfully and is not acknowledged as success.");
            }
            TraceResult silentError = Trace(completion, 0x25e, new Scenario { ShowResult = false, Result = 0, Error = "synthetic error" }, setter.MetadataToken, showFlag.MetadataToken);
            Check(silentError.Events.SequenceEqual(new[] { "error-box", "return:0" }),
                "Disabling the optional successful-result UI does not suppress or accept the native error dialog.");
            foreach (bool repeat in new[] { false, true })
            {
                TraceResult silentRetry = Trace(completion, 0x25e, new Scenario { ShowResult = false, Counter = 31, RetryAnswer = repeat ? 6 : 7 }, setter.MetadataToken, showFlag.MetadataToken);
                Check(silentRetry.Events.SequenceEqual(new[] { "retry-question", "return:" + (repeat ? 3 : 0) }),
                    "UI-disabled success mode preserves the native retry question and its " + (repeat ? "Yes" : "No") + " outcome.");
            }
            TraceResult silentPrint = Trace(completion, 0x25e, new Scenario { ShowResult = false, PrintEnabled = true }, setter.MetadataToken, showFlag.MetadataToken);
            Check(silentPrint.Events.SequenceEqual(new[] { "success:true", "return:2" }),
                "The optional successful-result branch also controls label printing; bridge-owned silent completion performs neither popup nor automatic label print.");
            VerifySilentResultContract(dir, bridge, gui, init, showFlag);
            Console.WriteLine("PASS " + checks + " official success-contract assertions. No InitDu construction, UI, MessageBox, label printing, connection, registration or hardware calls.");
            Console.WriteLine("LIMIT: This is real-DLL metadata/control-flow verification, not a Win32 GUI integration test. The scoped bridge command uses the native constructor flag; any legacy popup acknowledgement still requires exact runtime ownership proof.");
            return 0;
        }
        catch (Exception error)
        {
            while (error.InnerException != null) error = error.InnerException;
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1;
        }
    }

    // Read the exact production and vendor method bodies. None of the vendor
    // command constructors, prechecks, Run methods or registration UI execute.
    private static void VerifySilentResultContract(string directory, Assembly bridge, Assembly gui, Type init, MethodBase showFlag)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assembly core = Assembly.LoadFrom(Path.Combine(directory, "Core.dll"));
        Assembly engine = Assembly.LoadFrom(Path.Combine(directory, "Engine.dll"));
        Assembly commonGui = Assembly.LoadFrom(Path.Combine(directory, "Argus.GUI.Common.dll"));
        Type command = core.GetType("Core.Commands.BaseCommand", true);
        Type programmable = engine.GetType("Engine.Commands.BaseProgrammCommand", true);
        Type initCommand = gui.GetType("StreletcProGUI.Commands.InitDevice", true);
        PropertyInfo mode = command.GetProperty("SilentMode");
        MethodInfo getMode = mode.GetGetMethod(), setMode = mode.GetSetMethod();
        Instruction[] getModeIl = Read(getMode), setModeIl = Read(setMode);
        Check(mode.PropertyType == typeof(bool) && getMode.IsPublic && setMode.IsPublic && !getMode.IsVirtual && !setMode.IsVirtual &&
            getMode.MetadataToken == 0x06002b41 && setMode.MetadataToken == 0x06002b42,
            "SilentMode is the pinned public nonvirtual Boolean command property, not a private readonly field patch.");
        Check(getModeIl.Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ret }) &&
            setModeIl.Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Stfld, OpCodes.Ret }) &&
            getModeIl[1].Token == setModeIl[2].Token,
            "The SilentMode property reads/writes only its own command-instance Boolean backing field.");
        MethodInfo process = (MethodInfo)gui.ManifestModule.ResolveMethod(0x0600046c);
        Check(initCommand.IsPublic && !initCommand.IsSealed && initCommand.BaseType == programmable &&
            process.DeclaringType == initCommand && process.Name == "ProcessCommand" && process.IsPublic && process.IsVirtual && !process.IsFinal &&
            initCommand.GetConstructor(new[] { typeof(string) }) != null,
            "The pinned InitDevice supports a dedicated subclass overriding ProcessCommand; the registered shared command need not be mutated.");
        Instruction[] processIl = Read(process);
        MethodBase[] baseCalls = processIl.Where(IsCall).Select(x => CanonicalForwarder(Called(process, x)))
            .Where(m => m.DeclaringType == command || m.DeclaringType == programmable).ToArray();
        Check(baseCalls.Select(m => m.MetadataToken).SequenceEqual(new[] { 0x060032d6, 0x060032c8, 0x06002b41 }),
            "Complete InitDevice.ProcessCommand body has exactly three base calls: two-argument PreCommandCheck, Adapter getter, SilentMode getter.");
        var processBody = processIl.ToDictionary(x => x.Offset);
        Check(CanonicalForwarder(Called(process, processBody[0x279])).MetadataToken == 0x060032d6 &&
            CanonicalForwarder(Called(process, processBody[0x3b0])).MetadataToken == 0x060032c8 &&
            CanonicalForwarder(Called(process, processBody[0x3b6])).MetadataToken == getMode.MetadataToken &&
            processBody[0x3bb].Op == OpCodes.Ldc_I4_0 && processBody[0x3bc].Op == OpCodes.Ceq &&
            processBody[0x3be].Op == OpCodes.Newobj && processBody[0x3be].Token == 0x06000059,
            "The native precheck precedes construction; only !SilentMode is supplied as the fourth InitDu constructor argument.");
        MethodInfo precheck2 = (MethodInfo)engine.ManifestModule.ResolveMethod(0x060032d6);
        MethodInfo precheck4 = (MethodInfo)engine.ManifestModule.ResolveMethod(0x060032d7);
        Check(precheck2.IsFamily && !precheck2.IsVirtual && precheck4.IsFamily && precheck4.IsVirtual && !precheck4.IsFinal &&
            precheck4.ReturnType.FullName == "Engine.CommandCheckResult" &&
            precheck4.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] {
                "System.Guid", "System.Boolean", "System.Boolean", "Core.Adapters.TryGetDuplicateAdapterEnum" }),
            "The real four-argument PreCommandCheck is an overridable protected method with the exact preserved argument/result contract.");
        Instruction[] precheck2Il = Read(precheck2);
        Instruction dispatch = precheck2Il.Single(x => IsCall(x) && Called(precheck2, x).MetadataToken == precheck4.MetadataToken &&
            Called(precheck2, x).Module == precheck4.Module);
        Check(dispatch.Offset == 0x1e && dispatch.Op == OpCodes.Callvirt &&
            precheck2Il.Where(x => x.Offset >= 0x19 && x.Offset < 0x1e).Select(x => x.Op).SequenceEqual(new[] {
                OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldloc_0, OpCodes.Ldc_I4_1 }),
            "The native two-argument precheck uses callvirt to the four-argument override with the same instance, GUID, openAdapter, foundKsg and duplicate-adapter value.");
        Check(!precheck2Il.Any(x => IsCall(x) && CanonicalForwarder(Called(precheck2, x)).MetadataToken == getMode.MetadataToken &&
                CanonicalForwarder(Called(precheck2, x)).Module == getMode.Module),
            "The nonvirtual precheck wrapper does not inspect SilentMode before the guarded virtual precheck scope.");
        Check(!initCommand.GetMethods(declared).Any(m => m.Name == "PreCommandCheck") &&
            initCommand.GetMethods(declared).Single(m => m.Name == "GetCheckKodProSystemResult").GetBaseDefinition().MetadataToken == 0x060032da,
            "InitDevice inherits PreCommandCheck and retains its own system-code validation override inside the preserved normal precheck scope.");

        MethodBase initCtor = gui.ManifestModule.ResolveMethod(0x06000059);
        Instruction[] ctorIl = Read(initCtor), showIl = Read(showFlag);
        FieldInfo showField = gui.ManifestModule.ResolveField(showIl.Single(x => x.Op == OpCodes.Ldfld).Token);
        var ctorBody = ctorIl.ToDictionary(x => x.Offset);
        Check(showField.MetadataToken == 0x04000040 && showField.FieldType == typeof(bool) && showField.IsInitOnly && showField.DeclaringType == init &&
            ctorBody[0x4c].Op == OpCodes.Ldarg_0 && ctorBody[0x4d].Op == OpCodes.Ldarg_S && ctorBody[0x4d].Value == 4 &&
            ctorBody[0x4f].Op == OpCodes.Stfld && ctorBody[0x4f].Token == showField.MetadataToken,
            "InitDu constructor 06000059 stores argument four in readonly show-result field 04000040 at IL_004F.");
        Check(initCtor.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] {
                "Core.tree.CustomTreeNode", "Core.tree.CustomTreeNode", "Core.IAdapter", "System.Boolean" }) &&
            initCtor.GetParameters()[3].IsOptional && Object.Equals(initCtor.GetParameters()[3].DefaultValue, true),
            "The public native constructor explicitly accepts the optional success-result Boolean and defaults manual registration to true.");
        var initMembers = TypeAndNested(init).SelectMany(t => t.GetMethods(declared).Cast<MethodBase>().Concat(t.GetConstructors(declared)))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null).ToArray();
        MethodBase[] writers = initMembers.Where(m => Read(m).Any(x => x.Op == OpCodes.Stfld && x.Token == showField.MetadataToken)).ToArray();
        Check(writers.Length == 1 && writers[0].MetadataToken == initCtor.MetadataToken,
            "The optional result field has only its legitimate constructor write across InitDu and its nested worker types.");
        MethodInfo next = initCommand.GetMethod("GetNextCommand");
        Check(next.DeclaringType == command && next.MetadataToken == 0x06002b36 &&
            Read(next).Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_1, OpCodes.Ldnull, OpCodes.Stind_Ref, OpCodes.Ldnull, OpCodes.Ret }),
            "InitDevice inherits the no-next-command implementation; no later command inherits a temporary success-UI scope.");

        Type programmer = engine.GetType("Engine.Programmer", true);
        Type masterProgrammer = commonGui.GetType("Argus.GUI.Common.Programmers.MasterProgrammer", true);
        Check(masterProgrammer.BaseType == programmer && !masterProgrammer.GetMethods(declared).Any(m => m.Name == "Run" || m.Name == "RunInSegment" || m.Name == "InternalRun"),
            "Pinned MasterProgrammer inherits the inspected Engine.Programmer execution methods without replacing the local command path.");
        MethodInfo runType = initCommand.GetMethod("GetRunType");
        Instruction[] runTypeIl = Read(runType);
        int serverSide = Convert.ToInt32(runType.ReturnType.GetField("ServerSide").GetRawConstantValue());
        Check(runType.DeclaringType == programmable && runTypeIl.Length == 2 && runTypeIl[1].Op == OpCodes.Ret && IntConstant(runTypeIl[0]) == serverSide,
            "InitDevice keeps native ServerSide classification; the bridge must reject a remote CommandExecutor rather than force ClientSide.");
        // The member lookup is by verified shape as wrapper tokens can be opaque
        // in diagnostics; VersionGuard still pins the complete assembly bytes.
        var programmerMethods = programmer.GetMethods(declared);
        MethodBase processForwarder = programmerMethods.Single(m => {
            Instruction[] code = Read(m);
            return code.Count(IsCall) == 1 && code.All(x => x.Op.Name.StartsWith("ldarg", StringComparison.Ordinal) || IsCall(x) || x.Op == OpCodes.Ret) &&
                CanonicalForwarder(m).DeclaringType == command && CanonicalForwarder(m).Name == "ProcessCommand";
        });
        Instruction[] processForwarderIl = Read(processForwarder);
        Check(processForwarderIl.Single(IsCall).Op == OpCodes.Callvirt &&
            processForwarderIl.Take(3).Select(x => x.Op).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2 }),
            "The local programmer invokes ProcessCommand virtually on the supplied command instance with unchanged records and parameters.");
        MethodBase internalRun = engine.ManifestModule.ResolveMethod(0x06000aed);
        Check(Read(internalRun).Count(x => IsCall(x) && Called(internalRun, x).Module == processForwarder.Module &&
                Called(internalRun, x).MetadataToken == processForwarder.MetadataToken) == 1 &&
            !Read(internalRun).Any(x => IsCall(x) && CanonicalForwarder(Called(internalRun, x)).Name == "set_SilentMode"),
            "Programmer.InternalRun dispatches through that single virtual ProcessCommand wrapper and does not reset SilentMode.");
        VerifyCompiledScopedCommand(bridge, initCommand, process, precheck4, command);
        VerifyObservedResultFlag(bridge, showField);
    }

    private static void VerifyCompiledScopedCommand(Assembly bridge, Type initCommand, MethodInfo nativeProcess, MethodInfo nativePrecheck, Type baseCommand)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type scoped = bridge.GetType("Streletz.MasterBridge.NativeInitDeviceCommand", true);
        Check(scoped.IsSealed && scoped.BaseType == initCommand,
            "The built production DLL contains a sealed per-operation subclass of the pinned InitDevice.");
        MethodInfo process = scoped.GetMethods(declared).Single(m => m.Name == "ProcessCommand");
        MethodInfo precheck = scoped.GetMethods(declared).Single(m => m.Name == "PreCommandCheck");
        Check(process.GetBaseDefinition() == nativeProcess.GetBaseDefinition() && process.IsVirtual &&
            precheck.GetBaseDefinition() == nativePrecheck.GetBaseDefinition() && precheck.IsVirtual,
            "The compiled bridge overrides the real virtual ProcessCommand and four-argument PreCommandCheck slots.");
        MethodBase[] ownBodies = TypeAndNested(scoped).SelectMany(t => t.GetMethods(declared).Cast<MethodBase>().Concat(t.GetConstructors(declared)))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null).ToArray();
        var calls = ownBodies.SelectMany(m => Read(m).Where(IsCall).Select(x => new { Method = m, Instruction = x, Target = Called(m, x) })).ToArray();
        Check(calls.Count(x => x.Target == nativeProcess && x.Instruction.Op == OpCodes.Call) == 1 &&
            calls.Count(x => x.Target == nativePrecheck && x.Instruction.Op == OpCodes.Call) == 1,
            "The compiled helper calls each original vendor implementation once through nonvirtual base calls, preserving native registration and prechecks.");
        foreach (MethodInfo original in new[] { nativeProcess, nativePrecheck })
        {
            MethodBase forwarder = calls.Single(x => x.Target == original).Method;
            Instruction[] body = Read(forwarder);
            int argumentCount = original.GetParameters().Length + 1;
            Check(body.Length == argumentCount + 2 && body.Take(argumentCount).Select(ArgumentIndex).SequenceEqual(Enumerable.Range(0, argumentCount)) &&
                body[argumentCount].Op == OpCodes.Call && Called(forwarder, body[argumentCount]) == original && body.Last().Op == OpCodes.Ret,
                "The compiled " + original.Name + " base-call wrapper forwards the exact same command instance and every argument in order, with no recursive virtual call or result substitution.");
        }
        Check(!scoped.GetMethods(declared).Any(m => m.Name == "GetRunType" || m.Name == "GetCheckKodProSystemResult" || m.Name == "GetNextCommand" || m.Name == "IsVisible" || m.Name == "IsEnabled" || m.Name == "HasRightForCommand"),
            "The dedicated command does not override permissions, visibility, execution placement, system-code validation or command chaining.");
        Check(!calls.Any(x => x.Target.DeclaringType == typeof(FieldInfo) && x.Target.Name == "SetValue") &&
            !calls.Any(x => x.Target.DeclaringType.FullName == "System.Windows.Forms.MessageBox" || x.Target.Name == "set_Successfully"),
            "The compiled dedicated command neither patches readonly native fields, displays/accepts arbitrary dialogs, nor fabricates native success.");
    }

    private static void VerifyObservedResultFlag(Assembly bridge, FieldInfo nativeShowField)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type automation = bridge.GetType("Streletz.MasterBridge.NativeDialogAutomation", true);
        MethodInfo attach = automation.GetMethods(declared).Single(m => m.Name == "AttachDialog");
        Instruction[] il = Read(attach);
        FieldInfo token = automation.GetField("ShowSuccessResultField", declared);
        Check(token != null && token.IsLiteral && Object.Equals(token.GetRawConstantValue(), nativeShowField.MetadataToken) &&
            il.Where((x, index) => IsCall(x) && Called(attach, x).DeclaringType == typeof(Module) && Called(attach, x).Name == "ResolveField" &&
                index > 0 && il[index - 1].Op == OpCodes.Ldc_I4 && il[index - 1].Value == nativeShowField.MetadataToken).Count() == 1,
            "The compiled dialog observer resolves the exact pinned readonly ShowResult field token 04000040 on the native dialog module.");
        var reads = il.Where(IsCall).Select(x => new { Instruction = x, Target = Called(attach, x) }).ToArray();
        var valueRead = reads.Single(x => x.Target.DeclaringType == typeof(FieldInfo) && x.Target.Name == "GetValue");
        int readIndex = Array.IndexOf(il, valueRead.Instruction);
        Check(reads.Any(x => x.Target.DeclaringType == typeof(FieldInfo) && x.Target.Name == "get_IsInitOnly") &&
            il[readIndex + 1].Op == OpCodes.Ldc_I4_0 && il[readIndex + 2].Op == OpCodes.Box &&
            attach.Module.ResolveType(il[readIndex + 2].Token) == typeof(bool) &&
            IsCall(il[readIndex + 3]) && Called(attach, il[readIndex + 3]).DeclaringType == typeof(object) && Called(attach, il[readIndex + 3]).Name == "Equals" &&
            il[readIndex + 4].Op == OpCodes.Brtrue_S &&
            il.Where(x => x.Offset >= il[readIndex + 4].Next && x.Offset < il[readIndex + 4].Targets[0]).Last().Op == OpCodes.Throw,
            "The compiled observer reads the field, checks its readonly shape and requires Boolean false; a different value reaches the failure throw.");
        Instruction observedWrite = il.Single(x => x.Op == OpCodes.Stfld && attach.Module.ResolveField(x.Token).Name == "NativeSuccessDialogSuppressed");
        Check(observedWrite.Offset > il[readIndex + 4].Targets[0] &&
            il.Where(x => x.Offset >= il[readIndex + 4].Targets[0] && x.Offset < observedWrite.Offset).Last().Op == OpCodes.Ldc_I4_1,
            "The observed suppressed-result state is recorded only after the actual native constructor field is confirmed false.");
        MethodBase[] bodies = TypeAndNested(automation).SelectMany(t => t.GetMethods(declared).Cast<MethodBase>().Concat(t.GetConstructors(declared)))
            .Where(m => !m.IsAbstract && m.GetMethodBody() != null).ToArray();
        Check(!bodies.Any(m => Read(m).Any(x => IsCall(x) && Called(m, x).DeclaringType == typeof(FieldInfo) &&
                (Called(m, x).Name == "SetValue" || Called(m, x).Name == "SetValueDirect"))) &&
            !bodies.Any(m => Read(m).Any(x => (x.Op == OpCodes.Stfld || x.Op == OpCodes.Stsfld) && m.Module.ResolveField(x.Token) == nativeShowField)),
            "The compiled dialog automation has no reflective or direct write to the native readonly result field.");
    }

    private static int ArgumentIndex(Instruction instruction)
    {
        if (instruction.Op == OpCodes.Ldarg_0) return 0;
        if (instruction.Op == OpCodes.Ldarg_1) return 1;
        if (instruction.Op == OpCodes.Ldarg_2) return 2;
        if (instruction.Op == OpCodes.Ldarg_3) return 3;
        if (instruction.Op == OpCodes.Ldarg || instruction.Op == OpCodes.Ldarg_S) return instruction.Value;
        return -1;
    }

    private static IEnumerable<Type> TypeAndNested(Type root)
    {
        yield return root;
        foreach (Type nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (Type item in TypeAndNested(nested)) yield return item;
    }

    private static MethodBase CanonicalForwarder(MethodBase method)
    {
        for (int n = 0; n < 8; n++)
        {
            if (method.IsAbstract || method.GetMethodBody() == null) return method;
            Instruction[] code = Read(method);
            Instruction[] calls = code.Where(IsCall).ToArray();
            if (calls.Length != 1 || code.Any(x => !(x.Op.Name.StartsWith("ldarg", StringComparison.Ordinal) || IsCall(x) || x.Op == OpCodes.Ret))) return method;
            method = Called(method, calls[0]);
        }
        throw new Exception("Direct forwarding chain exceeded its metadata bound.");
    }

    private sealed class Scenario
    {
        public int Result = 2, Counter, RetryAnswer = 7;
        public bool ShowResult = true, PrintEnabled;
        public string Error = "", DeviceName = "Synthetic device";
    }
    private sealed class TraceResult
    {
        public readonly List<string> Events = new List<string>();
        public bool SuccessSetterUsesInit, ClosedExactInit;
        public int SuccessBoxes;
    }

    // Only arithmetic and control flow execute. Calls, field accesses and object
    // references are synthetic; an unknown instruction/call fails immediately.
    private static TraceResult Trace(MethodBase method, int start, Scenario scenario, int setterToken, int showToken)
    {
        var instructions = Read(method).ToDictionary(x => x.Offset);
        var stack = new Stack<object>();
        var locals = new object[method.GetMethodBody().LocalVariables.Count];
        for (int i = 0; i < locals.Length; i++) locals[i] = 0;
        locals[1] = InitInstance;
        var result = new TraceResult();
        int pc = start;
        for (int step = 0; step < 3000; step++)
        {
            Instruction x = instructions[pc]; pc = x.Next;
            if (IsCall(x))
            {
                MethodBase call = Called(method, x);
                int count = call.GetParameters().Length + (call.IsStatic ? 0 : 1);
                var values = new object[count];
                for (int i = count - 1; i >= 0; i--) values[i] = stack.Pop();
                object returnValue = null;
                if (call.Module == method.Module && call.MetadataToken == setterToken)
                {
                    if (!ReferenceEquals(values[0], InitInstance)) throw new Exception("Success setter did not use the exact InitDu local.");
                    result.SuccessSetterUsesInit = true; result.Events.Add("success:" + (Number(values[1]) != 0 ? "true" : "false"));
                }
                else if (call.Module == method.Module && call.MetadataToken == showToken) returnValue = scenario.ShowResult ? 1 : 0;
                else if (call.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.TaskAwaiter`1", StringComparison.Ordinal) && call.Name == "GetResult") returnValue = scenario.Result;
                else if (call.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1", StringComparison.Ordinal) && call.Name == "SetResult")
                    result.Events.Add("return:" + Number(values[values.Length - 1]));
                else if (call.DeclaringType.FullName == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder" && call.Name == "SetResult") result.Events.Add("return:void");
                else if (call.Module == method.Module && call.DeclaringType.Name == "<Module>" && call is MethodInfo && ((MethodInfo)call).IsGenericMethod &&
                    ((MethodInfo)call).ReturnType == typeof(string) && call.GetParameters().Length == 1 && call.GetParameters()[0].ParameterType == typeof(uint))
                    returnValue = x.Offset == 0x304 ? BodyKey : x.Offset == 0x313 ? CaptionKey : "synthetic constant string";
                else if (call.Module == method.Module)
                {
                    switch (call.MetadataToken)
                    {
                        case 0x060001b5: returnValue = values[0]; break;
                        case 0x060001b7: returnValue = String.IsNullOrEmpty((string)values[0]) ? 1 : 0; break;
                        case 0x060001bc: returnValue = String.IsNullOrWhiteSpace((string)values[0]) ? 1 : 0; break;
                        case 0x060001b9: returnValue = scenario.PrintEnabled ? 1 : 0; break;
                        case 0x060001ba: returnValue = new object(); break;
                        case 0x060001bb: returnValue = scenario.DeviceName; break;
                        case 0x060001bd:
                            if ((string)values[0] != scenario.DeviceName || Number(values[1]) != 0) throw new Exception("Unexpected print arguments.");
                            result.Events.Add("print-label"); break;
                        case 0x060001be:
                            if ((string)values[0] != BodyKey || (string)values[1] != CaptionKey || Number(values[2]) != 0) throw new Exception("Unexpected success popup arguments.");
                            result.SuccessBoxes++; result.Events.Add("success-box"); returnValue = 1; break;
                        case 0x060001b8: result.Events.Add("error-box"); returnValue = 1; break;
                        case 0x060001b6: result.Events.Add("retry-question"); returnValue = scenario.RetryAnswer; break;
                        case 0x060001c5:
                            result.ClosedExactInit = ReferenceEquals(values[0], InitInstance);
                            result.Events.Add("close-init"); break;
                        default:
                            if (method.MetadataToken == 0x060001bf && x.Offset == 0x220 && call.DeclaringType.FullName == "StreletcProGUI.Dialogs.InitDu")
                            {
                                if (!ReferenceEquals(values[0], InitInstance)) throw new Exception("Unexpected discovery instance.");
                                result.Events.Add("restart-discovery");
                            }
                            else throw new Exception("Unsupported synthetic call " + call.MetadataToken.ToString("X8") + " at " + x.Offset.ToString("X4"));
                            break;
                    }
                }
                else throw new Exception("Unexpected external call " + call.DeclaringType.FullName + "." + call.Name);
                if (call is MethodInfo && ((MethodInfo)call).ReturnType != typeof(void)) stack.Push(returnValue);
                continue;
            }
            if (x.Op == OpCodes.Nop || x.Op == OpCodes.Volatile) continue;
            if (x.Op == OpCodes.Ret) { if (stack.Count != 0) throw new Exception("Unbalanced synthetic stack."); return result; }
            if (x.Op == OpCodes.Ldarg_0 || x.Op == OpCodes.Ldnull) { stack.Push(null); continue; }
            if (x.Op == OpCodes.Ldfld || x.Op == OpCodes.Ldflda)
            {
                stack.Pop(); FieldInfo field = method.Module.ResolveField(x.Token);
                if (x.Op == OpCodes.Ldflda) { stack.Push(new object()); continue; }
                if (field.FieldType == typeof(string)) stack.Push(scenario.Error);
                else if (field.FieldType == typeof(int)) stack.Push(scenario.Counter);
                else if (field.FieldType.IsClass) stack.Push(new object());
                else throw new Exception("Unsupported synthetic field " + field.FieldType.FullName);
                continue;
            }
            if (x.Op == OpCodes.Stfld) { stack.Pop(); stack.Pop(); continue; }
            if (x.Op == OpCodes.Ldloca || x.Op == OpCodes.Ldloca_S) { stack.Push(new object()); continue; }
            if (x.Op.Name.StartsWith("ldloc", StringComparison.Ordinal)) { stack.Push(locals[LocalIndex(x)]); continue; }
            if (x.Op.Name.StartsWith("stloc", StringComparison.Ordinal)) { locals[LocalIndex(x)] = stack.Pop(); continue; }
            if (x.Op.Name.StartsWith("ldc.i4", StringComparison.Ordinal)) { stack.Push(IntConstant(x)); continue; }
            if (x.Op == OpCodes.Dup) { stack.Push(stack.Peek()); continue; }
            if (x.Op == OpCodes.Pop) { stack.Pop(); continue; }
            if (x.Op == OpCodes.Conv_U4 || x.Op == OpCodes.Conv_I4) continue;
            if (x.Op == OpCodes.Br || x.Op == OpCodes.Br_S) { pc = x.Targets[0]; continue; }
            if (x.Op == OpCodes.Leave || x.Op == OpCodes.Leave_S) { stack.Clear(); pc = x.Targets[0]; continue; }
            if (x.Op == OpCodes.Brtrue || x.Op == OpCodes.Brtrue_S) { if (Number(stack.Pop()) != 0) pc = x.Targets[0]; continue; }
            if (x.Op == OpCodes.Brfalse || x.Op == OpCodes.Brfalse_S) { if (Number(stack.Pop()) == 0) pc = x.Targets[0]; continue; }
            if (x.Op == OpCodes.Beq || x.Op == OpCodes.Beq_S || x.Op == OpCodes.Ble || x.Op == OpCodes.Ble_S)
            {
                int b = Number(stack.Pop()), a = Number(stack.Pop());
                if ((x.Op == OpCodes.Beq || x.Op == OpCodes.Beq_S) ? a == b : a <= b) pc = x.Targets[0];
                continue;
            }
            if (x.Op == OpCodes.Switch) { int n = Number(stack.Pop()); if (n >= 0 && n < x.Targets.Length) pc = x.Targets[n]; continue; }
            if (x.Op == OpCodes.Xor || x.Op == OpCodes.Mul || x.Op == OpCodes.Add || x.Op == OpCodes.Sub || x.Op == OpCodes.Rem_Un)
            {
                int b = Number(stack.Pop()), a = Number(stack.Pop());
                stack.Push(x.Op == OpCodes.Xor ? a ^ b : x.Op == OpCodes.Mul ? unchecked(a * b) : x.Op == OpCodes.Add ? unchecked(a + b) :
                    x.Op == OpCodes.Sub ? unchecked(a - b) : unchecked((int)((uint)a % (uint)b))); continue;
            }
            throw new Exception("Unsupported synthetic opcode " + x.Op.Name + " at " + x.Offset.ToString("X4"));
        }
        throw new Exception("Synthetic CFG trace exceeded its instruction limit.");
    }

    private static string Decode(MethodBase method, Dictionary<int, Instruction> body, int constantOffset, int callOffset)
    {
        Instruction constant = body[constantOffset];
        MethodInfo decode = (MethodInfo)Called(method, body[callOffset]);
        if (constant.Op != OpCodes.Ldc_I4 || !decode.IsStatic || decode.DeclaringType.Name != "<Module>" || decode.ReturnType != typeof(string) ||
            decode.GetParameters().Length != 1 || decode.GetParameters()[0].ParameterType != typeof(uint)) throw new Exception("String decoder contract changed.");
        return (string)decode.Invoke(null, new object[] { unchecked((uint)constant.Value) });
    }
    private static string Localization(string directory, string language, string key)
    {
        return XDocument.Load(Path.Combine(directory, "Resources", "Localization", language)).Descendants("string")
            .Single(e => (string)e.Attribute("key") == key).Value;
    }
    private static MethodBase SoleCall(MethodBase method)
    {
        Instruction[] body = Read(method);
        if (body.Any(x => x.Op.FlowControl == FlowControl.Branch || x.Op.FlowControl == FlowControl.Cond_Branch))
            throw new Exception("Expected a direct wrapper without branches.");
        return Called(method, body.Single(IsCall));
    }
    private static bool IsCall(Instruction x) { return x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt; }
    private static MethodBase Called(MethodBase method, Instruction x)
    {
        if (!IsCall(x)) throw new Exception("Expected a method call at " + x.Offset.ToString("X4"));
        return method.Module.ResolveMethod(x.Token, method.DeclaringType.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
    }
    private static int Number(object value) { return Convert.ToInt32(value); }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static int LocalIndex(Instruction x)
    {
        if (x.Op == OpCodes.Ldloc_0 || x.Op == OpCodes.Stloc_0) return 0;
        if (x.Op == OpCodes.Ldloc_1 || x.Op == OpCodes.Stloc_1) return 1;
        if (x.Op == OpCodes.Ldloc_2 || x.Op == OpCodes.Stloc_2) return 2;
        if (x.Op == OpCodes.Ldloc_3 || x.Op == OpCodes.Stloc_3) return 3;
        return x.Value;
    }
    private static int IntConstant(Instruction x)
    {
        if (x.Op == OpCodes.Ldc_I4_M1) return -1;
        if (x.Op.Value >= OpCodes.Ldc_I4_0.Value && x.Op.Value <= OpCodes.Ldc_I4_8.Value) return x.Op.Value - OpCodes.Ldc_I4_0.Value;
        return x.Value;
    }
    private sealed class Instruction
    {
        public OpCode Op;
        public int Offset, Next, Value, Token;
        public int[] Targets;
    }
    private static Instruction[] Read(MethodBase method)
    {
        byte[] il = method.GetMethodBody().GetILAsByteArray();
        var result = new List<Instruction>();
        for (int offset = 0; offset < il.Length;)
        {
            var x = new Instruction { Offset = offset };
            ushort value = il[offset++]; if (value == 0xfe) value = (ushort)(0xfe00 | il[offset++]);
            x.Op = Opcodes[value];
            switch (x.Op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: x.Value = unchecked((sbyte)il[offset++]); break;
                case OperandType.ShortInlineVar: x.Value = il[offset++]; break;
                case OperandType.InlineVar: x.Value = BitConverter.ToUInt16(il, offset); offset += 2; break;
                case OperandType.InlineI: x.Value = BitConverter.ToInt32(il, offset); offset += 4; break;
                case OperandType.InlineI8: case OperandType.InlineR: offset += 8; break;
                case OperandType.ShortInlineR: offset += 4; break;
                case OperandType.ShortInlineBrTarget: x.Value = unchecked((sbyte)il[offset++]); x.Targets = new[] { offset + x.Value }; break;
                case OperandType.InlineBrTarget: x.Value = BitConverter.ToInt32(il, offset); offset += 4; x.Targets = new[] { offset + x.Value }; break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(il, offset); offset += 4; int next = offset + count * 4;
                    x.Targets = new int[count]; for (int i = 0; i < count; i++, offset += 4) x.Targets[i] = next + BitConverter.ToInt32(il, offset);
                    break;
                default: x.Token = BitConverter.ToInt32(il, offset); offset += 4; break;
            }
            x.Next = offset; result.Add(x);
        }
        return result.ToArray();
    }
}
