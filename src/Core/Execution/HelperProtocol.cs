using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartitionManager.Core.Execution;

/// <summary>
/// Wire protocol between the unprivileged UI and the elevated helper. Messages are length-prefixed UTF-8 JSON
/// (4-byte little-endian length, max <see cref="MaxMessageBytes"/>). Unknown JSON members are rejected.
/// </summary>
public static class HelperProtocol
{
    // 2: read-only Recovery requests and response payloads (0.5.1).
    public const int Version = 2;
    public const int MaxMessageBytes = 64 * 1024;
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(30);

    public static JsonSerializerOptions Json { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaxMessageBytes) throw new ProtocolException("MESSAGE_TOO_LARGE", "Outgoing message exceeds the size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; returns null on a clean end of stream before any byte of a new message.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
            throw new ProtocolException("BAD_LENGTH", $"Message length {length} is outside 1..{MaxMessageBytes}.");
        var payload = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, payload, ct).ConfigureAwait(false))
            throw new ProtocolException("TRUNCATED", "The connection closed in the middle of a message.");
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new ProtocolException("EMPTY", "The message was empty.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("MALFORMED", "The message is not valid: " + ex.Message);
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new ProtocolException("TRUNCATED", "The connection closed in the middle of a message.");
            }
            read += n;
        }
        return true;
    }
}

public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record HelloMessage(int ProtocolVersion, string Token, int ClientProcessId);

public sealed record HelloReply(bool Accepted, string Code, string Message, bool IsElevated, string WriteMode);

[JsonConverter(typeof(JsonStringEnumConverter<HelperRequestKind>))]
public enum HelperRequestKind { Ping, Execute, Recovery }

public sealed record HelperRequest(Guid RequestId, DateTimeOffset IssuedAtUtc, HelperRequestKind Kind, WriteOperationRequest? Operation,
    PartitionManager.Core.Recovery.RecoveryRequest? Recovery = null);

[JsonConverter(typeof(JsonStringEnumConverter<HelperResponseStatus>))]
public enum HelperResponseStatus { Ok, Rejected, Failed }

public sealed record HelperResponse(Guid RequestId, HelperResponseStatus Status, string Code, string Message, IReadOnlyList<string> Steps)
{
    /// <summary>JSON result of a read-only Recovery request (bounded by the message size limit).</summary>
    public string? Payload { get; init; }

    public static HelperResponse Reject(Guid id, string code, string message) => new(id, HelperResponseStatus.Rejected, code, message, []);
}

/// <summary>Rejects reused request IDs and requests outside the freshness window.</summary>
public sealed class ReplayGuard(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];

    public ValidationResult Check(HelperRequest request)
    {
        var now = _clock.GetUtcNow();
        foreach (var stale in _seen.Where(kv => now - kv.Value > HelperProtocol.MaxRequestAge * 2).Select(kv => kv.Key).ToList())
            _seen.Remove(stale);

        if (request.RequestId == Guid.Empty) return ValidationResult.Fail("BAD_REQUEST_ID", "Request ID is missing.");
        if (request.IssuedAtUtc > now + HelperProtocol.MaxClockSkew) return ValidationResult.Fail("FUTURE_REQUEST", "The request is dated in the future.");
        if (now - request.IssuedAtUtc > HelperProtocol.MaxRequestAge) return ValidationResult.Fail("STALE_REQUEST", "The request is too old.");
        if (!_seen.TryAdd(request.RequestId, request.IssuedAtUtc)) return ValidationResult.Fail("REPLAY", "This request was already received.");
        return ValidationResult.Ok;
    }
}
