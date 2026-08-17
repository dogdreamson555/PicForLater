using System.Buffers.Binary;
using System.Text.Json;
using PicForLater.LocalInference.Protocol;

namespace PicForLater.Analysis.Tests;

public sealed class LocalInferenceProtocolTests
{
    [Fact]
    public async Task Frame_round_trip_preserves_version_request_and_payload()
    {
        var requestId = Guid.NewGuid();
        var expected = new LocalInferenceEnvelope(
            LocalInferenceProtocol.CurrentVersion,
            LocalInferenceMessageTypes.Hello,
            requestId,
            Operation: null,
            LocalInferenceProtocol.ToPayload(new LocalInferenceHelloRequest(
                1,
                1,
                42,
                @"C:\AppData",
                45)));
        await using var stream = new MemoryStream();

        await LocalInferenceProtocol.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await LocalInferenceProtocol.ReadAsync(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(expected.MessageType, actual.MessageType);
        Assert.Equal(requestId, actual.RequestId);
        var payload = LocalInferenceProtocol.ReadPayload<LocalInferenceHelloRequest>(actual);
        Assert.Equal(45, payload.IdleTimeoutSeconds);
        Assert.Equal(@"C:\AppData", payload.AppDataRootPath);
    }

    [Fact]
    public async Task Oversized_frame_length_is_rejected_before_allocation()
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes,
            LocalInferenceProtocol.MaximumFrameBytes + 1);
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<LocalInferenceProtocolException>(async () =>
            await LocalInferenceProtocol.ReadAsync(stream));

        Assert.Equal("local-worker.protocol-invalid-frame-length", exception.ErrorCode);
    }

    [Fact]
    public async Task Truncated_frame_is_rejected()
    {
        var bytes = new byte[sizeof(int) + 2];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, sizeof(int)), 8);
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<LocalInferenceProtocolException>(async () =>
            await LocalInferenceProtocol.ReadAsync(stream));

        Assert.Equal("local-worker.protocol-truncated-frame", exception.ErrorCode);
    }

    [Fact]
    public void Payload_with_unknown_members_is_rejected()
    {
        using var document = JsonDocument.Parse("""{"unknown":true}""");
        var envelope = new LocalInferenceEnvelope(
            1,
            LocalInferenceMessageTypes.Hello,
            Guid.NewGuid(),
            Operation: null,
            document.RootElement.Clone());

        var exception = Assert.Throws<LocalInferenceProtocolException>(() =>
            LocalInferenceProtocol.ReadPayload<LocalInferenceHelloRequest>(envelope));

        Assert.Equal("local-worker.protocol-invalid-payload", exception.ErrorCode);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1)]
    public void Version_negotiation_selects_highest_common_version(
        int minimum,
        int maximum,
        int expected)
    {
        Assert.Equal(expected, LocalInferenceProtocol.NegotiateVersion(minimum, maximum));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    public void Version_negotiation_rejects_no_common_version(int minimum, int maximum)
    {
        var exception = Assert.Throws<LocalInferenceProtocolException>(() =>
            LocalInferenceProtocol.NegotiateVersion(minimum, maximum));
        Assert.Equal("local-worker.protocol-version-unsupported", exception.ErrorCode);
    }

    [Fact]
    public void Version_negotiation_rejects_an_inverted_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalInferenceProtocol.NegotiateVersion(2, 1));
    }
}
