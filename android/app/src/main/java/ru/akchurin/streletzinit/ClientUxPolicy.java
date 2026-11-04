// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

/** Timing and presentation only. This policy never starts or repeats registration. */
final class ClientUxPolicy {
    static long projectDelay(int unchanged, int failures) {
        if (failures > 0) return boundedBackoff(15000, failures - 1, 120000);
        return boundedBackoff(15000, unchanged, 60000);
    }

    static long operationDelay(int failures) {
        return boundedBackoff(2000, failures, 30000);
    }

    private static long boundedBackoff(long base, int steps, long cap) {
        return Math.min(cap, base * (1L << Math.max(0, Math.min(steps, 10))));
    }

    static boolean canAdvance(boolean confirmed, boolean saved, boolean terminal,
                              boolean unresolved, boolean pending) {
        return confirmed && saved && terminal && !unresolved && !pending;
    }

    static String age(long elapsedMillis) {
        long seconds = Math.max(0, elapsedMillis) / 1000;
        return seconds < 60 ? seconds + " с" : (seconds / 60) + " мин " + (seconds % 60) + " с";
    }
}
