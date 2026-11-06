// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import org.json.*;
import com.google.zxing.*;
import com.google.zxing.common.*;
import com.google.zxing.qrcode.QRCodeWriter;

public final class Optimization014SelfTest {
    static int checks;
    static void check(boolean ok,String message){checks++;if(!ok)throw new AssertionError(message);}
    public static void main(String[] args)throws Exception{
        archives();frames();durable();metrics();preparedList();
        System.out.println("Optimization014SelfTest: "+checks+" checks passed (archive revision, QR buffer ownership, durable writes, metrics privacy, worker list preparation)");
    }
    static final String ID="00000000-0000-4000-8000-000000000001";
    static final String FP="a".repeat(64);
    static ExportSnapshot snapshot(Map<String,Object> prefs,Map<String,Object> bridge,String description,JSONArray panels){
        return new ExportSnapshot(Arrays.asList(new ExportSnapshot.Row(ID,"Device",description,"Zone","Group","Type",1,false,null)),
                "Private site",FP,panels,false,new ProjectPanelIndex(Collections.emptyList(),Collections.emptyMap()),prefs,bridge,Collections.emptySet());
    }
    static void archives()throws Exception{
        JSONObject root=new JSONObject().put("records",new JSONArray().put(new JSONObject().put("id",ID)));
        JSONArray panels=new JSONArray();Map<String,Object> prefs=new HashMap<>(),bridge=new HashMap<>();
        prefs.put("note:"+FP+":"+ID,"PRIVATE-NOTE");bridge.put("pairing_code","PRIVATE-CODE");
        String revision=ArchiveFreshness.revision(root,snapshot(prefs,bridge,"Room",panels));
        File directory=Files.createTempDirectory("archive-revision-").toFile(),file=new File(directory,"project.s3project");
        AtomicInteger writes=new AtomicInteger();ProjectTransfer.Change save=()->{writes.incrementAndGet();ProjectTransfer.write(file,"project "+writes.get());};
        check(ArchiveFreshness.saveIfChanged(file,revision,save)&&writes.get()==1,"first archive not saved");
        for(int i=0;i<20;i++)check(!ArchiveFreshness.saveIfChanged(file,revision,save),"unchanged archive rebuilt");
        check(writes.get()==1,"unchanged builder ran");
        bridge.put("view-state:"+FP,"private view");bridge.put("pairing_code","CHANGED-CODE");prefs.put("note:other:id","other project");
        check(ArchiveFreshness.revision(root,snapshot(prefs,bridge,"Room",panels)).equals(revision),"non-exported preferences invalidated archive");
        for(String field:Arrays.asList("stage","serial","device_key","note","updated","description","registration_summary")){
            Map<String,Object> changed=new HashMap<>(prefs);changed.put(field+":"+FP+":"+ID,"new "+field);
            check(!ArchiveFreshness.revision(root,snapshot(changed,bridge,"Room",panels)).equals(revision),"journal change lost: "+field);
        }
        for(String field:Arrays.asList("panel","registration","registration-history")){
            Map<String,Object> changed=new HashMap<>(bridge);changed.put(field+":"+FP+":"+ID,"new "+field);
            check(!ArchiveFreshness.revision(root,snapshot(prefs,changed,"Room",panels)).equals(revision),"bridge export change lost: "+field);
        }
        check(!ArchiveFreshness.revision(root,snapshot(prefs,bridge,"Changed room",panels)).equals(revision),"model metadata change lost");
        JSONObject replaced=new JSONObject(root.toString()).put("extra","changed");
        check(!ArchiveFreshness.revision(replaced,snapshot(prefs,bridge,"Room",panels)).equals(revision),"project replacement lost");
        String contents=ProjectTransfer.read(ArchiveFreshness.marker(file));
        check(!contents.contains("PRIVATE")&&!contents.contains(ID),"archive stamp contains private data");
        ProjectTransfer.write(file,"external edit with different length");
        check(ArchiveFreshness.saveIfChanged(file,revision,save)&&writes.get()==2,"external edit not detected");
        Files.delete(file.toPath());check(ArchiveFreshness.saveIfChanged(file,revision,save)&&writes.get()==3,"missing archive not rebuilt");
        ProjectTransfer.write(ArchiveFreshness.marker(file),"corrupt");
        check(ArchiveFreshness.saveIfChanged(file,revision,save)&&writes.get()==4,"corrupt marker not rebuilt");
        try{ArchiveFreshness.saveIfChanged(file,"new-revision",()->{throw new IOException("disk");});throw new AssertionError("failed save accepted");}
        catch(IOException expected){checks++;}
        check(!ArchiveFreshness.matches(file,"new-revision"),"failed save marked fresh");
        Files.delete(ArchiveFreshness.marker(file).toPath());Files.delete(file.toPath());Files.delete(directory.toPath());
    }
    static void frames()throws Exception{
        QrFramePool pool=new QrFramePool();byte[] source=new byte[640*480];Arrays.fill(source,(byte)255);
        BitMatrix qr=new QRCodeWriter().encode("synthetic-private-qr",BarcodeFormat.QR_CODE,160,160);
        for(int y=0;y<160;y++)for(int x=0;x<160;x++)source[(100+y)*640+200+x]=qr.get(x,y)?0:(byte)255;
        ScannerGeometry.Crop crop=new ScannerGeometry.Crop(180,80,200,200);
        QrFramePool.Frame first=pool.crop(source,640,480,crop);byte[] storage=first.data;
        check(storage.length==200*200&&storage.length<source.length,"copied the full preview instead of frame");
        String result=new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(first.data,first.width,first.height,0,0,first.width,first.height,false)))).getText();
        check(result.equals("synthetic-private-qr"),"cropped pool QR unreadable");
        byte[] old=first.data.clone();Arrays.fill(source,(byte)13);
        QrFramePool.Frame second=pool.crop(source,640,480,crop);
        check(second!=first&&Arrays.equals(old,first.data),"new camera generation overwrote busy decoder frame");
        check(pool.crop(source,640,480,crop)==null,"unbounded pending frames");
        pool.release(first);QrFramePool.Frame reused=pool.crop(source,640,480,crop);
        check(reused==first&&reused.data==storage,"same-size scan allocated a new array");
        pool.release(reused);pool.release(second);
        for(int i=0;i<1000;i++){QrFramePool.Frame f=pool.crop(source,640,480,crop);check(f.data==storage,"repeated scan allocated");pool.release(f);}
        try{pool.crop(source,640,480,new ScannerGeometry.Crop(600,0,100,100));throw new AssertionError("invalid frame");}
        catch(IllegalArgumentException expected){checks++;}
    }
    static class Store implements DurablePreferenceChange.Store {
        final Map<String,Object> values=new HashMap<>();int writes;final Set<Integer> failures=new HashSet<>();
        public Object get(String key){return values.get(key);}
        public boolean write(Map<String,Object> updates){writes++;for(Map.Entry<String,Object> e:updates.entrySet())if(e.getValue()==null)values.remove(e.getKey());else values.put(e.getKey(),e.getValue());return !failures.contains(writes);}
    }
    static void durable()throws Exception{
        Map<String,Object> before=new HashMap<>(),after=new HashMap<>();before.put("operation","old");before.put("history",null);
        after.put("operation","reviewed");after.put("history","audit");
        Store ok=new Store();ok.values.put("operation","old");DurablePreferenceChange.commit(ok,before,after);
        check(ok.writes==1&&ok.values.equals(after),"operation and audit not one write");
        try{DurablePreferenceChange.commit(ok,before,after);throw new AssertionError("stale write accepted");}catch(IOException expected){checks++;}
        check(ok.writes==1,"stale comparison mutated preferences");
        Store fail=new Store();fail.values.put("operation","old");fail.failures.add(1);
        try{DurablePreferenceChange.commit(fail,before,after);throw new AssertionError("disk failure accepted");}
        catch(DurablePreferenceChange.Failure expected){check(expected.rollbackSucceeded,"successful rollback marked uncertain");}
        check(fail.values.size()==1&&fail.values.get("operation").equals("old"),"rollback lost absence of audit key");
        fail.writes=0;fail.failures.add(2);
        try{DurablePreferenceChange.commit(fail,before,after);throw new AssertionError("double failure accepted");}
        catch(DurablePreferenceChange.Failure expected){check(!expected.rollbackSucceeded,"failed rollback marked safe");}
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);AtomicBoolean completed=new AtomicBoolean();
        Store delayed=new Store(){public boolean write(Map<String,Object> changes){entered.countDown();try{release.await();}catch(InterruptedException e){throw new RuntimeException(e);}return super.write(changes);}};
        delayed.values.put("operation","old");ExecutorService worker=Executors.newSingleThreadExecutor();
        Future<?> future=worker.submit(()->{try{DurablePreferenceChange.commit(delayed,before,after);completed.set(true);}catch(IOException e){throw new RuntimeException(e);}});
        check(entered.await(2,TimeUnit.SECONDS)&&!completed.get(),"completion preceded durable write");release.countDown();future.get(2,TimeUnit.SECONDS);
        check(completed.get(),"background commit did not finish");worker.shutdown();
    }
    static void metrics()throws Exception{
        PerformanceMetrics metrics=new PerformanceMetrics();metrics.record(PerformanceMetrics.Task.SEARCH,1000000,5,true);metrics.record(PerformanceMetrics.Task.SEARCH,3000000,7,false);
        JSONObject report=metrics.report(),entry=report.getJSONArray("measurements").getJSONObject(0);
        check(entry.getLong("runs")==2&&entry.getDouble("averageMs")==2&&entry.getDouble("maximumMs")==3,"duration aggregation wrong");
        check(entry.getLong("totalItems")==12&&entry.getLong("unsuccessfulRuns")==1,"counts wrong");
        Set<String> fields=new HashSet<>(Arrays.asList("task","runs","averageMs","maximumMs","lastMs","totalItems","unsuccessfulRuns","lastItems"));
        check(entry.keySet().equals(fields),"report contains unexpected free text fields");
        check(report.keySet().equals(new HashSet<>(Arrays.asList("format","version","appVersion","scope","measurements"))) && report.getString("appVersion").equals("0.5.25"),"report schema or app version incorrect");
        Thread[] writers=new Thread[4];for(int i=0;i<writers.length;i++){writers[i]=new Thread(()->{for(int j=0;j<1000;j++)metrics.record(PerformanceMetrics.Task.QR_DECODE,10,1,true);});writers[i].start();}
        for(Thread thread:writers)thread.join();JSONArray values=metrics.report().getJSONArray("measurements");
        check(values.length()==2,"metric rows grew per event");JSONObject camera=values.getJSONObject(1);
        check(camera.getLong("runs")==4000&&camera.getLong("totalItems")==4000,"concurrent metrics lost updates");
    }
    static void preparedList()throws Exception{
        Map<String,Object> journal=new HashMap<>();journal.put("stage:"+FP+":"+ID,3);journal.put("description:"+FP+":"+ID,"Описание");
        ExportSnapshot captured=snapshot(journal,new HashMap<>(),"Room",new JSONArray());journal.put("stage:"+FP+":"+ID,0);
        ExecutorService worker=Executors.newSingleThreadExecutor();ExportSnapshot.PreparedList list=worker.submit(captured::prepareList).get();worker.shutdown();
        check(list.counts.total()==1&&list.counts.checked()==1,"worker list read changing preferences");
        check(list.index.query("ОПИСАНИЕ",3,"Group","?",()->false).equals(Arrays.asList(ID)),"worker index differs from effective model");
    }
}
