// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;
import java.util.*;
import java.nio.file.*;
final class FloorPlanSelfTest {
    static int checks;static void check(boolean b){checks++;if(!b)throw new AssertionError("Plan check "+checks);}
    static void near(float a,float b){check(Math.abs(a-b)<.002);}
    public static void main(String[] args)throws Exception{
        FloorPlanTransform t=new FloorPlanTransform();t.resize(400,600,1000,500);near(t.drawWidth(),400);near(t.top,200);check(!t.contains(200,100));check(t.contains(200,250));
        t.scale(3,200,300);near(t.zoom,3);near(t.imageX(200),.5f);near(t.imageY(300),.5f);
        t.pan(20000,-20000);check(t.left<=0&&t.left>=t.width-t.drawWidth());check(t.top<=0&&t.top>=t.height-t.drawHeight());
        t.reset();near(t.zoom,1);near(t.top,200);t.focus(.7f,.5f);near(t.screenX(.7f),200);
        Random random=new Random(27);for(int i=0;i<300;i++){t.scale(.5f+random.nextFloat()*2,200,300);t.pan(random.nextFloat()*300-150,random.nextFloat()*300-150);float x=random.nextFloat(),y=random.nextFloat();near(t.imageX(t.screenX(x)),x);near(t.imageY(t.screenY(y)),y);check(t.zoom>=1&&t.zoom<=8);}
        t.scale(Float.NaN,0,0);check(Float.isFinite(t.zoom));t.resize(600,400,1000,500);check(t.zoom>=1&&t.zoom<=8);
        JSONObject state=Fieldwork.empty();for(String id:new String[]{"a","b"})state.getJSONArray("plans").put(new JSONObject().put("id",id).put("name",id).put("image","AQID").put("points",new JSONArray()));
        check(Fieldwork.plansForRecord(state,"dev").length()==0);Fieldwork.putPoint(state,"b","dev",.25,.75);check(Fieldwork.plansForRecord(state,"DEV").getJSONObject(0).getString("id").equals("b"));
        Fieldwork.putPoint(state,"b","DEV",.4,.2);check(state.getJSONArray("plans").getJSONObject(1).getJSONArray("points").length()==1);
        Fieldwork.putPoint(state,"a","dev",.1,.1);check(Fieldwork.plansForRecord(state,"dev").length()==2);Fieldwork.removePoint(state,"b","dev");check(Fieldwork.plansForRecord(state,"dev").length()==1);
        String before=state.toString();try{Fieldwork.putPoint(state,"a","dev",Double.NaN,.5);throw new AssertionError();}catch(java.io.IOException expected){}check(before.equals(state.toString()));
        Fieldwork.read(state.toString());String fixture=System.getenv("STRELETZ_PLAN_TEST_OUTPUT");if(fixture!=null){JSONObject windows=new JSONObject(Files.readString(Path.of(fixture)));JSONObject imported=Fieldwork.forProject(Fieldwork.read(windows.getJSONObject("fieldwork").toString()),windows);check(imported.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getString("recordId").equals("fixture-device"));check(imported.getJSONArray("scans").length()==1);}
        JSONObject project=new JSONObject().put("records",new JSONArray().put(new JSONObject().put("id","dev")));
        JSONObject baseline=Fieldwork.empty();baseline.getJSONArray("plans").put(new JSONObject().put("id","floor").put("name","Floor").put("image","AQID").put("points",new JSONArray()));
        JSONObject incoming=new JSONObject(baseline.toString());Fieldwork.putPoint(incoming,"floor","dev",.3,.4);int[] conflicts={0};
        JSONObject merged=Fieldwork.merge(baseline,incoming,project,baseline,conflicts);check(Fieldwork.plansForRecord(merged,"dev").length()==1);check(conflicts[0]==0);check(baseline.getJSONArray("plans").getJSONObject(0).getJSONArray("points").length()==0);
        JSONObject remoteMoved=new JSONObject(incoming.toString());Fieldwork.putPoint(remoteMoved,"floor","dev",.8,.6);
        JSONObject moved=Fieldwork.merge(merged,remoteMoved,project,incoming,conflicts);near((float)moved.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.8f);check(conflicts[0]==0);
        JSONObject localMoved=new JSONObject(incoming.toString());Fieldwork.putPoint(localMoved,"floor","dev",.1,.1);
        merged=Fieldwork.merge(localMoved,remoteMoved,project,incoming,conflicts);near((float)merged.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.1f);check(conflicts[0]==1);
        conflicts[0]=0;merged=Fieldwork.merge(localMoved,incoming,project,incoming,conflicts);check(conflicts[0]==0);near((float)merged.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.1f);
        JSONObject removed=new JSONObject(remoteMoved.toString());Fieldwork.removePoint(removed,"floor","dev");merged=Fieldwork.merge(moved,removed,project,remoteMoved,conflicts);check(Fieldwork.plansForRecord(merged,"dev").length()==0);
        merged=Fieldwork.merge(localMoved,remoteMoved,project,null,conflicts);check(conflicts[0]==1);near((float)merged.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.1f);
        conflicts[0]=0;Fieldwork.merge(remoteMoved,remoteMoved,project,incoming,conflicts);check(conflicts[0]==0);
        JSONObject firstLoad=Fieldwork.merge(Fieldwork.empty(),incoming,project,null,conflicts);check(firstLoad.getJSONArray("plans").getJSONObject(0).optString("_importedPlanDigest").length()==64);
        JSONObject afterRestart=Fieldwork.merge(firstLoad,remoteMoved,project,null,conflicts);near((float)afterRestart.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.8f);check(conflicts[0]==0);
        JSONObject editedPhone=new JSONObject(firstLoad.toString());Fieldwork.putPoint(editedPhone,"floor","dev",.2,.2);
        Fieldwork.merge(editedPhone,incoming,project,null,conflicts);check(conflicts[0]==0);
        JSONObject conflictAfterRestart=Fieldwork.merge(editedPhone,remoteMoved,project,null,conflicts);check(conflicts[0]==1);near((float)conflictAfterRestart.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.2f);
        JSONObject editedWindows=new JSONObject(firstLoad.toString());Fieldwork.putPoint(editedWindows,"floor","dev",.9,.9);conflicts[0]=0;
        JSONObject roundTrip=Fieldwork.merge(firstLoad,editedWindows,project,null,conflicts);near((float)roundTrip.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).getDouble("x"),.9f);check(conflicts[0]==0);
        System.out.println("PASS FloorPlan: "+checks+" checks; zoom/focus/clamp/inverse placement, device lookup, Windows import, clean updates, removals and local conflict preservation");
    }
}
