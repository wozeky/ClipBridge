namespace RollClip.Core;

public sealed class UploadQueue
{
    private const int MaxAttempts = 3;
    private readonly ClipStore _store;
    private readonly Dictionary<Provider, IUploadProvider> _providers;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Func<int> _speedLimit;
    private readonly object _activeGate = new();
    private Guid? _activeId;
    private CancellationTokenSource? _activeUpload;

    public UploadQueue(ClipStore store, IEnumerable<IUploadProvider> providers, Func<int> speedLimit)
    {
        _store = store;
        _providers = providers.ToDictionary(provider => provider.Id);
        _speedLimit = speedLimit;
    }

    public event Action<Clip>? Changed;
    public event Action<Guid, double>? Progress;

    public Clip? Enqueue(Guid id, Provider provider, Provider? fallback = null, bool renew = false)
    {
        if (!_providers.ContainsKey(provider) || (fallback is { } other && (other == provider || !_providers.ContainsKey(other))))
            throw new ArgumentOutOfRangeException(nameof(provider));
        var clip = _store.Change(id, current => (current.State is UploadState.Local or UploadState.Failed or UploadState.Cancelled || (renew && current.State == UploadState.Uploaded))
            ? current with { State = UploadState.Queued, Provider = provider, Attempts = 0, NextAttempt = null, Error = null,
                FallbackProvider = fallback }
            : null);
        if (clip is not null)
        {
            Changed?.Invoke(clip);
            Signal();
        }
        return clip;
    }

    public bool IsActive(Guid id)
    {
        lock (_activeGate)
            return _activeId == id;
    }

    public bool Cancel(Guid id)
    {
        var clip = _store.Change(id, current => current.State is UploadState.Queued or UploadState.Uploading
            ? current with { State = UploadState.Cancelled, Error = null, NextAttempt = null, FallbackProvider = null } : null);
        if (clip is null)
            return false;
        lock (_activeGate)
            if (_activeId == id)
                _activeUpload?.Cancel();
        Changed?.Invoke(clip);
        Signal();
        return true;
    }

    public void Signal()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var queued = _store.Snapshot().Where(clip => clip.State == UploadState.Queued)
                .OrderBy(clip => clip.NextAttempt ?? DateTimeOffset.MinValue).ThenBy(clip => clip.CreatedAt)
                .FirstOrDefault();
            if (queued is not null && (queued.NextAttempt is null || queued.NextAttempt <= now))
                await UploadAsync(queued.Id, cancellationToken);
            else
            {
                var wait = queued?.NextAttempt - now ?? TimeSpan.FromMinutes(1);
                await _wake.WaitAsync(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait,
                    cancellationToken);
            }
        }
    }

    private async Task UploadAsync(Guid id, CancellationToken cancellationToken)
    {
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeGate)
        {
            _activeId = id;
            _activeUpload = active;
        }
        try { await UploadCoreAsync(id, active.Token, cancellationToken); }
        finally
        {
            lock (_activeGate)
            {
                _activeUpload = null;
                _activeId = null;
            }
        }
    }

    private async Task UploadCoreAsync(Guid id, CancellationToken uploadToken, CancellationToken cancellationToken)
    {
        var clip = _store.Change(id, current => current.State == UploadState.Queued
            ? current with { State = UploadState.Uploading, Attempts = current.Attempts + 1, Error = null }
            : null);
        if (clip is null)
            return;
        Changed?.Invoke(clip);
        try
        {
            if (!_providers.TryGetValue(clip.Provider, out var provider))
                throw new UploadException("Yükleme servisi bulunamadı.");
            var progress = new CallbackProgress(percent => Progress?.Invoke(id, percent));
            var uploaded = await provider.UploadAsync(clip.FilePath, _speedLimit(), progress, uploadToken);
            Update(id, current => current with { State = UploadState.Uploaded, ShareUrl = uploaded.ShareUrl.AbsoluteUri, FallbackProvider = null,
                Timing = uploaded.Timing, LinkExpiresAt = clip.Provider == Provider.Litterbox ? DateTimeOffset.UtcNow.AddHours(24) : null,
                Error = null, NextAttempt = null });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Update(id, current => current with { State = UploadState.Failed, NextAttempt = null,
                Error = "Uygulama kapatılırken yükleme kesildi." });
            throw;
        }
        catch (OperationCanceledException) when (uploadToken.IsCancellationRequested) { }
        catch (UploadException error) when (error.RejectedForCapacity && clip.FallbackProvider is not null)
        {
            Update(id, current => current with
            {
                State = UploadState.Queued, Provider = clip.FallbackProvider.Value, FallbackProvider = null,
                Attempts = 0, NextAttempt = null, Error = error.Message + " Alternatif servis sıraya alındı."
            });
        }
        catch (Exception error) when (error is UploadException or HttpRequestException or IOException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            var retry = error is HttpRequestException or OperationCanceledException
                || error is UploadException { Retryable: true };
            var canRetry = retry && clip.Attempts < MaxAttempts;
            var delay = TimeSpan.FromSeconds(5 * Math.Pow(2, clip.Attempts - 1));
            if (error is UploadException { RetryAfter: { } requested } && requested > delay)
                delay = requested > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : requested;
            var message = error is UploadException ? error.Message : error switch
            {
                HttpRequestException => "Yükleme servisine bağlanılamadı.",
                OperationCanceledException => "Yükleme zaman aşımına uğradı.",
                _ => "Yerel video dosyası okunamadı."
            };
            Update(id, current => current with
            {
                State = canRetry ? UploadState.Queued : UploadState.Failed, Error = message,
                NextAttempt = canRetry ? DateTimeOffset.UtcNow + delay : null
            });
        }
    }

    private void Update(Guid id, Func<Clip, Clip> update)
    {
        if (_store.Change(id, item => item.State == UploadState.Uploading ? update(item) : null) is { } clip)
            Changed?.Invoke(clip);
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }
}
