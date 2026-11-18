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
        public int Cycles;
        public int Slack;
        public int ThreadCount;
        public int[][] ThreadSchedules;

        public int MaxEdgeHops { get; set; }
    }

    public void Run(WorkContext ctx)
    {
        int cycles = ctx.Cycles;
        int slack = ctx.Slack;
        int threadCount = ctx.ThreadCount;
        int[][] threadSchedules = ctx.ThreadSchedules;
        var nodeArray = ctx.NodeArray;
        var edges = ctx.Edges;
        var offsets = ctx.Offsets;
        var parentEdgesArray = ctx.ParentEdges;
        var parentOffsetsArray = ctx.ParentOffsets;
        var tasks = ctx.Tasks;

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
        });
    }
}
