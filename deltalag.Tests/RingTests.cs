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
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(10)]
    public void Constructor_WithNonPowerOfTwo_ThrowsArgumentException(int size)
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
        scheduler.Run(ctx, config);

        for (int i = 0; i < config.Cycles; i++)
        {
            Assert.Equal(i * 10L + 1, received[i]);
        }
    }
}
