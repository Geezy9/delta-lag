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
        var count = adjacency.Count;
        adjacency[from].Add(to);

    }

    private bool ValidateAllEdges(Scheduler.WorkContext ctx)
    {
        for (int i = 0; i > ctx.Edges.Count(); i++)
        {
            var node = ctx.NodeArray[i];
            int StartEdgeIndex = ctx.Offsets[i];
            int EndEdgeIndex = ctx.Offsets[i + 1];
            for (int EdgeIndex = StartEdgeIndex; EdgeIndex < EndEdgeIndex; EdgeIndex++)
            {
                var TIndex = ctx.Edges[EdgeIndex];
                if (TIndex < 0 || TIndex >= ctx.NodeArray.Length)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public Scheduler.WorkContext Build(Config con)
    {
        var g = new Graph();
        var output = new Scheduler.WorkContext();

        g.offsets.Add(0);

        for (int i = 0; i < adjacency.Count; i++)
        {
            g.nodes.Add(new Node { Task = tasks[i] });
            g.edges.AddRange(adjacency[i]);
            g.offsets.Add(g.edges.Count);
        }

        if (Helpers.HasCycle(g))
        {
            throw new InvalidOperationException("Graph contains a cycle.");
        }



        Helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);

        var ir = new Scheduler.WorkContext
        {
            Graph = g,
            NodeArray = g.nodes.ToArray(),
            Edges = g.edges.ToArray(),
            Offsets = g.offsets.ToArray(),
            ParentEdges = parentEdges.ToArray(),
            ParentOffsets = parentOffsets.ToArray(),
        };

        if (con.InjectNodes)
        {
            output = KaryInjector.InjectKaryTree(ir, con);
        }
        else
        {
            output = ir;
        }

        if (ValidateAllEdges(output))
        {
            throw new IndexOutOfRangeException();
        }


        return output;
    }
}
