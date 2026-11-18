namespace deltalag;

/// <summary>
/// Rewrites high-degree vertices into balanced k-ary trees of no-op relay nodes so that
/// <see cref="Helpers.CanFire"/> never has to scan unbounded parent/child lists on every poll.
/// Original node IDs [0..originalNodeCount) are preserved in place; relays are appended after
/// them so <see cref="GraphBuilder"/> can map tasks by index without a translation table.
/// </summary>
internal static class KaryInjector
{
    internal static Scheduler.WorkContext InjectKaryTree(Graph graph, Config con)
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
                MaxEdgeHops = 1,
            };

        int k = Math.Max(2, con.K);
        int fanOutLimit = NormalizeLimit(con.MaxFanOut, nameof(con.MaxFanOut));
        int fanInLimit = NormalizeLimit(con.MaxFanIn, nameof(con.MaxFanIn));
        // Clamp branching factor to the degree limit so newly injected relays also obey the cap
        // (each relay has 1 edge on its root side and <= branchFactor edges on its leaf side).
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
                MaxEdgeHops = 1,
            };
        }

        var adjacency = BuildAdjacency(sourceGraph);

        // Pass order matters: ReduceFanOut adds relays with in-degree 1 without changing any
        // child's in-degree, and ReduceFanIn adds relays with out-degree 1 without increasing
        // any parent's out-degree, so neither pass invalidates the other's degree bound.
        ReduceFanOut(adjacency, originalNodeCount, fanOutLimit, fanOutBranch);
        ReduceFanIn(adjacency, fanInLimit, fanInBranch);

        Graph optimized = BuildGraph(adjacency);
        Helpers.BuildReverseCSR(optimized, out var parentEdges, out var parentOffsets);
        int maxEdgeHops = ComputeMaxEdgeHops(optimized, originalNodeCount);

        return new Scheduler.WorkContext
        {
            NodeArray = new Node[optimized.NodeCount],
            Edges = optimized.edges.ToArray(),
            Offsets = optimized.offsets.ToArray(),
            ParentEdges = parentEdges.ToArray(),
            ParentOffsets = parentOffsets.ToArray(),
            MaxEdgeHops = maxEdgeHops,
        };
    }

    private static int NormalizeLimit(int configuredLimit, string paramName)
    {
        if (configuredLimit == 1)
            throw new ArgumentOutOfRangeException(paramName, "Degree limit must be >= 2 (or <= 0 for unlimited).");

        return configuredLimit <= 0 ? int.MaxValue : configuredLimit;
    }

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

            // Build the tree bottom-up, collapsing groups of branchFactor until the root's
            // direct children fit within branchFactor (which is <= fanOutLimit).
            while (level.Count > branchFactor)
            {
                var nextLevel = new List<int>((level.Count + branchFactor - 1) / branchFactor);

                for (int i = 0; i < level.Count; i += branchFactor)
                {
                    int count = Math.Min(branchFactor, level.Count - i);
                    if (count == 1)
                    {
                        // Avoid creating a 1-to-1 passthrough relay for a lone remainder.
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

        // Snapshot inbound parents before injecting relays; newly added fan-in relays have
        // in-degree <= branchFactor <= fanInLimit, so only pre-existing nodes need reduction.
        List<List<int>> parents = BuildParents(adjacency);

        for (int target = 0; target < parents.Count; target++)
        {
            List<int> inboundParents = parents[target];
            if (inboundParents.Count <= fanInLimit)
                continue;

            // Detach direct edges first (one per inboundParents entry to preserve multiplicity)
            // before wiring the bottom-up reduction tree into target.
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
                        // Promote lone remainder directly rather than inserting a 1-to-1 relay.
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

    /// <summary>
    /// Computes the longest hop count between any two original nodes across injected relay chains.
    /// Every relay node adds 1 hop of pipeline lag before WorkDone propagates to the original child.
    /// </summary>
    private static int ComputeMaxEdgeHops(Graph graph, int originalNodeCount)
    {
        int totalNodes = graph.NodeCount;
        int injectedCount = totalNodes - originalNodeCount;
        if (injectedCount <= 0)
            return 1;

        // Every injected node has >= 1 outgoing path to an original node, so its relay depth
        // is always >= 1; 0 is therefore a safe sentinel for unvisited.
        var memo = new int[injectedCount];

        int GetRelayDepth(int node)
        {
            if (node < originalNodeCount)
                return 0;

            int idx = node - originalNodeCount;
            if (memo[idx] != 0)
                return memo[idx];

            int maxChildDepth = 0;
            for (int ei = graph.offsets[node]; ei < graph.offsets[node + 1]; ei++)
            {
                int child = graph.edges[ei];
                int d = 1 + GetRelayDepth(child);
                if (d > maxChildDepth)
                    maxChildDepth = d;
            }

            memo[idx] = maxChildDepth;
            return maxChildDepth;
        }

        int maxHops = 1;
        for (int u = 0; u < originalNodeCount; u++)
        {
            for (int ei = graph.offsets[u]; ei < graph.offsets[u + 1]; ei++)
            {
                int child = graph.edges[ei];
                int hops = 1 + GetRelayDepth(child);
                if (hops > maxHops)
                    maxHops = hops;
            }
        }

        return maxHops;
    }
}
