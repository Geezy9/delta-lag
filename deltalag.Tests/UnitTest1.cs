using deltalag;
using Xunit;
namespace deltalag.Tests;

public class GraphBuilderTests
{
    [Fact]
    public void Build_WithKaryInjection_ExpandsGraphAndKeepsValidEdges()
    {
        var builder = new GraphBuilder();
        int source = builder.AddNode();
        int c1 = builder.AddNode();
        int c2 = builder.AddNode();
        int c3 = builder.AddNode();
        int c4 = builder.AddNode();

        builder.AddEdge(source, c1);
        builder.AddEdge(source, c2);
        builder.AddEdge(source, c3);
        builder.AddEdge(source, c4);

        var config = new Config { Cycles = 1, InjectNodes = true, K = 2, MaxFanIn = 2, MaxFanOut = 2, Strategy = PartitionStrategy.Stride, Slack = 0, Threads = 2 };
        var ctx = builder.Build(config);

        Assert.True(ctx.NodeArray.Length > 5);
        Assert.Equal(ctx.NodeArray.Length + 1, ctx.Offsets.Length);

        for (int i = 0; i < ctx.NodeArray.Length; i++)
        {
            for (int ei = ctx.Offsets[i]; ei < ctx.Offsets[i + 1]; ei++)
            {
                Assert.InRange(ctx.Edges[ei], 0, ctx.NodeArray.Length - 1);
            }
        }
    }

    [Fact]
    public void Build_WithAcyclicGraph_BuildsExpectedCsrAndParents()
    {
        var builder = new GraphBuilder();
        int n0 = builder.AddNode();
        int n1 = builder.AddNode();
        int n2 = builder.AddNode();

        builder.AddEdge(n0, n1);
        builder.AddEdge(n0, n2);
        builder.AddEdge(n1, n2);

        var ctx = builder.Build(new Config { InjectNodes = false });

        Assert.Equal(3, ctx.NodeArray.Length);
        Assert.Equal(new[] { 0, 2, 3, 3 }, ctx.Offsets);
        Assert.Equal(new[] { 1, 2, 2 }, ctx.Edges);
        Assert.Equal(new[] { 0, 0, 1, 3 }, ctx.ParentOffsets);
        Assert.Equal(new[] { 0, 0, 1 }, ctx.ParentEdges);
    }

    [Fact]
    public void Build_WithCycle_ThrowsInvalidOperationException()
    {
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        int b = builder.AddNode();
        builder.AddEdge(a, b);
        builder.AddEdge(b, a);
        Assert.Throws<InvalidOperationException>(() => builder.Build(new Config { InjectNodes = false }));
    }

    [Fact]
    public void Build_WithSelfLoop_ThrowsInvalidOperationException()
    {
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        builder.AddEdge(a, a);
        Assert.Throws<InvalidOperationException>(() => builder.Build(new Config { InjectNodes = false }));
    }

    [Fact]
    public void AddEdge_WithInvalidFrom_ThrowsArgumentOutOfRangeException()
    {
        var builder = new GraphBuilder();
        int to = builder.AddNode();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddEdge(200, to));
    }

    [Fact]
    public void AddEdge_WithInvalidTo_ThrowsArgumentOutOfRangeException()
    {
        var builder = new GraphBuilder();
        int from = builder.AddNode();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddEdge(from, 200));
    }
}

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

        var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Equal(config.Cycles, ctx.NodeArray[root].WorkDone);
        Assert.Equal(config.Cycles, ctx.NodeArray[child].WorkDone);
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

        var scheduler = new Scheduler();
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

        var scheduler = new Scheduler();
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

        var scheduler = new Scheduler();
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

        var scheduler = new Scheduler();
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
        new Scheduler().Run(ctx);

        Assert.Equal(0, calls);
        Assert.Equal(0, ctx.NodeArray[a].WorkDone);
        Assert.Equal(0, ctx.NodeArray[b].WorkDone);
    }

    [Fact]
    public void Run_WithEmptyGraph_CompletesCleanly()
    {
        var builder = new GraphBuilder();
        var ctx = builder.Build(new Config { Cycles = 5, Threads = 2 });

        new Scheduler().Run(ctx);

        Assert.Empty(ctx.NodeArray);
    }

    [Fact]
    public void Run_ReusingSchedulerAcrossMultipleRuns_CompletesBoth()
    {
        var scheduler = new Scheduler();

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
}
