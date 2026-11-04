// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreletzBridge
{
    // Embedded HTTP transport for KeenDNS. TLS terminates at the router; ProtocolV3 remains end to end.
    // The exact same RegistrationCoordinator handles TCP and HTTP, retaining its durable operation dedup.
    internal sealed class HttpGateway : IDisposable
    {
        public const int DefaultPort = 23020;
        public const string Route = "/streletz-bridge/";
        private readonly string code;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> handler;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(8, 8);
        private readonly ConcurrentDictionary<HttpListenerContext, byte> active = new ConcurrentDictionary<HttpListenerContext, byte>();
        private readonly int bodyTimeout, responseTimeout;
        private HttpListener listener;
        private volatile bool stopping;
        private string route = Route;
        private bool loopbackOnly;
        public bool IsRunning { get { HttpListener current = listener; return !stopping && current != null && current.IsListening; } }
        public event Action Stopped;

        public HttpGateway(string pairingCode, Func<Dictionary<string, object>, Dictionary<string, object>> handler,
            int bodyTimeoutMilliseconds = 15000, int responseTimeoutMilliseconds = 120000)
        {
            code = ProtocolV3.NormalizePairingCode(pairingCode);
            this.handler = handler ?? throw new ArgumentNullException("handler");
            if (bodyTimeoutMilliseconds < 1 || responseTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeout");
            bodyTimeout = bodyTimeoutMilliseconds; responseTimeout = responseTimeoutMilliseconds;
        }

        public static string Prefix(int port, bool loopback = false)
        {
            ValidatePort(port);
            return "http://" + (loopback ? "localhost" : "+") + ":" + port + Route;
        }

        public static void ValidatePort(int port)
        {
            if (port < 1024 || port > 65535 || port == 23019)
                throw new InvalidDataException("Для HTTP выберите порт 1024–65535, кроме TCP-порта моста 23019.");
        }

        public void Start(int port, bool loopback = false)
        {
            StartPrefix(Prefix(port, loopback));
        }

        // Isolated HTTP.sys test namespace already reserved by Windows for ordinary users.
        // No URL ACL/firewall mutation and no production project/driver in tests.
        internal string StartLocalTest()
        {
            route = "/Temporary_Listen_Addresses/StreletzBridge-" + Guid.NewGuid().ToString("N") + Route;
            loopbackOnly = true;
            // The Windows reservation grants Listen but not Delegate: register its exact prefix.
            StartPrefix("http://+:80/Temporary_Listen_Addresses/");
            return "http://localhost:80" + route;
        }

        private void StartPrefix(string prefix)
        {
            if (listener != null) throw new InvalidOperationException("HTTP-доступ уже запущен.");
            HttpListener opened = new HttpListener();
            opened.Prefixes.Add(prefix);
            // Keep HTTP.sys's header/rate protections and bound header acquisition as well as body IO.
            opened.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(15);
            opened.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(15);
            opened.TimeoutManager.EntityBody = TimeSpan.FromSeconds(15);
            try { opened.Start(); }
            catch { opened.Close(); throw; }
            listener = opened;
            Task.Run(() => Accept(opened));
        }

        private async Task Accept(HttpListener opened)
        {
            try
            {
                while (!stopping)
                {
                    HttpListenerContext context = await opened.GetContextAsync().ConfigureAwait(false);
                    if (!slots.Wait(0)) { Reply(context, 503, "busy"); continue; }
                    active.TryAdd(context, 0);
                    // Handle owns its slot until dispatch actually completes, even after a client timeout.
                    Task ignored = Handle(context);
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (!stopping) { Dispose(); Action notify = Stopped; if (notify != null) notify(); }
            }
        }

        private async Task Handle(HttpListenerContext context)
        {
            using (Timer deadline = new Timer(_ => Abort(context), null, responseTimeout, Timeout.Infinite))
            {
                try
                {
                    HttpListenerRequest request = context.Request;
                    if (loopbackOnly && (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address)))
                    { Reply(context, 403, "local_test_only"); return; }
                    if (request.Url.AbsolutePath != route || !String.IsNullOrEmpty(request.Url.Query))
                    { Reply(context, 404, "not_found"); return; }
                    if (request.HttpMethod == "GET")
                    {
                        await Write(context, Encoding.UTF8.GetBytes("{\"service\":\"streletz-bridge\",\"version\":\"0.5.18\",\"http\":\"available\",\"equipment\":\"not_checked\"}"));
                        return;
                    }
                    if (request.HttpMethod != "POST")
                    { context.Response.Headers["Allow"] = "GET, POST"; Reply(context, 405, "method_not_allowed"); return; }
                    string type = (request.ContentType ?? "").Split(';')[0].Trim();
                    if (!String.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase)
                        || (!String.IsNullOrEmpty(request.Headers["Content-Encoding"])
                        && !String.Equals(request.Headers["Content-Encoding"], "identity", StringComparison.OrdinalIgnoreCase)))
                    { Reply(context, 415, "json_required"); return; }
                    if (request.ContentLength64 > ProtocolV3.MaximumLineLength)
                    { Reply(context, 413, "request_too_large"); return; }
                    byte[] frame;
                    using (Timer bodyDeadline = new Timer(_ => Abort(context), null, bodyTimeout, Timeout.Infinite))
                        frame = await ReadLimited(request.InputStream, ProtocolV3.MaximumLineLength).ConfigureAwait(false);
                    if (stopping) { Reply(context, 503, "stopping"); return; }
                    // Exactly one dispatch. Disconnect/timeout never causes a second dispatch or inferred success.
                    byte[] response = await Task.Run(() => Process(frame)).ConfigureAwait(false);
                    await Write(context, response).ConfigureAwait(false);
                }
                catch (GatewayException error) { Reply(context, error.Status, error.Code); }
                catch (UnauthorizedAccessException) { Reply(context, 401, "invalid_pairing_code"); }
                catch (Exception) { Reply(context, 500, "gateway_error"); }
                finally
                {
                    Abort(context);
                    byte ignored; active.TryRemove(context, out ignored);
                    slots.Release();
                }
            }
        }

        // Pure boundary shared with tests; it never accesses hardware except through the injected coordinator.
        internal byte[] Process(byte[] frame)
        {
            if (frame == null || frame.Length == 0) throw new GatewayException(400, "empty_request");
            if (frame.Length > ProtocolV3.MaximumLineLength) throw new GatewayException(413, "request_too_large");
            Dictionary<string, object> request;
            try
            {
                string raw = new UTF8Encoding(false, true).GetString(frame);
                request = ProtocolV3.Decode(BridgeJson.ParseObject(raw), code, "request");
            }
            catch (UnauthorizedAccessException) { throw; }
            catch { throw new GatewayException(400, "invalid_envelope"); }
            string requestId = BridgeValues.Text(request, "requestId");
            Dictionary<string, object> response;
            try { response = handler(request); }
            catch { response = ProtocolV3.Response(requestId, false, "Не удалось обработать запрос. Сверьте состояние операции в мосте.", "request_failed"); }
            byte[] result = Encoding.UTF8.GetBytes(BridgeJson.Serialize(ProtocolV3.Encode(response, code, "response")));
            if (result.Length > ProtocolV3.MaximumResponseLineLength) throw new GatewayException(502, "response_too_large");
            return result;
        }

        internal static async Task<byte[]> ReadLimited(Stream stream, int maximum)
        {
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)).ConfigureAwait(false)) > 0)
                {
                    if (count > maximum - output.Length) throw new GatewayException(413, "request_too_large");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        private static async Task Write(HttpListenerContext context, byte[] body)
        {
            Headers(context, 200); context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            context.Response.Close();
        }
        private static void Headers(HttpListenerContext context, int status)
        {
            context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.KeepAlive = false;
        }
        private static void Reply(HttpListenerContext context, int status, string code)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes("{\"ok\":false,\"errorCode\":\"" + code + "\"}");
                Headers(context, status); context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body, 0, body.Length); context.Response.Close();
            }
            catch { Abort(context); }
        }
        private static void Abort(HttpListenerContext context) { try { context.Response.Abort(); } catch { } }
        public void Dispose()
        {
            stopping = true;
            HttpListener current = listener; listener = null;
            if (current != null) current.Close();
            foreach (HttpListenerContext context in active.Keys) Abort(context);
        }
        internal sealed class GatewayException : IOException
        {
            public readonly int Status; public readonly string Code;
            public GatewayException(int status, string code) : base(code) { Status = status; Code = code; }
        }
    }
}
