// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Comparator;
import java.util.stream.Stream;

/** Only the downloaded project/cache is disposable. Operation identities and the work journal survive. */
final class FreshProjectSession {
    static final String EMPTY_FINGERPRINT = "0000000000000000000000000000000000000000000000000000000000000000";
    private boolean active;

    synchronized boolean begin(boolean restoringActivity) {
        boolean fresh = !active || !restoringActivity;
        active = true;
        return fresh;
    }

    synchronized void end() { active = false; }

    static boolean cachedPreference(String key) {
        return key.equals("project_source") || key.startsWith("sync_")
                || key.startsWith("project-panels:") || key.startsWith("view-state:")
                || key.startsWith("panel-filter:");
    }

    // Call only on the serialized project-storage worker, after ProjectTransaction.recover.
    static void clearFiles(File directory) throws IOException {
        Path root = directory.toPath().toAbsolutePath().normalize();
        if (Files.exists(root.resolve("current-project.json.transaction")))
            throw new IOException("Сначала необходимо восстановить незавершённое сохранение проекта.");
        for (String name : new String[]{"current-project.json", "current-project.json.pending",
                "current-project.json.transaction.pending"}) Files.deleteIfExists(root.resolve(name));
        Path projects = root.resolve("projects");
        if (!Files.exists(projects, java.nio.file.LinkOption.NOFOLLOW_LINKS)) return;
        // walk does not follow symbolic links. Nothing outside the app's project cache is traversed.
        try (Stream<Path> paths = Files.walk(projects)) {
            for (Path path : (Iterable<Path>) paths.sorted(Comparator.reverseOrder())::iterator)
                Files.delete(path);
        }
    }
}
