using System.Runtime.CompilerServices;

namespace deltalag;

internal static class Helpers
{
    internal static void BuildReverseCSR(Graph g, out int[] parentEdges, out int[] parentOffsets)
    {
        int n = g.NodeCount;

        parentOffsets = new int[n + 1];
        parentEdges = new int[g.edges.Count];

        // Count in-degrees
        for (int u = 0; u < n; u++)
        {
            for (int ei = g.offsets[u]; ei < g.offsets[u + 1]; ei++)
            {
                int v = g.edges[ei];
                parentOffsets[v + 1]++;
            }
        }

        // Prefix sum
        for (int i = 1; i <= n; i++)
            parentOffsets[i] += parentOffsets[i - 1];

        // Fill reverse edges
        int[] cursor = (int[])parentOffsets.Clone();

        for (int u = 0; u < n; u++)
        {
            for (int ei = g.offsets[u]; ei < g.offsets[u + 1]; ei++)
            {
                int v = g.edges[ei];
                parentEdges[cursor[v]++] = u;
            }
        }
    }

    internal static bool HasCycle(Graph g)
    {
        int n = g.NodeCount;
        int[] inDegree = new int[n];

        for (int u = 0; u < n; u++)
        {
            for (int ei = g.offsets[u]; ei < g.offsets[u + 1]; ei++)
            {
                int v = g.edges[ei];
                inDegree[v]++;
            }
        }

        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (inDegree[i] == 0)
                queue.Enqueue(i);
        }

        int visited = 0;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            visited++;

            for (int ei = g.offsets[u]; ei < g.offsets[u + 1]; ei++)
            {
                int v = g.edges[ei];
                inDegree[v]--;
                if (inDegree[v] == 0)
                    queue.Enqueue(v);
            }
        }

        return visited != n;
    }



    // Returns the observed WorkDone value if the node can fire, or -1 if it cannot.
    // Returning the snapshot lets the caller use it directly in a CAS to prevent double-firing.
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    internal static long CanFire(
        int i,
        int slack,
        // Use an array instead of a List to keep data contiguous in memory
        Node[] nodes,
        int[] edges,
        int[] offsets,
        int[] parentEdges,
        int[] parentOffsets)
    {
        // Fetch work once
        ref var nodeRef = ref nodes[i];
        long myWork = nodeRef.WorkDone;

        long minParentWork = myWork + 1;
        long minChildWork = myWork - slack;

        int parentStart = parentOffsets[i];
        int parentEnd = parentOffsets[i + 1];
        int childStart = offsets[i];
        int childEnd = offsets[i + 1];

        // Scan whichever neighbor list is shorter first to fail fast on high-fanout/fanin nodes.
        int pLen = parentEnd - parentStart;
        int cLen = childEnd - childStart;

        if (pLen <= cLen)
        {
            for (int k = parentStart; k < parentEnd; k++)
            {
                // All parents must be at least 1 cycle ahead of this node.
                if (nodes[parentEdges[k]].WorkDone < minParentWork)
                    return -1;
            }

            for (int k = childStart; k < childEnd; k++)
            {
                // Children cannot lag this node by more than `slack` cycles (backpressure).
                if (nodes[edges[k]].WorkDone < minChildWork)
                    return -1;
            }
        }
        else
        {
            for (int k = childStart; k < childEnd; k++)
            {
                
                if (nodes[edges[k]].WorkDone < minChildWork)
                    return -1;
            }

            for (int k = parentStart; k < parentEnd; k++)
            {
                if (nodes[parentEdges[k]].WorkDone < minParentWork)
                    return -1;
            }
        }

        return myWork;
    }
}
