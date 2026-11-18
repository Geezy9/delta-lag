namespace deltalag.Tests;

public class SchedulerTests
{
    [Fact]
    public void Run_ExecutesConfiguredCycles()
    {
        var builder = new GraphBuilder();
        int root = builder.AddNode();
        int child = builder.AddNode();
        builder.AddEdge(root, child);

        var config = new Config { Cycles = 20, InjectNodes = false, Strategy = PartitionStrategy.Stride, Slack = 0, Threads = 2 };
        var ctx = builder.Build(config);

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Equal(config.Cycles, ctx.NodeArray[root].WorkDone);
        Assert.Equal(config.Cycles, ctx.NodeArray[child].WorkDone);
    }

    [Fact]
    public void Run_WithSlotAndZeroSlack_PassesPayloadsInLockStep()
    {
        var slot = new Slot<int>();
        var received = new int[32];

        var builder = new GraphBuilder();
        int producer = builder.AddNode(w => slot.Value = w * 3 + 5);
        int consumer = builder.AddNode(w => received[w] = slot.Value);
        builder.AddEdge(producer, consumer);

        var ctx = builder.Build(new Config { Cycles = received.Length, Slack = 0, Threads = 2 });
        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int w = 0; w < received.Length; w++)
        {
            Assert.Equal(w * 3 + 5, received[w]);
        }
    }

    [Fact]
    public void Run_WithInjectNodesAndNoInjectionNeeded_RunsTasks()
    {
        int rootRuns = 0;
        int childRuns = 0;

        var builder = new GraphBuilder();
        int root = builder.AddNode(_ => Interlocked.Increment(ref rootRuns));
        int child = builder.AddNode(_ => Interlocked.Increment(ref childRuns));
        builder.AddEdge(root, child);

        var config = new Config { Cycles = 20, InjectNodes = true, K = 2, MaxFanIn = 4, MaxFanOut = 4, Strategy = PartitionStrategy.Stride, Slack = 0, Threads = 2 };
        var ctx = builder.Build(config);

        Assert.Equal(new[] { 0, 0, 1 }, ctx.ParentOffsets);
        Assert.Equal(new[] { 0 }, ctx.ParentEdges);

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Equal(config.Cycles, rootRuns);
        Assert.Equal(config.Cycles, childRuns);
        Assert.Equal(config.Cycles, ctx.NodeArray[root].WorkDone);
        Assert.Equal(config.Cycles, ctx.NodeArray[child].WorkDone);
    }

    [Fact]
    public void Build_PreAllocatesThreadSchedulesAndConfigInWorkContext()
    {
        var builder = new GraphBuilder();
        int n0 = builder.AddNode();
        int n1 = builder.AddNode();
        int n2 = builder.AddNode();
        builder.AddEdge(n0, n1);
        builder.AddEdge(n1, n2);

        var config = new Config { Cycles = 10, Slack = 1, Strategy = PartitionStrategy.Stride, Threads = 2 };
        var ctx = builder.Build(config);

        Assert.Equal(10, ctx.Cycles);
        Assert.Equal(1, ctx.Slack);
        Assert.Equal(2, ctx.ThreadCount);
        Assert.NotNull(ctx.ThreadSchedules);
        Assert.Equal(2, ctx.ThreadSchedules.Length);

        var preAllocatedSchedules = ctx.ThreadSchedules;

        // Mutating config after Build must not affect WorkContext (single source of truth).
        config.Cycles = 999;
        config.Threads = 1;

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Same(preAllocatedSchedules, ctx.ThreadSchedules);
        Assert.Equal(10, ctx.NodeArray[n0].WorkDone);
        Assert.Equal(10, ctx.NodeArray[n1].WorkDone);
        Assert.Equal(10, ctx.NodeArray[n2].WorkDone);
    }

    [Theory]
    [InlineData(PartitionStrategy.Stride)]
    [InlineData(PartitionStrategy.ChunkedLinear)]
    [InlineData(PartitionStrategy.Clustered)]
    [InlineData(PartitionStrategy.Leveled)]
    [InlineData(PartitionStrategy.DepthFirst)]
    public void Build_WithSeparateRootThread_IsolatesRootsOnFirstThread(PartitionStrategy strategy)
    {
        var builder = new GraphBuilder();
        int root1 = builder.AddNode();
        int root2 = builder.AddNode();
        int mid = builder.AddNode();
        int sink = builder.AddNode();
        builder.AddEdge(root1, mid);
        builder.AddEdge(root2, mid);
        builder.AddEdge(mid, sink);

        var config = new Config
        {
            Cycles = 8,
            Strategy = strategy,
            Threads = 3,
            SeparateRootThread = true
        };

        var ctx = builder.Build(config);

        Assert.Equal(3, ctx.ThreadCount);
        Assert.Equal(new[] { root1, root2 }, ctx.ThreadSchedules[0].OrderBy(x => x).ToArray());
        Assert.DoesNotContain(root1, ctx.ThreadSchedules[1]);
        Assert.DoesNotContain(root1, ctx.ThreadSchedules[2]);
        Assert.DoesNotContain(root2, ctx.ThreadSchedules[1]);
        Assert.DoesNotContain(root2, ctx.ThreadSchedules[2]);

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int i = 0; i < ctx.NodeArray.Length; i++)
            Assert.Equal(config.Cycles, ctx.NodeArray[i].WorkDone);
    }

    [Theory]
    [InlineData(PartitionStrategy.Stride)]
    [InlineData(PartitionStrategy.ChunkedLinear)]
    [InlineData(PartitionStrategy.Clustered)]
    [InlineData(PartitionStrategy.Leveled)]
    [InlineData(PartitionStrategy.DepthFirst)]
    public void Build_WithSeparateSinkThread_IsolatesSinksOnLastThread(PartitionStrategy strategy)
    {
        var builder = new GraphBuilder();
        int root = builder.AddNode();
        int mid = builder.AddNode();
        int sink1 = builder.AddNode();
        int sink2 = builder.AddNode();
        builder.AddEdge(root, mid);
        builder.AddEdge(mid, sink1);
        builder.AddEdge(mid, sink2);

        var config = new Config
        {
            Cycles = 8,
            Strategy = strategy,
            Threads = 3,
            SeparateSinkThread = true
        };

        var ctx = builder.Build(config);

        Assert.Equal(3, ctx.ThreadCount);
        Assert.Equal(new[] { sink1, sink2 }, ctx.ThreadSchedules[2].OrderBy(x => x).ToArray());
        Assert.DoesNotContain(sink1, ctx.ThreadSchedules[0]);
        Assert.DoesNotContain(sink1, ctx.ThreadSchedules[1]);
        Assert.DoesNotContain(sink2, ctx.ThreadSchedules[0]);
        Assert.DoesNotContain(sink2, ctx.ThreadSchedules[1]);

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int i = 0; i < ctx.NodeArray.Length; i++)
            Assert.Equal(config.Cycles, ctx.NodeArray[i].WorkDone);
    }

    [Fact]
    public void Build_WithSeparateRootAndSinkThreads_IsolatesRootsAndSinksAndRunsWorkersInMiddle()
    {
        var builder = new GraphBuilder();
        int root = builder.AddNode();
        int mid1 = builder.AddNode();
        int mid2 = builder.AddNode();
        int sink = builder.AddNode();
        builder.AddEdge(root, mid1);
        builder.AddEdge(root, mid2);
        builder.AddEdge(mid1, sink);
        builder.AddEdge(mid2, sink);

        var config = new Config
        {
            Cycles = 12,
            Strategy = PartitionStrategy.Stride,
            Threads = 4,
            SeparateRootThread = true,
            SeparateSinkThread = true
        };

        var ctx = builder.Build(config);

        Assert.Equal(4, ctx.ThreadCount);
        Assert.Equal(new[] { root }, ctx.ThreadSchedules[0]);
        Assert.Equal(new[] { sink }, ctx.ThreadSchedules[3]);
        Assert.Equal(new[] { mid1, mid2 }, ctx.ThreadSchedules[1].Concat(ctx.ThreadSchedules[2]).OrderBy(x => x).ToArray());

        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int i = 0; i < ctx.NodeArray.Length; i++)
            Assert.Equal(config.Cycles, ctx.NodeArray[i].WorkDone);
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, true)]
    public void Build_WithInsufficientWorkerThreadsForDedicatedThreads_ThrowsInvalidOperationException(
        int threads,
        bool separateRoot,
        bool separateSink)
    {
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        int b = builder.AddNode();
        int c = builder.AddNode();
        builder.AddEdge(a, b);
        builder.AddEdge(b, c);

        var config = new Config
        {
            Cycles = 1,
            Strategy = PartitionStrategy.Stride,
            Threads = threads,
            SeparateRootThread = separateRoot,
            SeparateSinkThread = separateSink
        };

        Assert.Throws<InvalidOperationException>(() => builder.Build(config));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Build_WithInjectNodesAndDegreeLimitOne_ThrowsArgumentOutOfRangeException(int maxFanIn, int maxFanOut)
    {
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        int b = builder.AddNode();
        builder.AddEdge(a, b);

        var config = new Config
        {
            Cycles = 1,
            InjectNodes = true,
            K = 2,
            MaxFanIn = maxFanIn,
            MaxFanOut = maxFanOut
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(config));
    }

    [Fact]
    public void Run_WithZeroCycles_DoesNotExecuteTasks()
    {
        int calls = 0;
        var builder = new GraphBuilder();
        int a = builder.AddNode(_ => Interlocked.Increment(ref calls));
        int b = builder.AddNode(_ => Interlocked.Increment(ref calls));
        builder.AddEdge(a, b);

        var ctx = builder.Build(new Config { Cycles = 0, Threads = 2 });
        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Equal(0, calls);
        Assert.Equal(0, ctx.NodeArray[a].WorkDone);
        Assert.Equal(0, ctx.NodeArray[b].WorkDone);
    }

    [Fact]
    public void Run_ReusingSchedulerAcrossMultipleRuns_CompletesBoth()
    {
        using var scheduler = new Scheduler();

        for (int run = 0; run < 3; run++)
        {
            var builder = new GraphBuilder();
            int a = builder.AddNode();
            int b = builder.AddNode();
            builder.AddEdge(a, b);

            var ctx = builder.Build(new Config { Cycles = 10, Threads = 2 });
            scheduler.Run(ctx);

            Assert.Equal(10, ctx.NodeArray[a].WorkDone);
            Assert.Equal(10, ctx.NodeArray[b].WorkDone);
        }
    }

    [Fact]
    public void Run_WithThreadAffinityEnabled_AndToggled_CompletesAllCycles()
    {
        using var scheduler = new Scheduler();

        foreach (bool affinity in new[] { true, false, true })
        {
            int countA = 0;
            int countB = 0;
            var builder = new GraphBuilder();
            int a = builder.AddNode(_ => Interlocked.Increment(ref countA));
            int b = builder.AddNode(_ => Interlocked.Increment(ref countB));
            builder.AddEdge(a, b);

            var ctx = builder.Build(new Config
            {
                Cycles = 20,
                Slack = 2,
                Threads = 2,
                Strategy = PartitionStrategy.Stride,
                ThreadAffinity = affinity,
            });

            scheduler.Run(ctx);

            Assert.Equal(20, countA);
            Assert.Equal(20, countB);
            Assert.Equal(20, ctx.NodeArray[a].WorkDone);
            Assert.Equal(20, ctx.NodeArray[b].WorkDone);
        }
    }

    [Fact]
    public void Run_ChangingThreadCountAcrossRuns_ReconfiguresPoolAndCompletes()
    {
        using var scheduler = new Scheduler();

        foreach (int threads in new[] { 2, 4, 1, 3 })
        {
            var builder = new GraphBuilder();
            int n0 = builder.AddNode();
            int n1 = builder.AddNode();
            int n2 = builder.AddNode();
            int n3 = builder.AddNode();
            builder.AddEdge(n0, n1);
            builder.AddEdge(n1, n2);
            builder.AddEdge(n2, n3);

            var ctx = builder.Build(new Config
            {
                Cycles = 15,
                Slack = 1,
                Threads = threads,
                Strategy = PartitionStrategy.Stride,
            });

            scheduler.Run(ctx);

            Assert.Equal(15, ctx.NodeArray[n0].WorkDone);
            Assert.Equal(15, ctx.NodeArray[n3].WorkDone);
        }
    }

    [Fact]
    public void Run_WhenTaskThrows_PropagatesExceptionWithoutDeadlock()
    {
        using var scheduler = new Scheduler();
        var builder = new GraphBuilder();
        int a = builder.AddNode(cycle =>
        {
            if (cycle == 3)
                throw new InvalidOperationException("boom");
        });
        int b = builder.AddNode();
        builder.AddEdge(a, b);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            var ctx = builder.Build(new Config { Cycles = 10, Threads = 2, Strategy = PartitionStrategy.Stride });
            scheduler.Run(ctx);
        });
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Run_WhenMultipleWorkersThrowSimultaneously_ThrowsAggregateException()
    {
        if (Environment.ProcessorCount < 2)
            return;

        using var scheduler = new Scheduler();
        using var barrier = new Barrier(2);

        var builder = new GraphBuilder();
        builder.AddNode(_ =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("worker-0");
        });
        builder.AddNode(_ =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("worker-1");
        });

        var agg = Assert.Throws<AggregateException>(() =>
        {
            var ctx = builder.Build(new Config { Cycles = 1, Threads = 2, Strategy = PartitionStrategy.Stride });
            scheduler.Run(ctx);
        });

        Assert.Equal(2, agg.InnerExceptions.Count);
    }

    [Fact]
    public void Run_AfterDispose_ThrowsObjectDisposedException()
    {
        var scheduler = new Scheduler();
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        int b = builder.AddNode();
        builder.AddEdge(a, b);

        var ctx = builder.Build(new Config { Cycles = 5, Threads = 2 });
        scheduler.Run(ctx);
        scheduler.Dispose();
        scheduler.Dispose(); // idempotent

        Assert.Throws<ObjectDisposedException>(() =>
        {
            scheduler.Run(default);
        });

        Assert.Throws<ObjectDisposedException>(() =>
        {
            var nextCtx = builder.Build(new Config { Cycles = 5, Threads = 2 });
            scheduler.Run(nextCtx);
        });
    }

    [Fact]
    public void Run_WithDefaultWorkContext_ThrowsArgumentException()
    {
        using var scheduler = new Scheduler();

        Assert.Throws<ArgumentException>(() => scheduler.Run(default));
    }

    [Fact]
    public async Task Run_WhenConcurrentRunAttempted_ThrowsInvalidOperationException()
    {
        using var scheduler = new Scheduler();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var builder1 = new GraphBuilder();
        builder1.AddNode(_ =>
        {
            entered.Set();
            release.Wait();
        });

        var builder2 = new GraphBuilder();
        builder2.AddNode();

        var bgTask = Task.Run(() =>
        {
            var ctx1 = builder1.Build(new Config { Cycles = 1, Threads = 1 });
            scheduler.Run(ctx1);
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                var ctx2 = builder2.Build(new Config { Cycles = 1, Threads = 1 });
                scheduler.Run(ctx2);
            });

            Assert.Throws<InvalidOperationException>(() => scheduler.Dispose());
        }
        finally
        {
            release.Set();
        }

        await bgTask;
    }
}
