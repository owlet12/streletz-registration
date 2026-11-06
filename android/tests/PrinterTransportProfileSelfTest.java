// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

public final class PrinterTransportProfileSelfTest {
    private static void check(boolean value){if(!value)throw new AssertionError();}
    private static void blocked(byte[] data,int mtu){try{PrinterTransportProfile.validateWrite("wp9529",data,mtu);throw new AssertionError("Unexpected write permitted");}catch(IllegalArgumentException expected){}}
    public static void main(String[] args){
        check(PrinterTransportProfile.matches("wp9529","9529#TEST",false));
        for(String name:new String[]{null,"9529#","95291#TEST","95292#TEST","D110","FFF0"})check(!PrinterTransportProfile.matches("wp9529",name,true));
        check(PrinterTransportProfile.matches("d110","d110-test",false));
        check(PrinterTransportProfile.matches("d110",null,true));
        check(!PrinterTransportProfile.WP_WRITE.equals(PrinterTransportProfile.WP_NOTIFY));
        check(PrinterTransportProfile.maxPayload(23)==20&&PrinterTransportProfile.maxPayload(185)==182&&PrinterTransportProfile.maxPayload(517)==182);
        byte[] header={2,(byte)240,0,0,0,0,2,32,30,85,1};
        PrinterTransportProfile.validateWrite("wp9529",header,23);
        PrinterTransportProfile.validateWrite("wp9529",new byte[]{2,80,0,0,0,0,1,24},23);
        for(int op:new int[]{1,4})PrinterTransportProfile.validateWrite("wp9529",new byte[]{(byte)op},23);
        byte[] fourLines=new byte[52];fourLines[0]=3;fourLines[3]=4;
        PrinterTransportProfile.validateWrite("wp9529",fourLines,185);blocked(fourLines,23);
        byte[] oneLine=new byte[16];oneLine[0]=3;oneLine[3]=1;
        PrinterTransportProfile.validateWrite("wp9529",oneLine,23);
        oneLine[3]=2;blocked(oneLine,185);
        header[10]=2;blocked(header,185);header[10]=1;header[8]=40;blocked(header,185);
        for(int op:new int[]{0x80,0x81,0x82,0xb5,0x07,0xcd})blocked(new byte[]{(byte)op,0},185);
        blocked(new byte[]{1,0},185);blocked(new byte[0],185);
        PrinterTransportProfile.validateWrite("d110",new byte[1024],23);
        check(PrinterTransportProfile.parse("wp9539").equals("wp9539"));
        check(PrinterTransportProfile.service("wp9539").equals(PrinterTransportProfile.WP_SERVICE));
        check(PrinterTransportProfile.matches("wp9539","9539#TEST",false));
        for(String name:new String[]{null,"9539#","95391#TEST","9539A#TEST","9529#TEST","D110"})check(!PrinterTransportProfile.matches("wp9539",name,true));
        for(String selected:new String[]{"wp9539","wp9529"}){
            for(byte[] forbidden:new byte[][]{{(byte)0xb2},{(byte)0xbf,0,0,0,0,0,0},{(byte)0x80},{(byte)0xb5,60,0},{3,0,0,5}}){
                try{PrinterTransportProfile.validateWrite(selected,forbidden,185);throw new AssertionError();}catch(IllegalArgumentException expected){}
            }
        }
        PrinterTransportProfile.validateWrite("wp9539",new byte[]{2,64,3,0,0,0,1,32},23); // 832 rows, 100 mm + 4 mm feed.
        blocked(new byte[]{2,64,3,0,0,0,1,32},23);
        byte[] last=new byte[16];last[0]=3;last[1]=63;last[2]=3;last[3]=1;
        PrinterTransportProfile.validateWrite("wp9539",last,23);blocked(last,23);
        last[1]=64;try{PrinterTransportProfile.validateWrite("wp9539",last,23);throw new AssertionError();}catch(IllegalArgumentException expected){}
        check(PrinterTransportProfile.detect("9539#TEST",false).equals("wp9539"));
        check(PrinterTransportProfile.detect("9529#TEST",true).equals("wp9529"));
        check(PrinterTransportProfile.detect("D110-test",false).equals("d110"));
        for(String name:new String[]{"95391#TEST","9539A#TEST","95292#TEST","WP9539A"})check(PrinterTransportProfile.detect(name,true)==null);
        check(PrinterTransportProfile.detect("other",false)==null);
        check(PrinterTransportProfile.savedDevice("wp9539","9539#TEST","FA:00:00:00:00:01"));
        check(!PrinterTransportProfile.savedDevice("wp9539","9529#TEST","FA:00:00:00:00:01"));
        check(!PrinterTransportProfile.savedDevice("unknown","9539#TEST","FA:00:00:00:00:01"));
        check(!PrinterTransportProfile.savedDevice("wp9539","9539#TEST","invalid"));
        System.out.println("PASS PrinterTransportProfile: exact names, separate channels, whole-command MTU limits, valid frames, unsupported commands rejected, D110 unchanged");
    }
}
