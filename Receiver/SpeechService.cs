using System.Reflection;
using System.Runtime.InteropServices;

namespace CloudRemoteShouter.Receiver;

public sealed class SpeechService : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private SpeechRequest? _pending;
    private bool _stopRequested;
    private bool _disposed;

    public SpeechService()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CloudRemoteShouter.Speech",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SpeakLatest(string text, int speed, double volume)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending = new SpeechRequest(text, Math.Clamp(speed, -10, 10), Math.Clamp(volume, 0, 1));
            _stopRequested = false;
        }
        _signal.Set();
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending = null;
            _stopRequested = true;
        }
        _signal.Set();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            _stopRequested = true;
        }
        _signal.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }

    private void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        object? voice = null;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null) return;
            voice = Activator.CreateInstance(type);
            while (true)
            {
                _signal.WaitOne();
                SpeechRequest? request;
                bool stop;
                bool dispose;
                lock (_sync)
                {
                    request = _pending;
                    _pending = null;
                    stop = _stopRequested;
                    _stopRequested = false;
                    dispose = _disposed;
                }

                if (stop || request is not null)
                {
                    // SVSFlagsAsync | SVSFPurgeBeforeSpeak clears speech already queued by SAPI.
                    type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { "", 3 });
                }
                if (request is not null)
                {
                    type.InvokeMember("Rate", BindingFlags.SetProperty, null, voice, new object[] { request.Speed });
                    type.InvokeMember("Volume", BindingFlags.SetProperty, null, voice, new object[] { (int)Math.Round(request.Volume * 100) });
                    type.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { request.Text, 1 });
                }
                if (dispose) break;
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Windows TTS failed", exception);
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice);
        }
    }

    private sealed record SpeechRequest(string Text, int Speed, double Volume);
}
