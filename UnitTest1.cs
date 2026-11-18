using deltalag;
using Xunit;
namespace deltalag.Tests;

public class GraphTests
{
    [Fact]
    public void AssertCycleDetection()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        int c = b.AddNode();

        b.AddEdge(a, c);
        b.AddEdge(c, a);

        var config = new Config { InjectNodes = false };

        Assert.ThrowsAny<InvalidOperationException>(() => b.Build(config));
    }
    [Fact]
    public void AssertNoSelfLoop()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        b.AddEdge(a, a);
        var config = new Config { InjectNodes = false };
        Assert.ThrowsAny<InvalidOperationException>(() => b.Build(config));
    }
    [Fact]
    public void AssertAddEdgeInvalidFrom()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        int c = b.AddNode();
        Assert.ThrowsAny<InvalidEdge>(() => b.AddEdge(-1, c));

    }
    [Fact]
    public void AssertAddEdgeInvalidTo()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        int c = b.AddNode();
        Assert.ThrowsAny<InvalidEdge>(() => b.AddEdge(a, -1));
    }


}

public class 
