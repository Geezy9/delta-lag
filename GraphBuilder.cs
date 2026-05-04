namespace deltalag;

public class GraphBuilder
{
    private readonly List<List<int>> adjacency = new();
    private readonly List<Action?> tasks = new();

    public int AddNode(Action? task = null)
    {
        adjacency.Add(new List<int>());
        tasks.Add(task);
        return adjacency.Count - 1;
    }

    public void AddEdge(int from, int to)
    {
        adjacency[from].Add(to);
    }

    public Graph Build()
    {
        var g = new Graph();

        g.offsets.Add(0);

        for (int i = 0; i < adjacency.Count; i++)
        {
            g.nodes.Add(new Node { Task = tasks[i] });
            g.edges.AddRange(adjacency[i]);
            g.offsets.Add(g.edges.Count);
        }

        return g;
    }
}
