// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

final class DeviceLabels {
    private DeviceLabels() {}

    static String preferredName(String name, String description) {
        String cleanDescription = description == null ? "" : description.trim();
        if (!cleanDescription.isEmpty()) return cleanDescription;
        return name == null ? "" : name;
    }
}
