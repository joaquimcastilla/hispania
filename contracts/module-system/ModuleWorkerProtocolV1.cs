using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hispania.ModuleSystem.Contracts;

/// <summary>Public protocol messages for a future supervised out-of-process module host.</summary>
public static class ModuleWorkerProtocolV1
{
    public const string ProtocolId = "hispania.module-worker";
    public const int Version = 1;
    public const int DefaultMaximumFrameBytes = 1024 * 1024;

    public static ModuleWorkerNegotiationV1 Negotiate(ModuleWorkerHelloV1 hello, string expectedModuleId, string expectedModuleVersion, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (!string.Equals(hello.ModuleId, expectedModuleId, StringComparison.Ordinal) || !string.Equals(hello.ModuleVersion, expectedModuleVersion, StringComparison.Ordinal))
            return new(false, 0, "Module process identity does not match the authorized package.");
        byte[] suppliedToken;
        if (hello.AuthenticationToken is null || hello.AuthenticationToken.Length > 256)
            return new(false, 0, "Module process authentication token is malformed.");
        try { suppliedToken = Convert.FromBase64String(hello.AuthenticationToken); }
        catch (FormatException) { return new(false, 0, "Module process authentication token is malformed."); }
        var expectedTokenBytes = Convert.FromBase64String(expectedToken);
        if (!CryptographicOperations.FixedTimeEquals(expectedTokenBytes, suppliedToken))
            return new(false, 0, "Module process authentication failed.");
        if (!string.Equals(hello.ProtocolId, ProtocolId, StringComparison.Ordinal) || hello.MinimumProtocolVersion < 1 || hello.MaximumProtocolVersion < hello.MinimumProtocolVersion)
            return new(false, 0, "Module process protocol declaration is invalid.");
        if (hello.MinimumProtocolVersion > Version || hello.MaximumProtocolVersion < Version)
            return new(false, 0, "No mutually supported module protocol version.");
        return new(true, Version, null);
    }

    public static async ValueTask WriteFrameAsync<T>(Stream destination, T message, int maximumFrameBytes = DefaultMaximumFrameBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(message);
        ValidateLimit(maximumFrameBytes);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > maximumFrameBytes) throw new InvalidDataException("Module protocol frame exceeds the configured limit.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadFrameAsync<T>(Stream source, int maximumFrameBytes = DefaultMaximumFrameBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateLimit(maximumFrameBytes);
        var header = new byte[sizeof(int)];
        var firstRead = await source.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0) return default;
        await ReadExactlyAsync(source, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > maximumFrameBytes) throw new InvalidDataException("Module protocol frame length is invalid or exceeds the configured limit.");
        var payload = new byte[length];
        await ReadExactlyAsync(source, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(payload);
            RejectDuplicateFields(document.RootElement);
            var result = document.RootElement.Deserialize<T>() ?? throw new InvalidDataException("Module protocol frame contained an empty message.");
            if (result is ModuleWorkerFrameV1 frame && (frame.Kind is not ("hello" or "negotiation" or "capabilities" or "request" or "response" or "cancel" or "health" or "stop") || frame.Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
                throw new InvalidDataException("Module protocol envelope is invalid.");
            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Module protocol frame contains invalid JSON.", exception);
        }
    }

    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Module protocol contains duplicate fields.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }

    private static async ValueTask ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Module protocol frame ended unexpectedly.");
            buffer = buffer[read..];
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
    }
}

public sealed record ModuleWorkerHelloV1(string ModuleId, string ModuleVersion, string ProtocolId, int MinimumProtocolVersion, int MaximumProtocolVersion, string AuthenticationToken);
public sealed record ModuleWorkerNegotiationV1(bool Accepted, int SelectedProtocolVersion, string? Error);
public sealed record ModuleWorkerCapabilityV1(string ContractId, string ContractVersion);
public sealed record ModuleWorkerCapabilitiesV1(IReadOnlyList<ModuleWorkerCapabilityV1> Provides, IReadOnlyList<ModuleWorkerCapabilityV1> Requires);
public sealed record ModuleWorkerRequestV1(string RequestId, string ContractId, string ContractVersion, string Operation, JsonElement Payload);
public sealed record ModuleWorkerResponseV1(string RequestId, bool Succeeded, JsonElement? Payload, ModuleWorkerErrorV1? Error);
public sealed record ModuleWorkerErrorV1(string Code, string Message, bool Retryable);
public sealed record ModuleWorkerCancelV1(string RequestId);
public sealed record ModuleWorkerHealthRequestV1(string CorrelationId);
public sealed record ModuleWorkerHealthResponseV1(string CorrelationId, bool Healthy, string? Detail);
public sealed record ModuleWorkerStopV1(string Reason);

/// <summary>Typed payload envelope used on the bidirectional worker stream.</summary>
public sealed record ModuleWorkerFrameV1(string Kind, JsonElement Payload)
{
    public static ModuleWorkerFrameV1 From<T>(string kind, T payload) => new(kind, JsonSerializer.SerializeToElement(payload));
    public T Decode<T>() => Payload.Deserialize<T>() ?? throw new InvalidDataException($"Protocol frame '{Kind}' has an empty payload.");
}
