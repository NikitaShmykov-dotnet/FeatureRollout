# FeatureRollout

Сервис хранит опубликованные конфигурации feature flags в PostgreSQL. SDK периодически забирает снимок окружения и решает локально, включён ли флаг для пользователя. В демо флаг `checkout` выбирает обычное или ускоренное оформление заказа.

## Запуск

Нужны .NET SDK 10 (минимум 10.0.100), Docker с Compose и PowerShell 7 для демо. Команды ниже выполняются из каталога `FlagControl`.

```powershell
docker compose up -d --wait
$env:ConnectionStrings__Flags = 'Host=localhost;Port=55432;Database=flagcontrol;Username=flagcontrol;Password=flagcontrol-local'
$env:Admin__Token = 'flagcontrol-demo-token'
dotnet run --project src/FeatureRollout.Api -- --urls http://localhost:5080
```

При старте API применяет начальную SQL-миграцию. С теми же переменными окружения проект API можно запустить из IDE. Состояние базы проверяет `GET /health`, документ OpenAPI доступен по `GET /openapi/v1.json`.

Во втором терминале создайте окружение `demo` и опубликуйте первый снимок.

```powershell
$env:Admin__Token = 'flagcontrol-demo-token'
./demo/publish.ps1 -Percent 10 -ExpectedRevision 0
Invoke-RestMethod http://localhost:5080/v1/config/demo
```

API принимает полную карту флагов и ожидаемую ревизию. `0` означает, что окружение создано, но ещё ни разу не публиковалось. При конфликте API возвращает `409`. История отдаёт до 50 записей от новых к старым, параметр `before` принимает последнюю ревизию с предыдущей страницы. Запросы к истории и отдельным ревизиям есть в [requests.http](requests.http).

## Локальная оценка

Для известного флага SDK последовательно проверяет override по ID, первое совпавшее правило, попадание в процентную группу и значение `default`. Override и правило могут включить или выключить флаг. Процент включает пользователей, попавших в группу. Остальные получают `default`, поэтому для включения ровно заданной доли установите `default: false`. Имена флагов, ID и значения атрибутов сравниваются с учётом регистра. Для поиска ключа атрибута SDK использует comparer словаря, переданного в `FlagUser`.

```csharp
using FeatureRollout;

using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5080/") };
await using var flags = new FlagClient(http, "demo",
    refreshInterval: TimeSpan.FromSeconds(2), requestTimeout: TimeSpan.FromSeconds(1));

bool express = flags.Evaluate("checkout", new FlagUser("preview-user"), fallback: false);
```

До первой успешной загрузки и для неизвестного ключа SDK возвращает `fallback`. Во время сбоя SDK продолжает использовать последний принятый снимок. Он хранится в памяти процесса, поэтому после перезапуска без API снова действует `fallback`. `FlagUser` требует непустой ID без непарных UTF-16-суррогатов.

HTTP-клиент принадлежит вызывающему коду. Освобождайте его после `FlagClient`, чтобы SDK успел отменить запрос и дождаться завершения фонового обновления. Необязательный `reportFailure` выполняется синхронно. Его исключения игнорируются, чтобы следующие попытки обновления продолжались. Callback должен быстро возвращать управление.

`/v1/config/{environment}` доступен без токена. Любой клиент с сетевым доступом к API получает ключи, правила и список ID из overrides. SDK не отправляет профили пользователей на сервер. Для демо используйте синтетические ID, для закрытых конфигураций ограничьте сетевой доступ. Для управления используется один демонстрационный Bearer-токен с именем оператора из `Admin__Actor` (по умолчанию `demo-admin`). Токен из примера предназначен для локального запуска.

## Проверки

```powershell
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

Интеграционные тесты по умолчанию запускают PostgreSQL через Testcontainers. Для локальной PostgreSQL можно задать `FEATURE_ROLLOUT_TEST_POSTGRES` с правом создания схем в выделенной тестовой базе. Тесты создают и удаляют собственную схему.

[Демо из локального NuGet](docs/demo.md) · [Сценарии отказов](docs/failure-scenarios.md) · [Результаты проверок](docs/verification.md)

Чтобы включить CI, скопируйте [ci/feature-rollout.yml](ci/feature-rollout.yml) в `.github/workflows/feature-rollout.yml` в корне общей папки. Шаблон ожидает, что проект находится в корневом подкаталоге `FlagControl`.
