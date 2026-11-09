using System.Security.Claims;
using Memtly.Core.Controllers;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Helpers.Notifications;
using Memtly.Core.Models.Database;
using Memtly.Core.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memtly.Core.UnitTests.Tests.Controllers;

public class ReviewPreviewTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ApprovalWaitsForPreviewForTheApprovedFile(bool bulk)
    {
        var gallery = new GalleryModel { Id = 1, Identifier = "review-preview", Owner = 10 };
        var item = new GalleryItemModel { Id = 42, GalleryId = 1, Title = "photo.jpg",
            State = GalleryItemState.Pending, MediaType = MediaType.Image };
        var database = Substitute.For<IDatabaseHelper>();
        database.GetGallery(1).Returns(gallery);
        database.GetGalleryItem(42).Returns(item);
        database.GetGalleryItems(Arg.Any<GalleryItemSearch>()).Returns(new List<GalleryItemModel> { item });
        var files = Substitute.For<IFileHelper>();
        files.MoveFileIfExists(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var thumbnails = Substitute.For<IMediaThumbnailHelper>();
        var prepared = false;
        thumbnails.PreparePreview(gallery, item).Returns(async _ =>
        {
            Assert.That(item.State, Is.EqualTo(GalleryItemState.Approved));
            await database.Received(1).EditGalleryItem(item);
            files.Received(1).MoveFileIfExists(
                Arg.Is<string>(path => path.EndsWith(Path.Combine("Pending", item.Title))),
                Arg.Is<string>(path => path.EndsWith(Path.Combine(gallery.Identifier, item.Title))));
            await Task.Delay(25);
            prepared = true;
        });
        var localizer = Substitute.For<IStringLocalizer<Localization.Translations>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(Substitute.For<ISettingsHelper>(), database,
            Substitute.For<IDeviceDetector>(), files, Substitute.For<IImageHelper>(),
            Substitute.For<IEncryptionHelper>(), Substitute.For<INotificationHelper>(),
            Substitute.For<ISmtpClientWrapper>(), Substitute.For<Memtly.Core.Helpers.IUrlHelper>(),
            Substitute.For<IAuditHelper>(), new IdentityHelper(), NullLoggerFactory.Instance,
            localizer, thumbnails);
        controller.ControllerContext.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Sid, "1"), new Claim(ClaimTypes.Role, "Admin|None")], "Tests")) };

        var result = (JsonResult)(bulk ? await controller.BulkReview(ReviewAction.Approved, [42])
            : await controller.ReviewPhoto(42, ReviewAction.Approved));
        Assert.That(JsonResponseHelper.GetPropertyValue(result.Value, "success", false), Is.True);
        Assert.That(prepared, Is.True, "The approved preview must be prepared before completing moderation.");
    }
}
