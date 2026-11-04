// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

public final class StageCountsSelfTest {
    public static void main(String[] args) {
        StageCounts counts = new StageCounts();
        int[] reference = new int[100];
        for (int i = 0; i < reference.length; i++) counts.set("record-" + i, 0);
        java.util.Random random = new java.util.Random(512);
        for (int n = 0; n < 500; n++) {
            int index = random.nextInt(reference.length), stage = random.nextInt(4);
            reference[index] = stage;
            counts.set("RECORD-" + index, stage);
            counts.set("record-" + index, stage); // duplicate preference notification
            int found = 0, initialized = 0, checked = 0;
            for (int current : reference) { if (current >= 1) found++; if (current >= 2) initialized++; if (current == 3) checked++; }
            if (counts.found() != found || counts.initialized() != initialized || counts.checked() != checked || counts.total() != 100)
                throw new AssertionError("Incremental counters differ after update " + n);
        }
        System.out.println("StageCountsSelfTest: 500 mixed updates matched independent full recounts (including decreases and duplicate notifications)");
    }
}
