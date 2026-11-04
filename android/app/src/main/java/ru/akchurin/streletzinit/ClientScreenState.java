// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;

/** Only presentation/draft data. Restoring this object never authorizes a command. */
final class ClientScreenState {
    String fingerprint="",query="",group="Все группы",panel="",anchor="",card="";
    String deviceType; // null = all; empty = missing device type
    int stage,offset,draftStage;
    long recordUpdated;
    String serial="",deviceKey="",note="",description="";
    String encode() throws JSONException {
        return new JSONObject().put("version",1).put("fingerprint",fingerprint).put("query",query)
                .put("group",group).put("deviceType",deviceType).put("panel",panel).put("anchor",anchor).put("offset",offset)
                .put("stage",stage).put("card",card).put("draftStage",draftStage).put("recordUpdated",recordUpdated)
                .put("serial",serial).put("deviceKey",deviceKey).put("note",note).put("description",description).toString();
    }
    static ClientScreenState decode(String raw,String expected) {
        try {
            if(raw==null || raw.length()>262144) return null;
            JSONObject j=new JSONObject(raw);
            if(j.optInt("version")!=1 || !expected.equals(j.optString("fingerprint"))) return null;
            ClientScreenState s=new ClientScreenState();s.fingerprint=expected;s.query=j.optString("query");
            s.group=j.optString("group","Все группы");s.panel=j.optString("panel");s.anchor=j.optString("anchor");
            s.deviceType=j.has("deviceType") && !j.isNull("deviceType") ? j.optString("deviceType") : null;
            s.offset=j.optInt("offset");s.stage=Math.max(0,Math.min(3,j.optInt("stage")));
            s.card=j.optString("card");s.draftStage=Math.max(0,Math.min(3,j.optInt("draftStage")));
            s.recordUpdated=j.optLong("recordUpdated");s.serial=j.optString("serial");s.deviceKey=j.optString("deviceKey");
            s.note=j.optString("note");s.description=j.optString("description");return s;
        } catch(Exception ignored) { return null; }
    }
}
