using System.Buffers.Binary;
using System.Text;

namespace McpProxy.Models;

/// <summary>Reads AWS EventStream frames used by Bedrock streaming responses.</summary>
internal static class AwsEventStreamReader
{
    private const int PreludeLength = 12;
    private const int MessageCrcLength = 4;
    private const int MaximumMessageLength = 16 * 1024 * 1024;

    /// <summary>Reads EventStream messages until the underlying stream ends.</summary>
    public static async IAsyncEnumerable<AwsEventStreamMessage> ReadAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prelude = new byte[PreludeLength];

        while (true)
        {
            var first = await stream.ReadAsync(prelude.AsMemory(0, 1), cancellationToken);
            if (first == 0)
            {
                yield break;
            }

            await ReadExactlyAsync(stream, prelude.AsMemory(1), cancellationToken);

            var totalLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(0, 4)));
            var headersLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(4, 4)));
            if (totalLength < PreludeLength + MessageCrcLength ||
                totalLength > MaximumMessageLength ||
                headersLength < 0 ||
                headersLength > totalLength - PreludeLength - MessageCrcLength)
            {
                throw new InvalidOperationException("Bedrock returned an invalid AWS EventStream frame.");
            }

            var expectedPreludeCrc = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8, 4));
            var actualPreludeCrc = Crc32(prelude.AsSpan(0, 8));
            if (expectedPreludeCrc != actualPreludeCrc)
            {
                throw new InvalidOperationException("Bedrock EventStream prelude checksum validation failed.");
            }

            var remainingLength = totalLength - PreludeLength;
            var remaining = new byte[remainingLength];
            await ReadExactlyAsync(stream, remaining, cancellationToken);

            var fullWithoutMessageCrc = new byte[totalLength - MessageCrcLength];
            prelude.CopyTo(fullWithoutMessageCrc, 0);
            remaining.AsSpan(0, remaining.Length - MessageCrcLength).CopyTo(fullWithoutMessageCrc.AsSpan(PreludeLength));

            var expectedMessageCrc = BinaryPrimitives.ReadUInt32BigEndian(remaining.AsSpan(remaining.Length - MessageCrcLength));
            var actualMessageCrc = Crc32(fullWithoutMessageCrc);
            if (expectedMessageCrc != actualMessageCrc)
            {
                throw new InvalidOperationException("Bedrock EventStream message checksum validation failed.");
            }

            var headers = ParseHeaders(remaining.AsSpan(0, headersLength));
            var payloadLength = totalLength - PreludeLength - headersLength - MessageCrcLength;
            var payload = remaining.AsMemory(headersLength, payloadLength).ToArray();

            yield return new AwsEventStreamMessage(headers, payload);
        }
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> bytes)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;

        while (offset < bytes.Length)
        {
            var nameLength = bytes[offset++];
            if (offset + nameLength + 1 > bytes.Length)
            {
                throw new InvalidOperationException("Bedrock returned malformed EventStream headers.");
            }

            var name = Encoding.UTF8.GetString(bytes.Slice(offset, nameLength));
            offset += nameLength;
            var type = bytes[offset++];

            switch (type)
            {
                case 0: // true
                    headers[name] = "true";
                    break;
                case 1: // false
                    headers[name] = "false";
                    break;
                case 2: // byte
                    EnsureRemaining(bytes, offset, 1);
                    headers[name] = unchecked((sbyte)bytes[offset++]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 3: // short
                    EnsureRemaining(bytes, offset, 2);
                    headers[name] = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(offset, 2))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    offset += 2;
                    break;
                case 4: // int
                    EnsureRemaining(bytes, offset, 4);
                    headers[name] = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    offset += 4;
                    break;
                case 5: // long
                case 8: // timestamp
                    EnsureRemaining(bytes, offset, 8);
                    headers[name] = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(offset, 8))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    offset += 8;
                    break;
                case 6: // byte array
                case 7: // string
                    EnsureRemaining(bytes, offset, 2);
                    var valueLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                    offset += 2;
                    EnsureRemaining(bytes, offset, valueLength);
                    if (type == 7)
                    {
                        headers[name] = Encoding.UTF8.GetString(bytes.Slice(offset, valueLength));
                    }
                    offset += valueLength;
                    break;
                case 9: // UUID
                    EnsureRemaining(bytes, offset, 16);
                    headers[name] = Convert.ToHexString(bytes.Slice(offset, 16));
                    offset += 16;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported AWS EventStream header type '{type}'.");
            }
        }

        return headers;
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> bytes, int offset, int required)
    {
        if (required < 0 || offset < 0 || offset + required > bytes.Length)
        {
            throw new InvalidOperationException("Bedrock returned malformed EventStream headers.");
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Bedrock EventStream ended in the middle of a frame.");
            }
            offset += read;
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }
}

/// <summary>One decoded AWS EventStream message.</summary>
internal sealed record AwsEventStreamMessage(IReadOnlyDictionary<string, string> Headers, byte[] Payload);
