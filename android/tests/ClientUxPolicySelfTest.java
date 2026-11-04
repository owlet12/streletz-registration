// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

public final class ClientUxPolicySelfTest {
    private static int checks;
    public static void main(String[] args) {
        check(ClientUxPolicy.projectDelay(0, 0) == 15000, "fresh project keeps normal cadence");
        check(ClientUxPolicy.projectDelay(1, 0) == 30000 && ClientUxPolicy.projectDelay(2, 0) == 60000, "unchanged snapshots reduce polling");
        check(ClientUxPolicy.projectDelay(999, 0) == 60000, "unchanged polling is bounded");
        check(ClientUxPolicy.projectDelay(0, 1) == 15000 && ClientUxPolicy.projectDelay(0, 4) == 120000, "failed downloads back off");
        check(ClientUxPolicy.projectDelay(0, Integer.MAX_VALUE) == 120000, "failure counter cannot overflow delay");
        check(ClientUxPolicy.operationDelay(0) == 2000 && ClientUxPolicy.operationDelay(1) == 4000, "operation failures back off from normal status interval");
        check(ClientUxPolicy.operationDelay(Integer.MAX_VALUE) == 30000, "status polling remains bounded");
        for (int mask = 0; mask < 32; mask++)
            check(ClientUxPolicy.canAdvance((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
                    (mask & 8) != 0, (mask & 16) != 0) == (mask == 7), "advance requires saved confirmed terminal result without uncertainty or pending request");
        check(ClientUxPolicy.age(-1000).equals("0 с"), "clock adjustment cannot show negative age");
        check(ClientUxPolicy.age(61000).equals("1 мин 1 с"), "long wait stays readable");
        System.out.println("ClientUxPolicySelfTest: " + checks + " checks passed");
    }
    private static void check(boolean ok, String message) {
        if (!ok) throw new AssertionError(message);
        checks++;
    }
}
