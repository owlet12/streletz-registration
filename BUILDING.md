# Сборка

Автор проекта: [owlet](https://github.com/owlet12).

## Android

Нужны JDK 17, Gradle 9.6.0, Android SDK 37 и Build Tools 36.0.0.
В `android/local.properties` задайте `sdk.dir` своего Android SDK.
Из каталога `android` выполните `gradle assembleDebug assembleRelease`.
Release APK требует подписи собственным ключом; ключ исходного выпуска
не распространяется. Подпись другим ключом не позволяет обновить установленный
официальный для этого проекта APK поверх него.

JVM-проверки расположены в `android/tests/*SelfTest.java` и используют
собранные debug classes, android.jar, ZXing 3.5.4 и JVM-реализацию org.json.

Проверки печати: `node android/tests/label-print.test.cjs` и
`node android/tests/wp9529.test.cjs`.
Для UI-проверки установите Playwright и его Chromium, затем выполните
`node android/tests/label-ui.test.cjs` и `node android/tests/wp9529-ui.test.cjs`.
Повторите `wp9529.test.cjs` и `wp9529-ui.test.cjs` с переменной окружения
`PRINTER_PROFILE=wp9539` для профиля WP9539. Без переменной проверяется
WP9529. Общие сценарии проверяют оба профиля без дублирования тестов.
Если используется установленный Chrome,
задайте полный путь к нему в переменной окружения `CHROME_PATH`.
`node android/tests/font-ui.test.cjs` проверяет размер шрифта, сохранение,
защиту от обрезки и соответствие предпросмотра передаваемому растру.
`node android/tests/print-flow.test.cjs` проверяет временный текст без сохранения,
печать одной кнопкой, повторное подключение, смену принтера удержанием,
отмену и запрет повтора после обрыва. Выбор принтера Android также имитирован.
UI-проверка имитирует Bluetooth и не подтверждает работу физического принтера.

## Windows

Нужны Windows, .NET SDK 8 и .NET Framework 4.8.

```powershell
dotnet build windows-bridge/build.csproj -c Release
dotnet run --project windows-bridge/core-tests.csproj -c Release
dotnet build master-plugin/Plugin.StreletzRegistration.csproj -c Release '-p:MasterInstallDir=C:/Program Files/Streletz-Integral/Streletz-Master'
```

Укажите свой путь к Master 12.4. Его DLL не входят в репозиторий и не должны
копироваться в публичную сборку. Модуль проверяет совместимость библиотек.
Диагностические утилиты требуют отдельных входных данных; рабочие проекты
объектов в репозиторий не включены. Не запускайте утилиты с боевым проектом
без изучения их назначения.

## Проверки планов 0.5.27

`FloorPlanSelfTest.java` проверяет преобразование координат, центрирование,
масштаб, поиск планов устройства и перенос отметок. Для проверки данных из
Windows задайте `STRELETZ_PLAN_TEST_OUTPUT` — путь к файлу фикстуры. Сначала
запустите Windows `core-tests.csproj` с этой переменной, затем JVM-тест:
Windows создаст синтетический проект, Android прочитает его полевые данные.

`dotnet build windows-bridge/plan-image-tests.csproj -c Release` собирает
отдельный тест PNG/JPEG и окна редактора под .NET Framework 4.8. Запустите
`StreletzBridge.PlanImageTests.exe <папка-проверки>`: он создаст тестовый план,
переносимый проект и снимок окна. Master и мост при этом не запускаются.

## Расстановка в мосте (0.5.28)

Соберите `windows-bridge/build.csproj`, `core-tests.csproj` и `plan-image-tests.csproj` в Release. Последний запускает только собственное тестовое окно за пределами экрана, сохраняет снимки в переданный каталог, проверяет мышь, поиск, фильтры, отмену и сохранение. Установленный мост не запускается. `PlanPlacement`/`PlanViewport` покрыты основными тестами; `FloorPlanSelfTest` проверяет приём расстановки Android, обновления после перезапуска и конфликты. Для чтения точного Windows-снимка передайте его путь в `STRELETZ_PLAN_TEST_OUTPUT` обоим наборам тестов.
