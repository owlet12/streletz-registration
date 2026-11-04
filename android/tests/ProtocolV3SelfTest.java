// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import java.util.Base64;

/** Run directly with javac/java; all credentials and device IDs below are synthetic. */
public final class ProtocolV3SelfTest {
    private static int assertions;
    private static final String CODE = "0123456789ABCDEF";
    private static final String REQUEST = "{\"protocol\":3,\"type\":\"list_panels\",\"requestId\":\"test-request\",\"sentAt\":0}";
    private static final String RESPONSE = "{\"protocol\":3,\"type\":\"response\",\"requestId\":\"test-request\",\"ok\":true}";
    private static final String IV = "AAECAwQFBgcICQoLDA0ODw==";
    // Calculated independently with Python hashlib/hmac + cryptography AES-CBC/PKCS7.
    private static final ProtocolV3.Envelope REQUEST_VECTOR = new ProtocolV3.Envelope(IV,
            "KPS24ugnQ46qhyzdATVlWawvzSP+ezox93+bOTv9Ejsj1DMhEqJRXDTB5IzjnxiG5HDdcj6vL5O4Dq5aQw8plF01Pd+IAAWjd1gnorXeTtQ=",
            "xzCfm60sngExc/ruWtSfmLKNqwCE3puydRanEa8UWCE=");
    private static final ProtocolV3.Envelope RESPONSE_VECTOR = new ProtocolV3.Envelope(IV,
            "KPS24ugnQ46qhyzdATVlWeO3BewFw5g5QdgLGfwQDGBmnKWZyOHBTPWjKaYPLiccf4UcAVtNiimk/c4j8j5kQCopwIooTR0HzVX/8JVw/4o=",
            "DBxKCFoSOeaGR//GgOcIeat5AE3AHhnso+6zvIiHbY8=");

    public static void main(String[] args) throws Exception {
        check(CODE.equals(ProtocolV3.normalizePairingCode(" 0123-4567-89ab-cdef ")), "pairing normalization");
        rejects(() -> ProtocolV3.normalizePairingCode("0123 4567 89AB CDEF"), "internal spaces rejected");
        rejects(() -> ProtocolV3.normalizePairingCode("123"), "short pairing code rejected");
        byte[] iv = Base64.getDecoder().decode(IV);
        ProtocolV3.Envelope actual = ProtocolV3.seal(bytes(REQUEST), CODE, "request", iv);
        check(actual.iv.equals(REQUEST_VECTOR.iv), "request IV vector");
        check(actual.data.equals(REQUEST_VECTOR.data), "request AES vector");
        check(actual.mac.equals(REQUEST_VECTOR.mac), "request HMAC vector");
        check(RESPONSE.equals(new String(ProtocolV3.decryptResponse(RESPONSE_VECTOR, CODE), StandardCharsets.UTF_8)),
                "independent response vector decrypts");
        check(Arrays.equals(bytes(REQUEST), ProtocolV3.open(REQUEST_VECTOR, CODE, "request")), "request round trip");
        rejects(() -> ProtocolV3.decryptResponse(REQUEST_VECTOR, CODE), "request cannot be reflected as response");
        rejects(() -> ProtocolV3.open(RESPONSE_VECTOR, CODE, "request"), "response cannot be reflected as request");
        rejects(() -> ProtocolV3.decryptResponse(RESPONSE_VECTOR, "FEDCBA9876543210"), "wrong pairing code");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope(flip(IV), RESPONSE_VECTOR.data,
                RESPONSE_VECTOR.mac), CODE), "tampered IV");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope(IV, flip(RESPONSE_VECTOR.data),
                RESPONSE_VECTOR.mac), CODE), "tampered ciphertext");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope(IV, RESPONSE_VECTOR.data,
                flip(RESPONSE_VECTOR.mac)), CODE), "tampered MAC");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope("AA==", RESPONSE_VECTOR.data,
                RESPONSE_VECTOR.mac), CODE), "short IV");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope(IV, "!!!", RESPONSE_VECTOR.mac), CODE),
                "malformed base64");
        // A last-block mutation would break padding; authentication must reject it first.
        byte[] tail = Base64.getDecoder().decode(RESPONSE_VECTOR.data);
        tail[tail.length - 1] ^= 1;
        try {
            ProtocolV3.decryptResponse(new ProtocolV3.Envelope(IV, Base64.getEncoder().encodeToString(tail),
                    RESPONSE_VECTOR.mac), CODE);
            throw new AssertionError("tamper accepted");
        } catch (GeneralSecurityException expected) {
            check(expected.getMessage().contains("Подлинность"), "MAC is checked before padding/decrypt");
        }
        ProtocolV3.Envelope randomA = ProtocolV3.encryptRequest(bytes(REQUEST), CODE);
        ProtocolV3.Envelope randomB = ProtocolV3.encryptRequest(bytes(REQUEST), CODE);
        check(!randomA.iv.equals(randomB.iv), "production IV is fresh");
        ProtocolV3.verifyResponseMetadata(3, "response", "request-1", "request-1");
        assertions++;
        rejects(() -> ProtocolV3.verifyResponseMetadata(3, "response", "request-1", "request-2"), "wrong request ID");
        rejects(() -> ProtocolV3.verifyResponseMetadata(2, "response", "request-1", "request-1"), "protocol downgrade");
        rejects(() -> ProtocolV3.verifyResponseMetadata(3, "bridge-request", "request-1", "request-1"), "inner type binding");
        String fingerprint = ProtocolV3.projectFingerprint(Arrays.asList("C-Record", "a-record", "B-RECORD"));
        check(fingerprint.equals("67a47d11ae1475602b0896fa4498f7ebb2874cfd4ee3a0d21fea9330e192bbfb"),
                "project fingerprint independent vector");
        check(fingerprint.equals(ProtocolV3.projectFingerprint(Arrays.asList("b-record", "c-record", "A-Record"))),
                "project order/case canonicalization");
        check(!fingerprint.equals(ProtocolV3.projectFingerprint(Arrays.asList("a-record", "b-record"))),
                "fingerprint includes every record");

        check(confirmation("op-1", "panel-1", "record-1", "AABBCCDDEEFF", "registered", true), "confirmed actual ID");
        check(confirmation("op-1", "panel-1", "RECORD-1", "aabbccddeeff", "registered", true), "hex/UUID case");
        check(!confirmation("op-1", "panel-1", "record-1", "AABBCCDDEEFF", "registered", false), "ok alone is insufficient");
        check(!confirmation("op-1", "panel-1", "record-1", "AABBCCDDEEFF", "waiting_for_sensor", true), "sent job is insufficient");
        check(!confirmation("op-1", "panel-1", "record-1", "001122334455", "registered", true), "wrong actual sensor");
        check(!confirmation("op-1", "panel-1", "record-1", "", "registered", true), "missing actual ID");
        check(!confirmation("op-1", "panel-1", "record-1", null, "registered", true), "null actual ID");
        check(!confirmation("op-2", "panel-1", "record-1", "AABBCCDDEEFF", "registered", true), "wrong operation");
        check(!confirmation("op-1", "panel-2", "record-1", "AABBCCDDEEFF", "registered", true), "wrong panel");
        check(!confirmation("op-1", "panel-1", "record-2", "AABBCCDDEEFF", "registered", true), "wrong project record");
        check(!rejected(null, "native_outcome_unknown"), "unclassified native outcome is not a definitive rejection");
        check(!rejected(null, "bridge_error"), "authenticated backend error does not prove no side effects");
        check(!rejected(null, "driver_unavailable"), "even a recognized code needs explicit accepted false");
        check(!rejected(Boolean.TRUE, "driver_unavailable"), "an accepted operation is not rejected");
        check(!rejected(Boolean.FALSE, "native_outcome_unknown"), "unknown native outcome never releases operation ID");
        check(!rejected(Boolean.FALSE, "native_ui_busy"), "late UI start remains ambiguous");
        check(!rejected(Boolean.FALSE, "bridge_error"), "unexpected errors are not allowlisted");
        check(!rejected(Boolean.FALSE, "operation_not_found"), "missing server history cannot prove no previous action");
        check(rejected(Boolean.FALSE, "driver_unavailable"), "explicit pre-dispatch driver rejection");
        check(rejected(Boolean.FALSE, "project_mismatch"), "explicit project rejection");
        check(rejected(Boolean.FALSE, "journal_unavailable"), "explicit failure before durable dispatch");
        check(!ProtocolV3.isExplicitPredispatchRejection("registration_status", false, Boolean.FALSE,
                "driver_unavailable"), "status errors cannot invalidate an already-started operation");
        check(!ProtocolV3.isExplicitPredispatchRejection("cancel_registration", false, Boolean.FALSE,
                "driver_unavailable"), "cancel failure cannot release registration identity");
        check(!ProtocolV3.isExplicitPredispatchRejection("begin_registration", true, Boolean.FALSE,
                "driver_unavailable"), "contradictory ok response is ambiguous");
        StringBuilder catalogue = new StringBuilder("{\"projectPanels\":[");
        for (int i = 0; i < 453; i++) {
            if (i != 0) catalogue.append(',');
            catalogue.append("{\"recordId\":\"00000000-0000-4000-8000-")
                    .append(String.format("%012d", i)).append("\",\"description\":\"Дымовой извещатель, помещение ")
                    .append(i).append(", проектная подпись для проверки полного каталога\"}");
        }
        byte[] fullCatalogue = bytes(catalogue.append("]}").toString());
        check(fullCatalogue.length > 48000, "453-record UTF-8 response exceeds legacy budget");
        ProtocolV3.Envelope largeResponse = ProtocolV3.seal(fullCatalogue, CODE, "response", iv);
        check(Arrays.equals(fullCatalogue, ProtocolV3.decryptResponse(largeResponse, CODE)), "complete project catalogue round trip");
        rejects(() -> ProtocolV3.encryptRequest(fullCatalogue, CODE), "larger response budget does not enlarge requests");
        rejects(() -> ProtocolV3.open(largeResponse, CODE, "request"), "large response cannot be reflected as request");
        rejects(() -> ProtocolV3.decryptResponse(new ProtocolV3.Envelope(largeResponse.iv, largeResponse.data,
                flip(largeResponse.mac)), CODE), "catalogue still requires valid MAC");
        byte[] limit = new byte[ProtocolV3.MAX_RESPONSE_PLAINTEXT_BYTES];
        Arrays.fill(limit, (byte) 'x');
        check(Arrays.equals(limit, ProtocolV3.decryptResponse(ProtocolV3.seal(limit, CODE, "response", iv), CODE)),
                "exact response limit accepted with CBC padding");
        rejects(() -> ProtocolV3.seal(new byte[ProtocolV3.MAX_RESPONSE_PLAINTEXT_BYTES + 1], CODE, "response", iv),
                "oversized response rejected");
        rejects(() -> ProtocolV3.encryptRequest(new byte[48001], CODE), "request plaintext cap remains 48000 bytes");
        System.out.println("ProtocolV3: " + assertions + " assertions passed (synthetic fixtures only)");
    }

    private static boolean confirmation(String op, String panel, String record, String actualId, String state, boolean confirmed) {
        return ProtocolV3.confirmsRegistration("op-1", "panel-1", "record-1", "AABBCCDDEEFF",
                op, panel, record, actualId, state, confirmed);
    }
    private static boolean rejected(Boolean accepted, String errorCode) {
        return ProtocolV3.isExplicitPredispatchRejection("begin_registration", false, accepted, errorCode);
    }
    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }
    private static String flip(String value) {
        byte[] bytes = Base64.getDecoder().decode(value);
        bytes[0] ^= 1;
        return Base64.getEncoder().encodeToString(bytes);
    }
    private static void check(boolean condition, String name) {
        if (!condition) throw new AssertionError(name);
        assertions++;
    }
    private static void rejects(CheckedAction action, String name) throws Exception {
        try { action.run(); }
        catch (GeneralSecurityException | IllegalArgumentException expected) { assertions++; return; }
        throw new AssertionError(name);
    }
    private interface CheckedAction { void run() throws Exception; }
}
