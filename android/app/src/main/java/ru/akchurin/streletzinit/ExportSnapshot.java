// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.*;
import org.json.*;

/** UI capture copies scalar metadata and preference maps only; all JSON assembly runs on storage worker.
 * Project/catalogue JSON objects are published by replacement and never mutated after capture. */
final class ExportSnapshot {
    static final class Row {
        final String id,name,description,zone,group,type;
        final int sourceRow; final boolean sourceInitialized; final JSONObject commissioning;
        Row(String id,String name,String description,String zone,String group,String type,int sourceRow,
            boolean initialized,JSONObject commissioning) {
            this.id=id;this.name=name;this.description=description;this.zone=zone;this.group=group;this.type=type;
            this.sourceRow=sourceRow;this.sourceInitialized=initialized;this.commissioning=commissioning;
        }
    }
    private static final String[] STAGE_LABELS={"Не начато","Найдено","Инициализировано","Проверено"};
    final List<Row> devices;
    final String siteName,projectFingerprint;
    final JSONArray projectPanels;
    final boolean projectPanelCatalogueComplete;
    final ProjectPanelIndex panelIndex;
    final Map<String,?> prefs,bridgePrefs;
    final long capturedAt;
    final Set<String> blockedOperations;
    ExportSnapshot(List<Row> rows,String name,String fingerprint,JSONArray panels,boolean complete,
                   ProjectPanelIndex index,Map<String,?> prefs,Map<String,?> bridge,Set<String> blocked) {
        devices=new ArrayList<>(rows);siteName=name;projectFingerprint=fingerprint;projectPanels=panels;
        projectPanelCatalogueComplete=complete;panelIndex=index;
        blockedOperations=new HashSet<>(blocked);
        this.prefs=new HashMap<>(prefs);this.bridgePrefs=new HashMap<>(bridge);capturedAt=System.currentTimeMillis();
    }
    static final class PreparedList {
        final DeviceFilterIndex index=new DeviceFilterIndex();
        final StageCounts counts=new StageCounts();
    }
    PreparedList prepareList() {
        long started=System.nanoTime();PreparedList result=new PreparedList();
        for(Row d:devices) {
            ProjectPanelIndex.Panel panel=panelIndex.uniqueForRecord(d.id);
            String search=d.name+" "+getDescription(d)+" "+d.zone+" "+d.type+" "+projectPanelLabel(d)
                    +" "+getSerial(d)+" "+getDeviceKey(d)+" "+getNote(d);
            int stage=getStage(d);result.counts.set(d.id,stage);
            result.index.put(new DeviceFilterIndex.Row(d.id,d.group,panel==null?"?":panel.id,stage,search,d.type));
        }
        PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.LIST_PREPARE,System.nanoTime()-started,devices.size(),true);
        return result;
    }
    JSONObject build() throws JSONException {
        JSONObject root = new JSONObject();
        root.put("format", "streletz-commissioning-progress");
        root.put("version", 1);
        root.put("siteName", siteName);
        root.put("projectFingerprint", projectFingerprint);
        root.put("panels", projectPanels);
        root.put("panelCatalogComplete", projectPanelCatalogueComplete);
        JSONArray sourceDescriptions = new JSONArray();
        for (Row device : devices) sourceDescriptions.put(new JSONObject()
                .put("recordId", device.id).put("description", device.description));
        root.put("recordMetadata", sourceDescriptions);
        root.put("exportedAt", capturedAt);
        JSONArray records = new JSONArray();
        for (Row d : devices) {
            JSONObject item = new JSONObject();
            item.put("id", d.id);
            item.put("name", d.name);
            item.put("stage", getStage(d));
            item.put("stageName", STAGE_LABELS[getStage(d)]);
            item.put("sourceRow", d.sourceRow).put("type", d.type).put("group", d.group).put("zone", d.zone);
            item.put("panelLabel", projectPanelLabel(d));
            ProjectPanelIndex.Panel membership = panelIndex.uniqueForRecord(d.id);
            item.put("projectPanelId", membership == null ? "" : membership.id);
            item.put("serial", getSerial(d));
            item.put("deviceKey", getDeviceKey(d));
            item.put("note", getNote(d));
            item.put("description", getDescription(d));
            item.put("descriptionOverridden", prefs.containsKey(descriptionPreferenceKey(d))
                    || (d.commissioning != null && d.commissioning.has("description")));
            item.put("panelId", string(bridgePrefs,panelPreferenceKey(d),""));
            addRegistrationSummary(item, d);
            item.put("updatedAt", getUpdated(d));
            records.put(item);
        }
        root.put("records", records);
        String fieldwork=string(prefs,Fieldwork.key(projectFingerprint),"");
        if(!fieldwork.isEmpty())try{root.put("fieldwork",Fieldwork.read(fieldwork));}catch(Exception e){throw new JSONException("Повреждены планы или список QR: "+e.getMessage());}
        return root;
    }

    private String key(String field,Row d) { return field+":"+projectFingerprint+":"+d.id.toLowerCase(Locale.ROOT); }
    private String descriptionPreferenceKey(Row d) { return key("description",d); }
    private String panelPreferenceKey(Row d) { return "panel:"+projectFingerprint+":"+d.id.toLowerCase(Locale.ROOT); }
    private static String string(Map<String,?> values,String key,String fallback) { Object v=values.get(key);return v instanceof String?(String)v:fallback; }
    private String base(Row d,String field) { return d.commissioning==null?"":d.commissioning.optString(field,""); }
    private String getSerial(Row d) { return string(prefs,key("serial",d),base(d,"serial")); }
    private String getDeviceKey(Row d) { return string(prefs,key("device_key",d),base(d,"deviceKey")); }
    private String getNote(Row d) { return string(prefs,key("note",d),base(d,"note")); }
    private String getDescription(Row d) { return string(prefs,key("description",d),d.commissioning!=null&&d.commissioning.has("description")?base(d,"description"):d.description); }
    private int getStage(Row d) {
        Object v=prefs.get(key("stage",d));
        int stage=v instanceof Number?((Number)v).intValue():d.commissioning==null?(d.sourceInitialized?2:0):d.commissioning.optInt("stage",d.sourceInitialized?2:0);
        return Math.max(0,Math.min(3,stage));
    }
    private long getUpdated(Row d) { Object v=prefs.get(key("updated",d));return v instanceof Number?((Number)v).longValue():d.commissioning==null?0:d.commissioning.optLong("updatedAt"); }
    private String projectPanelLabel(Row d) {
        ProjectPanelIndex.Panel panel=panelIndex.uniqueForRecord(d.id);
        return panel!=null?panel.displayName():panelIndex.forRecord(d.id).isEmpty()?"Панель не определена":"Несколько панелей в проекте";
    }
    private void addRegistrationSummary(JSONObject destination,Row d) throws JSONException {
        String importedRaw=string(prefs,key("registration_summary",d),"");
        JSONObject imported=!importedRaw.isEmpty()?new JSONObject(importedRaw):d.commissioning==null?null:d.commissioning.optJSONObject("registration");
        String stored=string(bridgePrefs,key("registration",d),"");
        JSONObject operation=null;
        try {
            MainActivity.RegistrationOperation parsed=MainActivity.RegistrationOperation.fromStored(new JSONObject(stored));
            if(parsed.localResolved) MainActivity.validateRetiredArchive(parsed,new JSONArray(string(bridgePrefs,key("registration-history",d),"[]")));
            if(!blockedOperations.contains(key("registration",d)) && projectFingerprint.equals(parsed.projectFingerprint)
                    && d.id.equalsIgnoreCase(parsed.recordId)) operation=parsed.toStored();
        } catch(Exception ignored) { }
        if(operation==null) { if(imported!=null) destination.put("registration",RegistrationRecovery.safeSummary(imported)); return; }
        JSONObject summary=RegistrationRecovery.safeSummary(operation);
        JSONArray history=new JSONArray();Set<String> ids=new HashSet<>();
        JSONArray prior=imported==null?null:imported.optJSONArray("history");
        JSONArray local=new JSONArray(string(bridgePrefs,key("registration-history",d),"[]"));
        for(JSONArray entries:new JSONArray[]{prior,local}) if(entries!=null) for(int i=0;i<entries.length();i++) {
            JSONObject entry=RegistrationRecovery.safeSummary(entries.getJSONObject(i));
            if(ids.add(entry.optString("operationId"))) history.put(entry);
        }
        if(history.length()>0) summary.put("history",history);
        destination.put("registration",summary);
    }
}
