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
- **Секреты никогда не выводить** (ключи подписи, пароли, токены, PIN): только внутри shell, не в ответ,
  не в файлы репо, не в память. Ключ подписи Android (`_Secrets/` Android-проекта) сюда не копировать.
- Чужие данные (подписки пользователя, настройки) — не стирать без отдельного явного вопроса.

## Окружение (Ubuntu-сервер, без sudo)
- .NET 10 SDK: `export DOTNET_ROOT=~/.local/opt/dotnet PATH=~/.local/opt/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
  WPF собирается здесь (`EnableWindowsTargeting`), MSI (WiX) — только на Windows.
- JDK 21 (только для Android-справочника `app/`): `export JAVA_HOME=~/.local/opt/jdk-21 PATH=~/.local/opt/jdk-21/bin:$PATH`.
- `dl.google.com` по IPv6 → 404: загрузки `curl -4`; Gradle уже с `preferIPv4Stack=true`.
- `gh` (`~/.local/opt/gh/bin`) авторизован как `pico-soft`; `origin` = `pico-soft/X-Gates-Win` (закрытый). Экрана нет: UI/упаковка MSI — Windows (см. `HANDOFF.md` §10).
  У токена `gh` НЕТ права `workflow`: файлы `.github/workflows/*` GitHub не принимает (выдать — `gh auth refresh -s
  workflow`, решение Elyor).
- Структура: `src/XGates.Core` (порт Kotlin-ядра), `src/XGates.App` (логика экранов без WPF — тестируется здесь),
  `src/XGates.Win` (WPF: только отрисовка и события Windows), `tests/XGates.Core.Tests` (прогон ПОСЛЕДОВАТЕЛЬНО —
  общие синглтоны). Ядро для тестов: `tools/xray/fetch-xray.sh`.
- **Непроверенное на Windows** (окно, трей, MSI, workflow) — в ветке `wip/windows-shell`, не в main. В main —
  только проверенное тестами; после проверки на Windows разложить wip по коммитам в main.
- Телефоны (для Android-сверок) — только через `~/.local/bin/phone-adb`; нет связи → попросить Elyor
  запустить «синхронизатор» на Mac.
