# Демо за несколько минут

Запустите API и опубликуйте 10% по инструкции в [README](../README.md). Для первого прогона используйте пустую БД. При повторном прогоне возьмите текущую ревизию из `GET /v1/config/demo` и передайте её скрипту публикации.

В отдельном терминале соберите NuGet и восстановите демо только из локального источника.

```powershell
dotnet pack src/FeatureRollout.Sdk -c Release -o artifacts/packages
dotnet restore demo/FeatureRollout.Demo --source ./artifacts/packages --packages ./artifacts/demo-cache
dotnet build demo/FeatureRollout.Demo -c Release --no-restore
dotnet run --project demo/FeatureRollout.Demo -c Release --no-build -- http://localhost:5080/
```

Если пересобираете SDK с той же версией, удалите `artifacts/demo-cache` перед восстановлением демо, чтобы взять свежий пакет.

После загрузки появится строка `revision=1 preview=express legacy=classic`. У первого пользователя override включает флаг, у второго выключает. Демо также один раз для каждой увиденной ревизии прогоняет 100 000 синтетических ID и печатает размер группы и время оценки.

Опубликуйте 20%, пока демо работает.

```powershell
$env:Admin__Token = 'flagcontrol-demo-token'
./demo/publish.ps1 -Percent 20 -ExpectedRevision 1
```

Дождитесь `revision=2`. Размер группы вырастет с 10 094 до 20 081, `removed=0` означает, что ни один пользователь из первой группы не выпал. Два override дают те же варианты оформления.

Остановите API через Ctrl+C. Демо пишет сообщение о сбое загрузки в stderr и продолжает выводить ревизию 2. Если остановить и запустить само демо при недоступном API, оно покажет `revision=fallback preview=classic legacy=classic`.

Независимая проверка bucket запускает восемь процессов демо и сравнивает вывод с эталонами.

```powershell
pwsh -File demo/check-buckets.ps1
```
