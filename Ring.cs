using System.Numerics;
using System.Runtime.CompilerServices;

namespace deltalag;

/// <summary>
/// Typed, non-boxing SPSC ring buffer for passing data per edge when <c>Slack &gt; 0</c> or <c>MaxEdgeHops &gt; 1</c>.
/// </summary>
/// <typeparam name="T">The payload element type.</typeparam>
public sealed class Ring<T>
{
    internal readonly T[] Buffers;
    private readonly int _mask;

    /// <summary>
    /// Allocates a ring buffer with the specified power-of-two capacity.
    /// </summary>
    /// <param name="size">Buffer capacity; must be a positive power of two.</param>
    public Ring(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
            throw new ArgumentException("Size must be a positive power of two.", nameof(size));

        Buffers = new T[size];
        _mask = size - 1;
    }

    /// <summary>
    /// Creates a <see cref="Ring{T}"/> sized for single-hop edges (<c>config.Slack + 1</c> rounded up to a power of two).
    /// </summary>
    /// <param name="config">Execution configuration containing <see cref="Config.Slack"/>.</param>
    public static Ring<T> FromConfig(Config config)
        => FromConfig(config, maxHops: 1);

    /// <summary>
    /// Creates a <see cref="Ring{T}"/> sized for multi-hop relay paths (<c>maxHops * (config.Slack + 1)</c> rounded up to a power of two).
    /// </summary>
    /// <param name="config">Execution configuration containing <see cref="Config.Slack"/>.</param>
    /// <param name="maxHops">Maximum hop distance across an edge path (must be at least 1).</param>
    public static Ring<T> FromConfig(Config config, int maxHops)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(config.Slack);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHops, 1);

        int size = checked(maxHops * (config.Slack + 1));
        size = checked((int)BitOperations.RoundUpToPowerOf2((nuint)size));

        return new Ring<T>(size);
    }

    /// <summary>
    /// Creates a <see cref="Ring{T}"/> sized from <see cref="Scheduler.WorkContext.MaxEdgeHops"/> and <see cref="Config.Slack"/>.
    /// </summary>
    /// <param name="ctx">Compiled work context returned by <see cref="GraphBuilder.Build"/>.</param>
    /// <param name="config">Execution configuration containing <see cref="Config.Slack"/>.</param>
    public static Ring<T> FromContext(Scheduler.WorkContext ctx, Config config)
        => FromConfig(config, Math.Max(1, ctx.MaxEdgeHops));

    /// <summary>
    /// Reads the payload for <paramref name="workUnit"/> (masked to buffer capacity).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read(long workUnit)
        => Buffers[(int)workUnit & _mask];

    /// <summary>
    /// Writes <paramref name="payload"/> for <paramref name="workUnit"/> (masked to buffer capacity).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(long workUnit, T payload)
        => Buffers[(int)workUnit & _mask] = payload;
}
