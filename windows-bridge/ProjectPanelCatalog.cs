// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StreletzBridge
{
    // The source reads the already authenticated native project. It never starts
    // an InitDevice command merely to obtain panel names or membership.
    internal interface IProjectPanelSource
    {
        Dictionary<string, object> ReadProjectPanels();
    }

    internal static class ProjectPanelCatalog
    {
        public static bool IsComplete(Dictionary<string, object> project)
        {
            return project != null && BridgeValues.Flag(project, "panelCatalogComplete") &&
                BridgeValues.Text(project, "panelMappingFingerprint") == BridgeValues.ProjectFingerprint(project);
        }

        public static string StableId(string nativePanelId, string segmentId)
        {
            string native = BridgeValues.GuidValue(nativePanelId, "панель проекта");
            string segment = BridgeValues.GuidValue(segmentId, "сегмент проекта");
            byte[] digest;
            using (SHA256 hash = SHA256.Create())
                digest = hash.ComputeHash(Encoding.UTF8.GetBytes("streletz-project-panel-v1\n" + native + "\n" + segment));
            byte[] guid = digest.Take(16).ToArray();
            return new Guid(guid).ToString("D");
        }

        public static Dictionary<string, object>[] FromNative(Dictionary<string, object> listing, Dictionary<string, object> project)
        {
            string fingerprint = BridgeValues.Text(listing, "nativeProjectFingerprint");
            if (fingerprint.Length == 0) fingerprint = BridgeValues.Text(listing, "projectFingerprint");
            if (!Regex.IsMatch(fingerprint, "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Не получен отпечаток проекта Стрелец-Мастера.");
            object[] raw = BridgeValues.Array(listing, "panels");
            if (raw == null) throw new InvalidDataException("Стрелец-Мастер не вернул список панелей.");
            HashSet<string> records = RecordIds(project);
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var panels = new List<Dictionary<string, object>>();
            foreach (object value in raw)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                if (item == null) throw new InvalidDataException("Некорректная запись панели проекта.");
                string native = BridgeValues.GuidValue(BridgeValues.Text(item, "nativeNodeGuid"), "панель проекта");
                string segment = BridgeValues.GuidValue(BridgeValues.Text(item, "segmentGuid"), "сегмент проекта");
                string id = StableId(native, segment);
                if (!ids.Add(id)) throw new InvalidDataException("В проекте повторяется идентификатор панели и сегмента.");
                IPAddress host;
                string module = BridgeValues.Text(item, "moduleId").ToUpperInvariant();
                int port = (int)BridgeValues.Number(item, "port");
                if (!IPAddress.TryParse(BridgeValues.Text(item, "host"), out host) || port < 1 || port > 65535 ||
                    !Regex.IsMatch(module, "^[0-9A-F]{12}$") || module == "000000000000" || module == "FFFFFFFFFFFF")
                    throw new InvalidDataException("В проекте отсутствует однозначный маршрут МК-IP.");
                string[] membership = ReadMembership(item, records);
                // A catalog for this portable project must not include a panel
                // belonging only to an unrelated native project.
                if (membership.Length == 0) continue;
                string name = BridgeValues.Text(item, "displayName").Trim();
                bool hasName = name.Length > 0;
                if (name.Length == 0) name = "Панель " + native.Substring(0, 8);
                if (name.Length > 100) name = name.Substring(0, 100);
                var panel = new Dictionary<string, object> {
                    { "panelId", id }, { "projectPanelId", id }, { "nativePanelId", native }, { "segmentId", segment },
                    { "name", name }, { "nameSource", hasName ? "native_name" : "native_id" }, { "host", host.ToString() }, { "port", port }, { "moduleId", module },
                    { "recordIds", membership }, { "verifiedRecordIds", membership }, { "source", "project" }, { "origin", "native_project" }, { "available", false },
                    { "availableReason", "Требуется проверка связи со Стрелец-Мастером." }
                };
                PanelRouteMetadata.CopyNative(item, panel);
                panels.Add(panel);
            }
            if (panels.Count == 0)
                throw new InvalidDataException(raw.Length == 0
                    ? "В открытом проекте не найдены поддержанные панели с настроенным маршрутом МК-IP."
                    : "Принадлежность записей к панелям не подтверждена. Сверьте проект в Стрелец-Мастере и Windows-мосте.");
            return panels.OrderBy(p => BridgeValues.Text(p, "name"), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => BridgeValues.Text(p, "projectPanelId"), StringComparer.Ordinal).ToArray();
        }

        public static Dictionary<string, object>[] Cached(Dictionary<string, object> project)
        {
            if (project == null) return new Dictionary<string, object>[0];
            string bound = BridgeValues.Text(project, "panelMappingFingerprint");
            if (bound.Length > 0 && bound != BridgeValues.ProjectFingerprint(project)) return new Dictionary<string, object>[0];
            HashSet<string> records = RecordIds(project);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Dictionary<string, object>>();
            foreach (object value in BridgeValues.Array(project, "panels") ?? new object[0])
            {
                var row = value as Dictionary<string, object>;
                if (row == null) continue;
                string id = BridgeValues.Text(row, "projectPanelId");
                if (id.Length == 0) id = BridgeValues.Text(row, "panelId");
                Guid parsed;
                if (!Guid.TryParse(id, out parsed) || parsed == Guid.Empty || !ids.Add(parsed.ToString("D"))) continue;
                string[] membership;
                try { membership = ReadMembership(row, records); } catch (InvalidDataException) { continue; }
                var panel = BridgeValues.Copy(row);
                panel["panelId"] = parsed.ToString("D"); panel["projectPanelId"] = parsed.ToString("D");
                panel["recordIds"] = membership;
                panel["verifiedRecordIds"] = new object[0];
                panel["available"] = false;
                panel["availableReason"] = "Сохранённая принадлежность из проекта; подтвердите связь со Стрелец-Мастером.";
                result.Add(panel);
            }
            return result.ToArray();
        }

        public static Dictionary<string, object>[] Merge(Dictionary<string, object>[] saved, Dictionary<string, object>[] verified)
        {
            var liveById = verified.ToDictionary(p => BridgeValues.Text(p, "projectPanelId"), StringComparer.OrdinalIgnoreCase);
            var liveRecords = new HashSet<string>(verified.SelectMany(p => BridgeValues.Array(p, "recordIds") ?? new object[0])
                .Select(Convert.ToString), StringComparer.OrdinalIgnoreCase);
            var result = new List<Dictionary<string, object>>();
            foreach (var previous in saved)
            {
                string id = BridgeValues.Text(previous, "projectPanelId");
                Dictionary<string, object> fresh;
                string[] unverified = (BridgeValues.Array(previous, "recordIds") ?? new object[0]).Select(Convert.ToString)
                    .Where(record => !liveRecords.Contains(record)).ToArray();
                if (liveById.TryGetValue(id, out fresh))
                {
                    var merged = BridgeValues.Copy(previous);
                    foreach (var field in fresh) merged[field.Key] = field.Value;
                    if (BridgeValues.Text(fresh, "nameSource") == "native_id" && BridgeValues.Text(previous, "name").Length > 0)
                        merged["name"] = BridgeValues.Text(previous, "name");
                    merged["recordIds"] = (BridgeValues.Array(fresh, "recordIds") ?? new object[0]).Select(Convert.ToString)
                        .Concat(unverified).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(record => record, StringComparer.Ordinal).ToArray();
                    result.Add(merged); liveById.Remove(id);
                }
                else
                {
                    var cached = BridgeValues.Copy(previous);
                    cached["recordIds"] = unverified; cached["verifiedRecordIds"] = new object[0];
                    cached["available"] = false;
                    result.Add(cached);
                }
            }
            result.AddRange(liveById.Values.Select(BridgeValues.Copy));
            return result.OrderBy(p => BridgeValues.Text(p, "name"), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => BridgeValues.Text(p, "projectPanelId"), StringComparer.Ordinal).ToArray();
        }

        public static bool VerifiedContains(Dictionary<string, object> panel, string recordId)
        {
            return (BridgeValues.Array(panel, "verifiedRecordIds") ?? new object[0]).Any(value =>
                String.Equals(Convert.ToString(value), recordId, StringComparison.OrdinalIgnoreCase));
        }

        public static void Apply(Dictionary<string, object> project, Dictionary<string, object>[] panels, string nativeFingerprint = null)
        {
            if (!IsComplete(project)) project["panelCatalogComplete"] = false;
            project["panelMappingVersion"] = 1;
            project["panelMappingFingerprint"] = BridgeValues.ProjectFingerprint(project);
            if (!String.IsNullOrEmpty(nativeFingerprint)) project["panelCatalogNativeFingerprint"] = nativeFingerprint;
            project["panels"] = panels.Select(p => {
                var copy = BridgeValues.Copy(p);
                copy["available"] = false;
                copy["availableReason"] = "Сохранённая принадлежность из проекта; требуется контрольная связь.";
                copy.Remove("activeOperationId");
                copy["verifiedRecordIds"] = new object[0];
                return (object)copy;
            }).ToArray();
            foreach (object value in BridgeValues.Array(project, "records") ?? new object[0])
            {
                var row = value as Dictionary<string, object>;
                if (row == null) continue;
                string record = BridgeValues.Text(row, "id");
                var owners = panels.Where(p => Contains(p, record)).ToArray();
                row["projectPanelId"] = owners.Length == 1 ? BridgeValues.Text(owners[0], "projectPanelId") : "";
                row["description"] = EffectiveDescription(row);
            }
        }

        public static string EffectiveDescription(Dictionary<string, object> record)
        {
            var commissioning = BridgeValues.Object(record, "commissioning");
            return commissioning != null && commissioning.ContainsKey("description")
                ? BridgeValues.Text(commissioning, "description") : BridgeValues.Text(record, "description");
        }

        public static object[] RecordMetadata(Dictionary<string, object> project)
        {
            return (BridgeValues.Array(project, "records") ?? new object[0]).OfType<Dictionary<string, object>>()
                .Select(row => (object)new Dictionary<string, object> { { "recordId", BridgeValues.Text(row, "id") },
                    { "description", EffectiveDescription(row) } }).ToArray();
        }

        public static bool Contains(Dictionary<string, object> panel, string recordId)
        {
            return (BridgeValues.Array(panel, "recordIds") ?? new object[0]).Any(value =>
                String.Equals(Convert.ToString(value), recordId, StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> RecordIds(Dictionary<string, object> project)
        {
            return new HashSet<string>((BridgeValues.Array(project, "records") ?? new object[0])
                .OfType<Dictionary<string, object>>().Select(r => BridgeValues.Text(r, "id")), StringComparer.OrdinalIgnoreCase);
        }

        private static string[] ReadMembership(Dictionary<string, object> panel, HashSet<string> knownRecords)
        {
            object[] ids = BridgeValues.Array(panel, "recordIds");
            if (ids == null) throw new InvalidDataException("В панели отсутствует принадлежность записей проекта.");
            return ids.Select(id => BridgeValues.GuidValue(Convert.ToString(id), "запись панели"))
                .Where(knownRecords.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }
}
