using System.Runtime.CompilerServices;
namespace deltalag;

internal class helpers
{
    public int[] inDegree(Graph g)
    {
        var nodes = g.nodes;
        int[] inDegree = new int[nodes.Count];
        for (int i = 0; i < inDegree.Length; i++)
        {
            for (int ei = g.offsets[i]; ei < g.offsets[i + 1]; ei++)
            {
                int to = g.edges[ei];
                inDegree[to]++;
            }
        }
        return inDegree;

    }
    public int[] Plink(Graph g)
    {
        int l = g.nodes.Count;
        int[] parent = Enumerable.Repeat(-1, l).ToArray();

        for (int i = 0; i < l; i++)
        {

            for (int ei = g.offsets[i]; ei < g.offsets[i + 1]; ei++)
            {
                int v = g.edges[ei];

                if (parent[v] == -1)
                    parent[v] = i;
                else
                    parent[v] = -2;
            }
        }

        return parent;
    }
    public int[] outDegree(Graph g)
    {
        int l = g.nodes.Count;
        int[] outDeg = new int[l];

        for (int i = 0; i < l; i++)
            outDeg[i] = g.offsets[i + 1] - g.offsets[i];

        return outDeg;
    }
    public static void BuildReverseCSR(Graph g, out List<int> parentEdges, out List<int> parentOffsets)
    {
        int n = g.nodes.Count;

        parentOffsets = new List<int>(new int[n + 1]);
        parentEdges = new List<int>(g.edges.Count);

        // Count In-degrees
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

        // Allocate space
        for (int i = 0; i < g.edges.Count; i++)
            parentEdges.Add(0);

        // Fill reverse edges
        int[] cursor = parentOffsets.ToArray();

        for (int u = 0; u < n; u++)
        {
            for (int ei = g.offsets[u]; ei < g.offsets[u + 1]; ei++)
            {
                int v = g.edges[ei];
                parentEdges[cursor[v]++] = u;
            }
        }
    }


    // Returns the observed WorkDone value if the node can fire, or -1 if it cannot.
    // Returning the snapshot lets the caller use it directly in a CAS to prevent double-firing.
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public static int CanFire(
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
        int myWork = nodeRef.WorkDone;

        int minParentWork = myWork + 1;
        int minChildWork = myWork - slack;

        int parentStart = parentOffsets[i];
        int parentEnd = parentOffsets[i + 1];
        int childStart = offsets[i];
        int childEnd = offsets[i + 1];

        // Unroll the logic slightly to eliminate branching overheads
        int pLen = parentEnd - parentStart;
        int cLen = childEnd - childStart;

        if (pLen <= cLen)
        {
            // Check parents first, since they are more likely to block firing
            for (int k = parentStart; k < parentEnd; k++)
            {
                // all parents must be ahead by at least 1 unit of work
                if (nodes[parentEdges[k]].WorkDone < minParentWork)
                    return -1;
            }

            for (int k = childStart; k < childEnd; k++)
            {
                // all children must be behind by at most slack units of work
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









