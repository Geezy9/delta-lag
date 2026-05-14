namespace deltalag;

public enum PartitionStrategy
{
    ChunkedLinear,
    Stride,
}

public class Scheduler
{
    public struct WorkContext
    {
        public Graph Graph;
        public Node[] NodeArray;
        public int[] Edges;
        public int[] Offsets;
        public int[] ParentEdges;
        public int[] ParentOffsets;
    }

    private int _doneThreads;
    private int _barrierGeneration;

    public void Run(WorkContext ctx, int cycles, int slack, int threads, PartitionStrategy strategy)
    {
        var nodeArray = ctx.NodeArray;
        var edges = ctx.Edges;
        var offsets = ctx.Offsets;
        var parentEdgesArray = ctx.ParentEdges;
        var parentOffsetsArray = ctx.ParentOffsets;
        int nodeCount = nodeArray.Length;

        bool trustUser = threads > 0 && threads <= Environment.ProcessorCount * 2;
        int threadCount;
        if (trustUser)
        {
            threadCount = Math.Min(threads, Environment.ProcessorCount);
        }
        else
        {
            threadCount = Math.Min(Environment.ProcessorCount, Math.Max(1, nodeCount / 4));
        }
        int chunkSize = nodeCount / threadCount;

        Parallel.For(0, threadCount, t =>
        {
            int localStart = 0;
            int localEnd = 0;
            int localStep = 1;

            switch (strategy)
            {
                case PartitionStrategy.ChunkedLinear:
                    localStart = t * chunkSize;
                    localEnd = (t == threadCount - 1) ? nodeCount : localStart + chunkSize;
                    break;

                case PartitionStrategy.Stride:
                    localStart = t;
                    localEnd = nodeCount;
                    localStep = threadCount;
                    break;
            }

            bool allDone = false;
            while (!allDone)
            {
                allDone = true;

                for (int i = localStart; i < localEnd; i += localStep)
                {
                    int myWork = nodeArray[i].WorkDone;
                    if (myWork >= cycles) continue;

                    allDone = false;

                    int workSnap = helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);
                    if (workSnap != -1 && workSnap < cycles && nodeArray[i].TryClaimWork(workSnap))
                    {
                        nodeArray[i].Task?.Invoke(workSnap);
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
