using Memtly.Core.Enums;
using Memtly.Core.Models.Database;

namespace Memtly.Core.Models;

public sealed class WeddingOptions
{
    public bool Enabled { get; set; }
    public string Couple { get; set; } = "Julia & Patrick";
    public string HeroImage { get; set; } = "/_content/Memtly.Core/images/DemoImage.png";
    public string ProfessionalGallery { get; set; } = "Unsere Hochzeit";
    public string GuestGallery { get; set; } = "Eure Hochzeitsfotos";
    public string PrivateGallery { get; set; } = "Nur für Julia & Patrick";

    public static WeddingOptions Read(IConfiguration? configuration) =>
        configuration?.GetSection("Wedding").Get<WeddingOptions>() ?? new();

    public bool Matches(GalleryModel gallery, string value) =>
        gallery.Identifier.Equals(value, StringComparison.OrdinalIgnoreCase) ||
        gallery.Name.Equals(value, StringComparison.OrdinalIgnoreCase);

    public bool IsProfessional(GalleryModel gallery) => Enabled && Matches(gallery, ProfessionalGallery);
    public bool NeedsReview(GalleryModel gallery) => Enabled && Matches(gallery, GuestGallery);
    public bool IsPrivate(GalleryModel gallery) => Enabled && Matches(gallery, PrivateGallery);
    public bool IsWedding(GalleryModel gallery) => IsProfessional(gallery) || NeedsReview(gallery) || IsPrivate(gallery);
    public bool ValidType(GalleryModel gallery) => IsPrivate(gallery)
        ? gallery.Type == GalleryType.Drop : gallery.Type == GalleryType.Basic;
}
