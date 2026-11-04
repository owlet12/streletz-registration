// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Small HTTP/1.1 exchange over the already authenticated TLS socket; exactly one POST, never a retry. */
final class BridgeHttp {
    static void write(OutputStream out, BridgeEndpoint endpoint, byte[] frame) throws IOException {
        String header="POST "+endpoint.path+" HTTP/1.1\r\nHost: "+endpoint.authority
                +"\r\nContent-Type: application/json; charset=utf-8\r\nAccept: application/json"
                +"\r\nAccept-Encoding: identity\r\nCache-Control: no-store\r\nConnection: close"
                +"\r\nContent-Length: "+frame.length+"\r\n\r\n";
        out.write(header.getBytes(StandardCharsets.US_ASCII));out.write(frame);out.flush();
    }

    static String read(InputStream stream, int maxBytes) throws IOException {
        BufferedInputStream input=new BufferedInputStream(stream);
        int[] budget={32768};
        String status=line(input,budget);
        if(!status.matches("HTTP/1\\.[01] [0-9]{3}( .*)?")) throw new IOException("HTTPS-шлюз вернул некорректный HTTP-ответ.");
        int code=Integer.parseInt(status.substring(9,12));
        Map<String,String> headers=new HashMap<>();
        for(String row;(row=line(input,budget)).length()>0;) {
            int colon=row.indexOf(':');
            if(colon<=0) throw new IOException("Некорректные заголовки HTTPS-шлюза.");
            String key=row.substring(0,colon).toLowerCase(Locale.ROOT), value=row.substring(colon+1).trim();
            if(headers.containsKey(key) && (key.equals("content-length") || key.equals("transfer-encoding")))
                throw new IOException("Неоднозначная длина ответа HTTPS-шлюза.");
            headers.put(key,value);
        }
        if(code>=300 && code<400) throw new IOException("HTTPS-шлюз перенаправляет запрос. Укажите конечный адрес моста; команда повторно не отправлялась.");
        if(code!=200) throw new IOException("HTTPS-шлюз: HTTP "+code+". Проверьте адрес шлюза и доступность Windows-моста.");
        String type=headers.getOrDefault("content-type","").split(";",2)[0].trim();
        if(!"application/json".equalsIgnoreCase(type)) throw new IOException("По HTTPS-адресу открыт сайт или страница входа. Нужен адрес шлюза Windows-моста с ответом JSON.");
        if(!"identity".equalsIgnoreCase(headers.getOrDefault("content-encoding","identity")))
            throw new IOException("HTTPS-шлюз вернул неподдерживаемое сжатие ответа.");
        String transfer=headers.get("transfer-encoding"), length=headers.get("content-length");
        if(transfer!=null && length!=null) throw new IOException("Неоднозначная длина ответа HTTPS-шлюза.");
        ByteArrayOutputStream out=new ByteArrayOutputStream(Math.min(maxBytes,8192));
        if(transfer!=null) {
            if(!transfer.equalsIgnoreCase("chunked")) throw new IOException("Неизвестный формат ответа HTTPS-шлюза.");
            int chunks=0;
            while(true) {
                if(++chunks>65536) throw new IOException("Слишком много фрагментов ответа HTTPS-шлюза.");
                String chunk=line(input,new int[]{1024}).split(";",2)[0];
                long size=number(chunk,16);
                if(size>maxBytes-out.size()) throw tooLarge();
                if(size==0) { while(!line(input,budget).isEmpty()) { } break; }
                copy(input,out,(int)size);
                if(input.read()!='\r' || input.read()!='\n') throw new IOException("Неполный фрагмент ответа HTTPS-шлюза.");
            }
        } else if(length!=null) {
            long size=number(length,10);
            if(size>maxBytes) throw tooLarge();
            copy(input,out,(int)size);
        } else {
            byte[] buffer=new byte[8192];
            for(int count;(count=input.read(buffer))!=-1;) {
                if(count>maxBytes-out.size()) throw tooLarge();
                out.write(buffer,0,count);
            }
        }
        if(out.size()==0) throw new IOException("HTTPS-шлюз вернул пустой ответ.");
        return new String(out.toByteArray(),StandardCharsets.UTF_8);
    }

    private static long number(String value,int radix) throws IOException {
        if(!value.matches(radix==16?"[0-9a-fA-F]+":"[0-9]+")) throw new IOException("Некорректная длина ответа HTTPS-шлюза.");
        try {return Long.parseLong(value,radix);}catch(NumberFormatException e){throw tooLarge();}
    }
    private static IOException tooLarge(){return new IOException("Ответ HTTPS-шлюза превышает допустимый размер.");}
    private static void copy(InputStream in,ByteArrayOutputStream out,int size)throws IOException {
        byte[] buffer=new byte[Math.min(8192,Math.max(1,size))];
        while(size>0){int n=in.read(buffer,0,Math.min(size,buffer.length));if(n<0)throw new EOFException("HTTPS-шлюз оборвал ответ.");out.write(buffer,0,n);size-=n;}
    }
    private static String line(InputStream in,int[] budget)throws IOException {
        ByteArrayOutputStream out=new ByteArrayOutputStream();
        while(--budget[0]>=0) {
            int next=in.read();if(next<0)throw new EOFException("HTTPS-шлюз оборвал заголовки.");
            if(next=='\r'){if(--budget[0]<0 || in.read()!='\n')throw new IOException("Некорректные заголовки HTTPS-шлюза.");return out.toString("US-ASCII");}
            if(next<' ' || next>126)throw new IOException("Некорректные заголовки HTTPS-шлюза.");
            out.write(next);
        }
        throw new IOException("Заголовки HTTPS-шлюза превышают допустимый размер.");
    }
}
