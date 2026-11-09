using System.Buffers.Binary;
using RollClip.Core;

namespace RollClip.Tests;

public sealed class Mp4Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReadsMovieDurationFromVersionedHeaders(int version)
    {
        var file = Path.GetTempFileName();
        try
        {
            var offset = version == 0 ? 12 : 20;
            var bytes = new byte[16 + offset + (version == 0 ? 8 : 12)];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
            "moov"u8.CopyTo(bytes.AsSpan(4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)bytes.Length - 8);
            "mvhd"u8.CopyTo(bytes.AsSpan(12));
            bytes[16] = (byte)version;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16 + offset), 1000);
            if (version == 0)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20 + offset), 16250);
            else
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(20 + offset), 16250);
            File.WriteAllBytes(file, bytes);
            Assert.Equal(TimeSpan.FromSeconds(16.25), Mp4.ReadDuration(file));
        }
        finally { File.Delete(file); }
    }
}
