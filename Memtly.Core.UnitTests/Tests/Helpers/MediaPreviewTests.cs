using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Models.Database;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class MediaPreviewTests
{
    private string directory = null!;
    private ImageHelper images = null!;

    [SetUp]
    public void Setup()
    {
        directory = Directory.CreateTempSubdirectory("memtly-preview-tests-").FullName;
        var files = Substitute.For<IFileHelper>();
        files.FileExists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        images = new ImageHelper(files, Substitute.For<ILogger<ImageHelper>>(),
            Substitute.For<IStringLocalizer<Localization.Translations>>());
    }

    [TearDown] public void Cleanup() => Directory.Delete(directory, true);

    private string Image(int width, int height, SKEncodedImageFormat format, string extension)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Green);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        var path = Path.Combine(directory, "source" + extension);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [TestCase(SKEncodedImageFormat.Jpeg, ".jpg", 2400, 1200, 1920, 960)]
    [TestCase(SKEncodedImageFormat.Png, ".png", 1200, 2400, 960, 1920)]
    [TestCase(SKEncodedImageFormat.Webp, ".webp", 2400, 2400, 1920, 1920)]
    [TestCase(SKEncodedImageFormat.Jpeg, ".jpg", 80, 120, 80, 120)]
    [TestCase(SKEncodedImageFormat.Png, ".png", 1, 5000, 1, 1920)]
    public async Task PreviewIsBoundedWebpWithoutUpscalingOrChangingOriginal(SKEncodedImageFormat format,
        string extension, int width, int height, int expectedWidth, int expectedHeight)
    {
        var input = Image(width, height, format, extension);
        var bytes = File.ReadAllBytes(input);
        var output = Path.Combine(directory, "preview.webp");
        Assert.That(await images.GeneratePreview(input, output), Is.True);
        using var codec = SKCodec.Create(output);
        Assert.Multiple(() =>
        {
            Assert.That(codec.EncodedFormat, Is.EqualTo(SKEncodedImageFormat.Webp));
            Assert.That(codec.Info.Width, Is.EqualTo(expectedWidth));
            Assert.That(codec.Info.Height, Is.EqualTo(expectedHeight));
            Assert.That(File.ReadAllBytes(input), Is.EqualTo(bytes));
        });
    }

    [TestCase(1, 0)]
    [TestCase(2, 1)]
    [TestCase(3, 3)]
    [TestCase(4, 2)]
    [TestCase(5, 0)]
    [TestCase(6, 2)]
    [TestCase(7, 3)]
    [TestCase(8, 1)]
    public async Task PreviewAppliesAllExifRotationsAndReflections(int orientation, int topLeftColor)
    {
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        using var surface = SKSurface.Create(new SKImageInfo(80, 40));
        using var paint = new SKPaint();
        for (var i = 0; i < colors.Length; i++)
        {
            paint.Color = colors[i];
            surface.Canvas.DrawRect(i % 2 * 40, i / 2 * 20, 40, 20, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        // Minimal TIFF/EXIF IFD containing the orientation tag.
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
            73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0,
            (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        var input = Path.Combine(directory, "oriented.jpg");
        File.WriteAllBytes(input, jpeg.Take(2).Concat(exif).Concat(jpeg.Skip(2)).ToArray());
        var original = File.ReadAllBytes(input);
        var output = Path.Combine(directory, "preview.webp");
        Assert.That(await images.GeneratePreview(input, output), Is.True);
        using var bitmap = SKBitmap.Decode(output);
        Assert.That(bitmap.Width, Is.EqualTo(orientation >= 5 ? 40 : 80));
        Assert.That(bitmap.Height, Is.EqualTo(orientation >= 5 ? 80 : 40));
        var actual = bitmap.GetPixel(10, 10);
        var expected = colors[topLeftColor];
        Assert.That(actual.Red, Is.EqualTo(expected.Red).Within(30));
        Assert.That(actual.Green, Is.EqualTo(expected.Green).Within(30));
        Assert.That(actual.Blue, Is.EqualTo(expected.Blue).Within(30));
        Assert.That(File.ReadAllBytes(input), Is.EqualTo(original));
    }

    [Test]
    public async Task PreviewRejectsCorruptAndOversizedImagesAndVideo()
    {
        var output = Path.Combine(directory, "preview.webp");
        var input = Image(2, 2, SKEncodedImageFormat.Png, ".png");
        var png = File.ReadAllBytes(input);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), 100_000);
        File.WriteAllBytes(input, png);
        Assert.That(await images.GeneratePreview(input, output), Is.False);
        File.WriteAllText(input, "broken");
        Assert.That(await images.GeneratePreview(input, output), Is.False);
        var video = Path.Combine(directory, "video.mp4");
        File.WriteAllText(video, "video");
        Assert.That(await images.GeneratePreview(video, output), Is.False);
        Assert.That(File.Exists(output), Is.False);
    }

    [Test]
    public async Task CacheSeparatesVariantsReusesFilesAndInvalidatesSourceAndSizeChanges()
    {
        var gallery = new GalleryModel { Id = 1, Identifier = new string('a', 32) };
        var item = new GalleryItemModel { Id = 1, GalleryId = 1, Title = "photo.jpg", State = GalleryItemState.Approved, MediaType = MediaType.Image };
        var uploads = Directory.CreateDirectory(Path.Combine(directory, "uploads", gallery.Identifier)).FullName;
        var input = Path.Combine(uploads, item.Title);
        await File.WriteAllTextAsync(input, "original");
        var files = new MediaFileStore(directory);
        var settings = Substitute.For<ISettingsHelper>();
        var size = 1200;
        settings.GetOrDefault(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>()).Returns(_ => size);
        var generator = Substitute.For<IImageHelper>();
        generator.GeneratePreview(Arg.Any<string>(), Arg.Any<string>()).Returns(async call =>
        {
            await Task.Delay(25);
            await File.WriteAllTextAsync(call.ArgAt<string>(1), "preview-" + await File.ReadAllTextAsync(call.ArgAt<string>(0)));
            return true;
        });
        generator.GenerateThumbnail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(async call =>
        {
            await File.WriteAllTextAsync(call.ArgAt<string>(1), "thumbnail");
            return true;
        });
        MediaThumbnailHelper Helper() => new(files, generator, settings);
        var previews = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Helper().GetPreviewPath(gallery, item)));
        Assert.That(previews.All(p => p != null && p == previews[0]), Is.True);
        await generator.Received(1).GeneratePreview(Arg.Any<string>(), Arg.Any<string>());
        var thumbnail = await Helper().GetPath(gallery, item);
        Assert.That(thumbnail, Is.Not.EqualTo(previews[0]));
        Assert.That(await Helper().GetPath(gallery, item), Is.EqualTo(thumbnail));
        await generator.Received(1).GenerateThumbnail(Arg.Any<string>(), Arg.Any<string>(), 1200);
        size = 1600;
        Assert.That(await Helper().GetPath(gallery, item), Is.Not.EqualTo(thumbnail));
        Assert.That(await Helper().GetPreviewPath(gallery, item), Is.EqualTo(previews[0]));
        await File.WriteAllTextAsync(input, "modified"); // Same length; timestamp alone must invalidate.
        File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddSeconds(5));
        var changed = await Helper().GetPreviewPath(gallery, item);
        Assert.That(changed, Is.Not.Null.And.Not.EqualTo(previews[0]));
        Assert.That(await File.ReadAllTextAsync(changed!), Is.EqualTo("preview-modified"));
        File.Delete(input);
        Assert.That(await Helper().GetPreviewPath(gallery, item), Is.Null);
    }

    [Test]
    public async Task SourceChangedDuringGenerationIsNotPublishedAndFailureCanBeRetried()
    {
        var gallery = new GalleryModel { Id = 1, Identifier = "gallery" };
        var item = new GalleryItemModel { Id = 1, GalleryId = 1, Title = "photo.jpg", State = GalleryItemState.Approved, MediaType = MediaType.Image };
        var input = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "uploads", gallery.Identifier)).FullName, item.Title);
        await File.WriteAllTextAsync(input, "original");
        var generator = Substitute.For<IImageHelper>();
        var attempt = 0;
        generator.GeneratePreview(Arg.Any<string>(), Arg.Any<string>()).Returns(async call =>
        {
            await File.WriteAllTextAsync(call.ArgAt<string>(1), "preview");
            attempt++;
            if (attempt == 1) await File.WriteAllTextAsync(input, "replaced original");
            return attempt != 2;
        });
        var helper = new MediaThumbnailHelper(new MediaFileStore(directory), generator, Substitute.For<ISettingsHelper>());
        Assert.That(await helper.GetPreviewPath(gallery, item), Is.Null);
        Assert.That(await helper.GetPreviewPath(gallery, item), Is.Null);
        Assert.That(Directory.GetFiles(Path.Combine(directory, "thumbnails"), "*.webp", SearchOption.AllDirectories), Is.Empty);
        Assert.That(await helper.GetPreviewPath(gallery, item), Is.Not.Null);
    }
}
