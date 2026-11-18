namespace deltalag;

/// <summary>
/// Execution and compilation settings consumed by <see cref="GraphBuilder.Build"/>.
/// </summary>
public class Config
{
    /// <summary>Total iterations (work units <c>0..Cycles - 1</c>) executed by each node.</summary>
    public int Cycles;

    /// <summary>Maximum allowed downstream lag between a producer and its children.</summary>
    public int Slack;

    /// <summary>Desired worker thread count (clamped to available logical processors and node count).</summary>
    public int Threads;

    /// <summary>Static node-to-thread partitioning strategy.</summary>
    public PartitionStrategy Strategy;

    /// <summary>Enables automatic k-ary relay tree injection for high fan-in or fan-out nodes.</summary>
    public bool InjectNodes;

    /// <summary>Branching factor for injected k-ary relay trees (minimum 2 when enabled).</summary>
    public int K;

    /// <summary>Maximum allowed parent count per node before k-ary fan-in reduction (<c>&lt;= 0</c> for unlimited).</summary>
    public int MaxFanIn;

    /// <summary>Maximum allowed child count per node before k-ary fan-out reduction (<c>&lt;= 0</c> for unlimited).</summary>
    public int MaxFanOut;

    /// <summary>Isolates zero-in-degree root nodes on thread <c>0</c> to reduce startup latency.</summary>
    public bool SeparateRootThread;

    /// <summary>Isolates zero-out-degree sink nodes on the last worker thread to reduce completion latency.</summary>
    public bool SeparateSinkThread;

    /// <summary>Pins worker threads to logical CPU cores on Windows and Linux (with graceful fallback).</summary>
    public bool ThreadAffinity;
}
