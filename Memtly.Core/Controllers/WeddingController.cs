using Memtly.Core.Helpers.Database;
using Memtly.Core.Models;
using Memtly.Core.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Memtly.Core.Controllers;

[AllowAnonymous]
public sealed class WeddingController(IConfiguration configuration, IDatabaseHelper database, IMediaAccessHelper access) : Controller
{
    [HttpGet("/Wedding/Login")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!WeddingOptions.Read(configuration).Enabled) return NotFound();
        ViewBag.ReturnUrl = SafeReturn(returnUrl);
        return View();
    }

    [HttpPost("/Wedding/Login")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Login(string? code, string? returnUrl = null)
    {
        var options = WeddingOptions.Read(configuration);
        if (!options.Enabled) return NotFound();
        ViewBag.ReturnUrl = SafeReturn(returnUrl);
        if (!WeddingAccessHelper.TakeAttempt(HttpContext))
        {
            Response.StatusCode = 429;
            Response.Headers.RetryAfter = "900";
            ViewBag.AccessError = "Zu viele Versuche. Bitte versuche es in 15 Minuten erneut.";
            return View();
        }
        var galleries = await WeddingAccessHelper.Resolve(database, options);
        var expected = galleries == null ? null : WeddingAccessHelper.Code(options, galleries);
        if (expected == null)
        {
            Response.StatusCode = 503;
            ViewBag.AccessError = "Der Zugang wird gerade vorbereitet. Bitte versuche es später noch einmal.";
            return View();
        }
        if (!WeddingAccessHelper.Validate(code, expected))
        {
            Response.StatusCode = 400;
            ViewBag.AccessError = "Der Zugangscode stimmt leider nicht. Bitte versuche es noch einmal.";
            return View();
        }
        WeddingAccessHelper.Grant(HttpContext, options, galleries!, access);
        WeddingAccessHelper.ReleaseSuccessfulAttempt(HttpContext);
        return LocalRedirect(SafeReturn(returnUrl));
    }

    private static string SafeReturn(string? value)
    {
        if (value is "/Wedding/Gallery/professional" or "/Wedding/Gallery/guests" or "/Wedding/Gallery/private") return value;
        if (value?.StartsWith("/Gallery?identifier=", StringComparison.Ordinal) == true)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(value[8..]);
            var identifier = query["identifier"].ToString();
            if (identifier.Length is > 0 and <= 200) return $"/Gallery?identifier={Uri.EscapeDataString(identifier)}";
        }
        return "/";
    }

    [HttpGet("/Wedding/Gallery/{role}")]
    public async Task<IActionResult> Gallery(string role)
    {
        var options = WeddingOptions.Read(configuration);
        if (!options.Enabled) return NotFound();
        var target = role switch
        {
            "professional" => options.ProfessionalGallery,
            "guests" => options.GuestGallery,
            "private" => options.PrivateGallery,
            _ => null
        };
        if (target == null) return NotFound();
        var matches = (await database.GetGalleries()).Where(g => options.Matches(g, target)).ToList();
        if (matches.Count != 1 || !options.ValidType(matches[0]))
            return StatusCode(503, "Diese Galerie wird gerade vorbereitet. Bitte versucht es später noch einmal.");
        if (!await access.CanAccessGallery(HttpContext, matches[0]))
            return RedirectToAction("Login", new { returnUrl = $"/Wedding/Gallery/{role}" });
        return RedirectToAction("Index", "Gallery", new { identifier = matches[0].Identifier });
    }
}
