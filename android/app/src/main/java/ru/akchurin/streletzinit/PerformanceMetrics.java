// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import java.util.*;
import org.json.*;

/** Only enum names, counts and monotonic durations. No free text, project/device IDs or credentials. */
final class PerformanceMetrics {
    enum Task { PROJECT_LOAD, LIST_PREPARE, SEARCH, EXPORT, ARCHIVE, QR_DECODE, STORAGE }
    static final PerformanceMetrics INSTANCE=new PerformanceMetrics();
    private final EnumMap<Task,long[]> stats=new EnumMap<>(Task.class);
    synchronized void record(Task task,long nanos,int items,boolean success) {
        long[] s=stats.computeIfAbsent(task,k->new long[7]);
        long duration=Math.max(0,nanos);s[0]++;s[1]+=duration;s[2]=Math.max(s[2],duration);s[3]=duration;
        s[4]+=Math.max(0,items);if(!success)s[5]++;s[6]=Math.max(0,items);
    }
    synchronized JSONObject report() throws JSONException {
        JSONArray entries=new JSONArray();
        for(Map.Entry<Task,long[]> entry:stats.entrySet()) {
            long[] s=entry.getValue();entries.put(new JSONObject().put("task",entry.getKey().name())
                    .put("runs",s[0]).put("averageMs",s[0]==0?0:s[1]/1e6/s[0]).put("maximumMs",s[2]/1e6)
                    .put("lastMs",s[3]/1e6).put("totalItems",s[4]).put("unsuccessfulRuns",s[5]).put("lastItems",s[6]));
        }
        return new JSONObject().put("format","streletz-performance").put("version",1).put("appVersion","0.5.28")
                .put("scope","current_process").put("measurements",entries);
    }
}
