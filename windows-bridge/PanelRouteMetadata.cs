// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;

namespace StreletzBridge
{
    internal static class PanelRouteMetadata
    {
        internal const string Direct = "direct_mk_ip";
        internal const string Terminal = "panel_terminal";
        internal static readonly string[] IdentityFields = { "adapterNodeId", "adapterSegmentId", "terminalPanelId", "terminalSegmentId" };

        internal static string Kind(Dictionary<string, object> value)
        {
            string kind = BridgeValues.Text(value, "routeKind");
            return kind.Length == 0 ? Direct : kind;
        }

        internal static void CopyNative(Dictionary<string, object> native, Dictionary<string, object> panel)
        {
            string kind = Kind(native);
            if (kind != Direct && kind != Terminal) throw new InvalidDataException("Неизвестный маршрут панели проекта.");
            panel["routeKind"] = kind;
            panel["viaPanelTerminal"] = kind == Terminal;
            foreach (string key in IdentityFields)
            {
                string value = BridgeValues.Text(native, key);
                panel[key] = value.Length > 0 ? BridgeValues.GuidValue(value, "маршрут панели") : "";
            }
            string segment = BridgeValues.Text(panel, "segmentId");
            string adapterSegment = BridgeValues.Text(panel, "adapterSegmentId");
            if (kind == Terminal)
            {
                if (!BridgeValues.Flag(native, "viaPanelTerminal") || BridgeValues.Text(panel, "terminalPanelId").Length == 0
                    || BridgeValues.Text(panel, "terminalSegmentId").Length == 0 || BridgeValues.Text(panel, "adapterNodeId").Length == 0
                    || !Equal(adapterSegment, BridgeValues.Text(panel, "terminalSegmentId"))
                    || Equal(segment, adapterSegment) || Equal(BridgeValues.Text(panel, "nativePanelId"), BridgeValues.Text(panel, "terminalPanelId")))
                    throw new InvalidDataException("Маршрут через Панель-Терминал не подтверждён проектом Мастера.");
            }
            else if (BridgeValues.Flag(native, "viaPanelTerminal") || BridgeValues.Text(panel, "terminalPanelId").Length > 0
                || BridgeValues.Text(panel, "terminalSegmentId").Length > 0 || (adapterSegment.Length > 0 && !Equal(segment, adapterSegment)))
                throw new InvalidDataException("Прямой маршрут панели содержит противоречивую привязку терминала.");
            string terminalName = BridgeValues.Text(native, "terminalDisplayName").Trim();
            if (terminalName.Length > 160) terminalName = terminalName.Substring(0, 160);
            panel["terminalDisplayName"] = terminalName;
            panel["routeDisplayName"] = kind == Terminal ? "Через " + (terminalName.Length > 0 ? terminalName : "Панель-Терминал") : "Прямое подключение МК-IP";
            panel["routeResolved"] = true;
            panel["routeIssue"] = "";
        }

        internal static bool Matches(PanelProfile profile, Dictionary<string, object> value)
        {
            if (profile == null) return false;
            string kind = String.IsNullOrEmpty(profile.RouteKind) ? Direct : profile.RouteKind;
            if (kind != Kind(value)) return false;
            if (kind == Terminal && (!BridgeValues.Flag(value, "viaPanelTerminal") || profile.Source != "project")) return false;
            if (kind != Direct && kind != Terminal) return false;
            if (kind == Direct && !IsDirect(value)) return false;
            // A legacy explicit profile has no tree identity. It remains usable
            // only for a direct route whose record membership is checked live.
            if (profile.Source != "project") return kind == Direct;
            return Equal(profile.AdapterNodeId, BridgeValues.Text(value, "adapterNodeId"))
                && Equal(profile.AdapterSegmentId, BridgeValues.Text(value, "adapterSegmentId"))
                && Equal(profile.TerminalPanelId, BridgeValues.Text(value, "terminalPanelId"))
                && Equal(profile.TerminalSegmentId, BridgeValues.Text(value, "terminalSegmentId"));
        }

        internal static bool Same(Dictionary<string, object> expected, Dictionary<string, object> actual)
        {
            if (Kind(expected) != Kind(actual)) return false;
            if (Kind(expected) == Direct && (!IsDirect(expected) || !IsDirect(actual))) return false;
            if (Kind(expected) != Direct && Kind(expected) != Terminal) return false;
            if (Kind(expected) == Terminal && !BridgeValues.Flag(actual, "viaPanelTerminal")) return false;
            foreach (string key in IdentityFields)
            {
                string wanted = BridgeValues.Text(expected, key);
                // Old native plugins omit direct-route metadata entirely.
                if (wanted.Length > 0 && !Equal(wanted, BridgeValues.Text(actual, key))) return false;
                if (Kind(expected) == Terminal && wanted.Length == 0) return false;
            }
            return true;
        }

        internal static void CopyIdentity(Dictionary<string, object> source, Dictionary<string, object> destination)
        {
            destination["routeKind"] = Kind(source);
            destination["viaPanelTerminal"] = Kind(source) == Terminal;
            foreach (string key in IdentityFields) destination[key] = BridgeValues.Text(source, key);
        }

        private static bool Equal(string first, string second)
        { return String.Equals(first ?? "", second ?? "", StringComparison.OrdinalIgnoreCase); }

        private static bool IsDirect(Dictionary<string, object> value)
        {
            return !BridgeValues.Flag(value, "viaPanelTerminal") && BridgeValues.Text(value, "terminalPanelId").Length == 0
                && BridgeValues.Text(value, "terminalSegmentId").Length == 0;
        }
    }
}
