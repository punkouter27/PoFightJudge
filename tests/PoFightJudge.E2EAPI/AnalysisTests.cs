using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Storage;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EAPI;

/// <summary>
/// Reading a fight back over HTTP. The offline stand-ins do the reading, so a whole fight goes from ended to
/// readable with no key.
/// </summary>
[Collection(ApiCollection.Name)]
public class AnalysisTests(ApiFactory factory)
{
    private HttpClient User(string id)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, id);
        return client;
    }

    /// <summary>Starts a fight, lets it end, and waits for it to have been read.</summary>
    private static async Task<MatchId> FoughtAsync(HttpClient client, string one, string two)
    {
        var started = await client.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, one, two, "the thermostat"));
        started.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await started.Content.ReadFromJsonAsync<CreateFightResponse>())!.MatchId;
        await client.PostAsync(ApiRoutes.Fights.End(id), null);
        return id;
    }

    private static async Task<AnalysisResponse> WaitForAsync(HttpClient client, MatchId id, AnalysisStatus wanted)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var response = await client.GetAsync(ApiRoutes.Fights.Analysis(id));
            var body = await response.Content.ReadFromJsonAsync<AnalysisResponse>();
            if (body?.Status == wanted)
            {
                return body;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException($"The analysis never reached {wanted}.");
    }

    [Fact]
    public async Task Somebody_elses_fight_cannot_be_read()
    {
        var mine = User("analysis-owner");
        var theirs = User("analysis-stranger");
        var id = await FoughtAsync(mine, "oxa", "oxb");

        (await theirs.GetAsync(ApiRoutes.Fights.Analysis(id))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.PostAsync(ApiRoutes.Fights.AnalysisRetry(id), null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.GetAsync(ApiRoutes.Fights.Clip(id, 0))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reading_a_fight_needs_signing_in()
    {
        var anonymous = factory.CreateClient();
        var id = MatchId.New();

        (await anonymous.GetAsync(ApiRoutes.Fights.Analysis(id))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(ApiRoutes.Fights.AnalysisRetry(id), null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_fight_somebody_actually_argued_is_read_and_both_of_them_get_a_record()
    {
        var client = User("analysis-full");
        var id = await FoughtAsync(client, "fxa", "fxb");

        // The room is what feeds a fight; there is no room here, so the recording is put in by hand and re-read.
        await GiveItARecordingAsync(id);
        var retry = await client.PostAsync(ApiRoutes.Fights.AnalysisRetry(id), null);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var ready = await WaitForAsync(client, id, AnalysisStatus.Ready);
        ready.Report.Should().NotBeNull();
        ready.Report!.MatchId.Should().Be(id);
        ready.Report.Player1.Metrics.Words.Should().BeGreaterThan(0, "the stand-in transcriber says something");
        ready.Report.Overall.Reasons.Should().NotBeEmpty();

        // It has its answer. Asking again would pay the judge twice for the same recording.
        (await client.PostAsync(ApiRoutes.Fights.AnalysisRetry(id), null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Waited for, not read straight after Ready: the pipeline publishes the ruling before it writes the result
        // rows on purpose, so the room is not kept waiting on a record. Reading them the instant the status flips
        // races that write, and loses it under load.
        (await WaitForResultAsync("FXA", "analysis-full")).Should().ContainSingle();
        (await WaitForResultAsync("FXB", "analysis-full")).Should().ContainSingle();

        // A fight recorded on a phone with no host, uploaded afterwards, is read the same way into the same records,
        // and an upload retried after a timeout answers with the fight it already made rather than a second one.
        // Its turns are half-second taps that cover a sixth of the speech: with no host on the mic, speech outside
        // the turns is still the fighters', not host bleed to be thrown away.
        var recorded = new RecordedFightRequest(
            MatchId.New(), "fxa", "fxb", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            [.. Enumerable.Range(0, 7).Select(i => new RecordedTurnDto(i % 2 == 0 ? Speaker.Player1 : Speaker.Player2, i * 3, (i * 3) + 0.5))], "Ann", "Bob", "the thermostat");
        using (var upload = RecordedForm(recorded))
        {
            (await client.PostAsync(ApiRoutes.Fights.RecordedUrl, upload)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var retried = RecordedForm(recorded))
        {
            (await client.PostAsync(ApiRoutes.Fights.RecordedUrl, retried)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var recordedReport = (await WaitForAsync(client, recorded.MatchId, AnalysisStatus.Ready)).Report!;
        recordedReport.Player1.Metrics.Words.Should().BeGreaterThan(0, "a fight with no host keeps the speech its turns do not cover");
        recordedReport.Player2.Metrics.Words.Should().BeGreaterThan(0, "the phone's turn timeline puts each voice on its own fighter");
        (await WaitForResultAsync("FXA", "analysis-full", atLeast: 2)).Should().HaveCount(2);

        // And both of them are now somebody the CPU and 1P channels can put in a seat, read from this fight. The
        // persona is written after the ruling is readable, deliberately — the room is not kept waiting on it — so
        // it is allowed a moment to land.
        var f1 = await WaitForPersonaAsync(client, "FXA");
        f1.FromFights.Should().BeTrue();
        f1.Persona.Role.Should().Be(ProfileRole.Husband, "fighter one sits as the husband unless setup said otherwise");
        (await WaitForPersonaAsync(client, "FXB")).Persona.Role.Should().Be(ProfileRole.Wife);
    }

    /// <summary>One fighter's rows, once the pipeline has written them. Same ten seconds the persona is given.</summary>
    private async Task<IReadOnlyList<FighterResultDto>> WaitForResultAsync(string tag, string userId, int atLeast = 1)
    {
        var results = factory.Services.GetRequiredService<IFighterResultRepository>();
        const int Attempts = 100;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var rows = await results.ListForAsync(FighterId.From(tag), userId);
            if (rows.Count >= atLeast)
            {
                return rows;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No result row for {tag} was written within 10s.");
    }

    private static async Task<ProfileDto> WaitForPersonaAsync(HttpClient client, string initials)
    {
        const int Attempts = 100; // a hundred looks, 100 ms apart: ten seconds for a stand-in model that answers at once
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var cast = await client.GetFromJsonAsync<List<ProfileDto>>(ApiRoutes.Profiles.Base) ?? [];
            if (cast.Find(p => string.Equals(p.Persona.Initials, initials, StringComparison.Ordinal)) is { } persona)
            {
                return persona;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No persona for {initials} appeared in the cast within 10s.");
    }

    /// <summary>Puts a recording where the pipeline expects one, for a fight nobody spoke into.</summary>
    /// <summary>A phone's upload: the meta JSON and twenty seconds of 16 kHz mono WAV.</summary>
    private static MultipartFormDataContent RecordedForm(RecordedFightRequest meta)
    {
        var form = new MultipartFormDataContent { { new StringContent(System.Text.Json.JsonSerializer.Serialize(meta, System.Text.Json.JsonSerializerOptions.Web)), "meta" } };
        var audio = new ByteArrayContent(PoFightJudge.Api.Features.Fight.WavWriter.Build(16_000, new byte[640_000]));
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "fight.wav");
        return form;
    }

    private async Task GiveItARecordingAsync(MatchId id)
    {
        var matches = factory.Services.GetRequiredService<IMatchRepository>();
        var blobs = factory.Services.GetRequiredService<IAudioBlobStore>();
        var name = IAudioBlobStore.PlayersTrack(id);

        using var wav = new MemoryStream(PoFightJudge.Api.Features.Fight.WavWriter.Build(16_000, new byte[32_000]));
        await blobs.UploadAsync(name, wav, "audio/wav");

        var match = await matches.GetAsync("analysis-full", id) ?? throw new InvalidOperationException("The fight vanished.");
        await matches.UpsertAsync(match with { AudioBlobName = name });
    }
}
