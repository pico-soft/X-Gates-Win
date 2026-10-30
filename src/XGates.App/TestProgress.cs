using XGates.Core.Platform;

namespace XGates.App;

/// <summary>Снимок прогресса Полного теста для бара. Порт ui/TestProgress.kt.</summary>
public sealed record TestProgressState(
    bool active = false,        // бар виден
    bool indeterminate = false, // total неизвестен (подписка/подъём ядра/старт)
    float fraction = 0f,        // доля 0..1 текущей фазы
    string phase = "");         // текст «что происходит» (под баром)

/// <summary>Прогресс Полного теста. ОТДЕЛЬНЫЙ StateFlow (не состояние экрана): тик не перерисовывает список.</summary>
public static class TestProgress
{
    private static readonly MutableStateFlow<TestProgressState> _state = new(new TestProgressState());
    public static StateFlow<TestProgressState> state => _state.asStateFlow();

    /// <summary>Старт/индетерминатность (total неизвестен): бар виден, ползунок бесконечный.</summary>
    public static void startIndeterminate(string phase) =>
        _state.value = new TestProgressState(active: true, indeterminate: true, fraction: 0f, phase: phase);

    /// <summary>Determinate-тик фазы. total&lt;=0 → снова indeterminate. Фаза-текст не трогаем (идёт из phase()).</summary>
    public static void progress(int done, int total)
    {
        var f = total > 0 ? Math.Clamp((float)done / total, 0f, 1f) : 0f;
        _state.value = _state.value with { active = true, indeterminate = total <= 0, fraction = f };
    }

    /// <summary>Только текст фазы (бар/доля не трогаем).</summary>
    public static void phase(string text) => _state.value = _state.value with { phase = text };

    /// <summary>Завершение прогона / отмена: бар гаснет, итоговый текст остаётся.</summary>
    public static void finish(string text) => _state.value = _state.value with { active = false, phase = text };

    /// <summary>Полный сброс (бар и текст).</summary>
    public static void clear() => _state.value = new TestProgressState();
}
