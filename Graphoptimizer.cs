namespace deltalag;

public static class KaryInjector
{
    public static Scheduler.WorkContext InjectKaryTree(Graph graph, Config con)
    {
        if (graph is null)
            throw new ArgumentNullException(nameof(graph), "Graph cannot be null.");

        Graph sourceGraph = graph;
        int originalNodeCount = sourceGraph.NodeCount;
        if (originalNodeCount == 0)
            return new Scheduler.WorkContext
            {
                NodeArray = Array.Empty<Node>(),
                Edges = Array.Empty<int>(),
                Offsets = new[] { 0 },
                ParentEdges = Array.Empty<int>(),
                ParentOffsets = new[] { 0 },
            };

        int k = Math.Max(2, con.K);
        int fanOutLimit = NormalizeLimit(con.MaxFanOut);
        int fanInLimit = NormalizeLimit(con.MaxFanIn);
        int fanOutBranch = Math.Min(k, fanOutLimit);
        int fanInBranch = Math.Min(k, fanInLimit);

        if (!RequiresInjection(sourceGraph, fanOutLimit, fanInLimit))
        {
            Helpers.BuildReverseCSR(sourceGraph, out var sourceParentEdges, out var sourceParentOffsets);

            return new Scheduler.WorkContext
            {
                NodeArray = new Node[originalNodeCount],
                Edges = sourceGraph.edges.ToArray(),
                Offsets = sourceGraph.offsets.ToArray(),
                ParentEdges = sourceParentEdges.ToArray(),
                ParentOffsets = sourceParentOffsets.ToArray(),
            };
        }

        var adjacency = BuildAdjacency(sourceGraph);

        ReduceFanOut(adjacency, originalNodeCount, fanOutLimit, fanOutBranch);
        ReduceFanIn(adjacency, fanInLimit, fanInBranch);

        Graph optimized = BuildGraph(adjacency);
        Helpers.BuildReverseCSR(optimized, out var parentEdges, out var parentOffsets);

        return new Scheduler.WorkContext
        {
            NodeArray = new Node[optimized.NodeCount],
            Edges = optimized.edges.ToArray(),
            Offsets = optimized.offsets.ToArray(),
            ParentEdges = parentEdges.ToArray(),
            ParentOffsets = parentOffsets.ToArray(),
        };
    }

    private static int NormalizeLimit(int configuredLimit) => configuredLimit <= 0 ? int.MaxValue : configuredLimit;

    private static bool RequiresInjection(Graph graph, int fanOutLimit, int fanInLimit)
    {
        int nodeCount = graph.NodeCount;

        if (fanOutLimit != int.MaxValue)
        {
            for (int node = 0; node < nodeCount; node++)
            {
                int outDegree = graph.offsets[node + 1] - graph.offsets[node];
                if (outDegree > fanOutLimit)
                    return true;
            }
        }

        if (fanInLimit == int.MaxValue)
            return false;

        int[] inDegree = new int[nodeCount];
        for (int parent = 0; parent < nodeCount; parent++)
        {
            for (int ei = graph.offsets[parent]; ei < graph.offsets[parent + 1]; ei++)
            {
                int child = graph.edges[ei];
                inDegree[child]++;
                if (inDegree[child] > fanInLimit)
                    return true;
            }
        }

        return false;
    }

    private static List<List<int>> BuildAdjacency(Graph graph)
    {
        int nodeCount = graph.NodeCount;
        var adjacency = new List<List<int>>(nodeCount);

        for (int node = 0; node < nodeCount; node++)
        {
            int start = graph.offsets[node];
            int end = graph.offsets[node + 1];
            int edgeCount = end - start;

            var edges = new List<int>(edgeCount);
            for (int ei = start; ei < end; ei++)
                edges.Add(graph.edges[ei]);

            adjacency.Add(edges);
        }

        return adjacency;
    }

    private static void ReduceFanOut(
        List<List<int>> adjacency,
        int originalNodeCount,
        int fanOutLimit,
        int branchFactor)
    {
        if (fanOutLimit == int.MaxValue)
            return;

        for (int parent = 0; parent < originalNodeCount; parent++)
        {
            List<int> children = adjacency[parent];
            if (children.Count <= fanOutLimit)
                continue;

            var level = new List<int>(children);
            children.Clear();

            while (level.Count > branchFactor)
            {
                var nextLevel = new List<int>((level.Count + branchFactor - 1) / branchFactor);

                for (int i = 0; i < level.Count; i += branchFactor)
                {
                    int count = Math.Min(branchFactor, level.Count - i);
                    if (count == 1)
                    {
                        nextLevel.Add(level[i]);
                        continue;
                    }

                    int injected = AddInjectedNode(adjacency);
                    List<int> injectedChildren = adjacency[injected];
                    for (int j = 0; j < count; j++)
                        injectedChildren.Add(level[i + j]);

                    nextLevel.Add(injected);
                }

                level = nextLevel;
            }

            children.AddRange(level);
        }
    }

    private static void ReduceFanIn(
        List<List<int>> adjacency,
        int fanInLimit,
        int branchFactor)
    {
        if (fanInLimit == int.MaxValue)
            return;

        List<List<int>> parents = BuildParents(adjacency);

        for (int target = 0; target < parents.Count; target++)
        {
            List<int> inboundParents = parents[target];
            if (inboundParents.Count <= fanInLimit)
                continue;

            for (int i = 0; i < inboundParents.Count; i++)
                RemoveEdge(adjacency[inboundParents[i]], target);

            var level = new List<int>(inboundParents);

            while (level.Count > branchFactor)
            {
                var nextLevel = new List<int>((level.Count + branchFactor - 1) / branchFactor);

                for (int i = 0; i < level.Count; i += branchFactor)
                {
                    int count = Math.Min(branchFactor, level.Count - i);
                    if (count == 1)
                    {
                        nextLevel.Add(level[i]);
                        continue;
                    }

                    int injected = AddInjectedNode(adjacency);
                    for (int j = 0; j < count; j++)
                        adjacency[level[i + j]].Add(injected);

                    nextLevel.Add(injected);
                }

                level = nextLevel;
            }

            for (int i = 0; i < level.Count; i++)
                adjacency[level[i]].Add(target);
        }
    }

    private static List<List<int>> BuildParents(List<List<int>> adjacency)
    {
        int nodeCount = adjacency.Count;
        var parents = new List<List<int>>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
            parents.Add(new List<int>());

        for (int parent = 0; parent < nodeCount; parent++)
        {
            List<int> children = adjacency[parent];
            for (int i = 0; i < children.Count; i++)
                parents[children[i]].Add(parent);
        }

        return parents;
    }

    private static void RemoveEdge(List<int> edges, int target)
    {
        for (int i = edges.Count - 1; i >= 0; i--)
        {
            if (edges[i] == target)
            {
                edges.RemoveAt(i);
                return;
            }
        }
    }

    private static int AddInjectedNode(List<List<int>> adjacency)
    {
        int index = adjacency.Count;
        adjacency.Add(new List<int>());
        return index;
    }

    private static Graph BuildGraph(List<List<int>> adjacency)
    {
        var graph = new Graph { NodeCount = adjacency.Count };

        graph.offsets.Add(0);
        int edgeCount = 0;
        for (int i = 0; i < adjacency.Count; i++)
            edgeCount += adjacency[i].Count;

        if (edgeCount > 0)
            graph.edges.Capacity = edgeCount;

        for (int i = 0; i < adjacency.Count; i++)
        {
            graph.edges.AddRange(adjacency[i]);
            graph.offsets.Add(graph.edges.Count);
        }

        return graph;
    }
}
