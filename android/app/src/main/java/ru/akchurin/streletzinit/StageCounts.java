// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/** O(1) counter updates for a single record, including stage decreases and duplicate notifications. */
final class StageCounts {
    private final Map<String, Integer> stages = new HashMap<>();
    private final int[] counts = new int[4];
    void set(String id, int stage) {
        stage = Math.max(0, Math.min(3, stage));
        Integer previous = stages.put(id.toLowerCase(Locale.ROOT), stage);
        if (previous != null) counts[previous]--;
        counts[stage]++;
    }
    int total() { return stages.size(); }
    int found() { return counts[1] + counts[2] + counts[3]; }
    int initialized() { return counts[2] + counts[3]; }
    int checked() { return counts[3]; }
}
