using System.Buffers.Binary;

namespace RollClip.Core;

public static class Mp4
{
    public static TimeSpan? ReadDuration(string path)
    {
        using var stream = File.OpenRead(path);
        var duration = ReadBoxes(stream, stream.Length, inMovie: false);
        return duration;
    }

    private static TimeSpan? ReadBoxes(Stream stream, long end, bool inMovie)
    {
        Span<byte> header = stackalloc byte[8];
        Span<byte> extendedSize = stackalloc byte[8];
        while (end - stream.Position >= header.Length)
        {
            var start = stream.Position;
            stream.ReadExactly(header);
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 1)
            {
                if (end - stream.Position < extendedSize.Length)
                    return null;
                stream.ReadExactly(extendedSize);
                length = BinaryPrimitives.ReadUInt64BigEndian(extendedSize);
            }
            if (length == 0)
                length = (ulong)(end - start);
            if (length < (ulong)(stream.Position - start) || length > (ulong)(end - start))
                return null;

            var boxEnd = start + (long)length;
            var type = header[4..];
            if (!inMovie && type.SequenceEqual("moov"u8))
                return ReadBoxes(stream, boxEnd, inMovie: true);
            if (inMovie && type.SequenceEqual("mvhd"u8))
                return ReadMovieHeader(stream, boxEnd);
            stream.Position = boxEnd;
        }
        return null;
    }

    private static TimeSpan? ReadMovieHeader(Stream stream, long end)
    {
        var start = stream.Position;
        if (start >= end)
            return null;
        var version = stream.ReadByte();
        var offset = version switch { 0 => 12, 1 => 20, _ => -1 };
        var size = version == 0 ? 8 : 12;
        if (offset < 0 || end - start < offset + size)
            return null;
        stream.Position = start + offset;
        Span<byte> fields = stackalloc byte[12];
        stream.ReadExactly(fields[..size]);
        var scale = BinaryPrimitives.ReadUInt32BigEndian(fields);
        ulong ticks = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(fields[4..])
            : BinaryPrimitives.ReadUInt64BigEndian(fields[4..]);
        var unknown = version == 0 ? uint.MaxValue : ulong.MaxValue;
        var seconds = scale == 0 ? 0 : (double)ticks / scale;
        return ticks != unknown && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : null;
    }
}
