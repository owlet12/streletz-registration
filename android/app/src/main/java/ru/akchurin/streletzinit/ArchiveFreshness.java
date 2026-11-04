// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import org.json.*;

/** A sidecar is written only AFTER its archive. A failed or missing marker causes a fresh save. */
final class ArchiveFreshness {
    private static final Map<Object,String> objectHashes=Collections.synchronizedMap(new WeakHashMap<>());
    private static String cached(Object object) throws Exception {
        String value=objectHashes.get(object);if(value!=null)return value;
        MessageDigest md=MessageDigest.getInstance("SHA-256");add(md,object==null?"null":object.toString());
        value=hex(md.digest());if(object!=null)objectHashes.put(object,value);return value;
    }
    static String revision(JSONObject base,ExportSnapshot snapshot) throws Exception {
        MessageDigest md=MessageDigest.getInstance("SHA-256");
        add(md,cached(base));add(md,cached(snapshot.projectPanels));add(md,snapshot.siteName);
        add(md,snapshot.projectFingerprint);add(md,Boolean.toString(snapshot.projectPanelCatalogueComplete));
        for(ExportSnapshot.Row row:snapshot.devices) {add(md,row.id);add(md,row.description);}
        String scope=":"+snapshot.projectFingerprint+":";
        addPreferences(md,snapshot.prefs,scope,Arrays.asList("stage","serial","device_key","note","updated","description","registration_summary"));
        addPreferences(md,snapshot.bridgePrefs,scope,Arrays.asList("panel","registration","registration-history"));
        add(md,String.valueOf(snapshot.prefs.get(Fieldwork.key(snapshot.projectFingerprint))));
        for(String key:new TreeSet<>(snapshot.blockedOperations))add(md,key);
        return hex(md.digest());
    }
    private static void addPreferences(MessageDigest md,Map<String,?> values,String scope,List<String> prefixes) {
        for(String key:new TreeSet<>(values.keySet())) {
            boolean relevant=false;for(String prefix:prefixes)if(key.startsWith(prefix+scope)){relevant=true;break;}
            if(relevant){add(md,key);Object value=values.get(key);add(md,value==null?"null":value.getClass().getName());add(md,String.valueOf(value));}
        }
    }
    private static void add(MessageDigest md,String value) {byte[] bytes=(value==null?"":value).getBytes(StandardCharsets.UTF_8);
        int n=bytes.length;md.update(new byte[]{(byte)(n>>>24),(byte)(n>>>16),(byte)(n>>>8),(byte)n});md.update(bytes);}
    private static String hex(byte[] bytes) {StringBuilder b=new StringBuilder();for(byte v:bytes)b.append(String.format(Locale.ROOT,"%02x",v&255));return b.toString();}
    static boolean saveIfChanged(File file,String revision,ProjectTransfer.Change save) throws Exception {
        if(matches(file,revision))return false;
        save.run();remember(file,revision);return true;
    }
    static File marker(File file){return new File(file.getPath()+".revision.json");}
    static boolean matches(File file,String revision) {
        try {JSONObject stamp=new JSONObject(ProjectTransfer.read(marker(file)));
            return file.isFile()&&stamp.optInt("version")==1&&revision.equals(stamp.optString("revision"))
                    &&file.length()==stamp.optLong("size",-1)&&file.lastModified()==stamp.optLong("modified",-1);
        }catch(Exception ignored){return false;}
    }
    static void remember(File file,String revision) throws Exception {
        if(!file.isFile())throw new IOException("Archive is missing");
        ProjectTransfer.write(marker(file),new JSONObject().put("version",1).put("revision",revision)
                .put("size",file.length()).put("modified",file.lastModified()).toString());
    }
}
