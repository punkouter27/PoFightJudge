using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Carter;
using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Auth;
using PoFightJudge.Api.Features.Fighters;
using PoFightJudge.Api.Features.Live;
using PoFightJudge.Api.Features.Records;
using PoFightJudge.Api.Features.Watch;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Api.Features.Fight;

/// <summary>
/// Starting, watching and stopping a fight. The tags are validated here and never trusted from the model again: a
/// fight goes on both fighters' records, and the record is keyed by the tag.
/// </summary>
public sealed class FightEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var fights = app.MapGroup(ApiRoutes.Fights.Base).RequireAuthorization().WithTags("Fights");

        // A fight opens a live model session and holds a recording in memory, so starting one is rationed like any
        // other call that reaches a model.
        fights.MapPost("/", StartAsync)
            .RequireRateLimiting(WatchServiceExtensions.AiPolicy)
            .WithValidation<CreateFightRequest>()
            .Produces<CreateFightResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        fights.MapGet(ApiRoutes.Fights.ByIdSegment, GetAsync)
            .Produces<DebateSnapshotDto>()
            .Produces<MatchDto>()
            .Produces(StatusCodes.Status404NotFound);

        fights.MapPost(ApiRoutes.Fights.EndSegment, EndAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        // The hub is part of this feature, so it is mapped with it rather than by hand in the host.
        app.MapHub<LiveHub>(ApiRoutes.Hubs.Live);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The registry owns a running fight for its whole life and disposes it when the fight ends; the request that started it must not.")]
    private static async Task<IResult> StartAsync(
        CreateFightRequest? request,
        ClaimsPrincipal user,
        SessionRegistry registry,
        IFighterRepository fighters,
        IFighterResultRepository results,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Problem("A fight needs two tags and a host.");
        }

        if (!HostPersonaCatalogue.IsKnown(request.Persona))
        {
            return Problem("There is no such host.");
        }

        if (!Initials.AreNewPair(request.Player1Tag, request.Player2Tag))
        {
            return Problem("Both fighters need their own tag: exactly three letters, and not the same as each other.");
        }

        var first = Initials.Normalize(request.Player1Tag);
        var second = Initials.Normalize(request.Player2Tag);

        // Both fighters exist from the moment the fight starts, so a record has somewhere to land even if it ends badly.
        var now = clock.GetUtcNow();
        await fighters.EnsureAsync(FighterId.From(first), now, request.Player1Role, ct);
        await fighters.EnsureAsync(FighterId.From(second), now, request.Player2Role, ct);

        var setup = new ShowSetup(request.Persona, first, second, Topic(request.Topic))
        {
            Player1Stance = Topic(request.Topic) is null ? null : request.Player1Stance,
            Player1Digest = await DigestAsync(results, first, user.UserId(), ct),
            Player2Digest = await DigestAsync(results, second, user.UserId(), ct),
        };

        if (await registry.CreateAsync(user.UserId(), setup, ct) is not { } fight)
        {
            return Results.Problem("Every ring is taken right now. Try again in a minute.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Created(ApiRoutes.Fights.ById(fight.Session.MatchId), new CreateFightResponse(fight.Session.MatchId));
    }

    /// <summary>A running fight answers with its snapshot; a finished one with what was stored.</summary>
    private static async Task<IResult> GetAsync(
        MatchId id,
        ClaimsPrincipal user,
        SessionRegistry registry,
        IMatchRepository matches,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = user.UserId();
        if (registry.Get(id, userId) is { } live)
        {
            return Results.Ok(live.Session.Snapshot(clock.GetUtcNow()));
        }

        var stored = await matches.GetAsync(userId, id, ct);
        return stored is null ? Results.NotFound() : Results.Ok(stored);
    }

    private static async Task<IResult> EndAsync(MatchId id, ClaimsPrincipal user, SessionRegistry registry, CancellationToken ct)
    {
        if (registry.Get(id, user.UserId()) is null)
        {
            return Results.NotFound();
        }

        await registry.EndAsync(id, "ended through the API", ct);
        return Results.NoContent();
    }

    /// <summary>
    /// What the host is told about how this fighter argues, drawn from the debates they have had with this account.
    /// Somebody the room has not met gets "First fight.", which is more use to a host than silence.
    /// </summary>
    private static async Task<string> DigestAsync(IFighterResultRepository results, string tag, string userId, CancellationToken ct) =>
        StyleProfileBuilder.Build(tag, await results.ListForAsync(FighterId.From(tag), userId, ct)).Digest;

    private static string? Topic(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : DebateSession.Clean(trimmed, DebateSession.MaxShortText, string.Empty);
    }

    private static IResult Problem(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "The fight could not be started.");
}
