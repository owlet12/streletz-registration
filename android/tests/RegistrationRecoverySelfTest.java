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

/** Production policy and storage tests; no Android UI, native Master, network or hardware claim. */
public final class RegistrationRecoverySelfTest {
    private static int assertions;
    private static final String OP = "00000000-0000-4000-8000-000000000091";
    private static final String NEXT = "00000000-0000-4000-8000-000000000092";
    private static final String RECORD = "00000000-0000-4000-8000-000000000001";
    private static final String FP = "a".repeat(64);
    private static final String HW = "001122334455";
    private static final long NOW = 1000000;
    private static final Class<?> OP_TYPE;
    private static final Class<?> DEVICE_TYPE;
    static {
        try {
            OP_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$RegistrationOperation");
            DEVICE_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$Device");
        } catch (Exception error) { throw new ExceptionInInitializerError(error); }
    }

    public static void main(String[] args) throws Exception {
        policyTests();
        persistenceTests();
        confirmedResultTests();
        importTests();
        System.out.println("RegistrationRecoverySelfTest: " + assertions + " assertions passed (policy/storage only)");
    }

    private static RegistrationRecovery.Identity identity() throws Exception {
        return new RegistrationRecovery.Identity(OP, FP, RECORD, HW, "panel-a");
    }
    private static JSONObject response(String outcome, boolean allowed) throws Exception {
        return new JSONObject().put("protocol", 3).put("type", "response").put("requestId", "recovery-request")
                .put("operationId", OP).put("ok", true).put("recovery", identity().write(new JSONObject())
                        .put("outcome", outcome).put("manualReviewAllowed", allowed).put("message", "Synthetic recovery"));
    }
    private static RegistrationRecovery.Proof proof() throws Exception {
        return RegistrationRecovery.authenticatedProof(response("manual_review_required", true), identity(), 1, NOW);
    }
    private static JSONObject missing() {
        return new JSONObject().put("ok", false).put("operationId", OP).put("errorCode", "operation_not_found");
    }

    private static void policyTests() throws Exception {
        check(RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, missing()), "matching authenticated status recognizes missing history");
        check(RegistrationRecovery.authenticatedHistoryMissing("cancel_registration", OP, missing()), "matching authenticated cancel recognizes missing history");
        JSONObject legacy = missing(); legacy.remove("operationId");
        check(RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, legacy), "legacy missing echo relies on authenticated requestId binding");
        check(!RegistrationRecovery.authenticatedHistoryMissing("begin_registration", OP, missing()), "missing on begin never proves no hardware effect");
        check(!RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, missing().put("operationId", NEXT)), "wrong operation missing echo rejected");
        check(!RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, missing().put("ok", true)), "contradictory successful error rejected");
        check(!RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, missing().put("operation", new JSONObject())), "missing error cannot override operation body");
        for (String error : new String[]{"timeout", "native_pipe_connect_timeout", "native_operation_not_found", "invalid_operation", ""})
            check(!RegistrationRecovery.authenticatedHistoryMissing("registration_status", OP, missing().put("errorCode", error)), "ordinary errors do not become bridge history missing: " + error);
        check(!ProtocolV3.isExplicitPredispatchRejection("cancel_registration", false, false, "operation_not_found"), "missing does not become failed/cancelled");

        RegistrationRecovery.Proof proof = proof();
        check(proof.canReview(identity(), 1, NOW + 1), "fresh manual proof permits explicit review");
        check(!proof.canReview(identity(), 2, NOW + 1), "proof is bound to operation generation");
        check(!proof.canReview(identity(), 1, NOW - 1), "backward clock cannot refresh proof");
        check(!proof.canReview(identity(), 1, NOW + RegistrationRecovery.REVIEW_VALID_MILLIS + 1), "expired review authorization rejected");
        check(!RegistrationRecovery.authenticatedProof(response("native_active", false), identity(), 1, NOW)
                .canReview(identity(), 1, NOW + 1), "active native operation cannot authorize manual release");
        for (String field : new String[]{"operationId", "projectFingerprint", "recordId", "expectedHardwareId", "panelId"}) {
            JSONObject mismatch = response("manual_review_required", true);
            mismatch.getJSONObject("recovery").put(field, "wrong");
            rejects(() -> RegistrationRecovery.authenticatedProof(mismatch, identity(), 1, NOW), "recovery echo mismatch rejected: " + field);
            JSONObject absent = response("manual_review_required", true); absent.getJSONObject("recovery").remove(field);
            rejects(() -> RegistrationRecovery.authenticatedProof(absent, identity(), 1, NOW), "missing recovery echo rejected: " + field);
        }
        rejects(() -> RegistrationRecovery.authenticatedProof(response("manual_review_required", true).put("operationId", NEXT), identity(), 1, NOW), "wrong top-level operation rejected");
        rejects(() -> RegistrationRecovery.authenticatedProof(response("manual_review_required", true).put("ok", false), identity(), 1, NOW), "error response cannot authorize manual review");
        rejects(() -> RegistrationRecovery.authenticatedProof(response("native_active", true), identity(), 1, NOW), "active plus manual true contradiction rejected");
        rejects(() -> RegistrationRecovery.authenticatedProof(response("manual_review_required", false), identity(), 1, NOW), "manual without authorization rejected");
        rejects(() -> RegistrationRecovery.authenticatedProof(response("restored", false), identity(), 1, NOW), "restored without operation rejected");
        rejects(() -> RegistrationRecovery.authenticatedProof(response("manual_review_required", true).put("operation", new JSONObject()), identity(), 1, NOW), "manual with stale operation rejected");
        JSONObject wrongType = response("manual_review_required", true); wrongType.getJSONObject("recovery").put("manualReviewAllowed", "true");
        rejects(() -> RegistrationRecovery.authenticatedProof(wrongType, identity(), 1, NOW), "string true does not authorize review");
        JSONObject restored = response("restored", false).put("operation", operationValue());
        check(!RegistrationRecovery.authenticatedProof(restored, identity(), 1, NOW).manualReviewAllowed, "authoritative restoration is separate from manual review");

        JSONObject unsafe = operationValue().put("deviceKey", "SYNTHETIC-KEY").put("note", "private note")
                .put("pairing_code", "SYNTHETIC-PAIR").put("message", "unfiltered server content");
        JSONObject safe = RegistrationRecovery.safeSummary(unsafe);
        for (String forbidden : new String[]{"deviceKey", "pairingCode", "pairing_code", "note", "message", "deliveryMessage"})
            check(!safe.has(forbidden), "safe history excludes " + forbidden);
        check(identity().matches(safe) && safe.getString("host").equals("192.0.2.4"), "safe transfer preserves original identities and endpoint");
        unsafe.put("history", new JSONArray().put(operationValue().put("deviceKey", "SYNTHETIC-KEY")));
        check(!RegistrationRecovery.safeSummary(unsafe).getJSONArray("history").getJSONObject(0).has("deviceKey"), "nested history sanitized too");
    }

    private static void persistenceTests() throws Exception {
        Fixture f = new Fixture();
        Object operation = f.operation;
        check((Boolean) call(operation, "needsResolution"), "unknown remains locked");
        check((Boolean) call(operation, "shouldAutoPoll"), "ordinary uncertainty may still query same operation");
        set(operation, "terminal", true);
        check((Boolean) call(operation, "canRecover"), "preexisting terminal unknown is recoverable");
        check(call(operation, "actionLabel").equals("Требуется сверка результата"), "terminal unknown never says running");
        set(operation, "terminal", false);
        invoke(f.activity, "applyMissingHistory", new Class<?>[]{OP_TYPE, JSONObject.class}, operation, missing());
        check((Boolean) get(operation, "bridgeHistoryMissing"), "missing marker set by typed response");
        check(!(Boolean) call(operation, "shouldAutoPoll"), "missing stops two second polling");
        check((Boolean) call(operation, "needsResolution"), "stopping polling does not unlock");
        check(get(operation, "state").equals("unknown") && !(Boolean) get(operation, "registrationConfirmed"), "missing neither confirms nor fails operation");
        check((Boolean) invoke(f.activity, "saveOperation", new Class<?>[]{OP_TYPE, boolean.class}, operation, true), "missing marker saved durably");
        Object reloaded = fromStored(new JSONObject((String) f.bridge.values.get(f.key)));
        check((Boolean) get(reloaded, "bridgeHistoryMissing") && !(Boolean) call(reloaded, "shouldAutoPoll"), "missing survives restart without automatic polling");

        JSONObject wrong = operationValue().put("operationId", NEXT).put("state", "registered").put("registrationConfirmed", true);
        rejects(() -> invoke(f.activity, "applyOperationAnswer", new Class<?>[]{OP_TYPE, JSONObject.class}, operation, wrong), "another operation status cannot clear missing");
        check((Boolean) get(operation, "bridgeHistoryMissing"), "failed identity check preserves missing lock");
        JSONObject valid = operationValue().put("state", "unknown").put("updatedAt", NOW);
        invoke(f.activity, "applyOperationAnswer", new Class<?>[]{OP_TYPE, JSONObject.class}, operation, valid);
        check(!(Boolean) get(operation, "bridgeHistoryMissing") && (Boolean) call(operation, "needsResolution"), "known authoritative unknown clears missing marker but stays locked");
        set(operation, "bridgeHistoryMissing", true);
        set(operation, "recoveryProof", proof());
        check(!(Boolean) call(operation, "shouldAutoPoll"), "manual review never races an automatic poll");
        RegistrationRecovery.Proof permission = (RegistrationRecovery.Proof) get(operation, "recoveryProof");
        Map<String, Object> journal = new HashMap<>(f.journal.values);
        rejects(() -> f.retire(permission, false, true, NOW + 10), "search-stopped checkbox required");
        rejects(() -> f.retire(permission, true, false, NOW + 10), "full hardware absence checkbox required");
        rejects(() -> f.retire(permission, true, true, NOW + RegistrationRecovery.REVIEW_VALID_MILLIS + 1), "stale manual authorization cannot retire");
        f.inFlight.add(OP);
        rejects(() -> f.retire(permission, true, true, NOW + 10), "inflight request blocks retirement");
        f.inFlight.clear();
        set(operation, "recoveryProof", null);
        rejects(() -> f.retire(permission, true, true, NOW + 10), "a later request invalidates old review proof");
        set(operation, "recoveryProof", permission);
        int before = f.bridge.commits;
        Object retired = f.retire(permission, true, true, NOW + 10);
        check(f.bridge.commits == before + 1, "archive and retired slot use exactly one successful commit");
        check(f.journal.values.equals(journal), "manual retirement preserves all stored KEY/serial/description/note/stage data");
        check(!(Boolean) call(retired, "needsResolution") && !(Boolean) call(retired, "shouldAutoPoll"), "reviewed operation releases only local waiting");
        check(get(retired, "state").equals("unknown") && !(Boolean) get(retired, "terminal")
                && !(Boolean) get(retired, "registrationConfirmed"), "manual review does not fake registered/cancelled/failed/terminal state");
        JSONArray history = new JSONArray((String) f.bridge.values.get(f.historyKey));
        check(history.length() == 1 && history.getJSONObject(0).getBoolean("localResolved"), "audit history committed with retired slot");
        check(!history.toString().contains("pairingCode") && !history.toString().contains("deviceKey"), "archive contains no transport code or device key");
        check(!(Boolean) invoke(f.activity, "isCurrentOperation", new Class<?>[]{OP_TYPE, long.class}, operation, 1L), "late old callback rejected after retirement");
        String storedRetired = (String) f.bridge.values.get(f.key);
        check(!(Boolean) invoke(f.activity, "saveOperation", new Class<?>[]{OP_TYPE, boolean.class}, operation, true)
                && storedRetired.equals(f.bridge.values.get(f.key)), "late old save cannot overwrite retired slot");
        f.operations.clear();
        Object afterRestart = invoke(f.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, f.device);
        check(afterRestart != null && !(Boolean) call(afterRestart, "needsResolution"), "durable reviewed state validates against archive after restart");
        JSONObject exported = new JSONObject();
        invoke(f.activity, "addRegistrationSummary", new Class<?>[]{JSONObject.class, DEVICE_TYPE}, exported, f.device);
        JSONObject summary = exported.getJSONObject("registration");
        check(RegistrationRecovery.validArchivedManualReview(summary), "portable retired marker requires matching valid audit history");
        check(!RegistrationRecovery.unresolvedSummary(summary), "valid imported reviewed history is not silently reblocked");
        check(!summary.has("pairingCode") && !summary.has("deviceKey"), "portable history remains safe");
        JSONObject forged = new JSONObject(summary.toString()); forged.remove("history");
        check(RegistrationRecovery.unresolvedSummary(forged), "localResolved alone cannot bypass imported unknown");

        Object next = fromStored(operationValue().put("operationId", NEXT).put("state", "connecting").put("uncertain", false));
        check((Boolean) invoke(f.activity, "installOperation", new Class<?>[]{OP_TYPE, OP_TYPE}, next, afterRestart), "new operation can only be separately installed after explicit review");
        check(!(Boolean) invoke(f.activity, "saveOperation", new Class<?>[]{OP_TYPE, boolean.class}, operation, true), "callback from old UUID cannot overwrite new slot");
        check(new JSONObject((String) f.bridge.values.get(f.key)).getString("operationId").equals(NEXT), "new slot identity retained after late save");
        check(new JSONArray((String) f.bridge.values.get(f.historyKey)).length() == 1, "new slot never deletes old history");

        Fixture fail = new Fixture();
        RegistrationRecovery.Proof failureProof = proof(); set(fail.operation, "recoveryProof", failureProof);
        String original = (String) fail.bridge.values.get(fail.key);
        fail.bridge.failNextCommits = 1;
        rejects(() -> fail.retire(failureProof, true, true, NOW + 10), "failed commit refuses retirement");
        check(fail.operations.get(fail.key) == fail.operation && (Boolean) call(fail.operation, "needsResolution"), "failed commit never publishes retired memory object");
        check(original.equals(fail.bridge.values.get(fail.key)) && !fail.bridge.values.containsKey(fail.historyKey), "failed commit memory side effect is rolled back atomically");
        check(fail.journal.values.get("stage:" + FP + ":" + RECORD).equals(1), "failed retirement never promotes stage");
        fail.operations.clear();
        check((Boolean) call(invoke(fail.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, fail.device), "needsResolution"), "activity reload after commit failure stays locked");

        Fixture corrupt = new Fixture();
        corrupt.operations.clear(); corrupt.bridge.values.put(corrupt.key, "not json");
        check(invoke(corrupt.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, corrupt.device) == null, "corrupt stored operation is not reinterpreted");
        check((Boolean) invoke(corrupt.activity, "hasUnresolvedRegistrations", new Class<?>[0]), "corrupt persistent operation still blocks sync/new start");
        check(!(Boolean) invoke(corrupt.activity, "installOperation", new Class<?>[]{OP_TYPE, OP_TYPE}, next, null), "corrupt slot cannot be replaced with fresh operation");

        Fixture damagedArchive = new Fixture(); RegistrationRecovery.Proof archiveProof = proof();
        set(damagedArchive.operation, "recoveryProof", archiveProof); damagedArchive.retire(archiveProof, true, true, NOW + 10);
        damagedArchive.bridge.values.remove(damagedArchive.historyKey); damagedArchive.operations.clear();
        check(invoke(damagedArchive.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, damagedArchive.device) == null,
                "retired marker without durable matching archive is corrupt and cannot unlock");
        check((Boolean) invoke(damagedArchive.activity, "hasUnresolvedRegistrations", new Class<?>[0]), "lost retirement audit blocks project sync");

        Fixture bothFail = new Fixture(); RegistrationRecovery.Proof bothProof = proof();
        set(bothFail.operation, "recoveryProof", bothProof); bothFail.bridge.failNextCommits = 2;
        rejects(() -> bothFail.retire(bothProof, true, true, NOW + 10), "failed retirement and rollback report failure");
        bothFail.operations.clear();
        check(invoke(bothFail.activity, "operationFor", new Class<?>[]{DEVICE_TYPE}, bothFail.device) == null,
                "storage uncertainty remains blocked across activity recreation");
        check((Boolean) invoke(bothFail.activity, "hasUnresolvedRegistrations", new Class<?>[0]), "failed rollback never authorizes a new operation");
        Field uncertain = MainActivity.class.getDeclaredField("retirementStorageUncertain"); uncertain.setAccessible(true);
        ((Set<?>) uncertain.get(null)).clear(); // Isolate later fixtures; the production process deliberately retains this guard.
    }

    private static void importTests() throws Exception {
        Fixture imported = new Fixture(); imported.operations.clear(); imported.bridge.values.remove(imported.key);
        JSONObject legacy = new JSONObject().put("operationId", OP).put("panelId", "panel-a")
                .put("expectedHardwareId", HW).put("state", "unknown").put("registrationConfirmed", false);
        imported.journal.values.put("registration_summary:" + FP + ":" + RECORD, legacy.toString());
        check((Boolean) invoke(imported.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, imported.device), "legacy imported unknown is not silently unlocked");
        check((Boolean) invoke(imported.activity, "hasUnresolvedRegistrations", new Class<?>[0]), "legacy imported unknown blocks automatic project replacement");
        check(imported.bridge.values.get(imported.key) == null, "reading legacy summary does not create or replay a registration");
        JSONObject export = new JSONObject();
        invoke(imported.activity, "addRegistrationSummary", new Class<?>[]{JSONObject.class, DEVICE_TYPE}, export, imported.device);
        check(export.getJSONObject("registration").getString("operationId").equals(OP), "legacy unresolved operation identity survives reexport");
        imported.journal.values.put("registration_summary:" + FP + ":" + RECORD, "corrupt");
        check((Boolean) invoke(imported.activity, "importedRegistrationNeedsResolution", new Class<?>[]{DEVICE_TYPE}, imported.device), "corrupt imported history remains blocked");
        check(!RegistrationRecovery.unresolvedSummary(new JSONObject().put("state", "registered").put("registrationConfirmed", true)), "legacy completed history is still readable");
    }

    private static void confirmedResultTests() throws Exception {
        Fixture f = new Fixture();
        JSONObject confirmed = operationValue().put("state", "registered").put("registrationConfirmed", true)
                .put("confirmedHardwareId", HW).put("registrationEvidence", "native_confirmation")
                .put("terminal", true).put("uncertain", false).put("projectSaved", false).put("updatedAt", NOW);
        invoke(f.activity, "applyOperationAnswer", new Class<?>[]{OP_TYPE, JSONObject.class}, f.operation, confirmed);
        check((Boolean) get(f.operation, "registrationConfirmed") && (Boolean) call(f.operation, "needsWindowsSave"), "confirmed unsaved result offers explicit Windows save");
        check(!(Boolean) call(f.operation, "canRecover"), "confirmed result never enters unknown manual-release path");
        check(call(f.operation, "actionLabel").equals("Устройство уже инициализировано"), "confirmed unsaved result never enables another registration");
        invoke(f.activity, "applyMissingHistory", new Class<?>[]{OP_TYPE, JSONObject.class}, f.operation, missing());
        check(get(f.operation, "state").equals("registered") && (Boolean) get(f.operation, "registrationConfirmed")
                && get(f.operation, "confirmedHardwareId").equals(HW), "bridge missing history cannot downgrade prior confirmation");
        check(get(f.operation, "registrationEvidence").equals("native_confirmation"), "missing history retains native evidence");
        check(!(Boolean) call(f.operation, "shouldAutoPoll"), "missing confirmed history does not replay automatic save/start");
        check((Boolean) call(f.operation, "needsWindowsSave"), "explicit save remains available after missing response");
        RegistrationRecovery.Proof review = proof(); set(f.operation, "recoveryProof", review);
        rejects(() -> f.retire(review, true, true, NOW + 10), "even matching manual proof cannot retire an already confirmed operation");
        JSONObject unknown = operationValue().put("updatedAt", NOW + 1);
        rejects(() -> invoke(f.activity, "applyOperationAnswer", new Class<?>[]{OP_TYPE, JSONObject.class}, f.operation, unknown), "restored unknown cannot erase already confirmed result");
        check((Boolean) get(f.operation, "registrationConfirmed") && get(f.operation, "state").equals("registered"), "unknown restore rejection leaves confirmed state intact");
        JSONObject saved = new JSONObject(confirmed.toString()).put("projectSaved", true).put("updatedAt", NOW + 2);
        invoke(f.activity, "applyOperationAnswer", new Class<?>[]{OP_TYPE, JSONObject.class}, f.operation, saved);
        check((Boolean) get(f.operation, "registrationConfirmed") && (Boolean) get(f.operation, "projectSaved")
                && !(Boolean) call(f.operation, "needsWindowsSave"), "matching saved result clears save retry without changing registration");
    }

    private static JSONObject operationValue() throws Exception {
        return identity().write(new JSONObject()).put("panelLabel", "Synthetic panel").put("host", "192.0.2.4").put("port", 23019)
                .put("pairingCode", "0011223344556677").put("state", "unknown").put("uncertain", true)
                .put("terminal", false).put("registrationConfirmed", false).put("startedAt", NOW - 1000);
    }
    private static Object fromStored(JSONObject value) throws Exception {
        Method method = OP_TYPE.getDeclaredMethod("fromStored", JSONObject.class); method.setAccessible(true);
        return method.invoke(null, value);
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
            Class<?> unsafeClass = Class.forName("sun.misc.Unsafe"); Field field = unsafeClass.getDeclaredField("theUnsafe"); field.setAccessible(true);
            activity = (MainActivity) unsafeClass.getMethod("allocateInstance", Class.class).invoke(field.get(null), MainActivity.class);
            set(activity, "prefs", journal.preferences()); set(activity, "bridgePrefs", bridge.preferences());
            set(activity, "operations", operations); set(activity, "operationsInFlight", inFlight);
            set(activity, "operationPolls", new HashMap<>()); set(activity, "projectFingerprint", FP);
            Constructor<?> constructor = DEVICE_TYPE.getDeclaredConstructor(); constructor.setAccessible(true); device = constructor.newInstance();
            set(device, "id", RECORD); set(device, "name", "Synthetic record");
            set(activity, "devices", new ArrayList<>(List.of(device)));
            operation = fromStored(operationValue()); operations.put(key, operation); bridge.values.put(key, operationValue().toString());
            journal.values.put("serial:" + FP + ":" + RECORD, HW); journal.values.put("stage:" + FP + ":" + RECORD, 1);
            journal.values.put("device_key:" + FP + ":" + RECORD, "SYNTHETIC-KEY");
            journal.values.put("description:" + FP + ":" + RECORD, "description"); journal.values.put("note:" + FP + ":" + RECORD, "note");
        }
        Object retire(RegistrationRecovery.Proof proof, boolean stopped, boolean absent, long now) throws Exception {
            return invoke(activity, "retireReviewedOperation", new Class<?>[]{OP_TYPE, RegistrationRecovery.Proof.class, boolean.class, boolean.class, long.class},
                    operation, proof, stopped, absent, now);
        }
    }

    private static final class Store {
        final Map<String, Object> values = new HashMap<>();
        int commits, failNextCommits;
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
                    case "clear": removed.addAll(values.keySet()); return proxy;
                    case "apply": case "commit":
                        for (String key : removed) values.remove(key);
                        values.putAll(pending); // Android updates the in-process view even if writing the disk then fails.
                        if (method.getName().equals("apply")) return null;
                        commits++; if (failNextCommits > 0) { failNextCommits--; return false; } return true;
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
    private static Object get(Object target, String name) throws Exception { Field f = target.getClass().getDeclaredField(name); f.setAccessible(true); return f.get(target); }
    private static void set(Object target, String name, Object value) throws Exception { Field f = target.getClass().getDeclaredField(name); f.setAccessible(true); f.set(target, value); }
    private static void check(boolean value, String label) { if (!value) throw new AssertionError(label); assertions++; }
    private static void rejects(Action action, String label) throws Exception {
        try { action.run(); } catch (IOException | JSONException expected) { assertions++; return; }
        throw new AssertionError(label);
    }
    private interface Action { void run() throws Exception; }
}
