using System.Security.Cryptography;
using System.Text;
using Memtly.Core.Constants;
using Memtly.Core.Enums;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Models.Database;
using Microsoft.AspNetCore.Http.Features;

namespace Memtly.Core.Helpers;

public interface IMediaAccessHelper
{
    void GrantGallery(HttpContext context, GalleryModel gallery);
    Task<bool> CanAccessGallery(HttpContext context, GalleryModel gallery, string? secretKey = null);
    Task<bool> CanRead(HttpContext context, GalleryItemModel item);
    Task<bool> CanDownload(HttpContext context, GalleryItemModel item);
    bool CanManage(HttpContext context, GalleryModel gallery, bool download = false);
    Task<List<GalleryModel>> GetScope(HttpContext context, GalleryModel gallery, string? secretKey = null);
}

/// <summary>One request-scoped policy for gallery cards, previews, originals and archives.</summary>
public sealed class MediaAccessHelper(IDatabaseHelper database, IIdentityHelper identity, ISettingsHelper settings) : IMediaAccessHelper
{
    private readonly Dictionary<int, GalleryModel?> galleries = new();
    private readonly Dictionary<int, bool> access = new();
    private static string GrantKey(int id) => $"Memtly.Media.Gallery.{id}";
    private static string KeyDigest(GalleryModel gallery) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(gallery.SecretKey ?? string.Empty)));

    public void GrantGallery(HttpContext context, GalleryModel gallery)
    {
        access[gallery.Id] = true;
        // Only call after validating the credential. Session IDs are protected by
        // ASP.NET; credentials are never embedded in media URLs or capability tokens.
        context.Features.Get<ISessionFeature>()?.Session?.SetString(GrantKey(gallery.Id), KeyDigest(gallery));
    }

    public bool CanManage(HttpContext context, GalleryModel gallery, bool download = false)
    {
        if (!identity.IsValid(context.User)) return false;
        if (identity.IsOwner(context.User, gallery.Owner)) return true;
        var permissions = identity.GetUserPermissions(context.User);
        return identity.IsPrivilegedUser(context.User) && (download
            ? permissions.Gallery.HasFlag(GalleryPermissions.Download)
            : permissions.Review.HasFlag(ReviewPermissions.View));
    }

    public async Task<bool> CanAccessGallery(HttpContext context, GalleryModel gallery, string? secretKey = null)
    {
        if (secretKey != null && !string.IsNullOrEmpty(gallery.SecretKey) &&
            !string.Equals(secretKey, gallery.SecretKey, StringComparison.Ordinal)) return false;
        if (gallery.Id <= 0 || gallery.Identifier.Equals(SystemGalleries.AllGallery, StringComparison.OrdinalIgnoreCase))
            return identity.IsValid(context.User) && identity.IsPrivilegedUser(context.User) &&
                identity.GetUserPermissions(context.User).Gallery.HasFlag(GalleryPermissions.ViewAllGallery);
        if (gallery.Type is not (GalleryType.Basic or GalleryType.Drop or GalleryType.Collection)) return false;
        if (CanManage(context, gallery)) return true;
        if (string.IsNullOrEmpty(gallery.SecretKey)) return true;
        if (secretKey != null) { GrantGallery(context, gallery); return true; }
        if (context.Features.Get<ISessionFeature>()?.Session?.GetString(GrantKey(gallery.Id)) == KeyDigest(gallery)) return true;
        if (identity.IsValid(context.User))
        {
            var share = await database.GetGalleryShareRecord(identity.GetUserId(context.User), gallery.Id);
            if (share != null && share.UserId == identity.GetUserId(context.User) && share.GalleryId == gallery.Id)
                return true;
        }
        return false;
    }

    private async Task<GalleryModel?> Gallery(int id)
    {
        if (!galleries.TryGetValue(id, out var gallery)) galleries[id] = gallery = await database.GetGallery(id);
        return gallery;
    }

    public async Task<bool> CanRead(HttpContext context, GalleryItemModel item)
    {
        var gallery = await Gallery(item.GalleryId);
        if (gallery == null || gallery.Type is not (GalleryType.Basic or GalleryType.Drop)) return false;
        if (!access.TryGetValue(gallery.Id, out var allowed)) access[gallery.Id] = allowed = await CanAccessGallery(context, gallery);
        if (!allowed) return false;
        if (CanManage(context, gallery)) return item.State is GalleryItemState.Approved or GalleryItemState.Pending or GalleryItemState.Rejected;
        var ownsUpload = identity.IsValid(context.User) && item.UserId == identity.GetUserId(context.User);
        if (gallery.Type == GalleryType.Drop && !ownsUpload) return false;
        return item.State == GalleryItemState.Approved || (item.State == GalleryItemState.Pending && ownsUpload);
    }

    public async Task<bool> CanDownload(HttpContext context, GalleryItemModel item)
    {
        if (!await CanRead(context, item)) return false;
        var gallery = (await Gallery(item.GalleryId))!;
        if (CanManage(context, gallery, download: true)) return true;
        if (item.State != GalleryItemState.Approved) return false;
        return await settings.GetOrDefault(MemtlyConfiguration.Gallery.Download, true, gallery.Id);
    }

    public async Task<List<GalleryModel>> GetScope(HttpContext context, GalleryModel gallery, string? secretKey = null)
    {
        if (!await CanAccessGallery(context, gallery, secretKey)) return [];
        // The container's key never grants a key-protected child gallery.
        var candidates = gallery.Identifier.Equals(SystemGalleries.AllGallery, StringComparison.OrdinalIgnoreCase)
            ? await database.GetGalleries()
            : gallery.Type == GalleryType.Collection ? await database.GetGalleriesByCollectionId(gallery.Id) : [gallery];
        var result = new List<GalleryModel>();
        foreach (var child in candidates ?? [])
        {
            if (child.Id <= 0 || child.Type == GalleryType.Collection) continue;
            if (child.Type == GalleryType.Drop && !identity.IsValid(context.User)) continue;
            galleries[child.Id] = child;
            var allowed = child.Id == gallery.Id || await CanAccessGallery(context, child);
            access[child.Id] = allowed;
            if (allowed) result.Add(child);
        }
        return result;
    }
}
