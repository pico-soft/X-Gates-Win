# X-Gates Win — журнал решений

Что решили, **почему** и **что пробовали и отвергли**. Дополнять в момент решения/коммита.
Открытые вопросы из `HANDOFF.md` §13 закрыты 2026-10-29 (ответы Elyor).

## 2026-10-29 — ответы на HANDOFF §13

### §13.1 Стек: C# / .NET 10 + WPF (а не Kotlin + Compose Desktop)
- Слово Elyor: «что нативнее для Win 10|11, то и используем».
- **WPF** (.NET 10, встроенная Fluent-тема) — нативный фреймворк Microsoft, без JVM. Проверено фактом на
  Linux-сервере: с `<EnableWindowsTargeting>true</EnableWindowsTargeting>` WPF-проект **собирается здесь**;
  `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` + сжатие → PE32+ GUI exe ≈62 МБ.
- **Отвергнут WinUI 3** (Windows App SDK 1.8): на Linux не собирается — его `XamlCompiler.exe` (net472) →
  `Exec format error`. Любая правка UI требовала бы Windows; выигрыша для этого приложения нет.
- **Отвергнут Kotlin + Compose Desktop** (рекомендация `HANDOFF.md` §5): JVM-рантайм, не нативный вид.
- **Цена (принята):** вся логика Android (~10 тыс. строк Kotlin без UI, UI 4.7 тыс., тесты) переписывается на C#;
  синхронизация с Android — только схема C из `UPSTREAM-SYNC.md` (перенос по смыслу).
  Чтобы перенос фиксов был механическим, **C#-код повторяет Kotlin 1:1**: те же имена классов, функций, полей и
  файлов; пакет `com.picosoft.xrayproxydroid.xray.link` → пространство имён `XGates.Core.Xray.Link`, файл
  `xray/link/VlessParser.kt` → `src/XGates.Core/Xray/Link/VlessParser.cs`. Ключевые комментарии («Пр.N», «почему»)
  переносятся вместе с кодом.

### §13.2 Windows для проверок — отдельный ноутбук Elyor (Windows 10)
- Доступ КК — как Phone Bridge у телефонов: ноутбук держит обратный SSH-туннель на сервер; sshd на ноутбуке
  слушает только `127.0.0.1` (наружу не открыт). GUI, запущенный из SSH-сеанса, на экране не виден → запуск окна и
  скриншоты — через задачу планировщика в интерактивном сеансе пользователя. Настраивает Elyor с ноутбука.

### §13.3 Репозиторий — отдельный `pico-soft/X-Gates-Win`
- Создан **закрытым** (видимость Elyor не называл → минимальная экспозиция). Сделать публичным перед первым релизом
  (обновления качаются из GitHub Releases без токена) — только по слову Elyor.
- История форка Android в нём — не утечка: `pico-soft/X-Gates` публичный.

### §13.4 Имя, иконка, данные
- Имя **«X-Gates Win»**, иконка та же (`art/xrayproxy-logo.png`).
- Данные — **`%LOCALAPPDATA%\X-Gates\`** (не `%APPDATA%` Roaming: замеры и «живые» серверы верны только для сети
  этой машины, в перемещаемом профиле они бы уезжали на другие компьютеры; отдельно от папки программы → обновление
  и удаление программы данные не трогают). Внутри — те же файлы/схемы, что в `filesDir` Android, + `logs\`.

### §13.5 Установщик — MSI per-user, без прав администратора
- WiX `Scope="perUser"` → `%LOCALAPPDATA%\Programs\X-Gates Win\`, без UAC.
- Факт: WiX на Linux не работает («The WiX Toolset only supports Windows») → MSI собирается на Windows
  (ноутбук или GitHub Actions `windows-latest`). WiX v7 к тому же требует принять OSMF EULA (платный взнос для
  коммерческого использования) — **решить с Elyor при упаковке**; запасной вариант — WiX v5 без OSMF.

### §13.6 Подпись обновлений — своя (бесплатно); Authenticode — позже, за деньги
- Без Authenticode Windows не проверяет издателя обновления (на Android это делает ОС по подписи APK). Установщик
  качается каскадом, в т.ч. через чужие зеркала → подмена = запуск чужой программы у всех пользователей.
- Своя пара ключей: закрытый — только на сервере (секреты, вне репо, не выводить), открытый — вшит в приложение.
  Релиз подписывает `update.json` (в нём sha256 установщика); приложение не ставит обновление с неверной подписью.
- Не убирает предупреждение SmartScreen при первой установке из браузера — это только платный Authenticode
  (~$200–500/год). Автообновление из приложения (файл без Mark-of-the-Web) предупреждения не должно давать —
  **проверить на ноутбуке**.

### §13.7 Порты — 10815/10816 по умолчанию
- SOCKS5 `127.0.0.1:10815`, HTTP `127.0.0.1:10816` — те же, что на Android (одинаковые инструкции). Только
  loopback: соседи по сети не подключатся, брандмауэр Windows не спрашивает.
- Добавки Windows-версии: при занятом порте — понятное сообщение (не молчаливый сбой); порт можно сменить в
  настройках (на Android — константы; на ПК конфликтов больше: другие клиенты, резервирования Hyper-V/WSL/Docker).

## 2026-10-29 — как переносим Kotlin-код (принципы порта)

### Имена — как в Kotlin, даже вопреки стилю C#
- Портированный код сохраняет Kotlin-имена **дословно**, включая camelCase у членов (`ServerProfile.address`,
  `VlessParser.parse`, `p with { pingMs = … }` вместо `copy(pingMs = …)`), чтобы diff Kotlin↔C# читался построчно
  и фикс из Android переносился механически. Kotlin `object` → `static class`, верхнеуровневые функции файла
  `X.kt` → `static class XKt` + `using static` (как JVM-имя). Новый Windows-специфичный код — обычный C#-стиль.
- **Отвергнуто:** «идиоматичный» C# (PascalCase, LINQ-переписывание) — каждый будущий перенос фикса превращался бы в
  ручной перевод с риском потерять нюанс.

### Слой совместимости `XGates.Core.Compat` — поведение Android, а не .NET
- Kotlin-парсеры подписок опираются на семантику платформы. Её нельзя заменять «похожими» API .NET — другой набор
  серверов и другие serverKey. Перенесены точные порты:
  - `JavaUri` — `java.net.URI` из **Android libcore** (не JDK): на Android «_» в имени хоста разрешён, `%` между
    `[ ]` во фрагменте не декодируется, иначе устроен разбор IPv4. `System.Uri` — мягче и нормализует.
  - `AndroidBase64` — `android.util.Base64`: пропускает ЛЮБОЙ символ вне алфавита; `Convert.FromBase64String` бросает.
  - `org.json` (`JSONObject/JSONArray/JSONTokener`) — мягкий разбор Android (комментарии, одинарные кавычки,
    hex/octal, хвост после значения) и приведения `optString/optInt`; `System.Text.Json` строже.
  - `JavaUrlDecoder`, `JavaNumbers` (Double.toString/valueOf), `KotlinText` (isBlank/trim/toIntOrNull/trimIndent —
    у Kotlin своё определение пробела: U+001C..U+001F да, U+0085 нет).
- Исходники AOSP для сверки лежат в `tools/golden/aosp/` (Apache 2.0).

### Проверка паритета — golden-эталон с настоящего Kotlin-кода
- `tools/golden/run-golden.sh` гоняет корпус (`tools/golden/corpus`, генератор `make-corpus.py`) через НАСТОЯЩИЕ
  Kotlin-классы во временной копии Android-проекта; `android.util.Base64`/`org.json` в JVM-тестах — заглушки, поэтому
  подкладываются реальные AOSP-классы отдельным jar. C#-тест `GoldenParityTests` требует совпадения профилей и
  конфигов xray **байт в байт**. Разово сверено на 905 реальных ссылках 5 дефолтных подписок — 0 расхождений
  (реальный корпус в репо не кладётся: в нём рабочие пароли публичных серверов).
- Ограничение: эталон снимается на JDK 21, а не на телефоне → Android-правки URI проверяются отдельными тестами
  (`AndroidCompatTests`) по исходникам AOSP.
- **Найдено в Android (не чинили в одностороннем порядке, см. UPSTREAM-SYNC):** (1) `JsonConfigParser` отдаёт
  `network="splithttp"`, а `XrayConfigBuilder` его не принимает («unsupported transport») — такие JSON-серверы не
  собираются; (2) ~1.5% реальных vmess-ссылок вида `vmess://<base64>=?…` отбрасываются (хвост после паддинга ломает
  android Base64). Решать вместе с Android-версией.

## 2026-10-29 — ядро xray на Windows: процесс вместо libv2ray

- **Шов — тот же API, что у gomobile-библиотеки Android** (`XGates.Core.Libv2ray`: `Libv2ray.newCoreController`,
  `CoreController.startLoop/stopLoop/isRunning/queryStats`, `Libv2ray.measureOutboundDelay/checkVersionX`), но поверх
  дочернего процесса `xray.exe`. Почему: Kotlin-код, работающий с ядром (XrayController, ServerSpeedTester,
  ServerTester), переносится без смены логики. Логика `measureOutboundDelay` взята из исходников AndroidLibXrayLite
  **Lib v39** (коммит b213898 = версия AAR в Android): 2 GET по keep-alive, минимум, ≤12 с, 200/204.
- **Конфиг — через stdin** (`xray run -c stdin:`): UUID/пароли серверов не пишутся на диск.
- **Статистика**: вывод `XrayConfigBuilder` не трогаем (паритет); `CoreController` при наличии `"stats"` дописывает
  `"api": {listen 127.0.0.1:<эфемерный>, StatsService}`; чтение-со-сбросом — gRPC `GetStats{reset:true}` (свой
  мини-клиент h2c, без NuGet). Отвергнуто: запуск `xray api statsquery` на каждый опрос (процесс раз в секунду).
- **Сироты**: Windows Job Object `KILL_ON_JOB_CLOSE` — ОС убивает xray.exe при любой смерти приложения.
- **Готовность**: строка ядра «core: Xray … started» (печатается после старта inbound-слушателей) или выход процесса
  → ошибка с хвостом вывода ядра (напр. «address already in use»).
- **Версия ядра** — v26.7.28, как в Android; `tools/xray/fetch-xray.sh` (sha256 закреплены в скрипте),
  `third_party/` в git не кладём. **`wintun.dll` из Windows-архива НЕ поставляем** (TUN запрещён рамками продукта).
- Факты xray v26.7.28, найденные тестами: freedom по умолчанию не пускает на loopback/частные IP (`finalRules`);
  у vless-outbound счётчик uplink всегда 0 (через собственный CLI ядра тоже) — то же ядро на Android → паритет.
