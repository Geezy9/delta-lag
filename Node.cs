using System.Runtime.InteropServices;

namespace deltalag;

/// <summary>
/// Cache-padded 128-byte per-node progress state separating <c>_workDone</c> (offset 0) and
/// <c>_workClaimed</c> (offset 64) across distinct 64-byte cache lines to prevent false sharing.
/// Access elements in place (<c>ctx.NodeArray[i]</c> or <c>ref ctx.NodeArray[i]</c>).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct Node
{
    [FieldOffset(0)]
    private long _workDone;

    [FieldOffset(64)]
    private long _workClaimed;

    /// <summary>Volatile read of the number of work units claimed for execution.</summary>
    public long WorkClaimed => Volatile.Read(ref _workClaimed);

    /// <summary>Volatile read of the number of completed work units.</summary>
    public long WorkDone => Volatile.Read(ref _workDone);

    /// <summary>Atomically claims work unit <paramref name="expected"/> by advancing <see cref="WorkClaimed"/> to <c>expected + 1</c>.</summary>
    public bool TryClaimWork(long expected) =>
        Interlocked.CompareExchange(ref _workClaimed, expected + 1, expected) == expected;

    /// <summary>Atomically publishes <paramref name="newValue"/> as the latest completed work count.</summary>
    public void PublishWorkDone(long newValue) =>
        Interlocked.Exchange(ref _workDone, newValue);
}
