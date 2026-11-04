// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.*;

/** Allowlisted report columns. No raw journal, QR keys, bridge addresses or passwords. */
final class WorkReport {
    static final String[] HEADERS={"Зона / помещение","Описание","ID устройства","Панель","Время","Инициализация","Проект Windows","Сверка","Этап журнала"};
    static final class Row {final String[] cells;final boolean complete;Row(String[] c,boolean done){cells=c;complete=done;}}
    static List<Row> rows(JSONObject backup)throws Exception {
        List<Row> result=new ArrayList<>();JSONArray records=backup.getJSONArray("records");
        java.text.SimpleDateFormat date=new java.text.SimpleDateFormat("dd.MM.yyyy HH:mm",Locale.ROOT);
        for(int i=0;i<records.length();i++) {JSONObject r=records.getJSONObject(i),op=r.optJSONObject("registration");
            String serial=r.optString("serial");
            boolean confirmed=op!=null&&Boolean.TRUE.equals(op.opt("registrationConfirmed"))&&"registered".equals(op.optString("state"))
                &&!op.optBoolean("uncertain")&&!op.optBoolean("bridgeHistoryMissing")&&!op.optBoolean("localResolved")
                &&!op.optString("operationId").isEmpty()&&r.optString("id").equalsIgnoreCase(op.optString("recordId"))
                &&backup.optString("projectFingerprint").equals(op.optString("projectFingerprint"))
                &&serial.matches("(?i)[0-9a-f]{12}")&&serial.equalsIgnoreCase(op.optString("expectedHardwareId"))
                &&serial.equalsIgnoreCase(op.optString("confirmedHardwareId"));
            boolean saved=confirmed&&Boolean.TRUE.equals(op.opt("projectSaved"));
            String status=confirmed?"Подтверждено на панели":op==null?"Нет подтверждения":
                op.optBoolean("uncertain")||op.optBoolean("bridgeHistoryMissing")||"unknown".equals(op.optString("state"))?"Результат неизвестен":
                "failed".equals(op.optString("state"))?"Ошибка":"cancelled".equals(op.optString("state"))?"Отменено":"Ожидает подтверждения";
            String review=op!=null&&op.optJSONObject("manualReview")!=null?"Ручная сверка (не автоматическое подтверждение)":"Нет";
            long updated=op!=null&&op.optLong("updatedAt")>0?op.optLong("updatedAt"):r.optLong("updatedAt");
            result.add(new Row(new String[]{r.optString("zone"),r.optString("description").isEmpty()?r.optString("name"):r.optString("description"),serial,r.optString("panelLabel"),updated>0?date.format(new Date(updated)):"Не указано",status,saved?"Сохранён":"Не подтверждено",review,r.optString("stageName")},confirmed&&saved));
        }return result;
    }
    static void xlsx(OutputStream output,JSONObject backup)throws Exception {
        List<Row> all=rows(backup),pending=new ArrayList<>();for(Row r:all)if(!r.complete)pending.add(r);
        try(ZipOutputStream zip=new ZipOutputStream(output)) {
            entry(zip,"[Content_Types].xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            entry(zip,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            entry(zip,"xl/workbook.xml","<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Все устройства\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Незавершённые\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            entry(zip,"xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            entry(zip,"xl/styles.xml","<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"2\"><xf fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf><xf fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
            entry(zip,"xl/worksheets/sheet1.xml",sheet(all));entry(zip,"xl/worksheets/sheet2.xml",sheet(pending));
        }
    }
    private static String sheet(List<Row> rows){StringBuilder b=new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols><col min=\"1\" max=\"9\" width=\"28\" customWidth=\"1\"/></cols><sheetData>");row(b,HEADERS,1,true);int n=2;for(Row r:rows)row(b,r.cells,n++,false);return b.append("</sheetData><autoFilter ref=\"A1:I").append(n-1).append("\"/></worksheet>").toString();}
    private static void row(StringBuilder b,String[] cells,int n,boolean header){b.append("<row r=\"").append(n).append("\" ht=\"45\" customHeight=\"1\">");for(int i=0;i<cells.length;i++)b.append("<c r=\"").append((char)('A'+i)).append(n).append("\" t=\"inlineStr\" s=\"").append(header?1:0).append("\"><is><t xml:space=\"preserve\">").append(xml(cells[i])).append("</t></is></c>");b.append("</row>");}
    private static String xml(String s){return s.replaceAll("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]","").replace("&","&amp;").replace("<","&lt;").replace(">","&gt;").replace("\"","&quot;");}
    private static void entry(ZipOutputStream z,String name,String data)throws IOException{z.putNextEntry(new ZipEntry(name));z.write(data.getBytes(StandardCharsets.UTF_8));z.closeEntry();}
}
