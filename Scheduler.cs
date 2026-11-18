using System.Runtime.ExceptionServices;

namespace deltalag;

/// <summary>
/// Controls how compiled graph nodes are statically assigned across worker threads.
/// </summary>
public enum PartitionStrategy
{
    /// <summary>Distributes node indices round-robin across worker threads (default).</summary>
    Stride,

    /// <summary>Splits node indices into contiguous slices across worker threads.</summary>
    ChunkedLinear,

    /// <summary>Orders connected components via undirected BFS and assigns contiguous chunks per thread.</summary>
    Clustered,

    /// <summary>Groups nodes by topological depth and stripes levels across worker threads.</summary>
    Leveled,

    /// <summary>Orders nodes via depth-first traversal from roots and assigns contiguous chunks per thread.</summary>
    DepthFirst,
}

/// <summary>
/// Lock-free DAG execution engine backed by a persistent, reusable pool of worker threads.
/// </summary>
public class Scheduler : IDisposable
{
    /// <summary>
    /// Stack-only compiled graph and execution schedule produced by <see cref="GraphBuilder.Build"/>.
    /// </summary>
    public ref struct WorkContext
    {
        /// <summary>Per-node cache-padded atomic progress counters.</summary>
        public Node[] NodeArray;

        /// <summary>Forward CSR target node indices.</summary>
        public int[] Edges;

        /// <summary>Forward CSR row offsets (<c>NodeArray.Length + 1</c>).</summary>
        public int[] Offsets;

        /// <summary>Reverse CSR parent node indices.</summary>
        public int[] ParentEdges;

        /// <summary>Reverse CSR row offsets (<c>NodeArray.Length + 1</c>).</summary>
        public int[] ParentOffsets;

        /// <summary>Per-node task delegates (<c>null</c> for injected relay nodes).</summary>
        public Action<int>?[] Tasks;

        /// <summary>Target completed work units per node.</summary>
        public int Cycles;

        /// <summary>Maximum allowed downstream lag per hop.</summary>
        public int Slack;

        /// <summary>Effective worker thread count.</summary>
        public int ThreadCount;

        /// <summary>Pre-partitioned node ID schedules per worker thread.</summary>
        public int[][] ThreadSchedules;

        /// <summary>Whether worker threads attempt to pin to logical CPU cores.</summary>
        public bool ThreadAffinity;

        /// <summary>Maximum hop distance between any original node and its original children across injected relays.</summary>
        public int MaxEdgeHops { get; set; }
    }

    private sealed class RunState
    {
        public readonly Node[] NodeArray;
        public readonly int[] Edges;
        public readonly int[] Offsets;
        public readonly int[] ParentEdges;
        public readonly int[] ParentOffsets;
        public readonly Action<int>?[] Tasks;
        public readonly int Cycles;
        public readonly int Slack;
        public readonly int[][] ThreadSchedules;
        public int Remaining;
        public volatile bool Faulted;

        private List<Exception>? _exceptions;

        public RunState(WorkContext ctx)
        {
            NodeArray = ctx.NodeArray;
            Edges = ctx.Edges;
            Offsets = ctx.Offsets;
            ParentEdges = ctx.ParentEdges;
            ParentOffsets = ctx.ParentOffsets;
            Tasks = ctx.Tasks;
            Cycles = ctx.Cycles;
            Slack = ctx.Slack;
            ThreadSchedules = ctx.ThreadSchedules;
            Remaining = ctx.ThreadCount;
        }

        public void RecordException(Exception ex)
        {
            Faulted = true;
            lock (this)
            {
                _exceptions ??= new List<Exception>();
                _exceptions.Add(ex);
            }
        }

        public void ThrowIfFaulted()
        {
            if (_exceptions is null || _exceptions.Count == 0)
                return;

            if (_exceptions.Count == 1)
                ExceptionDispatchInfo.Capture(_exceptions[0]).Throw();

            throw new AggregateException(_exceptions);
        }
    }

    private readonly object _sync = new();
    private readonly ManualResetEventSlim _doneSignal = new(false);

    private Thread[]? _workers;
    private int _poolThreadCount;
    private bool _poolThreadAffinity;
    private RunState? _currentRun;
    private long _epoch;
    private bool _isRunning;
    private bool _stopRequested;
    private bool _disposed;

    /// <summary>
    /// Executes <paramref name="ctx"/> until all nodes complete <see cref="WorkContext.Cycles"/> work units.
    /// </summary>
    public void Run(WorkContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ValidateContext(ctx);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
                throw new InvalidOperationException("Scheduler is already running.");

            _isRunning = true;
        }

        try
        {
            int threadCount = ctx.ThreadCount;
            EnsurePool(threadCount, ctx.ThreadAffinity);

            var run = new RunState(ctx);
            _doneSignal.Reset();

            lock (_sync)
            {
                _currentRun = run;
                _epoch++;
                Monitor.PulseAll(_sync);
            }

            _doneSignal.Wait();
            run.ThrowIfFaulted();
        }
        finally
        {
            lock (_sync)
            {
                _currentRun = null;
                _isRunning = false;
            }
        }
    }

    private static void ValidateContext(WorkContext ctx)
    {
        if (ctx.ThreadCount <= 0
            || ctx.NodeArray is null || ctx.NodeArray.Length <= 0
            || ctx.Tasks is null || ctx.Tasks.Length < ctx.NodeArray.Length
            || ctx.Edges is null
            || ctx.Offsets is null || ctx.Offsets.Length != ctx.NodeArray.Length + 1
            || ctx.ParentEdges is null
            || ctx.ParentOffsets is null || ctx.ParentOffsets.Length != ctx.NodeArray.Length + 1
            || ctx.ThreadSchedules is null || ctx.ThreadSchedules.Length < ctx.ThreadCount)
        {
            throw new ArgumentException("WorkContext is invalid or uninitialized.", nameof(ctx));
        }
    }

    private void EnsurePool(int threadCount, bool threadAffinity)
    {
        if (_workers != null && _poolThreadCount == threadCount && _poolThreadAffinity == threadAffinity)
            return;

        ShutdownPool();

        _poolThreadCount = threadCount;
        _poolThreadAffinity = threadAffinity;
        _workers = new Thread[threadCount];

        long startEpoch;
        lock (_sync)
        {
            startEpoch = _epoch;
        }

        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            var thread = new Thread(() => WorkerLoop(threadIndex, threadAffinity, startEpoch))
            {
                IsBackground = true,
                Name = $"deltalag-worker-{threadIndex}",
            };
            _workers[t] = thread;
            thread.Start();
        }
    }

    private void WorkerLoop(int threadIndex, bool threadAffinity, long startEpoch)
    {
        bool pinned = false;
        if (threadAffinity)
        {
            pinned = ThreadAffinity.TryPinCurrentThread(threadIndex);
        }

        try
        {
            long seenEpoch = startEpoch;
            while (true)
            {
                RunState? run;
                lock (_sync)
                {
                    while (!_stopRequested && _epoch == seenEpoch)
                    {
                        Monitor.Wait(_sync);
                    }

                    if (_stopRequested)
                        return;

                    seenEpoch = _epoch;
                    run = _currentRun;
                }

                if (run != null)
                {
                    ExecuteWorker(threadIndex, run);
                }
            }
        }
        finally
        {
            if (pinned)
                Thread.EndThreadAffinity();
        }
    }

    private void ExecuteWorker(int threadIndex, RunState run)
    {
        try
        {
            int cycles = run.Cycles;
            int slack = run.Slack;
            int[] localSchedule = run.ThreadSchedules[threadIndex];
            var nodeArray = run.NodeArray;
            var edges = run.Edges;
            var offsets = run.Offsets;
            var parentEdgesArray = run.ParentEdges;
            var parentOffsetsArray = run.ParentOffsets;
            var tasks = run.Tasks;

            bool allDone = false;
            while (!allDone && !run.Faulted)
            {
                allDone = true;

                for (int si = 0; si < localSchedule.Length && !run.Faulted; si++)
                {
                    int i = localSchedule[si];
                    long myWork = nodeArray[i].WorkDone;
                    if (myWork >= cycles) continue;

                    allDone = false;

                    // CanFire gates on neighbors' WorkDone, while TryClaimWork CASes WorkClaimed;
                    // this lets multiple threads poll the same node without double-executing a cycle,
                    // and keeps dependents blocked until PublishWorkDone signals task completion.
                    long workSnap = Helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);
                    if (workSnap != -1 && workSnap < cycles && nodeArray[i].TryClaimWork(workSnap))
                    {
                        tasks[i]?.Invoke((int)workSnap);
                        nodeArray[i].PublishWorkDone(workSnap + 1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            run.RecordException(ex);
        }
        finally
        {
            if (Interlocked.Decrement(ref run.Remaining) == 0)
            {
                _doneSignal.Set();
            }
        }
    }

    private void ShutdownPool()
    {
        if (_workers == null)
            return;

        lock (_sync)
        {
            _stopRequested = true;
            Monitor.PulseAll(_sync);
        }

        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].Join();
        }

        lock (_sync)
        {
            _stopRequested = false;
            _currentRun = null;
        }

        _workers = null;
        _poolThreadCount = 0;
    }

    /// <summary>
    /// Stops and joins pooled worker threads and releases synchronization primitives.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_isRunning)
                throw new InvalidOperationException("Cannot dispose Scheduler while a run is in progress.");

            _disposed = true;
        }

        ShutdownPool();
        _doneSignal.Dispose();
    }
}
