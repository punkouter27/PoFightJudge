using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PoFightJudge.Api.Features.Diagnostics;

/// <summary>
/// Gemini answering 402 means the account's prepaid credit is spent: every AI call fails until somebody tops it up in
/// AI Studio. That is not a bug to retry, and "an error occurred" sends people looking for one, so every AI route says
/// what it is in one place rather than each endpoint catching it.
/// </summary>
public sealed partial class AiUnavailableExceptionHandler(ILogger<AiUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not HttpRequestException { StatusCode: HttpStatusCode.PaymentRequired } || httpContext.Response.HasStarted)
        {
            return false;
        }

        LogOutOfCredit(logger);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The AI is out of credit.",
                Detail = "The Gemini account's prepaid credit is used up, so nothing AI can run until it is topped up in AI Studio.",
            },
            cancellationToken);
        return true;
    }

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error, Message = "Gemini refused the call: the account's prepaid credit is depleted (402). Top it up in AI Studio.")]
    private static partial void LogOutOfCredit(ILogger logger);
}
