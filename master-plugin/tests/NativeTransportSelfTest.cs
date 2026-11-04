// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Web.Script.Serialization;
using Streletz.MasterBridge;
using TransportFixture;

internal static class NativeTransportSelfTest
{
    private static int checks;
    private static readonly Dictionary<string,Type> types = new Dictionary<string,Type> {
        { "BaseAdapter.LockedAdapter", typeof(Locked) },
        { "UsbEthernetAdapter.EthernetUSB", typeof(Ethernet) },
        { "UsbEthernetAdapter.UsbProxyAdapter", typeof(Proxy) },
        { "BaseAdapterTransport.Core.Transport.ITransportHolder", typeof(IHolder) },
        { "BaseAdapterTransport.Core.Transport.ITransportChannel", typeof(IChannel) },
        { "BaseAdapterTransport.Core.Transport.Impl.TransportHolder", typeof(Holder) },
        { "BaseAdapterTransport.Core.Transport.Impl.TransportCollaborator", typeof(Collaborator) },
        { "BaseAdapterTransport.Core.Transport.Impl.HidDeviceTransport", typeof(Hid) },
        { "BaseAdapterTransport.Core.Transport.Impl.UsbWrapper", typeof(Wrapper) },
        { "BaseAdapterTransport.Core.Transport.Impl.CryptoWebPort", typeof(Crypto) },
        { "BaseAdapterTransport.Core.Transport.Impl.CryptoUsb", typeof(CryptoUsb) },
        { "BaseAdapterTransport.Core.Transport.Impl.CloudProxy", typeof(Cloud) },
        { "BaseAdapterTransport.Core.Transport.Impl.WebPort", typeof(Web) }
    };

    public static int Main(string[] args)
    {
        TcpListener listener = null;
        try
        {
            const string id = "112233445566";
            const string owner = "11111111-2222-4333-8444-555555555555";
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,true,true,owner,owner)=="verified",
                "MK-IP ForceUsbScheme=true framing is valid for configured direct TCP.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,true,false,owner,owner)=="verified",
                "Changing protocol framing alone does not select the physical transport.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,"11223344",false,true,true,owner,owner)=="endpoint_module_invalid",
                "A shortened USB serial never substitutes the complete MK-IP ID.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,"112233445577",false,true,true,owner,owner)=="endpoint_module_mismatch",
                "Another module at the same address is rejected.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.2",6344,id,false,true,true,owner,owner)=="endpoint_host_mismatch", "Configured host remains bound.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6345,id,false,true,true,owner,owner)=="endpoint_port_mismatch", "Configured port remains bound.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,true,true,true,owner,owner)=="endpoint_tunnel", "An arbitrary nested URI tunnel is rejected.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,false,true,owner,owner)=="endpoint_direct_disabled", "Cloud-only configuration cannot become direct.");
            Check(NativeAdapterDiagnostic.Host("SECRET-URI")=="" && NativeAdapterDiagnostic.Module("SECRET-KEY")=="" && NativeAdapterDiagnostic.GuidText("SECRET-TEXT")=="",
                "Unparsed native data cannot become diagnostic identifiers.");

            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,true,true,owner,Guid.Empty.ToString())=="endpoint_owner_missing", "An unbound URI cannot establish a project adapter node.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,true,true,owner,"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")=="endpoint_owner_mismatch", "Same TCP address and full hardware ID cannot authorize a different project adapter owner.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,id,false,true,true,"",owner)=="endpoint_owner_missing", "A missing expected project adapter identity cannot authorize a connection.");
            Check(NativeEndpointPolicy.CheckOwner(owner,owner.ToUpperInvariant())=="verified", "Project GUID matching is canonical rather than case-sensitive.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,null,false,true,true,owner,owner)=="endpoint_module_missing",
                "An absent URI cloud serial requires separate native project-route proof.");
            foreach (string invalid in new[] { " ", "000000000000", "FFFFFFFFFFFF", "11223344556677", "11223344ZZ66" })
                Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,invalid,false,true,true,owner,owner)=="endpoint_module_invalid",
                    "Present invalid serials cannot be mistaken for the optional omitted cloud serial.");
            Check(NativeEndpointPolicy.Check("127.0.0.1",6344,id,"127.0.0.1",6344,null,false,true,true,owner,"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")=="endpoint_owner_mismatch",
                "A missing serial never bypasses project adapter ownership.");
            checks += NativeProjectRouteSelfTest.Run();
            listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback,port);
                using (var peer = listener.AcceptTcpClient())
                {
                    var web = new Web(new Uri("ethernet+usb://dummy:PRIVATE@127.0.0.1:"+port+"/PRIVATE"),client);
                    var crypto = new CryptoUsb(web,true);
                    var current = new Wrapper(crypto);
                    var collaborator = new Collaborator(current);
                    var adapter = new Ethernet(new Holder(collaborator));
                    NativeAdapterDiagnostic diagnostic;
                    Check(Verify(adapter,port,out diagnostic), "The selected encrypted MK-IP TCP leaf is accepted.");
                    Check(diagnostic.actualHost=="127.0.0.1" && diagnostic.actualPort==port && diagnostic.activeTransportType=="WebPort", "Evidence comes from the actual connected local socket.");
                    Check(Verify(new Locked(new Locked(adapter)),port,out diagnostic), "Inspected native adapter wrappers preserve the same physical proof.");
                    Check(!Verify(new Locked(new Locked(new Locked(new Locked(adapter)))),port,out diagnostic) && diagnostic.check=="adapter_wrapper_mismatch", "Unexpected wrapper depth is rejected.");
                    collaborator.Select(new Hid());
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_usb" && diagnostic.actualConnectionKind=="usb", "Active physical USB is rejected even with otherwise matching configured IP.");
                    collaborator.Select(new Wrapper(new CryptoUsb(new Cloud(),true)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_cloud", "Cloud fallback is rejected despite the configured direct endpoint.");
                    collaborator.Select(null);
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_not_selected", "No selected transport cannot authorize confirmation.");
                    collaborator.Select(new Wrapper(new CryptoUsb(web,false)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_not_authenticated", "A socket before native crypto authentication cannot authorize confirmation.");
                    collaborator.Select(new Unknown());
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_crypto_mismatch", "Unknown native transport wrappers are rejected.");
                    collaborator.Select(new Wrapper(new CryptoUsb(new Unknown(),true)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_leaf_mismatch", "A URI-shaped unknown leaf cannot masquerade as TCP.");
                    collaborator.Select(new Wrapper(new CryptoUsb(new Web(new Uri("ethernet+usb://127.0.0.2:"+port),client),true)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_host_mismatch", "The connected socket and immutable URI must name the same host.");
                    int otherPort = port == 65535 ? port-1 : port+1;
                    collaborator.Select(new Wrapper(new CryptoUsb(new Web(new Uri("ethernet+usb://127.0.0.1:"+otherPort),client),true)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_port_mismatch", "The connected socket and immutable URI must name the same port.");
                    collaborator.Select(current);
                    Check(!Verify(adapter,otherPort,out diagnostic) && diagnostic.check=="transport_port_mismatch", "A different requested port is rejected against the live socket.");
                    collaborator.Select(new Crypto(web,true));
                    Check(Verify(adapter,port,out diagnostic), "The inspected ordinary crypto wrapper also requires a live TCP leaf.");
                    collaborator.Select(new Wrapper(new CryptoUsb(new Web(new Uri("ethernet+usb://127.0.0.1:"+port),null),true)));
                    Check(!Verify(adapter,port,out diagnostic) && diagnostic.check=="transport_tcp_disconnected", "A configured URI without a connected client is rejected.");
                    Check(!Verify(new object(),port,out diagnostic) && diagnostic.check=="adapter_type_mismatch", "An uninspected adapter type is rejected.");
                    collaborator.Select(current);
                    Check(Verify(adapter,port,out diagnostic), "Valid evidence remains available after rejected alternatives.");
                    diagnostic.forceUsbScheme = true; diagnostic.check = "verified";
                    var operation = new OperationState { Request = new StartRequest { panel = new PanelRequest(), deviceKey = "PRIVATE-KEY" }, AdapterDiagnostic = diagnostic };
                    string serialized = new JavaScriptSerializer().Serialize(operation.Snapshot());
                    Check(serialized.Contains("adapterDiagnostic") && serialized.Contains("forceUsbScheme"), "Structured adapter diagnostics travel with the retained operation.");
                    Check(!serialized.Contains("PRIVATE") && !serialized.Contains("ethernet+") && !serialized.Contains("dummy"), "No raw URI, URI credentials or sensor KEY enter the operation snapshot.");
                    Check(Trap.SideEffects == 0, "No Open/IsOpen/Read/Write/Close vendor callbacks were called by proof.");
                    client.Close();
                    Check(!Verify(adapter,port,out diagnostic), "A closed previously valid socket is rejected.");
                }
            }
            Console.WriteLine("PASS "+checks+" native transport assertions; real loopback socket only, no Master or panel access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL "+error.Message); return 1; }
        finally { if (listener != null) listener.Stop(); }
    }
    private static bool Verify(object adapter,int port,out NativeAdapterDiagnostic diagnostic)
    {
        diagnostic = new NativeAdapterDiagnostic { expectedHost="127.0.0.1",expectedPort=port,expectedModuleId="112233445566" };
        object physical;
        return NativeTransportProof.Verify(adapter,(name,assembly)=>types[name],diagnostic,out physical);
    }
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); checks++; }
}

namespace TransportFixture
{
    internal interface IChannel { }
    internal interface IHolder : IChannel { }
    internal static class Trap { internal static int SideEffects; internal static bool Invoke() { SideEffects++; throw new Exception("A vendor I/O callback must never be called."); } }
    internal class Proxy { internal IHolder Transport { get; private set; } protected Proxy(IHolder value) { Transport=value; } }
    internal sealed class Ethernet : Proxy { internal Ethernet(IHolder value):base(value) { } public bool IsOpen() { return Trap.Invoke(); } }
    internal sealed class Locked { private readonly object inner; internal Locked(object value) { inner=value; } public object GetUnderlyingAdapter(){return inner;} }
    internal sealed class Holder : IHolder { private readonly IChannel nestedTransport; internal Holder(IChannel value){nestedTransport=value;} public bool IsOpen { get {return Trap.Invoke();} } }
    internal sealed class Collaborator : IChannel { private IChannel currentTransport; internal Collaborator(IChannel value){currentTransport=value;} internal void Select(IChannel value){currentTransport=value;} public bool IsOpen {get{return Trap.Invoke();}} }
    internal sealed class Wrapper : IChannel { private IChannel Inner {get;} internal Wrapper(IChannel value){Inner=value;} public bool IsOpen {get{return Trap.Invoke();}} }
    internal class Crypto : IChannel { private readonly IChannel transport; private bool isOpened; internal Crypto(IChannel value,bool open){transport=value;isOpened=open;} public bool IsOpen {get{return Trap.Invoke();}} }
    internal sealed class CryptoUsb : Crypto { internal CryptoUsb(IChannel value,bool open):base(value,open){} }
    internal sealed class Web : IChannel { private readonly Uri uri; private TcpClient client; internal Web(Uri value,TcpClient connection){uri=value;client=connection;} public bool IsOpen {get{return Trap.Invoke();}} }
    internal sealed class Hid : IChannel { public bool IsOpen {get{return Trap.Invoke();}} }
    internal sealed class Cloud : IChannel { public bool IsOpen {get{return Trap.Invoke();}} }
    internal sealed class Unknown : IChannel { }
}
