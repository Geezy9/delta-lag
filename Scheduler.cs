namespace revolver;

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

                helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);
                var parentEdgesArray = parentEdges.ToArray();
                var parentOffsetsArray = parentOffsets.ToArray();
                int nodeCount = nodes.Count;

                int threadCount = Environment.ProcessorCount;
                int chunkSize = nodeCount / threadCount;
                var nodeArray = nodes.ToArray();

                // Instead of for(c < cycles), we run one long-lived parallel block
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

                            allDone = false; // We found work, so we aren't done yet

                            // 2. CanFire now acts as the gatekeeper. 
                            // It naturally handles the "cycle" logic because it won't 
                            // fire if parents haven't finished their current version.
                            int workSnap = helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);

                            if (workSnap != -1)
                            {
                                nodeArray[i].Task?.Invoke();
                                nodeArray[i].TryIncrementWorkDone(workSnap);
                            }
                        }
                        // Optional: Thread.Yield() or a tiny spin here if you want to be nice to the CPU
                    }
                });
                break;

            default:
                throw new ArgumentException($"Unknown algorithm: {algo}");
        }
    }
}
