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
        adjacency[from].Add(to);
    }

    public Scheduler.WorkContext Build()
    {
        var g = new Graph();

        g.offsets.Add(0);

        for (int i = 0; i < adjacency.Count; i++)
        {
            g.nodes.Add(new Node { Task = tasks[i] });
            g.edges.AddRange(adjacency[i]);
            g.offsets.Add(g.edges.Count);
        }

        helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);

        return new Scheduler.WorkContext
        {
            Graph = g,
            NodeArray = g.nodes.ToArray(),
            Edges = g.edges.ToArray(),
            Offsets = g.offsets.ToArray(),
            ParentEdges = parentEdges.ToArray(),
            ParentOffsets = parentOffsets.ToArray(),
        };
    }
}
