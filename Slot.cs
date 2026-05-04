namespace deltalag;

/// <summary>A typed, non-boxing data slot passed between nodes via lambda capture.</summary>
public sealed class Slot<T>
{
    public T Value = default!;
}
