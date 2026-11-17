namespace deltalag;

public enum PartitionStrategy
{
    ChunkedLinear,
    Stride,
    Clustered,
    Leveled,
    DepthFirst,
}

public class Scheduler
{
    public ref struct WorkContext
    {
        public Node[] NodeArray;
        public int[] Edges;
        public int[] Offsets;
        public int[] ParentEdges;
        public int[] ParentOffsets;
        public Action<int>?[] Tasks;
    }

    private int _doneThreads;
    private int _barrierGeneration;

    public void Run(WorkContext ctx, Config config)
    {
        int cycles = config.Cycles;
        int slack = config.Slack;
        int threads = config.Threads;
        PartitionStrategy strategy = config.Strategy;
        bool seperateRootThread = config.SeperateRootThread;
        var nodeArray = ctx.NodeArray;
        var edges = ctx.Edges;
        var offsets = ctx.Offsets;
        var parentEdgesArray = ctx.ParentEdges;
        var parentOffsetsArray = ctx.ParentOffsets;
        int nodeCount = nodeArray.Length;
        var tasks = ctx.Tasks;

        bool trustUser = config.Threads > 0 && config.Threads <= Environment.ProcessorCount * 2;
        int threadCount;
        if (trustUser)
        {
            threadCount = Math.Min(config.Threads, Environment.ProcessorCount);
        }
        else
        {
            threadCount = Math.Min(Environment.ProcessorCount, Math.Max(1, nodeCount / 4));
        }

        threadCount = Math.Min(threadCount, Math.Max(1, nodeCount));

        int[][] threadSchedules = ThreadSchedules.Build(
            nodeCount,
            threadCount,
            strategy,
            edges,
            offsets,
            parentEdgesArray,
            parentOffsetsArray,
            seperateRootThread);

        Parallel.For(0, threadCount, t =>
        {
            int[] localSchedule = threadSchedules[t];

            bool allDone = false;
            while (!allDone)
            {
                allDone = true;

                for (int si = 0; si < localSchedule.Length; si++)
                {
                    int i = localSchedule[si];
                    long myWork = nodeArray[i].WorkDone;
                    if (myWork >= cycles) continue;

                    allDone = false;

                    long workSnap = Helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);
                    if (workSnap != -1 && workSnap < cycles && nodeArray[i].TryClaimWork(workSnap))
                    {
                        tasks[i]?.Invoke((int)workSnap);
                        nodeArray[i].PublishWorkDone(workSnap + 1);
                    }
                }
            }

            int gen = Volatile.Read(ref _barrierGeneration);
            if (Interlocked.Increment(ref _doneThreads) == threadCount)
            {
                Volatile.Write(ref _doneThreads, 0);
                Interlocked.Increment(ref _barrierGeneration); // advance generation to release waiters
            }
            else
            {
                while (Volatile.Read(ref _barrierGeneration) == gen)
                {
                    Thread.SpinWait(5);
                }
            }
        });
    }
}
