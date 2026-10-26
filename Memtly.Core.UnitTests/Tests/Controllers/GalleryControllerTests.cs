using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Memtly.Core.Constants;
using Memtly.Core.Controllers;
using Memtly.Core.Enums;
using Memtly.Core.Helpers;
using Memtly.Core.Helpers.Database;
using Memtly.Core.Helpers.Notifications;
using Memtly.Core.Models;
using Memtly.Core.Models.Database;
using Memtly.Core.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute.ReturnsExtensions;

namespace Memtly.Core.UnitTests.Tests.Helpers
{
    public class GalleryControllerTests
    {
        private readonly List<string> uploadPaths = [];
        private static readonly byte[] TestPhoto = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jr1sAAAAASUVORK5CYII=");
        private static IFormFile Photo(string filename = "photo.png") => new FormFile(new MemoryStream(TestPhoto), 0, TestPhoto.Length, "File", filename);

        [TearDown]
        public void CleanUploads()
        {
            foreach (var path in uploadPaths) if (File.Exists(path)) File.Delete(path);
            uploadPaths.Clear();
        }
        private readonly ISettingsHelper _settings = Substitute.For<ISettingsHelper>();
        private readonly IDatabaseHelper _database = Substitute.For<IDatabaseHelper>();
        private readonly IFileHelper _file = Substitute.For<IFileHelper>();
        private readonly IDeviceDetector _deviceDetector = Substitute.For<IDeviceDetector>();
        private readonly IImageHelper _image = Substitute.For<IImageHelper>();
        private readonly INotificationHelper _notification = Substitute.For<INotificationHelper>();
        private readonly IEncryptionHelper _encryption = Substitute.For<IEncryptionHelper>();
        private readonly Memtly.Core.Helpers.IUrlHelper _url = Substitute.For<Memtly.Core.Helpers.IUrlHelper>();
        private readonly IIdentityHelper _identity = Substitute.For<IIdentityHelper>();
        private readonly ILogger<GalleryController> _logger = Substitute.For<ILogger<GalleryController>>();
        private readonly IStringLocalizer<Memtly.Localization.Translations> _localizer = Substitute.For<IStringLocalizer<Memtly.Localization.Translations>>();

        private GalleryController CreateController(IConfiguration? configuration = null)
        {
            var access = new MediaAccessHelper(_database, _identity, _settings, configuration);
            return new GalleryController(_settings, _database, _file, _deviceDetector, _image, _notification,
                _encryption, _url, _identity, _logger, _localizer, access,
                new GalleryDownloadHelper(_database, access, new MediaFileStore(), _identity, _settings));
        }
        public GalleryControllerTests()
        {
        }

        [SetUp]
        public void Setup()
        {
            _file.SanitizeFilename(Arg.Any<string>()).Returns(call => Path.GetFileName(call.Arg<string>()));
            _file.CreateDirectoryIfNotExists(Arg.Any<string>()).Returns(call => { Directory.CreateDirectory(call.Arg<string>()); return true; });
            _file.SaveFile(Arg.Any<IFormFile>(), Arg.Any<string>(), Arg.Any<FileMode>()).Returns(async call =>
            {
                var path = call.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                uploadPaths.Add(path);
                await using var output = File.Create(path);
                await call.ArgAt<IFormFile>(0).CopyToAsync(output);
            });
            _file.DeleteFileIfExists(Arg.Any<string>()).Returns(call => { if (File.Exists(call.Arg<string>())) File.Delete(call.Arg<string>()); return true; });
            _image.GenerateThumbnail(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns(true);
            var mockData = GetMockGalleryData();
            // Collections contain actual child galleries, never items owned by
            // the collection itself. Use an explicitly public child fixture.
            var collectionChild = new GalleryModel { Id = 5, Identifier = "collection-child", Name = "Collection child", Owner = 1, Type = GalleryType.Basic };
            mockData["collection-child"] = collectionChild;
            _database.GetGallery(5).Returns(collectionChild);
            _database.GetGalleriesByCollectionId(2).Returns(new List<GalleryModel> { collectionChild });

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(1);
            _identity.GetUserPermissions(Arg.Any<ClaimsPrincipal>()).Returns(call =>
                _identity.IsPrivilegedUser(call.Arg<ClaimsPrincipal>()) ? new AdminPermissions() : new BasicUserPermissions());

            _database.GetGallery(1).Returns(Task.FromResult<GalleryModel?>(mockData["default"]));
            _database.GetGallery(2).Returns(Task.FromResult<GalleryModel?>(mockData["blaa"]));
            _database.GetGallery(3).Returns(Task.FromResult<GalleryModel?>(mockData["drop_test"]));
            _database.GetGallery(4).Returns(Task.FromResult<GalleryModel?>(null));

            _database.GetGalleryId("default").Returns(Task.FromResult<int?>(mockData["default"].Id));
            _database.GetGalleryId("blaa").Returns(Task.FromResult<int?>(mockData["blaa"].Id));
            _database.GetGalleryId("drop_test").Returns(Task.FromResult<int?>(mockData["drop_test"].Id));
            _database.GetGalleryId("missing").Returns(Task.FromResult<int?>(null));

            _database.GetGalleryIdByName("default").Returns(Task.FromResult<int?>(mockData["default"].Id));
            _database.GetGalleryIdByName("blaa").Returns(Task.FromResult<int?>(mockData["blaa"].Id));
            _database.GetGalleryIdByName("drop_test").Returns(Task.FromResult<int?>(mockData["drop_test"].Id));
            _database.GetGalleryIdByName("missing").Returns(Task.FromResult<int?>(null));

            _database.GetGalleryIdentifier(Arg.Any<int>()).Returns(paramArr => {
                var id = paramArr.Arg<int>();
                var data = mockData.FirstOrDefault(x => x.Value.Id == id).Value;
                return data != null ? new GalleryIdentifierModel() { Id = data.Id, Identifier = data.Identifier, Name = data.Name } : null;
            });

            _database.AddGallery(Arg.Any<GalleryModel>()).Returns(Task.FromResult<GalleryModel?>(new GalleryModel()
            {
                Id = 101,
                Name = "missing",
                SecretKey = "123456",
                ApprovedItems = 0,
                PendingItems = 0,
                TotalItems = 0,
                Owner = 1
            }));
            _database.AddGalleryItem(Arg.Any<GalleryItemModel>()).Returns(Task.FromResult<GalleryItemModel?>(MockData.MockGalleryItem()));

            _database.GetGalleryItems(Arg.Any<GalleryItemSearch>()).Returns(paramArr =>
            {
                var search = paramArr.Arg<GalleryItemSearch>() ?? new GalleryItemSearch();

                var galleryId = search.GalleryIds?.FirstOrDefault() ?? 1;
                var gallery = mockData.FirstOrDefault(x => x.Value.Id == galleryId).Value;

                var mockApprovedItems = MockData.MockGalleryItems(5, gallery.Id, GalleryItemState.Approved, ".jpg");
                var mockPendingItems = MockData.MockGalleryItems(5, gallery.Id, GalleryItemState.Pending, ".jpg");

                var mockItems = new List<GalleryItemModel>();

                if (search.ItemState.Approved == ItemOwner.All)
                {
                    mockItems.AddRange(mockApprovedItems);
                }
                else if (search.ItemState.Approved == ItemOwner.UserOnly)
                {
                    mockItems.AddRange(mockApprovedItems.Where(x => x.UserId == search.UserId || gallery.Owner == search.UserId));
                }

                if (search.ItemState.Pending == ItemOwner.All)
                {
                    mockItems.AddRange(mockPendingItems);
                }
                else if (search.ItemState.Pending == ItemOwner.UserOnly)
                {
                    mockItems.AddRange(mockPendingItems.Where(x => x.UserId == search.UserId || gallery.Owner == search.UserId));
                }

                return Task.FromResult(mockItems);
            });

            _database.GetGalleryItemByChecksum(Arg.Any<int>(), Arg.Any<string>()).ReturnsNull();

            _settings.GetOrDefault(MemtlyConfiguration.Gallery.Upload, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.Download, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.UploadPeriod, Arg.Any<string>(), Arg.Any<int>()).Returns("1970-01-01 00:00:00");
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.PreventDuplicates, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.DefaultView, Arg.Any<int>(), Arg.Any<int>()).Returns((int)ViewMode.Default);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.AllowedFileTypes, Arg.Any<string>(), Arg.Any<int>()).Returns(".jpg,.jpeg,.png,.heic,.heif,.webp,.mp4,.mov");
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.RequireReview, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.MaxSizeMB, Arg.Any<long>(), Arg.Any<int>()).Returns(1024);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.MaxFileSizeMB, Arg.Any<long>(), Arg.Any<int>()).Returns(10);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ItemsPerPage, Arg.Any<int>(), Arg.Any<int>()).Returns(50);
            _settings.GetOrDefault(MemtlyConfiguration.Slideshow.IncludeVideoSlides, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            _file.GetChecksum(Arg.Any<string>()).Returns(Guid.NewGuid().ToString());
            // A real file has a valid timestamp. DateTime.MinValue from an unstubbed
            // mock underflows DateTimeOffset in time zones east of UTC.
            _file.GetCreationDatetime(Arg.Any<string>()).Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            _notification.Send(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));

            _localizer[Arg.Any<string>()].Returns(new LocalizedString("UnitTest", "UnitTest"));
        }

        [TestCase(DeviceType.Desktop, 1, "default", "default", "password", ViewMode.Default, GalleryGroup.None, GalleryOrder.Descending, true)]
        [TestCase(DeviceType.Mobile, 2, "blaa", "blaa", "456789", ViewMode.Presentation, GalleryGroup.DateUploaded, GalleryOrder.Ascending, true)]
        [TestCase(DeviceType.Mobile, 2, "blaa", "blaa", "456789", ViewMode.Presentation, GalleryGroup.DateTaken, GalleryOrder.Ascending, true)]
        [TestCase(DeviceType.Tablet, 101, "missing", "missing", "123456", ViewMode.Slideshow, GalleryGroup.Uploader, GalleryOrder.Ascending, false)]
        public async Task GalleryController_Index(DeviceType deviceType, int id, string? identifier, string? name, string? key, ViewMode? mode, GalleryGroup group, GalleryOrder order, bool existing)
        {
            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(deviceType);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.GuestGalleryCreation, Arg.Any<bool>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            if (existing)
            {
                ViewResult actual = (ViewResult)await controller.Index(identifier, key, mode, group, order);
                Assert.That(actual, Is.TypeOf<ViewResult>());
                Assert.That(actual?.Model, Is.Not.Null);

                PhotoGallery model = (PhotoGallery)actual.Model;
                Assert.That(model?.Gallery?.Id, Is.EqualTo(id));
                Assert.That(model?.Gallery?.Identifier, Is.EqualTo(identifier));
                Assert.That(model?.Gallery?.Name, Is.EqualTo(name));
                Assert.That(model?.SecretKey, Is.EqualTo(key));
                Assert.That(model.ViewMode, Is.EqualTo(mode));
            }
            else
            {
                RedirectToActionResult actual = (RedirectToActionResult)await controller.Index(identifier, key, mode, group, order);
                Assert.That(actual, Is.TypeOf<RedirectToActionResult>());
            }
        }

        [TestCase("default", "default")]
        [TestCase("Default", "default")]
        [TestCase(null, null)]
        [TestCase("blaa", "blaa")]
        public async Task GalleryController_Index_GetByIdentifier(string? identifier, string? expected)
        {
            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.GuestGalleryCreation, Arg.Any<bool>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            if (expected != null)
            {
                ViewResult actual = (ViewResult)await controller.Index(identifier, "password", ViewMode.Default, GalleryGroup.None, GalleryOrder.Random);
                Assert.That(actual, Is.TypeOf<ViewResult>());
                Assert.That(actual?.Model, Is.Not.Null);

                PhotoGallery model = (PhotoGallery)actual.Model;
                Assert.That(model?.Gallery?.Identifier, Is.EqualTo(expected));
            }
            else
            {
                RedirectToActionResult actual = (RedirectToActionResult)await controller.Index(identifier, "password", ViewMode.Default, GalleryGroup.None, GalleryOrder.Random);
                Assert.That(actual, Is.TypeOf<RedirectToActionResult>());
            }
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public async Task GalleryController_UploadDisabled(bool enabled, bool expected)
        {
            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.Upload, Arg.Any<bool>(), Arg.Any<int>()).Returns(enabled);

            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index("default", "password", ViewMode.Default, GalleryGroup.None, GalleryOrder.Descending);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.UploadActivated, Is.EqualTo(expected));
        }

        [TestCase("1970-01-01 00:00", true)]
        [TestCase("3000-01-01 00:00", false)]
        [TestCase("1970-01-01 00:00 / 1980-01-01 00:00", false)]
        [TestCase("2999-01-01 00:00 / 3000-01-01 00:00", false)]
        [TestCase("1970-01-01 00:00 / 3000-01-01 00:00", true)]
        public async Task GalleryController_UploadDisabled(string uploadPeriod, bool expected)
        {
            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.UploadPeriod, Arg.Any<string>(), Arg.Any<int>()).Returns(uploadPeriod);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index("default", "password", ViewMode.Default, GalleryGroup.None, GalleryOrder.Descending);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.UploadActivated, Is.EqualTo(expected));
        }

        [TestCase(DeviceType.Desktop, ViewMode.Default, GalleryGroup.None, GalleryOrder.Descending)]
        [TestCase(DeviceType.Mobile, ViewMode.Presentation, GalleryGroup.DateUploaded, GalleryOrder.Ascending)]
        [TestCase(DeviceType.Mobile, ViewMode.Presentation, GalleryGroup.DateTaken, GalleryOrder.Ascending)]
        [TestCase(DeviceType.Tablet, ViewMode.Slideshow, GalleryGroup.Uploader, GalleryOrder.Ascending)]
        public async Task GalleryController_Index_SingleGalleryMode(DeviceType deviceType, ViewMode? mode, GalleryGroup group, GalleryOrder order)
        {
            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(deviceType);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index("default", "password", mode, group, order);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(1));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo("default"));
            Assert.That(model?.Gallery?.Name, Is.EqualTo("default"));
            Assert.That(model?.SecretKey, Is.EqualTo("password"));
            Assert.That(model.ViewMode, Is.EqualTo(mode));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 1)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_BasicUsers_ShowPendingUploads_Disabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(2);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 5)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_PrivilegedUsers_ShowPendingUploads_Disabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(2);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 6)]
        [TestCase(GalleryType.Drop, 2)]
        [TestCase(GalleryType.Collection, 6)]
        public async Task GalleryController_Index_BasicUsers_ShowPendingUploads_Enabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(2);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 10)]
        [TestCase(GalleryType.Drop, 10)]
        [TestCase(GalleryType.Collection, 10)]
        public async Task GalleryController_Index_PrivilegedUsers_ShowPendingUploads_Enabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(2);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 5)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_GalleryOwner_ShowPendingUploads_Disabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(1);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(true);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 10)]
        [TestCase(GalleryType.Drop, 10)]
        [TestCase(GalleryType.Collection, 10)]
        public async Task GalleryController_Index_GalleryOwner_ShowPendingUploads_Enabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(1);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(true);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 0)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_NonGalleryOwner_ShowPendingUploads_Disabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(20);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 0)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_NonGalleryOwner_ShowPendingUploads_Enabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(20);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 0)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_AnonymousGuest_ShowPendingUploads_Disabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(-1);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(false);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(GalleryType.Basic, 5)]
        [TestCase(GalleryType.Drop, 0)]
        [TestCase(GalleryType.Collection, 5)]
        public async Task GalleryController_Index_AnonymousGuest_ShowPendingUploads_Enabled(GalleryType type, int expectedItemCount)
        {
            var mockGallery = GetMockGalleryData().FirstOrDefault(x => x.Value.Type == type).Value;
            mockGallery.Owner = 1;

            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(-1);
            _identity.IsValid(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsBasicUser(Arg.Any<ClaimsPrincipal>()).Returns(true);
            _identity.IsPrivilegedUser(Arg.Any<ClaimsPrincipal>()).Returns(false);
            _identity.IsOwner(Arg.Any<ClaimsPrincipal>(), Arg.Any<int?>()).Returns(false);

            _deviceDetector.ParseDeviceType(Arg.Any<string>()).Returns(DeviceType.Desktop);
            _settings.GetOrDefault(MemtlyConfiguration.Basic.SingleGalleryMode, Arg.Any<bool>()).Returns(true);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.ShowPendingUploads, Arg.Any<bool>(), Arg.Any<int>()).Returns(true);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            ViewResult actual = (ViewResult)await controller.Index(mockGallery.Identifier, mockGallery.SecretKey);
            Assert.That(actual, Is.TypeOf<ViewResult>());
            Assert.That(actual?.Model, Is.Not.Null);

            PhotoGallery model = (PhotoGallery)actual.Model;
            Assert.That(model?.Gallery?.Id, Is.EqualTo(mockGallery.Id));
            Assert.That(model?.Gallery?.Identifier, Is.EqualTo(mockGallery.Identifier));
            Assert.That(model?.Gallery?.Name, Is.EqualTo(mockGallery.Name));
            Assert.That(model?.Images, Is.Not.Null);
            Assert.That(model.Images.Count, Is.EqualTo(expectedItemCount));
        }

        [TestCase(true, null, false)]
        [TestCase(true, "Bob", false)]
        [TestCase(false, "", false)]
        [TestCase(false, "Unit Testing", false)]
        [TestCase(false, "Logged In", true)]
        public async Task GalleryController_UploadFileChunk(bool requiresReview, string? uploadedBy, bool loggedIn)
        {
            _database.GetUser(Arg.Any<int>()).Returns(loggedIn ? new UserModel() { Firstname = "Logged", Lastname = "In" } : null);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.RequireReview, Arg.Any<bool>()).Returns(requiresReview);
            _file.FileExists(Arg.Any<string>()).Returns(true);

            var session = new MockSession();
            session.Set(SessionKey.Viewer.Identity, uploadedBy ?? string.Empty);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = "password",
                File = Photo(),
                FileSize = TestPhoto.Length,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(string.IsNullOrWhiteSpace(JsonResponseHelper.GetPropertyValue(actual.Value, "RequestId", string.Empty)), Is.False);
            Assert.That(string.IsNullOrWhiteSpace(JsonResponseHelper.GetPropertyValue(actual.Value, "UploadId", string.Empty)), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.True);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.True);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.EqualTo(0));
        }

        [TestCase("Unsere Hochzeit", GalleryType.Basic, false, false)]
        [TestCase("Eure Hochzeitsfotos", GalleryType.Basic, false, true)]
        [TestCase("Eure Hochzeitsfotos", GalleryType.Basic, true, false)]
        [TestCase("Nur für Julia & Patrick", GalleryType.Basic, false, false)]
        [TestCase("Nur für Julia & Patrick", GalleryType.Drop, false, true)]
        public async Task WeddingUploadsEnforceDestinationAndReview(string name, GalleryType type, bool disabled, bool allowed)
        {
            _database.GetGallery(1).Returns(new GalleryModel { Id = 1, Identifier = "wedding", Name = name, Type = type, Owner = 2 });
            _database.GetUser(Arg.Any<int>()).Returns(new UserModel());
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.RequireReview, Arg.Any<bool>(), Arg.Any<int?>()).Returns(false);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.Upload, Arg.Any<bool>(), Arg.Any<int?>()).Returns(!disabled);
            _file.FileExists(Arg.Any<string>()).Returns(true);
            _database.ClearReceivedCalls();
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Wedding:Enabled"] = "true" }).Build();
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
            controller.HttpContext.RequestServices = services;
            var result = await controller.UploadFileChunk(new MediaUploadRequest { RequestId = Guid.NewGuid().ToString(), UploadId = Guid.NewGuid().ToString(), CollectionId = 0, ChunkIndex = 0, GalleryId = 1, File = Photo(), FileSize = TestPhoto.Length, FileChecksum = Guid.NewGuid().ToString(), TotalChunks = 1 });
            if (!allowed)
            {
                Assert.That(result, Is.TypeOf<ObjectResult>());
                Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(403));
                await _database.DidNotReceive().AddGalleryItem(Arg.Any<GalleryItemModel>());
            }
            else
            {
                Assert.That(result, Is.TypeOf<JsonResult>());
                Assert.That(JsonResponseHelper.GetPropertyValue(((JsonResult)result).Value, "Complete", false), Is.True);
                await _database.Received().AddGalleryItem(Arg.Is<GalleryItemModel>(item => name != "Eure Hochzeitsfotos" || item.State == GalleryItemState.Pending));
            }
        }

        [TestCase("photo.heic")]
        [TestCase("photo.HEIF")]
        public async Task HeicUploadReturnsActionableError(string filename)
        {
            _database.ClearReceivedCalls();
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();
            var result = (JsonResult)await controller.UploadFileChunk(new MediaUploadRequest { RequestId = Guid.NewGuid().ToString(), UploadId = Guid.NewGuid().ToString(), CollectionId = 0, ChunkIndex = 0, GalleryId = 1, SecretKey = "password", FileSize = TestPhoto.Length, FileChecksum = "test", File = Photo(filename), TotalChunks = 1 });
            Assert.That(JsonResponseHelper.GetPropertyValue(result.Value, "Success", true), Is.False);
            Assert.That(string.Join(" ", JsonResponseHelper.GetPropertyValue(result.Value, "Errors", new List<string>())), Does.Contain("JPEG"));
            await _database.DidNotReceive().AddGalleryItem(Arg.Any<GalleryItemModel>());
        }

        [Test]
        public async Task CentralWeddingGrantAllowsChunkedPrivateUploadWithFrozenNameButNeverReading()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Wedding:Enabled"] = "true" }).Build();
            var options = WeddingOptions.Read(configuration);
            var galleries = new List<GalleryModel> {
                new() { Id = 11, Name = options.ProfessionalGallery, SecretKey = "shared" },
                new() { Id = 12, Name = options.GuestGallery, SecretKey = "shared" },
                new() { Id = 13, Name = options.PrivateGallery, SecretKey = "shared", Type = GalleryType.Drop, Owner = 2 }
            };
            _database.GetGalleries().Returns(galleries);
            _database.GetGallery(13).Returns(galleries[2]);
            _database.GetUser(2).Returns(new UserModel());
            _identity.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(-1);
            _settings.GetOrDefault(MemtlyConfiguration.Gallery.Upload, Arg.Any<bool>(), Arg.Any<int?>()).Returns(true);
            _file.FileExists(Arg.Any<string>()).Returns(true);
            _file.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(call => Directory.GetFiles(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<SearchOption>(2)));
            _database.ClearReceivedCalls();
            var controller = CreateController(configuration);
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
            controller.HttpContext.RequestServices = services;
            var access = new MediaAccessHelper(_database, _identity, _settings, configuration);
            WeddingAccessHelper.Grant(controller.HttpContext, options, galleries, access);
            controller.Request.Headers.Cookie = controller.Response.Headers.SetCookie.Single(value => value!.StartsWith(".Memtly.Wedding.Session="))!.Split(';')[0];
            controller.HttpContext.Session.SetString(SessionKey.Viewer.Identity, "First & name");
            var request = new MediaUploadRequest { RequestId = Guid.NewGuid().ToString(), UploadId = Guid.NewGuid().ToString(),
                GalleryId = 13, CollectionId = 0, FileSize = TestPhoto.Length, FileChecksum = Guid.NewGuid().ToString(), TotalChunks = 2, ChunkIndex = 0,
                File = new FormFile(new MemoryStream(TestPhoto.Take(30).ToArray()), 0, 30, "File", "photo.png") };
            var first = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(JsonResponseHelper.GetPropertyValue(first.Value, "Success", false), Is.True);
            Assert.That(JsonResponseHelper.GetPropertyValue(first.Value, "Complete", true), Is.False);
            controller.HttpContext.Session.SetString(SessionKey.Viewer.Identity, "Changed name");
            request.ChunkIndex = 1;
            request.File = new FormFile(new MemoryStream(TestPhoto.Skip(30).ToArray()), 0, TestPhoto.Length - 30, "File", "photo.png");
            var second = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(JsonResponseHelper.GetPropertyValue(second.Value, "Complete", false), Is.True,
                System.Text.Json.JsonSerializer.Serialize(second.Value) + " " + string.Join("\n", _logger.ReceivedCalls().Select(call => string.Join(" ", call.GetArguments().Select(arg => arg?.ToString())))));
            await _database.Received(1).AddGalleryItem(Arg.Is<GalleryItemModel>(item => item.GalleryId == 13 && item.UploadedBy == "First & name"));
            Assert.That(await access.CanRead(controller.HttpContext, new GalleryItemModel { GalleryId = 13, State = GalleryItemState.Approved }), Is.False);
            var saved = _database.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "AddGalleryItem").GetArguments()[0] as GalleryItemModel;
            var root = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!;
            uploadPaths.Add(Path.Combine(root, "uploads", galleries[2].Identifier, "Pending", saved!.Title));
            uploadPaths.Add(Path.Combine(root, "uploads", galleries[2].Identifier, saved.Title));
        }

        [TestCase]
        public async Task GalleryController_UploadFileChunk_Duplicate()
        {
            _database.GetUser(Arg.Any<int>()).Returns(new UserModel());
            _database.GetGalleryItemByChecksum(Arg.Any<int>(), Arg.Any<string>()).Returns(Task.FromResult(MockData.MockGalleryItems(1, 1, GalleryItemState.Approved).FirstOrDefault()));

            var session = new MockSession();
            session.Set(SessionKey.Viewer.Identity, string.Empty);

            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = "password",
                File = new FormFile(null, 0, 0, "TestFile_001", $"{Guid.NewGuid()}.jpg"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase(100)]
        [TestCase(1000)]
        public async Task GalleryController_UploadFileChunk_InvalidGallery(int id)
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = id,
                SecretKey = "password",
                File = new FormFile(null, 0, 0, "TestFile_001", $"{Guid.NewGuid()}.jpg"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase(null)]
        [TestCase("")]
        public async Task GalleryController_UploadFileChunk_InvalidSecretKey(string? key)
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = key,
                File = new FormFile(null, 0, 0, "TestFile_001", $"{Guid.NewGuid()}.jpg"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase(100, 1)]
        [TestCase(1, 100)]
        [TestCase(100, 100)]
        public async Task GalleryController_UploadFileChunk_MissingGalleryOrCollection(int collectionId, int galleryId)
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = collectionId,
                GalleryId = galleryId,
                SecretKey = "password",
                File = new FormFile(null, 0, 0, "TestFile_001", $"{Guid.NewGuid()}.jpg"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase()]
        public async Task GalleryController_UploadFileChunk_NoFiles()
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = "password",
                File = null,
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase()]
        public async Task GalleryController_UploadFileChunk_FileTooBig()
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = "password",
                File = new FormFile(null, 0, int.MaxValue, "TestFile_001", $"{Guid.NewGuid()}.jpg"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        [TestCase()]
        public async Task GalleryController_UploadFileChunk_InvalidFileType()
        {
            var controller = CreateController();
            controller.ControllerContext.HttpContext = MockData.MockHttpContext();

            var request = new MediaUploadRequest()
            {
                RequestId = Guid.NewGuid().ToString(),
                UploadId = Guid.NewGuid().ToString(),
                CollectionId = 0,
                GalleryId = 1,
                SecretKey = "password",
                File = new FormFile(null, 0, 0, "TestFile_001", $"{Guid.NewGuid()}.blaa"),
                FileSize = 10,
                FileChecksum = Guid.NewGuid().ToString(),
                ChunkIndex = 0,
                TotalChunks = 1
            };

            JsonResult actual = (JsonResult)await controller.UploadFileChunk(request);
            Assert.That(actual, Is.TypeOf<JsonResult>());
            Assert.That(actual?.Value, Is.Not.Null);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Success", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Complete", false), Is.False);
            Assert.That(JsonResponseHelper.GetPropertyValue(actual.Value, "Errors", new List<string>()).Count, Is.GreaterThan(0));
        }

        private IDictionary<string, GalleryModel> GetMockGalleryData()
        {
            return new Dictionary<string, GalleryModel>()
            {
                {
                    "default", new GalleryModel()
                    {
                        Id = 1,
                        Identifier = "default",
                        Name = "default",
                        SecretKey = "password",
                        ApprovedItems = 32,
                        PendingItems = 50,
                        TotalItems = 72,
                        Owner = 1,
                        Type = GalleryType.Basic
                    }
                },
                {
                    "blaa", new GalleryModel()
                    {
                        Id = 2,
                        Identifier = "blaa",
                        Name = "blaa",
                        SecretKey = "456789",
                        ApprovedItems = 2,
                        PendingItems = 1,
                        TotalItems = 3,
                        Owner = 1,
                        Type = GalleryType.Collection
                    }
                },
                {
                    "drop_test", new GalleryModel()
                    {
                        Id = 3,
                        Identifier = "drop_test",
                        Name = "drop_test",
                        SecretKey = "123456",
                        ApprovedItems = 0,
                        PendingItems = 0,
                        TotalItems = 0,
                        Owner = 1,
                        Type = GalleryType.Drop
                    }
                },
                {
                    "missing", new GalleryModel()
                    {
                        Id = 101,
                        Identifier = "missing",
                        Name = "missing",
                        SecretKey = "123456",
                        ApprovedItems = 0,
                        PendingItems = 0,
                        TotalItems = 0,
                        Owner = 1,
                        Type = GalleryType.Basic
                    }
                }
            };
        }
    }
}
