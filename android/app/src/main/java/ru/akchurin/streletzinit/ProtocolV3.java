// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/** The v3 wire cryptography. No Android or JSON dependency; usable in JVM tests. */
public final class ProtocolV3 {
    public static final int VERSION = 3;
    public static final int MAX_REQUEST_PLAINTEXT_BYTES = 48000;
    public static final int MAX_RESPONSE_PLAINTEXT_BYTES = 16 * 1024 * 1024;
    public static final int MAX_ENVELOPE_BYTES = 64 * 1024;
    public static final int MAX_RESPONSE_ENVELOPE_BYTES = 24 * 1024 * 1024;
    private static final SecureRandom RANDOM = new SecureRandom();

    private ProtocolV3() { }

    public static final class Envelope {
        public final String iv;
        public final String data;
        public final String mac;

        public Envelope(String iv, String data, String mac) {
            this.iv = iv;
            this.data = data;
            this.mac = mac;
        }
    }

    public static String normalizePairingCode(String code) {
        String normalized = code == null ? "" : code.trim().replace("-", "").toUpperCase(Locale.ROOT);
        if (!normalized.matches("[0-9A-F]{16}")) {
            throw new IllegalArgumentException("Код подключения должен содержать 16 шестнадцатеричных символов");
        }
        return normalized;
    }

    public static Envelope encryptRequest(byte[] plaintext, String code) throws GeneralSecurityException {
        byte[] iv = new byte[16];
        RANDOM.nextBytes(iv);
        return seal(plaintext, code, "request", iv);
    }

    // Package visibility permits a deterministic interoperability vector without a production fixed IV.
    static Envelope seal(byte[] plaintext, String code, String direction, byte[] iv)
            throws GeneralSecurityException {
        if (plaintext == null || plaintext.length > plaintextLimit(direction) || iv == null || iv.length != 16) {
            throw new GeneralSecurityException("Некорректный размер сообщения");
        }
        byte[] digest = derive(code);
        try {
            Cipher cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
            cipher.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(digest, 0, 32, "AES"), new IvParameterSpec(iv));
            byte[] encrypted = cipher.doFinal(plaintext);
            byte[] signature = authenticate(digest, direction, iv, encrypted);
            Base64.Encoder encoder = Base64.getEncoder();
            return new Envelope(encoder.encodeToString(iv), encoder.encodeToString(encrypted),
                    encoder.encodeToString(signature));
        } finally {
            Arrays.fill(digest, (byte) 0);
        }
    }

    public static byte[] decryptResponse(Envelope envelope, String code) throws GeneralSecurityException {
        return open(envelope, code, "response");
    }

    static byte[] open(Envelope envelope, String code, String direction) throws GeneralSecurityException {
        int plaintextLimit = plaintextLimit(direction);
        int ciphertextLimit = (plaintextLimit / 16 + 1) * 16;
        int encodedLimit = ((ciphertextLimit + 2) / 3) * 4;
        byte[] iv;
        byte[] encrypted;
        byte[] signature;
        try {
            if (envelope == null || envelope.iv == null || envelope.data == null || envelope.mac == null
                    || envelope.iv.length() > 32 || envelope.mac.length() > 48
                    || envelope.data.length() > encodedLimit) {
                throw new IllegalArgumentException();
            }
            Base64.Decoder decoder = Base64.getDecoder();
            iv = decoder.decode(envelope.iv);
            encrypted = decoder.decode(envelope.data);
            signature = decoder.decode(envelope.mac);
        } catch (IllegalArgumentException error) {
            throw new GeneralSecurityException("Некорректный защищённый ответ Windows-моста");
        }
        if (iv.length != 16 || signature.length != 32 || encrypted.length == 0
                || encrypted.length > ciphertextLimit || encrypted.length % 16 != 0) {
            throw new GeneralSecurityException("Некорректный размер защищённого ответа");
        }
        byte[] digest = derive(code);
        try {
            byte[] expected = authenticate(digest, direction, iv, encrypted);
            // Authentication must succeed before any ciphertext is decrypted or interpreted.
            if (!MessageDigest.isEqual(expected, signature)) {
                throw new GeneralSecurityException("Подлинность ответа Windows-моста не подтверждена. Проверьте код подключения");
            }
            Cipher cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
            cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(digest, 0, 32, "AES"), new IvParameterSpec(iv));
            byte[] plaintext = cipher.doFinal(encrypted);
            if (plaintext.length > plaintextLimit) throw new GeneralSecurityException("Сообщение превышает допустимый размер");
            return plaintext;
        } finally {
            Arrays.fill(digest, (byte) 0);
        }
    }

    private static int plaintextLimit(String direction) throws GeneralSecurityException {
        if ("request".equals(direction)) return MAX_REQUEST_PLAINTEXT_BYTES;
        if ("response".equals(direction)) return MAX_RESPONSE_PLAINTEXT_BYTES;
        throw new GeneralSecurityException("Некорректное направление сообщения");
    }

    private static byte[] derive(String code) throws GeneralSecurityException {
        return MessageDigest.getInstance("SHA-512").digest(
                ("StreletzBridge/v3\n" + normalizePairingCode(code)).getBytes(StandardCharsets.UTF_8));
    }

    private static byte[] authenticate(byte[] digest, String direction, byte[] iv, byte[] encrypted)
            throws GeneralSecurityException {
        if (!"request".equals(direction) && !"response".equals(direction)) {
            throw new GeneralSecurityException("Некорректное направление сообщения");
        }
        Mac hmac = Mac.getInstance("HmacSHA256");
        hmac.init(new SecretKeySpec(digest, 32, 32, "HmacSHA256"));
        hmac.update(("StreletzBridge/v3/" + direction + "\n").getBytes(StandardCharsets.UTF_8));
        hmac.update(iv);
        return hmac.doFinal(encrypted);
    }

    public static void verifyResponseMetadata(int protocol, String type, String expectedRequestId,
                                              String actualRequestId) throws GeneralSecurityException {
        if (protocol != VERSION || !"response".equals(type) || expectedRequestId == null
                || expectedRequestId.isEmpty() || !expectedRequestId.equals(actualRequestId)) {
            throw new GeneralSecurityException("Ответ Windows-моста не соответствует отправленному запросу");
        }
    }

    public static String projectFingerprint(List<String> recordIds) throws GeneralSecurityException {
        List<String> normalized = new ArrayList<>();
        for (String id : recordIds) {
            if (id == null || id.isEmpty()) throw new IllegalArgumentException("В проекте отсутствует ID записи");
            normalized.add(id.toLowerCase(Locale.ROOT));
        }
        Collections.sort(normalized);
        String canonical = "streletz-project-v1\n" + String.join("\n", normalized);
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(canonical.getBytes(StandardCharsets.UTF_8));
        StringBuilder hex = new StringBuilder(64);
        for (byte b : hash) hex.append(String.format(Locale.ROOT, "%02x", b & 0xff));
        return hex.toString();
    }

    public static boolean confirmsRegistration(String expectedOperationId, String expectedPanelId,
            String expectedRecordId, String expectedHardwareId, String operationId, String panelId,
            String recordId, String confirmedHardwareId, String state, boolean registrationConfirmed) {
        return registrationConfirmed && "registered".equals(state)
                && expectedHardwareId != null && expectedHardwareId.matches("[0-9A-Fa-f]{12}")
                && expectedHardwareId.equalsIgnoreCase(confirmedHardwareId)
                && expectedOperationId != null && expectedOperationId.equals(operationId)
                && expectedPanelId != null && expectedPanelId.equals(panelId)
                && expectedRecordId != null && expectedRecordId.equalsIgnoreCase(recordId);
    }

    /**
     * Used only when an authenticated response has no operation snapshot. A generic error says
     * nothing about whether hardware work started. Only an explicit pre-dispatch rejection with
     * a recognized validation code can release the saved operation ID for a new user action.
     */
    public static boolean isExplicitPredispatchRejection(String requestType, boolean ok,
                                                         Boolean accepted, String errorCode) {
        if (!"begin_registration".equals(requestType) || ok || !Boolean.FALSE.equals(accepted)) return false;
        if (errorCode == null) return false;
        switch (errorCode) {
            case "driver_unavailable":
            case "panel_busy":
            case "device_busy":
            case "project_missing":
            case "project_mismatch":
            case "record_missing":
            case "already_initialized":
            case "duplicate_hardware_id":
            case "journal_unavailable":
            case "stale_request":
            case "unsupported_protocol":
                return true;
            default:
                return false;
        }
    }
}
