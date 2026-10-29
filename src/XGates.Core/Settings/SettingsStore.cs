using System.Text.Json.Serialization;
using XGates.Core.Platform;
using XGates.Core.Xray.Link;

namespace XGates.Core.Settings;

/**
 * ЕДИНЫЙ ИСТОЧНИК ПРАВДЫ для всех порогов/таймаутов. Раньше они были константами в разных файлах
 * (FullTestRunner.MIN_USABLE_MBPS/DEFAULT_MARGIN_RATIO, warmup/окно в ServerSpeedTester, пулы/таймауты
 * в ServerTester) — теперь только ЗДЕСЬ, как дефолты. Модули читают [current] на момент замера →
 * смена значения действует со следующего замера без перезапуска. Двух копий порога быть не должно.
 *
 * ЗНАЧЕНИЯ = дефолты (перенесены 1:1 из бывших констант). Persist в filesDir (как подписки).
 * Порт 1:1 с settings/SettingsStore.kt (поля сконвертированы tools/port/kt2cs_dataclass.py — с комментариями).
 * Android-специфичные поля (startOnBoot, bypassSystemVpn, wifiOnlyEnabled…) оставлены: схема JSON общая.
 */
public sealed record AppSettings
{
    // --- Замер скорости (прямо влияет на открытый вопрос 0.1 vs 4.0) ---
    // Секунды ДРОБНЫЕ (Double): нужен суб-секундный прогрев (напр. 0.5). Старые целые из JSON читаются как X.0.
    [JsonPropertyName("speedWarmupSec")] public double speedWarmupSec { get; init; } = 0.5;  // предел прогрева ПО ВРЕМЕНИ (что раньше — время ИЛИ объём)
    [JsonPropertyName("speedWindowSec")] public double speedWindowSec { get; init; } = 2.0;  // предел измерения ПО ВРЕМЕНИ (что раньше — время ИЛИ объём)
    // Бюджеты ПО ОБЪЁМУ (что раньше — время ИЛИ объём): на быстром сервере фаза кончается по МБ, не по времени.
    [JsonPropertyName("speedWarmupMb")] public int speedWarmupMb { get; init; } = 3;  // объём прогрева, МБ (без него прогрев-по-времени тянул бы больше всего лимита)
    [JsonPropertyName("speedMeasureMb")] public int speedMeasureMb { get; init; } = 10;  // УСТАРЕЛО (Пр.131): не читается — бюджет замера теперь per-mode (measureMb*/activeMeasureMb)
    [JsonPropertyName("speedPool")] public int speedPool { get; init; } = 1;  // одновременных замеров скорости; ОТ 1 (1 = строго последовательно)
    [JsonPropertyName("speedProbeUrl")] public string speedProbeUrl { get; init; } = "http://speedtest.tele2.net/1GB.zip";  // 1 ГБ: заведомо больше окна (нет eof); редактируемый (Cloudflare 403 / Hetzner TLS)

    // --- Пинг ---
    [JsonPropertyName("pingTimeoutMs")] public int pingTimeoutMs { get; init; } = 5_000;  // мягкий таймаут одного пинга
    [JsonPropertyName("pingPool")] public int pingPool { get; init; } = 8;  // одновременных пингов

    // --- Подписки ---
    [JsonPropertyName("subUserAgent")] public string subUserAgent { get; init; } = "v2rayNG/1.8.0";  // многие панели по неизвестному UA отдают Clash YAML вместо base64
    [JsonPropertyName("subTimeoutSec")] public int subTimeoutSec { get; init; } = 15;  // таймаут загрузки подписки

    // --- Выбор сервера ---
    [JsonPropertyName("minUsableMbps")] public double minUsableMbps { get; init; } = 0.05;  // порог видимости в списке / «живой»
    [JsonPropertyName("upgradeMarginPercent")] public int upgradeMarginPercent { get; init; } = 10;  // запас для апгрейда на более быстрый
    // Разрешённые протоколы (фильтр ОТОБРАЖЕНИЯ+ВЫБОРА, НЕ замера). ДЕФОЛТ обязателен: старый JSON
    // без этого поля должен разобраться (иначе все настройки слетят) → все 4 по умолчанию.
    [JsonPropertyName("allowedProtocols")] public HashSet<Protocol> allowedProtocols { get; init; } = new HashSet<Protocol> { Protocol.VLESS, Protocol.VMESS, Protocol.TROJAN, Protocol.SHADOWSOCKS };

    // --- Прочее ---
    [JsonPropertyName("verboseLogs")] public bool verboseLogs { get; init; } = true;  // детальный лог ServerSpeedTester (промпт 18)
    // При старте приложения: авто-обновить подписки и сразу запустить полный тест (подключиться к
    // быстрейшему). ДЕФОЛТ включён — это ожидаемое поведение; отключаемо здесь для ручного контроля.
    [JsonPropertyName("autoStartOnLaunch")] public bool autoStartOnLaunch { get; init; } = true;
    // Пр.134: запускать прокси ПОСЛЕ ПЕРЕЗАГРУЗКИ телефона (BootReceiver поднимает последний рабочий сервер
    // из LastServerStore, без полного теста). ДЕФОЛТ ВЫКЛ — предлагаем один раз, ненавязчиво, после того как
    // связь уже поработала (не при первом запуске). На многих телефонах требует снятия энергоограничений.
    [JsonPropertyName("startOnBoot")] public bool startOnBoot { get; init; } = false;

    // Обход ЧУЖОГО системного VPN: привязать процесс к физической сети, чтобы наш туннель шёл МИМО
    // внешнего VPN (иначе туннель-в-туннеле: двойной крюк + падение внешнего VPN роняет наш прокси).
    // ДЕФОЛТ вкл; включается только когда VPN реально активен. Выкл — если у кого-то схема наоборот.
    [JsonPropertyName("bypassSystemVpn")] public bool bypassSystemVpn { get; init; } = true;

    // Промпт 93.M: сообщать о новых версиях (полоса на главной/в настройках + уведомление по итогу
    // самостоятельной проверки). ДЕФОЛТ ВКЛ; дефолт при десериализации обязателен (старый JSON без поля).
    [JsonPropertyName("notifyNewVersions")] public bool notifyNewVersions { get; init; } = true;

    // --- Автомониторинг (следит И переключает; ОДНА настройка) ---
    // ДЕФОЛТ ВКЛ. Выключенный монитор НЕ выполняет ничего (ни проверок, ни замеров, ни журнала, ни
    // строки состояния) — ради батареи. Режима «наблюдаю, но не переключаю» больше нет.
    [JsonPropertyName("monitorEnabled")] public bool monitorEnabled { get; init; } = true;
    [JsonPropertyName("monitorIntervalSec")] public int monitorIntervalSec { get; init; } = 120;  // период цикла в ЗДОРОВОМ состоянии, с (мин 60)
    // Промпт 82: при ПАДЕНИИ туннеля проверять чаще (раз в минуту), чтобы быстрее переключиться на рабочий.
    [JsonPropertyName("monitorProblemIntervalSec")] public int monitorProblemIntervalSec { get; init; } = 60;
    // Пр.131: интервал перемера / число кандидатов / бюджет замера — РАЗДЕЛЬНЫЕ наборы по режимам (обычный/
    // экономия), активный выбирается по [trafficSaveMode] (см. active* ниже). Старое одиночное monitorOptimizeSec
    // УСТАРЕЛО (оставлено для совместимости старого JSON; код читает activeOptimizeSec).
    // Пр.131.D: сопоставимость замеров ДОКАЗАНА (Пр.130 — активный меряем тем же temp-инстансом, что кандидатов;
    // числами temp≈живой в пределах шума) → дефолт ОБЫЧНОГО режима «раз в час». Экономия — ВЫКЛ.
    [JsonPropertyName("monitorOptimizeSec")] public int monitorOptimizeSec { get; init; } = 0;  // УСТАРЕЛО (Пр.131): не читается, см. optimizeSecNormal/Save + activeOptimizeSec
    // Пр.132: «Размер топа» = сколько быстрых серверов поддерживаем актуальными (перемеряем при перемере топа),
    // НЕ «мерить всех». Интервал/бюджет подобраны под ≤6 ГБ/мес (H): обычный топ5 / 6ч / 5МБ → ≈5.6 ГБ.
    [JsonPropertyName("optimizeSecNormal")] public int optimizeSecNormal { get; init; } = 21600;  // обычный: перемер топа раз в 6 ч (Пр.132.H — укладываемся в 5-6 ГБ/мес)
    [JsonPropertyName("optimizeSecSave")] public int optimizeSecSave { get; init; } = 0;  // экономия: перемер топа ВЫКЛ (только реактивно при потере связи)
    [JsonPropertyName("topBatchNormal")] public int topBatchNormal { get; init; } = 5;  // обычный: размер топа
    // ТЗ Elyor (в): «Самый быстрый» набирает N годных (скорость ≥ порога достаточности) и останавливается — не мерит
    // все подряд. Цель — держать N быстрых серверов. Серверы со СВЕЖИМ сохранённым ≥ порога НЕ перемеряются (фиксация).
    [JsonPropertyName("fastTopTarget")] public int fastTopTarget { get; init; } = 10;  // сколько серверов ≥ порога набрать в «Самом быстром» перед остановкой
    [JsonPropertyName("topBatchSave")] public int topBatchSave { get; init; } = 2;  // экономия: топ 1-2 (Пр.132.F)
    [JsonPropertyName("measureMbNormal")] public int measureMbNormal { get; init; } = 5;  // обычный: бюджет ОДНОГО замера, МБ (без прогрева) — 5 МБ ≈ 5.6 ГБ/мес
    [JsonPropertyName("measureMbSave")] public int measureMbSave { get; init; } = 5;  // экономия: тот же скромный бюджет
    // Пр.132.D: ПРЕДЕЛ ДАВНОСТИ замера. Старее — скорость считается НЕизвестной: сервер уходит из топа и
    // подлежит перемеру (старое число ≠ действующее). По умолч. = интервалу перемера, чтобы топ обновлялся полностью.
    [JsonPropertyName("topFreshSec")] public int topFreshSec { get; init; } = 21600;
    // ТЗ Elyor (2026-10-15): ОКНО СВЕЖЕСТИ для раздела «Живые» vs «Ранее измеренные». Сервер с живым пингом И
    // измеренной скоростью>0 попадает в список 1 «Живые», ПОКА замер скорости не старше этого окна; старее —
    // уходит в список 2 «Ранее измеренные» (пинг жив, но число мерили давно, не переподтверждено). Фон/рейтинг
    // приоритетно перемеряет список 2 и возвращает ожившие в список 1. Отдельно от topFreshSec (тот — «стабилен,
    // не перемерять» для экономии трафика, 6 ч). Деф. 1 ч; настраивается в Автомониторинге.
    [JsonPropertyName("liveFreshSec")] public int liveFreshSec { get; init; } = 3600;
    // Пр.132.B: сколько ещё НЕ измеренных живых подмешивать в перемер (иначе быстрый сервер с плохим пингом
    // никогда не найдётся). 0 = не подмешивать.
    [JsonPropertyName("topMixUnmeasured")] public int topMixUnmeasured { get; init; } = 1;
    // Пр.133.B: ПОРОГ ДОСТАТОЧНОСТИ. Цель — не точное число, а «хватает на видео/Телеграм». Замер кандидата
    // ОСТАНАВЛИВАЕТСЯ, как только подтверждено, что сервер держит этот порог (дальше качать незачем) → на порядок
    // меньше трафика, чем полный замер (верхняя граница 13 МБ на быстрый сервер). Ориентир 5-10 Мбит/с — хватает на видео.
    [JsonPropertyName("sufficientMbps")] public double sufficientMbps { get; init; } = 8.0;
    // Пр.126.A: предохранители перемера (работают, когда активный интервал > 0). ОБЩИЕ для обоих режимов.
    [JsonPropertyName("monitorOptimizeWifiOnly")] public bool monitorOptimizeWifiOnly { get; init; } = true;  // не перемерять на мобильной/платной сети — только Wi-Fi
    [JsonPropertyName("monitorOptimizeIdleSkipSec")] public int monitorOptimizeIdleSkipSec { get; init; } = 1800;  // не перемерять, если через туннель давно нет трафика, с (0=не пропускать)
    [JsonPropertyName("monitorOptimizeSkipAboveMbps")] public double monitorOptimizeSkipAboveMbps { get; init; } = 10.0;  // не перемерять, если текущий уже быстрый (≥), Мбит/с (0=не пропускать)
    [JsonPropertyName("monitorDirectThreshold")] public double monitorDirectThreshold { get; init; } = 1.0;  // порог прямого канала, Мбит/с (эталон direct_speed_threshold)
    [JsonPropertyName("monitorTunnelThreshold")] public double monitorTunnelThreshold { get; init; } = 1.0;  // порог туннеля, Мбит/с (эталон tunnel_speed_threshold)
    // Сколько неудач ПОДРЯД считать падением. В эталоне решение за 1 цикл (счётчика нет) — наш анти-дребезг.
    [JsonPropertyName("monitorFailuresToVerdict")] public int monitorFailuresToVerdict { get; init; } = 2;

    // --- Экономия трафика (Промпт 77; ПЕРЕРАБОТАНО Пр.125) ---
    // ВЫКЛ по умолчанию. Режим НЕ отсеивает серверы — он МЕНЯЕТ СПОСОБ ВЫБОРА, опираясь на СОХРАНЁННЫЕ замеры:
    //  • пинг живых — остаётся (стоит килобайты/сервер; замер скорости — мегабайты);
    //  • кандидаты упорядочены по ИЗВЕСТНОЙ скорости [speedMbps] от быстрых к медленным;
    //  • подключаемся к первому и мерим ТОЛЬКО его; ниже порога — следующий; МАССОВОГО замера НЕТ никогда;
    //  • прошлых замеров нет (первый запуск / список только обновлён) — честно сказать и померить первых
    //    [trafficSaveBlindProbe] по пингу, чтобы было из чего выбрать;
    //  • монитор в этом режиме РЕЗКО тише: перемер «держать лучший» ОТКЛЮЧЁН, плановая проба плашки в простое
    //    ОТКЛЮЧЕНА, остаются только дешёвые проверки живости (см. NetworkMonitor).
    // ГРАБЛЯ (Пр.125): раньше режим упорядочивал по ПИНГУ и мерил батчами → подключался к случайному/медленному
    //   (пинг ≠ скорость), а у кого замеров не было — «живых 0». Поля batch/minAlive/refreshSec БОЛЬШЕ НЕ
    //   используются (оставлены только для совместимости старого JSON; refreshSec был мёртв и раньше).
    // Пр.139: ЭФФЕКТИВНОЕ состояние экономии (его читают ВСЕ потребители). НЕ настраивается напрямую —
    // вычисляется из [economyEnabled] + [trafficSaveAutoByNetwork] + типа сети в EconomyNet. При загрузке
    // нормализуется (если мастер выкл → false).
    [JsonPropertyName("trafficSaveMode")] public bool trafficSaveMode { get; init; } = false;
    // Пр.139: МАСТЕР «Режим экономии трафика» — НАМЕРЕНИЕ пользователя. Persist, помним между обновлениями
    // (первая установка — ВЫКЛ; далее как оставил юзер). Всё, что связано с экономией, включается ТОЛЬКО при нём.
    [JsonPropertyName("economyEnabled")] public bool economyEnabled { get; init; } = false;
    // Пр.139: ПОДПУНКТ мастера «Экономить только на мобильной сети» (виден/меняется только при economyEnabled).
    // ДЕФОЛТ ВКЛ. вкл → на Wi-Fi/Ethernet обычный режим, на мобильной экономия; выкл → экономия всегда.
    // (Историческое имя trafficSaveAutoByNetwork из Пр.129 — та же суть «эконом. по типу сети».)
    [JsonPropertyName("trafficSaveAutoByNetwork")] public bool trafficSaveAutoByNetwork { get; init; } = true;
    // Пр.137: одноразовая миграция «выключить авто-экономию» на существующих установках. (Оставлено; Пр.139 —
    // отдельная миграция [economyModelV2Migrated] под новую модель мастер+подпункт.)
    [JsonPropertyName("economyAutoResetDone")] public bool economyAutoResetDone { get; init; } = false;
    // Пр.139: одноразовая миграция под модель мастер+подпункт: economyEnabled=ВЫКЛ (после обновления экономия
    // не активна, дальше — по выбору юзера), подпункт «только моб.» = ВКЛ (дефолт). Ставится один раз.
    [JsonPropertyName("economyModelV2Migrated")] public bool economyModelV2Migrated { get; init; } = false;

    // --- ТЗ Elyor: режим «Только Wi-Fi» (НЕЗАВИСИМ от «Режима экономии» выше — два отдельных тумблера) ---
    // ВЫКЛ по умолчанию. Когда ВКЛ, поведение зависит от сети+питания:
    //   • Wi-Fi/Ethernet + на шнуре  → работа по ПОЛНОЙ программе (агрессивный замер/рейтинг — как обычный режим);
    //   • Wi-Fi/Ethernet + на батарее → ЩАДЯЩИЙ режим (реже/меньше замеров) — через эффективный trafficSaveMode;
    //   • МОБИЛЬНАЯ сеть              → туннель на ПАУЗЕ (ядро Xray стоп, сервис жив и слушает сеть), САМ поднимается,
    //                                   когда появится Wi-Fi (пауза/возобновление — в XrayProxyService по событию сети).
    // Щадящий-на-батарее реализован как ИЛИ в EconomyNet.recompute (единственный писатель trafficSaveMode): кто угодно
    // из «экономии» ИЛИ «Только Wi-Fi на батарее» просит щадящий → trafficSaveMode=true. Конфликта/приоритета нет.
    [JsonPropertyName("wifiOnlyEnabled")] public bool wifiOnlyEnabled { get; init; } = false;

    // --- Пр.140: режим белых списков РФ ---
    // Фича (Настройки): если сеть пускает только «белый список» (yandex открыт, google — нет), приложение само
    // переходит на источники белого списка и раз в полчаса проверяет возврат google. ДЕФОЛТ ВКЛ.
    [JsonPropertyName("whiteListAutoEnabled")] public bool whiteListAutoEnabled { get; init; } = true;
    // РАНТАЙМ-состояние (пишет детектор, читают allServers/UI): сейчас активен режим белых списков.
    [JsonPropertyName("whiteListModeActive")] public bool whiteListModeActive { get; init; } = false;
    // Пользователь нажал «Обычный режим» на главной при активном белом режиме → НЕ загонять обратно автоматически,
    // пока google не вернётся (тогда снимаем) или пока не сменится сеть (свежая оценка).
    [JsonPropertyName("whiteListManualNormal")] public bool whiteListManualNormal { get; init; } = false;
    // Пр.129.C: пользователь переключил режим ВРУЧНУЮ при активной автоматике → его выбор держится ДО следующей
    // смены типа сети, потом автоматика снова берёт своё. Флаг снимается при смене сети (EconomyNet).
    [JsonPropertyName("trafficSaveManualUntilNetChange")] public bool trafficSaveManualUntilNetChange { get; init; } = false;
    [JsonPropertyName("trafficSaveBlindProbe")] public int trafficSaveBlindProbe { get; init; } = 3;  // Пр.139: экономия мерит НЕМНОГО (первый живой + пара на фоллбэк); «10» — про ОБЫЧНЫЙ режим (он мерит всех живых)
    [JsonPropertyName("trafficSaveBatch")] public int trafficSaveBatch { get; init; } = 5;  // УСТАРЕЛО (Пр.125): не используется
    [JsonPropertyName("trafficSaveMinAlive")] public int trafficSaveMinAlive { get; init; } = 2;  // УСТАРЕЛО (Пр.125): не используется
    [JsonPropertyName("trafficSaveRefreshSec")] public int trafficSaveRefreshSec { get; init; } = 3600;  // УСТАРЕЛО (Пр.125): не используется
    // top-N по скорости: для ОПТИМИЗАЦИИ монитора «держать самый быстрый» (Промпт 82). Ручной тест
    // мерит ВСЕХ (Промпт 82: ступенчатый top-N — только монитор/экономия, не ручной запуск).
    [JsonPropertyName("normalTopBatch")] public int normalTopBatch { get; init; } = 5;  // УСТАРЕЛО (Пр.131): не читается — число кандидатов теперь per-mode (topBatch*/activeTopBatch)
    // Промпт 82: общий БЮДЖЕТ ВРЕМЕНИ ручного полного теста, с. Мерим всех живых по очереди, но не дольше
    // этого (на ~100 серверах ×2с ≈ 3–4 мин; лимит защищает от зависания на медленных). Дефолт 10 мин.
    [JsonPropertyName("fullTestBudgetSec")] public int fullTestBudgetSec { get; init; } = 600;

    // --- Промпт 90: «держать лучший» + отдача + периодичность дешёвой проверки ---
    // ВТОРОЕ правило выбора (независимо от порога): если известный лучший живой сервер быстрее текущего
    // В [keepBestMultiplier] РАЗ (не на проценты — кратно), переключаемся, даже когда текущий выше порога.
    // ПОЧЕМУ кратно, а не 10%: небольшая разница шумит (±джиттер замера), кратная — реальный выигрыш.
    [JsonPropertyName("keepBestMultiplier")] public double keepBestMultiplier { get; init; } = 3.0;
    // Дешёвая проверка соединения раз в [connectionCheckIntervalSec] (жив ли туннель/идёт ли трафик). Полный
    // замер (скачивание+отдача) — ТОЛЬКО по подозрению/кнопке/часовому перемеру: замер отдачи раз в минуту
    // круглосуточно ≈ 3 ГБ/сут при 8 Мбит — так нельзя. Новое поле (дефолт применится и на старых устройствах).
    [JsonPropertyName("connectionCheckIntervalSec")] public int connectionCheckIntervalSec { get; init; } = 60;
    // ОТДАЧА (Промпт 90.B): измеряется у АКТИВНОГО соединения (выгрузка через туннель на приёмник).
    // Приёмник редактируемый (как для скачивания). НЕ Cloudflare (403 на прокси-адреса) и НЕ Hetzner (рвёт TLS).
    // Промпт 104.B: cloudflare __up отвечает 200 за ~1.2с (факт, POST 2МБ) — быстрый приёмник отдачи. tele2/upload.php
    // ЧЕРЕЗ ТУННЕЛЬ не отвечает (http=000) → ждали весь таймаут (сток 25-75с). tele2 остался запасным (uploadFallbacks).
    [JsonPropertyName("uploadProbeUrl")] public string uploadProbeUrl { get; init; } = "https://speed.cloudflare.com/__up";
    // Минимальная отдача, Мбит/с. Дефолт = порогу скачивания (monitorTunnelThreshold=1.0). Недобор → искать замену.
    [JsonPropertyName("minUploadMbps")] public double minUploadMbps { get; init; } = 1.0;
    // Бюджет ПО ОБЪЁМУ на один замер отдачи, МБ (отдельный от скачивания). Дефолт скромный — отдача дороже.
    [JsonPropertyName("uploadMeasureMb")] public int uploadMeasureMb { get; init; } = 2;

    // --- Деградация «живой, но медленный» (Пр.127.C) ---
    // Проверка живости говорит «связь есть», но не видит МЕДЛЕННЫЙ туннель (пингом скорость не измерить).
    // Признак деградации берём БЕСПЛАТНО из пассивных счётчиков УЖЕ идущего трафика: если через туннель реально
    // качается и наблюдаемая скорость ниже [degradationMinMbps] — делаем ОДИН замер лучшего по сохранённым
    // данным кандидата и переходим, если он приемлемо быстрее. Один шаг, не перебор. Тишину (нет трафика) НЕ
    // трактуем как ноль. Не чаще раза в [degradationCheckSec]. Работает в ОБОИХ режимах (в экономии цель —
    // приемлемый туннель, не лучший). degradationMinMbps=0 — фича выключена.
    [JsonPropertyName("degradationMinMbps")] public double degradationMinMbps { get; init; } = 2.0;  // Q3 Elyor: уходим с активного, только если он ниже 2 Мбит/с (было 3.0)
    [JsonPropertyName("degradationCheckSec")] public int degradationCheckSec { get; init; } = 1800;  // мин. интервал между шагами по деградации, с (по умолч. 30 мин)
    // Пр.141: в ЭКОНОМИИ автоперемер выключен, поэтому полу-живой сервер (проходит IP-чек, но реально плохо) сам
    // не чинится. Раз в [economyRecheckSec] делаем ОДИН дешёвый замер достаточности активного (temp-инстанс) и,
    // если ниже порога деградации, шагаем на лучшего кандидата. Дёшево (1 замер на достаточность). 0 = выкл.
    [JsonPropertyName("economyRecheckSec")] public int economyRecheckSec { get; init; } = 14400;  // 4 часа

    // --- Живая скорость на плашке (гибрид): показывать актуальную ↓/↑ активного туннеля, а не «скорость подбора» ---
    // ВКЛ по умолчанию. Когда идёт трафик пользователя — скорость берётся БЕСПЛАТНО из счётчиков ядра (дельта
    // туннельных байт). Когда простой — раз в [liveSpeedActiveProbeSec] маленькая активная проба (download+upload),
    // и ТОЛЬКО при включённом экране (нет смысла тратить трафик на замер для плашки, которую никто не видит).
    [JsonPropertyName("liveSpeedEnabled")] public bool liveSpeedEnabled { get; init; } = true;
    [JsonPropertyName("liveSpeedActiveProbeSec")] public int liveSpeedActiveProbeSec { get; init; } = 300;  // интервал активной пробы в простое, с (по умолч. 5 мин)
    // «Напрямую» ↓ (прямой канал без туннеля): БОЛЬШОЙ файл на быстром RU-зеркале, доступном НАПРЯМУЮ (Yandex
    // mirror, ISO ~1ГБ — заведомо больше окна, нет EOF; путь /latest/ стабилен). ПОЧЕМУ большой и RU: замер
    // мелкой страницы мерил бы задержку, а не ширину канала (фейковые 0.4 Мбит/с при живом туннеле 100+);
    // foreign-хост напрямую может быть заблокирован → провал. Проверено на устройстве: ~130 Мбит/с. Редактируемо.
    [JsonPropertyName("directProbeUrl")] public string directProbeUrl { get; init; } = "https://mirror.yandex.ru/archlinux/iso/latest/archlinux-x86_64.iso";

    // Производные (в единицах, которые нужны коду).
    [JsonIgnore] public int speedWarmupMs => (speedWarmupSec * 1_000).roundToInt();
    [JsonIgnore] public int speedWindowMs => (speedWindowSec * 1_000).roundToInt();
    [JsonIgnore] public long speedWarmupBudgetBytes => speedWarmupMb * 1_048_576L;
    [JsonIgnore] public long speedMeasureBudgetBytes => speedMeasureMb * 1_048_576L;
    [JsonIgnore] public long uploadMeasureBudgetBytes => uploadMeasureMb * 1_048_576L;
    [JsonIgnore] public double marginRatio => upgradeMarginPercent / 100.0;

    /** Пр.139: ЭФФЕКТИВНАЯ экономия для данного типа сети — чистая функция мастера+подпункта (для EconomyNet и тестов).
     *  мастер выкл → нет; мастер вкл + «только моб.» → лишь на мобильной; мастер вкл + «всегда» → всегда. */
    public bool economyEffective(bool isMobile) => economyEnabled && (!trafficSaveAutoByNetwork || isMobile);

    // Пр.131: АКТИВНЫЙ набор (обычный/экономия) по [trafficSaveMode] — его читают все потребители.
    [JsonIgnore] public int activeOptimizeSec => trafficSaveMode ? optimizeSecSave : optimizeSecNormal;
    [JsonIgnore] public int activeTopBatch => trafficSaveMode ? topBatchSave : topBatchNormal;
    [JsonIgnore] public int activeMeasureMb => trafficSaveMode ? measureMbSave : measureMbNormal;
    [JsonIgnore] public long activeMeasureBudgetBytes => activeMeasureMb * 1_048_576L;

    /// <summary>Kotlin data class сравнивает Set по содержимому; record — по ссылке. Чиним для allowedProtocols.</summary>
    public bool Equals(AppSettings? other) =>
        other is not null && EqualityContract == other.EqualityContract && ToKey() == other.ToKey();

    public override int GetHashCode() => ToKey().GetHashCode();

    private string ToKey() => System.Text.Json.JsonSerializer.Serialize(this with { allowedProtocols = new HashSet<Protocol>(allowedProtocols.Order()) }, KJson.Compact);
}

public static class SettingsStore
{
    private const string TAG = "SettingsStore";
    private const string FILE_NAME = "settings.json";
    private const string TMP_NAME = "settings.json.tmp";
    private static readonly object Sync = new();

    /// <summary>Дефолты — единственное место, где живут значения по умолчанию.</summary>
    public static readonly AppSettings DEFAULTS = new();

    private static readonly MutableStateFlow<AppSettings> _state = new(DEFAULTS);
    public static StateFlow<AppSettings> state => _state.asStateFlow();

    /// <summary>Текущий снимок — модули читают ЭТО на момент замера (живое применение).</summary>
    public static AppSettings current() => _state.value;

    private static string file(Context context) => Path.Combine(context.filesDir, FILE_NAME);

    /// <summary>Загрузить из файла в state. Вызвать один раз при старте (MainActivity.onCreate).</summary>
    public static void init(Context context)
    {
        lock (Sync)
        {
            var f = file(context);
            if (!File.Exists(f)) return;
            try
            {
                var s = KJson.decodeFromString<AppSettings>(File.ReadAllText(f));
                // Пр.139: одноразовая миграция под модель мастер+подпункт. Мастер economyEnabled=ВЫКЛ (после
                // обновления экономия не активна; дальше помним выбор юзера), подпункт «только моб.» = ВКЛ (дефолт).
                if (!s.economyModelV2Migrated)
                {
                    s = s with
                    {
                        economyEnabled = false,
                        trafficSaveAutoByNetwork = true,
                        economyModelV2Migrated = true,
                        economyAutoResetDone = true,   // старую миграцию считаем пройденной (её эффект поглощён этой)
                    };
                }
                // Нормализация ЭФФЕКТИВНОГО состояния: мастер выкл → экономии нет (EconomyNet уточнит по сети при старте).
                if (!s.economyEnabled && s.trafficSaveMode) s = s with { trafficSaveMode = false };
                _state.value = s;
                try { update(context, s); } catch (Exception) { }   // сохранить нормализованное/мигрированное
            }
            catch (Exception e)
            {
                Log.w(TAG, "load failed, keeping defaults", e);
            }
        }
    }

    /// <summary>Обновить и атомарно сохранить (temp→rename). Валидацию делает вызывающий (UI).</summary>
    public static void update(Context context, AppSettings @new)
    {
        lock (Sync)
        {
            _state.value = @new;
            try
            {
                KJson.atomicWrite(file(context), Path.Combine(context.filesDir, TMP_NAME), KJson.encodeToString(@new));
            }
            catch (Exception e)
            {
                Log.w(TAG, "save failed", e);
            }
        }
    }

    /// <summary>Сбросить всё к дефолтам.</summary>
    public static void resetToDefaults(Context context) => update(context, DEFAULTS);
}
