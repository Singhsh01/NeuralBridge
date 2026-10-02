using Microsoft.Extensions.Options;
using NeuralBridge.Web.Auth;

namespace NeuralBridge.Web.Security;

/// <summary>
/// Adds a strict Content Security Policy and related headers to every response.
/// <list type="bullet">
/// <item><c>script-src 'self'</c>: no inline or eval'd script (all JS ships as static files).</item>
/// <item>Inline <i>style attributes</i> are allowed because Blazor's reconnect UI and a few
/// components set them. Style <i>elements</i> must come from this origin.</item>
/// <item><c>connect-src</c> is limited to this origin over https/wss (or http/ws locally) for the SignalR/Blazor connections.</item>
/// <item><c>form-action</c> adds the Google authorization endpoint only when Google sign-in is configured.</item>
/// </list>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _formAction;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<GoogleAuthOptions> google, IHostEnvironment environment)
    {
        _next = next;
        _formAction = AuthenticationSetup.FormActionSources(google, environment);
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var host = context.Request.Host.Value;
            var ws = context.Request.IsHttps ? $"wss://{host}" : $"ws://{host} wss://{host}";

            headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                "script-src 'self'; " +
                "style-src 'self'; style-src-elem 'self'; style-src-attr 'unsafe-inline'; " +
                "img-src 'self' data: https://*.googleusercontent.com; " +
                "media-src 'self' blob:; " +
                "font-src 'self'; " +
                $"connect-src 'self' {ws}; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                $"form-action {_formAction}; " +
                "frame-ancestors 'none'; " +
                "manifest-src 'self'; " +
                "worker-src 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";

            // Join links carry the session code. Never leak it via Referer to sites opened from shared text.
            headers["Referrer-Policy"] = "no-referrer";
            // Camera and microphone are needed for calls, and only ever requested after a click.
            headers["Permissions-Policy"] = "geolocation=(self), clipboard-write=(self), camera=(self), microphone=(self), display-capture=(), payment=(), usb=(), interest-cohort=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // Shared text must never land in shared/proxy caches.
            if (!context.Request.Path.StartsWithSegments("/_framework") &&
                !context.Request.Path.StartsWithSegments("/fonts") &&
                !context.Request.Path.StartsWithSegments("/css") &&
                !context.Request.Path.StartsWithSegments("/js") &&
                !context.Request.Path.StartsWithSegments("/img") &&
                !context.Request.Path.StartsWithSegments("/icons"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
