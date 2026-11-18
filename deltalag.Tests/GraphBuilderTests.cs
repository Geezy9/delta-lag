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

        var config = new Config
        {
            Cycles = 1,
            InjectNodes = true,
            K = 2,
            MaxFanIn = 2,
            MaxFanOut = 2,
            Strategy = PartitionStrategy.Stride,
            Slack = 0,
            Threads = 2
        };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_WithParallelEdges_PreservesEdgesAndExecutesCleanly(bool injectNodes)
    {
        int countB = 0;
        var builder = new GraphBuilder();
        int a = builder.AddNode();
        int b = builder.AddNode(_ => Interlocked.Increment(ref countB));
        builder.AddEdge(a, b);
        builder.AddEdge(a, b);
        builder.AddEdge(a, b);

        var config = new Config
        {
            Cycles = 12,
            Slack = 1,
            Threads = 2,
            InjectNodes = injectNodes,
            K = 2,
            MaxFanIn = 2,
            MaxFanOut = 2
        };

        var ctx = builder.Build(config);
        using var scheduler = new Scheduler();
        scheduler.Run(ctx);

        Assert.Equal(12, countB);
        Assert.Equal(12, ctx.NodeArray[a].WorkDone);
        Assert.Equal(12, ctx.NodeArray[b].WorkDone);
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

    [Fact]
    public void Build_WithEmptyGraph_ThrowsInvalidOperationException()
    {
        var builder = new GraphBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Build(new Config { Cycles = 5, Threads = 2 }));
    }

    [Fact]
    public void Build_WithNullConfig_ThrowsArgumentNullException()
    {
        var builder = new GraphBuilder();
        builder.AddNode();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Build_WithNegativeCyclesOrSlack_ThrowsArgumentOutOfRangeException(int cycles, int slack)
    {
        var builder = new GraphBuilder();
        builder.AddNode();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(new Config { Cycles = cycles, Slack = slack }));
    }

    [Fact]
    public void DefaultPartitionStrategy_IsStride()
    {
        Assert.Equal(PartitionStrategy.Stride, default);
    }
}
