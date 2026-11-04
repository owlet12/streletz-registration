// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.*;
import java.util.*;
import org.json.*;

/** Disposable metadata cache. The chosen project is always parsed and validated again. */
final class SavedProjectCatalog {
    interface Reader { JSONObject read(File file) throws Exception; }
    static final class Entry {
        final File file; final String name, identity; final int count; final long modified;
        Entry(File file,String name,String identity,int count,long modified) {
            this.file=file;this.name=name;this.identity=identity;this.count=count;this.modified=modified;
        }
    }
    static void remember(File file, JSONObject project) {
        try {
            File index=new File(file.getParentFile(),"catalog-v1.json");
            JSONObject entries=new JSONObject();
            try { JSONObject saved=new JSONObject(ProjectTransfer.read(index));
                if(saved.optInt("version")==1) entries=saved.getJSONObject("entries"); } catch(Exception ignored) { }
            entries.put(file.getName(),new JSONObject().put("identity",ProjectTransfer.fingerprint(project))
                    .put("name",project.optString("siteName","Проект")).put("count",project.getJSONArray("records").length())
                    .put("size",file.length()).put("modified",file.lastModified()));
            ProjectTransfer.write(index,new JSONObject().put("version",1).put("entries",entries).toString());
        } catch(Exception ignored) { /* The archive is the source of truth; rebuild a missing cache later. */ }
    }
    static List<Entry> list(File directory, Reader reader) throws Exception {
        directory.mkdirs();
        File index=new File(directory,"catalog-v1.json");
        JSONObject cached=new JSONObject();
        try { if(index.isFile()) { JSONObject saved=new JSONObject(ProjectTransfer.read(index));
            if(saved.optInt("version")==1) cached=saved.getJSONObject("entries"); } } catch(Exception ignored) { }
        JSONObject fresh=new JSONObject(); List<Entry> result=new ArrayList<>();
        File[] files=directory.listFiles((d,n)->n.matches("[0-9a-f]{64}(\\.[0-9]{13})?\\.s3project"));
        if(files==null) return result;
        Arrays.sort(files,(a,b)->Long.compare(b.lastModified(),a.lastModified()));
        for(File file:files) {
            try {
                long size=file.length(), stamp=file.lastModified();
                JSONObject meta=cached.optJSONObject(file.getName());
                if(meta==null || meta.optLong("size",-1)!=size || meta.optLong("modified",-1)!=stamp
                        || !meta.has("name") || meta.optInt("count",-1)<0
                        || !file.getName().startsWith(meta.optString("identity")+".")) {
                    JSONObject project=reader.read(file);
                    String identity=ProjectTransfer.fingerprint(project);
                    if(!file.getName().startsWith(identity+".")) throw new IOException("Project identity mismatch");
                    meta=new JSONObject().put("name",project.optString("siteName","Проект"))
                            .put("identity",identity).put("count",project.getJSONArray("records").length())
                            .put("size",size).put("modified",stamp);
                }
                fresh.put(file.getName(),meta);
                result.add(new Entry(file,meta.getString("name"),meta.getString("identity"),meta.getInt("count"),stamp));
            } catch(Exception error) { result.add(new Entry(null,file.getName(),"",0,file.lastModified())); }
        }
        // Cache errors do not make project files inaccessible.
        try { ProjectTransfer.write(index,new JSONObject().put("version",1).put("entries",fresh).toString()); }
        catch(IOException ignored) { }
        return result;
    }
}
