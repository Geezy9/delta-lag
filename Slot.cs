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
/// SPSC Typed, non-boxing RingBuffer, data is passed between nodes though methods on the Ring
/// Use Per Edge
/// Default for slack > 0;
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class Ring<T>
{
    public T Type;
    public T[] Buffers;
    private int mask;



    public Ring(int size)
    {
        if ((size & (size - 1)) != 0)
            throw new ArgumentException(
                "Size must be a power of two");

        Buffers = new T[size];
        mask = size - 1;
    }

    public static Ring<T> FromCon(Config con)
    {
        int size = checked((int)(con.Slack + 1));

        size = (int)BitOperations.RoundUpToPowerOf2((nuint)size);

        return new Ring<T>(size);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read(long workUnit)
        => Buffers[(int)workUnit & mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(long workUnit, T payload)
        => Buffers[(int)workUnit & mask] = payload;


}