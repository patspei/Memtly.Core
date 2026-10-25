using Memtly.Core.Constants;
using Memtly.Core.Enums;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Helpers;

public sealed record GalleryDownloadPlan(int StatusCode, string FileName, List<ZipListing> Files);

public sealed class GalleryDownloadHelper(IDatabaseHelper database, IMediaAccessHelper access,
    IMediaFileStore files, IIdentityHelper identity, ISettingsHelper settings)
{
    public async Task<GalleryDownloadPlan> Build(HttpContext context, int id, string? secretKey,
        string? group, List<string>? fileFilter)
    {
        GalleryDownloadPlan Denied(int code) => new(code, string.Empty, []);
        var gallery = await database.GetGallery(id);
        if (gallery == null) return Denied(404);
        if (!await access.CanAccessGallery(context, gallery, secretKey)) return Denied(403);
        if (gallery.Type == GalleryType.Drop && !identity.IsValid(context.User)) return Denied(403);
        if (!access.CanManage(context, gallery, download: true) &&
            !await settings.GetOrDefault(MemtlyConfiguration.Gallery.Download, true, gallery.Id)) return Denied(403);

        GalleryGroup grouping = GalleryGroup.None;
        GalleryItemState state = GalleryItemState.All;
        string groupName = string.Empty;
        if (!string.IsNullOrWhiteSpace(group))
        {
            var parts = group.Split('|');
            if (parts.Length != 3 || !Enum.TryParse(parts[0], true, out grouping) || !Enum.IsDefined(grouping) ||
                !Enum.TryParse(parts[2], true, out state) || !Enum.IsDefined(state) || state == GalleryItemState.Unknown)
                return Denied(400);
            groupName = parts[1];
        }
        // Filter values are literal DB titles. Never normalize a malicious path
        // to a basename or treat an empty intersection as "download everything".
        if (fileFilter?.Any(name => string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") == true)
            return Denied(400);
        var filter = fileFilter?.Count > 0 ? fileFilter.ToHashSet(StringComparer.Ordinal) : null;
        var scope = await access.GetScope(context, gallery, secretKey);
        if (scope.Count == 0) return Denied(404);
        var byId = scope.ToDictionary(g => g.Id);
        var items = await database.GetGalleryItems(new GalleryItemSearch
        {
            GalleryIds = scope.Select(g => g.Id).ToList(),
            ItemState = new() { Approved = ItemOwner.All, Pending = ItemOwner.All, Rejected = ItemOwner.All }
        });
        foreach (var source in scope)
        {
            if (!access.CanManage(context, source, download: true)) continue;
            foreach (var legacy in files.GetRetainedRejected(source))
                if (!items.Any(i => i.GalleryId == legacy.GalleryId && i.State == GalleryItemState.Rejected && i.Title == legacy.Title))
                    items.Add(legacy);
        }
        var listing = new List<ZipListing>();
        foreach (var item in items)
        {
            if (!byId.TryGetValue(item.GalleryId, out var source) || !await access.CanDownload(context, item)) continue;
            if (filter != null && !filter.Contains(item.Title)) continue;
            if (state != GalleryItemState.All && item.State != state) continue;
            var name = grouping switch
            {
                GalleryGroup.Gallery => source.Name,
                GalleryGroup.DateUploaded => item.UploadedDate.ToLocalTime().ToString("dddd, d MMMM yyyy"),
                GalleryGroup.DateTaken => (item.DateTaken ?? item.UploadedDate).ToLocalTime().ToString("dddd, d MMMM yyyy"),
                GalleryGroup.MediaType => item.MediaType.ToString(),
                GalleryGroup.Uploader => string.IsNullOrWhiteSpace(item.UploadedBy) ? "Anonymous" : item.UploadedBy,
                _ => groupName
            };
            if (grouping != GalleryGroup.None && !string.Equals(name, groupName, StringComparison.Ordinal)) continue;
            var path = files.GetPath(source, item);
            if (path == null) continue;
            var directory = scope.Count > 1 ? $"{source.Identifier}/{item.State}" : item.State.ToString();
            listing.Add(new ZipListing(Path.GetDirectoryName(path)!, [path], directory));
        }
        if (listing.Count == 0) return Denied(404);
        var identifier = GalleryHelper.IsValidGalleryIdentifier(gallery.Identifier) ? gallery.Identifier : "Memtly";
        return new(200, $"{identifier}_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.zip", listing);
    }
}
