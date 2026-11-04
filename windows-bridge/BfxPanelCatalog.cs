// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace StreletzBridge
{
    // Reads the serialized project tree only. It does not initialize Master,
    // instantiate device drivers, connect to an adapter or send panel commands.
    internal static class BfxPanelCatalog
    {
        private const string SegmentClass = "CorePlugins.segment.CustomInfo";
        private const string MkIpClass = "MostIPI.Similar.MK_IP.MkIpInfo";
        private static readonly HashSet<string> ControllerClasses = new HashSet<string>(StringComparer.Ordinal) {
            "RROP_LON.Panel_1.Info", "RROP_LON.Panel_2.Info", "RROP_LON.Panel_3.Info", "PanelPT.Info",
            "RROP_LON.RRI_KS.Info", "RROP_LON.RRI2_KS.Info", "RROP_LON.KS.CustomInfo"
        };

        private sealed class Item
        {
            public string Id, ClassType;
            public XmlNode Xml, Info;
            public Item Parent;
            public readonly List<Item> Children = new List<Item>();
        }

        public static XmlNode ReadResourcePayload(XmlNode resource)
        {
            if (resource == null) throw new InvalidDataException("В BFX отсутствует ресурс устройства.");
            XmlElement[] payloads = resource.ChildNodes.OfType<XmlElement>().ToArray();
            if (payloads.Length != 1)
                throw new InvalidDataException("Некорректный ресурс BFX: ожидается один непосредственный элемент данных; обнаружено: " + payloads.Length + ".");
            XmlNode payload = payloads[0];
            XmlNodeList classTypes = payload.SelectNodes("ClassType");
            if (classTypes == null || classTypes.Count != 1 || String.IsNullOrWhiteSpace(classTypes[0].InnerText)
                || classTypes[0].ChildNodes.OfType<XmlElement>().Any())
                throw new InvalidDataException("Некорректный ресурс BFX: ожидается одно непустое непосредственное поле ClassType; обнаружено: "
                    + (classTypes == null ? 0 : classTypes.Count) + ".");
            // StructureTreeResource serializes Info.GetType() directly. Its XML
            // element may be Info, CustomInfo, MkIpInfo or another declared name.
            // The runtime ClassType field, not the element name, defines its role.
            return payload;
        }

        public static Dictionary<string, object>[] Read(XmlDocument document, Dictionary<string, object> project)
        {
            if (document == null || document.SelectSingleNode("/Root/Items") == null || document.SelectSingleNode("/Root/Resources") == null)
                throw new InvalidDataException("В BFX отсутствует сериализованное дерево Items/Resources.");
            var resources = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode resource in document.SelectNodes("/Root/Resources/Resource"))
            {
                string id = ParseGuid(Attr(resource, "id"));
                if (id.Length == 0) continue;
                if (resources.ContainsKey(id)) throw new InvalidDataException("В BFX повторяется GUID ресурса.");
                resources[id] = ReadResourcePayload(resource);
            }
            var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            var byNode = new Dictionary<XmlNode, Item>();
            foreach (XmlNode element in document.SelectNodes("/Root/Items//Item"))
            {
                string id = ParseGuid(Attr(element, "id"));
                if (id.Length == 0) throw new InvalidDataException("В дереве BFX обнаружен узел без корректного GUID.");
                if (items.ContainsKey(id)) throw new InvalidDataException("В дереве BFX повторяется GUID узла.");
                XmlNode info; resources.TryGetValue(id, out info);
                var item = new Item { Id = id, Xml = element, Info = info, ClassType = Text(info, "ClassType") };
                Item parent;
                if (element.ParentNode != null && byNode.TryGetValue(element.ParentNode, out parent))
                { item.Parent = parent; parent.Children.Add(item); }
                items.Add(id, item); byNode.Add(element, item);
            }
            var records = (BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>().ToArray();
            var known = new HashSet<string>(records.Select(row => BridgeValues.Text(row, "id")), StringComparer.OrdinalIgnoreCase);
            int missing = known.Count(id => !items.ContainsKey(id));
            if (missing > 0)
                throw new InvalidDataException("Выбранный BFX не содержит все записи открытого проекта (отсутствует: " + missing + "). Каталог не изменён.");
            var owners = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var panels = new List<Dictionary<string, object>>();
            var issues = new List<string>();
            var segments = items.Values.Where(item => item.ClassType == SegmentClass).ToArray();
            int segmentsWithoutController = 0, ambiguousSegments = 0, segmentNumber = 0;
            foreach (var segment in segments)
            {
                segmentNumber++;
                var controllers = segment.Children.Where(item => ControllerClasses.Contains(item.ClassType)).ToArray();
                if (controllers.Length != 1)
                {
                    if (controllers.Length == 0) segmentsWithoutController++; else ambiguousSegments++;
                    issues.Add("Сегмент № " + segmentNumber + ": " + (controllers.Length == 0
                        ? "не найден поддержанный непосредственный контроллер." : "найдено несколько поддержанных контроллеров."));
                    continue;
                }
                var controller = controllers[0];
                string panelId = ProjectPanelCatalog.StableId(controller.Id, segment.Id);
                var membership = items.Values.Where(item => known.Contains(item.Id) && NearestSegment(item) == segment)
                    .Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                string panelName = First(Attr(controller.Xml, "CustomName"), Text(controller.Info, "Name"));
                if (panelName.Length == 0) panelName = First(Attr(segment.Xml, "CustomName"), Text(segment.Info, "Name"));
                if (panelName.Length == 0) panelName = "Панель " + First(Attr(controller.Xml, "addres"), controller.Id.Substring(0, 8));
                var panel = new Dictionary<string, object> {
                    { "panelId", panelId }, { "projectPanelId", panelId }, { "nativePanelId", controller.Id }, { "segmentId", segment.Id },
                    { "name", panelName }, { "recordIds", membership }, { "verifiedRecordIds", new object[0] },
                    { "host", "" }, { "port", 0 }, { "moduleId", "" }, { "source", "project" }, { "origin", "bfx" },
                    { "available", false }, { "availableReason", "Панель прочитана из BFX; связь для регистрации не проверена." },
                    { "bfxControllerClass", controller.ClassType }, { "bfxMembershipEvidence", "nested-item-segment-and-controller" }
                };
                bool viaTerminal = IsTrue(Text(segment.Info, "IsViaPanel3DuplicateEnabled"));
                panel["routeKind"] = viaTerminal ? "panel_terminal" : "direct_mk_ip";
                panel["viaPanelTerminal"] = viaTerminal;
                panel["terminalPanelId"] = ""; panel["terminalSegmentId"] = ""; panel["terminalDisplayName"] = "";
                panel["adapterNodeId"] = ""; panel["adapterSegmentId"] = "";
                panel["routeResolved"] = false;
                panel["routeDisplayName"] = viaTerminal ? "Через Панель-Терминал; маршрут не определён" : "Прямое подключение МК-IP";
                Item adapterSegment = segment;
                if (viaTerminal)
                {
                    string routeIssue;
                    Item terminal;
                    adapterSegment = ResolveTerminalSegment(segment, controller, segments, out terminal, out routeIssue);
                    if (adapterSegment != null)
                    {
                        panel["terminalPanelId"] = terminal.Id;
                        panel["terminalSegmentId"] = adapterSegment.Id;
                        panel["terminalDisplayName"] = PanelName(terminal, adapterSegment);
                        panel["routeDisplayName"] = "Через " + PanelName(terminal, adapterSegment);
                    }
                    else panel["routeIssue"] = routeIssue;
                }
                Item adapter;
                string adapterId = adapterSegment == null ? "" : ParseGuid(Text(adapterSegment.Info, "LonInterface"));
                if (adapterId.Length > 0 && items.TryGetValue(adapterId, out adapter) && adapter.ClassType == MkIpClass)
                {
                    string host = EthernetAddress(adapter.Info.SelectSingleNode("ConnectionIpAddress"));
                    if (host.Length == 0) host = EthernetAddress(adapter.Info.SelectSingleNode("EthernetIpAddress"));
                    int port = Number(Text(adapter.Info, "ConnectionPort"));
                    if (port == 0)
                    {
                        int ethernetPort = Number(Text(adapter.Info, "EthernetConnectionPort"));
                        port = ethernetPort > 0 ? ethernetPort : 6344;
                    }
                    string hardware = DecodeHardwareId(Text(adapter.Info, "ID"));
                    panel["adapterNodeId"] = adapter.Id;
                    panel["adapterSegmentId"] = adapterSegment.Id;
                    panel["host"] = host;
                    panel["port"] = port > 0 && port <= 65535 ? port : 0;
                    panel["moduleId"] = hardware;
                    bool resolved = host.Length > 0 && port > 0 && port <= 65535 && hardware.Length > 0;
                    panel["routeResolved"] = resolved;
                    if (!resolved) panel["routeIssue"] = "Маршрут МК-IP в файле заполнен не полностью.";
                }
                else if (!panel.ContainsKey("routeIssue")) panel["routeIssue"] = viaTerminal
                    ? "В сегменте парной Панели-Терминала не задан адаптер МК-IP."
                    : "Прямое подключение МК-IP в этом сегменте не задано.";
                if (panel.ContainsKey("routeIssue")) panel["availableReason"] = "Панель прочитана из BFX. " + BridgeValues.Text(panel, "routeIssue");
                panels.Add(panel);
                foreach (string record in membership) owners[record] = panel;
            }
            string[] unresolved = known.Where(id => !owners.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (panels.Count == 0)
            {
                string reason = segments.Length == 0 ? "Не найден сегмент поддержанного типа."
                    : ambiguousSegments > 0 ? "Нет сегмента с единственным поддержанным контроллером."
                    : "В распознанных сегментах не найден поддержанный непосредственный контроллер.";
                throw new InvalidDataException("Панели из BFX не прочитаны. " + reason
                    + " Узлов: " + items.Count + "; ресурсов: " + resources.Count + "; сегментов: " + segments.Length
                    + "; поддержанных контроллеров: " + items.Values.Count(item => ControllerClasses.Contains(item.ClassType))
                    + "; сегментов без контроллера: " + segmentsWithoutController + "; неоднозначных сегментов: " + ambiguousSegments
                    + "; записей текущего проекта: " + known.Count + "; с подтверждённой принадлежностью: " + owners.Count + ".");
            }
            project["panelCatalogOrigin"] = "bfx";
            project["panelCatalogIssues"] = issues.ToArray();
            project["unresolvedPanelRecordIds"] = unresolved;
            ProjectPanelCatalog.Apply(project, panels.ToArray());
            project["panelCatalogComplete"] = unresolved.Length == 0 && issues.Count == 0;
            return panels.ToArray();
        }

        private static Item NearestSegment(Item item)
        {
            for (var parent = item; parent != null; parent = parent.Parent)
            {
                if (parent.ClassType == SegmentClass) return parent;
                // An unknown node exposing a segment adapter reference is not
                // assigned to an outer segment merely because its type is new.
                if (parent.Info != null && parent.Info.SelectSingleNode("LonInterface") != null && parent.ClassType != MkIpClass && !ControllerClasses.Contains(parent.ClassType))
                    return null;
            }
            return null;
        }

        // Verified against Master 12.4 SegmentControlPanel3TreeNodeWithCounter.
        // GetDuplicatePair() resolves Number + 30 within the SAME system and
        // requires PanelMode.Duplicate. Names/order/NumberMainSegment are not a
        // substitute for that relationship, and duplicates must stay unresolved.
        private static Item ResolveTerminalSegment(Item segment, Item controller, Item[] segments,
            out Item terminal, out string issue)
        {
            terminal = null;
            issue = "Не найдена однозначная пара основной панели и Панели-Терминала в той же системе.";
            int number = Number(Text(segment.Info, "Number"));
            if (controller.ClassType != "RROP_LON.Panel_3.Info" || Text(controller.Info, "PanelMode") != "Main"
                || number < 1 || number > 30) return null;
            var sameSystem = segments.Where(item => Object.ReferenceEquals(item.Xml.ParentNode, segment.Xml.ParentNode)).ToArray();
            if (sameSystem.Count(item => Number(Text(item.Info, "Number")) == number) != 1) return null;
            var paired = sameSystem.Where(item => Number(Text(item.Info, "Number")) == number + 30).ToArray();
            if (paired.Length != 1 || IsTrue(Text(paired[0].Info, "IsViaPanel3DuplicateEnabled"))) return null;
            var controllers = paired[0].Children.Where(item => ControllerClasses.Contains(item.ClassType)).ToArray();
            if (controllers.Length != 1 || controllers[0].ClassType != "RROP_LON.Panel_3.Info"
                || Text(controllers[0].Info, "PanelMode") != "Duplicate") return null;
            terminal = controllers[0]; issue = "";
            return paired[0];
        }

        private static string PanelName(Item controller, Item segment)
        {
            string name = First(Attr(controller.Xml, "CustomName"), Text(controller.Info, "Name"));
            if (name.Length == 0) name = First(Attr(segment.Xml, "CustomName"), Text(segment.Info, "Name"));
            return name.Length > 0 ? name : "Панель-Терминал " + Text(segment.Info, "Number");
        }

        private static bool IsTrue(string value) { return value == "true" || value == "1"; }

        private static string EthernetAddress(XmlNode value)
        {
            if (value == null) return "";
            var octets = new int[4];
            for (int i = 0; i < octets.Length; i++)
            {
                int number;
                if (!Int32.TryParse(Text(value, "A" + (i + 1)), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 0 || number > 255)
                    return "";
                octets[i] = number;
            }
            string address = String.Join(".", octets.Select(valuePart => valuePart.ToString(CultureInfo.InvariantCulture)));
            return address == "0.0.0.0" || address == "255.255.255.255" ? "" : address;
        }

        private static string DecodeHardwareId(string value)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                if (bytes.Length != 6) return "";
                string id = BitConverter.ToString(bytes).Replace("-", "");
                return id == "000000000000" || id == "FFFFFFFFFFFF" ? "" : id;
            }
            catch (FormatException) { return ""; }
        }

        private static string ParseGuid(string value)
        { Guid guid; return Guid.TryParse(value, out guid) && guid != Guid.Empty ? guid.ToString("D") : ""; }
        private static int Number(string value)
        { int number; return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0; }
        private static string Attr(XmlNode node, string name)
        { return node == null || node.Attributes[name] == null ? "" : node.Attributes[name].Value; }
        private static string Text(XmlNode node, string name)
        { XmlNode child = node == null ? null : node.SelectSingleNode(name); return child == null ? "" : child.InnerText.Trim(); }
        private static string First(string value, string fallback) { return String.IsNullOrEmpty(value) ? fallback : value; }
    }
}
