using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class PromptImageTests
    {
        [Theory]
        [InlineData("image/png")]
        [InlineData("image/jpeg")]
        [InlineData("image/gif")]
        [InlineData("image/webp")]
        [InlineData("IMAGE/PNG")]
        public void Supported_mime_types_are_accepted(string mime) => Assert.True(PromptImage.IsSupportedMimeType(mime));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("image/svg+xml")]
        [InlineData("image/bmp")]
        [InlineData("text/plain")]
        public void Other_mime_types_are_rejected(string? mime) => Assert.False(PromptImage.IsSupportedMimeType(mime));

        [Fact]
        public void Validate_accepts_valid_image() => Assert.Null(PromptImage.Validate("image/png", 10, 0));

        [Fact]
        public void Validate_accepts_boundaries()
        {
            Assert.Null(PromptImage.Validate("image/png", PromptImage.MaxBytes, PromptImage.MaxCount - 1));
        }

        [Fact]
        public void Validate_rejects_oversized_image() =>
            Assert.NotNull(PromptImage.Validate("image/png", PromptImage.MaxBytes + 1, 0));

        [Fact]
        public void Validate_rejects_empty_image() => Assert.NotNull(PromptImage.Validate("image/png", 0, 0));

        [Fact]
        public void Validate_rejects_too_many_images() =>
            Assert.NotNull(PromptImage.Validate("image/png", 10, PromptImage.MaxCount));

        [Fact]
        public void Validate_rejects_unsupported_type() => Assert.NotNull(PromptImage.Validate("image/bmp", 10, 0));

        [Fact]
        public void ToDataUrl_encodes_base64()
        {
            var image = new PromptImage("image/png", [1, 2, 3], "a.png");
            Assert.Equal("data:image/png;base64,AQID", image.ToDataUrl());
        }

        [Fact]
        public void ToDataUrl_is_cached_and_returns_the_same_instance()
        {
            var image = new PromptImage("image/png", [1, 2, 3], "a.png");
            Assert.Same(image.ToDataUrl(), image.ToDataUrl());
        }

        [Fact]
        public void Equality_ignores_the_cached_data_url()
        {
            byte[] data = [1, 2, 3];
            var a = new PromptImage("image/png", data, "a.png");
            var b = new PromptImage("image/png", data, "a.png");
            a.ToDataUrl();

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, new PromptImage("image/png", [1, 2, 3], "a.png"));
        }

        [Fact]
        public void Empty_data_is_a_placeholder()
        {
            Assert.True(new PromptImage("image/png", [], "a.png").IsPlaceholder);
            Assert.False(new PromptImage("image/png", [1], "a.png").IsPlaceholder);
        }
    }
}
