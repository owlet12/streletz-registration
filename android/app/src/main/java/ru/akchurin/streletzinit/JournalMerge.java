// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;

/** Exact-GUID carry-over of explicit local edits. Names, addresses and array positions are never matched. */
final class JournalMerge {
    static final String[] LOCAL_FIELDS = {"stage", "serial", "device_key", "note", "updated",
            "description", "registration_summary"};
    private static final String[] CRITICAL_FIELDS = {"stage", "serial", "device_key"};
    static final String CONFLICT_MESSAGE = "В проекте Windows изменены ID, KEY или этап этой записи. "
            + "На телефоне сохранён свой журнал. Сверьте обе версии перед новой регистрацией.";

    private JournalMerge() { }

    static String key(String field, String fingerprint, String id) {
        return field + ":" + fingerprint + ":" + id.toLowerCase(Locale.ROOT);
    }

    static Map<String, Object> carry(Map<String, ?> saved, JSONObject previous, String previousFingerprint,
                                     JSONObject incoming, String incomingFingerprint)
            throws JSONException, GeneralSecurityException {
        Map<String, Object> result = new LinkedHashMap<>();
        Map<String, JSONObject> oldRows = rows(previous);
        for (Map.Entry<String, JSONObject> entry : rows(incoming).entrySet()) {
            String id = entry.getKey();
            JSONObject oldRow = oldRows.get(id);
            if (oldRow == null) continue;
            for (String field : LOCAL_FIELDS) {
                String oldKey = key(field, previousFingerprint, id);
                String newKey = key(field, incomingFingerprint, id);
                if (!saved.containsKey(newKey) && saved.containsKey(oldKey)) result.put(newKey, saved.get(oldKey));
            }
            JSONObject next = entry.getValue();
            String signature = baseSignature(next);
            if (signature.equals(saved.get(key("journal_acknowledged_base", incomingFingerprint, id)))) continue;
            for (String field : CRITICAL_FIELDS) {
                String newKey = key(field, incomingFingerprint, id);
                boolean locallySet = saved.containsKey(newKey) || result.containsKey(newKey);
                Object local = saved.containsKey(newKey) ? saved.get(newKey) : result.get(newKey);
                if (locallySet && !equivalent(field, local, base(next, field))
                        && !equivalent(field, base(oldRow, field), base(next, field))) {
                    result.put(key("journal_conflict", incomingFingerprint, id), CONFLICT_MESSAGE);
                    break;
                }
            }
        }
        return result;
    }

    static Map<String, Object> importJournal(Map<String, ?> saved, JSONObject project, String fingerprint)
            throws JSONException {
        Map<String, Object> result = new LinkedHashMap<>();
        for (Map.Entry<String, JSONObject> entry : rows(project).entrySet()) {
            JSONObject commissioning = entry.getValue().optJSONObject("commissioning");
            if (commissioning == null) continue;
            for (String field : LOCAL_FIELDS) {
                String destination = key(field, fingerprint, entry.getKey());
                if (saved.containsKey(destination)) continue;
                String source = "device_key".equals(field) ? "deviceKey" : "updated".equals(field) ? "updatedAt"
                        : "registration_summary".equals(field) ? "registration" : field;
                if (!commissioning.has(source) || commissioning.isNull(source)) continue;
                if ("stage".equals(field)) result.put(destination, Math.max(0, Math.min(3, commissioning.optInt(source))));
                else if ("updated".equals(field)) result.put(destination, commissioning.optLong(source));
                else if ("registration_summary".equals(field))
                    result.put(destination, commissioning.getJSONObject(source).toString());
                else result.put(destination, commissioning.optString(source, ""));
            }
        }
        return result;
    }

    static Object base(JSONObject row, String field) {
        JSONObject commissioning = row.optJSONObject("commissioning");
        if ("stage".equals(field)) return commissioning != null
                ? Math.max(0, Math.min(3, commissioning.optInt("stage", "+".equals(row.optString("initialized")) ? 2 : 0)))
                : "+".equals(row.optString("initialized")) ? 2 : 0;
        String source = "device_key".equals(field) ? "deviceKey" : field;
        return commissioning == null ? "" : commissioning.optString(source, "");
    }

    static String baseSignature(JSONObject row) throws GeneralSecurityException {
        String value = String.valueOf(base(row, "stage")) + "\n"
                + normalize(base(row, "serial")) + "\n" + normalize(base(row, "device_key"));
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
        StringBuilder text = new StringBuilder();
        for (byte b : hash) text.append(String.format(Locale.ROOT, "%02x", b & 0xff));
        return text.toString();
    }

    private static boolean equivalent(String field, Object a, Object b) {
        return "stage".equals(field) ? String.valueOf(a).equals(String.valueOf(b)) : normalize(a).equals(normalize(b));
    }

    private static String normalize(Object value) { return value == null ? "" : value.toString().trim().toUpperCase(Locale.ROOT); }

    private static Map<String, JSONObject> rows(JSONObject project) throws JSONException {
        Map<String, JSONObject> result = new HashMap<>();
        if (project == null) return result;
        JSONArray rows = project.getJSONArray("records");
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            if ("device".equals(row.optString("recordKind"))) result.put(row.getString("id").toLowerCase(Locale.ROOT), row);
        }
        return result;
    }
}
