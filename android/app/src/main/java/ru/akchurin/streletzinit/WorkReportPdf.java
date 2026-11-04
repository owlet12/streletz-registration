// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import android.graphics.*;
import android.graphics.pdf.PdfDocument;
import java.io.*;
import java.util.*;
import org.json.*;

final class WorkReportPdf {
    static void write(OutputStream output,JSONObject backup)throws Exception {
        java.util.List<WorkReport.Row> rows=WorkReport.rows(backup),pending=new ArrayList<>();for(WorkReport.Row row:rows)if(!row.complete)pending.add(row);
        PdfDocument pdf=new PdfDocument();Writer writer=null;try {writer=new Writer(pdf,backup.optString("siteName"));
            writer.section("Все устройства — "+rows.size(),rows);writer.section("Незавершённые — "+pending.size(),pending);
            writer.finish();pdf.writeTo(output);
        }finally{if(writer!=null)writer.finish();pdf.close();}
    }
    private static final class Writer {
        final PdfDocument pdf;final String site;final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);PdfDocument.Page page;Canvas canvas;int y,pageNo;
        Writer(PdfDocument pdf,String site){this.pdf=pdf;this.site=site.length()>120?site.substring(0,117)+"…":site;paint.setTypeface(Typeface.create("sans-serif",Typeface.NORMAL));paint.setTextSize(10);}
        void next(){finish();page=pdf.startPage(new PdfDocument.PageInfo.Builder(595,842,++pageNo).create());canvas=page.getCanvas();y=32;line("Стрелец · Отчёт по выполненным работам",true);wrap(site,false);line("Страница "+pageNo,false);y+=10;}
        void finish(){if(page!=null){pdf.finishPage(page);page=null;}}
        void line(String text,boolean bold){if(page==null||y>805)next();paint.setFakeBoldText(bold);canvas.drawText(text,28,y,paint);y+=15;}
        void wrap(String text,boolean bold){for(String paragraph:text.replace('\r',' ').split("\n",-1)){String left=paragraph;if(left.isEmpty()){line("",bold);continue;}while(!left.isEmpty()){int count=paint.breakText(left,true,535,null);if(count<1)count=1; if(count<left.length()){int space=left.lastIndexOf(' ',count);if(space>count/2)count=space;}line(left.substring(0,count),bold);left=left.substring(count).trim();}}}
        void section(String title,java.util.List<WorkReport.Row> rows){next();wrap(title,true);if(rows.isEmpty())line("Нет устройств",false);for(WorkReport.Row row:rows){if(y>650)next();for(int i=0;i<WorkReport.HEADERS.length;i++)wrap(WorkReport.HEADERS[i]+": "+row.cells[i],i==1);y+=12;}}
    }
}
