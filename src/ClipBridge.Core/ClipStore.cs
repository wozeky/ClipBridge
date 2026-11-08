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
