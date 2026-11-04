// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StreletzBridge
{
    internal static class HttpGatewayNetworkTests
    {
        const string Code = "1234567890ABCDEF";
        static int checks;
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                ThreadPool.SetMinThreads(32, 32); ServicePointManager.DefaultConnectionLimit = 32;
                Network(); Deadlines(); Render(args[0]);
                Console.WriteLine("PASS " + checks + " HTTP.sys/.NET Framework network and dialog checks; synthetic handler only; no URL ACL/firewall changes.");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        }
        static byte[] Frame(string id = null, string type = "get_project")
        {
            return Encoding.UTF8.GetBytes(BridgeJson.Serialize(ProtocolV3.Encode(new Dictionary<string, object> {
                { "protocol", 3 }, { "type", type }, { "requestId", id ?? Guid.NewGuid().ToString("D") }, { "sentAt", BridgeValues.NowMs() }
            }, Code, "request")));
        }
        static void Network()
        {
            int calls = 0;
            using (var gateway = new HttpGateway(Code, request => {
                Interlocked.Increment(ref calls);
                return ProtocolV3.Response(BridgeValues.Text(request, "requestId"), true,
                    BridgeValues.Text(request, "type") == "large" ? new string('x', 1200000) : "test");
            }))
            {
                string url = gateway.StartLocalTest();
                var health = Send(url, "GET", null);
                Check(health.Item1 == 200 && health.Item2.Contains("not_checked") && calls == 0, "GET health must not claim device connectivity");
                string id = Guid.NewGuid().ToString("D");
                var reply = Send(url, "POST", Frame(id));
                var payload = ProtocolV3.Decode(BridgeJson.ParseObject(reply.Item2), Code, "response");
                Check(reply.Item1 == 200 && BridgeValues.Text(payload, "requestId") == id && calls == 1, "HTTP request response encryption/identity");
                Check(Send(url, "POST", Frame(), chunked: true).Item1 == 200 && calls == 2, "chunked POST from HTTP proxy");
                var large = Send(url, "POST", Frame(type: "large"));
                var decoded = ProtocolV3.Decode(BridgeJson.ParseObject(large.Item2), Code, "response");
                Check(BridgeValues.Text(decoded, "message").Length == 1200000 && calls == 3, "large project envelope via actual HTTP.sys");
                Check(Send(url, "PUT", Frame()).Item1 == 405, "unsupported HTTP method");
                Check(Send(url + "wrong", "GET", null).Item1 == 404, "unknown route");
                Check(Send(url + "?target=other", "POST", Frame()).Item1 == 404, "query cannot redirect gateway");
                Check(Send(url, "POST", Frame(), "text/plain").Item1 == 415, "browser simple request cannot reach dispatch");
                Check(Send(url, "POST", Encoding.UTF8.GetBytes("{}")).Item1 == 400, "malformed envelope");
                var bad = BridgeJson.ParseObject(Encoding.UTF8.GetString(Frame())); bad["mac"] = Convert.ToBase64String(new byte[32]);
                Check(Send(url, "POST", Encoding.UTF8.GetBytes(BridgeJson.Serialize(bad))).Item1 == 401, "wrong HMAC");
                Check(Send(url, "POST", new byte[65537]).Item1 == 413, "oversize Content-Length");
                Check(Send(url, "POST", new byte[65537], chunked: true).Item1 == 413, "oversize chunked body");
                Check(calls == 3, "all invalid requests rejected before dispatch");
                Check(gateway.IsRunning, "listener remains usable after invalid requests");
                gateway.Dispose(); Check(!gateway.IsRunning, "stop releases listener");
            }
        }
        static void Deadlines()
        {
            int calls = 0;
            using (var gateway = new HttpGateway(Code, request => { calls++; return ProtocolV3.Response("", true, ""); }, 200, 2000))
            {
                Uri url = new Uri(gateway.StartLocalTest());
                using (TcpClient client = new TcpClient("127.0.0.1", 80))
                {
                    client.ReceiveTimeout = 3000;
                    byte[] partial = Encoding.ASCII.GetBytes("POST " + url.AbsolutePath + " HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 1000\r\nConnection: close\r\n\r\nx");
                    client.GetStream().Write(partial, 0, partial.Length);
                    DateTime began = DateTime.UtcNow;
                    try { while (client.GetStream().ReadByte() != -1) { } } catch (IOException) { }
                    Check((DateTime.UtcNow - began).TotalMilliseconds < 1800 && calls == 0, "slow upload cut off before coordinator");
                }
            }
            int entered = 0;
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (var gateway = new HttpGateway(Code, request => {
                Interlocked.Increment(ref entered); release.Wait(6000);
                return ProtocolV3.Response(BridgeValues.Text(request, "requestId"), true, "");
            }, 2000, 800))
            {
                string url = gateway.StartLocalTest();
                Task[] pending = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { try { Send(url, "POST", Frame()); } catch (WebException) { } })).ToArray();
                try
                {
                    Check(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 8, 3000), "eight dispatches started");
                    Thread.Sleep(1000);
                    Check(Send(url, "POST", Frame()).Item1 == 503 && entered == 8, "timeouts keep occupied dispatch slots; no unbounded work or retries");
                    gateway.Dispose();
                }
                finally { release.Set(); Task.WaitAll(pending, 5000); }
                Check(entered == 8, "stop and disconnected clients do not replay dispatch");
            }
        }
        static Tuple<int, string> Send(string url, string method, byte[] body, string contentType = "application/json", bool chunked = false)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null; request.Method = method; request.AllowAutoRedirect = false;
            request.Timeout = 5000; request.ReadWriteTimeout = 5000; request.KeepAlive = false;
            request.ServicePoint.Expect100Continue = false;
            try
            {
                if (body != null)
                {
                    request.ContentType = contentType; request.SendChunked = chunked;
                    if (!chunked) request.ContentLength = body.Length;
                    using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return Read(response);
            }
            catch (WebException error)
            {
                if (error.Response == null) throw;
                using (HttpWebResponse response = (HttpWebResponse)error.Response) return Read(response);
            }
        }
        static Tuple<int, string> Read(HttpWebResponse response)
        {
            using (var input = new StreamReader(response.GetResponseStream()))
            {
                string text = input.ReadToEnd();
                Check(response.Headers["Cache-Control"] == "no-store", "HTTP replies not cacheable");
                Check(response.ContentType.StartsWith("application/json"), "HTTP replies have JSON type");
                return Tuple.Create((int)response.StatusCode, text);
            }
        }
        static void Render(string path)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var dialog = new HttpGatewayDialog(new HttpGatewaySettings { Enabled = true, Address = "https://bridge.example.net/streletz-bridge/" }, "192.168.0.244"))
            {
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-10000, -10000);
                dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show(); Application.DoEvents();
                IntPtr handle = dialog.Handle;
                Layout(dialog);
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path); }
                Check(All(dialog).OfType<Button>().Count() == 4, "all gateway controls rendered");
                Check(All(dialog).Where(c => c.Parent is FlowLayoutPanel).All(c => c.Bottom <= c.Parent.ClientSize.Height && c.Right <= c.Parent.ClientSize.Width), "dialog controls fit parent bounds");
            }
        }
        static void Layout(Control control) { control.CreateControl(); control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
        static IEnumerable<Control> All(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (Control item in All(child)) yield return item; } }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); Interlocked.Increment(ref checks); }
    }
}
