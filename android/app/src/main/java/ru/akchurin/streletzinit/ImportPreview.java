// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;
import java.util.*;

/** Selections are whole records; dependent panel membership follows the selected source. */
final class ImportPreview {
    static final class Change {
        final String id,label; final JSONObject before,after;
        Change(String id,JSONObject before,JSONObject after) {
            this.id=id;this.before=before;this.after=after;
            String name=after==null?display(before):display(after);
            label=(before==null?"Добавить: ":after==null?"Удалить: ":"Изменить: ")+name
                +(before!=null&&after!=null&&!before.optString("description").equals(after.optString("description"))
                ?"\nОписание: «"+before.optString("description")+"» → «"+after.optString("description")+"»":"")
                +(before!=null&&after!=null&&!canonical(before.optJSONObject("commissioning")).equals(canonical(after.optJSONObject("commissioning")))?"\nРасхождение журнала: будет выполнена сверка":"");
        }
    }
    final JSONObject current,incoming; final List<Change> changes=new ArrayList<>(); final boolean related;
    ImportPreview(JSONObject current,JSONObject incoming) throws Exception {
        ProjectTransfer.fingerprint(current);ProjectTransfer.fingerprint(incoming);
        this.current=new JSONObject(current.toString());this.incoming=new JSONObject(incoming.toString());
        Map<String,JSONObject> old=rows(current),next=rows(incoming);int common=0;
        for(String id:old.keySet())if(next.containsKey(id))common++;
        related=common>0;
        for(String id:next.keySet())if(!canonical(next.get(id)).equals(canonical(old.get(id))))changes.add(new Change(id,old.get(id),next.get(id)));
        for(String id:old.keySet())if(!next.containsKey(id))changes.add(new Change(id,old.get(id),null));
    }
    static String display(JSONObject r){return r.optString("description").isEmpty()?r.optString("name",r.optString("id")):r.optString("description");}
    static Map<String,JSONObject> rows(JSONObject p)throws Exception{Map<String,JSONObject>m=new LinkedHashMap<>();JSONArray a=p.getJSONArray("records");for(int i=0;i<a.length();i++){JSONObject r=a.getJSONObject(i);m.put(r.getString("id").toLowerCase(Locale.ROOT),r);}return m;}
    static String canonical(Object o){if(o==null||o==JSONObject.NULL)return "null";if(o instanceof JSONObject){JSONObject j=(JSONObject)o;List<String> keys=new ArrayList<>();java.util.Iterator<String> iterator=j.keys();while(iterator.hasNext())keys.add(iterator.next());Collections.sort(keys);StringBuilder b=new StringBuilder("{");for(String k:keys)b.append(JSONObject.quote(k)).append(':').append(canonical(j.opt(k))).append(',');return b.append('}').toString();}if(o instanceof JSONArray){StringBuilder b=new StringBuilder("[");JSONArray a=(JSONArray)o;for(int i=0;i<a.length();i++)b.append(canonical(a.opt(i))).append(',');return b.append(']').toString();}return o instanceof String?JSONObject.quote((String)o):o.toString();}
    JSONObject select(Set<String> selected,boolean includeFieldwork) throws Exception {return select(selected,includeFieldwork,false);}
    JSONObject select(Set<String> selected,boolean includeFieldwork,boolean includeCatalogue) throws Exception {
        if(!related) {JSONObject result=new JSONObject(incoming.toString());if(!includeFieldwork)result.remove("fieldwork");return result;}
        Map<String,JSONObject> records=rows(current);Set<String> fromNew=new HashSet<>();
        for(Change c:changes)if(selected.contains(c.id)){if(c.after==null)records.remove(c.id);else{records.put(c.id,c.after);fromNew.add(c.id);}}
        if(includeCatalogue) {
            Set<String> changed=new HashSet<>();for(Change c:changes)changed.add(c.id);
            for(String id:rows(incoming).keySet())if(!changed.contains(id)&&records.containsKey(id))fromNew.add(id);
        }
        JSONObject result=new JSONObject(current.toString());JSONArray output=new JSONArray();for(JSONObject r:records.values())output.put(new JSONObject(r.toString()));result.put("records",output);
        LinkedHashMap<String,JSONObject> panelMap=new LinkedHashMap<>();
        collectPanels(panelMap,current,records.keySet(),fromNew,false);collectPanels(panelMap,incoming,records.keySet(),fromNew,true);
        if(includeCatalogue) {
            JSONArray fresh=incoming.optJSONArray("panels");
            if(fresh!=null)for(int i=0;i<fresh.length();i++) {
                JSONObject panel=fresh.getJSONObject(i);String id=panel.optString("projectPanelId",panel.optString("panelId")).toLowerCase(Locale.ROOT);
                JSONObject combined=panelMap.get(id);if(combined!=null){JSONObject replacement=new JSONObject(panel.toString());replacement.put("recordIds",combined.getJSONArray("recordIds"));replacement.remove("verifiedRecordIds");replacement.remove("available");panelMap.put(id,replacement);}
            }
        }
        JSONArray panels=new JSONArray();for(JSONObject panel:panelMap.values())panels.put(panel);
        result.put("panels",panels).put("panelCatalogComplete",false).put("panelMappingVersion",1)
            .put("panelMappingFingerprint",ProjectTransfer.fingerprint(result));
        if(includeFieldwork&&incoming.has("fieldwork"))result.put("fieldwork",incoming.getJSONObject("fieldwork"));else result.remove("fieldwork");
        ProjectTransfer.fingerprint(result);return result;
    }
    private static void collectPanels(Map<String,JSONObject> dest,JSONObject source,Set<String> keep,Set<String> fromNew,boolean newer)throws Exception{
        JSONArray panels=source.optJSONArray("panels");if(panels==null)return;
        for(int i=0;i<panels.length();i++){JSONObject p=panels.getJSONObject(i);String id=p.optString("projectPanelId",p.optString("panelId")).toLowerCase(Locale.ROOT);JSONArray members=p.optJSONArray("recordIds"),filtered=new JSONArray();
            if(members!=null)for(int n=0;n<members.length();n++){String record=members.getString(n).toLowerCase(Locale.ROOT);if(keep.contains(record)&&fromNew.contains(record)==newer)filtered.put(members.getString(n));}
            if(filtered.length()==0)continue;
            JSONObject existing=dest.get(id);
            if(existing==null){existing=new JSONObject(p.toString());existing.remove("available");existing.remove("verifiedRecordIds");existing.put("recordIds",new JSONArray());dest.put(id,existing);}
            JSONArray all=existing.getJSONArray("recordIds");for(int n=0;n<filtered.length();n++)all.put(filtered.get(n));
        }
    }
}
