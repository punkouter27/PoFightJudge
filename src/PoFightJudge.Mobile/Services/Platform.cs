namespace PoFightJudge.Mobile.Services;

/// <summary>The microphone as 16 kHz mono 16-bit PCM in 100 ms frames, with the phone's echo cancellation when asked.</summary>
public interface IMicrophone
{
    Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing);

    void Stop();
}

/// <summary>Plays the host's live voice (a stream of 16-bit PCM) and Watch lines (a whole clip, PCM or MP3).</summary>
public interface ISpeaker
{
    void StartStream(int sampleRate);

    void Enqueue(byte[] pcm16);

    /// <summary>Drops whatever is queued — the host was interrupted.</summary>
    void Clear();

    void StopStream();

    Task PlayClipAsync(byte[] audio, string format, int sampleRate, CancellationToken ct);
}

/// <summary>The phone's own speech recognition, on-device where the OS offers it (item 3).</summary>
public interface ISpeechToText
{
    bool IsAvailable { get; }

    /// <summary>True when recognition will not leave the phone.</summary>
    bool OnDevice { get; }

    Task<bool> StartAsync(Action<string> onText);

    /// <summary>Stops listening and returns everything heard.</summary>
    Task<string> StopAsync();
}

/// <summary>Keeps a fight running with the screen locked (item 1): Android foreground service; iOS background audio.</summary>
public interface IFightKeepAlive
{
    void Start(string status);

    void Update(string status);

    void Stop();
}

/// <summary>Taps and buzzes for the moments of a fight (item 6). On by default, one switch in Settings.</summary>
public sealed class Haptics
{
    private const string Key = "pofightjudge_haptics";

    public static bool Enabled
    {
        get => Preferences.Default.Get(Key, true);
        set => Preferences.Default.Set(Key, value);
    }

    /// <summary>A turn changes, a button.</summary>
    public static void Tap() => Feedback(HapticFeedbackType.Click);

    /// <summary>The host cuts in.</summary>
    public static void Interrupt() => Buzz(80);

    /// <summary>A heated moment, a slap.</summary>
    public static void Heat() => Buzz(180);

    /// <summary>The ruling lands: three beats.</summary>
    public static async Task VerdictAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            Buzz(60);
            await Task.Delay(140);
        }

        Feedback(HapticFeedbackType.LongPress);
    }

    private static void Feedback(HapticFeedbackType type)
    {
        try
        {
            if (Enabled && HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(type);
            }
        }
        catch (FeatureNotSupportedException)
        {
        }
    }

    private static void Buzz(int ms)
    {
        try
        {
            if (Enabled && Vibration.Default.IsSupported)
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(ms));
            }
        }
        catch (FeatureNotSupportedException)
        {
        }
    }
}

/// <summary>Builds a 16 kHz mono 16-bit WAV around raw PCM (what the server's recorded-fight upload and /transcribe read).</summary>
public static class Wav
{
    public const int Rate = 16_000;

    public static byte[] Header(int pcmBytes, int sampleRate = Rate)
    {
        byte[] h = new byte[44];
        void Put(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(h, at);
        void I32(int at, int v) => BitConverter.TryWriteBytes(h.AsSpan(at, 4), v);
        void I16(int at, short v) => BitConverter.TryWriteBytes(h.AsSpan(at, 2), v);
        Put(0, "RIFF");
        I32(4, 36 + pcmBytes);
        Put(8, "WAVE");
        Put(12, "fmt ");
        I32(16, 16);
        I16(20, 1);
        I16(22, 1);
        I32(24, sampleRate);
        I32(28, sampleRate * 2);
        I16(32, 2);
        I16(34, 16);
        Put(36, "data");
        I32(40, pcmBytes);
        return h;
    }

    public static byte[] Build(byte[] pcm, int sampleRate = Rate) => [.. Header(pcm.Length, sampleRate), .. pcm];
}
