using System.Security.Cryptography;
using System.Text;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;
using Memtly.Core.Helpers.Database;
using Microsoft.Extensions.Caching.Memory;

namespace Memtly.Core.Helpers;

public static class WeddingAccessHelper
{
    private const string SessionKey = "Memtly.Wedding.Access";
    private const string BrowserCookie = ".Memtly.Wedding.Session";
    private static readonly MemoryCache Attempts = new(new MemoryCacheOptions { SizeLimit = 10000 });
    private static readonly object AttemptLock = new();

    public static async Task<List<GalleryModel>?> Resolve(IDatabaseHelper database, WeddingOptions options)
    {
        if (!options.Enabled) return null;
        var all = await database.GetGalleries();
        var result = new List<GalleryModel>();
        foreach (var target in new[] { options.ProfessionalGallery, options.GuestGallery, options.PrivateGallery })
        {
            var matches = all.Where(g => options.Matches(g, target)).ToList();
            if (matches.Count != 1 || !options.ValidType(matches[0]) || matches[0].Id <= 0) return null;
            result.Add(matches[0]);
        }
        return result.Select(g => g.Id).Distinct().Count() == 3 ? result : null;
    }

    public static string? Code(WeddingOptions options, List<GalleryModel> galleries)
    {
        if (!string.IsNullOrWhiteSpace(options.AccessCode)) return options.AccessCode;
        var keys = galleries.Select(g => g.SecretKey).Distinct(StringComparer.Ordinal).ToList();
        return keys.Count == 1 && !string.IsNullOrWhiteSpace(keys[0]) ? keys[0] : null;
    }

    private static string Digest(WeddingOptions options, List<GalleryModel> galleries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            new { Code = Code(options, galleries), Galleries = galleries.Select(g => new { g.Id, g.SecretKey }) }))));

    public static bool IsGranted(HttpContext context, WeddingOptions options, List<GalleryModel>? galleries) =>
        galleries != null && Code(options, galleries) != null && context.Session.GetString(SessionKey) == Digest(options, galleries) &&
        context.Request.Cookies.TryGetValue(BrowserCookie, out var browser) &&
        context.Session.GetString(SessionKey + ".Browser") == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(browser)));

    public static bool Validate(string? supplied, string expected) => supplied is { Length: <= 256 } &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static void Grant(HttpContext context, WeddingOptions options, List<GalleryModel> galleries, IMediaAccessHelper access)
    {
        context.Session.Remove("Memtly.Wedding.Identity");
        foreach (var gallery in galleries) access.GrantGallery(context, gallery);
        context.Session.SetString(SessionKey, Digest(options, galleries));
        var browser = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        context.Session.SetString(SessionKey + ".Browser", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(browser))));
        context.Response.Cookies.Append(BrowserCookie, browser, new CookieOptions
            { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true, Path = "/" });
    }

    // Limit each session, with a larger peer budget for guests sharing Wi-Fi or a reverse proxy.
    // Clearing cookies cannot reset the peer budget. Never trust arbitrary forwarding headers here.
    private static string[] AttemptKeys(HttpContext context) =>
        ["peer:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"), "session:" + context.Session.Id];

    public static bool TakeAttempt(HttpContext context)
    {
        lock (AttemptLock)
        {
            var windows = AttemptKeys(context).Select(key =>
            {
                if (!Attempts.TryGetValue(key, out AttemptWindow? window))
                {
                    window = new();
                    Attempts.Set(key, window, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(15)));
                }
                return window!;
            }).ToArray();
            if (windows[0].Count >= 30 || windows[1].Count >= 5) return false;
            foreach (var window in windows) window.Count++;
            return true;
        }
    }
    public static void ReleaseSuccessfulAttempt(HttpContext context)
    {
        lock (AttemptLock)
            foreach (var key in AttemptKeys(context))
                if (Attempts.TryGetValue(key, out AttemptWindow? window)) window!.Count = Math.Max(0, window.Count - 1);
    }
    private sealed class AttemptWindow { public int Count; }
}
