// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import android.content.SharedPreferences;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.IOException;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Independent production storage checks for an operator-verified, already registered sensor.
 * Does not exercise Android widgets, network, Master, or equipment. */
public final class ManualRegisteredRecoverySelfTest {
    private static int assertions;
    private static final String OP = "00000000-0000-4000-8000-000000000071";
    private static final String NEXT = "00000000-0000-4000-8000-000000000072";
    private static final String RECORD = "00000000-0000-4000-8000-000000000007";
    private static final String OTHER = "00000000-0000-4000-8000-000000000008";
    private static final String FP = fingerprint(RECORD);
    private static final String HW = "00AABBCCDD77";
    private static final long NOW = 2000000;
    private static final Class<?> OP_TYPE;
    private static final Class<?> DEVICE_TYPE;
    static {
        try {
            OP_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$RegistrationOperation");
            DEVICE_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$Device");
        } catch (Exception error) { throw new ExceptionInInitializerError(error); }
    }

    public static void main(String[] args) throws Exception {
        prerequisites();
        archiveAndReload();
        projectFingerprintTransition();
        failedSave();
        deferredPublication();
        System.out.println("ManualRegisteredRecoverySelfTest: " + assertions
                + " assertions passed (production storage/policy, no UI or hardware)");
    }

    private static void deferredPublication() throws Exception {
        Fixture f = new Fixture();RegistrationRecovery.Proof proof = f.authorize();
        Object plan = invoke(f.activity, "prepareRetirement", new Class<?>[]{OP_TYPE, RegistrationRecovery.Proof.class,
                boolean.class, boolean.class, boolean.class, long.class}, f.operation, proof, true, true, true, NOW + 1);
        check(f.bridge.commits == 0 && f.operations.get(f.key) == f.operation, "preparation mutated storage or current operation");
        java.util.concurrent.FutureTask<Object> saved = new java.util.concurrent.FutureTask<>(() -> call(plan, "persist"));
        new Thread(saved, "test-retirement-storage").start();saved.get();
        check(f.bridge.values.containsKey(f.historyKey) && f.operations.get(f.key) == f.operation,
                "worker changed UI-owned current operation before publication");
        check((Boolean) call(f.operation, "needsResolution"), "old operation released before publication");
        Object retired = call(plan, "publish");
        check(f.operations.get(f.key) == retired && !(Boolean) call(retired, "needsResolution"), "publication did not install durable retirement");
    }

    private static void prerequisites() throws Exception {
        Fixture f = new Fixture();
        RegistrationRecovery.Proof proof = f.authorize();
        rejects(() -> f.retire(proof, false, true, NOW + 1), "manual registered outcome requires a stopped search");
        rejects(() -> f.retire(proof, true, false, NOW + 1), "manual registered outcome requires full ID verification");
        rejects(() -> f.retire(proof, true, true, NOW + RegistrationRecovery.REVIEW_VALID_MILLIS + 1),
                "expired readonly recovery cannot release waiting");
        f.inFlight.add(OP);
        rejects(() -> f.retire(proof, true, true, NOW + 1), "inflight request prevents manual registered retirement");
        f.inFlight.clear();
        RegistrationRecovery.Proof active = RegistrationRecovery.authenticatedProof(
                response("native_active", false), identity(), 1, NOW);
        set(f.operation, "recoveryProof", active);
        rejects(() -> f.retire(active, true, true, NOW + 1), "known native activity cannot be archived as manually verified");
        check(f.bridge.commits == 0 && !f.bridge.values.containsKey(f.historyKey),
                "rejected reviews leave no archive or persistence mutation");
        check((Boolean) call(f.operation, "needsResolution"), "rejected review retains the unknown lock");
    }

    private static void archiveAndReload() throws Exception {
        Fixture f = new Fixture();
        Map<String, Object> before = new HashMap<>(f.journal.values);
        RegistrationRecovery.Proof proof = f.authorize();
        Object retired = f.retire(proof, true, true, NOW + 1);
        check(f.bridge.commits == 1, "operator archive and retired slot commit together once");
        check(f.journal.values.equals(before), "manual registered review preserves serial, KEY, stage, note and description");
        check("unknown".equals(get(retired, "state")) && !(Boolean) get(retired, "registrationConfirmed")
                && !(Boolean) get(retired, "terminal"), "operator finding never fabricates hardware confirmation or terminal outcome");
        check((Boolean) get(retired, "localResolved") && (Boolean) call(retired, "manuallyRegistered"),
                "manual finding is stored separately from the unknown hardware outcome");
        check(!(Boolean) call(retired, "needsResolution") && !(Boolean) call(retired, "shouldAutoPoll"),
                "operator review ends local waiting without another request");
        check((Boolean) invoke(f.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, f.device),
                "a reviewed registered sensor still blocks a new begin");
        check(!(Boolean) invoke(f.activity, "hasUnresolvedRegistrations", new Class<?>[0]),
                "a reviewed registered sensor does not indefinitely block project synchronization");

        JSONArray history = new JSONArray((String) f.bridge.values.get(f.historyKey));
        JSONObject audit = history.getJSONObject(0).getJSONObject("manualReview");
        check(history.length() == 1 && "registered_verified_by_user".equals(audit.getString("outcome")),
                "audit distinguishes operator-verified registration from verified absence");
        check(audit.getBoolean("searchStopped") && audit.getBoolean("fullIdRegistered")
                && !audit.optBoolean("fullIdNotRegistered"), "audit retains the exact positive operator finding");
        check(!history.toString().contains("pairingCode") && !history.toString().contains("deviceKey")
                && !history.toString().contains("TEST-PRIVATE-KEY"), "archived finding contains no credentials or device KEY");
        check(!(Boolean) invoke(f.activity, "isCurrentOperation", new Class<?>[]{OP_TYPE, long.class}, f.operation, 1L),
                "a delayed callback from the old generation is rejected");
        String stored = (String) f.bridge.values.get(f.key);
        check(!(Boolean) invoke(f.activity, "saveOperation", new Class<?>[]{OP_TYPE, boolean.class}, f.operation, true)
                && stored.equals(f.bridge.values.get(f.key)), "a delayed old save cannot erase the registered marker");

        f.operations.clear();
        Object reloaded = invoke(f.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, f.device);
        check(reloaded != null && (Boolean) call(reloaded, "manuallyRegistered"),
                "reloaded operation validates the matching manual audit");
        check(!(Boolean) invoke(f.activity, "hasUnresolvedRegistrations", new Class<?>[0])
                && (Boolean) invoke(f.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, f.device),
                "restart retains separate synchronization and begin decisions");
        Object next = fromStored(value().put("operationId", NEXT).put("state", "connecting").put("uncertain", false));
        check(!(Boolean) invoke(f.activity, "installOperation", new Class<?>[]{OP_TYPE, OP_TYPE}, next, reloaded),
                "low-level installation cannot replace manually registered history with a fresh begin");
        check(stored.equals(f.bridge.values.get(f.key)), "rejected new operation preserves the retired slot");

        JSONObject envelope = new JSONObject();
        invoke(f.activity, "addRegistrationSummary", new Class<?>[]{JSONObject.class, DEVICE_TYPE}, envelope, f.device);
        JSONObject summary = envelope.getJSONObject("registration");
        check(RegistrationRecovery.manualRegisteredSummary(summary), "safe portable summary preserves the validated manual marker");
        check(!RegistrationRecovery.unresolvedSummary(summary), "portable operator review is resolved locally");
        check(!summary.getBoolean("registrationConfirmed") && "unknown".equals(summary.getString("state")),
                "portable export does not upgrade the evidence to automatic confirmation");
        check(!summary.has("pairingCode") && !summary.has("deviceKey"), "portable summary omits secrets");
        for (String removed : new String[]{"history", "manualReview"}) {
            JSONObject malformed = new JSONObject(summary.toString()); malformed.remove(removed);
            check(!RegistrationRecovery.manualRegisteredSummary(malformed) && RegistrationRecovery.unresolvedSummary(malformed),
                    "missing " + removed + " cannot bypass unresolved import protection");
        }
        JSONObject wrongId = new JSONObject(summary.toString());
        wrongId.getJSONArray("history").getJSONObject(0).put("expectedHardwareId", "00AABBCCDD88");
        check(!RegistrationRecovery.manualRegisteredSummary(wrongId) && RegistrationRecovery.unresolvedSummary(wrongId),
                "an archive for another full hardware ID cannot release the imported operation");
        JSONObject contradictory = new JSONObject(summary.toString());
        contradictory.getJSONObject("manualReview").put("outcome", "not_registered_verified_by_user")
                .put("fullIdRegistered", false).put("fullIdNotRegistered", true);
        check(!RegistrationRecovery.validArchivedManualReview(contradictory)
                && RegistrationRecovery.unresolvedSummary(contradictory),
                "opposite top-level finding cannot reuse the registered archive even with the same request ID");

        Fixture imported = new Fixture();
        imported.operations.clear(); imported.bridge.values.clear();
        imported.journal.values.put("registration_summary:" + FP + ":" + RECORD, summary.toString());
        check(!(Boolean) invoke(imported.activity, "hasUnresolvedRegistrations", new Class<?>[0]),
                "imported verified operator outcome permits normal project synchronization");
        check((Boolean) invoke(imported.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, imported.device),
                "imported verified operator outcome still blocks another registration");
        check(!(Boolean) invoke(imported.activity, "installOperation", new Class<?>[]{OP_TYPE, OP_TYPE}, next, null),
                "low-level installation cannot bypass an imported registered marker with an empty local slot");
        check(((Integer) imported.journal.values.get("stage:" + FP + ":" + RECORD)) == 1,
                "importing the audit alone does not set initialized stage");
        check(!(Boolean) invoke(imported.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, device(OTHER)),
                "manual block is scoped to the original record");
        JSONObject reexport = new JSONObject();
        invoke(imported.activity, "addRegistrationSummary", new Class<?>[]{JSONObject.class, DEVICE_TYPE}, reexport, imported.device);
        check(RegistrationRecovery.manualRegisteredSummary(reexport.getJSONObject("registration")),
                "import then export retains audit identity and the no-repeat marker");

        String summaryKey = "registration_summary:" + FP + ":" + RECORD;
        SharedPreferences.Editor acceptWindows = imported.journal.preferences().edit().remove(summaryKey);
        invoke(imported.activity, "preserveManualRegistration", new Class<?>[]{SharedPreferences.Editor.class, DEVICE_TYPE},
                acceptWindows, imported.device);
        acceptWindows.commit();
        check(RegistrationRecovery.manualRegisteredSummary(new JSONObject((String) imported.journal.values.get(summaryKey))),
                "accepting the Windows journal cannot erase a validated imported no-repeat marker");
        SharedPreferences.Editor oldBackup = imported.journal.preferences().edit().putString(summaryKey,
                value().put("state", "failed").put("terminal", true).put("uncertain", false).toString());
        invoke(imported.activity, "preserveManualRegistration", new Class<?>[]{SharedPreferences.Editor.class, DEVICE_TYPE},
                oldBackup, imported.device);
        oldBackup.commit();
        check(RegistrationRecovery.manualRegisteredSummary(new JSONObject((String) imported.journal.values.get(summaryKey))),
                "an older backup cannot overwrite a validated imported no-repeat marker");

        imported.journal.values.remove(summaryKey);
        set(imported.device, "commissioning", new JSONObject().put("registration", summary));
        JSONObject project = new JSONObject().put("records", new JSONArray().put(
                new JSONObject().put("id", RECORD).put("recordKind", "device")));
        set(imported.activity, "projectRoot", project);
        @SuppressWarnings("unchecked")
        Map<String, Object> updates = (Map<String, Object>) invoke(imported.activity, "prepareLocalJournal",
                new Class<?>[]{JSONObject.class, String.class, boolean.class}, new JSONObject(project.toString()), FP, false);
        check(updates.get(summaryKey) instanceof String
                && RegistrationRecovery.manualRegisteredSummary(new JSONObject((String) updates.get(summaryKey))),
                "autosync snapshots a marker carried only by the old project before replacing its rows");
    }

    private static void failedSave() throws Exception {
        Fixture f = new Fixture();
        RegistrationRecovery.Proof proof = f.authorize();
        String before = (String) f.bridge.values.get(f.key);
        f.bridge.failNextCommit = true;
        rejects(() -> f.retire(proof, true, true, NOW + 1), "failed disk persistence reports failure");
        check(before.equals(f.bridge.values.get(f.key)) && !f.bridge.values.containsKey(f.historyKey),
                "failed manual registered save rolls back the in-process preferences view");
        check((Boolean) call(f.operation, "needsResolution") && !(Boolean) call(f.operation, "manuallyRegistered"),
                "failed archive does not mutate the active operation");
        check((Boolean) invoke(f.activity, "hasUnresolvedRegistrations", new Class<?>[0]),
                "failure retains the project synchronization lock");
    }

    private static void projectFingerprintTransition() throws Exception {
        JSONObject originalProject = new JSONObject().put("records", new JSONArray().put(
                new JSONObject().put("id", RECORD).put("recordKind", "device").put("name", "Existing sensor")));
        JSONObject withIb1 = new JSONObject(originalProject.toString());
        withIb1.getJSONArray("records").put(new JSONObject().put("id", OTHER).put("recordKind", "device")
                .put("name", "1 ИБ1-ПРО").put("type", "ИБ1-ПРО"));
        String expandedFingerprint = fingerprint(RECORD, OTHER);
        check(!FP.equals(expandedFingerprint), "adding an IB1 record changes the actual project fingerprint");

        for (boolean registered : new boolean[]{true, false}) {
            Fixture f = new Fixture();
            set(f.activity, "projectRoot", originalProject);
            RegistrationRecovery.Proof proof = f.authorize();
            invoke(f.activity, "retireReviewedOperation", new Class<?>[]{OP_TYPE, RegistrationRecovery.Proof.class,
                    boolean.class, boolean.class, boolean.class, long.class}, f.operation, proof, true, true, registered, NOW + 1);
            @SuppressWarnings("unchecked")
            Map<String, Object> updates = (Map<String, Object>) invoke(f.activity, "prepareLocalJournal",
                    new Class<?>[]{JSONObject.class, String.class, boolean.class}, withIb1, expandedFingerprint, false);
            String targetKey = "registration_summary:" + expandedFingerprint + ":" + RECORD;
            JSONObject carried = new JSONObject((String) updates.get(targetKey));
            check(FP.equals(carried.getString("projectFingerprint")) && RECORD.equals(carried.getString("recordId"))
                    && OP.equals(carried.getString("operationId")), "record carry preserves original operation/project identities: " + registered);
            check(RegistrationRecovery.validArchivedManualReview(carried), "carried original audit remains valid: " + registered);
            invoke(f.activity, "applyJournalUpdates", new Class<?>[]{Map.class}, updates);
            set(f.activity, "projectFingerprint", expandedFingerprint);
            set(f.activity, "projectRoot", withIb1);
            Object ib1 = device(OTHER); set(f.activity, "devices", new ArrayList<>(List.of(f.device, ib1)));
            check(invoke(f.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, f.device) == null,
                    "expanded project does not silently rebind the old private operation: " + registered);
            check(!(Boolean) invoke(f.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, f.device)
                    && !(Boolean) invoke(f.activity, "hasUnresolvedRegistrations", new Class<?>[0]),
                    "valid reviewed history does not reopen waiting or block synchronization after IB1 addition: " + registered);
            check(((Boolean) invoke(f.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, f.device)) == registered,
                    "exact-record registered prohibition survives new fingerprint; verified absence allows explicit begin: " + registered);
            for (String field : new String[]{"stage", "serial", "device_key", "description", "note"})
                check(f.journal.values.get(field + ":" + FP + ":" + RECORD)
                        .equals(f.journal.values.get(field + ":" + expandedFingerprint + ":" + RECORD)),
                        "IB1 update preserves existing " + field + ": " + registered);
            JSONObject exported = new JSONObject();
            invoke(f.activity, "addRegistrationSummary", new Class<?>[]{JSONObject.class, DEVICE_TYPE}, exported, f.device);
            JSONObject summary = exported.getJSONObject("registration");
            check(FP.equals(summary.getString("projectFingerprint")) && RegistrationRecovery.validArchivedManualReview(summary),
                    "reexport keeps original project binding and valid archived review: " + registered);

            // A later automatic snapshot may have no commissioning history. Preserve both outcomes even
            // when the only remaining copy came from the current project's commissioning object.
            f.journal.values.remove(targetKey);
            set(f.device, "commissioning", new JSONObject().put("registration", summary));
            @SuppressWarnings("unchecked")
            Map<String, Object> retained = (Map<String, Object>) invoke(f.activity, "prepareLocalJournal",
                    new Class<?>[]{JSONObject.class, String.class, boolean.class}, withIb1, expandedFingerprint, false);
            check(retained.get(targetKey) instanceof String && RegistrationRecovery.validArchivedManualReview(
                    new JSONObject((String) retained.get(targetKey))), "commissioning-only review survives later snapshot: " + registered);
            f.journal.values.put(targetKey, retained.get(targetKey));
            if (registered) {
                SharedPreferences.Editor accept = f.journal.preferences().edit().remove(targetKey);
                invoke(f.activity, "preserveManualRegistration", new Class<?>[]{SharedPreferences.Editor.class, DEVICE_TYPE}, accept, f.device);
                accept.commit();
                check((Boolean) invoke(f.activity, "manualRegistrationBlocksStart", new Class<?>[]{DEVICE_TYPE}, f.device),
                        "Windows journal acceptance after IB1 update retains the original registered prohibition");
            }

            String otherKey = "registration_summary:" + expandedFingerprint + ":" + OTHER;
            f.journal.values.put(otherKey, summary.toString());
            check((Boolean) invoke(f.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, ib1),
                    "a valid audit copied into another record never releases that record: " + registered);
            f.journal.values.remove(otherKey);
            JSONObject corrupt = new JSONObject(summary.toString());
            corrupt.getJSONArray("history").getJSONObject(0).put("projectFingerprint", expandedFingerprint);
            f.journal.values.put(targetKey, corrupt.toString());
            check((Boolean) invoke(f.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, f.device),
                    "rewriting only the archived fingerprint is rejected: " + registered);
            f.journal.values.put(targetKey, summary.toString());
            Object next = fromStored(value().put("operationId", NEXT).put("projectFingerprint", expandedFingerprint)
                    .put("state", "connecting").put("uncertain", false));
            check(((Boolean) invoke(f.activity, "installOperation", new Class<?>[]{OP_TYPE, OP_TYPE}, next, null)) != registered,
                    "local new-operation installation respects the carried manual finding: " + registered);
        }

        Fixture pending = new Fixture(); set(pending.activity, "projectRoot", originalProject);
        @SuppressWarnings("unchecked")
        Map<String, Object> pendingUpdates = (Map<String, Object>) invoke(pending.activity, "prepareLocalJournal",
                new Class<?>[]{JSONObject.class, String.class, boolean.class}, withIb1, expandedFingerprint, false);
        invoke(pending.activity, "applyJournalUpdates", new Class<?>[]{Map.class}, pendingUpdates);
        set(pending.activity, "projectFingerprint", expandedFingerprint); set(pending.activity, "projectRoot", withIb1);
        JSONObject unresolved = new JSONObject((String) pending.journal.values.get("registration_summary:" + expandedFingerprint + ":" + RECORD));
        check(FP.equals(unresolved.getString("projectFingerprint")) && !unresolved.optBoolean("localResolved"),
                "unfinished operation retains its original fingerprint and unresolved status after IB1 update");
        check((Boolean) invoke(pending.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, pending.device)
                && (Boolean) invoke(pending.activity, "hasUnresolvedRegistrations", new Class<?>[0]),
                "unfinished old operation remains blocked instead of silently becoming a current-project operation");
        check(invoke(pending.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, pending.device) == null,
                "unfinished old operation is never automatically recreated under the new fingerprint");
    }

    private static String fingerprint(String... recordIds) {
        try { return ProtocolV3.projectFingerprint(List.of(recordIds)); }
        catch (Exception error) { throw new AssertionError(error); }
    }

    private static RegistrationRecovery.Identity identity() throws Exception {
        return new RegistrationRecovery.Identity(OP, FP, RECORD, HW, "panel-test");
    }
    private static JSONObject response(String outcome, boolean allowed) throws Exception {
        return new JSONObject().put("ok", true).put("operationId", OP)
                .put("requestId", "00000000-0000-4000-8000-000000000073")
                .put("recovery", identity().write(new JSONObject()).put("outcome", outcome)
                        .put("manualReviewAllowed", allowed).put("message", "Synthetic proof"));
    }
    private static JSONObject value() throws Exception {
        return identity().write(new JSONObject()).put("panelLabel", "Synthetic panel").put("host", "192.0.2.7")
                .put("port", 23019).put("pairingCode", "0011223344556677").put("state", "unknown")
                .put("uncertain", true).put("terminal", false).put("registrationConfirmed", false)
                .put("startedAt", NOW - 1000).put("generation", 1);
    }
    private static Object fromStored(JSONObject value) throws Exception {
        Method method = OP_TYPE.getDeclaredMethod("fromStored", JSONObject.class); method.setAccessible(true);
        return method.invoke(null, value);
    }
    private static Object device(String id) throws Exception {
        Constructor<?> constructor = DEVICE_TYPE.getDeclaredConstructor(); constructor.setAccessible(true);
        Object value = constructor.newInstance(); set(value, "id", id); set(value, "name", "Synthetic record"); return value;
    }

    private static final class Fixture {
        final MainActivity activity;
        final Store bridge = new Store(), journal = new Store();
        final Map<String, Object> operations = new HashMap<>();
        final Set<String> inFlight = new HashSet<>();
        final Object operation, device;
        final String key = "registration:" + FP + ":" + RECORD;
        final String historyKey = "registration-history:" + FP + ":" + RECORD;
        Fixture() throws Exception {
            Class<?> unsafe = Class.forName("sun.misc.Unsafe"); Field singleton = unsafe.getDeclaredField("theUnsafe"); singleton.setAccessible(true);
            activity = (MainActivity) unsafe.getMethod("allocateInstance", Class.class).invoke(singleton.get(null), MainActivity.class);
            set(activity, "prefs", journal.preferences()); set(activity, "bridgePrefs", bridge.preferences());
            set(activity, "operations", operations); set(activity, "operationsInFlight", inFlight);
            set(activity, "operationPolls", new HashMap<>()); set(activity, "projectFingerprint", FP);
            device = device(RECORD); set(activity, "devices", new ArrayList<>(List.of(device)));
            operation = fromStored(value()); operations.put(key, operation); bridge.values.put(key, value().toString());
            journal.values.put("serial:" + FP + ":" + RECORD, HW); journal.values.put("stage:" + FP + ":" + RECORD, 1);
            journal.values.put("device_key:" + FP + ":" + RECORD, "TEST-PRIVATE-KEY");
            journal.values.put("description:" + FP + ":" + RECORD, "Description"); journal.values.put("note:" + FP + ":" + RECORD, "Note");
        }
        RegistrationRecovery.Proof authorize() throws Exception {
            RegistrationRecovery.Proof proof = RegistrationRecovery.authenticatedProof(response("manual_review_required", true), identity(), 1, NOW);
            set(operation, "recoveryProof", proof); return proof;
        }
        Object retire(RegistrationRecovery.Proof proof, boolean stopped, boolean idVerified, long now) throws Exception {
            return invoke(activity, "retireReviewedOperation", new Class<?>[]{OP_TYPE, RegistrationRecovery.Proof.class,
                    boolean.class, boolean.class, boolean.class, long.class}, operation, proof, stopped, idVerified, true, now);
        }
    }

    private static final class Store {
        final Map<String, Object> values = new HashMap<>(); int commits; boolean failNextCommit;
        SharedPreferences preferences() {
            return (SharedPreferences) Proxy.newProxyInstance(getClass().getClassLoader(), new Class<?>[]{SharedPreferences.class}, (proxy, method, args) -> {
                switch (method.getName()) {
                    case "getString": case "getInt": case "getLong": case "getBoolean": return values.getOrDefault(args[0], args[1]);
                    case "contains": return values.containsKey(args[0]);
                    case "getAll": return new HashMap<>(values);
                    case "edit": return editor();
                    default: return null;
                }
            });
        }
        SharedPreferences.Editor editor() {
            Map<String, Object> pending = new HashMap<>(); Set<String> removed = new HashSet<>();
            return (SharedPreferences.Editor) Proxy.newProxyInstance(getClass().getClassLoader(), new Class<?>[]{SharedPreferences.Editor.class}, (proxy, method, args) -> {
                if (method.getName().startsWith("put")) { pending.put((String) args[0], args[1]); return proxy; }
                switch (method.getName()) {
                    case "remove": removed.add((String) args[0]); return proxy;
                    case "apply": case "commit":
                        for (String key : removed) values.remove(key); values.putAll(pending);
                        if (method.getName().equals("apply")) return null;
                        commits++; if (failNextCommit) { failNextCommit = false; return false; } return true;
                    default: return proxy;
                }
            });
        }
    }

    private static Object call(Object target, String name) throws Exception { return invoke(target, name, new Class<?>[0]); }
    private static Object invoke(Object target, String name, Class<?>[] types, Object... args) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types); method.setAccessible(true);
        try { return method.invoke(target, args); }
        catch (InvocationTargetException error) {
            if (error.getCause() instanceof Exception) throw (Exception) error.getCause(); throw error;
        }
    }
    private static Object get(Object target, String name) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(target);
    }
    private static void set(Object target, String name, Object value) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); field.set(target, value);
    }
    private static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); assertions++; }
    private static void rejects(Action action, String message) throws Exception {
        try { action.run(); } catch (IOException | JSONException expected) { assertions++; return; }
        throw new AssertionError(message);
    }
    private interface Action { void run() throws Exception; }
}
