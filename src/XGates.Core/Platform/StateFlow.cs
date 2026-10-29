namespace XGates.Core.Platform;

/// <summary>
/// Аналог <c>kotlinx.coroutines.flow.StateFlow</c> (только то, что нужно: текущее значение + подписка на изменения).
/// Как у MutableStateFlow: равное значение (Equals) повторно не публикуется (conflation).
/// UI (WPF) подписывается через <see cref="changed"/> и маршалит в свой поток сам.
/// </summary>
public interface StateFlow<out T>
{
    T value { get; }
    event Action<object?>? changed;
}

public sealed class MutableStateFlow<T>(T initial) : StateFlow<T>
{
    private readonly object gate = new();
    private T current = initial;

    public event Action<object?>? changed;

    public T value
    {
        get { lock (gate) return current; }
        set
        {
            lock (gate)
            {
                if (EqualityComparer<T>.Default.Equals(current, value)) return;
                current = value;
            }
            changed?.Invoke(value);
        }
    }

    /// <summary>Kotlin <c>update { }</c>: атомарное преобразование.</summary>
    public void update(Func<T, T> function)
    {
        T next;
        lock (gate)
        {
            next = function(current);
            if (EqualityComparer<T>.Default.Equals(current, next)) return;
            current = next;
        }
        changed?.Invoke(next);
    }

    public StateFlow<T> asStateFlow() => this;
}
