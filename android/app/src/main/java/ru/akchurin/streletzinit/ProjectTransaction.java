// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

/** Write-ahead rollback journal for the active project and both preference stores. */
final class ProjectTransaction {
    interface Restore { void apply(Map<String, Object> journal, Map<String, Object> bridge) throws Exception; }
    interface Checkpoint { void reached(String point) throws Exception; }
    private static final Checkpoint NO_CHECKPOINT = point -> { };
    private ProjectTransaction() { }

    static File journalFile(File target) { return new File(target.getParentFile(), target.getName() + ".transaction"); }

    static void install(File target, String raw, Map<String, ?> oldJournal, Map<String, ?> oldBridge,
                        ProjectTransfer.Change publish, Restore restore) throws Exception {
        install(target, raw, oldJournal, oldBridge, publish, restore, NO_CHECKPOINT);
    }

    static void install(File target, String raw, Map<String, ?> oldJournal, Map<String, ?> oldBridge,
                        ProjectTransfer.Change publish, Restore restore, Checkpoint checkpoint) throws Exception {
        File log = journalFile(target);
        if (target.exists() && !target.isFile()) throw new IOException("Путь проекта не является файлом.");
        if (raw.getBytes(StandardCharsets.UTF_8).length > ProjectTransfer.MAX_DOCUMENT_BYTES)
            throw new IOException("Файл проекта превышает допустимый размер.");
        if (log.exists()) throw new IOException("Сначала восстановите прерванное сохранение проекта.");
        JSONObject state = new JSONObject().put("version", 1).put("phase", "prepared")
                .put("existed", target.isFile()).put("previous", target.isFile() ? ProjectTransfer.read(target) : "")
                .put("journal", encode(oldJournal)).put("bridge", encode(oldBridge));
        // No project or preference mutations before this durable journal is complete.
        String prepared = state.toString();
        if (prepared.getBytes(StandardCharsets.UTF_8).length > 32L * 1024 * 1024)
            throw new IOException("Журнал восстановления превышает допустимый размер. Сохранение не начато.");
        ProjectTransfer.write(log, prepared);
        try {
            checkpoint.reached("prepared");
            ProjectTransfer.write(target, raw);
            checkpoint.reached("file");
            publish.run();
            checkpoint.reached("published");
            ProjectTransfer.write(log, new JSONObject().put("version", 1).put("phase", "committed").toString());
        } catch (Exception failure) {
            boolean restored = false;
            try { recover(target, restore, NO_CHECKPOINT); restored = true; }
            catch (Exception rollback) { failure.addSuppressed(rollback); }
            throw new ProjectTransfer.InstallException(failure, restored);
        }
        checkpoint.reached("committed");
        // A committed marker is safe to retain if cleanup fails; startup only removes it.
        log.delete();
    }

    static boolean recover(File target, Restore restore) throws Exception {
        return recover(target, restore, NO_CHECKPOINT);
    }

    static boolean recover(File target, Restore restore, Checkpoint checkpoint) throws Exception {
        File log = journalFile(target);
        if (!log.exists()) return false;
        if (!log.isFile() || log.length() > 32L * 1024 * 1024)
            throw new IOException("Журнал сохранения повреждён; работа с проектом приостановлена.");
        JSONObject state = new JSONObject(new String(Files.readAllBytes(log.toPath()), StandardCharsets.UTF_8));
        if (state.getInt("version") != 1) throw new IOException("Неизвестная версия журнала сохранения.");
        String phase = state.getString("phase");
        if ("committed".equals(phase)) {
            if (!log.delete()) throw new IOException("Не удалось завершить сохранение проекта.");
            return true;
        }
        if (!"prepared".equals(phase)) throw new IOException("Некорректный этап сохранения проекта.");
        // Validate everything before changing anything. Corrupt journals fail closed.
        boolean existed = state.getBoolean("existed");
        String previous = state.getString("previous");
        Map<String, Object> journal = decode(state.getJSONObject("journal"));
        Map<String, Object> bridge = decode(state.getJSONObject("bridge"));
        if (existed) ProjectTransfer.write(target, previous);
        else if (target.exists() && !target.delete()) throw new IOException("Не удалось восстановить встроенный проект.");
        checkpoint.reached("restored_file");
        restore.apply(journal, bridge);
        checkpoint.reached("restored_preferences");
        if (!log.delete()) throw new IOException("Не удалось завершить восстановление проекта.");
        return true;
    }

    static JSONObject encode(Map<String, ?> values) throws Exception {
        JSONObject result = new JSONObject();
        for (Map.Entry<String, ?> entry : values.entrySet()) {
            Object value = entry.getValue();
            String type;
            if (value instanceof String) type = "string";
            else if (value instanceof Integer) type = "int";
            else if (value instanceof Long) type = "long";
            else if (value instanceof Boolean) type = "boolean";
            else if (value instanceof Float) type = "float";
            else if (value instanceof Set<?>) {
                type = "set";
                JSONArray strings = new JSONArray();
                for (Object item : (Set<?>) value) {
                    if (!(item instanceof String)) throw new IOException("Некорректный набор настроек.");
                    strings.put(item);
                }
                value = strings;
            } else throw new IOException("Неподдерживаемый тип настройки.");
            result.put(entry.getKey(), new JSONObject().put("type", type).put("value", value));
        }
        return result;
    }

    static Map<String, Object> decode(JSONObject encoded) throws Exception {
        Map<String, Object> values = new HashMap<>();
        java.util.Iterator<String> keys = encoded.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            JSONObject item = encoded.getJSONObject(key);
            Object value;
            switch (item.getString("type")) {
                case "string": value = item.getString("value"); break;
                case "int": value = item.getInt("value"); break;
                case "long": value = item.getLong("value"); break;
                case "boolean": value = item.getBoolean("value"); break;
                case "float": value = (float) item.getDouble("value"); break;
                case "set":
                    Set<String> strings = new HashSet<>();
                    JSONArray array = item.getJSONArray("value");
                    for (int i = 0; i < array.length(); i++) strings.add(array.getString(i));
                    value = strings;
                    break;
                default: throw new IOException("Повреждён тип настройки в журнале сохранения.");
            }
            values.put(key, value);
        }
        return values;
    }
}
