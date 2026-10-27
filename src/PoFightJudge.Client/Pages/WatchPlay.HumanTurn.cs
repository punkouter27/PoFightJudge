using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Pages;

/// <summary>
/// The person's spoken turn. The microphone opens by itself when the turn arrives and closes when they stop
/// talking; what it heard is transcribed into the box for them to read before it counts, because a transcriber
/// that mishears would otherwise put words on their record.
/// </summary>
/// <remarks>
/// There is still a button to stop early, and still a box to type in. The browser's own recogniser stays on offer
/// where it exists: it is instant and free, and some people would rather watch words appear than talk to a meter.
/// </remarks>
public sealed partial class WatchPlay
{
    /// <summary>How often the level is read. Ten times a second is smooth to look at and cheap to poll.</summary>
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Quiet enough to count as not talking. Set above room noise, below anything said out loud.</summary>
    private const double SilenceLevel = 0.06;

    /// <summary>
    /// How long a pause has to run before the turn is taken as finished. Long enough to think mid-sentence, short
    /// enough that nobody wonders whether the thing is still listening.
    /// </summary>
    internal static readonly TimeSpan SilenceToEnd = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a pause has run long enough to finish the turn. Nobody is cut off before they have started, so a
    /// silence only counts once something has actually been said into it.
    /// </summary>
    public static bool PauseEndsTurn(bool heardSomething, TimeSpan quietFor) =>
        heardSomething && quietFor >= SilenceToEnd;

    /// <summary>
    /// Whether a transcript is worth sending on. Blank is what a clip with no words in it comes back as, and
    /// sending that would put an empty turn on somebody's record.
    /// </summary>
    public static bool WorthSending(string? transcript) => !string.IsNullOrWhiteSpace(transcript);

    /// <summary>Nobody is cut off before they have started: silence only ends a turn once something was said.</summary>
    private bool _heardSomething;

    private bool _recording;
    private bool _transcribing;
    private bool _heardNothing;
    private string? _micProblem;
    private CancellationTokenSource? _listening;

    /// <summary>Recording is offered only when the server has a transcriber to finish the turn.</summary>
    private bool CanRecord => Features.Flags.HumanInWatch;

    /// <summary>Opens the microphone for a turn that has just begun. A failure here leaves the box to type in.</summary>
    private async Task BeginListeningAsync()
    {
        if (!CanRecord || _recording || _transcribing)
        {
            return;
        }

        await StartRecordingAsync();
        if (_recording)
        {
            StartLevelWatch();
        }

        StateHasChanged();
    }

    /// <summary>
    /// Watches the level while they speak: it drives the meter, and a long enough pause after they have actually
    /// said something ends the turn without them having to reach for anything.
    /// </summary>
    private void StartLevelWatch()
    {
        _listening?.Dispose();
        var watching = CancellationTokenSource.CreateLinkedTokenSource(_leaving.Token);
        _listening = watching;
        _heardSomething = false;
        var quietSince = Clock.GetUtcNow();

        _ = InvokeAsync(async () =>
        {
            try
            {
                while (!watching.Token.IsCancellationRequested && _recording)
                {
                    await Task.Delay(LevelInterval, Clock, watching.Token);

                    // Only the decision is made here; the meter is painted by the browser from the level the
                    // recorder publishes, so this loop never re-renders anything.
                    var level = await Mic.LevelAsync(watching.Token);
                    var now = Clock.GetUtcNow();
                    if (level >= SilenceLevel)
                    {
                        _heardSomething = true;
                        quietSince = now;
                    }
                    else if (PauseEndsTurn(_heardSomething, now - quietSince))
                    {
                        await StopRecordingAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The turn ended, or the page is going away.
            }
        });
    }

    private void StopLevelWatch()
    {
        _listening?.Cancel();
        _listening?.Dispose();
        _listening = null;
    }

    /// <summary>
    /// Lets the microphone go without transcribing what it heard. Somebody who has started typing has chosen how
    /// they are answering: the pause that ends a spoken turn will never come, and a clip they did not speak into
    /// must not be written over the top of what they wrote.
    /// </summary>
    private async Task StopListeningAsync()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        StopLevelWatch();
        await Mic.CancelAsync(_leaving.Token);
    }

    private async Task StartRecordingAsync()
    {
        _micProblem = null;
        _heardNothing = false;

        var error = await Mic.StartAsync(ct: _leaving.Token);
        if (error is not null)
        {
            _micProblem = MicError.Describe(error);
            return;
        }

        _recording = true;
    }

    private async Task StopRecordingAsync()
    {
        if (!_recording)
        {
            return;
        }

        _recording = false;
        StopLevelWatch();
        _transcribing = true;
        StateHasChanged();

        var written = false;
        try
        {
            var clip = await Mic.StopAsync(_leaving.Token);
            if (clip.IsEmpty)
            {
                _micProblem = ClipMessage(clip.Error);
                return;
            }

            // The browser's own Whisper first, when it is switched on and loaded; the server whenever it is not.
            var said = Prefs.OnDeviceAsr ? await Asr.TranscribeAsync(_leaving.Token) : null;
            if (said is null)
            {
                var heard = await Api.TranscribeAsync(new TranscribeRequest(clip.Wav), _leaving.Token);
                _isFake |= heard.IsFake;
                said = heard.Text.Trim();
            }

            if (!WorthSending(said))
            {
                // A legitimate outcome, not an error: the transcriber heard the clip and there were no words in it.
                // Nothing goes anywhere by itself, and the controls come back so the turn can still be finished.
                _heardNothing = true;
                return;
            }

            _line = _line.Trim().Length == 0 ? said : $"{_line.Trim()} {said}";
            written = true;
        }
        catch (ApiException ex)
        {
            _micProblem = ex.Summary;
        }
        catch (HttpRequestException ex)
        {
            _micProblem = $"The clip could not be sent ({ex.Message}).";
        }
        finally
        {
            _transcribing = false;
            StateHasChanged();
        }

        // Written down, so it goes. Reading it back before it counted was the last step standing between speaking
        // and being answered; every failure above returns instead, and those are the only times anything is asked
        // of the person whose turn it is.
        if (written)
        {
            await SayAsync();
        }
    }

    /// <summary>Says it again: throws away what is in the box and listens from the start.</summary>
    private async Task ListenAgainAsync()
    {
        _line = string.Empty;
        _heardNothing = false;
        await BeginListeningAsync();
    }

    /// <summary>What the browser's recogniser heard. It arrives a phrase at a time, so it is appended, not replaced.</summary>
    private void OnDictated(string? heard)
    {
        var said = heard?.Trim() ?? string.Empty;
        if (said.Length == 0)
        {
            return;
        }

        _heardNothing = false;
        _line = _line.Trim().Length == 0 ? said : $"{_line.Trim()} {said}";
    }

    /// <summary>Turns a browser error name into advice a person can act on.</summary>
    /// <summary>
    /// Why there is no clip. A microphone that heard nothing is the ordinary case and reads as advice; anything
    /// else is the browser having failed at a named step, and saying which one is the difference between somebody
    /// trying again and somebody giving up on a feature that looked like it was working.
    /// </summary>
    private static string ClipMessage(string error)
    {
        if (error.StartsWith("DecodeFailed", StringComparison.Ordinal) || error.StartsWith("EncodeFailed", StringComparison.Ordinal))
        {
            return $"The recording could not be read back ({Detail(error)}). Try again, or type it.";
        }

        if (string.Equals(error, "ContextSuspended", StringComparison.Ordinal))
        {
            return "The browser had not woken the microphone up yet. Press Speak it and try again.";
        }

        return error.Length == 0 || string.Equals(error, "NothingCaptured", StringComparison.Ordinal) || string.Equals(error, "NotRecording", StringComparison.Ordinal)
            ? "Nothing came through the microphone. Try that again."
            : $"The recording did not come back ({error}).";
    }

    /// <summary>The browser's own name for it, out of "Step:Name". Its name is the only part worth reading aloud.</summary>
    private static string Detail(string error)
    {
        var colon = error.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon < error.Length - 1 ? error[(colon + 1)..] : error;
    }

    /// <summary>Ctrl+Enter (or ⌘+Enter) says what is in the box, so nobody typing has to reach for the mouse to answer.</summary>
    private Task OnLineKeyAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) =>
        string.Equals(e.Key, "Enter", StringComparison.Ordinal) && (e.CtrlKey || e.MetaKey) && _line.Trim().Length > 0 && !_recording && !_transcribing
            ? SayAsync()
            : Task.CompletedTask;
}
