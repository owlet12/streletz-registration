// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.*;
import java.io.IOException;
import java.util.*;

/** Portable field notes. No command or registration status can be created by this store. */
final class Fieldwork {
    static final int MAX_SCANS=2000, MAX_PLANS=8, MAX_IMAGE_BYTES=800000, MAX_STATE_CHARS=7000000;
    static String key(String fingerprint) { return "fieldwork:"+fingerprint; }
    static JSONObject empty() throws JSONException { return new JSONObject().put("version",1).put("scans",new JSONArray()).put("plans",new JSONArray()); }
    static JSONObject read(String raw) throws Exception {
        if(raw==null||raw.isEmpty()) return empty();
        if(raw.length()>MAX_STATE_CHARS) throw new IOException("Планы и список QR превышают допустимый размер.");
        JSONObject state=new JSONObject(raw);
        if(state.optInt("version")!=1) throw new IOException("Неизвестная версия планов и списка QR.");
        JSONArray scans=state.getJSONArray("scans"), plans=state.getJSONArray("plans");
        if(scans.length()>MAX_SCANS||plans.length()>MAX_PLANS) throw new IOException("Слишком много устройств или планов.");
        Set<String> ids=new HashSet<>();
        for(int i=0;i<scans.length();i++) {
            JSONObject s=scans.getJSONObject(i);String id=s.getString("id");
            if(!id.matches("[0-9A-F]{12}")||!ids.add(id)) throw new IOException("Некорректный или повторяющийся ID в списке QR.");
            if(!s.optString("key").matches("(?:[0-9A-F]{8})?")) throw new IOException("Некорректный ключ QR.");
        }
        ids.clear();
        for(int i=0;i<plans.length();i++) {
            JSONObject plan=plans.getJSONObject(i);
            if(plan.getString("id").isEmpty()||!ids.add(plan.getString("id"))) throw new IOException("Повторяющийся план.");
            String image=plan.getString("image");
            if(image.length()>(MAX_IMAGE_BYTES+2)/3*4 || java.util.Base64.getDecoder().decode(image).length>MAX_IMAGE_BYTES)
                throw new IOException("Изображение плана слишком велико.");
            JSONArray points=plan.getJSONArray("points");Set<String> records=new HashSet<>();
            if(points.length()>20000) throw new IOException("Слишком много точек плана.");
            for(int n=0;n<points.length();n++) {JSONObject point=points.getJSONObject(n);
                if(!records.add(point.getString("recordId").toLowerCase(Locale.ROOT))) throw new IOException("Устройство повторяется на плане.");
                double x=point.getDouble("x"),y=point.getDouble("y");
                if(!Double.isFinite(x)||!Double.isFinite(y)||x<0||x>1||y<0||y>1) throw new IOException("Точка вне плана.");
            }
        }
        return state;
    }
    static JSONArray plansForRecord(JSONObject state,String id) throws Exception {
        JSONArray matches=new JSONArray(),plans=state.getJSONArray("plans");
        for(int i=0;i<plans.length();i++){JSONObject plan=plans.getJSONObject(i);JSONArray points=plan.getJSONArray("points");
            for(int n=0;n<points.length();n++)if(id.equalsIgnoreCase(points.getJSONObject(n).optString("recordId"))){matches.put(plan);break;}}
        return matches;
    }
    static void putPoint(JSONObject state,String planId,String recordId,double x,double y) throws Exception {
        if(recordId==null||recordId.isEmpty()||!Double.isFinite(x)||!Double.isFinite(y)||x<0||x>1||y<0||y>1)throw new IOException("Некорректная отметка плана.");
        JSONArray plans=state.getJSONArray("plans");
        for(int i=0;i<plans.length();i++)if(planId.equals(plans.getJSONObject(i).optString("id"))){JSONArray points=plans.getJSONObject(i).getJSONArray("points");
            for(int n=points.length()-1;n>=0;n--)if(recordId.equalsIgnoreCase(points.getJSONObject(n).optString("recordId")))points.remove(n);
            points.put(new JSONObject().put("recordId",recordId).put("x",x).put("y",y));return;}
        throw new IOException("План не найден.");
    }
    static void removePoint(JSONObject state,String planId,String recordId) throws Exception {
        JSONArray plans=state.getJSONArray("plans");
        for(int i=0;i<plans.length();i++)if(planId.equals(plans.getJSONObject(i).optString("id"))){JSONArray points=plans.getJSONObject(i).getJSONArray("points");
            for(int n=points.length()-1;n>=0;n--)if(recordId.equalsIgnoreCase(points.getJSONObject(n).optString("recordId")))points.remove(n);return;}
        throw new IOException("План не найден.");
    }
    static boolean addScan(JSONObject state,String id,String key,long now) throws Exception {
        id=id.trim().toUpperCase(Locale.ROOT);key=key.trim().toUpperCase(Locale.ROOT);
        if(!id.matches("[0-9A-F]{12}")||!key.matches("(?:[0-9A-F]{8})?")) throw new IOException("Нужны ID из 12 и ключ из 8 шестнадцатеричных символов.");
        JSONArray scans=state.getJSONArray("scans");
        for(int i=0;i<scans.length();i++) if(id.equals(scans.getJSONObject(i).getString("id"))) {
            String old=scans.getJSONObject(i).optString("key");
            if(!old.isEmpty()&&!key.isEmpty()&&!old.equals(key)) throw new IOException("Этот ID уже есть с другим ключом. Проверьте наклейку; прежняя запись сохранена.");
            if(old.isEmpty()&&!key.isEmpty()) scans.getJSONObject(i).put("key",key);
            return false;
        }
        if(scans.length()>=MAX_SCANS) throw new IOException("Список QR заполнен (2000 устройств).");
        scans.put(new JSONObject().put("id",id).put("key",key).put("scannedAt",now));return true;
    }
    static JSONObject forProject(JSONObject state,JSONObject project) throws Exception {
        JSONObject copy=read(state.toString());Set<String> records=new HashSet<>();JSONArray rows=project.getJSONArray("records");
        for(int i=0;i<rows.length();i++) records.add(rows.getJSONObject(i).getString("id").toLowerCase(Locale.ROOT));
        JSONArray scans=copy.getJSONArray("scans");
        for(int i=0;i<scans.length();i++) {JSONObject s=scans.getJSONObject(i);if(!records.contains(s.optString("recordId").toLowerCase(Locale.ROOT))) s.remove("recordId");}
        JSONArray plans=copy.getJSONArray("plans");
        for(int i=0;i<plans.length();i++) {JSONArray points=plans.getJSONObject(i).getJSONArray("points");
            for(int n=points.length()-1;n>=0;n--) if(!records.contains(points.getJSONObject(n).getString("recordId").toLowerCase(Locale.ROOT))) points.remove(n);
        }
        return copy;
    }
    static JSONObject merge(JSONObject local,JSONObject incoming,JSONObject project) throws Exception {
        return merge(local,incoming,project,null,null);
    }
    // The previous downloaded project is the baseline, never the edited local journal.
    // Replace only a plan unchanged on this phone; concurrent edits remain local.
    static JSONObject merge(JSONObject local,JSONObject incoming,JSONObject project,JSONObject baseline,int[] conflicts) throws Exception {
        JSONObject result=read(local.toString());
        JSONArray a=incoming.getJSONArray("scans"),dest=result.getJSONArray("scans");
        Set<String> ids=new HashSet<>();for(int i=0;i<dest.length();i++)ids.add(dest.getJSONObject(i).getString("id"));
        for(int i=0;i<a.length();i++)if(ids.add(a.getJSONObject(i).getString("id")))dest.put(new JSONObject(a.getJSONObject(i).toString()));
        JSONArray plans=result.getJSONArray("plans"),other=incoming.getJSONArray("plans");ids.clear();
        for(int i=0;i<plans.length();i++)ids.add(plans.getJSONObject(i).getString("id"));
        for(int i=0;i<other.length();i++){
            JSONObject remote=other.getJSONObject(i);String id=remote.getString("id");
            if(ids.add(id)){plans.put(importedPlan(remote));continue;}
            for(int n=0;n<plans.length();n++)if(id.equals(plans.getJSONObject(n).getString("id"))){
                JSONObject here=plans.getJSONObject(n),before=null;
                JSONArray old=baseline==null?null:baseline.optJSONArray("plans");
                if(old!=null)for(int k=0;k<old.length();k++)if(id.equals(old.getJSONObject(k).optString("id"))){before=old.getJSONObject(k);break;}
                String localDigest=planDigest(here),remoteDigest=planDigest(remote),accepted=here.optString("_importedPlanDigest");
                if(localDigest.equals(remoteDigest)){here.put("_importedPlanDigest",remoteDigest);break;}
                if(localDigest.equals(accepted)||(before!=null&&samePlan(here,before)))plans.put(n,importedPlan(remote));
                else if(conflicts!=null&&conflicts.length>0&&!remoteDigest.equals(accepted)&&(before==null||!samePlan(before,remote)))conflicts[0]++;
                break;
            }
        }
        return forProject(result,project);
    }
    private static JSONObject importedPlan(JSONObject plan)throws Exception{return new JSONObject(plan.toString()).put("_importedPlanDigest",planDigest(plan));}
    private static boolean samePlan(JSONObject a,JSONObject b)throws Exception{return planDigest(a).equals(planDigest(b));}
    private static String planDigest(JSONObject plan)throws Exception{
        JSONObject content=new JSONObject(plan.toString());content.remove("_importedPlanDigest");
        byte[] bytes=java.security.MessageDigest.getInstance("SHA-256").digest(canonical(content).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        char[] hex="0123456789abcdef".toCharArray();StringBuilder text=new StringBuilder(64);for(byte value:bytes)text.append(hex[(value&255)>>>4]).append(hex[value&15]);return text.toString();
    }
    private static String canonical(Object value)throws Exception{
        if(value instanceof JSONObject){JSONObject object=(JSONObject)value;List<String> keys=new ArrayList<>();Iterator<String> it=object.keys();while(it.hasNext())keys.add(it.next());Collections.sort(keys);
            StringBuilder s=new StringBuilder("{");for(String key:keys)s.append(JSONObject.quote(key)).append(':').append(canonical(object.get(key))).append(',');return s.append('}').toString();}
        if(value instanceof JSONArray){JSONArray array=(JSONArray)value;StringBuilder s=new StringBuilder("[");for(int i=0;i<array.length();i++)s.append(canonical(array.get(i))).append(',');return s.append(']').toString();}
        if(value instanceof Number)return Double.toString(((Number)value).doubleValue());
        if(value==null||value==JSONObject.NULL)return "null";
        if(value instanceof Boolean)return value.toString();
        return JSONObject.quote(String.valueOf(value));
    }
}
