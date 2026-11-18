namespace deltalag;

/// <summary>
/// Incremental builder for constructing and compiling directed acyclic execution graphs.
/// </summary>
public class GraphBuilder
{
    private readonly List<List<int>> _adjacency = new();
    private readonly List<Action<int>?> _tasks = new();

    /// <summary>
    /// Adds a node and optional per-cycle work delegate to the graph.
    /// </summary>
    /// <param name="task">Optional delegate invoked with the current 0-based cycle index.</param>
    /// <returns>The 0-based node identifier.</returns>
    public int AddNode(Action<int>? task = null)
    {
        _adjacency.Add(new List<int>());
        _tasks.Add(task);
        return _adjacency.Count - 1;
    }

    /// <summary>
    /// Adds a directed dependency edge (<paramref name="from"/> -&gt; <paramref name="to"/>).
    /// </summary>
    public void AddEdge(int from, int to)
    {
        if ((uint)from >= (uint)_adjacency.Count)
            throw new ArgumentOutOfRangeException(nameof(from));

        if ((uint)to >= (uint)_adjacency.Count)
            throw new ArgumentOutOfRangeException(nameof(to));

        _adjacency[from].Add(to);
    }

    private static bool ValidateAllEdgeRanges(Scheduler.WorkContext ctx)
    {
        for (int i = 0; i < ctx.NodeArray.Length; i++)
        {
            int startEdgeIndex = ctx.Offsets[i];
            int endEdgeIndex = ctx.Offsets[i + 1];
            for (int edgeIndex = startEdgeIndex; edgeIndex < endEdgeIndex; edgeIndex++)
            {
                int targetIndex = ctx.Edges[edgeIndex];
                if ((uint)targetIndex >= (uint)ctx.NodeArray.Length)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Validates acyclicity, optionally injects k-ary relay trees, and compiles the graph and schedules into a <see cref="Scheduler.WorkContext"/>.
    /// </summary>
    public Scheduler.WorkContext Build(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(config.Cycles);
        ArgumentOutOfRangeException.ThrowIfNegative(config.Slack);

        var g = new Graph { NodeCount = _adjacency.Count };
        if (g.NodeCount <= 0)
        {
            throw new InvalidOperationException("Graph must contain at least one node.");
        }

        g.offsets.Add(0);

        for (int i = 0; i < _adjacency.Count; i++)
        {
            g.edges.AddRange(_adjacency[i]);
            g.offsets.Add(g.edges.Count);
        }

        if (Helpers.HasCycle(g))
        {
            throw new InvalidOperationException("Graph contains a cycle.");
        }

        Scheduler.WorkContext output;
        if (config.InjectNodes)
        {
            output = KaryInjector.InjectKaryTree(g, config);
        }
        else
        {
            Helpers.BuildReverseCSR(g, out var parentEdges, out var parentOffsets);

            output = new Scheduler.WorkContext
            {
                NodeArray = new Node[g.NodeCount],
                Edges = g.edges.ToArray(),
                Offsets = g.offsets.ToArray(),
                ParentEdges = parentEdges,
                ParentOffsets = parentOffsets,
                MaxEdgeHops = 1,
            };
        }

        if (!ValidateAllEdgeRanges(output))
        {
            throw new IndexOutOfRangeException("Compiled graph contains an out-of-range edge target.");
        }

        // Injected nodes are appended after the originals, so indices line up; their entries stay null.
        var taskArray = new Action<int>?[output.NodeArray.Length];
        _tasks.CopyTo(taskArray);
        output.Tasks = taskArray;

        int nodeCount = output.NodeArray.Length;
        int threadCount = config.Threads > 0
            ? Math.Min(config.Threads, Environment.ProcessorCount)
            : Math.Min(Environment.ProcessorCount, Math.Max(1, nodeCount / 4));

        threadCount = Math.Min(threadCount, nodeCount);

        output.Cycles = config.Cycles;
        output.Slack = config.Slack;
        output.ThreadCount = threadCount;
        output.ThreadAffinity = config.ThreadAffinity;
        output.ThreadSchedules = ThreadSchedules.Build(
            nodeCount,
            threadCount,
            config.Strategy,
            output.Edges,
            output.Offsets,
            output.ParentEdges,
            output.ParentOffsets,
            config.SeparateRootThread,
            config.SeparateSinkThread);

        return output;
    }
}
