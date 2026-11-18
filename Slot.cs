namespace deltalag;

/// <summary>
/// A typed, non-boxing data slot passed between nodes via lambda capture.
/// Only safe when <c>Slack == 0</c> and <c>MaxEdgeHops == 1</c> (strict single-hop lock-step).
/// </summary>
/// <typeparam name="T">The value type stored in the slot.</typeparam>
public sealed class Slot<T>
{
    /// <summary>
    /// Current unbuffered value stored in the slot.
    /// </summary>
    public T Value = default!;
}