namespace deltalag;

/// <summary>
/// Provides methods to execute scheduling algorithms on a directed graph for a specified number of cycles and threads.
/// </summary>
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

    public void Run(WorkContext ctx, int cycles, int slack, int threads)
    {
        var nodeArray = ctx.NodeArray;
        var edges = ctx.Edges;
        var offsets = ctx.Offsets;
        var parentEdgesArray = ctx.ParentEdges;
        var parentOffsetsArray = ctx.ParentOffsets;
        int nodeCount = nodeArray.Length;

        // Trust user if they give a positive, reasonable value
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

        // nodes get chunked and passed out. threads loop until their chunks min work is == cycles.
        Parallel.For(0, threadCount, t =>
        {
            int start = t * chunkSize;
            int end = (t == threadCount - 1) ? nodeCount : start + chunkSize;
            bool allDone = false;

            while (!allDone)
            {
                allDone = true;
                for (int i = start; i < end; i++)
                {

                    int myWork = nodeArray[i].WorkDone;
                    if (myWork >= cycles) continue;

                    allDone = false;
                    // workSnap is the cycle we can work on. if it's -1, we can't work. if it's < cycles, we can work. if it's >= cycles, we can't work.
                    int workSnap = helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);
                    if (workSnap != -1 && workSnap < cycles && nodeArray[i].TryClaimWork(workSnap))
                    {
                        nodeArray[i].Task?.Invoke(workSnap);
                        nodeArray[i].PublishWorkDone(workSnap + 1);
                    }
                }
            }
        });
    }
}
