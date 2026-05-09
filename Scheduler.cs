namespace deltalag;

/// <summary>
/// Provides methods to execute scheduling algorithms on a directed graph for a specified number of cycles.
/// </summary>
public class Scheduler
{
    public void Run(Graph graph, int cycles, int slack, string algo)
    {
        switch (algo)
        {
            case "Wave":
                
                var g = graph;
                var nodes = g.nodes;
                var edges = g.edges.ToArray();
                var offsets = g.offsets.ToArray();

                // We need the reverse CSR to efficiently check parent completion in CanFire
                helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);
                var parentEdgesArray = parentEdges.ToArray();
                var parentOffsetsArray = parentOffsets.ToArray();
                int nodeCount = nodes.Count;
                int threadCount = Environment.ProcessorCount;
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
                break;

            default:
                throw new ArgumentException($"Unknown algorithm: {algo}");
        }
    }
}
