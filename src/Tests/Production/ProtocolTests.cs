using System.Buffers.Binary;
using System.Text;
using PartitionManager.Core.Execution;
using Xunit;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public class ProtocolTests
{
    private static MemoryStream Frame(byte[] payload, int? declaredLength = null)
    {
        var ms = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength ?? payload.Length);
        ms.Write(header);
        ms.Write(payload);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Request_round_trips()
    {
        var ms = new MemoryStream();
        var request = new HelperRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, HelperRequestKind.Execute, LabelRequest(VirtualDisk()));
        await HelperProtocol.WriteAsync(ms, request, CancellationToken.None);
        ms.Position = 0;
        var back = await HelperProtocol.ReadAsync<HelperRequest>(ms, CancellationToken.None);
        Assert.Equal(request.RequestId, back!.RequestId);
        Assert.Equal(request.Operation!.Disk.LayoutFingerprint, back.Operation!.Disk.LayoutFingerprint);
        Assert.Equal("NewLabel", back.Operation.NewLabel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HelperProtocol.MaxMessageBytes + 1)]
    public async Task Bad_lengths_are_rejected(int length)
    {
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => HelperProtocol.ReadAsync<HelperRequest>(Frame([1, 2, 3], length), CancellationToken.None));
        Assert.Equal("BAD_LENGTH", ex.Code);
    }

    [Fact]
    public async Task Truncated_message_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => HelperProtocol.ReadAsync<HelperRequest>(Frame("{\"x\":"u8.ToArray(), 100), CancellationToken.None));
        Assert.Equal("TRUNCATED", ex.Code);
    }

    [Fact]
    public async Task Garbage_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => HelperProtocol.ReadAsync<HelperRequest>(Frame("not json"u8.ToArray()), CancellationToken.None));
        Assert.Equal("MALFORMED", ex.Code);
    }

    [Fact]
    public async Task Unknown_members_are_rejected()
    {
        var json = $"{{\"requestId\":\"{Guid.NewGuid()}\",\"issuedAtUtc\":\"{DateTimeOffset.UtcNow:O}\",\"kind\":\"Ping\",\"operation\":null,\"runAs\":\"SYSTEM\"}}";
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => HelperProtocol.ReadAsync<HelperRequest>(Frame(Encoding.UTF8.GetBytes(json)), CancellationToken.None));
        Assert.Equal("MALFORMED", ex.Code);
    }

    [Fact]
    public async Task Unknown_operation_kind_string_is_rejected()
    {
        var ms = new MemoryStream();
        await HelperProtocol.WriteAsync(ms, new HelperRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, HelperRequestKind.Execute, LabelRequest(VirtualDisk())), CancellationToken.None);
        var text = Encoding.UTF8.GetString(ms.ToArray(), 4, (int)ms.Length - 4).Replace("\"SetVolumeLabel\"", "\"FormatDisk\"");
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => HelperProtocol.ReadAsync<HelperRequest>(Frame(Encoding.UTF8.GetBytes(text)), CancellationToken.None));
        Assert.Equal("MALFORMED", ex.Code);
    }

    [Fact]
    public async Task Clean_end_of_stream_returns_null() =>
        Assert.Null(await HelperProtocol.ReadAsync<HelperRequest>(new MemoryStream(), CancellationToken.None));
}

public class ReplayGuardTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replayed_request_is_rejected()
    {
        var guard = new ReplayGuard(new FixedClock(T0));
        var r = new HelperRequest(Guid.NewGuid(), T0, HelperRequestKind.Ping, null);
        Assert.True(guard.Check(r).IsValid);
        Assert.Equal("REPLAY", guard.Check(r).Code);
    }

    [Fact]
    public void Stale_and_future_requests_are_rejected()
    {
        var guard = new ReplayGuard(new FixedClock(T0));
        Assert.Equal("STALE_REQUEST", guard.Check(new HelperRequest(Guid.NewGuid(), T0 - TimeSpan.FromMinutes(3), HelperRequestKind.Ping, null)).Code);
        Assert.Equal("FUTURE_REQUEST", guard.Check(new HelperRequest(Guid.NewGuid(), T0 + TimeSpan.FromMinutes(1), HelperRequestKind.Ping, null)).Code);
        Assert.Equal("BAD_REQUEST_ID", guard.Check(new HelperRequest(Guid.Empty, T0, HelperRequestKind.Ping, null)).Code);
    }

    [Fact]
    public void Old_entries_are_pruned_but_still_too_old_to_replay()
    {
        var clock = new FixedClock(T0);
        var guard = new ReplayGuard(clock);
        var r = new HelperRequest(Guid.NewGuid(), T0, HelperRequestKind.Ping, null);
        Assert.True(guard.Check(r).IsValid);
        clock.Now = T0 + TimeSpan.FromMinutes(10);
        Assert.Equal("STALE_REQUEST", guard.Check(r).Code);
    }
}
