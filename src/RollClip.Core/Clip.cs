namespace RollClip.Core;

public enum UploadState
{
    Local,
    Queued,
    Uploading,
    Uploaded,
    Failed
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
    UploadTiming? Timing = null, Provider? FallbackProvider = null, string? PendingArchiveFolder = null);

public sealed record UploadTiming(TimeSpan BodyWritten, TimeSpan LinkReady)
{
    public TimeSpan ResponseWait => LinkReady - BodyWritten;
}

public sealed record UploadResult(Uri ShareUrl, UploadTiming Timing);
