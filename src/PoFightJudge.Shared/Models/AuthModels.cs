namespace PoFightJudge.Shared.Models;

/// <summary>
/// Who the API thinks is calling. <c>GET /auth/me</c> answers this for anonymous and signed-in callers alike.
/// <see cref="Email"/> is the sign-in address when the credential carries one (the Po header shows it); a guest has none.
/// </summary>
public sealed record AuthMeDto(bool Authenticated, string? UserId, string? Name, string? AuthType, string? Email = null)
{
    public static readonly AuthMeDto Anonymous = new(false, null, null, null);
}
