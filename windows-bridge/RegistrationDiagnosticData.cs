// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace StreletzBridge
{
    // Diagnostics contain only reviewed scalar fields. Raw native messages,
    // adapter URIs, credentials and arbitrary JSON must never enter the journal.
    internal static class RegistrationDiagnosticData
    {
        internal static string Code(string value)
        { return String.IsNullOrEmpty(value) ? "" : PanelRegistrationDriver.SafeDiagnosticCode(value); }

        internal static string Phase(string value)
        {
            return new[] { "registration_preflight", "native_start", "native_operation", "native_status" }
                .Contains(value) ? value : "";
        }

        private static readonly HashSet<string> Checks = new HashSet<string>((
            "not_checked verified dialog_type_mismatch target_changed dialog_target_mismatch owner_segment_mismatch " +
            "adapter_missing adapter_wrapper_mismatch adapter_type_mismatch adapter_segment_mismatch adapter_read_failed " +
            "terminal_tunnel_shape terminal_tunnel_mismatch endpoint_invalid endpoint_parse_failed endpoint_tunnel " +
            "endpoint_direct_disabled endpoint_host_mismatch endpoint_port_mismatch endpoint_module_mismatch " +
            "endpoint_module_missing endpoint_module_invalid endpoint_native_route_mismatch " +
            "endpoint_owner_missing endpoint_owner_mismatch " +
            "terminal_interface_owner_missing terminal_interface_owner_mismatch terminal_interface_tunnel_missing " +
            "terminal_interface_tunnel_mismatch terminal_interface_parse_failed " +
            "transport_holder_mismatch transport_collaborator_mismatch transport_not_selected transport_usb " +
            "transport_crypto_mismatch transport_not_authenticated transport_leaf_missing transport_cloud " +
            "transport_leaf_mismatch transport_tcp_disconnected transport_host_mismatch transport_port_mismatch " +
            "transport_changed transport_read_failed").Split(' '), StringComparer.Ordinal);

        private static readonly HashSet<string> AdapterTypes = new HashSet<string>((
            "BaseAdapter.LockedAdapter UsbEthernetAdapter.EthernetUSB UsbEthernetAdapter.UsbDirectAdapter " +
            "UsbEthernetAdapter.UsbProxyAdapter unknown").Split(' '), StringComparer.Ordinal);

        private static readonly HashSet<string> NativeCompletionStates = new HashSet<string>((
            "not_started waiting acknowledged completed blocked").Split(' '), StringComparer.Ordinal);

        private static readonly HashSet<string> NativeCompletionChecks = new HashSet<string>((
            "awaiting_success_window success_window_not_found success_window_ambiguous success_window_unverified " +
            "success_target_changed success_acknowledgement_requested success_acknowledgement_failed " +
            "success_acknowledgement_timeout native_completion_finished native_completion_unavailable " +
            "awaiting_native_completion native_completion_timeout").Split(' '), StringComparer.Ordinal);

        internal static Dictionary<string, object> Details(Dictionary<string, object> input)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (input == null) return result;
            foreach (var field in input)
            {
                string value = field.Value as string;
                switch (field.Key)
                {
                    case "check":
                        if (value != null && Checks.Contains(value)) result[field.Key] = value;
                        break;
                    case "nativeCompletionState":
                        if (value != null && NativeCompletionStates.Contains(value)) result[field.Key] = value;
                        break;
                    case "nativeCompletionCheck": case "nativeCompletionLastCheck":
                        if (value != null && NativeCompletionChecks.Contains(value)) result[field.Key] = value;
                        break;
                    case "routeKind":
                        if (value == "direct_mk_ip" || value == "panel_terminal") result[field.Key] = value;
                        break;
                    case "actualConnectionKind":
                        if (new[] { "direct_mk_ip", "usb", "cloud", "tunnel", "unknown" }.Contains(value)) result[field.Key] = value;
                        break;
                    case "adapterType":
                        if (value != null && AdapterTypes.Contains(value)) result[field.Key] = value;
                        break;
                    case "activeTransportType":
                        if (new[] { "unknown", "WebPort", "HidDeviceTransport", "CloudProxy" }.Contains(value)) result[field.Key] = value;
                        break;
                    case "actualModuleIdState":
                        if (new[] { "present", "missing", "invalid" }.Contains(value)) result[field.Key] = value;
                        break;
                    case "moduleIdentitySource":
                        if (new[] { "endpoint_and_project", "project_adapter_and_native_route" }.Contains(value)) result[field.Key] = value;
                        break;
                    case "expectedHost": case "actualHost":
                        IPAddress host;
                        if (value != null && IPAddress.TryParse(value, out host)) result[field.Key] = host.ToString();
                        break;
                    case "expectedPort": case "actualPort":
                        long port;
                        if (field.Value != null && Int64.TryParse(Convert.ToString(field.Value, CultureInfo.InvariantCulture),
                            NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535)
                            result[field.Key] = (int)port;
                        break;
                    case "expectedModuleId": case "actualModuleId": case "projectModuleId":
                        if (value != null && Regex.IsMatch(value, "\\A[0-9A-Fa-f]{12}\\z")) result[field.Key] = value.ToUpperInvariant();
                        break;
                    case "targetSegmentId": case "ownerSegmentId": case "adapterSegmentId":
                    case "nativePanelId": case "terminalPanelId": case "terminalSegmentId": case "adapterNodeId":
                    case "expectedAdapterNodeId": case "actualAdapterOwnerId":
                    case "expectedRouteOwnerId": case "actualRouteOwnerId":
                        Guid id;
                        if (value != null && Guid.TryParse(value, out id) && id != Guid.Empty) result[field.Key] = id.ToString("D");
                        break;
                    case "forceUsbScheme": case "nativeRouteCacheMatched": case "nativeCloudEnabled":
                    case "nativeDirectEnabled": case "nativeRoutePasswordMatched": case "autoConfirmSuccessResult":
                    case "nativeSuccessDialogSuppressed": case "suppressNativeSuccessDialog":
                        if (field.Value is bool) result[field.Key] = field.Value;
                        break;
                    case "pluginVersion":
                        Version version;
                        if (value != null && Version.TryParse(value, out version) && version.Build >= 0 && version.Revision >= 0)
                            result[field.Key] = version.ToString(4);
                        break;
                }
            }
            return result;
        }
    }
}
