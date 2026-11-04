// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.*;
import java.util.function.BooleanSupplier;

/** Indexed facets and immutable search rows. Never reads Android preferences on the worker. */
final class DeviceFilterIndex {
    static final class Row {
        final String id, group, panel, text, type;
        final int stage;
        Row(String id, String group, String panel, int stage, String text) {
            this(id,group,panel,stage,text,"");
        }
        Row(String id, String group, String panel, int stage, String text, String type) {
            this.id=id; this.group=group; this.panel=panel; this.stage=Math.max(0,Math.min(3,stage));
            this.text=text.toLowerCase(Locale.ROOT);
            this.type=typeKey(type);
        }
    }
    private final List<Row> rows = new ArrayList<>();
    private final Map<String,Integer> positions = new HashMap<>();
    private final Map<String,BitSet> groups = new HashMap<>(), panels = new HashMap<>(), types = new HashMap<>();
    static String typeKey(String type) { return type == null ? "" : type.trim().toLowerCase(Locale.ROOT); }
    private final BitSet[] stages = {new BitSet(),new BitSet(),new BitSet(),new BitSet()};
    synchronized void put(Row row) {
        String key=row.id.toLowerCase(Locale.ROOT);
        Integer i=positions.get(key);
        if(i==null) { i=rows.size(); positions.put(key,i); rows.add(row); }
        else { Row old=rows.set(i,row); groups.get(old.group).clear(i); panels.get(old.panel).clear(i); types.get(old.type).clear(i); stages[old.stage].clear(i); }
        groups.computeIfAbsent(row.group,k->new BitSet()).set(i);
        panels.computeIfAbsent(row.panel,k->new BitSet()).set(i);
        types.computeIfAbsent(row.type,k->new BitSet()).set(i);
        stages[row.stage].set(i);
    }
    List<String> query(String query, int stage, String group, String panel, BooleanSupplier stale) {
        return query(query,stage,group,panel,null,stale);
    }
    List<String> query(String query, int stage, String group, String panel, String type, BooleanSupplier stale) {
        List<Row> selected=new ArrayList<>();
        synchronized(this) {
            BitSet candidates=new BitSet(); candidates.set(0,rows.size());
            if(stage>0) {
                BitSet allowed=(BitSet)stages[stage==1?0:stage].clone();
                if(stage==1) allowed.or(stages[1]);
                candidates.and(allowed);
            }
            if(group!=null) candidates.and(groups.getOrDefault(group,new BitSet()));
            if(panel!=null && !panel.isEmpty()) candidates.and(panels.getOrDefault(panel,new BitSet()));
            if(type!=null) candidates.and(types.getOrDefault(typeKey(type),new BitSet()));
            for(int i=candidates.nextSetBit(0);i>=0;i=candidates.nextSetBit(i+1)) {
                if(stale.getAsBoolean()) return null;
                selected.add(rows.get(i));
            }
        }
        String needle=query.trim().toLowerCase(Locale.ROOT);
        List<String> result=new ArrayList<>();
        for(Row row:selected) { if(stale.getAsBoolean()) return null; if(needle.isEmpty() || row.text.contains(needle)) result.add(row.id); }
        return result;
    }
}
