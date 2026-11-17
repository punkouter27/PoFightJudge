using FluentValidation;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Shared.Validators;

/// <summary>
/// The 2P setup: two tags of exactly three letters, not the same person twice, and a topic short enough to put in a
/// prompt. The API runs it on <c>POST /api/fights</c> and the setup page runs it on every keystroke, so the message
/// under a tag box is the one the server would have answered with.
/// </summary>
public sealed class CreateFightRequestValidator : AbstractValidator<CreateFightRequest>
{
    public const string TagMessage = "A tag is exactly three letters.";

    public CreateFightRequestValidator()
    {
        RuleFor(r => r.Persona).IsInEnum();
        RuleFor(r => r.Player1Tag)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Fighter one needs a tag.")
            .Must(Initials.IsTag).WithMessage(TagMessage);
        RuleFor(r => r.Player2Tag)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Fighter two needs a tag.")
            .Must(Initials.IsTag).WithMessage(TagMessage)
            .Must((r, tag) => !string.Equals(Initials.Normalize(tag), Initials.Normalize(r.Player1Tag), StringComparison.Ordinal))
            .WithMessage("Two people cannot share one tag.");
        RuleFor(r => r.Topic).MaximumLength(WatchLimits.MaxTopicLength)
            .WithMessage($"A topic is at most {WatchLimits.MaxTopicLength} characters.");
        RuleFor(r => r.Player1Role).IsInEnum();
        RuleFor(r => r.Player2Role).IsInEnum();
        RuleFor(r => r.Player1Stance).IsInEnum().When(r => r.Player1Stance.HasValue);
    }
}

/// <summary>
/// What the CPU and 1P setup screen has chosen before it starts a watch. Nothing is posted at that moment — the
/// choices travel in every <see cref="GenerateRoundRequest"/>, which the API checks with its own validator — but the
/// screen is a form and is held to one set of rules, written here beside the request's.
/// </summary>
public sealed record WatchSetup(bool OnePlayer, MatchSide? Husband, MatchSide? Wife, MatchSide? Opponent, string Tag, string Topic);

public sealed class WatchSetupValidator : AbstractValidator<WatchSetup>
{
    public WatchSetupValidator()
    {
        When(s => s.OnePlayer, () =>
        {
            RuleFor(s => s.Opponent).NotNull().WithMessage("Pick who you are arguing with.");
            RuleFor(s => s.Tag)
                .Cascade(CascadeMode.Stop)
                .Must(Initials.IsTag).WithMessage("Say who you are: exactly three letters.")
                .Must((s, tag) => s.Opponent is null || !string.Equals(Initials.Normalize(tag), s.Opponent.Id, StringComparison.OrdinalIgnoreCase))
                .WithMessage("That tag belongs to the profile you are arguing with.");
        }).Otherwise(() =>
        {
            RuleFor(s => s.Husband).NotNull().WithMessage("Pick both sides.");
            RuleFor(s => s.Wife)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("Pick both sides.")
                .Must((s, wife) => s.Husband is null || !string.Equals(s.Husband.Id, wife!.Id, StringComparison.OrdinalIgnoreCase))
                .WithMessage("Both sides cannot be the same.");
        });

        RuleFor(s => s.Topic).MaximumLength(WatchLimits.MaxTopicLength)
            .WithMessage($"A topic is at most {WatchLimits.MaxTopicLength} characters.");
    }
}
