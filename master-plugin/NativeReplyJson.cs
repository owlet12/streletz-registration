// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Streletz.MasterBridge
{
    // Request parsing remains bounded separately at 32 KiB. A project catalogue
    // can legitimately be larger; never reuse the request serializer for replies.
    internal static class NativeReplyJson
    {
        internal const int MaximumRequestCharacters = 32768;
        internal const int MaximumResponseBytes = 4 * 1024 * 1024;
        internal const int MaximumResponseJsonCharacters = MaximumResponseBytes - 1;

        internal static string Serialize(object reply)
        {
            var serializer = new JavaScriptSerializer {
                MaxJsonLength = MaximumResponseJsonCharacters,
                RecursionLimit = 64
            };
            try
            {
                string json = serializer.Serialize(reply);
                if (Encoding.UTF8.GetByteCount(json) + 1 <= MaximumResponseBytes) return json;
            }
            catch (InvalidOperationException) { return Failure(serializer, "native_response_serialization_failed"); }
            catch (ArgumentException) { return Failure(serializer, "native_response_serialization_failed"); }
            catch (TargetInvocationException) { return Failure(serializer, "native_response_serialization_failed"); }
            // A bounded error is preferable to closing the pipe without a reply.
            // It contains no project fields or partial serialized payload.
            return Failure(serializer, "native_response_too_large");
        }

        private static string Failure(JavaScriptSerializer serializer, string code)
        {
            return serializer.Serialize(new Dictionary<string, object> {
                { "ok", false }, { "protocol", "streletz-master-plugin-v1" },
                { "error", new Dictionary<string, object> {
                    { "code", code },
                    { "message", "The native reply could not be serialized within the bounded response limit." }
                } }
            });
        }
    }
}
