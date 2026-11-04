// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StreletzBridge
{
    // Transfers only a portable project already opened in the bridge. Reading a
    // snapshot never probes Master, authenticates hardware or starts a command.
    internal static class ProjectSyncSnapshot
    {
        // Immutable prepared snapshot; callers receive copies, never the cached dictionary.
        internal sealed class Prepared
        {
            private readonly Dictionary<string, object> snapshot;
            private readonly string fingerprint, revision;
            internal Prepared(Dictionary<string, object> project)
            {
                if (project == null)
                    throw new RegistrationRequestException("project_missing", "Откройте проект в Windows-мосте: он автоматически загрузится на телефон.");
                if (BridgeValues.Number(project, "schemaVersion") != 1 || BridgeValues.Array(project, "records") == null)
                    throw new RegistrationRequestException("project_invalid", "Открытый проект не поддерживает передачу на телефон.");
                snapshot = BridgeValues.Copy(project);
                fingerprint = BridgeValues.ProjectFingerprint(snapshot);
                if (snapshot.ContainsKey("panels")) snapshot["panels"] = ProjectPanelCatalog.Cached(snapshot);
                revision = ContentRevision(snapshot);
            }

            internal Dictionary<string, object> Reply(string requestId, string knownFingerprint, string knownRevision)
            {
                bool unchanged = String.Equals(knownFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(knownRevision, revision, StringComparison.Ordinal);
                var response = ProtocolV3.Response(requestId, true,
                    unchanged ? "На телефоне актуальная версия проекта Windows-моста." : "Проект Windows-моста передан на телефон.");
                response["projectSource"] = "windows_bridge";
                response["projectFingerprint"] = fingerprint;
                response["projectRevision"] = revision;
                response["projectName"] = BridgeValues.Text(snapshot, "siteName");
                response["recordCount"] = BridgeValues.Array(snapshot, "records").Length;
                response["notModified"] = unchanged;
                if (!unchanged) response["project"] = BridgeValues.Copy(snapshot);
                if (Encoding.UTF8.GetByteCount(BridgeJson.Serialize(response)) > ProtocolV3.MaximumResponsePlaintext)
                    throw new RegistrationRequestException("project_too_large", "Проект превышает размер автоматической передачи. Сохраните копию и откройте её на телефоне.");
                return response;
            }
        }

        public static Dictionary<string, object> Reply(Dictionary<string, object> project,
            string requestId, string knownFingerprint, string knownRevision)
        {
            return new Prepared(project).Reply(requestId, knownFingerprint, knownRevision);
        }

        // Content based, so a bridge restart cannot reuse an old revision for a
        // changed project. Dictionary order is irrelevant; record order is not.
        internal static string ContentRevision(Dictionary<string, object> project)
        {
            var canonical = new StringBuilder("streletz-project-content-v1\n");
            AppendCanonical(canonical, project);
            using (SHA256 hash = SHA256.Create())
                return BridgeValues.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString())));
        }

        private static void AppendCanonical(StringBuilder text, object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                text.Append('{');
                bool first = true;
                foreach (string key in map.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(BridgeJson.Serialize(key)).Append(':');
                    AppendCanonical(text, map[key]);
                }
                text.Append('}');
                return;
            }
            if (value is IEnumerable && !(value is string))
            {
                text.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) text.Append(',');
                    first = false;
                    AppendCanonical(text, item);
                }
                text.Append(']');
                return;
            }
            text.Append(BridgeJson.Serialize(value));
        }
    }
}
