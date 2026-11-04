// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Globalization;
using System.Reflection;

namespace Streletz.MasterBridge
{
    // The inspected native direct-only route deliberately omits the optional
    // cloud SerialNumber from its URI. Bind that omission to the live project
    // and to the exact URI Master already generated, without generating a new
    // URI, changing configuration, reading hardware, or exposing credentials.
    internal static class NativeProjectRouteProof
    {
        internal static bool Verify(object operationAdapter, object resolvedAdapter, object selectedAdapter,
            object connectionSegment, string expectedAdapterId, string expectedSegmentId,
            string expectedModuleId, string endpoint, string endpointModule, byte[] endpointPassword,
            Func<string, string, Type> loadedType, Func<object, string, object> property,
            NativeAdapterDiagnostic diagnostic)
        {
            try
            {
                if (!String.IsNullOrEmpty(endpointModule)) return diagnostic.Reject("endpoint_module_invalid");
                if (operationAdapter == null || !Object.ReferenceEquals(operationAdapter, resolvedAdapter) ||
                    !Object.ReferenceEquals(operationAdapter, selectedAdapter) || connectionSegment == null)
                    return diagnostic.Reject("endpoint_native_route_mismatch");
                if (!NativeRoutePolicy.SameGuid(Text(property(operationAdapter, "Guid")), expectedAdapterId) ||
                    !NativeRoutePolicy.SameGuid(Text(property(operationAdapter, "SegmentGuid")), expectedSegmentId) ||
                    !NativeRoutePolicy.SameGuid(Text(property(connectionSegment, "Guid")), expectedSegmentId))
                    return diagnostic.Reject("endpoint_native_route_mismatch");

                object info = property(operationAdapter, "Info"), segmentInfo = property(connectionSegment, "Info");
                Type moduleType = loadedType("MostIPI.Similar.MK_IP.MkIpInfo", "Plugin.MostIPI");
                Type segmentType = loadedType("CorePlugins.segment.CustomInfo", "Plugin.CorePlugins");
                if (info == null || info.GetType() != moduleType || segmentInfo == null || segmentInfo.GetType() != segmentType ||
                    !NativeRoutePolicy.SameGuid(Text(property(segmentInfo, "LonInterface")), expectedAdapterId))
                    return diagnostic.Reject("endpoint_native_route_mismatch");

                diagnostic.projectModuleId = NativeAdapterDiagnostic.Module(Hex.Bytes(property(info, "ID") as byte[]));
                if (NativeAdapterDiagnostic.Module(expectedModuleId).Length == 0 ||
                    diagnostic.projectModuleId != NativeAdapterDiagnostic.Module(expectedModuleId))
                    return diagnostic.Reject("endpoint_module_mismatch");
                object cloud = property(segmentInfo, "IsCloudEnabled"), direct = property(segmentInfo, "IsDirectConnectionEnabled");
                diagnostic.nativeCloudEnabled = cloud is bool ? (bool?)cloud : null;
                diagnostic.nativeDirectEnabled = direct is bool ? (bool?)direct : null;

                // The public AdapterName getter may generate a URI and display
                // native configuration dialogs. Read only its pinned cache.
                FieldInfo cachedRoute = segmentType.GetField("adapterNameValue",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (cachedRoute == null || cachedRoute.DeclaringType != segmentType || !cachedRoute.IsPrivate ||
                    cachedRoute.IsStatic || cachedRoute.IsInitOnly || cachedRoute.FieldType != typeof(string))
                    return diagnostic.Reject("endpoint_native_route_mismatch");
                string nativeEndpoint = cachedRoute.GetValue(segmentInfo) as string;
                diagnostic.nativeRouteCacheMatched = !String.IsNullOrEmpty(endpoint) &&
                    !String.IsNullOrEmpty(nativeEndpoint) && String.Equals(endpoint, nativeEndpoint, StringComparison.Ordinal);
                byte[] configuredPassword = property(info, "ConnectionPasswordMD5Code") as byte[];
                diagnostic.nativeRoutePasswordMatched = SamePassword(configuredPassword, endpointPassword);

                if (diagnostic.nativeCloudEnabled != false || diagnostic.nativeDirectEnabled != true ||
                    diagnostic.nativeRouteCacheMatched != true || diagnostic.nativeRoutePasswordMatched != true)
                    return diagnostic.Reject("endpoint_native_route_mismatch");
                if (!Object.ReferenceEquals(property(operationAdapter, "Info"), info) ||
                    !Object.ReferenceEquals(property(connectionSegment, "Info"), segmentInfo) ||
                    !String.Equals(cachedRoute.GetValue(segmentInfo) as string, nativeEndpoint, StringComparison.Ordinal))
                    return diagnostic.Reject("endpoint_native_route_mismatch");

                // Do not populate actualModuleId: the opened URI has no serial.
                diagnostic.moduleIdentitySource = "project_adapter_and_native_route";
                diagnostic.check = "verified";
                return true;
            }
            catch { return diagnostic.Reject("endpoint_native_route_mismatch"); }
        }

        private static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty; }

        private static bool SamePassword(byte[] configured, byte[] opened)
        {
            if (configured == null || opened == null || configured.Length != 16 || opened.Length != 16) return false;
            int different = 0;
            for (int i = 0; i < 16; i++) different |= configured[i] ^ opened[i];
            return different == 0;
        }
    }
}
