namespace deltalag;

public class Node
{
    private int _workDone;
    private int _workClaimed;
    public int WorkClaimed => Volatile.Read(ref _workClaimed);
    public int WorkDone
    {
        get => Volatile.Read(ref _workDone);
        set => Interlocked.Exchange(ref _workDone, value);
    }


    /// <summary>
    /// The unit of work this node performs each time it fires.
    /// Reads from and writes to captured <see cref="Slot{T}"/> instances — no boxing.
    /// </summary>
    public Action<int>? Task
    {
        get; set;
    }

    /// <summary>Atomically claims a work unit to prevent double-firing. Returns true if this thread won the race.</summary>
    public bool TryClaimWork(int expected) =>
        Interlocked.CompareExchange(ref _workClaimed, expected + 1, expected) == expected;

    /// <summary>Publishes that the claimed work unit is fully complete and its outputs are visible.</summary>
    public void PublishWorkDone(int newValue) =>
        Interlocked.Exchange(ref _workDone, newValue);
}

