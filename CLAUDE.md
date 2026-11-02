# X-Gates-Win — правила для Claude Code («КК»)

Порт Android-приложения X-Gates на Windows 10. **Сначала прочитай `HANDOFF.md` целиком**, затем
`UPSTREAM-SYNC.md` и **`docs/decisions.md`** (принятые решения: стек C#/.NET 10 + WPF, а не Compose из HANDOFF §5).
Этот репозиторий — git-форк Android-проекта (Android-код в `app/` — справочник).

## Общение
- Владелец — Elyor. Пишет по-русски → отвечать по-русски. Elyor называет Claude Code «КК».
- Вопросы — только когда решение действительно за Elyor (стек, репозиторий, релиз, деньги, данные).

## Жёсткие рамки продукта
- Только **свой локальный прокси** (SOCKS5 127.0.0.1:10815, HTTP 127.0.0.1:10816). **Системный прокси
  Windows не трогать** (WinINET/WinHTTP/реестр Internet Settings/PAC/netsh), никаких TUN/драйверов/WFP.
- Функционально и визуально — как Android-версия (паритет по `HANDOFF.md` §3).
- Не менять системные настройки машины Elyor сверх того, что он явно разрешил (автозапуск — только
  по опции в приложении).

## Порядок работы (перенят из Android-проекта)
1. Код → сборка → **проверка фактом** (тест/запуск; UI — на Windows) → **коммит сам, без спроса** → пуш (бэкап),
   когда у форка будет свой `origin`.
2. **Не коммитить**: не проверено фактом / эффект «по построению» / работа не целая — доложить и ждать.
3. **Публикация** (тег, GitHub-релиз, раздача установщика) — **только по явному слову Elyor**.
4. Коммиты: по-русски, conventional-префиксы (`feat(…)`, `fix(…)`, `build:`…), ОДНО изменение на коммит,
   автор PICO-SOFT (уже в `git config`), в теле — **что проверено фактом, что нет**; окончание
   `Co-Authored-By` по системной подсказке. Force-push и переписывание опубликованной истории запрещены.
5. **Память = журнал решений**: фиксировать «почему так» и «что пробовали и отвергли», в момент коммита.

## Безопасность
- **Push в Android-репозитории отключён** (`upstream`, `upstream-github` → `DISABLED-push…`). Не включать.
  Свой `origin` (GitHub) создаётся только с согласия Elyor.
- **Здесь работаем только с Windows-версией** (Elyor 2026-10-31). Всё, что нужно сделать в Android-версии, —
  только **инструкции для Android-КК** (что и почему, где в коде, как проверить на телефоне) в хэндоффе
  (`docs/android-handoff/` + копия в Android-папке). Kotlin-патчи, Android-тесты и сборку APK здесь не делаем.
- **Сверка с Android (Elyor 2026-11-02):** папку `~/projects/XrayProxyDroid` читать можно и нужно: важна синхронизация функциональности и поиск
  логических ошибок в обеих версиях. Писать туда — только сообщения-хэндоффы с пометкой, что их написал Windows-КК.
- **Всегда читать `HANDOFF-FROM-ANDROID.md` в корне проекта** (Elyor 2026-11-02): его пишет Android-КК. Читать в начале работы и перед выпуском,
  утверждения проверять по коду и запуском. Файл не коммитить: репозиторий публичный, `git add` только явными путями.
- **Секреты никогда не выводить** (ключи подписи, пароли, токены, PIN): только внутри shell, не в ответ,
  не в файлы репо, не в память. Ключ подписи Android (`_Secrets/` Android-проекта) сюда не копировать.
- Чужие данные (подписки пользователя, настройки) — не стирать без отдельного явного вопроса.

## Окружение (Ubuntu-сервер, без sudo)
- .NET 10 SDK: `export DOTNET_ROOT=~/.local/opt/dotnet PATH=~/.local/opt/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
  WPF собирается здесь (`EnableWindowsTargeting`), MSI (WiX) — только на Windows.
- JDK 21 (только для Android-справочника `app/`): `export JAVA_HOME=~/.local/opt/jdk-21 PATH=~/.local/opt/jdk-21/bin:$PATH`.
- `dl.google.com` по IPv6 → 404: загрузки `curl -4`; Gradle уже с `preferIPv4Stack=true`.
- `gh` (`~/.local/opt/gh/bin`) авторизован как `pico-soft`, право `workflow` есть; `origin` = `pico-soft/X-Gates-Win` (публичный).
  Экрана нет: окно и MSI проверяются автопрогоном `.github/workflows/windows.yml` и на ноутбуке Elyor.
- Структура: `src/XGates.Core` (порт Kotlin-ядра), `src/XGates.App` (логика экранов без WPF — тестируется здесь),
  `src/XGates.Win` (WPF: только отрисовка и события Windows), `tests/XGates.Core.Tests` (прогон ПОСЛЕДОВАТЕЛЬНО —
  общие синглтоны). Ядро для тестов: `tools/xray/fetch-xray.sh`.
- **Непроверенное на Windows** — в отдельной ветке `wip/…`, не в main. В main — только проверенное; после проверки
  разложить wip по коммитам в main.
- **Выпуск** (только по слову Elyor): прочитать `HANDOFF-FROM-ANDROID.md` → в wip-ветке поднять `Version`, `VersionCode`, `VersionNameDisplay` в `Directory.Build.props`, коммит
  `release: vX.Y beta (versionCode N)` → `gh workflow run windows.yml --ref <ветка>` → только при полностью зелёном прогоне: fast-forward `main` на этот коммит, MSI из артефакта `windows-check` этого же прогона,
  `python3 tools/release/make-update.py --msi <msi> --notes-file <notes> --out <папка>` (подпись здесь), `gh release create vX.Y.0-beta --target <sha> --title "X-Gates Win X.Y beta" --latest --notes-file <body> <msi> update.json update.json.sig`.
  Затем проверить без токена: `releases/latest`, скачать файлы, sha256, настоящий `UpdateChecker` одноразовым тестом с `currentVersionCode` (N-1 Available, N UpToDate), тест удалить. Красная сборка выпуска не публикуется.
  Релизы с «beta» в теге и названии, но без флага GitHub pre-release: обновлялка читает `releases/latest` (`docs/decisions.md`, 2026-11-01).
- **Тесты на этом сервере:** коммит только по `if` от вывода тестов. Упавший тест может оставить живой `third_party/xray/linux-64/xray run -c stdin:`: он держит 10815 и валит соседние тесты (проверка `ss -ltnp`). Системный `/usr/local/bin/xray` не трогать.
- Телефоны (для Android-сверок) — только через `~/.local/bin/phone-adb`; нет связи → попросить Elyor
  запустить «синхронизатор» на Mac.
