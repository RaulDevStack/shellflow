using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ShellFlow.Core;

/// <summary>
/// Wire format used when a second ShellFlow launch hands its command line to the running instance:
/// a 4-byte little-endian length followed by a UTF-8 JSON array of argument strings. The receiver parses the
/// arguments with <see cref="StartupArguments"/>, so nothing beyond a folder, a profile id and a mode is honored.
/// </summary>
public static class InstanceMessage
{
    public const int MaxPayloadBytes = 64 * 1024;
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 4096;

    public static byte[] Encode(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var trimmed = args.Take(MaxArguments).Select(a => a.Length > MaxArgumentLength ? a[..MaxArgumentLength] : a).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(trimmed, InstanceMessageJsonContext.Default.StringArray);
        var buffer = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, json.Length);
        json.CopyTo(buffer, 4);
        return buffer;
    }

    /// <summary>Reads one message. Returns null for anything malformed or oversized.</summary>
    public static async Task<string[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxPayloadBytes)
        {
            return null;
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Decode(payload);
    }

    public static string[]? Decode(ReadOnlySpan<byte> payload)
    {
        try
        {
            var args = JsonSerializer.Deserialize(payload, InstanceMessageJsonContext.Default.StringArray);
            if (args is null || args.Length > MaxArguments || args.Any(a => a is null || a.Length > MaxArgumentLength || a.Contains('\0')))
            {
                return null;
            }

            return args;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class InstanceMessageJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
