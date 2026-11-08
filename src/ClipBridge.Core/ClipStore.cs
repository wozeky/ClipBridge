using System.Text.Json;

namespace ClipBridge.Core;

// The desktop process holds a single-instance mutex. The lock coordinates its UI and upload worker.
public sealed class ClipStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<Guid, Clip> _clips;

    public ClipStore(string path)
    {
        _path = Path.GetFullPath(path);
        _clips = File.Exists(_path)
            ? (JsonSerializer.Deserialize<Clip[]>(File.ReadAllBytes(_path))
                ?? throw new InvalidDataException("Klip arşivi okunamadı.")).ToDictionary(clip => clip.Id)
            : [];
        if (_clips.Values.Any(clip => clip.State == UploadState.Uploading))
        {
            // The previous request might have reached the server before the process exited.
            var recovered = _clips.Values.Select(clip => clip.State == UploadState.Uploading
                ? clip with { State = UploadState.Failed, Error = "Yükleme kesildi. Yeniden deneyebilirsiniz." }
                : clip).ToDictionary(clip => clip.Id);
            Save(recovered);
        }
    }

    public IReadOnlyList<Clip> Snapshot()
    {
        lock (_gate)
            return _clips.Values.OrderByDescending(clip => clip.CreatedAt).ToArray();
    }

    public Clip? Find(Guid id)
    {
        lock (_gate)
            return _clips.GetValueOrDefault(id);
    }

    public void Add(Clip clip)
    {
        lock (_gate)
        {
            var updated = new Dictionary<Guid, Clip>(_clips);
            updated.Add(clip.Id, clip);
            Save(updated);
        }
    }

    public int RecoverLocalClips(string directory, CancellationToken cancellationToken = default)
    {
        var knownIds = Snapshot().Select(clip => clip.Id).ToHashSet();
        var recovered = new List<Clip>();
        foreach (var path in Directory.EnumerateFiles(Path.GetFullPath(directory), "*.mp4"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            var suffixStart = name.LastIndexOf('-') + 1;
            if (!Guid.TryParseExact(name.AsSpan(suffixStart), "N", out var id) || knownIds.Contains(id))
                continue;
            try
            {
                if (Mp4.ReadDuration(path) is { } duration)
                {
                    var info = new FileInfo(path);
                    var title = suffixStart == 0 ? "Kurtarılan klip" : name[..(suffixStart - 1)];
                    recovered.Add(new Clip(id, path, title, info.LastWriteTimeUtc, info.Length, duration));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A file may be in use, moved, or inaccessible while the directory is scanned.
            }
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = new Dictionary<Guid, Clip>(_clips);
            foreach (var clip in recovered)
                updated.TryAdd(clip.Id, clip);
            var added = updated.Count - _clips.Count;
            if (added > 0)
                Save(updated);
            return added;
        }
    }

    public Clip? Change(Guid id, Func<Clip, Clip?> update)
    {
        lock (_gate)
        {
            if (!_clips.TryGetValue(id, out var existing) || update(existing) is not { } clip)
                return null;
            Save(new Dictionary<Guid, Clip>(_clips) { [id] = clip });
            return clip;
        }
    }

    private void Save(Dictionary<Guid, Clip> updated)
    {
        AtomicJson.Write(_path, updated.Values.ToArray());
        _clips = updated;
    }
}

public static class AtomicJson
{
    public static T? Read<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : default;

    public static void Write<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
