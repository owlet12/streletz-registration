// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.Locale;
import java.util.UUID;

/** Model-specific BLE boundaries, independent of Android for fixture tests. */
final class PrinterTransportProfile {
    static final String D110="d110", WP9529="wp9529", WP9539="wp9539";
    static final UUID NIIMBOT_SERVICE=UUID.fromString("e7810a71-73ae-499d-8c15-faa9aef0c3f2");
    static final UUID WP_SERVICE=UUID.fromString("0000fff0-0000-1000-8000-00805f9b34fb");
    static final UUID WP_WRITE=UUID.fromString("0000fff1-0000-1000-8000-00805f9b34fb");
    static final UUID WP_NOTIFY=UUID.fromString("0000fff4-0000-1000-8000-00805f9b34fb");
    static String parse(String value){
        if(value==null||value.isEmpty()||D110.equals(value))return D110;
        if(isVson(value))return value;
        throw new IllegalArgumentException("Неизвестная модель принтера");
    }
    static boolean isVson(String profile){return WP9529.equals(profile)||WP9539.equals(profile);}
    static UUID service(String profile){return isVson(profile)?WP_SERVICE:NIIMBOT_SERVICE;}
    static boolean matches(String profile,String name,boolean niimbotService){
        if(isVson(profile)){String prefix=WP9539.equals(profile)?"9539#":"9529#";return name!=null&&name.startsWith(prefix)&&name.length()>prefix.length();}
        return niimbotService||(name!=null&&name.toUpperCase(Locale.ROOT).startsWith("D110"));
    }
    static String detect(String name,boolean niimbotService){
        if(matches(WP9539,name,false))return WP9539;
        if(matches(WP9529,name,false))return WP9529;
        // Do not mistake excluded VSON variants for a NIIMBOT based on a service alone.
        if(name!=null&&(name.startsWith("95")||name.toUpperCase(Locale.ROOT).startsWith("WP95")))return null;
        return matches(D110,name,niimbotService)?D110:null;
    }
    static boolean savedDevice(String profile,String name,String address){
        return address!=null&&address.matches("(?i)[0-9a-f]{2}(:[0-9a-f]{2}){5}")
            &&(D110.equals(profile)||isVson(profile)&&matches(profile,name,false));
    }
    static int maxPayload(int mtu){return Math.max(20,Math.min(182,mtu-3));}
    static void validateWrite(String profile,byte[] bytes,int mtu){
        if(bytes==null||bytes.length<1||bytes.length>4096)throw new IllegalArgumentException("Некорректный размер пакета");
        parse(profile);
        if(!isVson(profile))return;
        if(bytes.length>maxPayload(mtu))throw new IllegalArgumentException("Команду VSON нельзя разделять на BLE-пакеты");
        int op=bytes[0]&255;
        if((op==1||op==4)&&bytes.length==1)return;
        if(op==2&&(bytes.length==8||bytes.length==11)){
            int lines=(bytes[1]&255)|((bytes[2]&255)<<8),density=bytes[7]&255;
            boolean continuous=bytes.length==8&&bytes[6]==1;
            int feed=WP9539.equals(profile)&&continuous?32:0;
            boolean base=lines>=80+feed&&lines<=800+feed&&lines%8==0&&bytes[3]==0&&bytes[4]==0&&bytes[5]==0&&(density==24||density==32||density==39);
            if(base&&bytes.length==8&&bytes[6]==1)return;
            if(base&&bytes.length==11&&bytes[6]==2&&(bytes[8]&255)*8==lines&&(bytes[9]&255)>0&&bytes[10]==1)return;
        }
        if(op==3&&bytes.length>=16){
            int line=(bytes[1]&255)|((bytes[2]&255)<<8),count=bytes[3]&255;
            if(count>=1&&count<=4&&bytes.length==4+12*count&&line+count<=(WP9539.equals(profile)?832:800))return;
        }
        throw new IllegalArgumentException("Неподдерживаемая команда VSON");
    }
}
