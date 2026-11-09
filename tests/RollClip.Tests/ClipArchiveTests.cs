using RollClip.Core;

namespace RollClip.Tests;

public sealed class ClipArchiveTests
{
    [Fact]
    public async Task CopiesIntoDatedFolderWithoutChangingOriginalOrDuplicatingBackup()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var archive = root.CreateSubdirectory("archive").FullName;
            var original = Path.Combine(root.FullName, "clip.mp4");
            byte[] bytes = [1, 2, 3, 4];
            File.WriteAllBytes(original, bytes);
            var clip = new Clip(Guid.NewGuid(), original, "Game / Match", new DateTimeOffset(2026, 11, 9, 15, 30, 0, TimeSpan.Zero),
                bytes.Length, null);

            Assert.True(await ClipArchive.CopyAsync(archive, clip, CancellationToken.None));
            Assert.False(await ClipArchive.CopyAsync(archive, clip, CancellationToken.None));

            var copy = Assert.Single(Directory.GetFiles(archive, "*", SearchOption.AllDirectories));
            Assert.Equal(Path.Combine(archive, "2026", "11"), Path.GetDirectoryName(copy));
            Assert.Equal(bytes, File.ReadAllBytes(copy));
            Assert.Equal(bytes, File.ReadAllBytes(original));

            File.WriteAllBytes(copy, [4, 3, 2, 1]);
            await Assert.ThrowsAsync<IOException>(() => ClipArchive.CopyAsync(archive, clip, CancellationToken.None));
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(copy));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task MissingArchiveFolderDoesNotCreateNewDestination()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var missing = Path.Combine(root.FullName, "unavailable");
            var clip = new Clip(Guid.NewGuid(), "clip.mp4", "Clip", DateTimeOffset.UtcNow, 5, null);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ClipArchive.CopyAsync(missing, clip, CancellationToken.None));
            Assert.False(Directory.Exists(missing));
        }
        finally { root.Delete(recursive: true); }
    }
}
