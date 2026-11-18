using System.Numerics;
using System.Runtime.CompilerServices;


namespace deltalag;

/// <summary>A typed, non-boxing data slot passed between nodes via lambda capture.</summary>
/// DO NOT USE WITH SLACK > 0; can cause issues.
public sealed class Slot<T>
{
    public T Value = default!;
}

/// <summary>
/// SPSC Typed, non-boxing RingBuffer, data is passed between nodes through methods on the Ring
/// Use Per Edge
/// Default for slack > 0;
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class Ring<T>
{
    internal readonly T[] Buffers;
    private readonly int mask;



    public Ring(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
            throw new ArgumentException(
                "Size must be a positive power of two.", nameof(size));

        Buffers = new T[size];
        mask = size - 1;
    }

    public static Ring<T> FromCon(Config con)
        => FromCon(con, maxHops: 1);

    public static Ring<T> FromCon(Config con, int maxHops)
    {
        if (con is null)
            throw new ArgumentNullException(nameof(con));
        if (con.Slack < 0)
            throw new ArgumentOutOfRangeException(nameof(con.Slack), "Slack cannot be negative.");
        if (maxHops < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHops), "maxHops must be at least 1.");

        int size = checked(maxHops * (con.Slack + 1));

        size = checked((int)BitOperations.RoundUpToPowerOf2((nuint)size));

        return new Ring<T>(size);
    }

    public static Ring<T> FromContext(Scheduler.WorkContext ctx, Config con)
        => FromCon(con, Math.Max(1, ctx.MaxEdgeHops));


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read(long workUnit)
        => Buffers[(int)workUnit & mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(long workUnit, T payload)
        => Buffers[(int)workUnit & mask] = payload;


}