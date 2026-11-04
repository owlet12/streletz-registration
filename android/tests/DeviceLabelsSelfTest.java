// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

public final class DeviceLabelsSelfTest {
    private static int assertions;

    public static void main(String[] args) {
        check(DeviceLabels.preferredName("Наименование", "Описание").equals("Описание"),
                "description replaces name");
        check(DeviceLabels.preferredName("Наименование", "  Описание  ").equals("Описание"),
                "description is trimmed");
        check(DeviceLabels.preferredName("Наименование", "").equals("Наименование"),
                "empty description falls back to name");
        check(DeviceLabels.preferredName("Наименование", "   ").equals("Наименование"),
                "blank description falls back to name");
        check(DeviceLabels.preferredName(null, null).isEmpty(),
                "missing values remain safe");
        System.out.println("DeviceLabelsSelfTest: " + assertions + " assertions passed");
    }

    private static void check(boolean condition, String message) {
        assertions++;
        if (!condition) throw new AssertionError(message);
    }
}
