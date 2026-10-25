using System.Security.Cryptography;
using System.Text;
using Memtly.Core.Constants;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Helpers;

public interface IMediaThumbnailHelper
{
    Task<string?> GetPath(GalleryModel gallery, GalleryItemModel item);
}

/// <summary>Build from the authorized original, never a shared basename thumbnail
/// that might have been overwritten by a pending file with another extension.</summary>
public sealed class MediaThumbnailHelper(IMediaFileStore files, IImageHelper images, ISettingsHelper settings) : IMediaThumbnailHelper
{
    public async Task<string?> GetPath(GalleryModel gallery, GalleryItemModel item)
    {
        var original = files.GetPath(gallery, item);
        if (original == null) return null;
        var size = Math.Clamp(await settings.GetOrDefault(MemtlyConfiguration.Gallery.Thumbnails.Size, 720, gallery.Id), 100, 4096);
        var info = new FileInfo(original);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{item.Id}|{item.State}|{item.Title}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{size}")));
        var target = files.GetThumbnailCachePath(gallery, $"{item.Id}-{version}.webp");
        if (target == null) return null;
        if (File.Exists(target)) return target;
        var temporary = files.GetThumbnailCachePath(gallery, $"{Guid.NewGuid():N}.webp");
        if (temporary == null) return null;
        try
        {
            if (!await images.GenerateThumbnail(original, temporary, size) || !File.Exists(temporary)) return null;
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { /* Another request completed first. */ }
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
