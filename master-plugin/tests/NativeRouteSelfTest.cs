// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Streletz.MasterBridge;

internal static class NativeRouteSelfTest
{
    private const string MainSegment = "10000000-0000-4000-8000-000000000001";
    private const string Terminal = "20000000-0000-4000-8000-000000000032";
    private const string TerminalPanel = "30000000-0000-4000-8000-000000000032";
    private const string Adapter = "40000000-0000-4000-8000-000000000032";
    private const string SystemId = "50000000-0000-4000-8000-000000000001";
    private const string Other = "60000000-0000-4000-8000-000000000001";
    private static int checks;

    private static int MainMethod()
    {
        try
        {
            var legacy = new PanelRequest();
            Check(Matches(legacy, false), "A legacy direct-only request remains valid.");
            Check(!Matches(legacy, true), "A legacy request cannot authorize a terminal route.");
            var direct = Request(false);
            Check(Matches(direct, false), "The current direct route is bound to its adapter GUIDs.");
            direct.adapterNodeId = Other;
            Check(!Matches(direct, false), "Changing the physical adapter is rejected even at the same host.");
            direct = Request(false); direct.adapterSegmentId = Other;
            Check(!Matches(direct, false), "Changing the adapter segment is rejected.");
            direct = Request(false); direct.terminalPanelId = TerminalPanel;
            Check(!Matches(direct, false), "Terminal metadata cannot be attached to a direct route.");
            direct = Request(false); direct.viaPanelTerminal = true;
            Check(!Matches(direct, false), "The explicit terminal switch cannot masquerade as direct.");
            var terminal = Request(true);
            Check(Matches(terminal, true), "A terminal request binds both segments and both physical nodes.");
            terminal.routeKind = NativeRoutePolicy.Direct;
            Check(!Matches(terminal, true), "A changed route kind is rejected.");
            terminal = Request(true); terminal.viaPanelTerminal = false;
            Check(!Matches(terminal, true), "A missing terminal flag is rejected.");
            foreach (string key in new[] { "terminalPanelId", "terminalSegmentId", "adapterNodeId", "adapterSegmentId" })
            {
                terminal = Request(true);
                typeof(PanelRequest).GetProperty(key).SetValue(terminal, Other, null);
                Check(!Matches(terminal, true), "Changed " + key + " is rejected.");
                typeof(PanelRequest).GetProperty(key).SetValue(terminal, "", null);
                Check(!Matches(terminal, true), "Missing " + key + " is rejected.");
            }
            Check(Pair(2,32), "Main segment 2 has terminal segment 32.");
            Check(Pair(3,33), "A second panel resolves its own terminal 33.");
            Check(!Pair(2,33), "The first or another visible terminal cannot be substituted.");
            Check(!NativeRoutePolicy.TerminalPairMatches(SystemId,Other,2,32,"Main","Duplicate",true,true), "Numbers from another system cannot establish a pair.");
            Check(!NativeRoutePolicy.TerminalPairMatches(SystemId,SystemId,2,32,"Main","Main",true,true), "A Main panel at address 32 is not a terminal.");
            Check(!NativeRoutePolicy.TerminalPairMatches(SystemId,SystemId,2,32,"Main","Duplicate",false,true), "The native pair must point back to the same main panel.");
            Check(!NativeRoutePolicy.TerminalPairMatches(SystemId,SystemId,2,32,"Main","Duplicate",true,false), "Duplicate segment numbers are ambiguous.");
            Check(!Pair(0,30) && !Pair(31,61), "Main panel numbers outside 1 to 30 are rejected.");
            Check(Tunnel(Terminal,MainSegment,"12.32.2","12.2.4"), "The actual tunnel has the expected start and target.");
            Check(!Tunnel(MainSegment,Terminal,"12.32.2","12.2.4"), "Reversed segment GUIDs are rejected.");
            Check(!Tunnel(Terminal,Other,"12.32.2","12.2.4"), "A route to another main segment is rejected.");
            Check(!Tunnel(Terminal,MainSegment,"12.33.2","12.2.4"), "A different source MMPI is rejected.");
            Check(!Tunnel(Terminal,MainSegment,"12.32.2","12.2.5"), "A different target MMPI is rejected.");
            Check(!Tunnel(Terminal,MainSegment,"","12.2.4"), "Missing transport evidence is rejected.");
            var operation = new OperationState {
                Request = new StartRequest { operationId = "route-test", expectedHardwareId = "112233445566", deviceKey = "SECRET-DO-NOT-EXPORT", panel = Request(true) },
                RouteKind = NativeRoutePolicy.Terminal, TerminalPanelId = TerminalPanel, TerminalSegmentId = Terminal,
                AdapterNodeId = Adapter, AdapterSegmentId = Terminal,
                TunnelStartMmpiAddress = "PRIVATE-MPI", TunnelEndMmpiAddress = "PRIVATE-MPI"
            };
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(operation.Snapshot());
            var status = serializer.Deserialize<Dictionary<string,object>>(json);
            Check((string)status["routeKind"] == NativeRoutePolicy.Terminal && (bool)status["viaPanelTerminal"], "Status carries the actual checked route kind.");
            Check((string)status["terminalPanelId"] == TerminalPanel && (string)status["terminalSegmentId"] == Terminal,
                "Status carries the native terminal identity for driver result validation.");
            Check((string)status["adapterNodeId"] == Adapter && (string)status["adapterSegmentId"] == Terminal,
                "Status carries the native physical adapter identity.");
            Check(!json.Contains("SECRET-DO-NOT-EXPORT") && !json.Contains("PRIVATE-MPI"), "Status excludes KEY and private transport data.");
            Console.WriteLine("PASS " + checks + " native route assertions; no Master UI, pipe or hardware operations invoked.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
    public static int Main(string[] args) { return MainMethod(); }
    private static bool Pair(int main, int terminal) { return NativeRoutePolicy.TerminalPairMatches(SystemId,SystemId,main,terminal,"Main","Duplicate",true,true); }
    private static bool Tunnel(string start, string end, string from, string to) { return NativeRoutePolicy.TunnelMatches(start,end,from,to,Terminal,MainSegment,"12.32.2","12.2.4"); }
    private static PanelRequest Request(bool terminal) { return new PanelRequest {
        routeKind = terminal ? NativeRoutePolicy.Terminal : NativeRoutePolicy.Direct, viaPanelTerminal = terminal,
        terminalPanelId = terminal ? TerminalPanel : "", terminalSegmentId = terminal ? Terminal : "",
        adapterNodeId = Adapter, adapterSegmentId = terminal ? Terminal : MainSegment
    }; }
    private static bool Matches(PanelRequest request, bool terminal) { return NativeRoutePolicy.RequestMatches(request,
        terminal ? NativeRoutePolicy.Terminal : NativeRoutePolicy.Direct, terminal ? TerminalPanel : "", terminal ? Terminal : "", Adapter, terminal ? Terminal : MainSegment); }
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); checks++; }
}
