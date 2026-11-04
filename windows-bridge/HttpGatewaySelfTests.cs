// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StreletzBridge
{
    internal static class HttpGatewaySelfTests
    {
        private const string Code = "1234567890ABCDEF";
        public static void Envelope()
        {
            int calls = 0;
            string id = Guid.NewGuid().ToString("D");
            var request = new Dictionary<string, object> { { "protocol", 3 }, { "requestId", id }, { "type", "get_project" }, { "sentAt", BridgeValues.NowMs() } };
            using (var gateway = new HttpGateway(Code, value => { calls++; return ProtocolV3.Response(BridgeValues.Text(value, "requestId"), true, "synthetic"); }))
            {
                byte[] frame = Encoding.UTF8.GetBytes(BridgeJson.Serialize(ProtocolV3.Encode(request, Code, "request")));
                var decoded = ProtocolV3.Decode(BridgeJson.ParseObject(Encoding.UTF8.GetString(gateway.Process(frame))), Code, "response");
                Assert(BridgeValues.Text(decoded, "requestId") == id && BridgeValues.Flag(decoded, "ok") && calls == 1, "HTTP lost authenticated identity or dispatched twice");
                foreach (byte[] invalid in new[] { new byte[0], new byte[65537], new byte[] { 255, 254 }, Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{\"protocol\":2}") })
                    Reject(() => gateway.Process(invalid));
                var wrong = Encoding.UTF8.GetBytes(BridgeJson.Serialize(ProtocolV3.Encode(request, "FEDCBA0987654321", "request")));
                Reject(() => gateway.Process(wrong));
                var tamper = BridgeJson.ParseObject(Encoding.UTF8.GetString(frame)); tamper["mac"] = Convert.ToBase64String(new byte[32]);
                Reject(() => gateway.Process(Encoding.UTF8.GetBytes(BridgeJson.Serialize(tamper))));
                Assert(calls == 1, "invalid HTTP envelope reached coordinator");
            }
            int failures = 0;
            using (var gateway = new HttpGateway(Code, value => { failures++; throw new IOException("private secret must not escape"); }))
            {
                byte[] frame = Encoding.UTF8.GetBytes(BridgeJson.Serialize(ProtocolV3.Encode(request, Code, "request")));
                var response = ProtocolV3.Decode(BridgeJson.ParseObject(Encoding.UTF8.GetString(gateway.Process(frame))), Code, "response");
                Assert(!BridgeValues.Flag(response, "ok") && BridgeValues.Text(response, "requestId") == id && failures == 1, "handler error lost identity or retried");
                Assert(!BridgeJson.Serialize(response).Contains("private secret"), "exception leaked private content");
            }
            Assert(HttpGateway.ReadLimited(new MemoryStream(new byte[65536]), 65536).GetAwaiter().GetResult().Length == 65536, "boundary request rejected");
            Reject(() => HttpGateway.ReadLimited(new MemoryStream(new byte[65537]), 65536).GetAwaiter().GetResult());
        }
        public static void Settings()
        {
            foreach (int port in new[] { 0, 80, 443, 23019, 65536 }) Reject(() => HttpGateway.ValidatePort(port));
            Assert(HttpGateway.Prefix(23020) == "http://+:23020/streletz-bridge/", "wrong default HTTP port/route");
            Assert(HttpGatewaySettings.NormalizeAddress("strelec.example.net") == "https://strelec.example.net/streletz-bridge/", "wrong APK address");
            Assert(HttpGatewaySettings.NormalizeAddress("https://strelec.example.net:8443/") == "https://strelec.example.net:8443/streletz-bridge/", "external HTTPS port lost");
            foreach (string value in new[] { "http://example.net", "https://user:password@example.net", "https://example.net/?token=secret", "https://example.net/other", "https://example.net/#hash" })
                Reject(() => HttpGatewaySettings.NormalizeAddress(value));
            string directory = Path.Combine(Path.GetTempPath(), "gateway-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "http-settings.json");
            try
            {
                Assert(!HttpGatewaySettings.Load(file).Enabled, "HTTP enabled on a new computer without user choice");
                new HttpGatewaySettings { Enabled = true, Port = 23020, Address = "https://example.net" }.Save(file);
                var saved = HttpGatewaySettings.Load(file);
                Assert(saved.Enabled && saved.Port == 23020 && saved.Address.EndsWith(HttpGateway.Route), "settings roundtrip failed");
                Assert(!File.ReadAllText(file).Contains(Code), "pairing secret copied into gateway settings");
                saved.Enabled = false; saved.Save(file); Assert(!HttpGatewaySettings.Load(file).Enabled, "disable was not saved");
                File.WriteAllText(file, "{\"version\":1,\"enabled\":true,\"port\":23019}");
                Reject(() => HttpGatewaySettings.Load(file));
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void Reject(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("invalid input accepted"); }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
