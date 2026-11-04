// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StreletzBridge
{
    internal static class BfxProjectUpdateSelfTests
    {
        private const string First = "11111111-1111-4111-8111-111111111111";
        private const string Second = "22222222-2222-4222-8222-222222222222";
        private const string Ib1 = "33333333-3333-4333-8333-333333333333";
        private const string Header = "44444444-4444-4444-8444-444444444444";
        private const string Panel = "55555555-5555-4555-8555-555555555555";
        private const string OriginalOperation = "66666666-6666-4666-8666-666666666666";
        private static int checks;

        private static void Check(bool value, string message)
        { checks++; if (!value) throw new Exception(message); }

        private static Dictionary<string, object> Row(string id, string type, int stage, string serial)
        {
            return new Dictionary<string, object> {
                { "id", id }, { "recordKind", "device" }, { "name", "New source " + type }, { "type", type },
                { "group", type == "ИБ1-ПРО" ? "Исполнительные устройства" : "Пожарные извещатели" },
                { "sourceRow", 1 }, { "description", "Fresh BFX description" }, { "zone", "2" }, { "initialized", stage >= 2 ? "+" : "-" },
                { "programmed", stage >= 2 ? "+" : "-" },
                { "bfx", new Dictionary<string, object> { { "address", "1" }, { "classType", type == "ИБ1-ПРО" ? "StreletcPro.IB1.Info" : "Synthetic.Info" } } },
                { "commissioning", new Dictionary<string, object> { { "stage", stage }, { "stageName", "Source stage" },
                    { "serial", serial }, { "deviceKey", "" }, { "note", "" }, { "updatedAt", 0L } } }
            };
        }

        private static Dictionary<string, object> Project(params Dictionary<string, object>[] records)
        {
            return new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "format", "streletz-portable-project" }, { "transferVersion", 1 }, { "siteName", "Fresh BFX name" },
                { "exportedAt", 200L }, { "source", new Dictionary<string, object> { { "fileName", "new.bfx" }, { "sha256", new string('b', 64) } } },
                { "records", records.Cast<object>().ToArray() },
                { "summary", new Dictionary<string, object> { { "total", 999 }, { "devices", 999 }, { "initialized", 999 },
                    { "groups", new Dictionary<string, object> { { "obsolete", 999 } } } } },
                { "panels", new object[] { new Dictionary<string, object> { { "panelId", Panel }, { "projectPanelId", Panel },
                    { "recordIds", records.Select(r => BridgeValues.Text(r, "id")).ToArray() }, { "available", true },
                    { "verifiedRecordIds", records.Select(r => BridgeValues.Text(r, "id")).ToArray() }, { "host", "192.0.2.10" } } } },
                { "panelCatalogComplete", true }, { "panelCatalogOrigin", "bfx" },
                { "panelCatalogIssues", new object[0] }, { "unresolvedPanelRecordIds", new object[0] }
            };
        }

        private static Dictionary<string, object> Find(Dictionary<string, object> project, string id)
        { return BridgeValues.Array(project, "records").Cast<Dictionary<string, object>>().Single(row => BridgeValues.Text(row, "id").Equals(id, StringComparison.OrdinalIgnoreCase)); }
        private static Dictionary<string, object> Work(Dictionary<string, object> project, string id)
        { return BridgeValues.Object(Find(project, id), "commissioning"); }

        private static void RejectedWithoutMutation(Dictionary<string, object> current, Dictionary<string, object> fresh, string context)
        {
            string original = BridgeJson.Serialize(current), incoming = BridgeJson.Serialize(fresh);
            bool rejected = false;
            try { BfxProjectUpdate.Merge(current, fresh); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, context + " unexpectedly accepted");
            Check(original == BridgeJson.Serialize(current) && incoming == BridgeJson.Serialize(fresh), context + " mutated an input before rejection");
        }

        private static void SupersetPreservesWorkByGuid()
        {
            var current = Project(Row(First, "ИПР-ПРО", 3, "FA0000000001"), Row(Second, "ИПР-ПРО", 1, "FA0000000002"));
            current["siteName"] = "User project title";
            current["rootExtra"] = new Dictionary<string, object> { { "siteNote", new object[] { "preserve root" } } };
            current["panelCatalogNativeFingerprint"] = new string('f', 64);
            BridgeValues.Object(current, "summary")["userCaption"] = "Preserve summary extension";
            var first = Find(current, First);
            first["rowExtra"] = new Dictionary<string, object> { { "label", "Preserve row extension" } };
            first["name"] = "Old source name";
            first["zone"] = "500";
            first["sourceRow"] = 81;
            var work = Work(current, First);
            work["deviceKey"] = "1234ABCD";
            work["note"] = "Commissioning note";
            work["description"] = "User edited description";
            work["updatedAt"] = 1000L;
            work["extra"] = new Dictionary<string, object> { { "nested", new object[] { "unknown work field" } } };
            string oldFingerprint = BridgeValues.ProjectFingerprint(current);
            work["registration"] = new Dictionary<string, object> { { "operationId", OriginalOperation }, { "projectFingerprint", oldFingerprint },
                { "state", "unknown" }, { "registrationConfirmed", false }, { "history", new object[] {
                    new Dictionary<string, object> { { "operationId", OriginalOperation }, { "manualReview", "preserved historical value" } }
                } } };
            Work(current, Second)["description"] = "";
            var fresh = Project(Row(Ib1, "ИБ1-ПРО", 0, ""), Row(Second, "ИПР-ПРО", 0, "FA0000000002"), Row(First, "ИПР-ПРО", 2, "FA0000000001"));
            Find(fresh, First)["sourceRow"] = 3;
            string before = BridgeJson.Serialize(current), sourceBefore = BridgeJson.Serialize(fresh);
            var merged = BfxProjectUpdate.Merge(current, fresh);
            Check(BridgeValues.Array(merged, "records").Length == 3 && BridgeValues.Text(Find(merged, Ib1), "type") == "ИБ1-ПРО", "new IB1 row missing");
            Check(BridgeValues.Text(Find(merged, Ib1), "group") == "Исполнительные устройства", "IB1 source group changed");
            Check(BridgeValues.Number(Work(merged, First), "stage") == 3 && BridgeValues.Text(Work(merged, First), "stageName") == "Проверено", "verified same-ID stage lost");
            Check(BridgeValues.Text(Work(merged, First), "deviceKey") == "1234ABCD" && BridgeValues.Text(Work(merged, First), "note") == "Commissioning note", "KEY or note overwritten by empty BFX values");
            Check(BridgeValues.Text(Find(merged, First), "description") == "User edited description" && BridgeValues.Text(Find(merged, Second), "description") == "", "explicit description, including empty, lost");
            Check(BridgeValues.Number(Work(merged, Second), "stage") == 1 && BridgeValues.Text(Work(merged, Second), "serial") == "FA0000000002", "GUID merge incorrectly used row order");
            Check(BridgeValues.Text(Find(merged, First), "name") == "New source ИПР-ПРО" && BridgeValues.Text(Find(merged, First), "zone") == "2"
                && BridgeValues.Number(Find(merged, First), "sourceRow") == 3, "fresh source metadata not refreshed");
            Check(BridgeValues.Text(merged, "siteName") == "User project title" && BridgeValues.Text(BridgeValues.Object(merged, "source"), "fileName") == "new.bfx", "user title or fresh source lost");
            Check(!merged.ContainsKey("panelCatalogNativeFingerprint"), "stale live fingerprint survived source update");
            Check(BridgeValues.Text(BridgeValues.Object(Work(merged, First), "registration"), "projectFingerprint") == oldFingerprint, "historical operation identity was rebound to new project fingerprint");
            Check(BridgeJson.Serialize(BridgeValues.Object(Work(merged, First), "registration")) == BridgeJson.Serialize(BridgeValues.Object(work, "registration")), "registration history changed");
            Check(BridgeValues.Number(BridgeValues.Object(merged, "summary"), "devices") == 3 && BridgeValues.Number(BridgeValues.Object(merged, "summary"), "initialized") == 1
                && BridgeValues.Number(BridgeValues.Object(merged, "summary"), "checked") == 1, "summary not rebuilt from preserved effective stages");
            Check(BridgeValues.Text(BridgeValues.Object(merged, "summary"), "userCaption") == "Preserve summary extension", "unknown summary metadata lost");
            var panel = (Dictionary<string, object>)BridgeValues.Array(merged, "panels")[0];
            Check(ProjectPanelCatalog.Contains(panel, Ib1) && !BridgeValues.Flag(panel, "available") && BridgeValues.Array(panel, "verifiedRecordIds").Length == 0,
                "fresh IB1 membership missing or file evidence promoted to live readiness");
            Check(BridgeValues.Text(merged, "panelMappingFingerprint") == BridgeValues.ProjectFingerprint(merged)
                && BridgeValues.ProjectFingerprint(merged) != oldFingerprint, "catalog not bound to new GUID set");
            Check(before == BridgeJson.Serialize(current) && sourceBefore == BridgeJson.Serialize(fresh), "successful merge changed source dictionaries");
            BridgeValues.Object(Work(merged, First), "extra")["nested"] = new object[] { "changed result" };
            BridgeValues.Object(merged, "rootExtra")["siteNote"] = "changed result";
            BridgeValues.Object(Find(merged, First), "rowExtra")["label"] = "changed result";
            Check(before == BridgeJson.Serialize(current) && sourceBefore == BridgeJson.Serialize(fresh), "merged unknown nested fields alias input data");
        }

        private static void StageAndIdReconciliation()
        {
            foreach (int oldStage in new[] { 0, 1, 2, 3 })
            foreach (int freshStage in new[] { 0, 1, 2, 3 })
            {
                var current = Project(Row(First, "ИПР-ПРО", oldStage, "FA0000000001"));
                var fresh = Project(Row(First, "ИПР-ПРО", freshStage, "FA0000000001"), Row(Ib1, "ИБ1-ПРО", 0, ""));
                var result = BfxProjectUpdate.Merge(current, fresh);
                Check(BridgeValues.Number(Work(result, First), "stage") == Math.Max(oldStage, freshStage), "same-ID stage regression");
                Check(BridgeValues.Text(Find(result, First), "initialized") == (Math.Max(oldStage, freshStage) >= 2 ? "+" : "-"), "effective initialized flag inconsistent");
            }
            foreach (int freshStage in new[] { 2, 3 })
                RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 1, "FA0000000001")),
                    Project(Row(First, "ИПР-ПРО", freshStage, "FA0000000002")), "initialized hardware ID conflict");
            foreach (int oldStage in new[] { 0, 1, 3 })
            foreach (int freshStage in new[] { 0, 1 })
            {
                var current = Project(Row(First, "ИПР-ПРО", oldStage, "FA0000000001"));
                Work(current, First)["deviceKey"] = "1234ABCD";
                var result = BfxProjectUpdate.Merge(current, Project(Row(First, "ИПР-ПРО", freshStage, "FA0000000002")));
                Check(BridgeValues.Number(Work(result, First), "stage") == oldStage && BridgeValues.Text(Work(result, First), "serial") == "FA0000000001"
                    && BridgeValues.Text(Work(result, First), "deviceKey") == "1234ABCD", "unconfirmed different source ID replaced existing work or promoted stage");
            }
            var empty = Project(Row(First, "ИПР-ПРО", 1, ""));
            Work(empty, First)["deviceKey"] = "1234ABCD";
            var filled = BfxProjectUpdate.Merge(empty, Project(Row(First, "ИПР-ПРО", 2, "FA0000000001")));
            Check(BridgeValues.Text(Work(filled, First), "serial") == "FA0000000001" && BridgeValues.Number(Work(filled, First), "stage") == 2
                && BridgeValues.Text(Work(filled, First), "deviceKey") == "1234ABCD", "empty serial not filled without losing current KEY");
            var caseOnly = Project(Row(First, "ИПР-ПРО", 3, "fa0000000001"));
            var caseResult = BfxProjectUpdate.Merge(caseOnly, Project(Row(First, "ИПР-ПРО", 2, "FA0000000001")));
            Check(BridgeValues.Number(Work(caseResult, First), "stage") == 3 && BridgeValues.Text(Work(caseResult, First), "serial") == "fa0000000001", "case-only serial difference treated as conflict or edited old value");
        }

        private static void RefuseWrongIncompleteAndMalformedSources()
        {
            var old = Project(Row(First, "ИПР-ПРО", 1, ""), Row(Second, "ИПР-ПРО", 0, ""));
            RejectedWithoutMutation(old, Project(Row(First, "ИПР-ПРО", 0, "")), "missing old device");
            RejectedWithoutMutation(old, Project(Row(Ib1, "ИБ1-ПРО", 0, "")), "foreign project");
            RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 0, "")), Project(Row(First, "ИПР-ПРО", 0, ""), Row(First.ToUpperInvariant(), "ИБ1-ПРО", 0, "")), "duplicate source GUID");
            RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 0, ""), Row(First.ToUpperInvariant(), "ИПР-ПРО", 0, "")), Project(Row(First, "ИПР-ПРО", 0, "")), "duplicate current GUID");
            var wrongKind = Project(Row(First, "ИПР-ПРО", 0, ""));
            Find(wrongKind, First)["recordKind"] = "metadata";
            RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 1, "")), wrongKind, "old device became nondevice");
            var malformed = Project(Row(First, "ИПР-ПРО", 0, ""));
            Work(malformed, First)["stage"] = "unknown-stage";
            RejectedWithoutMutation(malformed, Project(Row(First, "ИПР-ПРО", 0, "")), "invalid current stage");
            malformed = Project(Row(First, "ИПР-ПРО", 0, ""));
            Find(malformed, First)["commissioning"] = "not-an-object";
            RejectedWithoutMutation(malformed, Project(Row(First, "ИПР-ПРО", 0, "")), "malformed journal object");
            malformed = Project(Row(First, "ИПР-ПРО", 0, ""));
            Find(malformed, First)["id"] = "not-a-guid";
            RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 0, "")), malformed, "malformed source GUID");
            malformed = Project(Row(First, "ИПР-ПРО", 0, ""));
            malformed["schemaVersion"] = 2;
            RejectedWithoutMutation(Project(Row(First, "ИПР-ПРО", 0, "")), malformed, "unknown source schema");
        }

        private static void PreserveMetadataAndLegacyJournal()
        {
            var old = Project(Row(First, "ИПР-ПРО", 2, ""));
            Find(old, First).Remove("commissioning");
            old["records"] = new object[] { Find(old, First), new Dictionary<string, object> {
                { "id", Header }, { "recordKind", "metadata" }, { "description", "Portable-only annotation" }, { "payload", new object[] { "keep" } }
            } };
            var fresh = Project(Row(First, "ИПР-ПРО", 0, ""), Row(Ib1, "ИБ1-ПРО", 0, ""));
            var result = BfxProjectUpdate.Merge(old, fresh);
            Check(BridgeValues.Number(Work(result, First), "stage") == 2, "legacy initialized flag lost without commissioning object");
            Check(BridgeValues.Array(result, "records").Length == 3 && BridgeValues.Text(Find(result, Header), "description") == "Portable-only annotation", "unmatched nondevice metadata silently dropped");
            Check(BridgeValues.Number(BridgeValues.Object(result, "summary"), "devices") == 2 && BridgeValues.Number(BridgeValues.Object(result, "summary"), "total") == 3,
                "summary confused metadata rows and devices");
            Check(BridgeValues.Text(result, "panelMappingFingerprint") == BridgeValues.ProjectFingerprint(result), "metadata-preserving catalog fingerprint invalid");
            fresh.Remove("panels");
            var noPanels = BfxProjectUpdate.Merge(old, fresh);
            Check(BridgeValues.Array(noPanels, "panels").Length == 0 && !BridgeValues.Flag(noPanels, "panelCatalogComplete"), "stale panel catalog reused after missing fresh catalog");
        }

        internal static void Run()
        {
            checks = 0;
            SupersetPreservesWorkByGuid();
            StageAndIdReconciliation();
            RefuseWrongIncompleteAndMalformedSources();
            PreserveMetadataAndLegacyJournal();
            Console.WriteLine("PASS BFX project update checks: " + checks + "; pure GUID merge, no file or hardware writes");
        }
    }
}
