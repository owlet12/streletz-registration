// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.util.HashMap;
import java.util.Map;

/** Production snapshot, merge and file-transaction checks. No Android UI, Windows session or hardware. */
public final class ProjectTransferSelfTest {
    private static int assertions;
    private static final String A = "00000000-0000-4000-8000-000000000001";
    private static final String B = "00000000-0000-4000-8000-000000000002";
    private static final String C = "00000000-0000-4000-8000-000000000003";

    public static void main(String[] args) throws Exception {
        JSONObject original = project(record(A, "Ручной извещатель", "-"), record(B, "Ручной извещатель", "+"));
        String originalFingerprint = ProjectTransfer.fingerprint(original);
        JSONArray panels = new JSONArray().put(new JSONObject().put("projectPanelId", "main-panel")
                .put("name", "Панель 2.1").put("routeKind", "panel_terminal").put("viaPanelTerminal", true)
                .put("terminalPanelId", "terminal-32").put("terminalDisplayName", "32.1 Панель-Терминал")
                .put("host", "192.0.2.32").put("port", 6344).put("recordIds", new JSONArray().put(A))
                .put("available", true).put("verifiedRecordIds", new JSONArray().put(A)));
        original.put("panels", panels);
        JSONObject answer = response(original, "revision-1");
        ProjectTransfer.Snapshot accepted = ProjectTransfer.response(answer, "", "");
        check(accepted.fingerprint.equals(originalFingerprint), "authenticated project fingerprint matches actual full record set");
        check(accepted.project.getJSONArray("records").length() == 2, "all received records are loaded");
        JSONObject panel = accepted.project.getJSONArray("panels").getJSONObject(0);
        check(panel.getString("terminalPanelId").equals("terminal-32"), "terminal route is retained as metadata");
        check(panel.getString("name").equals("Панель 2.1"), "target panel is not replaced by a terminal");
        check(!panel.has("available") && !panel.has("verifiedRecordIds"), "downloaded project cannot grant live registration access");
        check(answer.getJSONObject("project").getJSONArray("panels").getJSONObject(0).has("available"), "validation does not mutate source response");

        JSONObject unchanged = new JSONObject().put("ok", true).put("projectSource", "windows_bridge")
                .put("projectFingerprint", originalFingerprint).put("projectRevision", "revision-1").put("notModified", true);
        check(ProjectTransfer.response(unchanged, originalFingerprint, "revision-1").notModified,
                "matching conditional request accepts notModified without a project body");
        rejects(() -> ProjectTransfer.response(unchanged, originalFingerprint, "other"), "stale revision cannot confirm a different local snapshot");
        rejects(() -> ProjectTransfer.response(unchanged, "", "revision-1"), "another project cannot satisfy notModified");
        rejects(() -> ProjectTransfer.response(new JSONObject(answer.toString()).put("projectFingerprint", "0".repeat(64)), "", ""),
                "mismatched full project fingerprint rejected before installation");
        rejects(() -> ProjectTransfer.response(new JSONObject(answer.toString()).put("projectSource", "native_response"), "", ""),
                "unexpected source rejected");
        rejects(() -> ProjectTransfer.fingerprint(project(record(A, "one", "-"), record(A.toUpperCase(), "two", "-"))),
                "duplicate GUID including case variation rejected");
        rejects(() -> ProjectTransfer.fingerprint(project(new JSONObject().put("id", A).put("recordKind", "other"))),
                "project without devices rejected before replacing the current project");
        rejects(() -> ProjectTransfer.fingerprint(new JSONObject(original.toString()).put("schemaVersion", 7)), "unknown project schema rejected");
        rejects(() -> ProjectTransfer.fingerprint(new JSONObject(original.toString()).put("panels", new JSONArray().put(panel).put(panel))),
                "ambiguous duplicate panel IDs rejected");

        check(ProjectTransfer.accepts(8, 8, originalFingerprint, originalFingerprint, true, true), "current foreground request may publish");
        check(!ProjectTransfer.accepts(7, 8, originalFingerprint, originalFingerprint, true, true), "old request generation cannot publish");
        check(!ProjectTransfer.accepts(8, 8, originalFingerprint, "other", true, true), "manual project switch invalidates old reply");
        check(!ProjectTransfer.accepts(8, 8, originalFingerprint, originalFingerprint, false, true), "endpoint or code change invalidates old reply");
        check(!ProjectTransfer.accepts(8, 8, originalFingerprint, originalFingerprint, true, false), "reply received in background cannot replace active project");

        JSONObject expanded = project(record(C, "Ручной извещатель", "-"), record(A, "Переименован", "-"), record(B, "Ручной извещатель", "+"));
        String expandedFingerprint = ProjectTransfer.fingerprint(expanded);
        Map<String, Object> saved = new HashMap<>();
        saved.put(JournalMerge.key("stage", originalFingerprint, A), 1);
        saved.put(JournalMerge.key("serial", originalFingerprint, A), "001122334455");
        saved.put(JournalMerge.key("device_key", originalFingerprint, A), "A1B2C3D4");
        saved.put(JournalMerge.key("note", originalFingerprint, A), "Локальное примечание");
        saved.put(JournalMerge.key("description", originalFingerprint, A), "");
        saved.put(JournalMerge.key("updated", originalFingerprint, A), 123456L);
        saved.put(JournalMerge.key("registration_summary", originalFingerprint, A), "{\"state\":\"failed\"}");
        Map<String, Object> copied = JournalMerge.carry(saved, original, originalFingerprint, expanded, expandedFingerprint);
        for (String field : JournalMerge.LOCAL_FIELDS)
            check(saved.get(JournalMerge.key(field, originalFingerprint, A)).equals(copied.get(JournalMerge.key(field, expandedFingerprint, A))),
                    "explicit local " + field + " survives added records, new fingerprint, renamed row and reordered array");
        check(!copied.containsKey(JournalMerge.key("stage", expandedFingerprint, B)), "untouched built-in initialized flag is never promoted to a local override");
        check(!copied.containsKey(JournalMerge.key("serial", expandedFingerprint, C)), "same device name never substitutes for the exact GUID");
        check(!copied.containsKey(JournalMerge.key("journal_conflict", expandedFingerprint, A)), "unchanged Windows base is not a conflict with phone work");
        check(saved.size() == 7, "carry does not mutate or erase the previous project's journal");

        JSONObject removed = project(record(C, "Ручной извещатель", "-"));
        check(JournalMerge.carry(saved, original, originalFingerprint, removed, ProjectTransfer.fingerprint(removed)).isEmpty(),
                "deleted GUID is retained only in its old project, not copied by name or position");
        saved.put(JournalMerge.key("serial", expandedFingerprint, A), "556677889900");
        check(!JournalMerge.carry(saved, original, originalFingerprint, expanded, expandedFingerprint)
                .containsKey(JournalMerge.key("serial", expandedFingerprint, A)), "existing edits in destination project are never silently overwritten");

        JSONObject changed = new JSONObject(expanded.toString());
        changed.getJSONArray("records").getJSONObject(1).put("commissioning", new JSONObject().put("stage", 2)
                .put("serial", "AABBCCDDEEFF").put("deviceKey", "11223344"));
        Map<String, Object> conflicted = JournalMerge.carry(saved, original, originalFingerprint, changed, expandedFingerprint);
        check(conflicted.containsKey(JournalMerge.key("journal_conflict", expandedFingerprint, A)), "concurrent changed Windows identity/stage creates a review state");
        check(!conflicted.containsKey(JournalMerge.key("serial", expandedFingerprint, A)), "conflict preserves existing phone ID");
        check(conflicted.get(JournalMerge.key("device_key", expandedFingerprint, A)).equals("A1B2C3D4"), "conflict preserves phone KEY");
        String signature = JournalMerge.baseSignature(changed.getJSONArray("records").getJSONObject(1));
        saved.put(JournalMerge.key("journal_acknowledged_base", expandedFingerprint, A), signature);
        check(!JournalMerge.carry(saved, original, originalFingerprint, changed, expandedFingerprint)
                .containsKey(JournalMerge.key("journal_conflict", expandedFingerprint, A)), "explicit review acknowledges only the exact Windows identity/stage base");
        changed.getJSONArray("records").getJSONObject(1).getJSONObject("commissioning").put("serial", "112233445566");
        check(JournalMerge.carry(saved, original, originalFingerprint, changed, expandedFingerprint)
                .containsKey(JournalMerge.key("journal_conflict", expandedFingerprint, A)), "later identity change requires a new review");

        JSONObject legacyPortable = project(record(A, "Ручной извещатель", "+").put("commissioning",
                new JSONObject().put("stage", 3).put("serial", "001122334455").put("deviceKey", "A1B2C3D4")
                        .put("description", "").put("note", "Монтаж проверен").put("updatedAt", 123456L)
                        .put("registration", new JSONObject().put("state", "registered").put("registrationConfirmed", true))));
        String legacyFingerprint = ProjectTransfer.fingerprint(legacyPortable);
        Map<String, Object> imported = JournalMerge.importJournal(new HashMap<>(), legacyPortable, legacyFingerprint);
        check(imported.get(JournalMerge.key("stage", legacyFingerprint, A)).equals(3), "legacy portable import preserves checked stage");
        check(imported.get(JournalMerge.key("serial", legacyFingerprint, A)).equals("001122334455"), "legacy portable import preserves ID");
        check(imported.get(JournalMerge.key("device_key", legacyFingerprint, A)).equals("A1B2C3D4"), "legacy portable import preserves KEY");
        check(imported.get(JournalMerge.key("description", legacyFingerprint, A)).equals(""), "legacy portable import retains intentionally cleared description");
        check(imported.get(JournalMerge.key("note", legacyFingerprint, A)).equals("Монтаж проверен"), "legacy portable import keeps separate installer note");
        check(imported.get(JournalMerge.key("registration_summary", legacyFingerprint, A)).toString().contains("registered"),
                "historical registration summary survives import without becoming an active operation");

        File directory = Files.createTempDirectory("streletz-project-transfer-test-").toFile();
        File active = new File(directory, "current-project.json");
        String before = original.toString();
        ProjectTransfer.write(active, before);
        File archive = ProjectTransfer.archiveFile(directory, originalFingerprint);
        ProjectTransfer.write(archive, before);
        Map<String, Object> journal = new HashMap<>(); journal.put("kept", "old");
        try {
            ProjectTransfer.install(active, expanded.toString(), () -> {
                journal.put("kept", "new");
                throw new IOException("simulated journal commit failure");
            }, () -> { journal.clear(); journal.put("kept", "old"); });
            throw new AssertionError("expected commit failure");
        } catch (ProjectTransfer.InstallException expected) { check(expected.rollbackSucceeded, "failed journal commit reports successful transaction rollback"); }
        check(ProjectTransfer.read(active).equals(before), "failed journal commit restores byte-identical active project");
        check(journal.size() == 1 && journal.get("kept").equals("old"), "failed journal commit restores old journal without partial edits");
        check(ProjectTransfer.read(archive).equals(before), "transaction failure keeps durable previous project archive");
        File fresh = new File(directory, "initial-project.json");
        try {
            ProjectTransfer.install(fresh, expanded.toString(), () -> { throw new IOException("simulated first-load failure"); }, () -> { });
            throw new AssertionError("expected first-install failure");
        } catch (ProjectTransfer.InstallException expected) { check(expected.rollbackSucceeded && !fresh.exists(), "failed first import restores absence of current file for the built-in project"); }
        File blocked = new File(directory, "current-project.json.pending");
        check(blocked.mkdir(), "create write-failure fixture");
        final boolean[] published = {false};
        rejects(() -> ProjectTransfer.install(active, expanded.toString(), () -> published[0] = true, () -> { }),
                "disk write failure is reported");
        check(!published[0] && ProjectTransfer.read(active).equals(before), "disk write failure cannot publish journal or replace old file");
        ProjectTransfer.install(active, expanded.toString(), () -> journal.put("kept", "accepted"), () -> { });
        check(ProjectTransfer.fingerprint(new JSONObject(ProjectTransfer.read(active))).equals(expandedFingerprint)
                && journal.get("kept").equals("accepted"), "complete transaction publishes new project and journal together");
        rejects(() -> ProjectTransfer.archiveFile(directory, "../foreign"), "untrusted fingerprint cannot escape archive directory");

        JSONObject realSize = new JSONObject().put("schemaVersion", 1).put("siteName", "Synthetic 455 records");
        JSONArray rows = new JSONArray();
        for (int i = 0; i < 455; i++) rows.put(record(String.format("00000000-0000-4000-8000-%012d", i), "Датчик " + i, i < 291 ? "+" : "-"));
        realSize.put("records", rows);
        check(ProjectTransfer.response(response(realSize, "revision-455"), "", "").project.getJSONArray("records").length() == 455,
                "server's actual 455-record composition supersedes the bundled 453-record fixture");
        System.out.println("ProjectTransferSelfTest: " + assertions + " assertions passed (snapshots, GUID merge, legacy portable import, transactional file rollback)");
    }

    private static JSONObject project(JSONObject... rows) throws Exception {
        JSONArray values = new JSONArray(); for (JSONObject row : rows) values.put(row);
        return new JSONObject().put("schemaVersion", 1).put("siteName", "Synthetic project").put("records", values);
    }
    private static JSONObject record(String id, String name, String initialized) throws Exception {
        return new JSONObject().put("id", id).put("recordKind", "device").put("name", name).put("initialized", initialized)
                .put("description", "Проектное описание").put("sourceRow", 4).put("zone", "2");
    }
    private static JSONObject response(JSONObject project, String revision) throws Exception {
        return new JSONObject().put("ok", true).put("projectSource", "windows_bridge")
                .put("projectFingerprint", ProjectTransfer.fingerprint(project)).put("projectRevision", revision)
                .put("notModified", false).put("project", project);
    }
    private static void check(boolean condition, String name) {
        if (!condition) throw new AssertionError(name); assertions++;
    }
    private static void rejects(Action action, String name) throws Exception {
        try { action.run(); }
        catch (IOException | org.json.JSONException expected) { assertions++; return; }
        throw new AssertionError(name);
    }
    private interface Action { void run() throws Exception; }
}
