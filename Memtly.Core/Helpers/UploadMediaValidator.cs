using System.Text;
using SkiaSharp;

namespace Memtly.Core.Helpers;

public static class UploadMediaValidator
{
    public const long MaxPixels = 50_000_000;
    public static bool IsHeif(string path) => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif";

    public static bool IsValid(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (IsHeif(path) || extension is ".mp4" or ".mov")
            {
                Span<byte> header = stackalloc byte[256];
                var count = stream.Read(header);
                if (count < 16) return false;
                var box = Encoding.ASCII.GetString(header.Slice(4, 4));
                if (!IsHeif(path) && extension == ".mov" && box is "moov" or "mdat" or "wide") return true;
                if (box != "ftyp") return false;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length < 16 || length > stream.Length) return false;
                for (var offset = 8; offset + 4 <= Math.Min(count, length); offset += 4)
                {
                    if (offset == 12) continue; // Minor version, not a compatible brand.
                    var brand = Encoding.ASCII.GetString(header.Slice(offset, 4));
                    if (IsHeif(path))
                    {
                        if (brand is "heic" or "heix" or "hevc" or "hevx" || (extension == ".heif" && brand is "mif1" or "msf1")) return true;
                    }
                    else if (brand is "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6" or "mp41" or "mp42" or "avc1" or "dash" or "M4V " or "MSNV" or "qt  ") return true;
                }
                return false;
            }
            using var codec = SKCodec.Create(stream);
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > MaxPixels) return false;
            return extension switch
            {
                ".jpg" or ".jpeg" => codec.EncodedFormat == SKEncodedImageFormat.Jpeg,
                ".png" => codec.EncodedFormat == SKEncodedImageFormat.Png,
                ".webp" => codec.EncodedFormat == SKEncodedImageFormat.Webp,
                _ => false
            };
        }
        catch { return false; }
    }
}
