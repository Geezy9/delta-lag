using deltalag;
using Xunit;

namespace deltalag.Tests;

public class RingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    public void Constructor_WithPowerOfTwo_AllocatesBufferOfExpectedLength(int size)
    {
        var ring = new Ring<int>(size);

        Assert.Equal(size, ring.Buffers.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(10)]
    public void Constructor_WithInvalidSize_ThrowsArgumentException(int size)
    {
        Assert.Throws<ArgumentException>(() => new Ring<int>(size));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(7, 8)]
    [InlineData(8, 16)]
    public void FromCon_RoundsSlackPlusOneUpToPowerOfTwo(int slack, int expectedSize)
    {
        var config = new Config { Slack = slack };

        var ring = Ring<int>.FromCon(config);

        Assert.Equal(expectedSize, ring.Buffers.Length);
    }

    [Fact]
    public void FromCon_WithMaxSlack_ThrowsOverflowException()
    {
        var config = new Config { Slack = int.MaxValue };

        Assert.Throws<OverflowException>(() => Ring<int>.FromCon(config));
    }

    [Fact]
    public void WriteAndRead_WithinCapacity_PreservesAllWorkUnits()
    {
        var ring = new Ring<long>(4);

        for (long i = 0; i < 4; i++)
        {
            ring.Write(i, i * 100 + 7);
        }

        for (long i = 0; i < 4; i++)
        {
            Assert.Equal(i * 100 + 7, ring.Read(i));
        }
    }

    [Fact]
    public void Write_BeyondCapacity_WrapsAroundAndOverwritesOldSlot()
    {
        var ring = new Ring<string>(4);

        ring.Write(0, "first");
        ring.Write(1, "second");
        ring.Write(4, "wrapped");

        Assert.Equal("wrapped", ring.Read(0));
        Assert.Equal("wrapped", ring.Read(4));
        Assert.Equal("second", ring.Read(1));
    }

    [Fact]
    public void WriteAndRead_WithLargeWorkUnit_MasksCorrectly()
    {
        var ring = new Ring<int>(8);
        long workUnit = (1L << 32) + 5;

        ring.Write(workUnit, 42);

        Assert.Equal(42, ring.Read(workUnit));
        Assert.Equal(42, ring.Read(5));
    }

    [Fact]
    public void Ring_WithSchedulerAndSlack_PassesAllPayloadsCorrectly()
    {
        var config = new Config
        {
            Cycles = 100,
            Slack = 3,
            Threads = 2,
            Strategy = PartitionStrategy.Stride,
            InjectNodes = false
        };

        var ring = Ring<long>.FromCon(config);
        var received = new long[config.Cycles];

        var builder = new GraphBuilder();
        int producer = builder.AddNode(workUnit => ring.Write(workUnit, workUnit * 10 + 1));
        int consumer = builder.AddNode(workUnit => received[workUnit] = ring.Read(workUnit));
        builder.AddEdge(producer, consumer);

        var ctx = builder.Build(config);
        var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int i = 0; i < config.Cycles; i++)
        {
            Assert.Equal(i * 10L + 1, received[i]);
        }
    }

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(1, 3, 8)]
    [InlineData(3, 2, 8)]
    [InlineData(4, 2, 16)]
    public void FromCon_WithMaxHops_SizesForMultiHopInFlightWindow(int slack, int maxHops, int expectedSize)
    {
        var config = new Config { Slack = slack };

        var ring = Ring<int>.FromCon(config, maxHops);

        Assert.Equal(expectedSize, ring.Buffers.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Ring_WithInjectNodesAndSlack_PassesAllPayloadsCorrectly(int slack)
    {
        var config = new Config
        {
            Cycles = 200,
            Slack = slack,
            Threads = 4,
            Strategy = PartitionStrategy.Stride,
            InjectNodes = true,
            K = 2,
            MaxFanIn = 2,
            MaxFanOut = 2
        };

        const int branchCount = 5;
        var fanOutRings = new Ring<long>[branchCount];
        var fanInRings = new Ring<long>[branchCount];
        var branchReceived = new long[branchCount][];
        var sinkReceived = new long[config.Cycles];

        var builder = new GraphBuilder();
        int producer = builder.AddNode(workUnit =>
        {
            for (int b = 0; b < branchCount; b++)
            {
                fanOutRings[b].Write(workUnit, workUnit * 100L + b);
            }
        });

        var middles = new int[branchCount];
        for (int b = 0; b < branchCount; b++)
        {
            int branch = b;
            branchReceived[branch] = new long[config.Cycles];
            middles[branch] = builder.AddNode(workUnit =>
            {
                long val = fanOutRings[branch].Read(workUnit);
                branchReceived[branch][workUnit] = val;
                fanInRings[branch].Write(workUnit, val + 1000L);
            });
            builder.AddEdge(producer, middles[branch]);
        }

        int sink = builder.AddNode(workUnit =>
        {
            long sum = 0;
            for (int b = 0; b < branchCount; b++)
            {
                sum += fanInRings[b].Read(workUnit);
            }
            sinkReceived[workUnit] = sum;
        });

        for (int b = 0; b < branchCount; b++)
        {
            builder.AddEdge(middles[b], sink);
        }

        var ctx = builder.Build(config);
        Assert.True(ctx.MaxEdgeHops >= 2);

        for (int b = 0; b < branchCount; b++)
        {
            fanOutRings[b] = Ring<long>.FromContext(ctx, config);
            fanInRings[b] = Ring<long>.FromContext(ctx, config);
        }

        var scheduler = new Scheduler();
        scheduler.Run(ctx);

        for (int w = 0; w < config.Cycles; w++)
        {
            long expectedSum = 0;
            for (int b = 0; b < branchCount; b++)
            {
                long expectedVal = w * 100L + b;
                Assert.Equal(expectedVal, branchReceived[b][w]);
                expectedSum += expectedVal + 1000L;
            }
            Assert.Equal(expectedSum, sinkReceived[w]);
        }
    }

    [Theory]
    [InlineData(PartitionStrategy.ChunkedLinear)]
    [InlineData(PartitionStrategy.Stride)]
    [InlineData(PartitionStrategy.Clustered)]
    [InlineData(PartitionStrategy.Leveled)]
    [InlineData(PartitionStrategy.DepthFirst)]
    public void Ring_WithAllPartitionStrategiesAndSlack_PassesAllPayloadsCorrectly(PartitionStrategy strategy)
    {
        var config = new Config
        {
            Cycles = 64,
            Slack = 3,
            Threads = 3,
            Strategy = strategy,
            InjectNodes = false
        };

        var r0 = Ring<long>.FromCon(config);
        var r1 = Ring<long>.FromCon(config);
        var received = new long[config.Cycles];

        var builder = new GraphBuilder();
        int n0 = builder.AddNode(w => r0.Write(w, w * 7L));
        int n1 = builder.AddNode(w => r1.Write(w, r0.Read(w) + 3L));
        int n2 = builder.AddNode(w => received[w] = r1.Read(w) * 2L);
        builder.AddEdge(n0, n1);
        builder.AddEdge(n1, n2);

        var ctx = builder.Build(config);
        new Scheduler().Run(ctx);

        for (int w = 0; w < config.Cycles; w++)
        {
            Assert.Equal((w * 7L + 3L) * 2L, received[w]);
        }
    }
}
