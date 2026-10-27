namespace PoFightJudge.Api.Common;

/// <summary>
/// Defence-in-depth headers for every response. The CSP allows the Blazor runtime (wasm), Radzen's inline styles,
/// Google Fonts, our own SignalR WebSocket, and the Microsoft login endpoints used by MSAL — nothing else.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        // The browser transcriber's data, when somebody turns it on: the model from Hugging Face (which redirects to
        // its CDN) and the ONNX runtime's .wasm, pinned to the one build the vendored script was made against.
        "connect-src 'self' ws: wss: https://login.microsoftonline.com https://huggingface.co https://*.hf.co " +
        "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.31.0-dev.20260914-8d85527a0/; " +
        "img-src 'self' data: blob:; " +
        // Highlight clips and round audio are fetched through the authenticated API and handed to <audio> as data/blob
        // URLs, because in Production a bare src would carry no bearer token.
        "media-src 'self' data: blob:; " +
        "worker-src 'self' blob:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self' https://login.microsoftonline.com";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=()";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        return next(context);
    }
}
