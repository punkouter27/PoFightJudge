using PoFightJudge.Client.Services;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Client.Features.Watch;

/// <summary>
/// Speculative fetch of the next line. A round trip to the model costs a second or two, and asking for it while the
/// current line is still being spoken is what keeps the argument sounding like an argument rather than a queue.
/// </summary>
/// <remarks>
/// A prefetch is only usable when nothing has changed the request it was built from: the same speaker, the same
/// history length, and no interjection thrown in the meantime. A slap invalidates it, which is the whole reason the
/// match is checked rather than assumed.
/// </remarks>
public sealed partial class WatchPlay
{
    private Task<GenerateRoundResponse>? _prefetched;
    private string? _prefetchedSpeaker;
    private int _prefetchedHistory;

    /// <summary>The line currently being written, as far as it has got. Rendered under the rounds, never as one.</summary>
    private readonly System.Text.StringBuilder _arriving = new();

    /// <summary>Whose line that is, so it renders in their colour and on their side.</summary>
    private string? _arrivingSpeaker;

    /// <summary>
    /// The voice for the speculative line, fetched behind it. Synthesis is the longer of the two waits, so a line
    /// that arrived early and then stood silent had only moved the pause rather than closed it.
    /// </summary>
    private Task<PrefetchedVoice?>? _prefetchedAudio;

    /// <summary>
    /// The same, once its line has been taken off the prefetch and is about to be spoken. It moves here because the
    /// very next thing the loop does is start prefetching the line after it, and that drops the speculative slot —
    /// which used to take the voice for the line now being spoken with it, so every prefetched line was synthesized
    /// twice.
    /// </summary>
    private Task<PrefetchedVoice?>? _takenAudio;

    /// <summary>
    /// A line's clauses with the line they were made from. Carried together rather than in fields beside the task:
    /// the words are only known once the line resolves, and two continuations off the same task have no order.
    /// </summary>
    private sealed record PrefetchedVoice(string SpeakerId, string Text, IReadOnlyList<TtsAudioDto> Clauses);

    /// <summary>
    /// The next line: the speculative fetch when it still fits, and otherwise a stream, because a line nobody has
    /// is a line somebody is watching an empty stage for.
    /// </summary>
    private async Task<GenerateRoundResponse> NextLineAsync(string speaker, string? interjection, CancellationToken ct)
    {
        if (interjection is null
            && _prefetched is { } waiting
            && string.Equals(_prefetchedSpeaker, speaker, StringComparison.Ordinal)
            && _prefetchedHistory == _rounds.Count)
        {
            _prefetched = null;
            _prefetchedSpeaker = null;
            _takenAudio = _prefetchedAudio;
            _prefetchedAudio = null;
            return await waiting;
        }

        DropPrefetch();
        return await StreamLineAsync(speaker, interjection, ct);
    }

    /// <summary>
    /// Reads the line as it is written, publishing each fragment so the stage fills in rather than sitting empty.
    /// The closing part is what the round is made from: the fragments are the decoded prefix of a JSON value still
    /// being written, and only the final object has been parsed.
    /// </summary>
    private async Task<GenerateRoundResponse> StreamLineAsync(string speaker, string? interjection, CancellationToken ct)
    {
        _arrivingSpeaker = speaker;
        _arriving.Clear();

        // Before the first fragment, not after it. Whose turn it is is known the moment the request goes out, and
        // the stage should say so while the line is still being written rather than staying on the last speaker
        // until the first words land.
        await InvokeAsync(StateHasChanged);

        GenerateRoundResponse? final = null;

        await foreach (var part in Api.StreamRoundAsync(Request(speaker, interjection), ct))
        {
            if (part.Delta is { Length: > 0 } fragment)
            {
                _arriving.Append(fragment);

                // Through the dispatcher, not straight at it. This runs on whatever thread the stream's
                // continuation landed on, and StateHasChanged off the renderer's dispatcher throws — which took
                // the whole round loop down with it rather than dropping one repaint.
                await InvokeAsync(StateHasChanged);
                continue;
            }

            if (part.Final is { } whole)
            {
                final = whole;
            }
        }

        if (final is { } complete)
        {
            return complete;
        }

        // The stream was cut off before the closing object. What did arrive is still the line — losing a sentence
        // the viewer has already read, to a missing wrapper, is worse than a line whose mood had to be guessed.
        // Nothing at all is a different thing entirely, and goes to the error path rather than adding a blank round.
        var partial = _arriving.ToString().Trim();
        return partial.Length > 0
            ? new GenerateRoundResponse(partial, string.Empty, string.Empty, _isFake)
            : throw new ApiException(0, "The line stopped arriving before anybody said anything.");
    }

    /// <summary>The line stops arriving the moment it is a round, or the moment it fails.</summary>
    private void ClearArriving()
    {
        _arriving.Clear();
        _arrivingSpeaker = null;
    }

    /// <summary>Starts fetching the line after this one, when the next turn is one the client can predict.</summary>
    private void StartPrefetch(CancellationToken ct)
    {
        DropPrefetch();

        if (_rounds.Count >= WatchTurns.MaxLines || WatchTurns.IsComplete(_rounds.Select(r => r.Speaker)))
        {
            return;
        }

        var speaker = WatchTurns.NextSpeaker(_rounds.Count, _slapUsed);
        if (SideOf(speaker).IsHuman)
        {
            // A person's turn cannot be guessed at, and the line after it depends on what they say.
            return;
        }

        _prefetchedSpeaker = speaker;
        _prefetchedHistory = _rounds.Count;
        var line = Api.GenerateRoundAsync(Request(speaker, null), ct);
        _prefetched = line;
        _prefetchedAudio = SpeakAheadAsync(line, SideOf(speaker), ct);
    }

    /// <summary>
    /// Synthesizes the prefetched line as soon as it exists, so the voice is waiting rather than the listener. A
    /// discarded one is not wasted work: the TTS cache is keyed on the words, so the line it was thrown away for
    /// pays nothing to say the same thing again.
    /// </summary>
    private async Task<PrefetchedVoice?> SpeakAheadAsync(Task<GenerateRoundResponse> line, MatchSide side, CancellationToken ct)
    {
        try
        {
#pragma warning disable VSTHRD003 // StartPrefetch a few lines above is what started it; this is the same context.
            var response = await line;
#pragma warning restore VSTHRD003
            return new PrefetchedVoice(side.Id, response.Text, await ClausesAsync(side, response.Text, ct));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or OperationCanceledException)
        {
            // The line itself failing is the loop's problem, not this one's; a voice that could not be fetched
            // ahead is simply fetched again when the line is actually spoken.
            return null;
        }
    }

    /// <summary>Reads a line's clauses off the audio stream into a list. Used when nothing is waiting to play them yet.</summary>
    private async Task<IReadOnlyList<TtsAudioDto>> ClausesAsync(MatchSide side, string text, CancellationToken ct)
    {
        var clauses = new List<TtsAudioDto>();
        await foreach (var chunk in Api.StreamRoundAudioAsync(new RoundAudioRequest(ProfileId.From(side.Id), text), ct))
        {
            if (!chunk.Audio.IsEmpty)
            {
                clauses.Add(chunk.Audio);
            }
        }

        return clauses;
    }

    /// <summary>
    /// The voice fetched ahead for this exact line, or null when there is none to use. Matching on the words as
    /// well as the speaker is what stops a discarded prefetch — a slap landed, a person spoke — being played over
    /// the line that actually replaced it.
    /// </summary>
    private bool TryTakePrefetchedAudio(out Task<PrefetchedVoice?> voice)
    {
        if (_takenAudio is { } waiting)
        {
            voice = waiting;
            _takenAudio = null;
            return true;
        }

        voice = Task.FromResult<PrefetchedVoice?>(null);
        return false;
    }

    /// <summary>Abandons a speculative fetch, observing its failure so a discarded request cannot surface elsewhere.</summary>
    private void DropPrefetch()
    {
        var abandoned = _prefetched;
        var unspoken = _prefetchedAudio;
        _prefetched = null;
        _prefetchedSpeaker = null;
        _prefetchedHistory = 0;
        _prefetchedAudio = null;

        Observe(abandoned);
        Observe(unspoken);

        static void Observe(Task? dropped) =>
            _ = dropped?.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Builds the request for one line. The history is copied rather than passed by reference: the list keeps
    /// growing while a speculative fetch is in flight, and a request must carry the argument as it stood.
    /// </summary>
    private GenerateRoundRequest Request(string speaker, string? interjection) => new(
        Simulation.Husband!,
        Simulation.Wife!,
        [.. _rounds],
        speaker,
        Simulation.Topic.Length == 0 ? null : Simulation.Topic,
        interjection,
        string.Equals(interjection, Interjections.SlapKey, StringComparison.Ordinal),
        Simulation.HusbandStance);
}
