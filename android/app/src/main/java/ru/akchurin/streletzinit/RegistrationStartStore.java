// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.IOException;

/** Durable pre-dispatch barrier. No network or UI work is permitted here. */
final class RegistrationStartStore {
    interface Store {
        String readOperation();
        boolean writeOperation(String value);
        boolean writeRecord();
    }
    static void prepare(Store store,String expected,String operation,String failed) throws IOException {
        if(!expected.equals(store.readOperation())) throw new IOException("Операция изменилась. Обновите карточку.");
        if(!store.writeOperation(operation)) {
            store.writeOperation(expected);
            throw new IOException("Не удалось сохранить операцию. Регистрация не отправлена.");
        }
        if(!store.writeRecord()) {
            // If this write also fails, the unresolved durable operation blocks a fresh start.
            store.writeOperation(failed);
            throw new IOException("Не удалось сохранить запись. Регистрация не отправлена.");
        }
    }
}
