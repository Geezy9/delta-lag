using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace deltalag;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// 64-byte cache line,
[StructLayout(LayoutKind.Sequential, Size = 60)]
public struct Pad60 { }

// for object header and alignment padding. 8 bytes on x64
[StructLayout(LayoutKind.Sequential, Size = 56)]
public struct Pad56 { }

[StructLayout(LayoutKind.Explicit)]
public class Node
{
    
    [FieldOffset(0)]
    private long _workDone;

    [FieldOffset(8)]
    private Pad56 _pad0;          

    [FieldOffset(64)]
    private long _workClaimed;

    [FieldOffset(72)]
    private Pad56 _pad1;          

    [FieldOffset(128)]
    private Action<int>? _task;

    [FieldOffset(136)]
    private Pad56 _pad2;          

    public long WorkClaimed => Volatile.Read(ref _workClaimed);

    public long WorkDone
    {
        get => Volatile.Read(ref _workDone);
        set => Interlocked.Exchange(ref _workDone, value);
    }

    public Action<int>? Task
    {
        get => Volatile.Read(ref _task);
        set => Volatile.Write(ref _task, value);
    }

    public bool TryClaimWork(long expected) =>
        Interlocked.CompareExchange(ref _workClaimed, expected + 1, expected) == expected;

    public void PublishWorkDone(long newValue) =>
        Interlocked.Exchange(ref _workDone, newValue);
}



