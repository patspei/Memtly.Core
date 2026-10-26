using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Memtly.Core.Controllers;

[AllowAnonymous]
public sealed class MediaController(IDatabaseHelper database, IMediaAccessHelper access, IMediaFileStore files,
    IIdentityHelper identity, IMediaThumbnailHelper thumbnails) : Controller
{
    [HttpGet("/Media/DownloadInfo/{id:int}")]
    public async Task<IActionResult> DownloadInfo(int id)
    {
        NoCache();
        var item = await database.GetGalleryItem(id);
        if (item == null || !await access.CanDownload(HttpContext, item)) return NotFound();
        var gallery = await database.GetGallery(item.GalleryId);
        var path = gallery == null ? null : files.GetPath(gallery, item);
        return path == null ? NotFound() : Json(new { count = 1, bytes = new FileInfo(path).Length, filename = item.Title });
    }
    [HttpGet("/Media/Original/{id:int}")]
    [HttpHead("/Media/Original/{id:int}")]
    public async Task<IActionResult> Original([FromRoute] int id, bool download = false) => await Serve(id, false, download);

    [HttpGet("/Media/Thumbnail/{id:int}")]
    [HttpHead("/Media/Thumbnail/{id:int}")]
    public async Task<IActionResult> Thumbnail([FromRoute] int id) => await Serve(id, true, false);

    private async Task<IActionResult> Serve(int id, bool thumbnail, bool download)
    {
        NoCache();
        var item = await database.GetGalleryItem(id);
        if (item == null || !await access.CanRead(HttpContext, item) ||
            (download && !await access.CanDownload(HttpContext, item))) return NotFound();
        var gallery = await database.GetGallery(item.GalleryId);
        var browserPreview = !download && UploadMediaValidator.IsHeif(item.Title);
        var path = gallery == null ? null : thumbnail || browserPreview ? await thumbnails.GetPath(gallery, item) : files.GetPath(gallery, item);
        if (path == null) return NotFound();
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(path, out var contentType)) contentType = "application/octet-stream";
        if ((!contentType.StartsWith("image/", StringComparison.Ordinal) && !contentType.StartsWith("video/", StringComparison.Ordinal)) ||
            contentType == "image/svg+xml") contentType = "application/octet-stream";
        return PhysicalFile(path, contentType, download ? item.Title : null, enableRangeProcessing: true);
    }

    [HttpGet("/Media/CustomResourceThumbnail/{id:int}")]
    public async Task<IActionResult> CustomResourceThumbnail(int id)
    {
        NoCache();
        var resource = await database.GetCustomResource(id);
        if (resource == null || !identity.IsValid(User) || !identity.CanEdit(User, CustomResourcePermissions.View, resource.Owner))
            return NotFound();
        var path = files.GetCustomThumbnail(resource);
        return path == null ? NotFound() : PhysicalFile(path, "image/webp");
    }

    private void NoCache()
    {
        Response.Headers.CacheControl = "private, no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Vary = "Cookie";
        Response.Headers.XContentTypeOptions = "nosniff";
    }
}
