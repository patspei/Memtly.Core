using System.Reflection;
using Memtly.Core.Constants;
using Memtly.Core.Enums;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.Xmp;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Localization;
using SkiaSharp;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Memtly.Core.Helpers
{
    public interface IImageHelper
    {
        Task<bool> GenerateThumbnail(string filePath, string savePath, int size = 720);
        Task<bool> GeneratePreview(string filePath, string savePath);
        MediaType GuessMediaTypeFromExtension(string path);
        MediaType GetMediaType(string filePath);
        ImageOrientation GetOrientation(string path);
        DateTime? GetExifCreationDateTaken(string path);
        Task<bool> DownloadFFMPEG(string path);
    }

    public class ImageHelper : IImageHelper
    {
        private readonly IFileHelper _fileHelper;
        private readonly ILogger _logger;
        private readonly IStringLocalizer<Localization.Translations> _localizer;

        private static bool FfmpegInstalled = false;
        private static readonly SemaphoreSlim ThumbnailSlots = new(1);

        public ImageHelper(IFileHelper fileHelper, ILogger<ImageHelper> logger, IStringLocalizer<Localization.Translations> localizer)
        {
            _fileHelper = fileHelper;
            _logger = logger;
            _localizer = localizer;
        }

        public async Task<bool> GenerateThumbnail(string filePath, string savePath, int resolution = 720)
            => await GenerateVariant(filePath, savePath, Math.Clamp(resolution, 100, 4096), false);

        public async Task<bool> GeneratePreview(string filePath, string savePath)
            => await GenerateVariant(filePath, savePath, 1920, true);

        private async Task<bool> GenerateVariant(string filePath, string savePath, int resolution, bool preview)
        {
            if (!await ThumbnailSlots.WaitAsync(TimeSpan.FromSeconds(15))) return false;
            try { return await GenerateThumbnailCore(filePath, savePath, resolution, preview); }
            finally { ThumbnailSlots.Release(); }
        }

        private async Task<bool> GenerateThumbnailCore(string filePath, string savePath, int resolution, bool preview)
        {
            if (UploadMediaValidator.IsHeif(filePath))
            {
                var success = await HeifThumbnail.Generate(filePath, savePath, resolution, preview ? 84 : 80);
                if (!success && File.Exists(savePath)) File.Delete(savePath);
                return success;
            }
            if (_fileHelper.FileExists(filePath))
            {
                var mediaType = MediaType.Unknown;

                try
                {
                    mediaType = GetMediaType(filePath);
                    if (preview && mediaType != MediaType.Image) return false;
                    if (mediaType == MediaType.Image || mediaType == MediaType.Video)
                    {
                        var filename = Path.GetFileName(filePath);

                        if (mediaType == MediaType.Video)
                        {
                            try
                            {
                                if (FfmpegInstalled == false)
                                {
                                    _logger.LogWarning(_localizer["FFMPEG_Downloading"].Value);
                                    return false;
                                }

                                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(filePath, savePath, TimeSpan.FromSeconds(0));
                                await conversion.Start();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Failed to grab thumbnail frame from video - '{filePath}' - {ex?.Message}");
                                File.Copy(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!, Directories.Private.Assets, $"BrokenVideo.webp"), savePath, true);
                            }

                            filePath = savePath;
                        }

                        using var input = File.OpenRead(filePath);
                        using var codec = SKCodec.Create(input, out var result) ?? throw new InvalidOperationException("Unsupported or invalid image.");
                        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > UploadMediaValidator.MaxPixels)
                            throw new InvalidDataException("Image exceeds the pixel limit.");

                        using var rawBitmap = new SKBitmap(codec.Info);
                        var decodeResult = codec.GetPixels(codec.Info, rawBitmap.GetPixels());
                        if (decodeResult != SKCodecResult.Success)
                        { 
                            throw new InvalidOperationException($"Pixel decode failed: {decodeResult}");
                        }

                        using var rawImage = SKImage.FromBitmap(rawBitmap);

                        var (originWidth, originHeight) = GetDimensions(codec);

                        SKImage correctedImage;
                        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft)
                        {
                            correctedImage = rawImage;
                        }
                        else
                        {
                            using var orientSurface = SKSurface.Create(new SKImageInfo(originWidth, originHeight));
                            var orientCanvas = orientSurface.Canvas;
                            orientCanvas.Clear(SKColors.Transparent);

                            switch (codec.EncodedOrigin)
                            {
                                case SKEncodedOrigin.TopRight:
                                    orientCanvas.Translate(originWidth, 0);
                                    orientCanvas.Scale(-1, 1);
                                    break;
                                case SKEncodedOrigin.BottomRight:
                                    orientCanvas.Translate(originWidth, originHeight);
                                    orientCanvas.RotateDegrees(180);
                                    break;
                                case SKEncodedOrigin.BottomLeft:
                                    orientCanvas.Translate(0, originHeight);
                                    orientCanvas.Scale(1, -1);
                                    break;
                                case SKEncodedOrigin.LeftTop:
                                    orientCanvas.RotateDegrees(90);
                                    orientCanvas.Scale(1, -1);
                                    break;
                                case SKEncodedOrigin.RightTop:
                                    orientCanvas.Translate(originWidth, 0);
                                    orientCanvas.RotateDegrees(90);
                                    break;
                                case SKEncodedOrigin.RightBottom:
                                    orientCanvas.Translate(originWidth, originHeight);
                                    orientCanvas.RotateDegrees(90);
                                    orientCanvas.Scale(-1, 1);
                                    break;
                                case SKEncodedOrigin.LeftBottom:
                                    orientCanvas.Translate(0, originHeight);
                                    orientCanvas.RotateDegrees(-90);
                                    break;
                            }

                            orientCanvas.DrawImage(rawImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                            correctedImage = orientSurface.Snapshot();
                        }

                        int width, height, drawWidth, drawHeight;

                        if (preview)
                        {
                            var scale = Math.Min(1d, (double)resolution / Math.Max(originWidth, originHeight));
                            width = Math.Max(1, (int)Math.Round(originWidth * scale));
                            height = Math.Max(1, (int)Math.Round(originHeight * scale));
                        }
                        else if (originHeight > originWidth)
                        {
                            var scale = (float)resolution / originHeight;
                            width = (int)Math.Round(originWidth * scale);
                            height = resolution;
                        }
                        else
                        {
                            var scale = (float)resolution / originWidth;
                            width = resolution;
                            height = (int)Math.Round(originHeight * scale);
                        }
                        
                        drawWidth = width;
                        drawHeight = height;

                        var drawX = (int)((width - drawWidth) / 2f);
                        var drawY = (int)((height - drawHeight) / 2f);

                        using var surface = SKSurface.Create(new SKImageInfo(width, height));
                        var canvas = surface.Canvas;
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawImage(correctedImage, new SKRect(drawX, drawY, drawX + drawWidth, drawY + drawHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                        canvas.ClipRect(new SKRect(0, 0, width, height));

                        if (!ReferenceEquals(correctedImage, rawImage))
                        { 
                            correctedImage.Dispose();
                        }

                        using var thumbnail = surface.Snapshot(new SKRectI(0, 0, width, height));
                        using var data = thumbnail.Encode(SKEncodedImageFormat.Webp, preview ? 84 : 80);

                        input.Flush();
                        input.Close();

                        using var output = File.Create(savePath);
                        data.SaveTo(output);
                        output.Flush();
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to generate thumbnail - '{filePath}'");
                    if (File.Exists(savePath)) File.Delete(savePath);
                }
            }

            return false;
        }

        public MediaType GuessMediaTypeFromExtension(string path)
        {
            try
            {
                var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".tiff", ".tif", ".ico", ".heic", ".heif", ".avif" };
                var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".avi", ".wmv", ".flv", ".mkv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts" };

                var extension = $".{Path.GetExtension(path).Trim('.')}";
                if (imageExtensions.Contains(extension))
                {
                    return MediaType.Image;
                }
                else if (videoExtensions.Contains(extension))
                {
                    return MediaType.Video;
                }
            }
            catch { }

            return MediaType.Unknown;
        }

        public MediaType GetMediaType(string path)
        {
            if (UploadMediaValidator.IsHeif(path) || Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)) return MediaType.Image;
            try
            {
                var provider = new FileExtensionContentTypeProvider();
                if (provider.TryGetContentType(path, out string? contentType))
                {
                    if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        return MediaType.Image;
                    }
                    else if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        return MediaType.Video;
                    }
                }
            }
            catch { }
                
            return MediaType.Unknown;
        }

        public ImageOrientation GetOrientation(string path)
        {
            if (_fileHelper.FileExists(path))
            {
                try
                {
                    using var input = File.OpenRead(path);
                    using var codec = SKCodec.Create(input) ?? throw new InvalidOperationException("Unsupported or invalid image.");

                    return GetOrientation(codec);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get image orientation - '{path}'");
                }
            }

            return ImageOrientation.Unknown;
        }

        public ImageOrientation GetOrientation(SKCodec codec)
        {
            var (width, height) = GetDimensions(codec);
            if (width > height)
            {
                return ImageOrientation.Landscape;
            }
            else if (width < height)
            {
                return ImageOrientation.Portrait;
            }
            else
            {
                return ImageOrientation.Square;
            }
        }

        public (int, int) GetDimensions(SKCodec codec)
        {
            var rotated = IsRotated(codec);

            var width = rotated ? codec.Info.Height : codec.Info.Width;
            var height = rotated ? codec.Info.Width : codec.Info.Height;

            return (width, height);
        }

        public bool IsRotated(SKCodec codec)
        {
            return codec.EncodedOrigin == SKEncodedOrigin.LeftTop ||
                codec.EncodedOrigin == SKEncodedOrigin.RightTop ||
                codec.EncodedOrigin == SKEncodedOrigin.LeftBottom ||
                codec.EncodedOrigin == SKEncodedOrigin.RightBottom;
        }

        public DateTime? GetExifCreationDateTaken(string path)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);

                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateOriginal) == true)
                {
                    return dateOriginal;
                }

                if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dateDigitized) == true)
                {
                    return dateDigitized;
                }

                var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                if (ifd0?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dateTime) == true)
                {
                    return dateTime;
                }

                var pngDir = directories.OfType<PngDirectory>().FirstOrDefault();
                if (pngDir?.TryGetDateTime(PngDirectory.TagLastModificationTime, out var pngDate) == true)
                {
                    return pngDate;
                }

                var xmpDir = directories.OfType<XmpDirectory>().FirstOrDefault();
                if (xmpDir?.XmpMeta != null)
                {
                    try
                    {
                        var xmpDateStr = xmpDir.XmpMeta.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:CreateDate");
                        if (!string.IsNullOrEmpty(xmpDateStr) && DateTime.TryParse(xmpDateStr, out var xmpDate))
                        {
                            return xmpDate;
                        }
                    } 
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get image EXIF creation datetime - '{path}'");
            }

            return null;
        }

        public async Task<bool> DownloadFFMPEG(string path)
        {
            try
            {
                if (!_fileHelper.DirectoryExists(path))
                {
                    _fileHelper.CreateDirectoryIfNotExists(path);
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path);
                }

                FFmpeg.SetExecutablesPath(path);
                FfmpegInstalled = true;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to download FFmpeg - '{path}'");
            }

            return false;
        }
    }
}
