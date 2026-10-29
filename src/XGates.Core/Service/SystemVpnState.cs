using XGates.Core.Net;
using XGates.Core.Platform;

namespace XGates.Core.Service;

/**
 * Состояние по отношению к ЧУЖОМУ системному VPN. ЕДИНСТВЕННЫЙ источник истины — каскад и статус-бокс читают
 * отсюда. Порт 1:1 с service/SystemVpnState.kt.
 * Windows: обход VPN — фаза 2 (HANDOFF §7), поэтому пока никто не пишет сюда и relation = NONE (идём маршрутом ОС).
 *
 * @param relation     отношение нашего трафика к VPN (нет / внутри / исключены)
 * @param bypassed     идём ли МИМО VPN (процесс привязан к физической сети). Осмысленно при INSIDE.
 * @param bypassFailed обход не удался (lockdown), но через VPN связь ЕСТЬ → идём через VPN; замер = канал VPN.
 * @param noExit       INSIDE + lockdown + чужой VPN не пропускает → наружу не выходит НИКТО (не наша поломка).
 */
public sealed record VpnStatus(
    VpnRelation relation = VpnRelation.NONE,
    bool bypassed = false,
    bool bypassFailed = false,
    bool noExit = false);

public static class SystemVpnState
{
    private static readonly MutableStateFlow<VpnStatus> _state = new(new VpnStatus());
    public static StateFlow<VpnStatus> state => _state.asStateFlow();

    public static void update(VpnRelation relation, bool bypassed, bool bypassFailed, bool noExit = false) =>
        _state.value = new VpnStatus(relation, bypassed, bypassFailed, noExit);

    public static void reset() => _state.value = new VpnStatus();
}
