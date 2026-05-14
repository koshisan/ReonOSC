using System.Buffers.Binary;
using System.Text;

namespace ReonOSC.Osc;

/// <summary>One parsed OSC message: an address pattern and zero or more typed arguments.</summary>
public sealed record OscMessage(string Address, IReadOnlyList<object?> Arguments)
{
    /// <summary>Try to read an argument as a float, applying common type coercions.</summary>
    public bool TryGetFloat(int index, out float value)
    {
        value = 0;
        if (index >= Arguments.Count) return false;
        switch (Arguments[index])
        {
            case float f: value = f; return true;
            case double d: value = (float)d; return true;
            case int i:   value = i; return true;
            case bool b:  value = b ? 1f : 0f; return true;
            default: return false;
        }
    }

    /// <summary>True if any of (int != 0), (bool true), (float >= 0.5), or OSC true tag.</summary>
    public bool TryGetBool(int index, out bool value)
    {
        value = false;
        if (index >= Arguments.Count) return false;
        switch (Arguments[index])
        {
            case bool b: value = b; return true;
            case int i:  value = i != 0; return true;
            case float f: value = f >= 0.5f; return true;
            case double d: value = d >= 0.5; return true;
            default: return false;
        }
    }
}

/// <summary>
/// Minimal OSC 1.0 parser. Handles single messages and bundles, with the
/// argument types VRChat / Pebble Feel / ChairOSC actually emit: i, f, s, T, F.
/// </summary>
public static class OscParser
{
    public static IReadOnlyList<OscMessage> Parse(ReadOnlySpan<byte> packet)
    {
        var messages = new List<OscMessage>();
        ParseInto(packet, messages);
        return messages;
    }

    private static void ParseInto(ReadOnlySpan<byte> data, List<OscMessage> sink)
    {
        if (data.Length == 0) return;

        // Bundle?
        if (data.Length >= 8 && data[0] == '#' && ReadOscString(data, 0, out var first, out _) && first == "#bundle")
        {
            // Skip timetag (8 bytes after the #bundle string with padding = 16)
            int offset = 16;
            while (offset < data.Length)
            {
                if (offset + 4 > data.Length) return;
                int size = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
                offset += 4;
                if (size <= 0 || offset + size > data.Length) return;
                ParseInto(data.Slice(offset, size), sink);
                offset += size;
            }
            return;
        }

        // Plain message
        if (!ReadOscString(data, 0, out var address, out var addrEnd)) return;
        if (addrEnd >= data.Length) { sink.Add(new OscMessage(address, Array.Empty<object?>())); return; }

        if (!ReadOscString(data, addrEnd, out var typeTag, out var tagEnd) || typeTag.Length == 0 || typeTag[0] != ',')
        {
            sink.Add(new OscMessage(address, Array.Empty<object?>()));
            return;
        }

        var args = new List<object?>();
        int cursor = tagEnd;
        for (int i = 1; i < typeTag.Length; i++)
        {
            switch (typeTag[i])
            {
                case 'i':
                    if (cursor + 4 > data.Length) return;
                    args.Add(BinaryPrimitives.ReadInt32BigEndian(data.Slice(cursor, 4)));
                    cursor += 4;
                    break;
                case 'f':
                    if (cursor + 4 > data.Length) return;
                    args.Add(BinaryPrimitives.ReadSingleBigEndian(data.Slice(cursor, 4)));
                    cursor += 4;
                    break;
                case 's':
                    if (!ReadOscString(data, cursor, out var s, out var sEnd)) return;
                    args.Add(s);
                    cursor = sEnd;
                    break;
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                case 'N': args.Add(null); break;
                case 'I': args.Add(double.PositiveInfinity); break;
                case 'b':
                    // blob: int32 length + bytes + padding
                    if (cursor + 4 > data.Length) return;
                    int blobLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(cursor, 4));
                    cursor += 4;
                    if (blobLen < 0 || cursor + blobLen > data.Length) return;
                    args.Add(data.Slice(cursor, blobLen).ToArray());
                    cursor += blobLen;
                    cursor += PadTo4(cursor) - cursor;
                    break;
                default:
                    // unknown type tag; we can't safely advance, abort the message
                    return;
            }
        }

        sink.Add(new OscMessage(address, args));
    }

    private static bool ReadOscString(ReadOnlySpan<byte> data, int offset, out string str, out int endOffset)
    {
        str = "";
        endOffset = offset;
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        if (end >= data.Length) return false;
        str = Encoding.UTF8.GetString(data.Slice(offset, end - offset));
        endOffset = PadTo4(end + 1);
        return endOffset <= data.Length;
    }

    private static int PadTo4(int value) => (value + 3) & ~3;
}
