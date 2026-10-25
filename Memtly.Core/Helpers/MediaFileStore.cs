using System.Reflection;
using Memtly.Core.Enums;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Helpers;

public interface IMediaFileStore
{
    string? GetPath(GalleryModel gallery, GalleryItemModel item);
    string? GetCustomThumbnail(CustomResourceModel resource);
    IEnumerable<GalleryItemModel> GetRetainedRejected(GalleryModel gallery);
    string? GetThumbnailCachePath(GalleryModel gallery, string fileName);
}

/// <summary>Only database-owned single path segments; never a caller-supplied path.</summary>
public sealed class MediaFileStore : IMediaFileStore
{
    private readonly string root;
    public MediaFileStore() : this(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!) { }
    public MediaFileStore(string root) { this.root = Path.GetFullPath(root); }

    private static bool Segment(string value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value == value.Trim() && !value.EndsWith('.') && !value.Any(c => char.IsControl(c) || "/\\:%".Contains(c));

    private string? Resolve(params string[] segments) => Resolve(false, segments);

    private string? Resolve(bool directory, params string[] segments)
    {
        if (segments.Any(s => !Segment(s))) return null;
        var path = root;
        try
        {
            foreach (var segment in new[] { string.Empty }.Concat(segments))
            {
                if (segment.Length > 0) path = Path.Combine(path, segment);
                // Reject symlinks/junctions in every existing path component.
                if ((File.Exists(path) || Directory.Exists(path)) &&
                    File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            }
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
            return (directory ? Directory.Exists(path) : File.Exists(path)) ? path : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public string? GetPath(GalleryModel gallery, GalleryItemModel item)
    {
        if (gallery.Id != item.GalleryId || !Segment(item.Title)) return null;
        if (item.State is not (GalleryItemState.Approved or GalleryItemState.Pending or GalleryItemState.Rejected)) return null;
        return item.State switch
        {
            GalleryItemState.Approved => Resolve("uploads", gallery.Identifier, item.Title),
            GalleryItemState.Pending => Resolve("uploads", gallery.Identifier, "Pending", item.Title),
            _ => Resolve("uploads", gallery.Identifier, "Rejected", item.Title)
        };
    }

    public string? GetCustomThumbnail(CustomResourceModel resource) => !Segment(resource.FileName) ? null :
        Resolve("thumbnails", "CustomResources", Path.GetFileNameWithoutExtension(resource.FileName) + ".webp");

    // Old Memtly versions deleted DB rows even when retaining rejected files.
    // Only the archive planner for an authorized manager uses this compatibility
    // inventory; no guest route or media ID is created for these orphaned files.
    public IEnumerable<GalleryItemModel> GetRetainedRejected(GalleryModel gallery)
    {
        var directory = Resolve(true, "uploads", gallery.Identifier, "Rejected");
        if (directory == null) return [];
        var result = new List<GalleryItemModel>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var item = new GalleryItemModel { GalleryId = gallery.Id, GalleryName = gallery.Name,
                Title = Path.GetFileName(file), State = GalleryItemState.Rejected,
                UploadedDate = new DateTimeOffset(File.GetCreationTimeUtc(file)) };
            if (GetPath(gallery, item) != null) result.Add(item);
        }
        return result;
    }

    public string? GetThumbnailCachePath(GalleryModel gallery, string fileName)
    {
        if (!Segment(gallery.Identifier) || !Segment(fileName)) return null;
        // Validate before and after creation, including junctions/symlinks.
        var segments = new[] { "thumbnails", "Secure", gallery.Identifier };
        var path = root;
        foreach (var segment in new[] { string.Empty }.Concat(segments))
        {
            if (segment.Length > 0) path = Path.Combine(path, segment);
            if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
        }
        Directory.CreateDirectory(path);
        var directory = Resolve(true, segments);
        if (directory == null) return null;
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) return null;
        return target;
    }
}
