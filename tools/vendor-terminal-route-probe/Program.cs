// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// This probe reads PE metadata only. It does not Assembly.Load, instantiate any
// vendor object, evaluate a vendor getter, open a pipe or access equipment.
internal static class Program
{
    private static int checks;
    private static readonly Names names = new Names();
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: probe <MasterDirectory> <VersionGuard.cs>");
            string vendor = Path.GetFullPath(args[0]);
            foreach (Match match in Regex.Matches(File.ReadAllText(args[1]), "\\{ \\\"([^\\\"]+\\.(?:dll|exe))\\\", \\\"([0-9a-f]{64})\\\" \\}"))
            {
                string filename = match.Groups[1].Value;
                using var bytes = File.OpenRead(Path.Combine(vendor, filename));
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Check(sha == match.Groups[2].Value, "VERSION " + filename + " " + sha);
            }
            using var core = new Metadata(Path.Combine(vendor,"Core.dll"));
            using var plugin = new Metadata(Path.Combine(vendor,"Plugin.CorePlugins.dll"));
            using var rrop = new Metadata(Path.Combine(vendor,"Plugin.RROP-LON.dll"));
            using var ethernet = new Metadata(Path.Combine(vendor,"UsbEthernetAdapter.dll"));
            using var locked = new Metadata(Path.Combine(vendor,"BaseAdapter.dll"));
            using var engine = new Metadata(Path.Combine(vendor,"Engine.dll"));
            using var transport = new Metadata(Path.Combine(vendor,"BaseAdapterTransport.dll"));
            using var mkip = new Metadata(Path.Combine(vendor,"Plugin.MostIPI.dll"));
            CheckProperty(core,"Core.tree.CustomTreeNode","ParentNode","Core.tree.CustomTreeNode",false);
            CheckSignature(engine,"Engine.DataController","GetNode","Core.tree.CustomTreeNode","Object","System.Guid");
            CheckSignature(engine,"Engine.DataController","GetSegmentControllerNode","Core.tree.ISegmentControlTreeNode","Object","System.Guid");
            CheckProperty(plugin,"CorePlugins.segment.CustomInfo","IsViaPanel3DuplicateEnabled","Boolean",true);
            CheckProperty(rrop,"RROP_LON.Panel_3.Info","PanelMode","Core.DeviceTypes.PanelMode",true);
            CheckProperty(rrop,"RROP_LON.Panel_3.Info","NumberMainSegment","Byte",true);
            var panelMode = core.Type("Core.DeviceTypes.PanelMode");
            foreach (string value in new[] {"Main","Duplicate"})
                Check(panelMode.GetFields().Select(core.Reader.GetFieldDefinition).Any(f => core.Reader.GetString(f.Name) == value && (f.Attributes & FieldAttributes.Literal) != 0), "ENUM PanelMode." + value);
            CheckMethod(rrop,"RROP_LON.tree.SegmentControlPanel3TreeNodeWithCounter","GetDuplicatePair","Core.tree.CustomTreeNode");
            CheckMethod(core,"Core.tree.SegmentTreeNode","GetMmpiAddress","String");
            CheckMethod(locked,"BaseAdapter.LockedAdapter","GetUnderlyingAdapter",null);
            var tunnel = ethernet.Reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(0xDB));
            Check(ethernet.FullName(tunnel.GetDeclaringType()) == "UsbEthernetAdapter.UsbProxyAdapter" &&
                tunnel.DecodeSignature(names,null) == "Core.Adapters.TunnelDto" &&
                (tunnel.Attributes & FieldAttributes.InitOnly) != 0 && (tunnel.Attributes & FieldAttributes.Static) == 0,
                "FIELD 0x040000DB is the readonly UsbProxyAdapter TunnelDto");
            foreach (string property in new[] {"StartPointSegment","EndPointSegment","StartPointMmpiAddress","EndPointMmpiAddress","StartPointAdapterName"})
                CheckProperty(core,"Core.Adapters.TunnelDto",property,"String",false);
            const string channel = "BaseAdapterTransport.Core.Transport.ITransportChannel";
            const string impl = "BaseAdapterTransport.Core.Transport.Impl.";
            CheckNonPublicProperty(ethernet,"UsbEthernetAdapter.UsbProxyAdapter","Transport",
                "BaseAdapterTransport.Core.Transport.ITransportHolder",MethodAttributes.Assembly,false);
            CheckField(transport,impl+"TransportHolder","nestedTransport",channel,true);
            CheckField(transport,impl+"TransportCollaborator","currentTransport",channel,false);
            CheckNonPublicProperty(transport,impl+"UsbWrapper","Inner",channel,MethodAttributes.Private,true);
            CheckField(transport,impl+"UsbWrapper","<Inner>k__BackingField",channel,true);
            CheckField(transport,impl+"CryptoWebPort","transport",channel,true);
            CheckField(transport,impl+"CryptoWebPort","isOpened","Boolean",false);
            CheckField(transport,impl+"WebPort","uri","System.Uri",true);
            CheckField(transport,impl+"WebPort","client","System.Net.Sockets.TcpClient",false);
            Check(transport.Name(transport.Type(impl+"CryptoUsb").BaseType)==impl+"CryptoWebPort",
                "BASE CryptoUsb derives from CryptoWebPort");
            Check((transport.Type(impl+"WebPort").Attributes&TypeAttributes.Sealed)!=0,
                "TYPE WebPort is sealed");
            CheckMkIpFraming(mkip);
            CheckEthernetFramingFactory(transport);
            CheckAdapterSegmentHint(ethernet, vendor);
            CheckDirectAdapterOwner(core, plugin);
            CheckPhysicalInterfaceName(ethernet);
            CheckSelectiveModuleMetadata(core, plugin, mkip, transport);
            Console.WriteLine("PASS " + checks + " vendor metadata/hash assertions; no vendor code executed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
    private static void CheckField(Metadata meta,string type,string field,string expected,bool readOnly)
    {
        var value=meta.Type(type).GetFields().Select(meta.Reader.GetFieldDefinition).Single(f=>meta.Reader.GetString(f.Name)==field);
        Check(value.DecodeSignature(names,null)==expected && (value.Attributes&FieldAttributes.Static)==0 &&
            (value.Attributes&FieldAttributes.FieldAccessMask)==FieldAttributes.Private &&
            ((value.Attributes&FieldAttributes.InitOnly)!=0)==readOnly,
            "FIELD "+type+"."+field+" : "+expected+(readOnly?" readonly":""));
    }
    private static void CheckNonPublicProperty(Metadata meta,string type,string property,string expected,MethodAttributes access,bool getOnly)
    {
        var value=meta.Type(type).GetProperties().Select(meta.Reader.GetPropertyDefinition).Single(p=>meta.Reader.GetString(p.Name)==property);
        var methods=value.GetAccessors();
        var getter=meta.Reader.GetMethodDefinition(methods.Getter);
        Check(value.DecodeSignature(names,null).ReturnType==expected &&
            (getter.Attributes&MethodAttributes.MemberAccessMask)==access && (getter.Attributes&MethodAttributes.Static)==0 &&
            (!getOnly||methods.Setter.IsNil),"PROPERTY "+type+"."+property+" : "+expected+" (non-public)");
    }
    private static void CheckMkIpFraming(Metadata meta)
    {
        var method=meta.Type("MostIPI.Similar.MK_IP.MkIpInfo").GetMethods().Select(meta.Reader.GetMethodDefinition)
            .Single(m=>meta.Reader.GetString(m.Name)=="GetConnectionParams");
        var body=meta.Instructions(method).ToArray();
        int assignment=-1;
        for(int i=1;i<body.Length;i++)
        {
            if(body[i].Op!=OpCodes.Call||body[i-1].Op!=OpCodes.Ldc_I4_1) continue;
            var handle=MetadataTokens.EntityHandle(body[i].Token);
            if(handle.Kind!=HandleKind.MethodDefinition) continue;
            var helper=meta.Reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            if(meta.Instructions(helper).Any(x=>(x.Op==OpCodes.Call||x.Op==OpCodes.Callvirt)&&
                meta.MemberName(x.Token)=="Core.Adapters.AdapterConnectionDto.set_ForceUsbScheme")) assignment=i;
        }
        Check(assignment>0 && !body.Take(assignment).Any(x=>x.Op.FlowControl==FlowControl.Branch||x.Op.FlowControl==FlowControl.Cond_Branch||x.Op.FlowControl==FlowControl.Return),
            "IL MkIpInfo.GetConnectionParams unconditionally sets ForceUsbScheme=true before first branch");
    }
    private static void CheckEthernetFramingFactory(Metadata meta)
    {
        const string prefix="BaseAdapterTransport.Core.Transport.Impl.";
        var method=meta.Type("BaseAdapterTransport.Core.Transport.TransportChannelFactory").GetMethods().Select(meta.Reader.GetMethodDefinition)
            .Single(m=>meta.Reader.GetString(m.Name)=="CreateTransportChannel");
        var calls=meta.Instructions(method).Where(x=>x.Op==OpCodes.Call||x.Op==OpCodes.Callvirt||x.Op==OpCodes.Newobj)
            .Select(x=>meta.MemberName(x.Token)).ToArray();
        Check(calls.Contains("Core.Adapters.AdapterConnectionDto.get_ForceUsbScheme") && calls.Contains(prefix+"WebPort..ctor") &&
            calls.Contains(prefix+"UsbWrapper..ctor") && calls.Contains(prefix+"CryptoUsb..ctor") && calls.Contains(prefix+"CryptoWebPort..ctor"),
            "IL factory references ForceUsbScheme and both TCP framing constructions (control flow reviewed separately)");
    }
    private static void CheckAdapterSegmentHint(Metadata ethernet, string vendor)
    {
        const string adapterType = "UsbEthernetAdapter.UsbProxyAdapter";
        var property = ethernet.Type(adapterType).GetProperties().Select(ethernet.Reader.GetPropertyDefinition)
            .Single(p => ethernet.Reader.GetString(p.Name) == "SegmentGuid");
        var access = property.GetAccessors();
        var getter = ethernet.Reader.GetMethodDefinition(access.Getter);
        var setter = ethernet.Reader.GetMethodDefinition(access.Setter);
        var reads = ethernet.Instructions(getter).Where(x => x.Op != OpCodes.Nop).ToArray();
        Check(property.DecodeSignature(names,null).ReturnType == "System.Guid" && reads.Length == 3 &&
            reads[0].Op == OpCodes.Ldarg_0 && reads[1].Op == OpCodes.Ldfld && reads[2].Op == OpCodes.Ret,
            "IL UsbProxyAdapter.SegmentGuid reads one plain Guid backing field");
        int fieldToken = reads[1].Token;
        var field = ethernet.Reader.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(fieldToken));
        Check(field.DecodeSignature(names,null) == "System.Guid" &&
            ethernet.FullName(field.GetDeclaringType()) == adapterType &&
            (field.Attributes & FieldAttributes.Static) == 0,
            "FIELD SegmentGuid is per-instance Guid metadata, not computed route identity");
        var stores = new List<MethodDefinitionHandle>();
        bool byReference = false;
        foreach (var handle in ethernet.Reader.MethodDefinitions)
        {
            var method = ethernet.Reader.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            foreach (var instruction in ethernet.Instructions(method))
            {
                if (instruction.Token != fieldToken) continue;
                if (instruction.Op == OpCodes.Stfld) stores.Add(handle);
                if (instruction.Op == OpCodes.Ldflda) byReference = true;
            }
        }
        Check(stores.Count == 1 && stores[0] == access.Setter && !byReference,
            "IL SegmentGuid backing field is written only by its setter; constructors do not assign it");
        var assignmentCalls = new List<string>();
        foreach (string file in Directory.EnumerateFiles(vendor).Where(p => p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var meta = new Metadata(file);
                foreach (var handle in meta.Reader.MethodDefinitions)
                {
                    var method = meta.Reader.GetMethodDefinition(handle);
                    if (method.RelativeVirtualAddress == 0) continue;
                    foreach (var instruction in meta.Instructions(method).Where(x => x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt))
                    {
                        string name = meta.MemberName(instruction.Token);
                        if (name == "Core.IAdapter.set_SegmentGuid" || name == adapterType + ".set_SegmentGuid" ||
                            name == "BaseAdapter.LockedAdapter.set_SegmentGuid")
                            assignmentCalls.Add(Path.GetFileName(file) + ":" + meta.FullName(method.GetDeclaringType()));
                    }
                }
            }
            catch (BadImageFormatException) { /* Native PE files have no managed metadata. */ }
        }
        Check(assignmentCalls.Count == 1 && assignmentCalls[0] == "BaseAdapter.dll:BaseAdapter.LockedAdapter",
            "IL official managed app contains only LockedAdapter's forwarding setter call; no native assignment of adapter SegmentGuid");
    }
    private static void CheckDirectAdapterOwner(Metadata core, Metadata plugin)
    {
        const string converter = "Core.Adapters.AdapterUrlConverter";
        var owner = core.Type(converter).GetProperties().Select(core.Reader.GetPropertyDefinition)
            .Single(p => core.Reader.GetString(p.Name) == "AdapterOwnerGuid");
        var access = owner.GetAccessors();
        Check(owner.DecodeSignature(names,null).ReturnType == "System.Guid" &&
            (core.Reader.GetMethodDefinition(access.Getter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
            (core.Reader.GetMethodDefinition(access.Setter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private,
            "PROPERTY AdapterUrlConverter.AdapterOwnerGuid is public-read/private-write Guid");
        int setterToken = MetadataTokens.GetToken(access.Setter);
        var ctor = core.Type(converter).GetMethods().Select(core.Reader.GetMethodDefinition)
            .Single(m => core.Reader.GetString(m.Name) == ".ctor" && m.DecodeSignature(names,null).ParameterTypes
                .SequenceEqual(new[] { "Core.Adapters.AdapterConnectionDto", "Byte[]", "System.Guid", "Boolean" }));
        var instructions = core.Instructions(ctor).ToArray();
        Check(Enumerable.Range(1,instructions.Length-1).Any(i => instructions[i].Op == OpCodes.Call && instructions[i].Token == setterToken &&
            instructions[i-1].Op == OpCodes.Ldarg_3),
            "IL AdapterUrlConverter constructor stores its third argument as AdapterOwnerGuid");
        var uri = core.Type(converter).GetMethods().Select(core.Reader.GetMethodDefinition)
            .Single(m => core.Reader.GetString(m.Name) == "GetAdapterUri");
        Check(core.Instructions(uri).Any(x => (x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt) &&
            x.Token == MetadataTokens.GetToken(access.Getter)),
            "IL GetAdapterUri serializes AdapterOwnerGuid into the native URI");
        Check(core.Type(converter).GetMethods().Select(core.Reader.GetMethodDefinition).Where(m => m.RelativeVirtualAddress != 0)
            .Any(m => { var calls = core.Instructions(m).Where(x => x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt).ToArray();
                return calls.Any(x => x.Token == setterToken) && calls.Any(x => core.MemberName(x.Token) == "System.Guid.Parse"); }),
            "IL native URI parser restores AdapterOwnerGuid using Guid.Parse");

        var segmentMethods = plugin.Type("CorePlugins.segment.CustomInfo").GetMethods().ToArray();
        bool Calls(MethodDefinitionHandle handle, string target) => plugin.Instructions(plugin.Reader.GetMethodDefinition(handle))
            .Any(x => (x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt || x.Op == OpCodes.Newobj) && plugin.MemberName(x.Token) == target);
        var ownerHelper = segmentMethods.Single(h => plugin.Reader.GetMethodDefinition(h).DecodeSignature(names,null).ParameterTypes
            .SequenceEqual(new[] { "Core.BaseCustomInfo" }) && Calls(h,"Core.BaseCustomInfo.get_OwnerGUID"));
        var converterHelper = segmentMethods.Single(h => {
            var method = plugin.Reader.GetMethodDefinition(h);
            return method.DecodeSignature(names,null).ParameterTypes.SequenceEqual(new[] { "Core.Adapters.AdapterConnectionDto", "Byte[]", "System.Guid", "Boolean" }) &&
                Calls(h,converter+"..ctor"); });
        var paramsHelper = segmentMethods.Single(h => Calls(h,"Core.DeviceTypes.CombineDeviceWithNetworkAdapter.GetConnectionParams"));
        int ownerHelperToken=MetadataTokens.GetToken(ownerHelper), converterHelperToken=MetadataTokens.GetToken(converterHelper), paramsHelperToken=MetadataTokens.GetToken(paramsHelper);
        bool matched=false;
        foreach(var handle in segmentMethods)
        {
            var method=plugin.Reader.GetMethodDefinition(handle);
            if(method.RelativeVirtualAddress==0)continue;
            var body=plugin.Instructions(method).ToArray();
            for(int i=0;i<body.Length;i++)
            {
                if(body[i].Op!=OpCodes.Call || body[i].Token!=converterHelperToken)continue;
                var start=i-1;
                while(start>=0 && i-start<18 && body[start].Op.FlowControl!=FlowControl.Branch &&
                    body[start].Op.FlowControl!=FlowControl.Cond_Branch && body[start].Op.FlowControl!=FlowControl.Return)start--;
                var arguments=body.Skip(start+1).Take(i-start-1).ToArray();
                if(arguments.Any(x=>x.Op==OpCodes.Call && x.Token==ownerHelperToken) &&
                    arguments.Any(x=>x.Op==OpCodes.Call && x.Token==paramsHelperToken)) matched=true;
            }
        }
        Check(matched,"IL segment native URI generation passes the adapter record's OwnerGUID beside its ConnectionParams to AdapterUrlConverter");
    }
    private static void CheckPhysicalInterfaceName(Metadata ethernet)
    {
        var type = ethernet.Type("UsbEthernetAdapter.UsbProxyAdapter");
        var getter = type.GetMethods().Select(ethernet.Reader.GetMethodDefinition)
            .Single(m => ethernet.Reader.GetString(m.Name) == "GetInterfaceName");
        var il = ethernet.Instructions(getter).Where(x => x.Op != OpCodes.Nop && x.Op != OpCodes.Volatile).ToArray();
        Check(il[0].Op == OpCodes.Ldarg_0 && il[1].Op == OpCodes.Ldfld,
            "IL physical GetInterfaceName reads the adapter's saved Open argument");
        int field = il[1].Token;
        int stores = 0;
        foreach (var method in type.GetMethods().Select(ethernet.Reader.GetMethodDefinition))
        {
            if (method.RelativeVirtualAddress == 0) continue;
            var body = ethernet.Instructions(method).Where(x => x.Op != OpCodes.Volatile).ToArray();
            for (int n = 1; n < body.Length; n++)
                if (body[n].Op == OpCodes.Stfld && body[n].Token == field)
                {
                    Check((ethernet.Reader.GetString(method.Name) == "Open" || ethernet.Reader.GetString(method.Name) == "OpenForFirmware") && body[n-1].Op == OpCodes.Ldarg_1,
                        "IL saved physical interface is assigned only from Open's adapterName argument, not an outer tunnel URI");
                    stores++;
                }
        }
        Check(stores == 2, "Inspected Open and OpenForFirmware retain their physical adapterName");
    }

    private static void CheckSelectiveModuleMetadata(Metadata core, Metadata plugin, Metadata mkip, Metadata transport)
    {
        const string segment = "CorePlugins.segment.CustomInfo";
        const string module = "MostIPI.Similar.MK_IP.MkIpInfo";
        var methods = plugin.Type(segment).GetMethods().ToArray();
        bool Calls(Metadata meta, MethodDefinitionHandle handle, string target) =>
            meta.Instructions(meta.Reader.GetMethodDefinition(handle)).Any(x =>
                (x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt || x.Op == OpCodes.Newobj) && meta.MemberName(x.Token) == target);
        MethodDefinitionHandle Helper(string target) => methods.Single(h => Calls(plugin,h,target));

        var cacheHandle = plugin.Type(segment).GetFields().Single(h => plugin.Reader.GetString(plugin.Reader.GetFieldDefinition(h).Name) == "adapterNameValue");
        var cache = plugin.Reader.GetFieldDefinition(cacheHandle);
        Check(cache.DecodeSignature(names,null) == "String" &&
            (cache.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private &&
            (cache.Attributes & (FieldAttributes.Static | FieldAttributes.InitOnly)) == 0,
            "FIELD CustomInfo.adapterNameValue is the private instance String cache of native adapter parameters");
        var reset = plugin.Type(segment).GetMethods().Select(plugin.Reader.GetMethodDefinition)
            .Single(m => plugin.Reader.GetString(m.Name) == "ResetAdapterName");
        var resetIl = plugin.Instructions(reset).ToArray();
        Check(Enumerable.Range(1,resetIl.Length-1).Any(i => resetIl[i].Op == OpCodes.Stfld &&
            resetIl[i].Token == MetadataTokens.GetToken(cacheHandle) && resetIl[i-1].Op == OpCodes.Ldnull),
            "IL ResetAdapterName clears the exact adapterNameValue cache");
        foreach (string property in new[] { "IsCloudEnabled", "IsDirectConnectionEnabled", "IsUSBEnabled" })
        {
            var prop = plugin.Type(segment).GetProperties().Select(plugin.Reader.GetPropertyDefinition)
                .Single(p => plugin.Reader.GetString(p.Name) == property);
            var getter = plugin.Reader.GetMethodDefinition(prop.GetAccessors().Getter);
            var il = plugin.Instructions(getter).Where(x => x.Op != OpCodes.Nop).ToArray();
            Check(prop.DecodeSignature(names,null).ReturnType == "Boolean" &&
                (getter.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                il.Length == 3 && il[0].Op == OpCodes.Ldarg_0 && il[1].Op == OpCodes.Ldfld && il[2].Op == OpCodes.Ret,
                "IL CustomInfo." + property + " is a side-effect-free Boolean backing-field getter");
        }
        var adapterMode = mkip.Type(module).GetMethods().Select(mkip.Reader.GetMethodDefinition)
            .Single(m => mkip.Reader.GetString(m.Name) == "get_IsAdapterMode");
        var modeIl = mkip.Instructions(adapterMode).Where(x => x.Op != OpCodes.Nop).ToArray();
        Check(modeIl.Length == 2 && modeIl[0].Op == OpCodes.Ldc_I4_1 && modeIl[1].Op == OpCodes.Ret,
            "IL MkIpInfo.IsAdapterMode is unconditionally true for the configuration URI branch");
        var password = mkip.Type(module).GetProperties().Select(mkip.Reader.GetPropertyDefinition)
            .Single(p => mkip.Reader.GetString(p.Name) == "ConnectionPasswordMD5Code");
        Check(password.DecodeSignature(names,null).ReturnType == "Byte[]" &&
            (mkip.Reader.GetMethodDefinition(password.GetAccessors().Getter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
            "PROPERTY MkIpInfo.ConnectionPasswordMD5Code is a public Byte[] read contract");

        int setSerial = MetadataTokens.GetToken(Helper("Core.Adapters.AdapterConnectionDto.set_SerialNumber"));
        int paramsHelper = MetadataTokens.GetToken(Helper("Core.DeviceTypes.CombineDeviceWithNetworkAdapter.GetConnectionParams"));
        var generate = methods.Single(h => plugin.Instructions(plugin.Reader.GetMethodDefinition(h)).Any(x => x.Op == OpCodes.Call && x.Token == paramsHelper));
        var generateIl = plugin.Instructions(plugin.Reader.GetMethodDefinition(generate)).ToArray();
        int cloudGetter = MetadataTokens.GetToken(plugin.Type(segment).GetProperties().Select(plugin.Reader.GetPropertyDefinition)
            .Single(p => plugin.Reader.GetString(p.Name) == "IsCloudEnabled").GetAccessors().Getter);
        int usbGetter = MetadataTokens.GetToken(plugin.Type(segment).GetProperties().Select(plugin.Reader.GetPropertyDefinition)
            .Single(p => plugin.Reader.GetString(p.Name) == "IsUSBEnabled").GetAccessors().Getter);
        var provenSelectors = new List<int>();
        foreach (var instruction in generateIl.Where(x => (x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt) && x.Token == cloudGetter))
        {
            if (TraceCloudSelector(generateIl,instruction.Offset,cloudGetter,setSerial,usbGetter,true) == "serial" &&
                TraceCloudSelector(generateIl,instruction.Offset,cloudGetter,setSerial,usbGetter,false) == "usb")
                provenSelectors.Add(instruction.Offset);
        }
        Check(provenSelectors.Count == 1,
            "CFG native configuration URI assigns SerialNumber only for Cloud enabled; Cloud disabled skips directly to the USB selector");
        Console.WriteLine("EVIDENCE GenerateAdapterName token=0x" + MetadataTokens.GetToken(generate).ToString("X8") +
            " Cloud selector IL_" + provenSelectors[0].ToString("X4") + "; branch evaluation reads IL only");

        var factory = transport.Type("BaseAdapterTransport.Core.Transport.TransportChannelFactory").GetMethods().Select(transport.Reader.GetMethodDefinition)
            .Single(m => transport.Reader.GetString(m.Name) == "CreateTransportChannel");
        var factoryIl = transport.Instructions(factory).ToArray();
        Check(Enumerable.Range(1,factoryIl.Length-1).Any(i => factoryIl[i].Op == OpCodes.Newobj &&
            transport.MemberName(factoryIl[i].Token) == "BaseAdapterTransport.Core.Transport.Impl.CloudProxy..ctor" &&
            (factoryIl[i-1].Op == OpCodes.Call || factoryIl[i-1].Op == OpCodes.Callvirt) &&
            transport.MemberName(factoryIl[i-1].Token) == "Core.Adapters.AdapterConnectionDto.get_SerialNumber"),
            "IL transport factory uses DTO SerialNumber as the CloudProxy constructor address, not a device ID readback");
    }

    // Bounded evaluation of arithmetic/branches in the obfuscated selector.
    // No vendor method executes. The only allowed call is the selected Boolean
    // getter; reaching another call stops the trace. Reference locals are never
    // dereferenced and are represented by zero until the traced setter boundary.
    private static string TraceCloudSelector(Instruction[] body, int start, int cloudGetter, int serialSetter, int usbGetter, bool cloud)
    {
        try
        {
            var instructions = body.ToDictionary(x => x.Offset);
            var stack = new Stack<int>();
            var locals = new Dictionary<int,int>();
            stack.Push(0); // this for the initial Cloud flag getter
            int pc = start;
            for (int step=0;step<500;step++)
            {
                var x = instructions[pc]; pc = x.NextOffset;
                if (x.Op == OpCodes.Call || x.Op == OpCodes.Callvirt)
                {
                    if (x.Token == serialSetter) return "serial";
                    if (x.Token == usbGetter) return "usb";
                    if (x.Token != cloudGetter) return "other-call";
                    stack.Pop(); stack.Push(cloud?1:0); continue;
                }
                if (x.Op == OpCodes.Nop) continue;
                if (x.Op == OpCodes.Ldarg_0 || x.Op == OpCodes.Ldnull) { stack.Push(0); continue; }
                int local = LocalIndex(x);
                if (x.Op.Name.StartsWith("ldloc",StringComparison.Ordinal)) { stack.Push(locals.TryGetValue(local,out int value)?value:0); continue; }
                if (x.Op.Name.StartsWith("stloc",StringComparison.Ordinal)) { locals[local]=stack.Pop(); continue; }
                if (x.Op.Name.StartsWith("ldc.i4",StringComparison.Ordinal)) { stack.Push(IntConstant(x)); continue; }
                if (x.Op == OpCodes.Dup) { stack.Push(stack.Peek()); continue; }
                if (x.Op == OpCodes.Pop) { stack.Pop(); continue; }
                if (x.Op == OpCodes.Conv_U4 || x.Op == OpCodes.Conv_I4) continue;
                if (x.Op == OpCodes.Br || x.Op == OpCodes.Br_S) { pc=x.Targets[0]; continue; }
                if (x.Op == OpCodes.Brtrue || x.Op == OpCodes.Brtrue_S) { if(stack.Pop()!=0)pc=x.Targets[0]; continue; }
                if (x.Op == OpCodes.Brfalse || x.Op == OpCodes.Brfalse_S) { if(stack.Pop()==0)pc=x.Targets[0]; continue; }
                if (x.Op == OpCodes.Switch) { int n=stack.Pop(); if(n>=0&&n<x.Targets.Length)pc=x.Targets[n]; continue; }
                if (x.Op == OpCodes.Xor || x.Op == OpCodes.Mul || x.Op == OpCodes.Add || x.Op == OpCodes.Sub || x.Op == OpCodes.Rem_Un)
                {
                    int b=stack.Pop(),a=stack.Pop();
                    stack.Push(x.Op == OpCodes.Xor ? a^b : x.Op == OpCodes.Mul ? unchecked(a*b) : x.Op == OpCodes.Add ? unchecked(a+b) :
                        x.Op == OpCodes.Sub ? unchecked(a-b) : unchecked((int)((uint)a%(uint)b))); continue;
                }
                return "unsupported:"+x.Op.Name;
            }
            return "limit";
        }
        catch { return "invalid"; }
    }
    private static int LocalIndex(Instruction x)
    {
        if (x.Op == OpCodes.Ldloc_0 || x.Op == OpCodes.Stloc_0)return 0;
        if (x.Op == OpCodes.Ldloc_1 || x.Op == OpCodes.Stloc_1)return 1;
        if (x.Op == OpCodes.Ldloc_2 || x.Op == OpCodes.Stloc_2)return 2;
        if (x.Op == OpCodes.Ldloc_3 || x.Op == OpCodes.Stloc_3)return 3;
        return x.Value;
    }
    private static int IntConstant(Instruction x)
    {
        if (x.Op == OpCodes.Ldc_I4_M1)return -1;
        if (x.Op == OpCodes.Ldc_I4_0)return 0;
        if (x.Op == OpCodes.Ldc_I4_1)return 1;
        if (x.Op == OpCodes.Ldc_I4_2)return 2;
        if (x.Op == OpCodes.Ldc_I4_3)return 3;
        if (x.Op == OpCodes.Ldc_I4_4)return 4;
        if (x.Op == OpCodes.Ldc_I4_5)return 5;
        if (x.Op == OpCodes.Ldc_I4_6)return 6;
        if (x.Op == OpCodes.Ldc_I4_7)return 7;
        if (x.Op == OpCodes.Ldc_I4_8)return 8;
        return x.Value;
    }
    private static void CheckProperty(Metadata meta, string type, string property, string expected, bool xml)
    {
        var value = meta.Type(type).GetProperties().Select(meta.Reader.GetPropertyDefinition).Single(p => meta.Reader.GetString(p.Name) == property);
        var accessors = value.GetAccessors();
        bool readable = !accessors.Getter.IsNil && (meta.Reader.GetMethodDefinition(accessors.Getter).Attributes & MethodAttributes.Public) != 0;
        bool writable = !accessors.Setter.IsNil && (meta.Reader.GetMethodDefinition(accessors.Setter).Attributes & MethodAttributes.Public) != 0;
        Check(value.DecodeSignature(names,null).ReturnType == expected && readable && writable,
            "PROPERTY " + type + "." + property + " : " + expected);
        if (xml) Check(!value.GetCustomAttributes().Any(h => meta.AttributeType(h) == "System.Xml.Serialization.XmlIgnoreAttribute"), "XML includes " + property);
    }
    private static void CheckMethod(Metadata meta, string type, string method, string expectedReturn)
    {
        var value = meta.Type(type).GetMethods().Select(meta.Reader.GetMethodDefinition).Single(m => meta.Reader.GetString(m.Name) == method);
        var signature = value.DecodeSignature(names,null);
        Check((value.Attributes & MethodAttributes.Public) != 0 && (value.Attributes & MethodAttributes.Static) == 0 &&
            signature.ParameterTypes.Length == 0 && (expectedReturn == null ? signature.ReturnType.EndsWith(".IAdapter",StringComparison.Ordinal) : signature.ReturnType == expectedReturn),
            "METHOD " + type + "." + method + " -> " + signature.ReturnType);
    }
    private static void CheckSignature(Metadata meta, string type, string method, string expectedReturn, params string[] parameters)
    {
        var matches = meta.Type(type).GetMethods().Select(meta.Reader.GetMethodDefinition).Where(m => meta.Reader.GetString(m.Name)==method)
            .Where(m => m.DecodeSignature(names,null).ParameterTypes.SequenceEqual(parameters)).ToArray();
        Check(matches.Length==1 && (matches[0].Attributes & MethodAttributes.Public)!=0 &&
            matches[0].DecodeSignature(names,null).ReturnType==expectedReturn,
            "SIGNATURE " + type + "." + method + "("+String.Join(",",parameters)+") -> " + expectedReturn);
    }
    private static void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); checks++; Console.WriteLine("OK " + text); }
    private sealed class Metadata : IDisposable
    {
        private static readonly Dictionary<ushort,OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
            .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null))
            .ToDictionary(op=>unchecked((ushort)op.Value));
        private readonly FileStream file;
        private readonly PEReader pe;
        public readonly MetadataReader Reader;
        public Metadata(string path) { file=File.OpenRead(path); pe=new PEReader(file);
            if(!pe.HasMetadata) {pe.Dispose();file.Dispose();throw new BadImageFormatException("Native PE has no managed metadata.");}
            Reader=pe.GetMetadataReader(); }
        public TypeDefinition Type(string name) { return Reader.TypeDefinitions.Select(Reader.GetTypeDefinition).Single(t => Reader.GetString(t.Namespace)+"."+Reader.GetString(t.Name)==name); }
        public string FullName(TypeDefinitionHandle handle) { var t=Reader.GetTypeDefinition(handle); return Reader.GetString(t.Namespace)+"."+Reader.GetString(t.Name); }
        public string Name(EntityHandle handle)
        {
            if(handle.Kind==HandleKind.TypeDefinition)return FullName((TypeDefinitionHandle)handle);
            if(handle.Kind==HandleKind.TypeReference){var t=Reader.GetTypeReference((TypeReferenceHandle)handle);return Reader.GetString(t.Namespace)+"."+Reader.GetString(t.Name);}
            return "";
        }
        public string MemberName(int token)
        {
            EntityHandle handle=MetadataTokens.EntityHandle(token);
            if(handle.Kind==HandleKind.MethodDefinition){var m=Reader.GetMethodDefinition((MethodDefinitionHandle)handle);return FullName(m.GetDeclaringType())+"."+Reader.GetString(m.Name);}
            if(handle.Kind==HandleKind.MemberReference){var m=Reader.GetMemberReference((MemberReferenceHandle)handle);return Name(m.Parent)+"."+Reader.GetString(m.Name);}
            return "";
        }
        public IEnumerable<Instruction> Instructions(MethodDefinition method)
        {
            byte[] bytes=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            for(int offset=0;offset<bytes.Length;)
            {
                int start=offset;
                ushort value=bytes[offset++];
                if(value==0xfe)value=(ushort)(0xfe00|bytes[offset++]);
                OpCode op=Opcodes[value];
                int token=0,length,number=0;
                int[] targets=Array.Empty<int>();
                switch(op.OperandType)
                {
                    case OperandType.InlineNone:length=0;break;
                    case OperandType.ShortInlineBrTarget:length=1;targets=new[]{offset+1+(sbyte)bytes[offset]};break;
                    case OperandType.ShortInlineI:length=1;number=(sbyte)bytes[offset];break;
                    case OperandType.ShortInlineVar:length=1;number=bytes[offset];break;
                    case OperandType.InlineVar:length=2;number=BitConverter.ToUInt16(bytes,offset);break;
                    case OperandType.InlineBrTarget:length=4;targets=new[]{offset+4+BitConverter.ToInt32(bytes,offset)};break;
                    case OperandType.InlineI8:case OperandType.InlineR:length=8;break;
                    case OperandType.InlineSwitch:
                        int count=BitConverter.ToInt32(bytes,offset);length=4+4*count;
                        targets=Enumerable.Range(0,count).Select(i=>offset+length+BitConverter.ToInt32(bytes,offset+4+4*i)).ToArray();break;
                    default:length=4;number=token=BitConverter.ToInt32(bytes,offset);break;
                }
                offset+=length;
                yield return new Instruction{Op=op,Token=token,Value=number,Offset=start,NextOffset=offset,Targets=targets};
            }
        }
        public string AttributeType(CustomAttributeHandle handle)
        {
            var attribute=Reader.GetCustomAttribute(handle);
            EntityHandle parent=attribute.Constructor.Kind==HandleKind.MemberReference ? Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent : Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
            if(parent.Kind==HandleKind.TypeDefinition) return FullName((TypeDefinitionHandle)parent);
            if(parent.Kind==HandleKind.TypeReference) { var t=Reader.GetTypeReference((TypeReferenceHandle)parent); return Reader.GetString(t.Namespace)+"."+Reader.GetString(t.Name); }
            return "";
        }
        public void Dispose() { pe.Dispose(); file.Dispose(); }
    }
    private sealed class Instruction {public OpCode Op;public int Token,Value,Offset,NextOffset;public int[] Targets;}
    private sealed class Names : ISignatureTypeProvider<string,object>
    {
        public string GetArrayType(string e,ArrayShape s)=>e+"[]";
        public string GetByReferenceType(string e)=>e+"&";
        public string GetFunctionPointerType(MethodSignature<string> s)=>"function";
        public string GetGenericInstantiation(string g,ImmutableArray<string> a)=>g+"<"+String.Join(",",a)+">";
        public string GetGenericMethodParameter(object c,int i)=>"!!"+i;
        public string GetGenericTypeParameter(object c,int i)=>"!"+i;
        public string GetModifiedType(string m,string u,bool r)=>u;
        public string GetPinnedType(string e)=>e;
        public string GetPointerType(string e)=>e+"*";
        public string GetPrimitiveType(PrimitiveTypeCode c)=>c.ToString();
        public string GetSZArrayType(string e)=>e+"[]";
        public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k){var t=r.GetTypeDefinition(h);return r.GetString(t.Namespace)+"."+r.GetString(t.Name);}
        public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k){var t=r.GetTypeReference(h);return r.GetString(t.Namespace)+"."+r.GetString(t.Name);}
        public string GetTypeFromSpecification(MetadataReader r,object c,TypeSpecificationHandle h,byte k)=>r.GetTypeSpecification(h).DecodeSignature(this,c);
    }
}
