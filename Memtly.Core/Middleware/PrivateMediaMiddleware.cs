namespace Memtly.Core.Middleware;

/// <summary>Legacy disk URLs must never reach static-file middleware, including
/// copies accidentally placed below wwwroot. Storage stays outside the web root.</summary>
public sealed class PrivateMediaMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        for (var i = 0; i < 3; i++) path = Uri.UnescapeDataString(path);
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }
        // Also deny accidental RCL/web-root copies such as
        // /_content/Memtly.Core/uploads/...; only the ID routes may serve media.
        if (segments.Any(segment => new[] { "uploads", "thumbnails", "temp" }.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }
        await next(context);
    }
}
