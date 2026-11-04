// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import org.json.JSONObject;

/** Actual child JVM termination at write boundaries; never kills another application. */
public final class ProjectTransactionSelfTest {
    private static int checks;
    public static void main(String[] args) throws Exception {
        if (args.length == 3 && args[0].equals("crash")) { crashChild(new File(args[1]), args[2]); return; }
        Map<String, Object> typed = new HashMap<>();
        typed.put("description", "Комната 12"); typed.put("stage", 2); typed.put("time", Long.MAX_VALUE);
        typed.put("automatic", true); typed.put("scale", 1.25f); typed.put("names", new HashSet<>(Arrays.asList("a", "б")));
        check(ProjectTransaction.decode(ProjectTransaction.encode(typed)).equals(typed), "preference types survive durable round trip");
        for (String phase : new String[]{"prepared", "file", "journal", "published", "committed"}) {
            File directory = Files.createTempDirectory("streletz-transaction-crash-").toFile();
            String java = new File(System.getProperty("java.home"), "bin/java.exe").isFile()
                    ? new File(System.getProperty("java.home"), "bin/java.exe").getPath()
                    : new File(System.getProperty("java.home"), "bin/java").getPath();
            Process child = new ProcessBuilder(java, "-cp", System.getProperty("java.class.path"),
                    ProjectTransactionSelfTest.class.getName(), "crash", directory.getPath(), phase).inheritIO().start();
            check(child.waitFor() == 86, "child really terminated at " + phase);
            File target = new File(directory, "current-project.json");
            check(ProjectTransaction.journalFile(target).isFile(), "durable marker remains after " + phase);
            ProjectTransaction.recover(target, (journal, bridge) -> persistSettings(directory, journal, bridge));
            boolean committed = phase.equals("committed");
            check(ProjectTransfer.read(target).equals(committed ? "NEW" : "OLD"), "correct project after " + phase);
            check(ProjectTransfer.read(new File(directory, "journal.json")).contains(committed ? "new-value" : "old-value"), "correct journal after " + phase);
            check(ProjectTransfer.read(new File(directory, "bridge.json")).contains(committed ? "new-value" : "old-value"), "correct bridge settings after " + phase);
            check(!ProjectTransaction.journalFile(target).exists(), "recovery completes after " + phase);
            check(!ProjectTransaction.recover(target, (a, b) -> { throw new AssertionError("replayed recovery"); }), "recovery is idempotent");
        }
        File directory = Files.createTempDirectory("streletz-transaction-errors-").toFile();
        File target = new File(directory, "current-project.json");
        ProjectTransfer.write(target, "OLD");
        try {
            ProjectTransaction.install(target, "NEW", values("old-value"), values("old-value"),
                    () -> { throw new IOException("publish failed"); }, (a, b) -> { throw new IOException("restore failed"); });
            throw new AssertionError("expected rollback failure");
        } catch (ProjectTransfer.InstallException expected) { check(!expected.rollbackSucceeded, "rollback failure is explicit"); }
        check(ProjectTransaction.journalFile(target).exists(), "failed rollback retains recovery evidence");
        try {
            ProjectTransaction.recover(target, (a, b) -> persistSettings(directory, a, b),
                    point -> { if (point.equals("restored_file")) throw new IOException("recovery interrupted"); });
            throw new AssertionError("expected recovery interruption");
        } catch (IOException expected) { checks++; }
        ProjectTransaction.recover(target, (a, b) -> persistSettings(directory, a, b));
        check(ProjectTransfer.read(target).equals("OLD"), "recovery can itself be retried after interruption");
        File first = new File(directory, "first-project.json");
        try {
            ProjectTransaction.install(first, "NEW", values("old-value"), values("old-value"),
                    () -> { throw new IOException("first publication failed"); }, (a, b) -> { });
        } catch (ProjectTransfer.InstallException expected) { check(expected.rollbackSucceeded && !first.exists(), "first import restores built-in project absence"); }
        ProjectTransfer.write(ProjectTransaction.journalFile(target), "{\"version\":1,\"phase\":\"bad\"}");
        try { ProjectTransaction.recover(target, (a, b) -> { throw new AssertionError("corrupt journal applied"); }); throw new AssertionError("corrupt marker accepted"); }
        catch (IOException expected) { check(ProjectTransfer.read(target).equals("OLD"), "corrupt marker fails closed without changing project"); }
        System.out.println("ProjectTransactionSelfTest: " + checks + " checks passed, including five terminated child JVMs");
    }

    private static void crashChild(File directory, String phase) throws Exception {
        File target = new File(directory, "current-project.json");
        ProjectTransfer.write(target, "OLD");
        persistSettings(directory, values("old-value"), values("old-value"));
        ProjectTransaction.install(target, "NEW", values("old-value"), values("old-value"), () -> {
            ProjectTransfer.write(new File(directory, "journal.json"), ProjectTransaction.encode(values("new-value")).toString());
            if (phase.equals("journal")) Runtime.getRuntime().halt(86);
            ProjectTransfer.write(new File(directory, "bridge.json"), ProjectTransaction.encode(values("new-value")).toString());
        }, (a, b) -> persistSettings(directory, a, b), point -> { if (point.equals(phase)) Runtime.getRuntime().halt(86); });
        throw new AssertionError("crash checkpoint was not reached");
    }
    private static Map<String, Object> values(String value) { Map<String, Object> result = new HashMap<>(); result.put("value", value); return result; }
    private static void persistSettings(File directory, Map<String, Object> journal, Map<String, Object> bridge) throws Exception {
        ProjectTransfer.write(new File(directory, "journal.json"), ProjectTransaction.encode(journal).toString());
        ProjectTransfer.write(new File(directory, "bridge.json"), ProjectTransaction.encode(bridge).toString());
    }
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); checks++; }
}
