# Sea Power Tactical UI Dev (dev-only)

> TL;DR: Отдельный BepInEx-плагин для разработки (ADR-0046, T-139). Открывает тестовый канал `sptui-dev/1` на 127.0.0.1 для MCP `voicebot-dev` (`ui_state`, `ui_selfcheck`, `ui_log`, `ui_command`). Не для раздачи: в пакет Workshop не входит, по умолчанию выключен.

## Установка
- Сборка: `plugins/sea_power/bridge/build.sh` (результат `out-dev/SeaPowerTacticalUI.Dev.dll`).
- Установка (только при закрытой игре): `plugins/sea_power/bridge/install.sh dev`; удаление: `install.sh dev --uninstall`. В `install.sh all` dev не входит.
- Конфиг `BepInEx/config/ru.voicebot.seapower.ui.dev.cfg`, секция `[Dev]`:
  - `Enabled` (false; установщик ставит true);
  - `FireCommands` (true): команды `attack`, `smart_attack`, `asw_attack`, `set_roe` — только по враждебным целям, каждая строка в журнале `[Dev][FIRE]`;
  - `Port` (47390; перебор до +4);
  - `StartDelayS` (20), `StartQuietS` (5), `StartMaxDelayS` (180) — отложенный старт канала (T-282, зависание первого запуска): сокет и потоки появляются не раньше `StartDelayS` с запуска игры и после загрузки сцены главного меню или миссии (buildIndex 1 или 2; 0 — загрузочная сцена; T-285) и через `StartQuietS` без загрузки сцен, но не позже `StartMaxDelayS` (предел считается от запуска игры). Журнал: `dev gate: scene '…' #N loaded`. До этого клиенты видят `unavailable: no session file`. В журнале: `dev server start delayed …` при запуске, `dev server started: listening on … (after quiet|max_delay …s)` при открытии. `0/0/0` — открыть сразу. Accept — через `Poll` 250 мс, клиентские потоки постоянные (пул по числу клиентов).

## Протокол
- TCP 127.0.0.1, одна строка JSON на запрос, токен из `BepInEx/config/SeaPowerTacticalUI.Dev/session.json` в каждом запросе.
- Первая строка от сервера — `hello` (`groups`: read, control, fire).
- Чтение: слот `ru.voicebot.seapower.ui/diag` публичной DLL (только при установленном dev-моде). Команды: закрытая таблица перевода в `link` v1 (`DevCommands.cs`, эталон `tests/data/ui_dev_command_table.json`).
- До 2 клиентов одновременно (третий получает `busy`; ADR-0093), строка до 64 КБ, простой 2 мин — разрыв. Для опроса — одно долгое соединение: `tools/qa/ui_poll.py`, `ui_watch.py` (`UiModClient(keep_alive=True)`), MCP — короткие.

## QA-чит `air_ready` (ADR-0061)
- Обнуляет остаток подготовки (`task:<guid>`, `PendingLaunchTask`) и переподготовки после посадки (`cool:<guid>`, `CoolDownTask`) на **своих** палубах; игра завершает их на следующем тике. Ожидание экипажа/места на палубе не пропускается.
- Аргументы (ровно одна область): `{"all": true}` | `{"ship_uid": 7}` | `{"key": "task:…"}` (можно вместе с `ship_uid`). Ключи — в `ui_state sections=[deck]`.
- Исполняет сама dev-DLL (`DevAirReady.cs`, члены игры — `DevAirReadySpecs.cs`, все необязательные); в `link` не уходит. Ответ: `{changed, decks, tasks:[{ship_uid,key,type,state,duration_was,set}]}`; ошибки `unsupported` | `no_mission` | `unit_not_found` | `no_flight_deck` | `deck_task_not_found`. Каждый вызов — строка `[Dev][AIR]` в журнале.
- Пример (MCP): `ui_command air_ready {"all": true}`.

## Графика `gfx` (T-165, A/B рычагов производительности)
- `{"op":"dump"}` — камеры (имя, depth, enabled, targetTexture, cullingMask), MonoBehaviour главной камеры, Ceto Ocean (оверлеи) / PlanarReflection (reflectionMask, разрешение, размытие) / UnderWater (режим), QualitySettings (lodBias, pixelLightCount, softParticles, anisotropicFiltering, shadows, antiAliasing, vSyncCount), облака (`Clouds` активен, `ScreenSpaceCloudShadow`), число ParticleSystem, `Application.targetFrameRate`, список включённых рычагов.
- `{"op":"set","lever":L,"on":true|false}`; L: `rcm` (RenderColorMask off), `ssao` (AmplifyOcclusionEffect off), `bloom_lite` (glare/flare off, BloomDownsampleCount ≤3), `cloud` (`Clouds` SetActive(false) + ScreenSpaceCloudShadow off), `refl_mask` (reflectionMask=0), `ovl` (normal/foam overlay QUARTER), `lod` (lodBias 0.5), `plight` (pixelLightCount 0), `softp` (softParticles off), `particles` (maxParticles ×0.25 у систем, живых сейчас), `fps60` (targetFrameRate 60 + vSync 0, только для замера). Исходное значение запоминается при первой записи и возвращается при `on:false`; `{"op":"reset"}` откатывает всё.
- Ответ: `{lever, on, changes:[{member, was, now}], errors}`; нет ни одного члена — `unsupported` (detail — список). Строка `[Dev][GFX]` в журнале. Всё через рефлексию в dev-DLL (`DevGfx.cs`), в Workshop-DLL ничего. Частота рендера / render scale и PiP-камера — вне объёма (решение заказчика).
- Игра может перезаписать часть значений при смене настроек (`OptionsManager.ApplyOptions`) — тогда повторить `set`.

## Память `gc` (T-212)
- `ui_command gc {}` — на главном потоке `GC.Collect(); WaitForPendingFinalizers(); Collect()`; ответ `{live_mb, before_mb, ms}`. Пауза 0.1–1 с, вызывать только вручную (не из таймеров). Строка `[Dev][GC]` в журнале. Живой объём без мусора для сравнения между загрузками; затем `ui_state memory` → `lifecycle.prev_alive` (старые миссии, ещё живые после GC).
- `ui_state memory` теперь содержит `threads` (`mod[]` — потоки и пул-задачи мода с Linux TID, `hot[]` — самые горячие потоки процесса по `/proc`, поле `mod` не `null` = поток мода) и `lifecycle` (`generation`, `subscribers`, `prev_alive`, `prev_tracked`). Потоки самого dev-сервера (`sptui-dev-*`) ведут свой реестр и в `threads.mod` не попадают: в `hot` они видны по `comm` (`sptui-dev-…`) с `mod: null`.

## Офлайн-проверка
`devtest` (net8.0) собирает те же BCL-файлы: `dotnet devtest/bin/devtest.dll selftest`; режим `serve --dir D` поднимает настоящий сокет с фальшивой игрой.
