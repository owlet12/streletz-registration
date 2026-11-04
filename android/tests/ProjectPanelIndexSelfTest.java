// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Synthetic project topology; no Android, network, or hardware dependency. */
public final class ProjectPanelIndexSelfTest {
    private static int assertions;

    public static void main(String[] args) {
        ProjectPanelIndex.Panel a = panel("panel-a", "Панель", "sensor-a", "SHARED");
        ProjectPanelIndex.Panel b = panel("panel-b", "Панель", "sensor-b", "shared");
        ProjectPanelIndex index = new ProjectPanelIndex(Arrays.asList(a, b), Collections.emptyMap());
        check(index.uniqueForRecord("SENSOR-A") == a, "membership is case-insensitive and exact");
        check(index.uniqueForRecord("sensor-b") == b, "same panel names do not merge identities");
        check(!a.displayName().equals(b.displayName()), "equal labels and route remain distinguishable");
        check(index.uniqueForRecord("shared") == null, "ambiguous membership cannot auto-select first panel");
        check(index.uniqueForRecord("missing") == null, "missing member cannot auto-select sole/default panel");
        check(index.matchesFilter("sensor-a", "PANEL-A"), "filter uses stable identity");
        check(!index.matchesFilter("sensor-a", "panel-b"), "filter does not use an unrelated saved choice");
        check(index.matchesFilter("shared", "?"), "ambiguous records remain discoverable under unresolved filter");
        check(index.matchesFilter("missing", "?"), "missing records remain discoverable under unresolved filter");
        check(!index.matchesFilter("sensor-a", "?"), "resolved records are excluded from unresolved filter");
        check(index.matchesFilter("shared", ""), "all panels retains unresolved records");

        Map<String, String> pointers = new HashMap<>();
        pointers.put("sensor-c", "panel-a");
        pointers.put("sensor-a", "panel-b");
        pointers.put("sensor-d", "deleted-panel");
        ProjectPanelIndex imported = new ProjectPanelIndex(Arrays.asList(a, b), pointers);
        check(imported.uniqueForRecord("SENSOR-C") == a, "portable project pointer supplies membership");
        check(imported.uniqueForRecord("sensor-a") == null, "pointer/catalog conflict is ambiguous, never silently repaired");
        check(imported.uniqueForRecord("sensor-d") == null, "dangling pointer does not create a fake panel");
        rejects(() -> new ProjectPanelIndex(Arrays.asList(a, panel("PANEL-A", "Other")), Collections.emptyMap()),
                "duplicate identity is rejected even with another label");

        List<ProjectPanelIndex.Panel> full = new ArrayList<>();
        for (int panel = 0; panel < 9; panel++) {
            List<String> members = new ArrayList<>();
            for (int record = panel; record < 453; record += 9) members.add("record-" + record);
            full.add(new ProjectPanelIndex.Panel("panel-" + panel, "Контроллер " + panel, "native-" + panel,
                    "segment", "192.0.2.1", 6344, "001122334455", members));
        }
        ProjectPanelIndex offline = new ProjectPanelIndex(full, Collections.emptyMap());
        int selected = 0;
        for (int i = 0; i < 453; i++) {
            check(offline.uniqueForRecord("record-" + i).id.equals("panel-" + i % 9), "complete 453-record offline map");
            if (offline.matchesFilter("record-" + i, "panel-0")) selected++;
        }
        check(selected == 51, "filter count is independent of live connection/availability");
        check(ProjectPanelIndex.description("3R.ВТН1", false, "ignored").equals("3R.ВТН1"), "project description is default");
        check(ProjectPanelIndex.description("3R.ВТН1", true, "").isEmpty(), "explicitly cleared description remains empty");
        check(ProjectPanelIndex.description("updated in Windows", true, "монтаж").equals("монтаж"),
                "refresh cannot overwrite local description");
        System.out.println("ProjectPanelIndexSelfTest: " + assertions + " assertions passed");
    }

    private static ProjectPanelIndex.Panel panel(String id, String name, String... records) {
        return new ProjectPanelIndex.Panel(id, name, "native", "segment", "192.0.2.1", 6344,
                "001122334455", Arrays.asList(records));
    }
    private static void check(boolean value, String message) {
        if (!value) throw new AssertionError(message);
        assertions++;
    }
    private static void rejects(Runnable action, String message) {
        try { action.run(); } catch (IllegalArgumentException expected) { assertions++; return; }
        throw new AssertionError(message);
    }
}
