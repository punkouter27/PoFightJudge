using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Integration.Support;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Integration;

[Collection(AzuriteCollection.Name)]
public class ProfileRepositoryTests(AzuriteFixture azurite)
{
    private ProfileRepository Sut()
    {
        Skip.IfNot(azurite.IsAvailable, azurite.Unavailable);
        return new ProfileRepository(azurite.Tables);
    }

    private static Profile Persona(string initials, ProfileRole role, string likes = "coffee")
    {
        var profile = new CreateProfileRequest
        {
            Initials = initials,
            Role = role,
            Name = "Sam",
            Age = 38,
            Likes = likes,
            Dislikes = "mornings",
            Patience = 12,
            CommonArguments = "budget",
            Philosophy = "stoic",
            TtsSettings = new TtsSettingsDto { Pitch = 0.8, Speed = 1.3, VoiceName = role == ProfileRole.Wife ? "Zephyr" : "Puck" },
        }.ToDomain();
        profile.UpdateFacePic($"faces/{initials}.png");
        return profile;
    }

    [SkippableFact]
    public async Task Upsert_get_list_filter_replace_and_delete_round_trip()
    {
        var sut = Sut();
        var husband = Persona("RH1", ProfileRole.Husband);
        var wife = Persona("RW1", ProfileRole.Wife);

        await sut.UpsertAsync(husband);
        await sut.UpsertAsync(wife);

        var loaded = await sut.GetByIdAsync(ProfileId.From("RH1"));
        loaded.Should().NotBeNull();
        loaded!.ToDto().Should().BeEquivalentTo(husband.ToDto(), "every field survives the table round trip");
        loaded.FacePic.Should().Be("faces/RH1.png");

        var all = await sut.GetAllAsync();
        all.Select(p => p.Initials).Should().Contain(["RH1", "RW1"]);

        await sut.UpsertAsync(Persona("RH1", ProfileRole.Husband, likes: "tea"));
        (await sut.GetByIdAsync(ProfileId.From("RH1")))!.Likes.Should().Be("tea", "upsert replaces the row");

        await sut.DeleteAsync(ProfileId.From("RH1"));
        (await sut.GetByIdAsync(ProfileId.From("RH1"))).Should().BeNull();
        (await sut.GetByIdAsync(ProfileId.From("ZZZ"))).Should().BeNull("a missing row is null, not an exception");
    }

    [SkippableFact]
    public async Task Delete_of_a_missing_profile_is_a_no_op()
    {
        var sut = Sut();

        var act = () => sut.DeleteAsync(ProfileId.From("NON"));

        await act.Should().NotThrowAsync();
    }
}
