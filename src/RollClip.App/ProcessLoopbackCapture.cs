// WASAPI activation and raw COM layout adapted from WKI-Clipper (MIT), commit 528f370.
using System.Runtime.InteropServices;
using NAudio.Wave;
using RollClip.Core;

namespace RollClip.App;

internal sealed class ProcessLoopbackCapture(uint processId) : IWaveIn, IProcessAudioActivation
{
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private readonly TaskCompletionSource<RawAudioClient?> _activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private const int BlockAlign = 8;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public WaveFormat WaveFormat
    {
        get => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        set => throw new NotSupportedException();
    }

    public void StartRecording()
    {
        if (_worker is null)
            _worker = Task.Run(Capture);
        _started.Task.GetAwaiter().GetResult();
    }

    private void Capture()
    {
        try
        {
            using var client = Activate();
            var format = Marshal.AllocCoTaskMem(18);
            try
            {
                Marshal.WriteInt16(format, 0, 3); // IEEE float
                Marshal.WriteInt16(format, 2, 2);
                Marshal.WriteInt32(format, 4, 48000);
                Marshal.WriteInt32(format, 8, 48000 * BlockAlign);
                Marshal.WriteInt16(format, 12, BlockAlign);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);
                Marshal.ThrowExceptionForHR(client.Initialize(0, 0x88020000, 2_000_000, 0, format));
            }
            finally { Marshal.FreeCoTaskMem(format); }
            Marshal.ThrowExceptionForHR(client.GetService(CaptureClientId, out var capturePtr));
            using var capture = new RawCaptureClient(capturePtr);
            Marshal.ThrowExceptionForHR(client.Start());
            _started.TrySetResult();
            try
            {
                var samples = Array.Empty<byte>();
                while (!_stop.Token.WaitHandle.WaitOne(10))
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var pending));
                    while (pending > 0 && !_stop.IsCancellationRequested)
                    {
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                        try
                        {
                            var bytes = checked((int)frames * BlockAlign);
                            if (samples.Length < bytes)
                                samples = new byte[bytes];
                            if ((flags & 2) != 0)
                                Array.Clear(samples, 0, bytes);
                            else if (bytes > 0)
                                Marshal.Copy(data, samples, 0, bytes);
                            if (bytes > 0)
                                DataAvailable?.Invoke(this, new WaveInEventArgs(samples, bytes));
                        }
                        finally { capture.ReleaseBuffer(frames); }
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out pending));
                    }
                }
            }
            finally { client.Stop(); }
        }
        catch (Exception error)
        {
            _started.TrySetException(new CaptureException("Seçili oyunun sesi yakalanamadı. " + error.Message));
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private RawAudioClient Activate()
    {
        if (processId == 0)
            throw new CaptureException("Ses için pencerenin işlem kimliği okunamadı.");
        var blob = Marshal.AllocCoTaskMem(12);
        try
        {
            Marshal.WriteInt32(blob, 0, 1); // Process loopback
            Marshal.WriteInt32(blob, 4, unchecked((int)processId));
            Marshal.WriteInt32(blob, 8, 0); // Include process tree
            var parameters = new ActivationBlob { Type = 65, Size = 12, Data = blob };
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", AudioClientId, ref parameters, this, out var operation));
            try
            {
                try
                {
                    return _activation.Task.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).GetAwaiter().GetResult()
                        ?? throw new CaptureException("Oyun sesi başlatılamadı.");
                }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException)
                {
                    if (!_activation.TrySetResult(null) && _activation.Task.IsCompletedSuccessfully)
                        _activation.Task.Result?.Dispose();
                    throw;
                }
            }
            finally { Marshal.ReleaseComObject(operation); }
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    void IProcessAudioActivation.ActivateCompleted(IProcessAudioOperation operation)
    {
        try
        {
            operation.GetActivateResult(out var status, out var instance);
            Marshal.ThrowExceptionForHR(status);
            try
            {
                var client = RawAudioClient.FromActivatedObject(instance);
                if (!_activation.TrySetResult(client))
                    client.Dispose();
            }
            finally { Marshal.ReleaseComObject(instance); }
        }
        catch (Exception error) { _activation.TrySetException(error); }
    }

    public void StopRecording()
    {
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        StopRecording();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ActivationBlob
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public int Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("mmdevapi.dll")]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string device,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid, ref ActivationBlob parameters,
        IProcessAudioActivation callback, out IProcessAudioOperation operation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProcessAudioOperation
{
    void GetActivateResult(out int status, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProcessAudioActivation
{
    void ActivateCompleted(IProcessAudioOperation operation);
}
