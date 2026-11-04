// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;

namespace Streletz.MasterBridge
{
    // Pure identity checks shared by discovery and confirmation. The terminal
    // relationship is read from Master's GetDuplicatePair, never from a label.
    internal static class NativeRoutePolicy
    {
        internal const string Direct = "direct_mk_ip";
        internal const string Terminal = "panel_terminal";

        internal static bool SameGuid(string left, string right)
        {
            Guid a, b;
            return Guid.TryParse(left, out a) && a != Guid.Empty &&
                Guid.TryParse(right, out b) && a == b;
        }

        internal static bool RequestMatches(PanelRequest wanted, string kind,
            string terminalPanel, string terminalSegment, string adapterNode, string adapterSegment)
        {
            if (wanted == null) return false;
            if (kind == Terminal)
                return wanted.routeKind == Terminal && wanted.viaPanelTerminal &&
                    SameGuid(wanted.terminalPanelId, terminalPanel) &&
                    SameGuid(wanted.terminalSegmentId, terminalSegment) &&
                    SameGuid(wanted.adapterNodeId, adapterNode) &&
                    SameGuid(wanted.adapterSegmentId, adapterSegment);
            if (kind != Direct || wanted.viaPanelTerminal ||
                !String.IsNullOrWhiteSpace(wanted.terminalPanelId) ||
                !String.IsNullOrWhiteSpace(wanted.terminalSegmentId)) return false;
            // Old clients can still use their original direct-only contract.
            if (String.IsNullOrWhiteSpace(wanted.routeKind))
                return String.IsNullOrWhiteSpace(wanted.adapterNodeId) &&
                    String.IsNullOrWhiteSpace(wanted.adapterSegmentId);
            return wanted.routeKind == Direct && SameGuid(wanted.adapterNodeId, adapterNode) &&
                SameGuid(wanted.adapterSegmentId, adapterSegment);
        }

        internal static bool TerminalPairMatches(string mainSystem, string terminalSystem,
            int mainNumber, int terminalNumber, string mainMode, string terminalMode,
            bool reciprocal, bool uniqueSegments)
        {
            // This is the exact numbering relationship used by the inspected
            // RROP_LON.tree.SegmentControlPanel3TreeNodeWithCounter, within one system.
            return SameGuid(mainSystem, terminalSystem) && mainNumber >= 1 && mainNumber <= 30 &&
                terminalNumber == mainNumber + 30 && mainMode == "Main" && terminalMode == "Duplicate" &&
                reciprocal && uniqueSegments;
        }

        internal static bool TunnelMatches(string actualStartSegment, string actualEndSegment,
            string actualStartMmpi, string actualEndMmpi, string expectedStartSegment,
            string expectedEndSegment, string expectedStartMmpi, string expectedEndMmpi)
        {
            return SameGuid(actualStartSegment, expectedStartSegment) &&
                SameGuid(actualEndSegment, expectedEndSegment) &&
                !String.IsNullOrWhiteSpace(expectedStartMmpi) &&
                !String.IsNullOrWhiteSpace(expectedEndMmpi) &&
                String.Equals(actualStartMmpi, expectedStartMmpi, StringComparison.Ordinal) &&
                String.Equals(actualEndMmpi, expectedEndMmpi, StringComparison.Ordinal);
        }
    }
}
