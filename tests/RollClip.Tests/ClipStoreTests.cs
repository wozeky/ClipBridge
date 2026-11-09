using System.Buffers.Binary;
using RollClip.Core;

namespace RollClip.Tests;

public sealed class ClipStoreTests
{
    [Fact]
    public void RecoversOnlyMissingManagedClipsAsLocalAndPersistsWithoutDuplicates()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var index = Path.Combine(root.FullName, "library.json");
            var store = new ClipStore(index);
            var id = Guid.NewGuid();
            var path = Path.Combine(root.FullName, $"Klip 2026-11-08 22-10-00-{id:N}.mp4");
            var bytes = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, 36);
            "moov"u8.CopyTo(bytes.AsSpan(4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 28);
            "mvhd"u8.CopyTo(bytes.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32), 2500);
            File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(Path.Combine(root.FullName, "unmanaged.mp4"), bytes);
            File.WriteAllBytes(Path.Combine(root.FullName, $"{Guid.NewGuid():N}.mp4.partial"), bytes);
            File.WriteAllBytes(Path.Combine(root.FullName, $"{Guid.NewGuid():N}.mp4"), []);

            Assert.Equal(1, store.RecoverLocalClips(root.FullName));
            Assert.Equal(0, store.RecoverLocalClips(root.FullName));
            var recovered = Assert.Single(new ClipStore(index).Snapshot());
            Assert.Equal(id, recovered.Id);
            Assert.Equal(path, recovered.FilePath);
            Assert.Equal(UploadState.Local, recovered.State);
            Assert.Equal(TimeSpan.FromSeconds(2.5), recovered.Duration);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { root.Delete(recursive: true); }
    }
}
