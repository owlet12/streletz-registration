# Независимая проверка XML-корней ресурсов BFX

Runner использует внешние DLL установленного Стрелец-Мастера и штатный
`XmlSerializer(Type)`, который используется в `StructureTreeResource.WriteXml`.
Он проверяет 11 типов: сегмент, МК-IP, семь контроллеров и RR/RRI.
Имена XML-корней не зашиты в fixture: они получаются из сериализации `null`
с реальными типами производителя и сверяются с `XmlReflectionImporter`.

Экземпляры исследуемых классов конфигурации и устройств не создаются; их
конструкторы и getters не вызываются. XML metadata и атрибуты типов читает
штатный сериализатор.
Runner не запускает Master, формы, плагин, native pipe, адаптеры или оборудование.
Проверка отражает XML-mapping типов, а не полную сериализацию экземпляра проекта.
Сами vendor DLL в исходники и комплект не входят.

## Сборка и проверка

Нужны .NET SDK для сборки и .NET Framework 4.8 в Windows либо Mono для запуска.

```powershell
dotnet build probe.csproj -c Release
.\bin\Release\net48\BfxResourceRootProbe.exe "C:\Program Files (x86)\Streletz-Integral\Streletz-Master"
```

Дополнительно можно проверить реальный parser собранного Windows-моста.
Assembly загружается только для вызова `BfxPanelCatalog.Read`; её `Main` и UI
не запускаются. Fixture содержит семь панелей, вложенные RR/RRI, одинаковые
имена, синтетический маршрут и маркер, который не должен попасть в экспорт.
Проверяются состав записей, точная принадлежность, маршрут, сохранение журнала
и запрет регистрации для offline-каталога.

```powershell
.\bin\Release\net48\BfxResourceRootProbe.exe "C:\Program Files (x86)\Streletz-Integral\Streletz-Master" "C:\Streletz\Windows\StreletzBridge.exe" ".\probe-output"
```

Для воспроизведения дефекта выпуска 0.5.3 используется отдельный процесс
с прежним неизменённым EXE, ожидаемым результатом `0` и искусственным контрольным
fixture, в котором все payload принудительно названы `Info`:

```powershell
.\bin\Release\net48\BfxResourceRootProbe.exe "C:\Program Files (x86)\Streletz-Integral\Streletz-Master" "C:\Streletz-0.5.3\Windows\StreletzBridge.exe" ".\probe-old" 0 legacy-control
```

Ожидается 0 панелей для корней из vendor metadata и 7 для искусственного
`all-Info` fixture. Именно такое искусственное допущение маскировало дефект
в первоначальных тестах. Не загружайте старую и новую bridge assembly
в один процесс: .NET может объединить одинаковые assembly identities.

В журнал выводятся hashes входных DLL и EXE для воспроизводимости. Отсутствие
ошибок в этом runner не подтверждает работу Windows GUI, native pipe или
конкретного пользовательского BFX на оборудовании.
