using System.ComponentModel;
using deltalag;
using Xunit;
namespace deltalag.Tests;

public class GraphTests
{
    [Fact]
    public void CycleDetection()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        int c = b.AddNode();
        b.AddEdge(a, c);
        b.AddEdge(c, a);
        
        var config = new Config { InjectNodes = false };
        var scheduler = new Scheduler();
        Assert.ThrowsAny<InvalidOperationException>(() => b.Build(config));


    }
    [Fact]
    public void SelfLoopDetection()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        b.AddEdge(a, a);
        var config = new Config { InjectNodes = false };
        Assert.ThrowsAny<InvalidOperationException>(() => b.Build(config));
    }
    [Fact]
    public void AddEdgeInvalidFrom()
    {
        var b = new GraphBuilder();
        int c = b.AddNode();
        b.AddEdge(200, c);
        var config = new Config { InjectNodes = false };
        Assert.ThrowsAny<Exception>(() => b.Build(config));
    }
    [Fact]
    public void AddEdgeInvalidTo()
    {
        var b = new GraphBuilder();
        int a = b.AddNode();
        b.AddEdge(a, 200);
        var config = new Config { InjectNodes = false };
        Assert.ThrowsAny<Exception>(() => b.Build(config));
    }
}
