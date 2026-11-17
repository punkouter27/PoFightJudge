using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fight;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EAPI;

/// <summary>
/// Starting, watching and stopping a fight over HTTP. The live half runs on the scripted host, so the whole thing
/// works with no key.
/// </summary>
[Collection(ApiCollection.Name)]
public class FightTests(ApiFactory factory)
{
    private HttpClient User(string id)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, id);
        return client;
    }

    private static async Task<MatchId> StartAsync(HttpClient client, string one, string two, string? topic = "the thermostat")
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, one, two, topic));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var started = await response.Content.ReadFromJsonAsync<CreateFightResponse>();
        started.Should().NotBeNull();
        return started!.MatchId;
    }

    [Fact]
    public async Task Starting_a_fight_creates_both_fighters_and_answers_with_somewhere_to_join()
    {
        var client = User("fight-start");

        var response = await client.PostAsJsonAsync(
            ApiRoutes.Fights.Base,
            new CreateFightRequest(HostPersonaId.Referee, "aax", "bbx", null, ProfileRole.Wife, ProfileRole.Husband));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<CreateFightResponse>())!.MatchId;

        // A tag is exactly three letters: two, or one with a digit, is not somebody yet.
        (await client.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, "aa", "bbx")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "two letters is not a tag");
        (await client.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, "aa1", "bbx")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "letters only");

        // The same Shared rules the setup page runs, answered field by field so the page can put them under the box.
        var sameTwice = await client.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, "aax", "AAX"));
        sameTwice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sameTwice.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())!.Errors.Keys.Should().Contain(nameof(CreateFightRequest.Player2Tag));

        // Read through the store rather than over HTTP: the roster endpoint is a later task, but the rows are the point.
        var fighters = factory.Services.GetRequiredService<IFighterRepository>();
        var one = await fighters.GetAsync(FighterId.From("AAX"));
        one.Should().NotBeNull("both fighters exist from the moment the fight starts");
        one!.Role.Should().Be(ProfileRole.Wife, "the seat chosen at setup is what their persona will argue from");
        (await fighters.GetAsync(FighterId.From("BBX")))!.Role.Should().Be(ProfileRole.Husband);

        var snapshot = await client.GetFromJsonAsync<DebateSnapshotDto>(ApiRoutes.Fights.ById(id));
        snapshot.Should().NotBeNull();
        snapshot!.MatchId.Should().Be(id);
        snapshot.Phase.Should().Be(SessionPhase.Intro);
        snapshot.Player1Name.Should().Be("AAX");
        snapshot.Persona.Should().Be(HostPersonaId.Referee);

        await client.PostAsync(ApiRoutes.Fights.End(id), null);
    }

    [Fact]
    public async Task Somebody_elses_fight_is_not_found_rather_than_forbidden()
    {
        var mine = User("fight-owner");
        var theirs = User("fight-stranger");
        var id = await StartAsync(mine, "ccx", "ddx");

        (await theirs.GetAsync(ApiRoutes.Fights.ById(id))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.PostAsync(ApiRoutes.Fights.End(id), null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await mine.PostAsync(ApiRoutes.Fights.End(id), null);
    }

    [Fact]
    public async Task Ending_a_fight_stores_it_and_the_same_route_then_answers_with_the_record()
    {
        var client = User("fight-end");
        var id = await StartAsync(client, "eex", "ffx", "who does the dishes");

        var ended = await client.PostAsync(ApiRoutes.Fights.End(id), null);
        ended.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stored = await client.GetFromJsonAsync<MatchDto>(ApiRoutes.Fights.ById(id));
        stored.Should().NotBeNull();
        stored!.Mode.Should().Be(MatchMode.Fight);
        stored.Side1.Id.Should().Be("EEX");
        stored.Side2.Id.Should().Be("FFX");
        stored.Side1.IsHuman.Should().BeTrue("a fight is two people on one microphone");
        stored.IsFake.Should().BeTrue("this one was argued in front of the scripted host");
        stored.EndedAt.Should().NotBeNull();
        stored.Persona.Should().Be(nameof(HostPersonaId.Referee));

        // Ending it twice is what two tabs pressing stop looks like, and the second is simply no longer there.
        (await client.PostAsync(ApiRoutes.Fights.End(id), null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_fight_cannot_be_started_or_read_without_signing_in()
    {
        var anonymous = factory.CreateClient();

        (await anonymous.PostAsJsonAsync(ApiRoutes.Fights.Base, new CreateFightRequest(HostPersonaId.Referee, "AB", "CD")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(ApiRoutes.Fights.ById(MatchId.New()))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
