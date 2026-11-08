# Проверка

## После ревью, 8 ноября 2026

Сборка `FeatureRollout.slnx` в Release завершилась без предупреждений и ошибок. Все 15 тестов прошли на отдельной PostgreSQL 18.6. Добавлены проверки восстановления после исключения в диагностике, тайм-аута зависшего тела ответа по управляемым часам и валидации ID. Тест завершения с активным HTTP-запросом также использует управляемые часы.

```powershell
./.local/dotnet.exe restore
./.local/dotnet.exe build -c Release --no-restore
$env:FEATURE_ROLLOUT_TEST_POSTGRES = 'Host=127.0.0.1;Port=55439;Username=flagcontrol;Database=postgres'
./.local/dotnet.exe test -c Release --no-build
./.local/dotnet.exe format --verify-no-changes --no-restore
```

Собран пакет `FeatureRollout.Sdk.0.1.0.nupkg`. Демо восстановлено из локального NuGet, собрано и проверено по [инструкции](demo.md). При 10% включено 10 094 из 100 000 пользователей, при 20% — 20 081, из первой группы никто не выбыл. После остановки API активный клиент сохранил ревизию 2, новый процесс вернулся к fallback. Четыре эталона bucket совпали в восьми процессах. Форматирование решения и демо прошло.

## Первый прогон, до переименования

Среда локального прогона — Windows 11 x64, Intel Core i7-8750H, .NET SDK 10.0.100, PostgreSQL 18.6. SDK запускался из `.local/dotnet.exe`. Тесты подключались к отдельному экземпляру PostgreSQL на порту 55439, созданному для этого прогона.

```powershell
dotnet restore
dotnet build -c Release --no-restore
$env:FLAGCONTROL_TEST_POSTGRES = 'Host=127.0.0.1;Port=55439;Username=flagcontrol;Database=postgres'
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
dotnet pack src/FlagControl.Sdk -c Release -o artifacts/packages
dotnet restore demo/FlagControl.Demo --source ./artifacts/packages --packages ./artifacts/demo-cache
dotnet build demo/FlagControl.Demo -c Release --no-restore
dotnet format demo/FlagControl.Demo --verify-no-changes --no-restore
pwsh -File demo/check-buckets.ps1
docker-compose -f compose.yaml config --quiet
```

- Сборки решения и потребителя NuGet завершились без предупреждений и ошибок. Проверки форматирования прошли.
- Все 12 тестов прошли, включая 10 конкурирующих HTTP-публикаций, откат после ошибки PostgreSQL, правила, распределение и обновление SDK во время чтения.
- Четыре эталонных bucket совпали в восьми независимых процессах потребителя. Тестовый набор дополнительно проверяет пустой ID.
- Прогон [демо](demo.md) использовал локальный API и потребителя из `FlagControl.Sdk.0.1.0.nupkg`. При остановке API старый процесс сохранил ревизию 2, новый вернулся к fallback.
- Docker Compose проверен на уровне конфигурации. Docker daemon был недоступен, поэтому контейнерный запуск Compose и Testcontainers в этой среде не выполнялся. Шаблон GitHub Actions локально не запускался.

## Замер демо

Команда `dotnet demo/FlagControl.Demo/bin/Release/net10.0/FlagControl.Demo.dll http://127.0.0.1:55080/` в указанной выше среде. Для каждого снимка один проход по ID от `user-000000` до `user-099999`. Таймер охватывает только цикл локальной оценки с проверкой ревизии. Время загрузки снимка, подсчёта группы и вывода результата в замер не входит.

| Rollout | Включено из 100 000 | Выбыло из предыдущей группы | Время прохода, мс |
| ---: | ---: | ---: | ---: |
| 10% | 10 094 | — | 92,73 |
| 20% | 20 081 | 0 | 91,73 |

Это разовый замер для демонстрационного набора без отдельного прогрева. В тестах допуск задан заранее и составляет ±0,5 процентного пункта.
