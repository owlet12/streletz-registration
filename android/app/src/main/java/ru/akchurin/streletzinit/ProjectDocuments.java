// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/** Pure document construction from a frozen UI snapshot; safe on a storage worker. */
final class ProjectDocuments {
    private ProjectDocuments() { }

    static JSONObject portable(JSONObject base, JSONObject backup) throws JSONException {
        JSONObject result = new JSONObject(base.toString());
        result.put("format", "streletz-portable-project").put("transferVersion", 1)
                .put("exportedAt", System.currentTimeMillis()).put("panelMappingVersion", 1)
                .put("panelMappingFingerprint", backup.getString("projectFingerprint"))
                .put("panelCatalogComplete", backup.optBoolean("panelCatalogComplete"))
                .put("panels", new JSONArray(backup.getJSONArray("panels").toString()));
        Map<String, JSONObject> work = new HashMap<>();
        JSONArray entries = backup.getJSONArray("records");
        for (int i = 0; i < entries.length(); i++) {
            JSONObject row = entries.getJSONObject(i);
            work.put(row.getString("id").toLowerCase(Locale.ROOT), row);
        }
        JSONArray rows = result.getJSONArray("records");
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            String id = row.getString("id").toLowerCase(Locale.ROOT);
            JSONObject saved = work.get(id);
            if (saved == null) continue;
            JSONObject commissioning = new JSONObject();
            for (String key : new String[]{"stage", "stageName", "serial", "deviceKey", "note", "panelId", "updatedAt", "registration"})
                if (saved.has(key)) commissioning.put(key, saved.get(key));
            if (saved.optBoolean("descriptionOverridden")) commissioning.put("description", saved.optString("description"));
            row.put("commissioning", commissioning).put("description", saved.optString("description"))
                    .put("initialized", saved.optInt("stage") >= 2 ? "+" : "-");
            if (!saved.optString("projectPanelId").isEmpty()) row.put("projectPanelId", saved.getString("projectPanelId"));
            else row.remove("projectPanelId");
        }
        if(backup.has("fieldwork"))result.put("fieldwork",new JSONObject(backup.getJSONObject("fieldwork").toString()));
        return result;
    }
    static String csv(JSONObject backup) throws JSONException {
        StringBuilder text = new StringBuilder("\uFEFFsep=;\r\nНомер;Название;Тип;Группа;Зона;Описание;Панель;Этап;Серийный номер;Ключ устройства;Примечание;Изменено\r\n");
        JSONArray rows = backup.getJSONArray("records");
        java.text.SimpleDateFormat date = new java.text.SimpleDateFormat("dd.MM.yyyy HH:mm", Locale.getDefault());
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            text.append(row.optInt("sourceRow")).append(';');
            for (String field : new String[]{"name", "type", "group", "zone", "description", "panelLabel", "stageName", "serial", "deviceKey", "note"})
                text.append(cell(row.optString(field))).append(';');
            long updated = row.optLong("updatedAt");
            text.append(cell(updated == 0 ? "" : date.format(new java.util.Date(updated)))).append("\r\n");
        }
        return text.toString();
    }

    private static String cell(String value) {
        return "\"" + value.replace("\"", "\"\"") + "\"";
    }
}
