using Memtly.Core.Helpers.Database;
using Memtly.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Memtly.Core.Controllers;

[AllowAnonymous]
public sealed class WeddingController(IConfiguration configuration, IDatabaseHelper database) : Controller
{
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
        // Resolve only the destination. A protected gallery still requires its existing credential.
        return RedirectToAction("Index", "Gallery", new { identifier = matches[0].Identifier });
    }
}
