using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Profiles.Seeding;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.E2EAPI;

[Collection(ApiCollection.Name)]
public class ProfilesTests(ApiFactory factory)
{
    private HttpClient User(string id = "e2e-user", string? roles = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(FakeAuthOptions.UserHeader, id);
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(FakeAuthOptions.RolesHeader, roles);
        }

        return client;
    }

    private static CreateProfileRequest Persona(string initials, ProfileRole role = ProfileRole.Husband) => new()
    {
        Initials = initials,
        Role = role,
        Name = "Sam",
        Likes = "coffee",
        Dislikes = "queues",
        CommonArguments = "chores",
        Philosophy = "keep it short",
        Patience = 30,
        TtsSettings = new TtsSettingsDto { VoiceName = "Puck" },
    };

    /// <summary>
    /// Profiles are the pre-made cast or written from play; nobody writes one by hand. The routes that used to are
    /// gone (404 or 405, depending on whether another verb shares the path), so it answers only to reading — and every
    /// profile it returns carries its record.
    /// </summary>
    [Fact]
    public async Task Profiles_are_read_only_and_each_one_carries_its_record()
    {
        using var client = User();

        // "Load default cast" is a reset: every profile that is not a built-in persona goes, the ones written from
        // play included (they come back after that person's next match), and the built-ins are put back.
        var store = factory.Services.GetRequiredService<IProfileRepository>();
        await store.UpsertAsync(Persona("ZZZ").ToDomain());
        var fromPlay = Persona("HAX").ToDomain();
        fromPlay.MarkFromFights();
        await store.UpsertAsync(fromPlay);

        var seeded = await client.PostAsync(ApiRoutes.Seed.ProfilesUrl, null);
        seeded.StatusCode.Should().Be(HttpStatusCode.OK);
        (await seeded.Content.ReadFromJsonAsync<SeedResultDto>())!.Removed.Should().BeGreaterThanOrEqualTo(2);

        var list = (await client.GetFromJsonAsync<List<ProfileDto>>(ApiRoutes.Profiles.Base))!;
        list.Select(p => p.Persona.Initials).Should().NotContain(["ZZZ", "HAX"], "a hand-made profile and one written from play are both gone");
        list.Select(p => p.Persona.Initials).Should().Contain(SeedProfiles.All.Select(p => p.Initials), "every built-in persona is back");
        list.Should().OnlyContain(p => p.Record != null && p.Record.Rating > 0);

        var kim = ProfileId.From("MLT");
        (await client.PostAsJsonAsync(ApiRoutes.Profiles.Base, Persona("E2A"))).StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "no profile is created by hand");
        (await client.PutAsJsonAsync(ApiRoutes.Profiles.ById(kim), Persona("MLT", ProfileRole.Wife))).StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "or edited");
        (await client.DeleteAsync(ApiRoutes.Profiles.ById(kim))).StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "or deleted");
        (await client.PostAsync(ApiRoutes.Profiles.Face(kim), null)).StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "or given a face");
        (await client.GetAsync(ApiRoutes.Profiles.ById(kim))).StatusCode.Should().Be(HttpStatusCode.OK, "reading one still works");
        (await client.GetAsync(ApiRoutes.Profiles.ById(ProfileId.From("NOP")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_bodies_come_back_as_validation_problems_naming_the_field()
    {
        using var client = User();
        var bad = Persona("SELF");
        bad.Patience = 101;
        bad.TtsSettings.Pitch = 9;

        // The voice preview is the one route left that takes a persona, and it holds it to the same rules.
        var response = await client.PostAsJsonAsync(ApiRoutes.Profiles.PreviewLineUrl, bad);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())!;
        problem.Errors.Keys.Should().Contain(["Initials", "Patience", "TtsSettings.Pitch"]);
        problem.Errors["Initials"].Single().Should().Contain("1–3 letters or digits");
    }

    [Fact]
    public async Task Profiles_require_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync(ApiRoutes.Profiles.Base)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync(ApiRoutes.Profiles.PreviewLineUrl, Persona("ANO"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Calls_that_reach_a_model_are_rationed_per_person_and_say_when_to_come_back()
    {
        // Its own user: the window is a minute long, and nobody else's test should find the bucket empty.
        using var client = User("rationed-" + Guid.NewGuid().ToString("N"));

        HttpResponseMessage? last = null;
        for (var i = 0; i <= PoFightJudge.Api.Features.Watch.WatchServiceExtensions.PermitsPerWindow; i++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync(ApiRoutes.Profiles.PreviewLineUrl, Persona("RAT"));
        }

        using (last)
        {
            last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "a voiced line is a model call like any other");
            last.Headers.RetryAfter.Should().NotBeNull("a throttled client is told when to come back");
        }
    }
}
