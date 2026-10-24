using Memtly.Core.Constants;
using Memtly.Core.Enums;

namespace Memtly.Core.UnitTests.Tests.Helpers;

public class ThumbnailStyleTests
{
    [Test]
    public void MasonryIsSelectableWithoutChangingPersistedExistingStyleValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)ThumbnailStyle.Tile, Is.EqualTo(1));
            Assert.That((int)ThumbnailStyle.Letterbox, Is.EqualTo(2));
            Assert.That((int)ThumbnailStyle.Single, Is.EqualTo(3));
            Assert.That(ViewOptions.ThumbnailStyleOption["Masonry"], Is.EqualTo("4"));
            Assert.That(Enum.Parse<ThumbnailStyle>(ViewOptions.ThumbnailStyleOption["Masonry"]),
                Is.EqualTo(ThumbnailStyle.Masonry));
        });
    }
}
