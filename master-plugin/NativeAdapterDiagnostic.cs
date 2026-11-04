// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Net;

namespace Streletz.MasterBridge
{
    // Only allowlisted, already parsed values leave the process. Never include a
    // URI, exception text, credentials, KEY, or a native project object here.
    internal sealed class NativeAdapterDiagnostic
    {
        public string check = "not_checked";
        public string actualConnectionKind = "unknown";
        public string activeTransportType = "unknown";
        public string adapterType = "unknown";
        public string expectedHost = "";
        public string actualHost = "";
        public int expectedPort;
        public int actualPort;
        public string expectedModuleId = "";
        public string actualModuleId = "";
        public string actualModuleIdState = "";
        public string moduleIdentitySource = "";
        public string projectModuleId = "";
        public bool? nativeRouteCacheMatched;
        public bool? nativeCloudEnabled;
        public bool? nativeDirectEnabled;
        public bool? nativeRoutePasswordMatched;
        public string targetSegmentId = "";
        public string ownerSegmentId = "";
        public string adapterSegmentId = "";
        public string expectedAdapterNodeId = "";
        public string actualAdapterOwnerId = "";
        public string expectedRouteOwnerId = "";
        public string actualRouteOwnerId = "";
        public string routeKind = "";
        public bool forceUsbScheme;

        internal bool Reject(string reason) { check = reason; return false; }
        internal static string Host(string value)
        {
            IPAddress address;
            return IPAddress.TryParse((value ?? "").Trim(), out address) ? address.ToString().ToLowerInvariant() : "";
        }
        internal static string GuidText(string value)
        {
            Guid guid;
            return Guid.TryParse(value, out guid) && guid != Guid.Empty ? guid.ToString("D") : "";
        }
        internal static string Module(string value)
        {
            string id = Hex.Normalize(value);
            return Hex.IsDeviceId(id) ? id : "";
        }
    }

    internal static class NativeEndpointPolicy
    {
        internal static string Check(string expectedHost, int expectedPort, string expectedModule,
            string actualHost, int actualPort, string actualModule, bool hasTunnel, bool directEnabled, bool forceUsbScheme,
            string expectedAdapterNodeId, string actualAdapterOwnerId)
        {
            string owner = CheckOwner(expectedAdapterNodeId, actualAdapterOwnerId);
            if (owner != "verified") return owner;
            if (hasTunnel) return "endpoint_tunnel";
            if (!directEnabled) return "endpoint_direct_disabled";
            if (NativeAdapterDiagnostic.Host(expectedHost).Length == 0 ||
                NativeAdapterDiagnostic.Host(expectedHost) != NativeAdapterDiagnostic.Host(actualHost)) return "endpoint_host_mismatch";
            if (expectedPort < 1 || expectedPort > 65535 || actualPort != expectedPort) return "endpoint_port_mismatch";
            if (NativeAdapterDiagnostic.Module(expectedModule).Length == 0) return "endpoint_module_mismatch";
            if (String.IsNullOrEmpty(actualModule)) return "endpoint_module_missing";
            if (NativeAdapterDiagnostic.Module(actualModule).Length == 0) return "endpoint_module_invalid";
            if (NativeAdapterDiagnostic.Module(expectedModule) != NativeAdapterDiagnostic.Module(actualModule)) return "endpoint_module_mismatch";
            // Official MkIpInfo.GetConnectionParams always sets ForceUsbScheme.
            // It chooses the protocol framing for TCP as well as USB. Physical
            // transport identity is proved separately from the live WebPort.
            return "verified";
        }

        internal static string CheckOwner(string expected, string actual)
        {
            string wanted = NativeAdapterDiagnostic.GuidText(expected), observed = NativeAdapterDiagnostic.GuidText(actual);
            if (wanted.Length == 0 || observed.Length == 0) return "endpoint_owner_missing";
            return wanted == observed ? "verified" : "endpoint_owner_mismatch";
        }
    }
}
