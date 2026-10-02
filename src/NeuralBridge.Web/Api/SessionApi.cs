using NeuralBridge.Application.Common;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Web.Infrastructure;

namespace NeuralBridge.Web.Api;

/// <summary>Validated by the application layer (lengths, PIN policy, offered lifetimes).</summary>
public sealed record CreateSessionRequest(string? DisplayName, string? Pin, int? LifetimeMinutes);

public sealed record JoinSessionRequest(string? Code, string? Pin, string? DisplayName);

public sealed record SessionCredentialsResponse(
    string PublicId,
    Guid ParticipantId,
    string Token,
    string? Code,
    string? ShareUrl,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// JSON API for non-browser clients. Credentials returned here are used with
/// <c>/hubs/session</c> → <c>Attach(publicId, participantId, token)</c>.
/// These endpoints never authenticate with cookies, so CSRF does not apply. Sessions
/// created here are always guest sessions.
/// </summary>
public static class SessionApi
{
    public const string CreatePolicy = "api-create";
    public const string JoinPolicy = "api-join";

    public static IEndpointRouteBuilder MapSessionApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        group.MapPost("/", async (CreateSessionRequest request, ISessionService sessions, HttpContext http, CancellationToken ct) =>
        {
            var result = await sessions.CreateAsync(
                new CreateSessionCommand(request.DisplayName, request.Pin, request.LifetimeMinutes, OwnerUserId: null, ClientContext.FromHttpContext(http)),
                ct);
            if (!result.Succeeded)
            {
                return Problem(result);
            }

            var created = result.Value!;
            var shareUrl = $"{http.Request.Scheme}://{http.Request.Host}/join/{created.FormattedCode}";
            return Results.Created(
                $"/session/{created.PublicId}",
                new SessionCredentialsResponse(created.PublicId, created.Credentials.ParticipantId, created.Credentials.Token, created.FormattedCode, shareUrl, created.ExpiresAt));
        }).RequireRateLimiting(CreatePolicy);

        group.MapPost("/join", async (JoinSessionRequest request, ISessionService sessions, HttpContext http, CancellationToken ct) =>
        {
            var result = await sessions.JoinAsync(new JoinSessionCommand(request.Code, request.Pin, request.DisplayName, ClientContext.FromHttpContext(http)), ct);
            if (!result.Succeeded)
            {
                return Problem(result);
            }

            var joined = result.Value!;
            return Results.Ok(new SessionCredentialsResponse(joined.PublicId, joined.Credentials.ParticipantId, joined.Credentials.Token, null, null, null));
        }).RequireRateLimiting(JoinPolicy);

        return app;
    }

    internal static IResult Problem(Result result) =>
        Results.Problem(
            title: result.Error.ToString(),
            detail: result.Message,
            statusCode: StatusFor(result.Error));

    internal static int StatusFor(SessionError error) => error switch
    {
        SessionError.InvalidCode or SessionError.ValidationFailed => StatusCodes.Status400BadRequest,
        SessionError.Unauthorized => StatusCodes.Status401Unauthorized,
        SessionError.Forbidden or SessionError.Removed or SessionError.ReadOnly => StatusCodes.Status403Forbidden,
        SessionError.NotFoundOrPinIncorrect or SessionError.NotFound => StatusCodes.Status404NotFound,
        SessionError.CapacityReached or SessionError.ExtensionLimitReached => StatusCodes.Status409Conflict,
        SessionError.Expired or SessionError.Closed => StatusCodes.Status410Gone,
        SessionError.PinLocked => StatusCodes.Status423Locked,
        SessionError.RateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };
}
