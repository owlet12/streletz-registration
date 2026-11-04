// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import org.json.*;

public final class OptimizationSelfTest {
    static int checks;
    static void check(boolean ok,String message) { checks++;if(!ok)throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        filters();catalog();screen();start();
        System.out.println("OptimizationSelfTest: "+checks+" checks passed (indexed filters, cache IO, state, durable pre-dispatch barrier)");
    }
    static void filters() throws Exception {
        DeviceFilterIndex index=new DeviceFilterIndex();List<DeviceFilterIndex.Row> reference=new ArrayList<>();
        Random random=new Random(1313);
        for(int i=0;i<5000;i++) { DeviceFilterIndex.Row r=new DeviceFilterIndex.Row("id"+i,"group"+(i%19),i%7==0?"?":"p"+(i%13),i%4,"Датчик "+i+" zone"+(i%23));reference.add(r);index.put(r); }
        for(int n=0;n<600;n++) {
            if(n%3==0) {int i=random.nextInt(reference.size());DeviceFilterIndex.Row old=reference.get(i);
                DeviceFilterIndex.Row r=new DeviceFilterIndex.Row(old.id,"group"+random.nextInt(19),"p"+random.nextInt(13),random.nextInt(4),"Изменено zone"+n%23);
                reference.set(i,r);index.put(r);}
            String group=n%5==0?null:"group"+random.nextInt(19),panel=n%4==0?"":n%9==0?"?":"p"+random.nextInt(13);
            int stage=n%4;String needle=n%3==0?"":"ZONE"+random.nextInt(23);
            List<String> expected=new ArrayList<>();
            for(DeviceFilterIndex.Row r:reference) if((stage==0||(stage==1?r.stage<2:r.stage==stage)) && (group==null||r.group.equals(group))
                    &&(panel.isEmpty()||r.panel.equals(panel))&&r.text.contains(needle.toLowerCase(Locale.ROOT)))expected.add(r.id);
            check(expected.equals(index.query(needle,stage,group,panel,()->false)),"indexed result differs from independent full scan "+n);
        }
        check(index.query("",0,null,"",()->true)==null,"cancelled query returned results");
        AtomicInteger calls=new AtomicInteger();
        check(index.query("",0,null,"",()->calls.incrementAndGet()>100)==null,"mid-scan cancellation ignored");
        check(index.query("",0,"missing","",()->false).isEmpty(),"missing group matched");
        index.put(new DeviceFilterIndex.Row("ID0","new","new",3,"new"));
        check(index.query("new",3,"new","new",()->false).equals(Arrays.asList("ID0")),"case insensitive update duplicated GUID");
    }
    static JSONObject project(String name,String id) throws Exception {
        return new JSONObject().put("siteName",name).put("format","streletz-portable-project").put("schemaVersion",1)
                .put("records",new JSONArray().put(new JSONObject().put("id",id).put("recordKind","device")));
    }
    static void catalog() throws Exception {
        File dir=Files.createTempDirectory("streletz-catalog-").toFile();
        JSONObject a=project("Объект A","00000000-0000-4000-8000-000000000001"),b=project("Объект B","00000000-0000-4000-8000-000000000002");
        File fa=new File(dir,ProjectTransfer.fingerprint(a)+".s3project"),fb=new File(dir,ProjectTransfer.fingerprint(b)+".s3project");
        ProjectTransfer.write(fa,a.toString());ProjectTransfer.write(fb,b.toString());
        AtomicInteger reads=new AtomicInteger();SavedProjectCatalog.Reader reader=f->{reads.incrementAndGet();return new JSONObject(ProjectTransfer.read(f));};
        List<SavedProjectCatalog.Entry> first=SavedProjectCatalog.list(dir,reader);
        check(reads.get()==2&&first.size()==2,"cold catalog must read old archives once");
        SavedProjectCatalog.list(dir,reader);check(reads.get()==2,"warm catalog reread project bodies");
        a.put("siteName","Изменённое название A");ProjectTransfer.write(fa,a.toString());
        check(SavedProjectCatalog.list(dir,reader).stream().anyMatch(e->e.name.equals("Изменённое название A")),"changed file metadata stale");
        check(reads.get()==3,"only changed body should be read");
        a.put("siteName","Сохранено приложением");ProjectTransfer.write(fa,a.toString());SavedProjectCatalog.remember(fa,a);
        SavedProjectCatalog.list(dir,reader);check(reads.get()==3,"remembered archive reread");
        ProjectTransfer.write(new File(dir,"catalog-v1.json"),"corrupt");SavedProjectCatalog.list(dir,reader);
        check(reads.get()==5,"corrupt cache not rebuilt");
        ProjectTransfer.write(fb,"corrupt");List<SavedProjectCatalog.Entry> damaged=SavedProjectCatalog.list(dir,reader);
        check(damaged.size()==2&&damaged.stream().filter(e->e.file==null).count()==1,"damaged file hid valid project");
        check(fb.isFile(),"damaged archive deleted");
        Files.delete(fb.toPath());check(SavedProjectCatalog.list(dir,reader).size()==1,"deleted archive retained");
        File index=new File(dir,"catalog-v1.json");Files.delete(index.toPath());check(index.mkdir(),"setup cache write failure");
        check(SavedProjectCatalog.list(dir,reader).size()==1,"cache write failure blocked listing");
        Files.delete(index.toPath());Files.delete(fa.toPath());Files.delete(dir.toPath());
    }
    static void screen() throws Exception {
        ClientScreenState state=new ClientScreenState();state.fingerprint="project-a";state.query="Этаж 2";state.group="Группа";
        state.panel="panel1";state.anchor="record55";state.offset=-27;state.stage=2;state.card="record99";
        state.draftStage=1;state.serial="ABCDEF123456";state.deviceKey="01234567";state.note="Черновик";state.description="Описание";state.recordUpdated=123;
        ClientScreenState copy=ClientScreenState.decode(state.encode(),"project-a");
        check(copy!=null&&copy.encode().equals(state.encode()),"state roundtrip lost draft/position/filters");
        check(ClientScreenState.decode(state.encode(),"project-b")==null,"state crossed project boundary");
        check(ClientScreenState.decode("broken","project-a")==null,"corrupt state accepted");
        check(ClientScreenState.decode(new JSONObject(state.encode()).put("version",2).toString(),"project-a")==null,"unsupported state accepted");
        ClientScreenState clamped=ClientScreenState.decode(new JSONObject(state.encode()).put("stage",99).put("draftStage",-7).toString(),"project-a");
        check(clamped.stage==3&&clamped.draftStage==0,"invalid selection not bounded");
        check(ClientScreenState.decode(null,"project-a")==null,"null state accepted");
    }
    static class Store implements RegistrationStartStore.Store {
        String value="old";int writes;boolean record=true;Set<Integer> failedWrites=new HashSet<>();List<String> events=new ArrayList<>();
        public String readOperation(){return value;}
        public boolean writeOperation(String next){writes++;events.add("operation:"+next);value=next;return !failedWrites.contains(writes);}
        public boolean writeRecord(){events.add("record");return record;}
    }
    static void rejected(Store s,String expected) throws Exception {
        try{RegistrationStartStore.prepare(s,expected,"prepared","failed");throw new AssertionError("accepted failed save");}
        catch(IOException expectedError){checks++;}
    }
    static void start() throws Exception {
        Store ok=new Store();RegistrationStartStore.prepare(ok,"old","prepared","failed");ok.events.add("send");
        check(ok.events.equals(Arrays.asList("operation:prepared","record","send")),"network precedes durable data");
        rejected(ok,"old");check(ok.writes==1,"duplicate preparation overwrote operation");
        Store stale=new Store();rejected(stale,"different");check(stale.events.isEmpty(),"stale expected value mutated store");
        Store opFail=new Store();opFail.failedWrites.add(1);rejected(opFail,"old");
        check(opFail.value.equals("old")&&!opFail.events.contains("record"),"failed op write not rolled back or wrote record");
        Store recordFail=new Store();recordFail.record=false;rejected(recordFail,"old");
        check(recordFail.value.equals("failed"),"record failure not durably terminal");
        Store bothFail=new Store();bothFail.record=false;bothFail.failedWrites.add(2);rejected(bothFail,"old");
        check(bothFail.events.size()==3,"failure path changed order");
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);AtomicBoolean ready=new AtomicBoolean();
        Store blocked=new Store(){public boolean writeRecord(){entered.countDown();try{release.await();}catch(InterruptedException e){throw new RuntimeException(e);}return super.writeRecord();}};
        ExecutorService worker=Executors.newSingleThreadExecutor();Future<?> task=worker.submit(()->{try{RegistrationStartStore.prepare(blocked,"old","prepared","failed");ready.set(true);}catch(Exception e){throw new RuntimeException(e);}});
        check(entered.await(2,TimeUnit.SECONDS)&&!ready.get(),"worker allowed dispatch before record completed");
        release.countDown();task.get(2,TimeUnit.SECONDS);check(ready.get(),"worker did not finish");worker.shutdown();
    }
}
