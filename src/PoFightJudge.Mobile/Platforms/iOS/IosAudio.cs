using AVFoundation;
using Foundation;
using PoFightJudge.Mobile.Services;
using Speech;
using UIKit;

namespace PoFightJudge.Mobile;

internal static class AudioSessionSetup
{
    /// <summary>Play-and-record with voice chat processing: echo cancellation, and audio that carries on when the screen locks.</summary>
    public static void Activate()
    {
        AVAudioSession session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker | AVAudioSessionCategoryOptions.AllowBluetooth, out _);
        session.SetMode(AVAudioSession.ModeVoiceChat, out _);
        session.SetActive(true, out _);
    }
}

public sealed class IosMicrophone : IMicrophone
{
    private AVAudioEngine? _engine;

    public Task<bool> StartAsync(Action<byte[]> onPcm16k, bool voiceProcessing)
    {
        AudioSessionSetup.Activate();
        _engine = new AVAudioEngine();
        AVAudioInputNode input = _engine.InputNode;
        if (voiceProcessing)
        {
            input.SetVoiceProcessingEnabled(true, out _);
        }

        AVAudioFormat source = input.GetBusOutputFormat(0);
        AVAudioFormat target = new(AVAudioCommonFormat.PCMInt16, Wav.Rate, 1, interleaved: true);
        AVAudioConverter converter = new(source, target);
        input.InstallTapOnBus(0, (uint)(source.SampleRate / 10), source, (buffer, _) =>
        {
            uint capacity = (uint)(buffer.FrameLength * Wav.Rate / source.SampleRate) + 32;
            using AVAudioPcmBuffer output = new(target, capacity);
            bool given = false;
            converter.ConvertToBuffer(output, out NSError? error, (uint _, out AVAudioConverterInputStatus status) =>
            {
                status = given ? AVAudioConverterInputStatus.NoDataNow : AVAudioConverterInputStatus.HaveData;
                given = true;
                return buffer;
            });
            if (error is null && output.FrameLength > 0)
            {
                byte[] pcm = new byte[output.FrameLength * 2];
                unsafe
                {
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)((short**)output.Int16ChannelData)[0], pcm, 0, pcm.Length);
                }

                onPcm16k(pcm);
            }
        });
        _engine.Prepare();
        return Task.FromResult(_engine.StartAndReturnError(out NSError? startError) && startError is null);
    }

    public void Stop()
    {
        _engine?.InputNode.RemoveTapOnBus(0);
        _engine?.Stop();
        _engine = null;
    }
}

public sealed class IosSpeaker : ISpeaker
{
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _format;
    private int _rate;

    public void StartStream(int sampleRate)
    {
        StopStream();
        AudioSessionSetup.Activate();
        _rate = sampleRate;
        _engine = new AVAudioEngine();
        _player = new AVAudioPlayerNode();
        _format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, sampleRate, 1, interleaved: false);
        _engine.AttachNode(_player);
        _engine.Connect(_player, _engine.MainMixerNode, _format);
        _engine.Prepare();
        _engine.StartAndReturnError(out _);
        _player.Play();
    }

    public void Enqueue(byte[] pcm16)
    {
        if (_player is null || _format is null || pcm16.Length < 2)
        {
            return;
        }

        int frames = pcm16.Length / 2;
        AVAudioPcmBuffer buffer = new(_format, (uint)frames) { FrameLength = (uint)frames };
        unsafe
        {
            float* samples = ((float**)buffer.FloatChannelData)[0];
            for (int i = 0; i < frames; i++)
            {
                samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
            }
        }

        _player.ScheduleBuffer(buffer, () => buffer.Dispose());
    }

    public void Clear()
    {
        _player?.Stop();
        _player?.Play();
    }

    public void StopStream()
    {
        _player?.Stop();
        _engine?.Stop();
        _player = null;
        _engine = null;
    }

    public async Task PlayClipAsync(byte[] audio, string format, int sampleRate, CancellationToken ct)
    {
        if (audio.Length == 0)
        {
            return;
        }

        if (string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            AudioSessionSetup.Activate();
            using AVAudioPlayer? player = AVAudioPlayer.FromData(NSData.FromArray(audio));
            if (player is null)
            {
                return;
            }

            player.Play();
            await Task.Delay(TimeSpan.FromSeconds(player.Duration) + TimeSpan.FromMilliseconds(150), ct);
            return;
        }

        StartStream(sampleRate);
        Enqueue(audio);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(audio.Length / 2.0 / sampleRate) + TimeSpan.FromMilliseconds(150), ct);
        }
        finally
        {
            StopStream();
        }
    }
}

/// <summary>SFSpeechRecognizer, required to stay on the phone when the device supports on-device recognition.</summary>
public sealed class IosSpeechToText : ISpeechToText
{
    private readonly SFSpeechRecognizer? _recognizer = new();
    private AVAudioEngine? _engine;
    private SFSpeechAudioBufferRecognitionRequest? _request;
    private SFSpeechRecognitionTask? _task;
    private string _text = string.Empty;

    public bool IsAvailable => _recognizer?.Available == true;

    public bool OnDevice => _recognizer?.SupportsOnDeviceRecognition == true;

    public async Task<bool> StartAsync(Action<string> onText)
    {
        TaskCompletionSource<SFSpeechRecognizerAuthorizationStatus> asked = new();
        SFSpeechRecognizer.RequestAuthorization(s => asked.TrySetResult(s));
        if (await asked.Task != SFSpeechRecognizerAuthorizationStatus.Authorized || !IsAvailable)
        {
            return false;
        }

        AudioSessionSetup.Activate();
        _text = string.Empty;
        _request = new SFSpeechAudioBufferRecognitionRequest { ShouldReportPartialResults = true, RequiresOnDeviceRecognition = OnDevice };
        _engine = new AVAudioEngine();
        AVAudioInputNode input = _engine.InputNode;
        input.InstallTapOnBus(0, 1024, input.GetBusOutputFormat(0), (buffer, _) => _request?.Append(buffer));
        _engine.Prepare();
        _engine.StartAndReturnError(out _);
        _task = _recognizer!.GetRecognitionTask(_request, (result, _) =>
        {
            if (result?.BestTranscription.FormattedString is { } text)
            {
                _text = text;
                onText(text);
            }
        });
        return true;
    }

    public Task<string> StopAsync()
    {
        _engine?.InputNode.RemoveTapOnBus(0);
        _engine?.Stop();
        _request?.EndAudio();
        _task?.Finish();
        _engine = null;
        return Task.FromResult(_text.Trim());
    }
}

public sealed class IosKeepAlive : IFightKeepAlive
{
    public void Start(string status) => MainThread.BeginInvokeOnMainThread(() =>
    {
        AudioSessionSetup.Activate();
        UIApplication.SharedApplication.IdleTimerDisabled = true;
    });

    public void Update(string status)
    {
    }

    public void Stop() => MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.IdleTimerDisabled = false);
}
