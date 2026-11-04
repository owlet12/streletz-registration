// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.*;
import java.nio.file.*;
import java.util.*;
import org.json.*;
import java.lang.reflect.*;

public final class FreshProjectSessionSelfTest {
    private static int checks;
    public static void main(String[] args) throws Exception {
        FreshProjectSession session=new FreshProjectSession();
        check(session.begin(false),"first open starts fresh");
        check(!session.begin(true),"rotation or camera activity recreation retains current session");
        session.end();check(session.begin(false),"explicit close then open starts fresh");
        check(new FreshProjectSession().begin(true),"OS-killed process never restores stale base from saved activity state");
        check(session.begin(false),"new launch without activity state clears old base");
        for(String key:new String[]{"host","port","pairing_code","operation:project:device","operation-history:project:device","panel:project:device"})
            check(!FreshProjectSession.cachedPreference(key),"durable setting survives: "+key);
        for(String key:new String[]{"project_source","sync_revision","sync_endpoint","sync_fingerprint","project-panels:project","view-state:project","panel-filter:project"})
            check(FreshProjectSession.cachedPreference(key),"stale cache discarded: "+key);
        Path root=Files.createTempDirectory("fresh-project-");
        Path outside=Files.createTempDirectory("user-export-");
        Files.writeString(outside.resolve("export.s3project"),"external export");
        Files.createDirectories(root.resolve("projects"));
        Files.writeString(root.resolve("projects/old.json"),"archive");
        Files.writeString(root.resolve("projects/old.json.meta"),"index");
        Files.writeString(root.resolve("current-project.json"),"old base");
        Files.writeString(root.resolve("current-project.json.pending"),"interrupted write");
        Files.writeString(root.resolve("work-journal.json"),"user edits and pending operation");
        FreshProjectSession.clearFiles(root.toFile());
        check(!Files.exists(root.resolve("current-project.json")),"active base deleted");
        check(!Files.exists(root.resolve("current-project.json.pending")),"pending copy deleted");
        check(!Files.exists(root.resolve("projects")),"internal archives and index deleted");
        check(Files.readString(root.resolve("work-journal.json")).contains("pending operation"),"unrelated work journal untouched");
        check(Files.readString(outside.resolve("export.s3project")).equals("external export"),"external export untouched");
        FreshProjectSession.clearFiles(root.toFile());check(true,"cleanup repeatable");

        // A rollback must restore the operation journal before discarding the project file.
        File target=root.resolve("current-project.json").toFile();
        ProjectTransfer.write(target,"OLD");
        Map<String,Object> saved=new HashMap<>();saved.put("operation:device","unresolved");
        Map<String,Object> bridge=new HashMap<>();bridge.put("host","192.0.2.1");bridge.put("pairing_code","test-only");
        try {
            ProjectTransaction.install(target,"NEW",saved,bridge,()->{throw new IOException("power loss simulation");},
                    (a,b)->{throw new IOException("interrupted rollback");});
            throw new AssertionError("expected failure");
        }catch(ProjectTransfer.InstallException expected){check(!expected.rollbackSucceeded,"pending transaction retained");}
        try{FreshProjectSession.clearFiles(root.toFile());throw new AssertionError("must recover first");}
        catch(IOException expected){check(target.exists(),"cleanup blocked until operation journal recovered");}
        Map<String,Object> recovered=new HashMap<>(), recoveredBridge=new HashMap<>();
        ProjectTransaction.recover(target,(a,b)->{recovered.putAll(a);recoveredBridge.putAll(b);});
        FreshProjectSession.clearFiles(root.toFile());
        check(recovered.equals(saved),"operation identity recovered before cache deletion");
        check(recoveredBridge.equals(bridge),"endpoint and pairing preserved during interrupted-save cleanup");
        check(!target.exists(),"recovered old base discarded before next download");
        JSONObject empty=new JSONObject().put("schemaVersion",1).put("records",new JSONArray());
        try{ProjectTransfer.fingerprint(empty);throw new AssertionError("external empty project cannot be accepted");}
        catch(IOException expected){check(true,"empty view does not weaken imported-project validation");}
        productionModel(empty);
        System.out.println("FreshProjectSessionSelfTest: "+checks+" checks passed (launch/recreation, crash recovery, cache scope, journal preservation)");
    }
    private static void productionModel(JSONObject empty)throws Exception {
        // Exercise the actual model publication path, bypassing only Android UI construction.
        Class<?> type=Class.forName("sun.misc.Unsafe");Field field=type.getDeclaredField("theUnsafe");field.setAccessible(true);
        Object activity=type.getMethod("allocateInstance",Class.class).invoke(field.get(null),MainActivity.class);
        Map<String,Object> journal=new HashMap<>(),bridge=new HashMap<>();journal.put("journal_scoped_v1",true);
        Method preferences=MainActivityDataSelfTest.class.getDeclaredMethod("preferences",Map.class);preferences.setAccessible(true);
        set(activity,"prefs",preferences.invoke(null,journal));set(activity,"bridgePrefs",preferences.invoke(null,bridge));
        set(activity,"devices",new ArrayList<>());set(activity,"operations",new HashMap<>());
        set(activity,"panelFilterIds",new ArrayList<>());set(activity,"projectRecordMetadata",new JSONArray());
        Method loadEmpty=MainActivity.class.getDeclaredMethod("loadProject",JSONObject.class,String.class,boolean.class);loadEmpty.setAccessible(true);
        loadEmpty.invoke(activity,empty,FreshProjectSession.EMPTY_FINGERPRINT,true);
        check((Boolean)get(activity,"waitingForProject"),"production empty view waits for download");
        check(((List<?>)get(activity,"devices")).isEmpty(),"offline launch displays no embedded or stale devices");
        check(((ProjectPanelIndex)get(activity,"panelIndex")).panels().isEmpty(),"offline launch has no stale panel authorization");
        String id="00000000-0000-4000-8000-000000000001";
        JSONObject fresh=new JSONObject().put("schemaVersion",1).put("siteName","Fresh project").put("records",new JSONArray()
                .put(new JSONObject().put("id",id).put("recordKind","device").put("name","New device").put("description","Fresh description")));
        String fingerprint=ProjectTransfer.fingerprint(fresh);
        journal.put("note:"+fingerprint+":"+id,"unsent field note");
        bridge.put("operation:"+fingerprint+":"+id,"unresolved identity");
        Map<String,Object> beforeJournal=new HashMap<>(journal),beforeBridge=new HashMap<>(bridge);
        Method load=MainActivity.class.getDeclaredMethod("loadProject",JSONObject.class,String.class);load.setAccessible(true);load.invoke(activity,fresh,fingerprint);
        check(!(Boolean)get(activity,"waitingForProject"),"fresh response activates project");
        check(((List<?>)get(activity,"devices")).size()==1&&get(activity,"siteName").equals("Fresh project"),"download replaces empty model with actual project");
        check(journal.equals(beforeJournal)&&bridge.equals(beforeBridge),"fresh load preserves unsent work and unresolved operation identity");
    }
    private static void set(Object target,String name,Object value)throws Exception {Field field=MainActivity.class.getDeclaredField(name);field.setAccessible(true);field.set(target,value);}
    private static Object get(Object target,String name)throws Exception {Field field=MainActivity.class.getDeclaredField(name);field.setAccessible(true);return field.get(target);}
    private static void check(boolean condition,String message){if(!condition)throw new AssertionError(message);checks++;}
}
