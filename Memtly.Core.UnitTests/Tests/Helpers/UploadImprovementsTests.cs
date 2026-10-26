using System.Diagnostics;
using Memtly.Core.Helpers;
using Memtly.Core.Models;
using Memtly.Core.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class UploadImprovementsTests
{
    private string directory = null!;
    [SetUp] public void Setup() => directory = Directory.CreateTempSubdirectory("memtly-upload-tests-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(directory, true);

    [TestCase("", true)]
    [TestCase("  Julia & Patrick  ", true)]
    [TestCase("<script>alert(1)</script>", false)]
    [TestCase("A\nB", false)]
    [TestCase("A\u202eB", false)]
    public void NamesAllowAnonymousAndPlainTextOnly(string name, bool valid)
    {
        Assert.That(UploaderIdentity.TryNormalize(name, out var normalized), Is.EqualTo(valid));
        if (name == "") Assert.That(normalized, Is.EqualTo("Anonymous"));
        Assert.That(UploaderIdentity.TryNormalize(new string('a', 81), out _), Is.False);
    }

    [Test]
    public void ChunkIdentityIsFrozenAndScopedToSessionAndGallery()
    {
        var context = new DefaultHttpContext { Session = new MockSession() };
        var request = new MediaUploadRequest { RequestId = "batch", UploadId = Guid.NewGuid().ToString(), GalleryId = 2,
            CollectionId = 0, File = new FormFile(Stream.Null, 0, 10, "File", "photo.heic"), FileSize = 20,
            FileChecksum = "checksum", ChunkIndex = 0, TotalChunks = 2 };
        var prefix = UploadSnapshot.Prefix(context, request);
        var original = UploadSnapshot.Capture(directory, prefix, request, "First name", "");
        request.ChunkIndex = 1;
        Assert.That(UploadSnapshot.Capture(directory, prefix, request, "Changed in another tab", "").Name, Is.EqualTo(original.Name));
        request.TotalChunks = 3;
        Assert.Throws<InvalidDataException>(() => UploadSnapshot.Capture(directory, prefix, request, "", ""));
        request.GalleryId = 3;
        Assert.That(UploadSnapshot.Prefix(context, request), Is.Not.EqualTo(prefix));
        request.GalleryId = 2;
        context.Session = new MockSession();
        Assert.That(UploadSnapshot.Prefix(context, request), Is.Not.EqualTo(prefix));
    }

    [TestCase(SKEncodedImageFormat.Jpeg, ".jpg")]
    [TestCase(SKEncodedImageFormat.Png, ".png")]
    [TestCase(SKEncodedImageFormat.Webp, ".webp")]
    public void ImageContentMustMatchTheExtensionAndOriginalBytesStayUnchanged(SKEncodedImageFormat format, string extension)
    {
        using var surface = SKSurface.Create(new SKImageInfo(30, 20));
        surface.Canvas.Clear(SKColors.Green);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 85);
        var path = Path.Combine(directory, "photo" + extension);
        File.WriteAllBytes(path, data.ToArray());
        Assert.That(UploadMediaValidator.IsValid(path), Is.True);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(data.ToArray()));
        var disguised = Path.Combine(directory, "disguised.heic");
        File.Copy(path, disguised);
        Assert.That(UploadMediaValidator.IsValid(disguised), Is.False);
        File.WriteAllText(path, "damaged image");
        Assert.That(UploadMediaValidator.IsValid(path), Is.False);
    }

    [Test]
    public void HeicContainerCannotMasqueradeAsVideo()
    {
        var header = new byte[] { 0, 0, 0, 20 }.Concat(System.Text.Encoding.ASCII.GetBytes("ftypheic\0\0\0\0heic")).ToArray();
        foreach (var extension in new[] { ".mp4", ".mov" })
        {
            var path = Path.Combine(directory, "disguised" + extension);
            File.WriteAllBytes(path, header);
            Assert.That(UploadMediaValidator.IsValid(path), Is.False);
        }
    }

    [TestCase(80, 120, 80, 120)]
    [TestCase(1200, 2400, 960, 1920)]
    public async Task LinuxHeicAndHeifDecodeToBrowserPreviewAndKeepOriginal(int width, int height, int previewWidth, int previewHeight)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Native HEIC decoder runs in Linux AMD64 CI and Docker.");
        var original = Path.Combine(directory, "photo.heic");
        // Encode the fixture before applying the production decoder-only security policy.
        using var process = Process.Start(new ProcessStartInfo("convert") { ArgumentList = { "-size", $"{width}x{height}", "xc:green", original }, UseShellExecute = false });
        await process!.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero);
        var bytes = File.ReadAllBytes(original);
        var files = Substitute.For<IFileHelper>();
        files.FileExists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        var images = new ImageHelper(files, Substitute.For<ILogger<ImageHelper>>(),
            Substitute.For<IStringLocalizer<Localization.Translations>>());
        foreach (var extension in new[] { ".heic", ".heif" })
        {
            var path = Path.Combine(directory, "input" + extension);
            File.Copy(original, path);
            Assert.That(UploadMediaValidator.IsValid(path), Is.True);
            var preview = path + ".webp";
            Assert.That(await HeifThumbnail.Generate(path, preview, 720), Is.True, $"Native preview for {extension}");
            Assert.That(UploadMediaValidator.IsValid(preview), Is.True);
            using var codec = SKCodec.Create(preview);
            Assert.That(codec.Info.Width, Is.LessThan(codec.Info.Height));
            var screenPreview = path + ".preview.webp";
            Assert.That(await images.GeneratePreview(path, screenPreview), Is.True);
            using var previewCodec = SKCodec.Create(screenPreview);
            Assert.That(previewCodec.EncodedFormat, Is.EqualTo(SKEncodedImageFormat.Webp));
            Assert.That(previewCodec.Info.Width, Is.EqualTo(previewWidth));
            Assert.That(previewCodec.Info.Height, Is.EqualTo(previewHeight));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }
        File.WriteAllBytes(original, bytes.Take(30).ToArray());
        Assert.That(await HeifThumbnail.Generate(original, original + ".webp", 720), Is.False);
    }
}
