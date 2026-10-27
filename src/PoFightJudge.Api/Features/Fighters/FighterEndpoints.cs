using System.Security.Claims;
using Carter;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Fighters;

/// <summary>
/// The roster: everyone who has ever argued. Reading it is what lets a setup screen suggest a tag somebody already
/// fights under, rather than letting them invent a second one by accident.
/// </summary>
/// <remarks>
/// The roster itself is not owned by anybody. A tag is a person, and two people who argue on one microphone are
/// usually signed in as one of them, so scoping the list of tags per account would hide half of every couple from
/// themselves.
///
/// Their record is a different matter. What was argued about, who said the best line and how it was scored come
/// out of somebody's private debates, so every read of the results is filtered to the account that ran them —
/// otherwise anybody could walk the three-letter tag space and read a stranger's arguments back to them.
/// </remarks>
public sealed class FighterEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var fighters = app.MapGroup(ApiRoutes.Fighters.Base).RequireAuthorization().WithTags("Fighters");

        fighters.MapGet("/", ListAsync).Produces<IReadOnlyList<FighterDto>>();

        fighters.MapGet(ApiRoutes.Fighters.RosterSegment, RosterAsync).Produces<IReadOnlyList<FighterStatsDto>>();

        fighters.MapGet(ApiRoutes.Fighters.ProfileSegment, ProfileAsync)
            .Produces<FighterProfileDto>()
            .Produces(StatusCodes.Status404NotFound);

        fighters.MapGet(ApiRoutes.Fighters.VersusSegment, VersusAsync)
            .Produces<HeadToHeadDto>()
            .Produces(StatusCodes.Status404NotFound);

        fighters.MapPut(ApiRoutes.Fighters.ByTagSegment, RenameAsync).WithValidation<RenameFighterRequest>()
            .Produces<FighterDto>()
            .Produces(StatusCodes.Status404NotFound);

        fighters.MapDelete(ApiRoutes.Fighters.ByTagSegment, DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The roster with everybody's record, in one read of the result rows rather than one read per person. It is
    /// what a list of fighters has to say to be worth looking at: a page of bare tags says nothing.
    /// </summary>
    private static async Task<IReadOnlyList<FighterStatsDto>> RosterAsync(
        ClaimsPrincipal user,
        IFighterRepository fighters,
        IFighterResultRepository results,
        CancellationToken ct)
    {
        var roster = await fighters.ListAsync(ct);
        var all = await results.ListAllAsync(user.UserId(), ct);
        var ratings = EloRatings.Compute(all);
        var byTag = all
            .GroupBy(r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, IReadOnlyList<FighterResultDto> (g) => [.. g], StringComparer.OrdinalIgnoreCase);

        return
        [
            .. roster
                .Select(f => f.ToDto())
                .Select(dto => FighterStatsBuilder.Build(dto, byTag.GetValueOrDefault(dto.Tag, [])) with
                {
                    Rating = ratings.GetValueOrDefault(dto.Tag, EloRatings.Start),
                })
                .OrderByDescending(s => s.Fights)
                .ThenByDescending(s => s.LastFoughtAt)
                .ThenBy(s => s.Tag, StringComparer.Ordinal),
        ];
    }

    /// <summary>Everything one person's page shows: who they are, their record, how they argue, and what they argued.</summary>
    private static async Task<IResult> ProfileAsync(
        FighterId tag,
        ClaimsPrincipal user,
        IFighterRepository fighters,
        IFighterResultRepository results,
        IFighterWordsRepository words,
        IMatchRepository matches,
        CancellationToken ct)
    {
        if (await fighters.GetAsync(tag, ct) is not { } fighter)
        {
            return Results.NotFound();
        }

        var rows = await results.ListForAsync(tag, user.UserId(), ct);
        var dto = fighter.ToDto();

        // A rating depends on everybody they argued with, so it is replayed over the account's whole record.
        var ratings = EloRatings.Compute(await results.ListAllAsync(user.UserId(), ct));

        // Every debate, not the model's bounded corpus: a career number that quietly stopped counting at twelve
        // would be wrong in a way nobody could see from the page.
        var spoken = await words.MineAsync(matches, tag, user.UserId(), ct);
        return Results.Ok(new FighterProfileDto(
            dto,
            FighterStatsBuilder.Build(dto, rows) with { Rating = ratings.GetValueOrDefault(dto.Tag, EloRatings.Start) },
            StyleProfileBuilder.Build(dto.Tag, rows),
            rows,
            CareerWords.Compute(spoken)));
    }
    /// <summary>
    /// The series between two people: every debate this account has on record between them, newest first. It is
    /// read from the same result rows each of their records is, so it cannot disagree with either page.
    /// </summary>
    private static async Task<IResult> VersusAsync(
        FighterId tag,
        FighterId opponent,
        ClaimsPrincipal user,
        IFighterRepository fighters,
        IFighterResultRepository results,
        CancellationToken ct)
    {
        if (await fighters.GetAsync(tag, ct) is not { } one || await fighters.GetAsync(opponent, ct) is not { } two)
        {
            return Results.NotFound();
        }

        var mine = (await results.ListForAsync(tag, user.UserId(), ct))
            .Where(r => string.Equals(r.Opponent, two.Tag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.At)
            .ToList();

        // The other side of each debate is a row of its own, under their tag: that is where their score is.
        var theirs = (await results.ListForAsync(opponent, user.UserId(), ct))
            .GroupBy(r => r.MatchId.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Score, StringComparer.Ordinal);

        var meetings = mine
            .Select(r => new MeetingDto(
                r.MatchId,
                r.Mode,
                r.At,
                r.Topic,
                r.Draw ? string.Empty : r.Won ? one.Tag : two.Tag,
                r.Score,
                theirs.GetValueOrDefault(r.MatchId.Value)))
            .ToList();

        return Results.Ok(new HeadToHeadDto(
            one.Tag,
            one.DisplayName,
            two.Tag,
            two.DisplayName,
            meetings.Count,
            mine.Count(r => r.Won),
            mine.Count(r => !r.Won && !r.Draw),
            mine.Count(r => r.Draw),
            meetings.Count == 0 ? 0 : Math.Round(meetings.Average(m => m.Score), 1),
            meetings.Count == 0 ? 0 : Math.Round(meetings.Average(m => m.OpponentScore), 1),
            [.. meetings.Take(MaxMeetings)]));
    }

    /// <summary>How many debates of a series are sent. The totals count all of them; the list is the recent ones.</summary>
    public const int MaxMeetings = 25;

    /// <summary>
    /// Only the display name can change. The tag is the identity every result is keyed on, so renaming that would
    /// not be a rename — it would be a different person inheriting somebody's record.
    /// </summary>
    private static async Task<IResult> RenameAsync(
        FighterId tag,
        RenameFighterRequest request,
        ClaimsPrincipal user,
        IFighterRepository fighters,
        IFighterResultRepository results,
        CancellationToken ct)
    {
        // The name is shown to everybody who can see the roster. It is theirs to change if they have argued under
        // the tag, or if nobody has yet; somebody else's fighter is not found rather than forbidden.
        var theirs = (await results.ListForAsync(tag, user.UserId(), ct)).Count > 0;
        if (!theirs && (await results.ListForAnyoneAsync(tag, ct)).Count > 0)
        {
            return Results.NotFound();
        }

        return await fighters.RenameAsync(tag, request.DisplayName, ct) is { } renamed ? Results.Ok(renamed.ToDto()) : Results.NotFound();
    }

    /// <summary>
    /// Forgets this person: everything they have on record with this account. The debates themselves stay — the
    /// other side argued in them too, and they are somebody else's history as much as this person's — and the tag
    /// stays on the roster as long as anybody else still has a record under it.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        FighterId tag,
        ClaimsPrincipal user,
        IFighterRepository fighters,
        IFighterResultRepository results,
        IFighterWordsRepository words,
        IProfileRepository profiles,
        CancellationToken ct)
    {
        if (await fighters.GetAsync(tag, ct) is null)
        {
            return Results.NotFound();
        }

        foreach (var row in await results.ListForAsync(tag, user.UserId(), ct))
        {
            await results.DeleteForMatchAsync(row.MatchId, [tag.Value], ct);
            await words.DeleteForMatchAsync(row.MatchId, [tag.Value], ct);
        }

        if ((await results.ListForAnyoneAsync(tag, ct)).Count == 0)
        {
            await words.DeleteForFighterAsync(tag, ct);
            await fighters.DeleteAsync(tag, ct);

            // Their persona was read from these fights; with the last of them gone, it goes too. An authored cast
            // member who happens to share the initials is not theirs and stays.
            var personaId = ProfileId.From(tag.Value);
            if (await profiles.GetByIdAsync(personaId, ct) is { FromFights: true })
            {
                await profiles.DeleteAsync(personaId, ct);
            }
        }

        return Results.NoContent();
    }

    private static async Task<IReadOnlyList<FighterDto>> ListAsync(IFighterRepository fighters, CancellationToken ct)
    {
        var roster = await fighters.ListAsync(ct);
        return [.. roster.Select(f => f.ToDto())];
    }
}
