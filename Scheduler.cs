namespace deltalag;

/// <summary>
/// Provides methods to execute scheduling algorithms on a directed graph for a specified number of cycles and threads.
/// </summary>
public class Scheduler
{
    public void Run(Graph graph, int cycles, int slack, int threads)
    {
        var g = graph;
        var nodes = g.nodes;
        var edges = g.edges.ToArray();
        var offsets = g.offsets.ToArray();

        // We need the reverse CSR to efficiently check parent completion in CanFire
        helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);
        var parentEdgesArray = parentEdges.ToArray();
        var parentOffsetsArray = parentOffsets.ToArray();
        int nodeCount = g.nodes.Count();

        // Trust user if they give a positive, reasonable value
        bool trustUser = threads > 0 && threads <= Environment.ProcessorCount * 2;
        int threadCount;
        if (trustUser)
        {
            // Clamp to CPU count to avoid oversubscription
            threadCount = Math.Min(threads, Environment.ProcessorCount);
        }
        else
        {
            // Auto mode: scale based on graph size
            threadCount = Math.Min(Environment.ProcessorCount, Math.Max(1, nodeCount / 4));
        }
        int chunkSize = nodeCount / threadCount;
        var nodeArray = nodes.ToArray();


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
                    // 1. Check if this specific node still has work to do
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
        });
               
        }
    }
}
