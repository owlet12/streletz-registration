// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Streletz.MasterBridge
{
    internal static class NativeTransportProof
    {
        private const string TransportAssembly = "BaseAdapterTransport";
        private const string TransportNamespace = "BaseAdapterTransport.Core.Transport.Impl.";

        // Reads the selected native transport without invoking Open, IsOpen,
        // Read, Write, Cancel or Close. In the inspected vendor build IsOpen can
        // itself send a zero-byte socket probe and close an inactive connection.
        internal static bool Verify(object originalAdapter, Func<string, string, Type> loadedType,
            NativeAdapterDiagnostic diagnostic, out object physicalAdapter)
        {
            physicalAdapter = null;
            try
            {
                object adapter = originalAdapter;
                Type lockedType = loadedType("BaseAdapter.LockedAdapter", "BaseAdapter");
                for (int depth = 0; adapter != null && adapter.GetType() == lockedType && depth < 3; depth++)
                {
                    MethodInfo method = lockedType.GetMethod("GetUnderlyingAdapter", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (method == null || method.IsStatic) return diagnostic.Reject("adapter_wrapper_mismatch");
                    object next = method.Invoke(adapter, null);
                    if (next == null || Object.ReferenceEquals(next, adapter)) return diagnostic.Reject("adapter_wrapper_mismatch");
                    adapter = next;
                }
                if (adapter == null) return diagnostic.Reject("adapter_missing");
                if (adapter.GetType() == lockedType) return diagnostic.Reject("adapter_wrapper_mismatch");
                if (adapter.GetType() != loadedType("UsbEthernetAdapter.EthernetUSB", "UsbEthernetAdapter"))
                    return diagnostic.Reject("adapter_type_mismatch");
                diagnostic.adapterType = "UsbEthernetAdapter.EthernetUSB";
                physicalAdapter = adapter;
                Type proxyType = loadedType("UsbEthernetAdapter.UsbProxyAdapter", "UsbEthernetAdapter");
                Type holderInterface = loadedType("BaseAdapterTransport.Core.Transport.ITransportHolder", TransportAssembly);
                PropertyInfo transportProperty = ReadProperty(proxyType, "Transport", holderInterface, false);
                object holder = transportProperty.GetValue(adapter, null);
                Type holderType = loadedType(TransportNamespace + "TransportHolder", TransportAssembly);
                if (holder == null || holder.GetType() != holderType) return diagnostic.Reject("transport_holder_mismatch");
                Type channelInterface = loadedType("BaseAdapterTransport.Core.Transport.ITransportChannel", TransportAssembly);
                FieldInfo nestedField = ReadField(holderType, "nestedTransport", channelInterface, true);
                object collaborator = nestedField.GetValue(holder);
                Type collaboratorType = loadedType(TransportNamespace + "TransportCollaborator", TransportAssembly);
                if (collaborator == null || collaborator.GetType() != collaboratorType)
                    return diagnostic.Reject("transport_collaborator_mismatch");
                FieldInfo currentField = ReadField(collaboratorType, "currentTransport", channelInterface, false);
                object selected = currentField.GetValue(collaborator);
                if (selected == null) return diagnostic.Reject("transport_not_selected");
                object transport = selected;
                Type hidType = loadedType(TransportNamespace + "HidDeviceTransport", TransportAssembly);
                if (transport.GetType() == hidType)
                {
                    diagnostic.actualConnectionKind = "usb"; diagnostic.activeTransportType = "HidDeviceTransport";
                    return diagnostic.Reject("transport_usb");
                }
                Type wrapperType = loadedType(TransportNamespace + "UsbWrapper", TransportAssembly);
                if (transport.GetType() == wrapperType)
                    transport = ReadProperty(wrapperType, "Inner", channelInterface, false).GetValue(transport, null);
                Type cryptoType = loadedType(TransportNamespace + "CryptoWebPort", TransportAssembly);
                Type cryptoUsbType = loadedType(TransportNamespace + "CryptoUsb", TransportAssembly);
                if (transport == null || (transport.GetType() != cryptoType && transport.GetType() != cryptoUsbType))
                    return diagnostic.Reject("transport_crypto_mismatch");
                object crypto = transport;
                if (!Object.Equals(ReadField(cryptoType, "isOpened", typeof(bool), false).GetValue(crypto), true))
                    return diagnostic.Reject("transport_not_authenticated");
                transport = ReadField(cryptoType, "transport", channelInterface, true).GetValue(crypto);
                if (transport == null) return diagnostic.Reject("transport_leaf_missing");
                if (transport.GetType() == loadedType(TransportNamespace + "CloudProxy", TransportAssembly))
                {
                    diagnostic.actualConnectionKind = "cloud"; diagnostic.activeTransportType = "CloudProxy";
                    return diagnostic.Reject("transport_cloud");
                }
                Type webType = loadedType(TransportNamespace + "WebPort", TransportAssembly);
                if (transport.GetType() != webType) return diagnostic.Reject("transport_leaf_mismatch");
                diagnostic.actualConnectionKind = "direct_mk_ip"; diagnostic.activeTransportType = "WebPort";
                Uri uri = ReadField(webType, "uri", typeof(Uri), true).GetValue(transport) as Uri;
                FieldInfo clientField = ReadField(webType, "client", typeof(TcpClient), false);
                TcpClient client = clientField.GetValue(transport) as TcpClient;
                Socket socket = client == null ? null : client.Client;
                if (socket == null || !socket.Connected) return diagnostic.Reject("transport_tcp_disconnected");
                IPEndPoint remote = socket.RemoteEndPoint as IPEndPoint;
                if (remote == null) return diagnostic.Reject("transport_tcp_disconnected");
                diagnostic.actualHost = NativeAdapterDiagnostic.Host(remote.Address.ToString());
                diagnostic.actualPort = remote.Port;
                if (uri == null || !uri.IsAbsoluteUri || NativeAdapterDiagnostic.Host(uri.Host).Length == 0 ||
                    NativeAdapterDiagnostic.Host(uri.Host) != diagnostic.expectedHost || diagnostic.actualHost != diagnostic.expectedHost)
                    return diagnostic.Reject("transport_host_mismatch");
                if (uri.Port != diagnostic.expectedPort || remote.Port != diagnostic.expectedPort)
                    return diagnostic.Reject("transport_port_mismatch");
                if (!Object.ReferenceEquals(transportProperty.GetValue(adapter, null), holder) ||
                    !Object.ReferenceEquals(nestedField.GetValue(holder), collaborator) ||
                    !Object.ReferenceEquals(currentField.GetValue(collaborator), selected) ||
                    !Object.ReferenceEquals(clientField.GetValue(transport), client))
                    return diagnostic.Reject("transport_changed");
                return true;
            }
            catch { return diagnostic.Reject("transport_read_failed"); }
        }

        private static FieldInfo ReadField(Type owner, string name, Type type, bool readOnly)
        {
            FieldInfo field = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.DeclaringType != owner || field.IsStatic || field.FieldType != type || field.IsInitOnly != readOnly)
                throw new InvalidOperationException("Native transport field contract mismatch.");
            return field;
        }

        private static PropertyInfo ReadProperty(Type owner, string name, Type type, bool publicGetter)
        {
            PropertyInfo property = owner.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            MethodInfo getter = property == null ? null : property.GetGetMethod(true);
            if (property == null || property.DeclaringType != owner || property.PropertyType != type ||
                property.GetIndexParameters().Length != 0 || getter == null || getter.IsStatic || getter.IsPublic != publicGetter)
                throw new InvalidOperationException("Native transport property contract mismatch.");
            return property;
        }
    }
}
