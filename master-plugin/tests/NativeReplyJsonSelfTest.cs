// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Streletz.MasterBridge
{
    internal static class NativeReplyJsonSelfTest
    {
        private static int assertions;
        private static void Check(bool value, string name)
        { if (!value) throw new Exception(name); assertions++; }

        private static int Main()
        {
            foreach (int count in new[] { 453, 445 })
            {
                var panels = new List<object>();
                for (int offset = 0; offset < count; offset += 10)
                {
                    panels.Add(new Dictionary<string, object> {
                        { "panelId", Guid.NewGuid().ToString("D") }, { "nativeNodeGuid", Guid.NewGuid().ToString("D") },
                        { "segmentGuid", Guid.NewGuid().ToString("D") }, { "moduleId", "AABBCCDDEEFF" },
                        { "host", "192.0.2.1" }, { "port", 6344 },
                        { "displayName", "Панель проекта " + new string('П', 90) + offset },
                        { "recordIds", Enumerable.Range(offset, Math.Min(10, count - offset)).Select(x =>
                            "00000000-0000-4000-8000-" + x.ToString("D12")).ToArray() }
                    });
                }
                var reply = new Dictionary<string, object> {
                    { "ok", true }, { "protocol", "streletz-master-plugin-v1" },
                    { "data", new Dictionary<string, object> { { "panels", panels.ToArray() }, { "projectFingerprint", new string('a', 64) } } }
                };
                bool oldRejected = false;
                try { new JavaScriptSerializer { MaxJsonLength = 32768 }.Serialize(reply); }
                catch (Exception error) when (IsLimitException(error)) { oldRejected = true; }
                Check(oldRejected, "The actual request-sized serializer must reproduce the old catalogue failure");
                string encoded = NativeReplyJson.Serialize(reply);
                Check(encoded.Length > 32768, "Fixture must cross the old response limit");
                Check(Encoding.UTF8.GetByteCount(encoded) + 1 <= NativeReplyJson.MaximumResponseBytes, "Native pipe byte budget exceeded");
                var restored = new JavaScriptSerializer { MaxJsonLength = NativeReplyJson.MaximumResponseJsonCharacters }
                    .DeserializeObject(encoded) as Dictionary<string, object>;
                Check((bool)restored["ok"], "Large successful reply became an error");
                var data = (Dictionary<string, object>)restored["data"];
                object[] returned = (object[])data["panels"];
                int members = returned.Cast<Dictionary<string, object>>().Sum(p => ((object[])p["recordIds"]).Length);
                Check(members == count, "Native reply round trip lost catalogue members");
                Console.WriteLine("PASS actual JavaScriptSerializer: " + count + " records, " + encoded.Length + " JSON characters, " + Encoding.UTF8.GetByteCount(encoded) + " UTF-8 bytes");
            }
            string excessive = NativeReplyJson.Serialize(new Dictionary<string, object> {
                { "privateMarker", "must-not-appear-in-error" }, { "text", new string('Ж', NativeReplyJson.MaximumResponseBytes / 2 + 20) }
            });
            Check(excessive.Contains("native_response_too_large") && !excessive.Contains("must-not-appear-in-error"),
                "Oversized UTF-8 reply must become a small error without its payload");
            Check(Encoding.UTF8.GetByteCount(excessive) < 1024, "Fallback error must remain bounded");
            bool requestRejected = false;
            try { new JavaScriptSerializer { MaxJsonLength = NativeReplyJson.MaximumRequestCharacters }
                .DeserializeObject("{\"text\":\"" + new string('x', 32768) + "\"}"); }
            catch (ArgumentException) { requestRejected = true; }
            catch (InvalidOperationException) { requestRejected = true; }
            Check(requestRejected, "Increasing reply capacity must not enlarge the request limit");
            Console.WriteLine("NativeReplyJsonSelfTest: " + assertions + " assertions passed; no UI or equipment used");
            return 0;
        }

        private static bool IsLimitException(Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            return error is InvalidOperationException || error is ArgumentException;
        }
    }
}
