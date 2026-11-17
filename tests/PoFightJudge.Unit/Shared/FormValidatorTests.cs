using FluentValidation.TestHelper;
using PoFightJudge.Shared.Models;
using PoFightJudge.Shared.Validators;
using PoFightJudge.Unit.Profiles;

namespace PoFightJudge.Unit.Shared;

/// <summary>
/// Every Shared form validator: the page shows these messages under the fields, and the API answers with them. One
/// fact, because the Unit tier is capped at 100 cases.
/// </summary>
public class FormValidatorTests
{
    private readonly CreateProfileRequestValidator _sut = new();

    [Fact]
    public void Each_form_passes_when_complete_and_each_broken_rule_names_its_field()
    {
        _sut.TestValidate(ProfileTests.FullRequest()).ShouldNotHaveAnyValidationErrors();
        _sut.TestValidate(new CreateProfileRequest { Initials = "A", Likes = "x", Dislikes = "y" }).ShouldNotHaveAnyValidationErrors();

        (string Property, Action<CreateProfileRequest> Mutate)[] invalidCases =
        [
            (nameof(CreateProfileRequest.Initials), r => r.Initials = ""),
            (nameof(CreateProfileRequest.Initials), r => r.Initials = "ABCD"),
            (nameof(CreateProfileRequest.Initials), r => r.Initials = "A-B"),
            (nameof(CreateProfileRequest.Role), r => r.Role = (ProfileRole)42),
            (nameof(CreateProfileRequest.Name), r => r.Name = new string('n', CreateProfileRequestValidator.MaxNameLength + 1)),
            (nameof(CreateProfileRequest.Age), r => r.Age = 17),
            (nameof(CreateProfileRequest.Age), r => r.Age = 121),
            (nameof(CreateProfileRequest.Likes), r => r.Likes = ""),
            (nameof(CreateProfileRequest.Dislikes), r => r.Dislikes = " "),
            (nameof(CreateProfileRequest.Philosophy), r => r.Philosophy = new string('p', CreateProfileRequestValidator.MaxTextLength + 1)),
            (nameof(CreateProfileRequest.Patience), r => r.Patience = 101),
            (nameof(CreateProfileRequest.Jealousy), r => r.Jealousy = -1),
            ($"{nameof(CreateProfileRequest.TtsSettings)}.{nameof(TtsSettingsDto.Pitch)}", r => r.TtsSettings.Pitch = 2.5),
            ($"{nameof(CreateProfileRequest.TtsSettings)}.{nameof(TtsSettingsDto.Speed)}", r => r.TtsSettings.Speed = 0.1),
            ($"{nameof(CreateProfileRequest.TtsSettings)}.{nameof(TtsSettingsDto.VoiceName)}", r => r.TtsSettings.VoiceName = ""),
        ];

        foreach (var (property, mutate) in invalidCases)
        {
            var request = ProfileTests.FullRequest();
            mutate(request);

            var result = _sut.TestValidate(request);
            result.ShouldHaveValidationErrorFor(property);
            result.Errors.Should().ContainSingle("one message per broken field keeps the form readable");
        }

        // The 2P setup: two different three-letter tags and a short topic.
        var fight = new CreateFightRequestValidator();
        fight.TestValidate(new CreateFightRequest(HostPersonaId.Referee, "ALI", "SAM", "the thermostat")).ShouldNotHaveAnyValidationErrors();

        fight.TestValidate(new CreateFightRequest(HostPersonaId.Referee, "", "SAM")).ShouldHaveValidationErrorFor(r => r.Player1Tag);
        fight.TestValidate(new CreateFightRequest(HostPersonaId.Referee, "AL1", "SAM")).ShouldHaveValidationErrorFor(r => r.Player1Tag)
            .WithErrorMessage(CreateFightRequestValidator.TagMessage);
        fight.TestValidate(new CreateFightRequest(HostPersonaId.Referee, "ALI", "ali")).ShouldHaveValidationErrorFor(r => r.Player2Tag)
            .WithErrorMessage("Two people cannot share one tag.");
        fight.TestValidate(new CreateFightRequest(HostPersonaId.Referee, "ALI", "SAM", new string('t', WatchLimits.MaxTopicLength + 1)))
            .ShouldHaveValidationErrorFor(r => r.Topic);
        fight.TestValidate(new CreateFightRequest((HostPersonaId)99, "ALI", "SAM")).ShouldHaveValidationErrorFor(r => r.Persona);

        // The CPU / 1P setup: both sides in CPU; an opponent and your own tag in 1P.
        var watch = new WatchSetupValidator();
        var husband = MatchSide.Persona("MAH", "Married Husband");
        var wife = MatchSide.Persona("KSH", "Karen");

        watch.TestValidate(new WatchSetup(false, husband, wife, null, "", "")).ShouldNotHaveAnyValidationErrors();
        watch.TestValidate(new WatchSetup(false, husband, null, null, "", "")).ShouldHaveValidationErrorFor(s => s.Wife)
            .WithErrorMessage("Pick both sides.");
        watch.TestValidate(new WatchSetup(false, husband, husband, null, "", "")).ShouldHaveValidationErrorFor(s => s.Wife)
            .WithErrorMessage("Both sides cannot be the same.");

        watch.TestValidate(new WatchSetup(true, husband, MatchSide.Human("ALI"), husband, "ALI", "")).ShouldNotHaveAnyValidationErrors();
        watch.TestValidate(new WatchSetup(true, null, null, null, "ALI", "")).ShouldHaveValidationErrorFor(s => s.Opponent);
        watch.TestValidate(new WatchSetup(true, husband, null, husband, "AL", "")).ShouldHaveValidationErrorFor(s => s.Tag);
        watch.TestValidate(new WatchSetup(true, husband, null, husband, "mah", "")).ShouldHaveValidationErrorFor(s => s.Tag)
            .WithErrorMessage("That tag belongs to the profile you are arguing with.");
        watch.TestValidate(new WatchSetup(false, husband, wife, null, "", new string('t', WatchLimits.MaxTopicLength + 1)))
            .ShouldHaveValidationErrorFor(s => s.Topic);
    }
}
