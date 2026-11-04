// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import org.json.*;
final class ConnectionCheck {
    static String describe(JSONObject r,String fingerprint,String panelId) throws Exception {
        StringBuilder b=new StringBuilder("Windows-мост: защищённый ответ получен.\n\n");
        if(!r.optBoolean("ok")){String code=r.optString("errorCode",r.optString("code"));
            b.append("Проект / Стрелец-Мастер / панель: не проверены.\n");
            b.append("project_mismatch".equals(code)?"Откройте одинаковый проект на телефоне и в Windows.":"project_missing".equals(code)?"Откройте проект в Windows-мосте.":"Обновите Windows-мост до 0.5.15 и проверьте открытый проект.");return b.toString();}
        boolean same=fingerprint.equals(r.optString("projectFingerprint"));
        b.append("Проект: ").append(same?"совпадает.":"другой. Откройте одинаковый проект.").append("\n\n");
        boolean live="native_project".equals(r.optString("catalogSource"))&&r.optBoolean("nativeAvailable",false);
        b.append("Стрелец-Мастер: ").append(live?"живой каталог получен.":"связь не подтверждена. Откройте Master с дополнением и нужным проектом.").append("\n\n");
        if(!live)b.append(nativeHint(r.optString("connectionErrorCode"))).append("\n\n");
        JSONArray panels=r.optJSONArray("panels");boolean found=false;
        if(panels!=null)for(int i=0;i<panels.length();i++){JSONObject p=panels.getJSONObject(i);String id=p.optString("projectPanelId",p.optString("panelId"));if(!panelId.isEmpty()&&!panelId.equalsIgnoreCase(id))continue;found=true;
            b.append("Панель ").append(p.optString("name",id)).append(": ")
             .append(same&&live&&p.optBoolean("available")?"маршрут и принадлежность подтверждены Master.":"маршрут не подтверждён. Проверьте настройки панели и принадлежность устройств в Master.").append('\n');}
        if(!found)b.append("Панель: не найдена. Выберите её в фильтре или обновите проект.\n");
        return b.append("\nФизическая связь с контроллером: не проверена. Каталог Master подтверждает маршрут, но не ответ контроллера. Команды регистрации не отправлялись.").toString();
    }

    static String failure(Exception error) {
        String hint=error instanceof java.net.UnknownHostException?"Не удалось определить адрес компьютера. Проверьте имя или укажите IP.":
            error instanceof java.net.ConnectException?"Компьютер отклонил подключение. Запустите обмен Windows-моста и проверьте порт.":
            error instanceof java.net.SocketTimeoutException?"Истекло время ожидания. Проверьте общую сеть, адрес компьютера и правила брандмауэра.":
            error instanceof java.security.GeneralSecurityException?"Ответ не прошёл проверку защиты. Сверьте код связи на телефоне и в мосте.":
            "Ответ отсутствует или имеет неверный формат. Проверьте адрес, порт, код связи и версию Windows-моста.";
        return "Windows-мост: связь не подтверждена.\n"+hint+"\n\nMaster, проект и панель: не проверены.";
    }
    private static String nativeHint(String code) {
        switch(code) {
            case "master_login_required":return "Выполните вход в проект в Стрелец-Мастере.";
            case "master_project_required":return "В Стрелец-Мастере не открыт проект. Откройте нужный BFX.";
            case "unsupported_master_version":case "unsupported_master_build":return "Версия Master не поддерживается установленным дополнением. Сверьте версии комплекта.";
            case "native_access_denied":return "Windows запретила доступ к модулю. Запустите мост и Master в одном сеансе под одной учётной записью.";
            case "native_pipe_connect_timeout":return "Локальный канал модуля не открылся вовремя. Проверьте загрузку дополнения и сеанс Windows.";
            case "native_response_timeout":case "native_transport_timeout":return "Модуль не ответил вовремя. Проверьте, не занято ли окно Master.";
            case "native_ui_busy":case "native_registration_busy":return "Master занят другой операцией. Дождитесь её окончания.";
            default:return "Подробности доступны в Windows-мосте: «Диагностика связи».";
        }
    }
}
