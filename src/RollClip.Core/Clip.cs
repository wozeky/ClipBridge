namespace RollClip.Core;

public enum UploadState
{
    Local,
    Queued,
    Uploading,
    Uploaded,
    Failed,
    Cancelled
}

public enum Provider
{
    Litterbox,
    Gofile
}

public sealed record Clip(
    Guid Id, string FilePath, string Name, DateTimeOffset CreatedAt, long Bytes, TimeSpan? Duration,
    string? Game = null, Provider Provider = Provider.Litterbox, UploadState State = UploadState.Local,
    string? ShareUrl = null, string? Error = null, int Attempts = 0, DateTimeOffset? NextAttempt = null,
    UploadTiming? Timing = null, Provider? FallbackProvider = null, string? PendingArchiveFolder = null,
    DateTimeOffset? LinkExpiresAt = null, bool SeparateMicrophone = false, string? ImportKey = null, double[]? Bookmarks = null,
    bool Favorite = false, DateTimeOffset? BackupCopiedAt = null, string? BackupFolder = null, DateTimeOffset? LocalRemovedAt = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanReplaceLocal => LocalRemovedAt is null && State == UploadState.Local && ShareUrl is null && BackupCopiedAt is null && PendingArchiveFolder is null;

    public bool LinkExpired(DateTimeOffset now) => LinkExpiresAt is { } expires && expires <= now;
}

public sealed record UploadTiming(TimeSpan BodyWritten, TimeSpan LinkReady)
{
    public TimeSpan ResponseWait => LinkReady - BodyWritten;
}

public sealed record UploadResult(Uri ShareUrl, UploadTiming Timing);
