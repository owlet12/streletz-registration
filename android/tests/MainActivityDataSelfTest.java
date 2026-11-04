// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import android.content.SharedPreferences;
import org.json.JSONArray;
import org.json.JSONObject;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Exercises the production data methods with a real JSON implementation and in-memory preferences.
 * Android UI construction is deliberately bypassed; no UI, network, or hardware test is claimed.
 * Classpath: compiled app classes, Android SDK android.jar, and JSON-java (before android.jar).
 */
public final class MainActivityDataSelfTest {
    private static int assertions;
    private static final String A = "00000000-0000-4000-8000-000000000001";
    private static final String B = "00000000-0000-4000-8000-000000000002";

    public static void main(String[] args) throws Exception {
        Class<?> unsafeType = Class.forName("sun.misc.Unsafe");
        Field singleton = unsafeType.getDeclaredField("theUnsafe"); singleton.setAccessible(true);
        Object unsafe = singleton.get(null);
        MainActivity activity = (MainActivity) unsafeType.getMethod("allocateInstance", Class.class).invoke(unsafe, MainActivity.class);
        Map<String, Object> values = new HashMap<>(), bridge = new HashMap<>();
        set(activity, "prefs", preferences(values));
        set(activity, "bridgePrefs", preferences(bridge));
        String fingerprint = ProtocolV3.projectFingerprint(Arrays.asList(A, B));
        set(activity, "projectFingerprint", fingerprint);
        set(activity, "siteName", "Synthetic site");
        set(activity, "operations", new HashMap<>());
        set(activity, "projectRecordMetadata", new JSONArray());
        set(activity, "panelFilterIds", new ArrayList<>());
        Object a = device(A, "Датчик A", "3R.ВТН1", 1);
        Object b = device(B, "Датчик B", "3R.ВТН2", 2);
        set(activity, "devices", new ArrayList<>(Arrays.asList(a, b)));
        JSONObject root = new JSONObject().put("format", "streletz-portable-project").put("schemaVersion", 1)
                .put("records", new JSONArray().put(record(A, "3R.ВТН1")).put(record(B, "3R.ВТН2")));
        set(activity, "projectRoot", root);
        JSONArray panels = new JSONArray().put(new JSONObject().put("panelId", "panel-a").put("projectPanelId", "panel-a")
                .put("name", "Панель проекта").put("recordIds", new JSONArray(Arrays.asList(A, B)))
                .put("available", true).put("host", "192.0.2.1").put("port", 6344));
        invoke(activity, "installProjectCatalogue", new Class<?>[]{JSONArray.class, JSONArray.class, boolean.class}, panels, null, false);
        String overrideKey = "description:" + fingerprint + ":" + B;
        values.put(overrideKey, "");
        values.put("note:" + fingerprint + ":" + B, "Заметка монтажника");

        JSONObject backup = (JSONObject) invoke(activity, "createBackup", new Class<?>[0]);
        check(backup.getJSONArray("records").getJSONObject(1).getBoolean("descriptionOverridden"), "backup retains explicit override bit");
        check(backup.getJSONArray("records").getJSONObject(1).getString("description").isEmpty(), "backup retains cleared description");
        check(backup.getJSONArray("recordMetadata").getJSONObject(0).getString("description").equals("3R.ВТН1"), "backup retains source description");
        JSONObject copied = new JSONObject(backup.toString());
        JSONArray changed = new JSONArray().put(new JSONObject().put("recordId", A).put("description", "changed in Windows"))
                .put(new JSONObject().put("recordId", B).put("description", "also changed"));
        invoke(activity, "installProjectCatalogue", new Class<?>[]{JSONArray.class, JSONArray.class, boolean.class}, new JSONArray(), changed, false);
        values.remove(overrideKey);
        check((Integer) invoke(activity, "restoreBackup", new Class<?>[]{String.class}, copied.toString()) == 2, "actual backup round trip restores two records");
        check(description(activity, a).equals("3R.ВТН1"), "restoring non-overridden description restores its earlier base");
        check(description(activity, b).isEmpty(), "restoring cleared override does not fall back to old base");
        check(displayName(activity, a).equals("3R.ВТН1"), "non-empty description replaces the project name in the client");
        check(displayName(activity, b).equals("Датчик B"), "empty description falls back to the project name in the client");
        check("Заметка монтажника".equals(values.get("note:" + fingerprint + ":" + B)), "note remains independent of description");
        ProjectPanelIndex index = (ProjectPanelIndex) get(activity, "panelIndex");
        check(index.uniqueForRecord(A).id.equals("panel-a"), "backup restores project membership for offline filter");
        JSONObject cache = new JSONObject((String) bridge.get("project-panels:" + fingerprint));
        check(!cache.getJSONArray("panels").getJSONObject(0).has("available"), "cache never persists live registration authorization");

        JSONObject portable = (JSONObject) invoke(activity, "createPortableProject", new Class<?>[0]);
        JSONObject exportedA = portable.getJSONArray("records").getJSONObject(0);
        JSONObject exportedB = portable.getJSONArray("records").getJSONObject(1);
        check(exportedA.getString("description").equals("3R.ВТН1"), "portable row exports effective base description");
        check(!exportedA.getJSONObject("commissioning").has("description"), "base remains distinguishable from local override");
        check(exportedB.getJSONObject("commissioning").getString("description").isEmpty(), "portable preserves empty explicit override");
        check(exportedB.getString("projectPanelId").equals("panel-a"), "portable record identifies project panel");
        check(portable.getJSONArray("panels").length() == 1, "portable retains full offline panel catalogue");
        String csv = (String) invoke(activity, "createCsv", new Class<?>[0]);
        check(csv.contains("Описание;Панель;") && csv.contains("3R.ВТН1") && !csv.contains(";Место;"), "CSV uses effective descriptions and panel column");

        JSONArray malformed = new JSONArray().put(new JSONObject().put("recordId", A).put("description", "must not publish")).put(7);
        rejects(() -> invoke(activity, "installProjectCatalogue", new Class<?>[]{JSONArray.class, JSONArray.class, boolean.class},
                new JSONArray(), malformed, false), "malformed metadata rejected before publishing catalogue");
        check(((ProjectPanelIndex) get(activity, "panelIndex")).uniqueForRecord(A) != null
                && description(activity, a).equals("3R.ВТН1"), "failed catalogue replacement leaves topology and descriptions intact");
        JSONObject wrongProject = new JSONObject(backup.toString()).put("projectFingerprint", "different-project");
        rejects(() -> invoke(activity, "restoreBackup", new Class<?>[]{String.class}, wrongProject.toString()), "backup from another project rejected");
        set(activity, "projectFingerprint", "different-project");
        check(description(activity, b).equals("3R.ВТН2"), "description override cannot leak into another project fingerprint");
        set(activity, "projectFingerprint", fingerprint);

        JSONArray bfxPanels = new JSONArray()
                .put(new JSONObject().put("panelId", "bfx-panel").put("projectPanelId", "bfx-panel")
                        .put("name", "Панель из BFX").put("host", "").put("port", 0).put("recordIds", new JSONArray(Arrays.asList(A, B)))
                        .put("routeKind", "panel_terminal").put("viaPanelTerminal", true)
                        .put("terminalPanelId", "native-terminal-32").put("terminalSegmentId", "segment-32")
                        .put("terminalDisplayName", "32.1 Панель-Терминал").put("adapterNodeId", "mk-32")
                        .put("adapterSegmentId", "segment-32").put("routeDisplayName", "Через терминал 32"))
                .put(new JSONObject().put("panelId", "empty-panel").put("projectPanelId", "empty-panel")
                        .put("name", "Пустая панель").put("recordIds", new JSONArray()));
        JSONObject bfx = catalogue(fingerprint, bfxPanels, true)
                .put("recordMetadata", new JSONArray().put(new JSONObject().put("recordId", A).put("description", "Новая подпись из BFX")))
                .put("catalogMessage", "Панели прочитаны из BFX.").put("nativeReason", "Модуль регистрации не найден.");
        check((Boolean) invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class}, bfx),
                "fresh cached BFX snapshot replaces an already populated phone catalogue");
        index = (ProjectPanelIndex) get(activity, "panelIndex");
        check(index.uniqueForRecord(A).id.equals("bfx-panel") && index.panels().size() == 2,
                "BFX panels without an IP route and without member records remain visible");
        check(description(activity, a).equals("Новая подпись из BFX") && description(activity, b).isEmpty(),
                "cached snapshot updates base descriptions without overwriting local clear override");
        check(new JSONObject((String) bridge.get("project-panels:" + fingerprint)).getBoolean("catalogComplete"),
                "complete snapshot marker survives phone cache");
        JSONObject exportedBfx = (JSONObject) invoke(activity, "createPortableProject", new Class<?>[0]);
        check(exportedBfx.getBoolean("panelCatalogComplete") && exportedBfx.getString("panelMappingFingerprint").equals(fingerprint),
                "complete snapshot marker and its project binding survive portable export");

        JSONObject terminalExport = exportedBfx.getJSONArray("panels").getJSONObject(0);
        check(terminalExport.getBoolean("viaPanelTerminal") && terminalExport.getString("terminalPanelId").equals("native-terminal-32")
                        && terminalExport.getString("adapterNodeId").equals("mk-32")
                        && terminalExport.getString("routeDisplayName").equals("Через терминал 32"),
                "terminal routing survives actual phone cache and portable project export");
        check(index.uniqueForRecord(A).displayName().contains("через 32.1 Панель-Терминал"),
                "phone labels keep main panel and identify the exact terminal separately");

        String beforeEmpty = (String) bridge.get("project-panels:" + fingerprint);
        JSONObject unavailableEmpty = catalogue(fingerprint, new JSONArray(), false);
        check(!(Boolean) invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class}, unavailableEmpty),
                "empty legacy fallback without a project map does not replace a known catalogue");
        check(((ProjectPanelIndex) get(activity, "panelIndex")).uniqueForRecord(A).id.equals("bfx-panel")
                        && beforeEmpty.equals(bridge.get("project-panels:" + fingerprint)),
                "retained fallback preserves both topology and cache freshness");
        JSONObject emptyProject = catalogue(fingerprint, new JSONArray(), true);
        check((Boolean) invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class}, emptyProject)
                        && ((ProjectPanelIndex) get(activity, "panelIndex")).panels().isEmpty(),
                "explicitly complete empty BFX snapshot clears deleted project panels");
        check(!(Boolean) invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class}, unavailableEmpty)
                        && (Boolean) get(activity, "projectPanelCatalogueComplete"),
                "unknown empty fallback cannot downgrade a known complete empty snapshot");
        invoke(activity, "reportCatalogueResponse", new Class<?>[]{JSONObject.class, boolean.class}, unavailableEmpty, false);
        check(((String) get(activity, "catalogueStatusShort")).contains("карта не получена"),
                "empty fallback with no previous map is not displayed as successful zero-panel import");
        rejects(() -> invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class},
                catalogue("wrong-project", bfxPanels, true)), "another project's catalogue cannot replace the phone map");

        JSONObject operational = new JSONObject().put("panelId", "profile").put("projectPanelId", "bfx-panel")
                .put("recordIds", new JSONArray(Arrays.asList(A, B))).put("available", true)
                .put("verifiedRecordIds", new JSONArray(Arrays.asList(B)));
        check(!profileAvailable(operational, false, B), "cached catalogue cannot authorize registration even if available is true");
        check(!profileAvailable(operational, true, A), "another record's verified route cannot authorize this record");
        check(profileAvailable(operational, true, B), "native-verified record may use its available profile");
        operational.put("available", false);
        check(!profileAvailable(operational, true, B), "per-record unavailable flag remains binding in a mixed live/offline catalogue");
        check(!(Boolean) invoke(activity, "isNativeCatalogue", new Class<?>[]{JSONObject.class},
                new JSONObject().put("catalogSource", "native_project").put("nativeAvailable", false)),
                "nativeAvailable false prevents contradictory global authorization");

        invoke(activity, "applyProjectCatalogueResponse", new Class<?>[]{JSONObject.class}, bfx);
        invoke(activity, "reportCatalogueResponse", new Class<?>[]{JSONObject.class, boolean.class}, bfx, true);
        check(((String) get(activity, "catalogueStatusShort")).contains("2")
                        && ((String) get(activity, "catalogueStatusDetails")).contains("Модуль регистрации не найден"),
                "background catalogue success exposes the separate registration reason");
        JSONObject missingProject = new JSONObject().put("ok", false).put("errorCode", "project_missing")
                .put("message", "Откройте проект в Windows-мосте.");
        invoke(activity, "reportCatalogueFailure", new Class<?>[]{JSONObject.class, Exception.class}, missingProject,
                new java.io.IOException(missingProject.getString("message")));
        check(((String) get(activity, "catalogueStatusShort")).contains("откройте проект")
                        && ((String) get(activity, "catalogueStatusDetails")).contains("Откройте проект в Windows-мосте"),
                "automatic refresh failure leaves a visible actionable state");
        // Frozen 0.5.3 backup format has no appId/signature dependency. Restore it into the matching project.
        JSONObject legacyBackup = new JSONObject().put("format", "streletz-commissioning-progress").put("version", 1)
                .put("siteName", "Synthetic old application").put("projectFingerprint", fingerprint)
                .put("panels", panels).put("panelCatalogComplete", true)
                .put("records", new JSONArray().put(new JSONObject().put("id", A).put("name", "Old device")
                        .put("stage", 3).put("serial", "001122334455").put("deviceKey", "A1B2C3D4")
                        .put("description", "").put("descriptionOverridden", true).put("note", "Работа старой версии")
                        .put("panelId", "panel-a").put("updatedAt", 123456L)
                        .put("registration", new JSONObject().put("state", "registered").put("registrationConfirmed", true))));
        check((Integer) invoke(activity, "restoreBackup", new Class<?>[]{String.class}, legacyBackup.toString()) == 1,
                "legacy 0.5.3 backup is restored without old Android package or signing key");
        check(values.get("stage:" + fingerprint + ":" + A).equals(3), "legacy backup restores checked stage in project scope");
        check(values.get("serial:" + fingerprint + ":" + A).equals("001122334455"), "legacy backup restores exact hardware ID");
        check(values.get("device_key:" + fingerprint + ":" + A).equals("A1B2C3D4"), "legacy backup restores KEY");
        check(values.get("note:" + fingerprint + ":" + A).equals("Работа старой версии"), "legacy backup restores separate note");
        check(description(activity, a).isEmpty(), "legacy backup restores deliberate description clear");
        check(bridge.get("panel:" + fingerprint + ":" + A).equals("panel-a"), "legacy backup restores selected panel in matching project scope");
        JSONObject legacyReexport = (JSONObject) invoke(activity, "createPortableProject", new Class<?>[0]);
        JSONObject legacyCommissioning = legacyReexport.getJSONArray("records").getJSONObject(0).getJSONObject("commissioning");
        check(legacyCommissioning.getJSONObject("registration").getString("state").equals("registered"),
                "historical registration summary survives backup restore and subsequent full project export");
        check(legacyCommissioning.getString("deviceKey").equals("A1B2C3D4") && legacyCommissioning.getInt("stage") == 3,
                "legacy restored journal is not lost when reexported as a full portable project");

        String otherFingerprint = ProtocolV3.projectFingerprint(Arrays.asList(A, B, "00000000-0000-4000-8000-000000000003"));
        set(activity, "projectFingerprint", otherFingerprint);
        check(((String) invoke(activity, "getSerial", new Class<?>[]{a.getClass()}, a)).isEmpty(),
                "a shared GUID in another project cannot read old unmerged scoped ID");
        set(activity, "projectFingerprint", fingerprint);
        check(((String) invoke(activity, "getSerial", new Class<?>[]{a.getClass()}, a)).equals("001122334455"),
                "returning to an archived project retains its journal");

        Map<String, Object> migrated = new HashMap<>();
        migrated.put("stage:" + A, 1); migrated.put("serial:" + A, "AABBCCDDEEFF");
        migrated.put("device_key:" + A, "11223344"); migrated.put("note:" + A, "Legacy preferences");
        migrated.put("updated:" + A, 77L);
        set(activity, "prefs", preferences(migrated));
        invoke(activity, "migrateLegacyJournal", new Class<?>[]{JSONObject.class, String.class}, root, fingerprint);
        check(migrated.get("serial:" + fingerprint + ":" + A).equals("AABBCCDDEEFF")
                && migrated.get("device_key:" + fingerprint + ":" + A).equals("11223344"),
                "one-time migration preserves legacy unscoped ID and KEY in the current project");
        invoke(activity, "migrateLegacyJournal", new Class<?>[]{JSONObject.class, String.class}, root, otherFingerprint);
        check(!migrated.containsKey("serial:" + otherFingerprint + ":" + A),
                "legacy preferences cannot leak to the next imported project");
        JSONObject expandedRoot = new JSONObject(root.toString());
        expandedRoot.getJSONArray("records").put(record("00000000-0000-4000-8000-000000000003", "new"));
        invoke(activity, "carryAndImportLocalJournal", new Class<?>[]{JSONObject.class, String.class, boolean.class},
                expandedRoot, otherFingerprint, false);
        check(migrated.get("serial:" + otherFingerprint + ":" + A).equals("AABBCCDDEEFF"),
                "production merge carries explicit phone ID to a new fingerprint by exact GUID");
        check(!migrated.containsKey("stage:" + otherFingerprint + ":" + B),
                "production merge does not promote untouched source initialization to local work");
        check(migrated.get("serial:" + fingerprint + ":" + A).equals("AABBCCDDEEFF"),
                "new-project merge retains previous scoped journal");

        // The actual next-device selector respects visible filters and unambiguous panel membership.
        values.clear(); bridge.clear();
        set(activity, "prefs", preferences(values));
        set(activity, "projectFingerprint", fingerprint);
        set(activity, "projectRoot", root);
        set(activity, "operations", new HashMap<>());
        set(activity, "visible", new ArrayList<>(Arrays.asList(a, b)));
        invoke(activity, "installProjectCatalogue", new Class<?>[]{JSONArray.class, JSONArray.class, boolean.class}, panels, null, false);
        Class<?> cardType = Class.forName("ru.akchurin.streletzinit.MainActivity$DeviceCard");
        Constructor<?> cardConstructor = cardType.getDeclaredConstructor(a.getClass()); cardConstructor.setAccessible(true);
        Object cardA = cardConstructor.newInstance(a), cardB = cardConstructor.newInstance(b);
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == b, "next device belongs to the same panel");
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardB) == a, "next device wraps within visible eligible devices");
        set(activity, "visible", new ArrayList<>(Arrays.asList(a)));
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == null, "next device never escapes the visible filter");
        set(activity, "visible", new ArrayList<>(Arrays.asList(a, b)));
        values.put("stage:" + fingerprint + ":" + B, 2);
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == null, "initialized device is skipped");
        values.remove("stage:" + fingerprint + ":" + B);
        values.put("journal_conflict:" + fingerprint + ":" + B, "conflict");
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == null, "journal conflict is skipped");
        values.clear();
        bridge.put("registration:" + fingerprint + ":" + B, "corrupt");
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == null, "damaged saved operation cannot become next");
        bridge.clear();
        JSONArray splitPanels = new JSONArray().put(new JSONObject().put("panelId", "panel-a").put("recordIds", new JSONArray().put(A)))
                .put(new JSONObject().put("panelId", "panel-b").put("recordIds", new JSONArray().put(B)));
        invoke(activity, "installProjectCatalogue", new Class<?>[]{JSONArray.class, JSONArray.class, boolean.class}, splitPanels, null, false);
        check(invoke(activity, "nextDevice", new Class<?>[]{cardType}, cardA) == null, "next device cannot cross panel boundary");
        String search = (String) invoke(activity, "searchText", new Class<?>[]{a.getClass()}, a);
        check(search == invoke(activity, "searchText", new Class<?>[]{a.getClass()}, a), "unchanged search reuses cached text");
        values.put("description:" + fingerprint + ":" + A, "Новое описание для поиска");
        values.put("note:" + fingerprint + ":" + A, "Новая заметка");
        invoke(activity, "invalidateSearchIndex", new Class<?>[0]);
        String updatedSearch = (String) invoke(activity, "searchText", new Class<?>[]{a.getClass()}, a);
        check(updatedSearch.contains("новое описание для поиска") && updatedSearch.contains("новая заметка"), "search cache refreshes after local changes");

        JSONObject frozen = (JSONObject) invoke(activity, "createBackup", new Class<?>[0]);
        ExportSnapshot deferred = (ExportSnapshot) invoke(activity, "captureExport", new Class<?>[0]);
        JSONObject built = deferred.build();
        JSONObject expectedSnapshot = new JSONObject(frozen.toString());
        built.remove("exportedAt"); expectedSnapshot.remove("exportedAt");
        check(built.similar(expectedSnapshot), "deferred snapshot preserves legacy backup schema and values");
        values.put("note:" + fingerprint + ":" + A, "changed after capture");
        set(a, "description", "changed model after capture");
        JSONObject builtAgain = deferred.build(); builtAgain.remove("exportedAt");
        check(builtAgain.similar(expectedSnapshot), "deferred snapshot isolates later preferences and model metadata");
        values.remove("note:" + fingerprint + ":" + A);
        values.put("note:" + fingerprint + ":" + A, "Новая заметка");

        check(ProjectDocuments.csv(frozen).equals(invoke(activity, "createCsv", new Class<?>[0])), "background CSV preserves original columns, quotes, stages and timestamps");
        String frozenDescription = frozen.getJSONArray("records").getJSONObject(0).getString("description");
        values.put("description:" + fingerprint + ":" + A, "changed after snapshot");
        JSONObject backgroundExport = ProjectDocuments.portable(root, frozen);
        check(backgroundExport.getJSONArray("records").getJSONObject(0).getString("description").equals(frozenDescription), "background export uses the frozen journal instead of later edits");
        check(!root.getJSONArray("records").getJSONObject(0).has("commissioning"), "background export leaves original project immutable");
        CountingList indexed = new CountingList(); indexed.add(a); indexed.add(b);
        set(activity, "devices", indexed);
        check(invoke(activity, "findById", new Class<?>[]{String.class}, B.toUpperCase(java.util.Locale.ROOT)) == b, "GUID index is case insensitive");
        int scans = indexed.scans;
        for (int i = 0; i < 10000; i++) invoke(activity, "findById", new Class<?>[]{String.class}, i % 2 == 0 ? A : B);
        check(indexed.scans == scans, "ten thousand GUID lookups do not scan the device list again");
        check(invoke(activity, "findById", new Class<?>[]{String.class}, "missing") == null, "unknown GUID cannot select another device");
        set(activity, "devices", new ArrayList<>(Arrays.asList(a)));
        check(invoke(activity, "findById", new Class<?>[]{String.class}, B) == null, "project replacement invalidates GUID index");


        // Compare deferred registration summaries with the independently exercised legacy data path.
        JSONObject op = new JSONObject().put("operationId", "00000000-0000-4000-8000-000000000009")
                .put("projectFingerprint", fingerprint).put("recordId", A).put("panelId", "panel-a")
                .put("expectedHardwareId", "ABCDEF123456").put("host", "192.0.2.1").put("port", 23019)
                .put("pairingCode", "0123456789ABCDEF").put("state", "registered").put("registrationConfirmed", true)
                .put("confirmedHardwareId", "ABCDEF123456").put("projectSaved", true);
        // The index invalidation check above temporarily replaced the devices list.
        set(activity, "devices", new ArrayList<>(Arrays.asList(a,b)));
        for (int variant = 0; variant < 3; variant++) {
            JSONObject candidate = new JSONObject(op.toString());
            if (variant == 1) candidate.put("expectedHardwareId", "invalid");
            if (variant == 2) candidate.put("confirmedHardwareId", "ABCDEF000000");
            bridge.put("registration:" + fingerprint + ":" + A, candidate.toString());
            set(activity, "operations", new HashMap<>());
            JSONObject legacy = (JSONObject) invoke(activity, "createBackup", new Class<?>[0]);
            JSONObject deferredOperation = ((ExportSnapshot) invoke(activity, "captureExport", new Class<?>[0])).build();
            legacy.remove("exportedAt"); deferredOperation.remove("exportedAt");
            check(legacy.similar(deferredOperation), "snapshot keeps validated operation semantics variant " + variant);
            check(!deferredOperation.toString().contains("pairingCode"), "snapshot leaked bridge pairing credentials");
        }
        System.out.println("MainActivityDataSelfTest: " + assertions + " assertions passed (data methods only)");
    }

    private static final class CountingList extends ArrayList<Object> {
        int scans;
        @Override public java.util.Iterator<Object> iterator() { scans++; return super.iterator(); }
    }

    private static JSONObject record(String id, String description) {
        return new JSONObject().put("id", id).put("recordKind", "device").put("name", id).put("description", description);
    }
    private static JSONObject catalogue(String fingerprint, JSONArray panels, boolean complete) {
        return new JSONObject().put("ok", true).put("panelMappingVersion", 1).put("projectFingerprint", fingerprint)
                .put("catalogSource", "cached_project").put("nativeAvailable", false).put("catalogComplete", complete)
                .put("projectPanels", panels);
    }
    private static boolean profileAvailable(JSONObject value, boolean live, String recordId) throws Exception {
        Class<?> type = Class.forName("ru.akchurin.streletzinit.MainActivity$PanelProfile");
        Constructor<?> constructor = type.getDeclaredConstructor(JSONObject.class, boolean.class, String.class);
        constructor.setAccessible(true);
        return (Boolean) get(constructor.newInstance(value, live, recordId), "available");
    }
    private static Object device(String id, String name, String description, int order) throws Exception {
        Class<?> type = Class.forName("ru.akchurin.streletzinit.MainActivity$Device");
        Constructor<?> constructor = type.getDeclaredConstructor(); constructor.setAccessible(true);
        Object result = constructor.newInstance();
        set(result, "id", id); set(result, "name", name); set(result, "description", description);
        set(result, "group", "Пожарные извещатели"); set(result, "zone", "1"); set(result, "type", "Извещатель"); set(result, "sourceRow", order);
        return result;
    }
    private static String description(MainActivity activity, Object device) throws Exception {
        return (String) invoke(activity, "getDescription", new Class<?>[]{device.getClass()}, device);
    }
    private static String displayName(MainActivity activity, Object device) throws Exception {
        return (String) invoke(activity, "getDisplayName", new Class<?>[]{device.getClass()}, device);
    }
    private static Object invoke(Object target, String name, Class<?>[] types, Object... args) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types); method.setAccessible(true);
        try { return method.invoke(target, args); }
        catch (InvocationTargetException error) {
            if (error.getCause() instanceof Exception) throw (Exception) error.getCause();
            throw error;
        }
    }
    private static void set(Object target, String name, Object value) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); field.set(target, value);
    }
    private static Object get(Object target, String name) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(target);
    }
    private static SharedPreferences preferences(Map<String, Object> values) {
        return (SharedPreferences) Proxy.newProxyInstance(MainActivityDataSelfTest.class.getClassLoader(), new Class<?>[]{SharedPreferences.class},
                (proxy, method, args) -> {
                    switch (method.getName()) {
                        case "contains": return values.containsKey(args[0]);
                        case "getString": case "getInt": case "getLong": case "getBoolean": return values.getOrDefault(args[0], args[1]);
                        case "getAll": return new HashMap<>(values);
                        case "edit": return editor(values);
                        default: return null;
                    }
                });
    }
    private static SharedPreferences.Editor editor(Map<String, Object> values) {
        Map<String, Object> pending = new HashMap<>();
        List<String> removed = new ArrayList<>();
        return (SharedPreferences.Editor) Proxy.newProxyInstance(MainActivityDataSelfTest.class.getClassLoader(), new Class<?>[]{SharedPreferences.Editor.class},
                (proxy, method, args) -> {
                    if (method.getName().startsWith("put")) { pending.put((String) args[0], args[1]); return proxy; }
                    switch (method.getName()) {
                        case "remove": removed.add((String) args[0]); return proxy;
                        case "clear": removed.addAll(values.keySet()); return proxy;
                        case "apply": case "commit":
                            for (String key : removed) values.remove(key);
                            values.putAll(pending);
                            return method.getName().equals("commit") ? true : null;
                        default: return proxy;
                    }
                });
    }
    private static void check(boolean value, String name) { if (!value) throw new AssertionError(name); assertions++; }
    private static void rejects(Action action, String name) throws Exception {
        try { action.run(); } catch (org.json.JSONException | java.io.IOException expected) { assertions++; return; }
        throw new AssertionError(name);
    }
    private interface Action { void run() throws Exception; }
}
