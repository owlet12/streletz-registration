// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.security.GeneralSecurityException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/** Read-only Windows snapshot validation and atomic private project storage. No hardware calls. */
final class ProjectTransfer {
    static final int MAX_DOCUMENT_BYTES = 16 * 1024 * 1024;
    static final long POLL_MILLIS = 15000;
    static final long UNSUPPORTED_RETRY_MILLIS = 300000;

    static final class Snapshot {
        final String fingerprint;
        final String revision;
        final boolean notModified;
        final JSONObject project;

        private Snapshot(String fingerprint, String revision, boolean notModified, JSONObject project) {
            this.fingerprint = fingerprint;
            this.revision = revision;
            this.notModified = notModified;
            this.project = project;
        }
    }

    private ProjectTransfer() { }

    interface Change { void run() throws Exception; }

    static final class InstallException extends IOException {
        final boolean rollbackSucceeded;
        InstallException(Exception cause, boolean rollbackSucceeded) {
            super(rollbackSucceeded
                    ? "Проект не обновлён; восстановлены предыдущий проект и журнал."
                    : "Не удалось полностью восстановить состояние после ошибки сохранения. Откройте предыдущий проект из «Проекты на телефоне».", cause);
            this.rollbackSucceeded = rollbackSucceeded;
        }
    }

    /** The active file and journal are one publish step, with a byte-exact rollback on a failed commit. */
    static void install(File target, String raw, Change publish, Change rollback) throws Exception {
        boolean existed = target.isFile();
        String previous = existed ? read(target) : null;
        write(target, raw);
        try { publish.run(); }
        catch (Exception failure) {
            boolean restored = true;
            try {
                if (existed) write(target, previous);
                else if (target.exists() && !target.delete()) throw new IOException("Не удалось вернуть встроенный проект.");
            } catch (Exception error) { restored = false; failure.addSuppressed(error); }
            try { rollback.run(); }
            catch (Exception error) { restored = false; failure.addSuppressed(error); }
            throw new InstallException(failure, restored);
        }
    }

    static String fingerprint(JSONObject project) throws JSONException, IOException {
        if (project.optInt("schemaVersion", 0) != 1)
            throw new IOException("Неподдерживаемая версия проекта Windows.");
        JSONArray records = project.getJSONArray("records");
        Set<String> unique = new HashSet<>();
        List<String> ids = new ArrayList<>();
        boolean hasDevice = false;
        for (int i = 0; i < records.length(); i++) {
            JSONObject row = records.getJSONObject(i);
            String id = row.getString("id");
            if (id.trim().isEmpty() || !id.equals(id.trim()) || !unique.add(id.toLowerCase(Locale.ROOT)))
                throw new IOException("В проекте отсутствует или повторяется идентификатор записи.");
            ids.add(id);
            if ("device".equals(row.optString("recordKind"))) hasDevice = true;
            if (row.has("commissioning") && !row.isNull("commissioning")) row.getJSONObject("commissioning");
        }
        if (!hasDevice) throw new IOException("В проекте нет устройств для инициализации.");
        if (project.has("panels") && !project.isNull("panels")) {
            JSONArray panels = project.getJSONArray("panels");
            Set<String> panelIds = new HashSet<>();
            for (int i = 0; i < panels.length(); i++) {
                JSONObject panel = panels.getJSONObject(i);
                String panelId = ProjectPanelIndex.normalized(panel.optString("projectPanelId", panel.optString("panelId", "")));
                if (panelId.isEmpty() || !panelIds.add(panelId))
                    throw new IOException("В проекте отсутствует или повторяется идентификатор панели.");
                if (panel.has("recordIds") && !panel.isNull("recordIds")) {
                    JSONArray members = panel.getJSONArray("recordIds");
                    for (int n = 0; n < members.length(); n++) members.getString(n);
                }
            }
        }
        try { return ProtocolV3.projectFingerprint(ids); }
        catch (GeneralSecurityException error) { throw new IOException("Не удалось проверить состав проекта.", error); }
    }

    static Snapshot response(JSONObject response, String knownFingerprint, String knownRevision)
            throws JSONException, IOException {
        if (response == null || !response.optBoolean("ok", false))
            throw new IOException(response == null ? "Windows-мост не вернул проект."
                    : response.optString("message", "Не удалось получить проект Windows."));
        if (!"windows_bridge".equals(response.optString("projectSource", "")))
            throw new IOException("Windows-мост не подтвердил источник проекта. Обновите Windows-мост.");
        String fingerprint = response.getString("projectFingerprint");
        String revision = response.getString("projectRevision");
        if (!fingerprint.matches("[0-9a-f]{64}") || revision.isEmpty() || revision.length() > 256)
            throw new IOException("Windows-мост вернул некорректную версию проекта.");
        boolean unchanged = response.getBoolean("notModified");
        if (unchanged) {
            if (!fingerprint.equals(knownFingerprint) || !revision.equals(knownRevision))
                throw new IOException("Подтверждение версии относится к другому снимку проекта.");
            return new Snapshot(fingerprint, revision, true, null);
        }
        JSONObject project = new JSONObject(response.getJSONObject("project").toString());
        if (!fingerprint.equals(fingerprint(project)))
            throw new IOException("Состав полученного проекта не соответствует его отпечатку.");
        // Only an authenticated fresh list_panels answer may authorize a registration.
        JSONArray panels = project.optJSONArray("panels");
        if (panels != null) for (int i = 0; i < panels.length(); i++) {
            JSONObject panel = panels.getJSONObject(i);
            panel.remove("available");
            panel.remove("verifiedRecordIds");
        }
        return new Snapshot(fingerprint, revision, false, project);
    }

    static boolean accepts(int sentGeneration, int currentGeneration, String sentFingerprint,
                           String currentFingerprint, boolean sameEndpoint, boolean foreground) {
        return foreground && sameEndpoint && sentGeneration == currentGeneration
                && sentFingerprint != null && sentFingerprint.equals(currentFingerprint);
    }

    static File archiveFile(File directory, String fingerprint) throws IOException {
        if (fingerprint == null || !fingerprint.matches("[0-9a-f]{64}"))
            throw new IOException("Некорректный идентификатор сохранённого проекта.");
        File archive = new File(directory, "projects");
        if (!archive.isDirectory() && !archive.mkdirs())
            throw new IOException("Не удалось создать папку сохранённых проектов.");
        return new File(archive, fingerprint + ".s3project");
    }

    static void write(File file, String raw) throws IOException {
        File parent = file.getParentFile();
        if (parent == null || (!parent.isDirectory() && !parent.mkdirs()))
            throw new IOException("Не удалось открыть папку проекта.");
        File temp = new File(parent, file.getName() + ".pending");
        try {
            try (FileOutputStream stream = new FileOutputStream(temp)) {
                stream.write(raw.getBytes(StandardCharsets.UTF_8));
                stream.getFD().sync();
            }
            // Same directory / same filesystem. An interrupted write keeps the previous project.
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING);
        } finally { if (temp.exists()) temp.delete(); }
    }

    static String read(File file) throws IOException {
        if (file.length() > MAX_DOCUMENT_BYTES)
            throw new IOException("Сохранённый проект превышает допустимый размер.");
        return new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8);
    }
}
