using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using Android.Speech;
using AndroidX.Core.App;
using PoFightJudge.Mobile.Services;
using AndroidApp = Android.App.Application;

namespace PoFightJudge.Mobile;

/// <summary>AudioRecord at 16 kHz; VoiceCommunication + the platform echo canceller while the host is talking back.</summary>
public sealed class AndroidMicrophone : IMicrophone, IDisposable
{
    private AudioRecord? _record;
    private AcousticEchoCanceler? _aec;
    private NoiseSuppressor? _ns;
    private CancellationTokenSource? _stop;

    public Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing)
    {
        int min = AudioRecord.GetMinBufferSize(Wav.Rate, ChannelIn.Mono, Encoding.Pcm16bit);
        try
        {
            _record = new AudioRecord(voiceProcessing ? AudioSource.VoiceCommunication : AudioSource.Mic, Wav.Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Wav.Rate));
            if (_record.State != State.Initialized)
            {
                return Task.FromResult(false);
            }

            if (voiceProcessing && AcousticEchoCanceler.IsAvailable)
            {
                _aec = AcousticEchoCanceler.Create(_record.AudioSessionId);
                _aec?.SetEnabled(true);
            }

            if (voiceProcessing && NoiseSuppressor.IsAvailable)
            {
                _ns = NoiseSuppressor.Create(_record.AudioSessionId);
                _ns?.SetEnabled(true);
            }

            _record.StartRecording();
        }
        catch (Java.Lang.Exception)
        {
            return Task.FromResult(false);
        }

        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        AudioRecord record = _record;
        _ = Task.Run(() =>
        {
            byte[] frame = new byte[Wav.Rate / 10 * 2]; // 100 ms
            while (!ct.IsCancellationRequested)
            {
                int read = record.Read(frame, 0, frame.Length);
                if (read > 0)
                {
                    onPcm16k(frame.AsSpan(0, read & ~1).ToArray());
                }
            }
        }, ct);
        return Task.FromResult(true);
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _record?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        _aec?.Release();
        _ns?.Release();
        _record?.Release();
        _aec = null;
        _ns = null;
        _record = null;
    }

    public void Dispose()
    {
        Stop();
        _stop?.Dispose();
    }
}

/// <summary>AudioTrack streaming for the host's voice; a one-off track (PCM) or MediaPlayer (MP3) for Watch lines.</summary>
public sealed class AndroidSpeaker : ISpeaker, IDisposable
{
    private readonly BlockingCollection<byte[]> _queue = [];
    private AudioTrack? _track;
    private CancellationTokenSource? _stop;

    public void StartStream(int sampleRate)
    {
        StopStream();
        _track = NewTrack(sampleRate, Math.Max(AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Mono, Encoding.Pcm16bit), sampleRate), AudioTrackMode.Stream);
        _track.Play();
        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        AudioTrack track = _track;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (byte[] chunk in _queue.GetConsumingEnumerable(ct))
                {
                    track.Write(chunk, 0, chunk.Length);
                }
            }
            catch (System.OperationCanceledException)
            {
            }
        }, ct);
    }

    public void Enqueue(byte[] pcm16) => _queue.Add(pcm16);

    public void Clear()
    {
        while (_queue.TryTake(out _))
        {
        }

        if (_track is { } t)
        {
            t.Pause();
            t.Flush();
            t.Play();
        }
    }

    public void StopStream()
    {
        _stop?.Cancel();
        Clear();
        _track?.Stop();
        _track?.Release();
        _track = null;
    }

    public async Task PlayClipAsync(byte[] audio, string format, int sampleRate, CancellationToken ct)
    {
        if (audio.Length == 0)
        {
            return;
        }

        if (string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            string file = Path.Combine(FileSystem.CacheDirectory, "line.mp3");
            await File.WriteAllBytesAsync(file, audio, ct);
            using MediaPlayer player = new();
            TaskCompletionSource done = new();
            player.Completion += (_, _) => done.TrySetResult();
            player.SetDataSource(file);
            player.Prepare();
            player.Start();
            using (ct.Register(() => { player.Stop(); done.TrySetResult(); }))
            {
                await done.Task;
            }

            return;
        }

        using AudioTrack track = NewTrack(sampleRate, audio.Length, AudioTrackMode.Static);
        track.Write(audio, 0, audio.Length);
        track.Play();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(audio.Length / 2.0 / sampleRate) + TimeSpan.FromMilliseconds(150), ct);
        }
        finally
        {
            track.Stop();
        }
    }

    private static AudioTrack NewTrack(int rate, int bytes, AudioTrackMode mode) =>
        new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Speech)!.Build()!)!
            .SetAudioFormat(new AudioFormat.Builder().SetSampleRate(rate)!.SetEncoding(Encoding.Pcm16bit)!.SetChannelMask(ChannelOut.Mono)!.Build()!)!
            .SetBufferSizeInBytes(bytes)!
            .SetTransferMode(mode)!
            .Build()!;

    public void Dispose()
    {
        StopStream();
        _queue.Dispose();
        _stop?.Dispose();
    }
}

/// <summary>
/// SpeechRecognizer — the on-device recognizer where Android has one (12+), otherwise the system recognizer asked to
/// prefer offline. It stops after a pause, so a turn restarts it until the speaker is done.
/// </summary>
public sealed class AndroidSpeechToText : ISpeechToText, IDisposable
{
    private SpeechRecognizer? _recognizer;
    private Intent? _intent;
    private readonly List<string> _finals = [];
    private Action<string>? _onText;
    private bool _listening;

    private static Context Ctx => AndroidApp.Context;

    public bool IsAvailable => SpeechRecognizer.IsRecognitionAvailable(Ctx);

    public bool OnDevice => OperatingSystem.IsAndroidVersionAtLeast(31) && SpeechRecognizer.IsOnDeviceRecognitionAvailable(Ctx);

    public async Task<bool> StartAsync(Action<string> onText)
    {
        if (!IsAvailable || await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            return false;
        }

        _onText = onText;
        _finals.Clear();
        _listening = true;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _recognizer = OnDevice && OperatingSystem.IsAndroidVersionAtLeast(31) ? SpeechRecognizer.CreateOnDeviceSpeechRecognizer(Ctx) : SpeechRecognizer.CreateSpeechRecognizer(Ctx);
            _recognizer!.SetRecognitionListener(new Listener(this));
            _intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            _intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            _intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
            _intent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);
            _recognizer.StartListening(_intent);
        });
        return true;
    }

    public async Task<string> StopAsync()
    {
        _listening = false;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _recognizer?.StopListening();
            _recognizer?.Destroy();
            _recognizer = null;
        });
        return string.Join(' ', _finals).Trim();
    }

    public void Dispose()
    {
        _recognizer?.Destroy();
        _recognizer?.Dispose();
        _intent?.Dispose();
    }

    private void Restart() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_listening && _recognizer is not null && _intent is not null)
        {
            _recognizer.StartListening(_intent);
        }
    });

    private sealed class Listener(AndroidSpeechToText owner) : Java.Lang.Object, IRecognitionListener
    {
        public void OnResults(Bundle? results)
        {
            if (results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition)?.FirstOrDefault() is { Length: > 0 } text)
            {
                owner._finals.Add(text);
                owner._onText?.Invoke(string.Join(' ', owner._finals));
            }

            owner.Restart();
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            if (partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition)?.FirstOrDefault() is { Length: > 0 } text)
            {
                owner._onText?.Invoke(string.Join(' ', owner._finals.Append(text)));
            }
        }

        public void OnError(SpeechRecognizerError error) => owner.Restart();

        public void OnBeginningOfSpeech()
        {
        }

        public void OnBufferReceived(byte[]? buffer)
        {
        }

        public void OnEndOfSpeech()
        {
        }

        public void OnEvent(int eventType, Bundle? @params)
        {
        }

        public void OnReadyForSpeech(Bundle? @params)
        {
        }

        public void OnRmsChanged(float rmsdB)
        {
        }
    }
}

public sealed class AndroidKeepAlive : IFightKeepAlive
{
    private static Context Ctx => AndroidApp.Context;

    public void Start(string status) => Send(status);

    public void Update(string status) => Send(status);

    public void Stop() => Ctx.StopService(new Intent(Ctx, typeof(FightService)));

    private static void Send(string status) =>
        Ctx.StartForegroundService(new Intent(Ctx, typeof(FightService)).PutExtra(FightService.StatusExtra, status));
}

/// <summary>
/// Item 1: while a fight is live (or recording), a microphone + media-playback foreground service with a partial wake
/// lock keeps the microphone, the host's voice and the connection alive with the screen locked.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone | ForegroundService.TypeMediaPlayback)]
public sealed class FightService : Service
{
    public const string StatusExtra = "status";
    private const string Channel = "fight";
    private PowerManager.WakeLock? _wake;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Fight in progress", NotificationImportance.Low));
        PendingIntent open = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!)!, PendingIntentFlags.Immutable)!;
        var builder = new NotificationCompat.Builder(this, Channel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("PoFightJudge");
        builder.SetContentText(intent?.GetStringExtra(StatusExtra) ?? "Fight in progress");
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetContentIntent(open);
        Notification note = builder.Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            StartForeground(77, note, ForegroundService.TypeMicrophone | ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(77, note);
        }

        if (_wake is null)
        {
            _wake = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "pofightjudge:fight");
            _wake!.Acquire();
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_wake?.IsHeld == true)
        {
            _wake.Release();
        }

        base.OnDestroy();
    }
}
