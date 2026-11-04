// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import javax.xml.parsers.*;

public final class Fieldwork015SelfTest {
    static int count;
    static final String A="11111111-1111-4111-8111-111111111111",B="22222222-2222-4222-8222-222222222222",C="33333333-3333-4333-8333-333333333333";
    static void check(boolean value,String message){count++;if(!value)throw new AssertionError(message);}
    interface Action{void run()throws Exception;}
    static void rejects(Action a,String message)throws Exception{boolean failed=false;try{a.run();}catch(Exception e){failed=true;}check(failed,message);}
    static JSONObject record(String id,String description)throws Exception{return new JSONObject().put("id",id).put("recordKind","device").put("name","Датчик").put("description",description).put("zone","101");}
    static JSONObject project(JSONObject...rows)throws Exception{JSONArray a=new JSONArray();for(JSONObject row:rows)a.put(row);return new JSONObject().put("schemaVersion",1).put("siteName","Тестовый объект").put("records",a);}
    static JSONObject plan(String id,String record)throws Exception{return new JSONObject().put("id",id).put("name","Этаж 1").put("image","AQID").put("points",new JSONArray().put(new JSONObject().put("recordId",record).put("x",0.25).put("y",0.75)));}
    public static void main(String[] args)throws Exception{batch();transfer();preview();reports();connection();System.out.println("Fieldwork015SelfTest: "+count+" checks passed (batch persistence, portable plans, selective import, report evidence and XLSX XML, read-only diagnostic labels)");}
    static void batch()throws Exception{
        JSONObject s=Fieldwork.empty();check(Fieldwork.addScan(s,"fa0000000003","a1b2c3d4",10),"first scan");
        for(int i=0;i<100;i++)check(!Fieldwork.addScan(s,"FA0000000003","A1B2C3D4",11),"duplicate QR inserted");
        check(s.getJSONArray("scans").length()==1,"duplicate count");
        rejects(()->Fieldwork.addScan(s,"FA0000000003","FFFFFFFF",12),"conflicting key accepted");
        check("A1B2C3D4".equals(s.getJSONArray("scans").getJSONObject(0).getString("key")),"conflict overwrote key");
        rejects(()->Fieldwork.addScan(s,"123","",12),"short ID accepted");rejects(()->Fieldwork.addScan(s,"FA0000000004","NO-KEY",12),"bad key accepted");
        Fieldwork.addScan(s,"FA0000000004","",13);check(!Fieldwork.addScan(s,"FA0000000004","12345678",14),"key filling created row");
        Path file=Files.createTempFile("fieldwork-roundtrip", ".json");ProjectTransfer.write(file.toFile(),s.toString());JSONObject loaded=Fieldwork.read(ProjectTransfer.read(file.toFile()));
        check(ImportPreview.canonical(s).equals(ImportPreview.canonical(loaded)),"reopen changed scans");Files.delete(file);
        JSONObject malformed=new JSONObject(s.toString());malformed.getJSONArray("scans").put(new JSONObject(malformed.getJSONArray("scans").getJSONObject(0).toString()));rejects(()->Fieldwork.read(malformed.toString()),"import duplicate accepted");
        rejects(()->Fieldwork.read("{broken"),"corrupt state accepted");rejects(()->Fieldwork.read(new JSONObject().put("version",2).put("scans",new JSONArray()).put("plans",new JSONArray()).toString()),"new version accepted");
    }
    static void transfer()throws Exception{
        JSONObject fields=Fieldwork.empty();Fieldwork.addScan(fields,"FA0000000003","A1B2C3D4",1);fields.getJSONArray("scans").getJSONObject(0).put("recordId",B);fields.getJSONArray("plans").put(plan("one",A)).put(plan("two",B));
        JSONObject clipped=Fieldwork.forProject(fields,project(record(A,"Комната")));check(clipped.getJSONArray("plans").getJSONObject(0).getJSONArray("points").length()==1,"valid point lost");check(clipped.getJSONArray("plans").getJSONObject(1).getJSONArray("points").length()==0,"removed record retained on plan");check(!clipped.getJSONArray("scans").getJSONObject(0).has("recordId"),"removed assignment kept");
        check(fields.getJSONArray("plans").getJSONObject(1).getJSONArray("points").length()==1,"source mutated");
        JSONObject other=Fieldwork.empty();other.getJSONArray("plans").put(plan("three",A));JSONObject merged=Fieldwork.merge(fields,other,project(record(A,"Room"),record(B,"Store")));check(merged.getJSONArray("plans").length()==3,"new plans lost");
        JSONObject invalid=Fieldwork.empty();invalid.getJSONArray("plans").put(plan("bad",A));invalid.getJSONArray("plans").getJSONObject(0).getJSONArray("points").getJSONObject(0).put("x",1.01);rejects(()->Fieldwork.read(invalid.toString()),"outside image accepted");
        Map<String,Object> prefs=new HashMap<>();prefs.put(Fieldwork.key(Optimization014SelfTest.FP),fields.toString());ExportSnapshot snap=Optimization014SelfTest.snapshot(prefs,new HashMap<>(),"Room",new JSONArray());JSONObject backup=snap.build();check(backup.has("fieldwork"),"backup omits plans");
        JSONObject base=project(record(Optimization014SelfTest.ID,"Room"));JSONObject portable=ProjectDocuments.portable(base,backup);check(ImportPreview.canonical(fields).equals(ImportPreview.canonical(portable.getJSONObject("fieldwork"))),"portable omitted or altered fieldwork");
        String before=ArchiveFreshness.revision(base,snap);prefs.put(Fieldwork.key(Optimization014SelfTest.FP),other.toString());check(!before.equals(ArchiveFreshness.revision(base,Optimization014SelfTest.snapshot(prefs,new HashMap<>(),"Room",new JSONArray()))),"plan change did not invalidate archive");
    }
    static void preview()throws Exception{
        JSONObject old=project(record(A,"Старое описание"),record(B,"Оставить")),incoming=project(record(A,"Новое описание"),record(C,"Добавить"));String before=old.toString();ImportPreview preview=new ImportPreview(old,incoming);
        check(preview.related&&preview.changes.size()==3,"diff missing add/change/remove");
        JSONObject none=preview.select(Collections.emptySet(),false);check(ImportPreview.rows(none).size()==2&&"Старое описание".equals(ImportPreview.rows(none).get(A).optString("description")),"deselection changed records");
        JSONObject some=preview.select(new HashSet<>(Arrays.asList(A,C)),false);check(ImportPreview.rows(some).size()==3&&"Новое описание".equals(ImportPreview.rows(some).get(A).optString("description")),"selective import lost kept or added rows");
        JSONObject all=preview.select(new HashSet<>(Arrays.asList(A,B,C)),false);check(ImportPreview.rows(all).size()==2&&!ImportPreview.rows(all).containsKey(B),"selected deletion ignored");check(old.toString().equals(before),"preview mutated current project");
        check(!new ImportPreview(old,project(record(C,"Other"))).related,"foreign project merged");
        JSONObject panel=new JSONObject().put("projectPanelId","p").put("panelId","p").put("recordIds",new JSONArray().put(A).put(B));old.put("panels",new JSONArray().put(panel));JSONObject nextPanel=new JSONObject(panel.toString()).put("recordIds",new JSONArray().put(A).put(C));incoming.put("panels",new JSONArray().put(nextPanel));
        JSONObject selected=new ImportPreview(old,incoming).select(new HashSet<>(Arrays.asList(A,C)),false);Set<String> members=new HashSet<>();JSONArray m=selected.getJSONArray("panels").getJSONObject(0).getJSONArray("recordIds");for(int i=0;i<m.length();i++)members.add(m.getString(i));check(members.equals(new HashSet<>(Arrays.asList(A,B,C))),"partial import panel membership lost");
        JSONObject sorted=new JSONObject().put("a",1).put("b",2),reversed=new JSONObject().put("b",2).put("a",1);check(ImportPreview.canonical(sorted).equals(ImportPreview.canonical(reversed)),"key order produced false diff");
        JSONObject sameRows=new JSONObject(old.toString());sameRows.getJSONArray("panels").getJSONObject(0).put("host","new-host");
        ImportPreview routes=new ImportPreview(old,sameRows);
        check("new-host".equals(routes.select(Collections.emptySet(),false,true).getJSONArray("panels").getJSONObject(0).optString("host")),"catalogue-only change ignored");
        check(!"new-host".equals(routes.select(Collections.emptySet(),false,false).getJSONArray("panels").getJSONObject(0).optString("host")),"declined catalogue change applied");
        incoming.put("fieldwork",Fieldwork.empty());check(!new ImportPreview(old,incoming).select(Collections.emptySet(),false).has("fieldwork"),"declined fields imported");
    }
    static JSONObject reportFixture()throws Exception{
        JSONObject p=project(record(A,"=HYPERLINK(\"bad\")"));String fp=ProjectTransfer.fingerprint(p);JSONObject r=p.getJSONArray("records").getJSONObject(0);r.put("serial","FA0000000003").put("deviceKey","SECRET-KEY").put("panelLabel","Панель 1").put("stageName","Проверено");
        JSONObject op=new JSONObject().put("operationId","operation").put("projectFingerprint",fp).put("recordId",A).put("state","registered").put("registrationConfirmed",true).put("projectSaved",true).put("expectedHardwareId","FA0000000003").put("confirmedHardwareId","FA0000000003").put("host","SECRET-HOST");r.put("registration",op);p.put("projectFingerprint",fp);return p;
    }
    static void reports()throws Exception{
        JSONObject p=reportFixture();check(WorkReport.rows(p).get(0).complete,"verified report not complete");JSONObject op=p.getJSONArray("records").getJSONObject(0).getJSONObject("registration");
        op.put("projectSaved",false);check(!WorkReport.rows(p).get(0).complete,"unsaved considered complete");op.put("projectSaved",true);
        for(String flag:Arrays.asList("uncertain","bridgeHistoryMissing","localResolved")){op.put(flag,true);check(!WorkReport.rows(p).get(0).complete,"uncertainty considered complete: "+flag);op.remove(flag);}
        op.put("confirmedHardwareId","FA0000000004");check(!WorkReport.rows(p).get(0).complete,"wrong device confirmed");op.put("confirmedHardwareId","FA0000000003");
        op.put("registrationConfirmed","true");check(!WorkReport.rows(p).get(0).complete,"text boolean accepted");op.put("registrationConfirmed",true);
        op.put("recordId",B);check(!WorkReport.rows(p).get(0).complete,"foreign record confirmed");op.put("recordId",A);
        p.getJSONArray("records").getJSONObject(0).remove("registration");check(!WorkReport.rows(p).get(0).complete,"stage alone became confirmation");
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();WorkReport.xlsx(bytes,p);int entries=0;String all="";DocumentBuilderFactory factory=DocumentBuilderFactory.newInstance();factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl",true);
        try(ZipInputStream zip=new ZipInputStream(new ByteArrayInputStream(bytes.toByteArray()))){ZipEntry e;while((e=zip.getNextEntry())!=null){byte[] content=zip.readAllBytes();factory.newDocumentBuilder().parse(new ByteArrayInputStream(content));String text=new String(content,java.nio.charset.StandardCharsets.UTF_8);all+=text;entries++;}}
        check(entries==7,"incomplete XLSX package");check(all.contains("Незавершённые")&&all.contains("inlineStr")&&!all.contains("<f>"),"formula injection or missing pending sheet");check(!all.contains("SECRET-KEY")&&!all.contains("SECRET-HOST"),"report exposed secrets");
        Path file=Paths.get("tmp/report-0515-test.xlsx");Files.createDirectories(file.getParent());Files.write(file,bytes.toByteArray());
    }
    static void connection()throws Exception{
        JSONObject r=new JSONObject().put("ok",true).put("projectFingerprint","fp").put("catalogSource","cached_project").put("nativeAvailable",false).put("panels",new JSONArray().put(new JSONObject().put("projectPanelId","p").put("name","Панель").put("available",true)));
        String text=ConnectionCheck.describe(r,"fp","p");check(text.contains("маршрут не подтверждён")&&text.contains("Физическая связь с контроллером: не проверена"),"cached availability counted as live");
        r.put("nativeAvailable",true).put("catalogSource","native_project");text=ConnectionCheck.describe(r,"fp","p");check(text.contains("маршрут и принадлежность подтверждены Master")&&text.contains("не проверена"),"live route misreported");
        text=ConnectionCheck.describe(r,"other","p");check(text.contains("другой")&&!text.contains("маршрут и принадлежность подтверждены Master"),"foreign project accepted");
        check(ConnectionCheck.failure(new java.net.UnknownHostException("SECRET-HOST")).contains("Не удалось определить адрес"),"DNS failure not explained");
        check(!ConnectionCheck.failure(new java.net.UnknownHostException("SECRET-HOST")).contains("SECRET-HOST"),"raw exception leaked");
        r.put("nativeAvailable",false).put("connectionErrorCode","master_login_required");check(ConnectionCheck.describe(r,"fp","p").contains("Выполните вход"),"login failure missing next step");
        check(ConnectionCheck.describe(new JSONObject().put("ok",false).put("errorCode","unsupported_command"),"fp","").contains("0.5.15"),"old bridge no upgrade guidance");
    }
}
