using System.Runtime.InteropServices;

namespace deltalag;

// Two 64-byte cache lines: _workDone and _workClaimed never share a line with each other
// or with a neighbouring node in a Node[].
// Access in place only (nodes[i].X or ref nodes[i]); a copy would lose writes.
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct Node
{
    
    [FieldOffset(0)]
    private long _workDone;

    [FieldOffset(64)]
    private long _workClaimed;
     

    public long WorkClaimed => Volatile.Read(ref _workClaimed);

    public long WorkDone
    {
        get => Volatile.Read(ref _workDone);
        set => Interlocked.Exchange(ref _workDone, value);
    }

    public bool TryClaimWork(long expected) =>
        Interlocked.CompareExchange(ref _workClaimed, expected + 1, expected) == expected;

    public void PublishWorkDone(long newValue) =>
        Interlocked.Exchange(ref _workDone, newValue);
}



