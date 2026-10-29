namespace XGates.Core.Net;

/**
 * Отношение НАШЕГО трафика к системному VPN (Промпт 60). Наличие VPN в системе НЕ означает, что через
 * него идём мы: при раздельном туннелировании наш пакет может быть исключён.
 *  - NONE     — VPN в системе нет;
 *  - INSIDE   — VPN есть, НАШ трафик идёт через него;
 *  - EXCLUDED — VPN есть, наш пакет исключён → мы уже мимо.
 * (Помощники NetworkUtils.kt для Android ConnectivityManager — фаза 2 на Windows; см. HANDOFF §7.)
 */
public enum VpnRelation { NONE, INSIDE, EXCLUDED }
