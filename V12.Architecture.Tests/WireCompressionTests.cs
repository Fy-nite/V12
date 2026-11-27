using System;
using System.Linq;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// The wire codec: BSON plus an optional, self-describing Brotli layer that must
/// stay backward compatible with raw BSON payloads.
/// </summary>
public sealed class WireCompressionTests
{
    [Fact]
    public void LargeRepetitivePayload_ShrinksAndRoundTrips()
    {
        var message = new MessageDTO
        {
            Sender = new Uri("networkcables://test"),
            MessageType = MessageType.WorldUpdate,
            Message = Enumerable.Repeat((byte)'A', 64 * 1024).ToArray(),
        };

        byte[] packed = AncientCompressor.Compress(message);

        Assert.Equal((byte)'V', packed[0]);
        Assert.Equal((byte)'1', packed[1]);
        Assert.Equal((byte)'2', packed[2]);
        Assert.Equal((byte)'C', packed[3]);
        Assert.True(packed.Length < message.Message.Length / 4,
            $"expected a big win, got {packed.Length} bytes for {message.Message.Length}");

        var restored = AncientCompressor.Decompress<MessageDTO>(packed);
        Assert.Equal(message.Message, restored!.Message);
        Assert.Equal(message.MessageType, restored.MessageType);
    }

    [Fact]
    public void SmallPayload_IsSentAsRawBson()
    {
        var message = new MessageDTO
        {
            Sender = new Uri("networkcables://test"),
            MessageType = MessageType.SyncBatch,
            Message = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
        };

        byte[] packed = AncientCompressor.Compress(message);

        // No V12C header: a 60 Hz value update must not pay it.
        Assert.False(packed.Length >= 4 && packed[0] == (byte)'V' && packed[1] == (byte)'1');

        var restored = AncientCompressor.Decompress<MessageDTO>(packed);
        Assert.Equal(message.Message, restored!.Message);
    }

    [Fact]
    public void IncompressiblePayload_IsStoredRaw()
    {
        // Random bytes do not shrink, so the codec must not add a header just to
        // make them bigger.
        var bytes = new byte[64 * 1024];
        new Random(42).NextBytes(bytes);

        var message = new MessageDTO
        {
            Sender = new Uri("networkcables://test"),
            MessageType = MessageType.WorldSync,
            Message = bytes,
        };

        byte[] packed = AncientCompressor.Compress(message);

        Assert.False(packed.Length >= 4 && packed[0] == (byte)'V' && packed[1] == (byte)'1');

        var restored = AncientCompressor.Decompress<MessageDTO>(packed);
        Assert.Equal(bytes, restored!.Message);
    }
}
