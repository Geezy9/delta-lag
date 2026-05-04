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

                for (int c = 0; c < cycles; c++)
                {
                    Parallel.For(0, threadCount, t =>
                    {
                        int start = t * chunkSize;
                        int end = (t == threadCount - 1) ? nodeCount : start + chunkSize;

                        for (int i = start; i < end; i++)
                        {
                            int myWork = helpers.CanFire(i, slack, nodeArray, edges, offsets, parentEdgesArray, parentOffsetsArray);
                            if (myWork == -1)
                                continue;

                            try
                            {
                                nodeArray[i].Task?.Invoke();
                                nodeArray[i].TryIncrementWorkDone(myWork);
                            }
                            catch {
                                throw new Exception($"node {i} Task or Counter Threw");
                            }

                        }
                    });
                }
                break;

            default:
                throw new ArgumentException($"Unknown algorithm: {algo}");
        }
    }
}
