// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import java.io.IOException;
import java.util.*;

/** One preferences-file commit and compensating rollback; null values mean absent keys. */
final class DurablePreferenceChange {
    interface Store { Object get(String key); boolean write(Map<String,Object> changes); }
    static final class Failure extends IOException {
        final boolean rollbackSucceeded;
        Failure(boolean restored){super("Не удалось надёжно сохранить изменения.");rollbackSucceeded=restored;}
    }
    static void commit(Store store,Map<String,Object> expected,Map<String,Object> changes) throws IOException {
        if(!expected.keySet().equals(changes.keySet()))throw new IllegalArgumentException("Missing expected keys");
        for(String key:expected.keySet())if(!Objects.equals(expected.get(key),store.get(key)))
            throw new IOException("Запись изменилась. Повторите действие после обновления карточки.");
        if(!store.write(changes))throw new Failure(store.write(expected));
    }
}
