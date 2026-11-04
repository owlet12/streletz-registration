// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** Project membership only. This catalogue never grants permission to register hardware. */
final class ProjectPanelIndex {
    static final String[] ROUTE_FIELDS = {"routeKind", "terminalPanelId", "terminalSegmentId",
            "terminalDisplayName", "adapterNodeId", "adapterSegmentId", "routeDisplayName"};
    static final class Panel {
        final String id;
        final String name;
        final String nativePanelId;
        final String segmentId;
        final String host;
        final int port;
        final String moduleId;
        final Set<String> recordIds;
        final Map<String, String> routeMetadata;
        final boolean viaPanelTerminal;

        Panel(String id, String name, String nativePanelId, String segmentId, String host,
              int port, String moduleId, Collection<String> recordIds) {
            this(id, name, nativePanelId, segmentId, host, port, moduleId, recordIds,
                    Collections.emptyMap(), false);
        }

        Panel(String id, String name, String nativePanelId, String segmentId, String host,
              int port, String moduleId, Collection<String> recordIds, Map<String, String> routeMetadata,
              boolean viaPanelTerminal) {
            this.id = normalized(id);
            if (this.id.isEmpty()) throw new IllegalArgumentException("Панель проекта без идентификатора");
            this.name = name == null || name.trim().isEmpty() ? "Панель " + this.id : name;
            this.nativePanelId = normalized(nativePanelId);
            this.segmentId = normalized(segmentId);
            this.host = host == null ? "" : host;
            this.port = port;
            this.moduleId = moduleId == null ? "" : moduleId;
            Set<String> ids = new LinkedHashSet<>();
            for (String recordId : recordIds) if (!normalized(recordId).isEmpty()) ids.add(normalized(recordId));
            this.recordIds = Collections.unmodifiableSet(ids);
            this.routeMetadata = Collections.unmodifiableMap(new LinkedHashMap<>(routeMetadata));
            this.viaPanelTerminal = viaPanelTerminal;
        }

        String displayName() {
            String route = !host.isEmpty() ? host + ":" + port : moduleId;
            String terminal = terminalRouteLabel(routeMetadata, viaPanelTerminal);
            if (!terminal.isEmpty()) route = terminal + (route.isEmpty() ? "" : " · " + route);
            // The stable suffix keeps equal names and equal routes distinguishable.
            return name + (route.isEmpty() ? "" : " · " + route) + " · " + id.substring(0, Math.min(8, id.length()));
        }
    }

    static String terminalRouteLabel(Map<String, String> metadata, boolean viaTerminal) {
        if (!viaTerminal && !"panel_terminal".equals(metadata.get("routeKind"))) return "";
        String terminal = metadata.get("terminalDisplayName");
        return "через " + (terminal == null || terminal.trim().isEmpty() ? "Панель-Терминал" : terminal);
    }

    private final List<Panel> panels;
    private final Map<String, List<Panel>> byRecord;

    ProjectPanelIndex(Collection<Panel> values, Map<String, String> recordPointers) {
        Map<String, Panel> byId = new LinkedHashMap<>();
        for (Panel panel : values) {
            if (byId.put(panel.id, panel) != null) throw new IllegalArgumentException("Повторяющийся ID панели проекта");
        }
        panels = Collections.unmodifiableList(new ArrayList<>(byId.values()));
        byRecord = new LinkedHashMap<>();
        for (Panel panel : panels) for (String recordId : panel.recordIds) add(recordId, panel);
        for (Map.Entry<String, String> pointer : recordPointers.entrySet()) {
            Panel panel = byId.get(normalized(pointer.getValue()));
            if (panel != null) add(normalized(pointer.getKey()), panel);
        }
    }

    private void add(String recordId, Panel panel) {
        List<Panel> matches = byRecord.get(recordId);
        if (matches == null) { matches = new ArrayList<>(); byRecord.put(recordId, matches); }
        if (!matches.contains(panel)) matches.add(panel);
    }

    List<Panel> panels() { return panels; }

    List<Panel> forRecord(String recordId) {
        List<Panel> result = byRecord.get(normalized(recordId));
        return result == null ? Collections.emptyList() : Collections.unmodifiableList(result);
    }

    Panel uniqueForRecord(String recordId) {
        List<Panel> matches = forRecord(recordId);
        return matches.size() == 1 ? matches.get(0) : null;
    }

    boolean matchesFilter(String recordId, String panelId) {
        if (panelId == null || panelId.isEmpty()) return true;
        Panel panel = uniqueForRecord(recordId);
        return "?".equals(panelId) ? panel == null : panel != null && panel.id.equals(normalized(panelId));
    }

    static String description(String base, boolean hasOverride, String override) {
        return hasOverride ? (override == null ? "" : override) : (base == null ? "" : base);
    }

    static String normalized(String value) { return value == null ? "" : value.trim().toLowerCase(Locale.ROOT); }
}
