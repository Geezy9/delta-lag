namespace deltalag;

public class GraphBuilder
{
    private readonly List<List<int>> adjacency = new();
    private readonly List<Action<int>?> tasks = new();

    public int AddNode(Action<int>? task = null)
    {
        adjacency.Add(new List<int>());
        tasks.Add(task);
        return adjacency.Count - 1;
    }

    public void AddEdge(int from, int to)
    {
        if ((uint)from >= (uint)adjacency.Count)
            throw new ArgumentOutOfRangeException(nameof(from));

        if ((uint)to >= (uint)adjacency.Count)
            throw new ArgumentOutOfRangeException(nameof(to));

        adjacency[from].Add(to);

    }

    private bool ValidateAllEdgeRanges(Scheduler.WorkContext ctx)
    {
        for (int i = 0; i < ctx.NodeArray.Length; i++)
        {
            int startEdgeIndex = ctx.Offsets[i];
            int endEdgeIndex = ctx.Offsets[i + 1];
            for (int edgeIndex = startEdgeIndex; edgeIndex < endEdgeIndex; edgeIndex++)
            {
                int targetIndex = ctx.Edges[edgeIndex];
                if (targetIndex < 0 || targetIndex >= ctx.NodeArray.Length)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public Scheduler.WorkContext Build(Config con)
    {
        var g = new Graph { NodeCount = adjacency.Count };

        g.offsets.Add(0);

        for (int i = 0; i < adjacency.Count; i++)
        {
            g.edges.AddRange(adjacency[i]);
            g.offsets.Add(g.edges.Count);
        }

        if (Helpers.HasCycle(g))
        {
            throw new InvalidOperationException("Graph contains a cycle.");
        }

        Scheduler.WorkContext output;
        if (con.InjectNodes)
        {
            output = KaryInjector.InjectKaryTree(g, con);
        }
        else
        {
            Helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);

            output = new Scheduler.WorkContext
            {
                NodeArray = new Node[g.NodeCount],
                Edges = g.edges.ToArray(),
                Offsets = g.offsets.ToArray(),
                ParentEdges = parentEdges.ToArray(),
                ParentOffsets = parentOffsets.ToArray(),
            };
        }

        if (!ValidateAllEdgeRanges(output))
        {
            throw new IndexOutOfRangeException();
        }

        // Injected nodes are appended after the originals, so indices line up; their entries stay null.
        var taskArray = new Action<int>?[output.NodeArray.Length];
        tasks.CopyTo(taskArray);
        output.Tasks = taskArray;

        return output;
    }
}
