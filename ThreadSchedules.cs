namespace deltalag;

internal static class ThreadSchedules
{
    internal static int[][] Build(
        int nodeCount,
        int threadCount,
        PartitionStrategy strategy,
        int[] edges,
        int[] offsets,
        int[] parentEdges,
        int[] parentOffsets,
        bool separateRootThread,
        bool separateSinkThread)
    {
        int reserved = (separateRootThread ? 1 : 0) + (separateSinkThread ? 1 : 0);
        if (reserved > 0 && threadCount - reserved < 1)
        {
            throw new InvalidOperationException(
                $"Thread count ({threadCount}) is insufficient for the requested dedicated threads ({reserved}); at least 1 general worker thread is required.");
        }

        if (threadCount <= 1)
            return new[] { Enumerable.Range(0, nodeCount).ToArray() };

        int[][] schedules = new int[threadCount][];
        var isDedicated = new bool[nodeCount];

        if (separateRootThread)
        {
            var roots = new List<int>();
            for (int node = 0; node < nodeCount; node++)
            {
                if (parentOffsets[node + 1] - parentOffsets[node] == 0)
                {
                    roots.Add(node);
                    isDedicated[node] = true;
                }
            }
            schedules[0] = roots.ToArray();
        }

        if (separateSinkThread)
        {
            var sinks = new List<int>();
            for (int node = 0; node < nodeCount; node++)
            {
                if (!isDedicated[node] && offsets[node + 1] - offsets[node] == 0)
                {
                    sinks.Add(node);
                    isDedicated[node] = true;
                }
            }
            schedules[threadCount - 1] = sinks.ToArray();
        }

        int workerStart = separateRootThread ? 1 : 0;
        int workerCount = threadCount - reserved;

        int[][] workerSchedules = strategy switch
        {
            PartitionStrategy.ChunkedLinear => BuildChunkedSchedule(FilterOrder(Enumerable.Range(0, nodeCount), isDedicated), workerCount),
            PartitionStrategy.Stride => BuildStrideSchedule(FilterOrder(Enumerable.Range(0, nodeCount), isDedicated), workerCount),
            PartitionStrategy.Clustered => BuildChunkedSchedule(FilterOrder(BuildClusteredOrder(nodeCount, edges, offsets, parentEdges, parentOffsets), isDedicated), workerCount),
            PartitionStrategy.Leveled => BuildLeveledSchedule(nodeCount, edges, offsets, parentOffsets, workerCount, isDedicated),
            PartitionStrategy.DepthFirst => BuildChunkedSchedule(FilterOrder(BuildDepthFirstOrder(nodeCount, edges, offsets, parentOffsets), isDedicated), workerCount),
            _ => BuildStrideSchedule(FilterOrder(Enumerable.Range(0, nodeCount), isDedicated), workerCount),
        };

        for (int w = 0; w < workerCount; w++)
            schedules[workerStart + w] = workerSchedules[w];

        return schedules;
    }

    private static int[] FilterOrder(IEnumerable<int> order, bool[] isDedicated)
    {
        var result = new List<int>();
        foreach (int node in order)
        {
            if (!isDedicated[node])
                result.Add(node);
        }
        return result.ToArray();
    }

    private static int[][] BuildChunkedSchedule(int[] order, int threadCount)
    {
        int[][] schedules = new int[threadCount][];
        int chunkSize = (order.Length + threadCount - 1) / threadCount;

        for (int t = 0; t < threadCount; t++)
        {
            int start = t * chunkSize;
            int end = Math.Min(order.Length, start + chunkSize);
            if (start >= order.Length)
            {
                schedules[t] = Array.Empty<int>();
                continue;
            }

            int len = end - start;
            int[] slice = new int[len];
            Array.Copy(order, start, slice, 0, len);
            schedules[t] = slice;
        }

        return schedules;
    }

    private static int[][] BuildStrideSchedule(int[] order, int threadCount)
    {
        var buckets = new List<int>[threadCount];
        for (int t = 0; t < threadCount; t++)
            buckets[t] = new List<int>();

        for (int i = 0; i < order.Length; i++)
            buckets[i % threadCount].Add(order[i]);

        int[][] schedules = new int[threadCount][];
        for (int t = 0; t < threadCount; t++)
            schedules[t] = buckets[t].ToArray();

        return schedules;
    }

    /// <summary>
    /// Orders nodes via undirected BFS (following both outgoing and incoming edges) so connected
    /// components stay contiguous when chunked across threads, improving cache locality on shared edges.
    /// </summary>
    private static int[] BuildClusteredOrder(int nodeCount, int[] edges, int[] offsets, int[] parentEdges, int[] parentOffsets)
    {
        var order = new List<int>(nodeCount);
        var visited = new bool[nodeCount];
        var queue = new Queue<int>();

        for (int start = 0; start < nodeCount; start++)
        {
            if (visited[start])
                continue;

            visited[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                order.Add(node);

                for (int ei = offsets[node]; ei < offsets[node + 1]; ei++)
                {
                    int child = edges[ei];
                    if (visited[child])
                        continue;

                    visited[child] = true;
                    queue.Enqueue(child);
                }

                for (int pi = parentOffsets[node]; pi < parentOffsets[node + 1]; pi++)
                {
                    int parent = parentEdges[pi];
                    if (visited[parent])
                        continue;

                    visited[parent] = true;
                    queue.Enqueue(parent);
                }
            }
        }

        return order.ToArray();
    }

    /// <summary>
    /// Groups nodes by topological depth (Kahn's algorithm) and stripes levels across threads
    /// so each thread tends to own a slice of the pipeline wave.
    /// </summary>
    private static int[][] BuildLeveledSchedule(
        int nodeCount,
        int[] edges,
        int[] offsets,
        int[] parentOffsets,
        int threadCount,
        bool[] isDedicated)
    {
        int[] inDegree = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            inDegree[i] = parentOffsets[i + 1] - parentOffsets[i];

        var queue = new Queue<int>();
        for (int i = 0; i < nodeCount; i++)
        {
            if (inDegree[i] == 0)
                queue.Enqueue(i);
        }

        var levels = new List<List<int>>();
        var seen = new bool[nodeCount];
        while (queue.Count > 0)
        {
            int levelCount = queue.Count;
            var levelNodes = new List<int>(levelCount);
            for (int i = 0; i < levelCount; i++)
            {
                int node = queue.Dequeue();
                if (seen[node])
                    continue;

                seen[node] = true;
                if (!isDedicated[node])
                    levelNodes.Add(node);

                for (int ei = offsets[node]; ei < offsets[node + 1]; ei++)
                {
                    int child = edges[ei];
                    if (--inDegree[child] == 0)
                        queue.Enqueue(child);
                }
            }

            if (levelNodes.Count > 0)
                levels.Add(levelNodes);
        }

        // Defensive fallback: if a caller bypasses cycle validation, ensure unreached nodes
        // are still scheduled rather than silently dropped.
        for (int i = 0; i < nodeCount; i++)
        {
            if (seen[i])
                continue;

            if (!isDedicated[i])
                levels.Add(new List<int> { i });
        }

        var buckets = new List<int>[threadCount];
        for (int t = 0; t < threadCount; t++)
            buckets[t] = new List<int>();

        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            int targetThread = levelIndex % threadCount;
            buckets[targetThread].AddRange(levels[levelIndex]);
        }

        int[][] schedules = new int[threadCount][];
        for (int t = 0; t < threadCount; t++)
            schedules[t] = buckets[t].ToArray();

        return schedules;
    }

    /// <summary>
    /// Produces a pre-order DFS traversal seeded from zero-in-degree roots so parent-to-child
    /// chains land on the same thread when chunked.
    /// </summary>
    private static int[] BuildDepthFirstOrder(int nodeCount, int[] edges, int[] offsets, int[] parentOffsets)
    {
        var visited = new bool[nodeCount];
        var order = new List<int>(nodeCount);
        var stack = new Stack<int>();

        for (int i = 0; i < nodeCount; i++)
        {
            if (parentOffsets[i + 1] - parentOffsets[i] != 0)
                continue;

            stack.Push(i);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (visited[node])
                    continue;

                visited[node] = true;
                order.Add(node);

                // Push in reverse CSR order so the first child is popped first.
                for (int ei = offsets[node + 1] - 1; ei >= offsets[node]; ei--)
                {
                    int child = edges[ei];
                    if (!visited[child])
                        stack.Push(child);
                }
            }
        }

        // Cover any remaining nodes not reachable from a root (e.g. if cycles were present).
        for (int i = 0; i < nodeCount; i++)
        {
            if (visited[i])
                continue;

            stack.Push(i);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (visited[node])
                    continue;

                visited[node] = true;
                order.Add(node);

                for (int ei = offsets[node + 1] - 1; ei >= offsets[node]; ei--)
                {
                    int child = edges[ei];
                    if (!visited[child])
                        stack.Push(child);
                }
            }
        }

        return order.ToArray();
    }
}
