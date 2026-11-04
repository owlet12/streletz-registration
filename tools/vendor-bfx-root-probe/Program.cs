// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Serialization;

// Independent regression runner. Vendor assemblies are external inputs and are
// never copied into the deliverable. No configuration/device object is
// constructed; no Master entry point, native plugin, adapter or equipment
// operation is called. XML metadata attributes are read by the serializer.
internal static class Program
{
    private const string SegmentType = "CorePlugins.segment.CustomInfo";
    private const string AdapterType = "MostIPI.Similar.MK_IP.MkIpInfo";
    private const string RepeaterType = "StreletcPro.RRI.Info";
    private const string DeviceType = "StreletcPro.RR.Info";
    private const string SecretMarker = "SYNTHETIC-VENDOR-SECRET-MUST-NOT-EXPORT";
    private static readonly string[][] Cases = {
        new[] { "Plugin.CorePlugins.dll", SegmentType },
        new[] { "Plugin.MostIPI.dll", AdapterType },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.Panel_1.Info" },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.Panel_2.Info" },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.Panel_3.Info" },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.RRI_KS.Info" },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.RRI2_KS.Info" },
        new[] { "Plugin.RROP-LON.dll", "RROP_LON.KS.CustomInfo" },
        new[] { "Plugin.PanelPT.dll", "PanelPT.Info" },
        new[] { "Plugin.StreletcPro.dll", DeviceType },
        new[] { "Plugin.StreletcPro.dll", RepeaterType }
    };
    private static int assertions;

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            for (Exception e = error; e != null; e = e.InnerException)
                Console.Error.WriteLine("FAIL " + e.GetType().FullName + ": " + e.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: BfxResourceRootProbe.exe <MasterDirectory> [<BridgeExe> <OutputDirectory> [<ExpectedPanels=7> [legacy-control]]]");
            return 2;
        }
        string vendor = Path.GetFullPath(args[0]);
        string bridge = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
        string output = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
        int expected = args.Length > 3 ? Int32.Parse(args[3]) : 7;
        bool legacyControl = args.Length > 4 && args[4] == "legacy-control";
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string candidate = Path.Combine(vendor, new AssemblyName(request.Name).Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        Console.WriteLine("RUNTIME " + Environment.Version + "; " + Environment.OSVersion.Platform);
        foreach (string dll in Cases.Select(test => test[0]).Concat(new[] { "Core.dll", "DataManager.dll" }).Distinct())
            Console.WriteLine("INPUT_SHA256 " + dll + " " + Sha256(Path.Combine(vendor, dll)));
        var payloads = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        foreach (string[] test in Cases)
        {
            Type type = Assembly.LoadFrom(Path.Combine(vendor, test[0])).GetType(test[1], true);
            XmlTypeMapping mapping = new XmlReflectionImporter().ImportTypeMapping(type);
            var text = new StringWriter();
            // Exactly the XmlSerializer(Type) constructor used by the vendor's
            // StructureTreeResource.WriteXml. Null avoids all instance constructors
            // and property getters of the inspected configuration/device types
            // while retaining their real XML root mapping.
            var serializer = new XmlSerializer(type);
            using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
                serializer.Serialize(writer, null);
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(text.ToString());
            Check(document.DocumentElement.LocalName == mapping.ElementName && document.DocumentElement.NamespaceURI == mapping.Namespace,
                "XmlSerializer and XmlReflectionImporter disagree for " + type.FullName);
            payloads.Add(type.FullName, document.DocumentElement);
            Console.WriteLine("ROOT " + type.FullName + " => " + document.DocumentElement.LocalName + "; namespace=" + mapping.Namespace);
            Console.WriteLine("  SERIALIZED_NULL " + text);
        }
        Console.WriteLine("TYPE_MAP_PASS " + payloads.Count + "/" + Cases.Length);
        if (bridge != null)
        {
            Console.WriteLine("BRIDGE " + FileVersionInfo.GetVersionInfo(bridge).FileVersion + "; SHA256=" + Sha256(bridge));
            var fixture = Fixture(payloads, false);
            var fakeInfo = legacyControl ? Fixture(payloads, true) : null;
            if (output != null)
            {
                Directory.CreateDirectory(output);
                fixture.Save(Path.Combine(output, "vendor-derived-payload-roots.xml"));
                if (fakeInfo != null) fakeInfo.Save(Path.Combine(output, "artificial-all-Info-control.xml"));
            }
            // Load the real bridge assembly but never invoke its Main or any Form.
            // Read is a pure XML/project catalogue transform.
            Type parser = Assembly.LoadFrom(bridge).GetType("StreletzBridge.BfxPanelCatalog", true);
            MethodInfo read = parser.GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
            Check(read != null, "The bridge does not expose BfxPanelCatalog.Read");
            var project = Project();
            object[] records = (object[])project["records"];
            string[] journal = records.Cast<Dictionary<string, object>>().Select(row => Json(row["commissioning"])).ToArray();
            var panels = ((Array)read.Invoke(null, new object[] { fixture, project })).Cast<Dictionary<string, object>>().ToArray();
            Check(panels.Length == expected, "Expected " + expected + " panels from vendor-derived roots, received " + panels.Length);
            Console.WriteLine("PARSER_VENDOR_ROOTS " + panels.Length + " panels; expected " + expected);
            Check(((object[])project["records"]).Length == 7, "Panel import changed record count");
            for (int i = 0; i < 7; i++)
                Check(Json(((Dictionary<string, object>)records[i])["commissioning"]) == journal[i], "Panel import changed the commissioning journal");
            Check(!Json(project).Contains(SecretMarker), "A vendor authentication field leaked into the portable project");
            Check(panels.All(panel => !(bool)panel["available"] && ((Array)panel["verifiedRecordIds"]).Length == 0),
                "Offline panel import enabled or verified registration");
            if (expected == 7)
            {
                for (int i = 0; i < 7; i++)
                {
                    string recordId = Id(4, i);
                    var owner = panels.Where(panel => ((IEnumerable<string>)panel["recordIds"]).Contains(recordId)).ToArray();
                    Check(owner.Length == 1, "Each fixture record must have exactly one panel owner");
                    Check((string)owner[0]["nativePanelId"] == Id(3, i) && (string)owner[0]["segmentId"] == Id(2, i),
                        "Record membership resolved to the wrong controller or segment");
                    Check((string)owner[0]["host"] == "192.0.2.240" && Convert.ToInt32(owner[0]["port"]) == 6344 && (string)owner[0]["moduleId"] == "112233445566",
                        "The MK-IP payload root or its serialized route fields were not read");
                }
            }
            if (fakeInfo != null)
            {
                var control = (Array)read.Invoke(null, new object[] { fakeInfo, Project() });
                Check(control.Length == 7, "The artificial all-Info control did not produce seven panels");
                Console.WriteLine("ARTIFICIAL_ALL_INFO_CONTROL " + control.Length + " panels");
            }
        }
        Console.WriteLine("PASS " + assertions + " assertions; no configuration/device constructors or getters, UI, native pipe or equipment operations invoked");
        return 0;
    }

    private static XmlDocument Fixture(Dictionary<string, XmlElement> payloads, bool forceInfo)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml("<Root><Items/><Resources/></Root>");
        XmlNode items = doc.SelectSingleNode("/Root/Items");
        AddItem(doc, items, Id(1, 0), "MK-IP");
        XmlElement adapter = AddResource(doc, payloads, AdapterType, Id(1, 0), forceInfo);
        Add(doc, adapter, "ID", Convert.ToBase64String(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }));
        XmlElement address = Add(doc, adapter, "ConnectionIpAddress");
        int[] octets = { 192, 0, 2, 240 };
        for (int i = 0; i < octets.Length; i++) Add(doc, address, "A" + (i + 1), octets[i].ToString());
        Add(doc, adapter, "ConnectionPort", "0");
        Add(doc, adapter, "EthernetConnectionPort", "6344");
        Add(doc, adapter, "Password", SecretMarker);
        Add(doc, adapter, "PasswordMd5", SecretMarker);
        for (int i = 0; i < 7; i++)
        {
            XmlElement segment = AddItem(doc, items, Id(2, i), "Same display name");
            XmlElement segmentInfo = AddResource(doc, payloads, SegmentType, Id(2, i), forceInfo);
            Add(doc, segmentInfo, "LonInterface", Id(1, 0));
            XmlElement controller = AddItem(doc, segment, Id(3, i), "Same display name");
            AddResource(doc, payloads, Cases[i + 2][1], Id(3, i), forceInfo);
            XmlElement repeater = AddItem(doc, controller, Id(5, i), "Repeater");
            AddResource(doc, payloads, RepeaterType, Id(5, i), forceInfo);
            AddItem(doc, repeater, Id(4, i), "Misleading name: belongs to a different panel");
            AddResource(doc, payloads, DeviceType, Id(4, i), forceInfo);
        }
        return doc;
    }

    private static XmlElement AddResource(XmlDocument doc, Dictionary<string, XmlElement> payloads, string type, string id, bool forceInfo)
    {
        XmlElement resource = Add(doc, doc.SelectSingleNode("/Root/Resources"), "Resource");
        resource.SetAttribute("id", id);
        XmlElement payload = forceInfo ? doc.CreateElement("Info") : (XmlElement)doc.ImportNode(payloads[type], true);
        payload.RemoveAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance");
        resource.AppendChild(payload);
        Add(doc, payload, "ClassType", type);
        Add(doc, payload, "Name", "Synthetic " + type);
        return payload;
    }

    private static XmlElement AddItem(XmlDocument doc, XmlNode parent, string id, string name)
    {
        XmlElement item = Add(doc, parent, "Item");
        item.SetAttribute("id", id); item.SetAttribute("CustomName", name); item.SetAttribute("addres", "1");
        return item;
    }

    private static XmlElement Add(XmlDocument doc, XmlNode parent, string name, string text = null)
    {
        XmlElement element = doc.CreateElement(name);
        if (text != null) element.InnerText = text;
        parent.AppendChild(element); return element;
    }

    private static Dictionary<string, object> Project()
    {
        return new Dictionary<string, object> { { "records", Enumerable.Range(0, 7).Select(i => (object)new Dictionary<string, object> {
            { "id", Id(4, i) }, { "description", "Original description" },
            { "commissioning", new Dictionary<string, object> { { "stage", 2 }, { "serial", "00000000000" + (i + 1) },
                { "note", "Preserve this note" }, { "description", "" }, { "updatedAt", 1234567L } } }
        }).ToArray() } };
    }

    private static string Id(int group, int index) { return group + "0000000-0000-4000-8000-" + (index + 1).ToString("D12"); }
    private static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(value); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); assertions++; }
    private static string Sha256(string path)
    {
        using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
}
