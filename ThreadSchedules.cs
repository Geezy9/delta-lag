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
        bool seperateRootThread)
    {
        if (threadCount <= 1)
            return new[] { Enumerable.Range(0, nodeCount).ToArray() };

        int[][] schedules = strategy switch
        {
            PartitionStrategy.ChunkedLinear => BuildChunkedSchedule(Enumerable.Range(0, nodeCount).ToArray(), threadCount),
            PartitionStrategy.Stride => BuildStrideSchedule(Enumerable.Range(0, nodeCount).ToArray(), threadCount),
            PartitionStrategy.Clustered => BuildChunkedSchedule(BuildClusteredOrder(nodeCount, edges, offsets, parentEdges, parentOffsets), threadCount),
            PartitionStrategy.Leveled => BuildLeveledSchedule(nodeCount, edges, offsets, parentOffsets, threadCount, seperateRootThread),
            PartitionStrategy.DepthFirst => BuildChunkedSchedule(BuildDepthFirstOrder(nodeCount, edges, offsets, parentOffsets), threadCount),
            _ => BuildStrideSchedule(Enumerable.Range(0, nodeCount).ToArray(), threadCount),
        };

        if (seperateRootThread && strategy != PartitionStrategy.Leveled)
            ApplySeperateRootThread(schedules, parentOffsets);

        return schedules;
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

    private static int[][] BuildLeveledSchedule(
        int nodeCount,
        int[] edges,
        int[] offsets,
        int[] parentOffsets,
        int threadCount,
        bool seperateRootThread)
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

        for (int i = 0; i < nodeCount; i++)
        {
            if (seen[i])
                continue;

            levels.Add(new List<int> { i });
        }

        var buckets = new List<int>[threadCount];
        for (int t = 0; t < threadCount; t++)
            buckets[t] = new List<int>();

        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            int targetThread;
            if (seperateRootThread && threadCount > 1)
            {
                if (levelIndex == 0)
                {
                    targetThread = 0;
                }
                else
                {
                    targetThread = 1 + ((levelIndex - 1) % (threadCount - 1));
                }
            }
            else
            {
                targetThread = levelIndex % threadCount;
            }

            buckets[targetThread].AddRange(levels[levelIndex]);
        }

        int[][] schedules = new int[threadCount][];
        for (int t = 0; t < threadCount; t++)
            schedules[t] = buckets[t].ToArray();

        return schedules;
    }

    private static void ApplySeperateRootThread(int[][] schedules, int[] parentOffsets)
    {
        if (schedules.Length <= 1)
            return;

        var rootSet = new HashSet<int>();
        for (int node = 0; node < parentOffsets.Length - 1; node++)
        {
            if (parentOffsets[node + 1] - parentOffsets[node] == 0)
                rootSet.Add(node);
        }

        if (rootSet.Count == 0)
            return;

        var rootThreadNodes = new List<int>(schedules[0].Length + rootSet.Count);
        for (int i = 0; i < schedules[0].Length; i++)
            rootThreadNodes.Add(schedules[0][i]);

        for (int t = 0; t < schedules.Length; t++)
        {
            var next = new List<int>(schedules[t].Length);
            for (int i = 0; i < schedules[t].Length; i++)
            {
                int node = schedules[t][i];
                if (!rootSet.Contains(node))
                {
                    next.Add(node);
                    continue;
                }

                if (t != 0)
                    rootThreadNodes.Add(node);
            }

            if (t != 0)
                schedules[t] = next.ToArray();
        }

        schedules[0] = rootThreadNodes.ToArray();
    }

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

                for (int ei = offsets[node + 1] - 1; ei >= offsets[node]; ei--)
                {
                    int child = edges[ei];
                    if (!visited[child])
                        stack.Push(child);
                }
            }
        }

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
