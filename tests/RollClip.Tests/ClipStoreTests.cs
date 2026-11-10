using System.Buffers.Binary;
using RollClip.Core;

namespace RollClip.Tests;

public sealed class ClipStoreTests
{
    [Fact]
    public void PendingArchiveFolderSurvivesRestartAndUploadUpdates()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var index = Path.Combine(root.FullName, "library.json");
            var archive = Path.Combine(root.FullName, "archive");
            var clip = new Clip(Guid.NewGuid(), "clip.mp4", "Clip", DateTimeOffset.UtcNow, 8, null,
                PendingArchiveFolder: archive);
            var store = new ClipStore(index);
            store.Add(clip);
            store.Change(clip.Id, current => current with { State = UploadState.Queued });

            var reopened = new ClipStore(index);
            Assert.Equal(archive, reopened.Find(clip.Id)!.PendingArchiveFolder);
            reopened.Change(clip.Id, current => current with { PendingArchiveFolder = null });
            var completed = new ClipStore(index).Find(clip.Id)!;
            Assert.Null(completed.PendingArchiveFolder);
            Assert.Equal(UploadState.Queued, completed.State);
        }
        finally { root.Delete(recursive: true); }
    }

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
    [Fact]
    public void FavoritePersistsAndDeletionRejectsActiveUploadsAndExternalFiles()
    {
        using var root = new TemporaryDirectory();
        var managed = Directory.CreateDirectory(Path.Combine(root.Path, "managed")).FullName;
        var local = Path.Combine(managed, "owned.mp4");
        var external = Path.Combine(root.Path, "external.mp4");
        File.WriteAllBytes(local, [1, 2, 3]);
        File.WriteAllBytes(external, [4, 5, 6]);
        var index = Path.Combine(root.Path, "library.json");
        var store = new ClipStore(index);
        var clip = new Clip(Guid.NewGuid(), local, "Local", DateTimeOffset.UtcNow, 3, null);
        var other = clip with { Id = Guid.NewGuid(), FilePath = external };
        store.Add(clip);
        store.Add(other);
        store.Change(clip.Id, item => item with { Favorite = true, State = UploadState.Queued });

        store = new ClipStore(index);
        Assert.True(store.Find(clip.Id)!.Favorite);
        Assert.False(store.DeleteLocal(clip.Id, managed, out _));
        Assert.False(store.DeleteLocal(other.Id, managed, out _));
        store.Change(clip.Id, item => item with { State = UploadState.Local });
        Assert.True(store.DeleteLocal(clip.Id, managed, out var pending));
        Assert.Null(pending);
        Assert.False(File.Exists(local));
        Assert.True(File.Exists(external));
        Assert.Null(new ClipStore(index).Find(clip.Id));
    }

    [Fact]
    public void StorageCleanupCanRetainUploadHistory()
    {
        using var root = new TemporaryDirectory();
        var local = Path.Combine(root.Path, "owned.mp4");
        File.WriteAllBytes(local, [1, 2, 3]);
        var index = Path.Combine(root.Path, "library.json");
        var store = new ClipStore(index);
        var clip = new Clip(Guid.NewGuid(), local, "Local", DateTimeOffset.UtcNow, 3, null,
            State: UploadState.Uploaded, ShareUrl: "https://example.com/clip");
        store.Add(clip);

        Assert.True(store.DeleteLocal(clip.Id, root.Path, out _, keepHistory: true));
        var retained = Assert.Single(new ClipStore(index).Snapshot());
        Assert.NotNull(retained.LocalRemovedAt);
        Assert.Equal(clip.ShareUrl, retained.ShareUrl);
        Assert.False(File.Exists(local));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
