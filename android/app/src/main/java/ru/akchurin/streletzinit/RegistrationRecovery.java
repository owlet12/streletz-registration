// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.IOException;
import java.util.Locale;
import java.util.UUID;

/** Read-only recovery policy. Callers must authenticate/decrypt and bind requestId before using a response. */
final class RegistrationRecovery {
    static final long REVIEW_VALID_MILLIS = 120000;

    static final class Identity {
        final String operationId, projectFingerprint, recordId, expectedHardwareId, panelId;
        Identity(String operationId, String fingerprint, String recordId, String hardwareId, String panelId)
                throws JSONException {
            try { UUID.fromString(operationId); UUID.fromString(recordId); }
            catch (RuntimeException error) { throw new JSONException("Повреждены идентификаторы сохранённой операции."); }
            if (fingerprint == null || !fingerprint.matches("[0-9a-f]{64}") || hardwareId == null
                    || !hardwareId.matches("[0-9A-Fa-f]{12}") || panelId == null || panelId.isEmpty())
                throw new JSONException("Неполные данные сохранённой операции.");
            this.operationId = operationId;
            projectFingerprint = fingerprint;
            this.recordId = recordId;
            expectedHardwareId = hardwareId.toUpperCase(Locale.ROOT);
            this.panelId = panelId;
        }
        JSONObject write(JSONObject value) throws JSONException {
            return value.put("operationId", operationId).put("projectFingerprint", projectFingerprint)
                    .put("recordId", recordId).put("expectedHardwareId", expectedHardwareId).put("panelId", panelId);
        }
        boolean matches(JSONObject value) {
            return value != null && operationId.equals(value.optString("operationId"))
                    && projectFingerprint.equals(value.optString("projectFingerprint"))
                    && recordId.equalsIgnoreCase(value.optString("recordId"))
                    && expectedHardwareId.equalsIgnoreCase(value.optString("expectedHardwareId"))
                    && panelId.equals(value.optString("panelId"));
        }
    }

    static final class Proof {
        final Identity identity;
        final String requestId, outcome, message;
        final long generation, receivedAt;
        final boolean manualReviewAllowed;
        private Proof(Identity identity, JSONObject response, long generation, long now) throws JSONException, IOException {
            JSONObject recovery = response.optJSONObject("recovery");
            if (!Boolean.TRUE.equals(response.opt("ok")) || !identity.operationId.equals(response.optString("operationId"))
                    || !identity.matches(recovery) || !(recovery.opt("manualReviewAllowed") instanceof Boolean))
                throw new IOException("Ответ сверки не соответствует сохранённой операции. Разблокировка не выполнена.");
            outcome = recovery.getString("outcome");
            if (!"restored".equals(outcome) && !"native_active".equals(outcome) && !"manual_review_required".equals(outcome))
                throw new IOException("Windows-мост вернул неизвестный результат сверки.");
            manualReviewAllowed = recovery.getBoolean("manualReviewAllowed");
            if (manualReviewAllowed != "manual_review_required".equals(outcome))
                throw new IOException("Противоречивое разрешение ручной сверки.");
            if ("restored".equals(outcome) && response.optJSONObject("operation") == null)
                throw new IOException("Мост не вернул восстановленную операцию.");
            if (!"restored".equals(outcome) && response.has("operation"))
                throw new IOException("Противоречивый ответ сверки операции.");
            this.identity = identity;
            requestId = response.getString("requestId");
            if (requestId.isEmpty()) throw new IOException("В ответе отсутствует идентификатор запроса сверки.");
            message = recovery.optString("message", "");
            this.generation = generation;
            receivedAt = now;
        }
        boolean canReview(Identity current, long currentGeneration, long now) throws JSONException {
            return manualReviewAllowed && "manual_review_required".equals(outcome)
                    && generation == currentGeneration && identity.matches(current.write(new JSONObject()))
                    && now >= receivedAt && now - receivedAt <= REVIEW_VALID_MILLIS;
        }
    }

    static Proof authenticatedProof(JSONObject response, Identity identity, long generation, long now)
            throws JSONException, IOException {
        return new Proof(identity, response, generation, now);
    }

    static boolean authenticatedHistoryMissing(String requestType, String operationId, JSONObject response) {
        return ("registration_status".equals(requestType) || "cancel_registration".equals(requestType))
                && Boolean.FALSE.equals(response.opt("ok")) && !response.has("operation")
                && "operation_not_found".equals(response.optString("errorCode"))
                // Legacy bridges omit this echo. callBridge has already authenticated and bound requestId
                // to the captured status/cancel request containing this immutable operationId.
                && (!response.has("operationId") || operationId.equals(response.optString("operationId")));
    }

    static boolean unresolvedSummary(JSONObject summary) {
        if (summary == null) return false;
        if (validArchivedManualReview(summary)) return false;
        String state = summary.optString("state", "unknown");
        if ("registered".equals(state) && summary.optBoolean("registrationConfirmed")) return false;
        return summary.optBoolean("uncertain") || summary.optBoolean("bridgeHistoryMissing")
                || ("registered".equals(state) && !summary.optBoolean("registrationConfirmed"))
                || summary.optBoolean("cancelQueued") || !("registered".equals(state)
                || "cancelled".equals(state) || "failed".equals(state) || "timed_out".equals(state));
    }

    static boolean validArchivedManualReview(JSONObject summary) {
        try {
            if (!summary.optBoolean("localResolved") || !"unknown".equals(summary.optString("state"))
                    || summary.optBoolean("registrationConfirmed") || summary.optBoolean("terminal")
                    || !summary.optBoolean("uncertain")) return false;
            long at = summary.getLong("localResolvedAt");
            JSONObject review = summary.getJSONObject("manualReview");
            if (!validManualReview(review, at)) return false;
            Identity identity = new Identity(summary.getString("operationId"), summary.getString("projectFingerprint"),
                    summary.getString("recordId"), summary.getString("expectedHardwareId"), summary.getString("panelId"));
            JSONArray history = summary.getJSONArray("history");
            for (int i = 0; i < history.length(); i++) {
                JSONObject entry = history.getJSONObject(i);
                JSONObject archivedReview = entry.optJSONObject("manualReview");
                if (identity.matches(entry) && entry.optBoolean("localResolved") && "unknown".equals(entry.optString("state"))
                        && !entry.optBoolean("registrationConfirmed") && entry.optLong("localResolvedAt") == at
                        && validManualReview(archivedReview, at)
                        && review.getString("outcome").equals(archivedReview.getString("outcome"))
                        && review.getString("requestId").equals(archivedReview.getString("requestId"))) return true;
            }
        } catch (Exception error) { return false; }
        return false;
    }

    static boolean validManualReview(JSONObject review, long at) {
        if (review == null || at <= 0) return false;
        boolean absent = "not_registered_verified_by_user".equals(review.optString("outcome"))
                && Boolean.TRUE.equals(review.opt("fullIdNotRegistered")) && !review.optBoolean("fullIdRegistered");
        boolean present = "registered_verified_by_user".equals(review.optString("outcome"))
                && Boolean.TRUE.equals(review.opt("fullIdRegistered")) && !review.optBoolean("fullIdNotRegistered");
        return (absent || present)
                && "manual_review_after_readonly_recovery".equals(review.optString("source"))
                && review.optLong("reviewedAt") == at && review.optLong("recoveryReceivedAt") > 0
                && at >= review.optLong("recoveryReceivedAt") && at - review.optLong("recoveryReceivedAt") <= REVIEW_VALID_MILLIS
                && !review.optString("requestId").isEmpty()
                && Boolean.TRUE.equals(review.opt("searchStopped"));
    }

    static boolean manualRegisteredSummary(JSONObject summary) {
        return summary != null && validArchivedManualReview(summary)
                && "registered_verified_by_user".equals(summary.optJSONObject("manualReview").optString("outcome"));
    }

    /** Explicit allowlist: neither pairing credentials nor KEY nor arbitrary server messages enter history. */
    static JSONObject safeSummary(JSONObject source) throws JSONException {
        JSONObject result = safeEntry(source);
        JSONArray history = source.optJSONArray("history");
        if (history != null) {
            JSONArray safe = new JSONArray();
            for (int i = 0; i < history.length(); i++) safe.put(safeEntry(history.getJSONObject(i)));
            result.put("history", safe);
        }
        return result;
    }

    private static JSONObject safeEntry(JSONObject source) throws JSONException {
        JSONObject result = new JSONObject();
        for (String field : new String[]{"operationId", "projectFingerprint", "recordId", "panelId", "panelLabel",
                "expectedHardwareId", "confirmedHardwareId", "state", "registrationEvidence", "host", "connectionSource"})
            if (source.opt(field) instanceof String) result.put(field, source.getString(field));
        for (String field : new String[]{"port", "startedAt", "updatedAt", "expiresAt", "localResolvedAt"})
            if (source.opt(field) instanceof Number) result.put(field, source.getLong(field));
        for (String field : new String[]{"terminal", "registrationConfirmed", "projectSaved", "uncertain",
                "bridgeHistoryMissing", "localResolved"})
            if (source.opt(field) instanceof Boolean) result.put(field, source.getBoolean(field));
        JSONObject review = source.optJSONObject("manualReview");
        if (review != null) {
            JSONObject safe = new JSONObject();
            for (String field : new String[]{"outcome", "source", "requestId"})
                if (review.opt(field) instanceof String) safe.put(field, review.getString(field));
            for (String field : new String[]{"reviewedAt", "recoveryReceivedAt"})
                if (review.opt(field) instanceof Number) safe.put(field, review.getLong(field));
            for (String field : new String[]{"searchStopped", "fullIdNotRegistered", "fullIdRegistered"})
                if (review.opt(field) instanceof Boolean) safe.put(field, review.getBoolean(field));
            result.put("manualReview", safe);
        }
        return result;
    }
}
