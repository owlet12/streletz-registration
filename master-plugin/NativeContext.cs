// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Streletz.MasterBridge
{
    internal sealed class NativePanel
    {
        public string panelId;
        public string nativeNodeGuid;
        public string moduleId;
        public string host;
        public int port;
        public string displayName;
        public string segmentGuid;
        public string routeKind;
        public bool viaPanelTerminal;
        public string terminalPanelId;
        public string terminalSegmentId;
        public string terminalDisplayName;
        public string adapterNodeId;
        public string adapterSegmentId;
        public List<string> recordIds = new List<string>();
        internal object Node;
        internal object Adapter;
        internal object TerminalNode;
        internal string TunnelStartMmpiAddress;
        internal string TunnelEndMmpiAddress;
        public object PublicView() { return new { panelId, nativeNodeGuid, moduleId, host, port, displayName, segmentGuid, recordIds,
            routeKind, viaPanelTerminal, terminalPanelId, terminalSegmentId, terminalDisplayName, adapterNodeId, adapterSegmentId }; }
    }

    internal sealed class NativeSnapshot
    {
        public string Fingerprint;
        public object Root;
        public readonly List<NativePanel> Panels = new List<NativePanel>();
        public readonly Dictionary<string, object> Records = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> HardwareOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    internal sealed class NativeContext
    {
        private object Manager
        {
            get
            {
                Type type = LoadedType("Engine.ApplicationManager", "Engine");
                return type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            }
        }

        internal static Type LoadedType(string fullName, string assemblyName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == assemblyName).ToArray();
            if (assemblies.Length != 1) throw new BridgeException("native_component_unavailable", "The required native component is not uniquely loaded.");
            Type type = assemblies[0].GetType(fullName, false);
            if (type == null) throw new BridgeException("native_component_unavailable", "The required native type is unavailable.");
            return type;
        }

        public object GetProperty(object instance, string name)
        {
            if (instance == null) return null;
            PropertyInfo p = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead) return p.GetValue(instance, null);
            // IUniqueDevice.ID is implemented explicitly by several vendor device types.
            foreach (Type contract in instance.GetType().GetInterfaces())
            {
                p = contract.GetProperty(name);
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead) return p.GetValue(instance, null);
            }
            return null;
        }

        public object GetField(object instance, int token)
        {
            if (instance == null) throw new BridgeException("native_dialog_mismatch", "Native dialog is unavailable.");
            FieldInfo field = instance.GetType().Module.ResolveField(token);
            if (!field.DeclaringType.IsInstanceOfType(instance))
                throw new BridgeException("native_dialog_mismatch", "Native field belongs to a different dialog type.");
            return field.GetValue(instance);
        }

        public object Call(object instance, string name, params object[] args)
        {
            if (instance == null) throw new BridgeException("native_context_unavailable", "Native application context is not ready.");
            MethodInfo[] choices = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition && Matches(m.GetParameters(), args)).ToArray();
            if (choices.Length != 1) throw new BridgeException("native_api_mismatch", "Native method signature is not unique: " + name);
            return choices[0].Invoke(instance, args);
        }

        private static bool Matches(ParameterInfo[] p, object[] args)
        {
            if (p.Length != args.Length) return false;
            for (int i = 0; i < p.Length; i++)
                if (args[i] == null ? p[i].ParameterType.IsValueType : !p[i].ParameterType.IsInstanceOfType(args[i])) return false;
            return true;
        }

        public string GuidOf(object node)
        {
            object value = GetProperty(node, "Guid");
            return value is Guid ? ((Guid)value).ToString("D") : String.Empty;
        }

        public string GetHardwareId(object node)
        {
            return Hex.Bytes(GetProperty(GetProperty(node, "Info"), "ID") as byte[]);
        }

        public string GetKey(object node)
        {
            return Hex.Bytes(GetProperty(GetProperty(node, "Info"), "Key") as byte[]);
        }

        private static string AsString(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty; }
        private static int AsInt(object value) { return value == null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }

        internal void EnsureAuthenticated()
        {
            Type type = LoadedType("Argus.Configuration.Configuration", "Argus.Configuration");
            object configuration = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (GetProperty(configuration, "CurrentUser") == null)
                throw new BridgeException("master_login_required", "Open the native project and sign in to Master before registration.");
            object manager = Manager;
            object programmer = Call(manager, "GetProgrammer");
            object formManager = Call(manager, "GetFormManager");
            if (Call(manager, "GetDataController") == null || programmer == null || formManager == null)
                throw new BridgeException("native_context_unavailable", "Native project context is not ready.");
            // These are the concrete implementations registered by Master.ApplicationInitializer.
            // A remote command executor or replacement programmer has a different completion contract.
            if (programmer.GetType() != LoadedType("Argus.GUI.Common.Programmers.MasterProgrammer", "Argus.GUI.Common") ||
                formManager.GetType() != LoadedType("Argus.GUI.Common.Forms.FormManager.FormManager", "Argus.GUI.Common") ||
                GetProperty(manager, "CommandExecutorChannel") != null)
                throw new BridgeException("native_execution_context_unsupported", "The inspected local Master UI command context is required.");
        }

        internal NativeSnapshot Snapshot(OperationState registration = null)
        {
            EnsureAuthenticated();
            object manager = Manager;
            object dc = Call(manager, "GetDataController");
            object root = Call(dc, "GetRootNode", this);
            if (root == null) throw new BridgeException("master_project_required", "Open the native project in Master.");
            var snapshot = new NativeSnapshot { Root = root };
            var nodes = new Stack<object>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identity = new List<string>();
            nodes.Push(root);
            while (nodes.Count != 0)
            {
                object node = nodes.Pop();
                string guid = GuidOf(node);
                if (guid.Length == 0 || !visited.Add(guid)) continue;
                if (visited.Count > 100000) throw new BridgeException("project_too_large", "Project exceeds the inspected traversal limit.");
                object children = GetProperty(node, "ChildNodes");
                if (children is IEnumerable)
                    foreach (object child in (IEnumerable)children) if (child != null) nodes.Push(child);
                string hardwareId = GetHardwareId(node);
                if (Hex.IsDeviceId(hardwareId) && hardwareId != "000000000000" && hardwareId != "FFFFFFFFFFFF")
                {
                    List<string> owners;
                    if (!snapshot.HardwareOwners.TryGetValue(hardwareId, out owners))
                        snapshot.HardwareOwners.Add(hardwareId, owners = new List<string>());
                    owners.Add(guid);
                }
                int type = AsInt(GetProperty(node, "InfoType"));
                int subtype = AsInt(GetProperty(node, "InfoSubType"));
                bool acceptedTargetMutation = registration != null && registration.ConfirmationSent &&
                    registration.Request.recordId == guid && Object.ReferenceEquals(registration.TargetNode, node);
                if (type != 128) continue;
                object info = GetProperty(node, "Info");
                if (!NativeDeviceCatalog.IsSupported(type, subtype, info,
                    () => LoadedType("StreletcPro.IB1.Info", "Plugin.StreletcPro")) && !acceptedTargetMutation) continue;
                string segmentGuid = AsString(GetProperty(node, "SegmentGuid"));
                Guid segment;
                if (!Guid.TryParse(segmentGuid, out segment) || segment == Guid.Empty) continue;
                object controller = Call(dc, "GetSegmentControllerNode", Guid.Parse(guid));
                if (controller == null) continue;
                string panelGuid = GuidOf(controller);
                NativePanel panel = snapshot.Panels.SingleOrDefault(p => p.nativeNodeGuid == panelGuid && p.segmentGuid == segment.ToString("D"));
                if (panel == null)
                {
                    panel = ReadPanelRoute(manager, dc, controller, segment);
                    if (panel == null) continue;
                    snapshot.Panels.Add(panel);
                }
                panel.recordIds.Add(guid);
                snapshot.Records.Add(guid, node);
                // Native InitDu may replace the selected record's compatible hardware subtype.
                // Only that field on that exact node is normalized after confirmation starts.
                // Parent/number/segment/controller/MK-IP route and every other record stay strict.
                int identitySubtype = acceptedTargetMutation ? registration.InitialInfoSubtype : subtype;
                identity.Add(String.Join("|", guid, AsString(GetProperty(node, "ParentGuid")), type, identitySubtype,
                    AsInt(GetProperty(info, "Number")), panelGuid, segmentGuid, panel.moduleId, panel.host, panel.port,
                    panel.routeKind, panel.terminalPanelId, panel.terminalSegmentId, panel.adapterNodeId, panel.adapterSegmentId,
                    panel.TunnelStartMmpiAddress, panel.TunnelEndMmpiAddress));
            }
            identity.Sort(StringComparer.Ordinal);
            string canonical = "native-project-v1\n" + GuidOf(root) + "\n" + String.Join("\n", identity);
            using (var hash = SHA256.Create()) snapshot.Fingerprint = Hex.Bytes(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            foreach (NativePanel panel in snapshot.Panels) panel.recordIds.Sort(StringComparer.Ordinal);
            return snapshot;
        }

        private NativePanel ReadPanelRoute(object manager, object dc, object controller, Guid segment)
        {
            object segmentNode = Call(dc, "GetNode", this, segment);
            object segmentInfo = GetProperty(segmentNode, "Info");
            if (segmentInfo == null || segmentInfo.GetType().FullName != "CorePlugins.segment.CustomInfo") return null;
            bool viaTerminal = Object.Equals(GetProperty(segmentInfo, "IsViaPanel3DuplicateEnabled"), true);
            object connectionSegment = segmentNode;
            object terminal = null;
            string terminalId = String.Empty, terminalSegment = String.Empty, startMmpi = String.Empty, endMmpi = String.Empty;
            if (viaTerminal)
            {
                Type panelType = LoadedType("Core.tree.SegmentControlPanel3TreeNode", "Core");
                if (!panelType.IsInstanceOfType(controller)) return null;
                terminal = Call(controller, "GetDuplicatePair");
                if (terminal == null || !panelType.IsInstanceOfType(terminal)) return null;
                terminalId = GuidOf(terminal);
                terminalSegment = AsString(GetProperty(terminal, "SegmentGuid"));
                Guid terminalGuid;
                if (!Guid.TryParse(terminalSegment, out terminalGuid) || terminalGuid == Guid.Empty) return null;
                connectionSegment = Call(dc, "GetNode", this, terminalGuid);
                object terminalInfo = GetProperty(connectionSegment, "Info");
                if (terminalInfo == null || terminalInfo.GetType().FullName != "CorePlugins.segment.CustomInfo" ||
                    Object.Equals(GetProperty(terminalInfo, "IsViaPanel3DuplicateEnabled"), true)) return null;
                // This overload explicitly resolves the segment GUID itself;
                // the one-argument overload first asks an arbitrary node for its Segment.
                if (!Object.ReferenceEquals(Call(dc, "GetSegmentControllerNode", this, terminalGuid), terminal)) return null;
                bool unique = HasUniqueSiblingSegmentNumber(segmentNode) && HasUniqueSiblingSegmentNumber(connectionSegment);
                if (!NativeRoutePolicy.TerminalPairMatches(AsString(GetProperty(segmentNode, "ParentGuid")),
                    AsString(GetProperty(connectionSegment, "ParentGuid")), AsInt(GetProperty(segmentInfo, "Number")),
                    AsInt(GetProperty(terminalInfo, "Number")), AsString(Call(controller, "GetPanelMode")),
                    AsString(Call(terminal, "GetPanelMode")), Object.ReferenceEquals(Call(terminal, "GetDuplicatePair"), controller), unique)) return null;
                startMmpi = AsString(Call(connectionSegment, "GetMmpiAddress"));
                endMmpi = AsString(Call(segmentNode, "GetMmpiAddress"));
                if (String.IsNullOrWhiteSpace(startMmpi) || String.IsNullOrWhiteSpace(endMmpi)) return null;
            }
            string connectionSegmentId = GuidOf(connectionSegment);
            Guid connectionGuid;
            if (!Guid.TryParse(connectionSegmentId, out connectionGuid) || connectionGuid == Guid.Empty) return null;
            object adapter = Call(manager, "GetAdapterNode", connectionGuid);
            object adapterInfo = GetProperty(adapter, "Info");
            if (adapterInfo == null || adapterInfo.GetType().FullName != "MostIPI.Similar.MK_IP.MkIpInfo") return null;
            string adapterSegment = AsString(GetProperty(adapter, "SegmentGuid"));
            // The physical MK-IP must belong to the selected connection segment.
            if (!NativeRoutePolicy.SameGuid(adapterSegment, connectionSegmentId)) return null;
            string moduleId = GetHardwareId(adapter);
            string host = NormalizeHost(AsString(GetProperty(adapterInfo, "ConnectionIpAddr")));
            int port = AsInt(GetProperty(adapterInfo, "ConnectionIpPort"));
            if (!Hex.IsDeviceId(moduleId) || host.Length == 0 || port < 1 || port > 65535) return null;
            string panelGuid = GuidOf(controller), adapterId = GuidOf(adapter);
            Guid parsed;
            if (!Guid.TryParse(panelGuid, out parsed) || parsed == Guid.Empty ||
                !Guid.TryParse(adapterId, out parsed) || parsed == Guid.Empty) return null;
            return new NativePanel {
                panelId = panelGuid, nativeNodeGuid = panelGuid, segmentGuid = segment.ToString("D"),
                moduleId = moduleId, host = host, port = port, displayName = DisplayName(controller),
                routeKind = viaTerminal ? NativeRoutePolicy.Terminal : NativeRoutePolicy.Direct, viaPanelTerminal = viaTerminal,
                terminalPanelId = terminalId, terminalSegmentId = terminalSegment, terminalDisplayName = DisplayName(terminal),
                adapterNodeId = adapterId, adapterSegmentId = adapterSegment,
                Node = controller, Adapter = adapter, TerminalNode = terminal,
                TunnelStartMmpiAddress = startMmpi, TunnelEndMmpiAddress = endMmpi
            };
        }

        private bool HasUniqueSiblingSegmentNumber(object segment)
        {
            object parent = GetProperty(segment, "ParentNode");
            IEnumerable children = GetProperty(parent, "ChildNodes") as IEnumerable;
            if (children == null) return false;
            int number = AsInt(GetProperty(GetProperty(segment, "Info"), "Number"));
            var matching = new List<object>();
            foreach (object child in children)
            {
                object info = GetProperty(child, "Info");
                if (info != null && info.GetType().FullName == "CorePlugins.segment.CustomInfo" &&
                    AsInt(GetProperty(info, "Number")) == number) matching.Add(child);
            }
            return matching.Count == 1 && Object.ReferenceEquals(matching[0], segment);
        }

        private string DisplayName(object node)
        {
            if (node == null) return String.Empty;
            string text = AsString(GetProperty(node, "Text")).Trim();
            if (text.Length == 0) text = AsString(GetProperty(node, "CustomName")).Trim();
            if (text.Length == 0) text = AsString(GetProperty(GetProperty(node, "Info"), "Name")).Trim();
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        private static string NormalizeHost(string input)
        {
            IPAddress ip;
            if (!IPAddress.TryParse((input ?? String.Empty).Trim(), out ip)) return String.Empty;
            return ip.ToString().ToLowerInvariant();
        }

        internal OperationState Prepare(StartRequest request)
        {
            Guid record;
            if (request == null || !Guid.TryParse(request.recordId, out record) || record == Guid.Empty)
                throw new BridgeException("record_id_invalid", "A native project record GUID is required.");
            if (String.IsNullOrWhiteSpace(request.operationId) || !Regex.IsMatch(request.operationId, "\\A[A-Za-z0-9._-]{1,128}\\z"))
                throw new BridgeException("operation_id_invalid", "A valid operation ID is required.");
            request.expectedHardwareId = Hex.Normalize(request.expectedHardwareId);
            if (!Hex.IsDeviceId(request.expectedHardwareId)) throw new BridgeException("hardware_id_invalid", "A 12-character hexadecimal hardware ID is required.");
            request.deviceKey = Hex.Normalize(request.deviceKey);
            if (request.deviceKey.Length != 0 && !Regex.IsMatch(request.deviceKey, "\\A[0-9A-F]{8}\\z"))
                throw new BridgeException("device_key_invalid", "Device KEY must contain eight hexadecimal characters or be empty.");
            request.recordId = record.ToString("D");
            if (request.timeoutSeconds == 0) request.timeoutSeconds = 120;
            if (request.timeoutSeconds < 15 || request.timeoutSeconds > 300)
                throw new BridgeException("timeout_invalid", "Registration timeout must be between 15 and 300 seconds.");
            NativeSnapshot snapshot = Snapshot();
            object target;
            if (!snapshot.Records.TryGetValue(request.recordId, out target))
                throw new BridgeException("record_not_available", "The record is not in the active project on a verified direct or panel-terminal MK-IP route.");
            List<string> existingOwners;
            if (snapshot.HardwareOwners.TryGetValue(request.expectedHardwareId, out existingOwners) && existingOwners.Any(g => g != request.recordId))
                throw new BridgeException("hardware_id_already_assigned", "The requested hardware ID belongs to another record in the active native project.");
            NativePanel panel = snapshot.Panels.SingleOrDefault(p => p.recordIds.Contains(request.recordId));
            if (panel == null || !PanelMatches(request.panel, panel))
                throw new BridgeException("panel_mismatch", "The requested panel does not match the record's configured native MK-IP route.");
            if (!String.IsNullOrWhiteSpace(request.nativeProjectFingerprint) && !String.Equals(snapshot.Fingerprint, request.nativeProjectFingerprint, StringComparison.Ordinal))
                throw new BridgeException("native_project_changed", "The active native project changed after discovery.");
            string previousId = GetHardwareId(target);
            if (previousId.Length != 0 && previousId != "000000000000" && previousId != "FFFFFFFFFFFF" && previousId != request.expectedHardwareId)
                throw new BridgeException("record_already_assigned", "The selected record already belongs to a different hardware ID.");
            string previousKey = GetKey(target);
            if (AsInt(GetProperty(GetProperty(target, "Info"), "InitMode")) == 1 && request.deviceKey.Length == 0 &&
                (previousKey.Length == 0 || previousKey.All(c => c == '0')))
                throw new BridgeException("device_key_required", "The native initialization mode requires the device KEY before opening registration.");
            return new OperationState {
                Request = request, TargetNode = target, PanelNode = panel.Node, AdapterNode = panel.Adapter,
                TerminalNode = panel.TerminalNode, RouteKind = panel.routeKind,
                TerminalPanelId = panel.terminalPanelId, TerminalSegmentId = panel.terminalSegmentId,
                AdapterNodeId = panel.adapterNodeId, AdapterSegmentId = panel.adapterSegmentId,
                TunnelStartMmpiAddress = panel.TunnelStartMmpiAddress, TunnelEndMmpiAddress = panel.TunnelEndMmpiAddress,
                NativeProjectFingerprint = snapshot.Fingerprint,
                ValidationMode = String.IsNullOrWhiteSpace(request.nativeProjectFingerprint) ? "record-and-panel" : "native-fingerprint-record-and-panel",
                InitialHardwareId = previousId, InitialKey = previousKey, KeyWasSupplied = request.deviceKey.Length > 0,
                InitialInfoSubtype = AsInt(GetProperty(target, "InfoSubType"))
            };
        }

        private static bool PanelMatches(PanelRequest wanted, NativePanel actual)
        {
            if (wanted == null || NormalizeHost(wanted.host) != actual.host || wanted.port != actual.port || Hex.Normalize(wanted.moduleId) != actual.moduleId) return false;
            if (!NativeRoutePolicy.RequestMatches(wanted, actual.routeKind, actual.terminalPanelId,
                actual.terminalSegmentId, actual.adapterNodeId, actual.adapterSegmentId)) return false;
            if (actual.viaPanelTerminal && !NativeRoutePolicy.SameGuid(wanted.nativePanelId, actual.nativeNodeGuid)) return false;
            if (!String.IsNullOrWhiteSpace(wanted.nativePanelId))
            {
                Guid id;
                if (!Guid.TryParse(wanted.nativePanelId, out id) || id.ToString("D") != actual.nativeNodeGuid) return false;
            }
            // wanted.panelId is a bridge profile ID, never a native panel GUID.
            return true;
        }

        public bool VerifyTargetStillMatches(OperationState op)
        {
            try
            {
                NativeSnapshot snapshot = Snapshot(op);
                object node;
                if (snapshot.Fingerprint != op.NativeProjectFingerprint || !snapshot.Records.TryGetValue(op.Request.recordId, out node)) return false;
                if (!Object.ReferenceEquals(node, op.TargetNode)) return false;
                List<string> owners;
                if (snapshot.HardwareOwners.TryGetValue(op.Request.expectedHardwareId, out owners) && owners.Any(g => g != op.Request.recordId)) return false;
                NativePanel panel = snapshot.Panels.SingleOrDefault(p => p.recordIds.Contains(op.Request.recordId));
                return panel != null && Object.ReferenceEquals(panel.Node, op.PanelNode) && Object.ReferenceEquals(panel.Adapter, op.AdapterNode) &&
                    Object.ReferenceEquals(panel.TerminalNode, op.TerminalNode) && panel.routeKind == op.RouteKind &&
                    panel.TunnelStartMmpiAddress == op.TunnelStartMmpiAddress && panel.TunnelEndMmpiAddress == op.TunnelEndMmpiAddress &&
                    PanelMatches(op.Request.panel, panel);
            }
            catch { return false; }
        }

        public bool VerifyDialogAdapter(OperationState op, System.Windows.Forms.Form dialog)
        {
            var diagnostic = new NativeAdapterDiagnostic {
                expectedHost = NativeAdapterDiagnostic.Host(op.Request.panel.host), expectedPort = op.Request.panel.port,
                expectedModuleId = NativeAdapterDiagnostic.Module(op.Request.panel.moduleId),
                expectedAdapterNodeId = NativeAdapterDiagnostic.GuidText(op.AdapterNodeId),
                routeKind = op.RouteKind == NativeRoutePolicy.Terminal ? NativeRoutePolicy.Terminal : NativeRoutePolicy.Direct
            };
            try
            {
                if (dialog == null || dialog.GetType().FullName != "StreletcProGUI.Dialogs.InitDu") return diagnostic.Reject("dialog_type_mismatch");
                if (!VerifyTargetStillMatches(op)) return diagnostic.Reject("target_changed");
                if (!Object.ReferenceEquals(GetField(dialog, 0x04000035), op.TargetNode)) return diagnostic.Reject("dialog_target_mismatch");
                object owner = GetField(dialog, 0x04000036);
                string segment = AsString(GetProperty(op.TargetNode, "SegmentGuid"));
                diagnostic.targetSegmentId = NativeAdapterDiagnostic.GuidText(segment);
                diagnostic.ownerSegmentId = NativeAdapterDiagnostic.GuidText(AsString(GetProperty(owner, "SegmentGuid")));
                if (!NativeRoutePolicy.SameGuid(diagnostic.targetSegmentId, diagnostic.ownerSegmentId))
                    return diagnostic.Reject("owner_segment_mismatch");
                object adapter = GetField(dialog, 0x04000031);
                diagnostic.adapterSegmentId = NativeAdapterDiagnostic.GuidText(AsString(GetProperty(adapter, "SegmentGuid")));
                object physicalAdapter;
                if (!NativeTransportProof.Verify(adapter, LoadedType, diagnostic, out physicalAdapter)) return false;
                if (op.RouteKind == NativeRoutePolicy.Terminal)
                {
                    if (!VerifyTerminalAdapter(op, physicalAdapter, segment, diagnostic)) return false;
                }
                else
                {
                    // SegmentGuid on the vendor physical adapter is an unused mutable
                    // event-routing hint, not the project's segment identity. The
                    // decoded URI owner is the exact configured MK-IP node instead.
                    if (!VerifyConfiguredDirectEndpoint(op,
                        AsString(Call(physicalAdapter, "GetInterfaceName")), diagnostic)) return false;
                }
                diagnostic.check = "verified";
                return true;
            }
            catch { return diagnostic.Reject("adapter_read_failed"); }
            finally { lock (op.Sync) op.AdapterDiagnostic = diagnostic; }
        }

        private bool VerifyTerminalAdapter(OperationState op, object adapter, string targetSegment, NativeAdapterDiagnostic diagnostic)
        {
            // NativeTransportProof already unwrapped the guarded LockedAdapter
            // and proved the actually selected, authenticated TCP transport.
            if (adapter == null || adapter.GetType() != LoadedType("UsbEthernetAdapter.EthernetUSB", "UsbEthernetAdapter"))
                return diagnostic.Reject("adapter_type_mismatch");
            // Token 0x040000DB is the readonly TunnelDto supplied to UsbProxyAdapter
            // by the inspected factory, not an arbitrary URI supplied by a client.
            Type baseType = LoadedType("UsbEthernetAdapter.UsbProxyAdapter", "UsbEthernetAdapter");
            FieldInfo field = baseType.Module.ResolveField(0x040000DB);
            if (field.DeclaringType != baseType || field.IsStatic || !field.IsInitOnly ||
                field.FieldType != LoadedType("Core.Adapters.TunnelDto", "Core")) return diagnostic.Reject("terminal_tunnel_shape");
            object tunnel = field.GetValue(adapter);
            if (tunnel == null || !NativeRoutePolicy.TunnelMatches(
                AsString(GetProperty(tunnel, "StartPointSegment")), AsString(GetProperty(tunnel, "EndPointSegment")),
                AsString(GetProperty(tunnel, "StartPointMmpiAddress")), AsString(GetProperty(tunnel, "EndPointMmpiAddress")),
                op.TerminalSegmentId, targetSegment, op.TunnelStartMmpiAddress, op.TunnelEndMmpiAddress))
                return diagnostic.Reject("terminal_tunnel_mismatch");
            // The two segment GUIDs in the actual native tunnel identify the
            // target and transport separately. A physical adapter's SegmentGuid
            // alone cannot establish the main panel's identity on this route.
            return VerifyConfiguredDirectEndpoint(op,
                    AsString(GetProperty(tunnel, "StartPointAdapterName")), diagnostic) &&
                VerifyTerminalInterface(op, AsString(Call(adapter, "GetInterfaceName")), diagnostic);
        }

        private bool VerifyTerminalInterface(OperationState op, string endpoint, NativeAdapterDiagnostic diagnostic)
        {
            // The unwrapped EthernetUSB returns the PHYSICAL MK-IP Open() URI,
            // not the outer main-panel tunnel URI. The actual readonly TunnelDto
            // above binds both project segments and both MMPI addresses. Retain
            // that proof and independently validate this physical interface.
            try
            {
                Uri uri;
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)) return diagnostic.Reject("terminal_interface_parse_failed");
                Type converterType = LoadedType("Core.Adapters.AdapterUrlConverter", "Core");
                object converter = Activator.CreateInstance(converterType, new object[] { uri });
                diagnostic.expectedRouteOwnerId = NativeAdapterDiagnostic.GuidText(op.AdapterNodeId);
                diagnostic.actualRouteOwnerId = NativeAdapterDiagnostic.GuidText(AsString(GetProperty(converter, "AdapterOwnerGuid")));
                string owner = NativeEndpointPolicy.CheckOwner(diagnostic.expectedRouteOwnerId, diagnostic.actualRouteOwnerId);
                if (owner != "verified") return diagnostic.Reject(owner == "endpoint_owner_missing"
                    ? "terminal_interface_owner_missing" : "terminal_interface_owner_mismatch");
                return VerifyConfiguredDirectEndpoint(op, endpoint, diagnostic);
            }
            catch { return diagnostic.Reject("terminal_interface_parse_failed"); }
        }

        private bool VerifyDirectEndpoint(PanelRequest expected, string expectedAdapterNodeId, string endpoint, NativeAdapterDiagnostic diagnostic)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)) return diagnostic.Reject("endpoint_invalid");
                // Decode the actual opened adapter using the vendor's own URI converter.
                // This reads connection metadata; it does not open another connection.
                Type converterType = LoadedType("Core.Adapters.AdapterUrlConverter", "Core");
                object converter = Activator.CreateInstance(converterType, new object[] { uri });
                object parameters = GetProperty(converter, "ConnectionParams");
                if (parameters == null) return diagnostic.Reject("endpoint_parse_failed");
                diagnostic.expectedAdapterNodeId = NativeAdapterDiagnostic.GuidText(expectedAdapterNodeId);
                diagnostic.actualAdapterOwnerId = NativeAdapterDiagnostic.GuidText(AsString(GetProperty(converter, "AdapterOwnerGuid")));
                string endpointModule = AsString(GetProperty(parameters, "SerialNumber"));
                diagnostic.actualModuleId = NativeAdapterDiagnostic.Module(endpointModule);
                diagnostic.actualModuleIdState = String.IsNullOrEmpty(endpointModule) ? "missing"
                    : diagnostic.actualModuleId.Length == 0 ? "invalid" : "present";
                diagnostic.forceUsbScheme = Object.Equals(GetProperty(parameters, "ForceUsbScheme"), true);
                string reason = NativeEndpointPolicy.Check(expected.host, expected.port, expected.moduleId,
                    AsString(GetProperty(parameters, "ConnectionIpAddr")), AsInt(GetProperty(parameters, "ConnectionIpPort")),
                    endpointModule, GetProperty(parameters, "Tunnel") != null,
                    Object.Equals(GetProperty(converter, "IsDirectConnectionEnabled"), true), diagnostic.forceUsbScheme,
                    diagnostic.expectedAdapterNodeId, diagnostic.actualAdapterOwnerId);
                return reason == "verified" || diagnostic.Reject(reason);
            }
            catch { return diagnostic.Reject("endpoint_parse_failed"); }
        }

        private bool VerifyConfiguredDirectEndpoint(OperationState op, string endpoint, NativeAdapterDiagnostic diagnostic)
        {
            bool completeEndpoint = VerifyDirectEndpoint(op.Request.panel, op.AdapterNodeId, endpoint, diagnostic);
            if (!completeEndpoint && diagnostic.check != "endpoint_module_missing") return false;
            diagnostic.projectModuleId = NativeAdapterDiagnostic.Module(GetHardwareId(op.AdapterNode));
            if (diagnostic.projectModuleId.Length == 0 || diagnostic.projectModuleId != NativeAdapterDiagnostic.Module(op.Request.panel.moduleId))
                return diagnostic.Reject("endpoint_module_mismatch");
            if (completeEndpoint)
            {
                diagnostic.moduleIdentitySource = "endpoint_and_project";
                return true;
            }
            try
            {
                Guid adapterId, segmentId;
                if (!Guid.TryParse(op.AdapterNodeId, out adapterId) || adapterId == Guid.Empty ||
                    !Guid.TryParse(op.AdapterSegmentId, out segmentId) || segmentId == Guid.Empty)
                    return diagnostic.Reject("endpoint_native_route_mismatch");
                object manager = Manager, dc = Call(manager, "GetDataController");
                object resolvedAdapter = Call(dc, "GetNode", this, adapterId);
                object connectionSegment = Call(dc, "GetNode", this, segmentId);
                object selectedAdapter = Call(manager, "GetAdapterNode", segmentId);
                Type converterType = LoadedType("Core.Adapters.AdapterUrlConverter", "Core");
                object converter = Activator.CreateInstance(converterType, new object[] { new Uri(endpoint, UriKind.Absolute) });
                object parameters = GetProperty(converter, "ConnectionParams");
                return NativeProjectRouteProof.Verify(op.AdapterNode, resolvedAdapter, selectedAdapter, connectionSegment,
                    op.AdapterNodeId, op.AdapterSegmentId, op.Request.panel.moduleId, endpoint,
                    AsString(GetProperty(parameters, "SerialNumber")), GetProperty(converter, "ConnectionPasswordMd5Code") as byte[],
                    LoadedType, GetProperty, diagnostic);
            }
            catch { return diagnostic.Reject("endpoint_native_route_mismatch"); }
        }

        internal void RunInitDevice(OperationState op)
        {
            if (!VerifyTargetStillMatches(op)) throw new BridgeException("native_project_changed", "The native project or panel changed before registration.");
            object manager = Manager;
            object formManager = Call(manager, "GetFormManager");
            // Guarded FormManager.InvokeAction (0x06001B08) is synchronous: Action.Invoke
            // on its UI thread, Control.Invoke (0x06001B58) otherwise. It never queues BeginInvoke.
            Call(formManager, "InvokeAction", new Action(() => {
                if (op.CancellationRequested) return;
                if (!VerifyTargetStillMatches(op)) throw new BridgeException("native_project_changed", "The native target changed before registration.");
                Type initType = LoadedType("StreletcProGUI.Commands.InitDevice", "Plugin.StreletcProGUI");
                Guid commandGuid = (Guid)initType.GetField("CommandID", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                object command = Call(manager, "GetCommand", commandGuid);
                if (command == null || command.GetType() != initType) throw new BridgeException("native_command_unavailable", "The registered native initialization command is unavailable.");
                object programmer = Call(manager, "GetProgrammer");
                // The local Programmer keeps this command instance. A remote
                // executor sends only its GUID and would recreate the ordinary
                // command, losing the narrowly scoped result-mode override.
                // Do not change RunType or bypass the configured executor.
                PropertyInfo executor = programmer == null ? null : programmer.GetType().GetProperty("CommandExecutor", BindingFlags.Public | BindingFlags.Instance);
                if (executor == null || !executor.CanRead || executor.GetIndexParameters().Length != 0 ||
                    executor.DeclaringType != LoadedType("Engine.Programmer", "Engine") ||
                    executor.GetValue(programmer, null) != null)
                    throw new BridgeException("native_result_mode_unavailable", "The verified local native command instance cannot be retained by the configured programmer.");
                command = NativeInitDeviceCommand.Create(command, op);
                // A manual modal could have opened after the start request was
                // queued. Do not attach its InitDu to this bridge operation.
                if (Application.OpenForms.Cast<Form>().Any(f => !f.IsDisposed &&
                    (f.GetType().FullName == "StreletcProGUI.Dialogs.InitDu" || (f.Visible && f.Modal))))
                    throw new BridgeException("native_dialog_busy", "Another native dialog opened before the registration command.");
                op.SetStatus("connecting", "opening_native_registration");
                // MasterProgrammer inherits synchronous Engine.Programmer.Run. InitDevice calls
                // Form.ShowDialog (0x06000487), which returns only after its native dialog closes.
                op.NativeCommandStarted = true;
                Call(programmer, "Run", command, Guid.Parse(op.Request.recordId), null);
                op.NativeRunFinished = true;
                // A successful outer command alone is not proof of registration. The dialog watcher
                // must observe Successfully and verify the target ID/KEY before MarkRegistered.
                if (!op.NativeSuccessObserved && !op.IsTerminal)
                    op.Fail(op.CancellationRequested ? "cancelled_before_confirmation" : "native_registration_unconfirmed",
                        op.CancellationRequested ? "Native registration was cancelled." : "Native command returned without confirmed registration.");
            }));
        }
    }
}
