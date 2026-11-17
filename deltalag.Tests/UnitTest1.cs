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
        scheduler.Run(ctx, config);

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
        scheduler.Run(ctx, config);

        Assert.Equal(config.Cycles, rootRuns);
        Assert.Equal(config.Cycles, childRuns);
        Assert.Equal(config.Cycles, ctx.NodeArray[root].WorkDone);
        Assert.Equal(config.Cycles, ctx.NodeArray[child].WorkDone);
    }
}
