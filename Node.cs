namespace deltalag;

public class Node
{
    private int _workDone;
    public int WorkDone
    {
        get => Volatile.Read(ref _workDone);
        set => Interlocked.Exchange(ref _workDone, value);
    }

    /// <summary>
    /// The unit of work this node performs each time it fires.
    /// Reads from and writes to captured <see cref="Slot{T}"/> instances — no boxing.
    /// </summary>
    public Action? Task
    {
        get; set;
    }

    /// <summary>Atomically fires the node only if WorkDone is still <paramref name="expected"/>. Returns true if the CAS succeeded.</summary>
    public bool TryIncrementWorkDone(int expected) =>
        Interlocked.CompareExchange(ref _workDone, expected + 1, expected) == expected;
}
