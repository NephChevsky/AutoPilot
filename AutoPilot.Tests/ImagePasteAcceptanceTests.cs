using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests;

public class ImagePasteAcceptanceTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 250, 251, 252];

    private static UserMessageEvent UserWith(string text, params Attachment[] atts) =>
        new() { Data = new UserMessageData { Content = text, Attachments = atts } };

    private static ChatEntry ReplayOne(UserMessageEvent e)
    {
        var t = new ChatTranscript { OrchestratorName = "Boss" };
        t.Apply(e, replay: true);
        return Assert.Single(t.GetEntries());
    }

    // AC-4
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/PNG")]
    [InlineData("Image/Jpeg")]
    public void SupportedMime_Accepted(string mime)
    {
        Assert.True(PromptImage.IsSupportedMimeType(mime));
        Assert.Null(PromptImage.Validate(mime, 100, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("image/svg+xml")]
    [InlineData("image/bmp")]
    [InlineData("text/plain")]
    [InlineData("application/pdf")]
    public void UnsupportedMime_Rejected(string? mime)
    {
        Assert.False(PromptImage.IsSupportedMimeType(mime));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate(mime, 100, 0)));
    }

    [Fact]
    public void Constants_AndSupportedList()
    {
        Assert.Equal(5L * 1024 * 1024, PromptImage.MaxBytes);
        Assert.Equal(5, PromptImage.MaxCount);
        Assert.Equal(
            new[] { "image/gif", "image/jpeg", "image/png", "image/webp" },
            PromptImage.SupportedMimeTypes.Select(s => s.ToLowerInvariant()).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Validate_SizeBoundaries()
    {
        Assert.Null(PromptImage.Validate("image/png", 1, 0));
        Assert.Null(PromptImage.Validate("image/png", PromptImage.MaxBytes, 0));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate("image/png", PromptImage.MaxBytes + 1, 0)));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate("image/png", 0, 0)));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate("image/png", -1, 0)));
    }

    [Fact]
    public void Validate_CountBoundaries()
    {
        Assert.Null(PromptImage.Validate("image/png", 10, PromptImage.MaxCount - 1));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate("image/png", 10, PromptImage.MaxCount)));
        Assert.False(string.IsNullOrWhiteSpace(PromptImage.Validate("image/png", 10, PromptImage.MaxCount + 3)));
    }

    // PromptImage basics (AC-5/6 support)
    [Fact]
    public void ToDataUrl_Format()
    {
        var img = new PromptImage("image/png", Bytes, "a.png");
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Bytes), img.ToDataUrl());
    }

    [Fact]
    public void IsPlaceholder_OnlyWhenDataEmpty()
    {
        Assert.True(new PromptImage("image/png", [], "x").IsPlaceholder);
        Assert.False(new PromptImage("image/png", Bytes, "x").IsPlaceholder);
    }

    [Fact]
    public void ChatEntry_ImagesDefaultsEmpty()
    {
        var e = ReplayOne(UserWith("hi"));
        Assert.NotNull(e.Images);
        Assert.Empty(e.Images);
    }

    // AC-3
    [Fact]
    public void ToAttachments_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AgentChatSession.ToAttachments(null));
        Assert.Null(AgentChatSession.ToAttachments([]));
    }

    [Fact]
    public void ToAttachments_MapsEachImageToBlob_InOrder()
    {
        var imgs = new[]
        {
            new PromptImage("image/png", Bytes, "one.png"),
            new PromptImage("image/jpeg", [9, 8, 7], "two.jpg"),
        };
        var list = AgentChatSession.ToAttachments(imgs)!;
        Assert.Equal(2, list.Count);
        var a = Assert.IsType<AttachmentBlob>(list[0]);
        var b = Assert.IsType<AttachmentBlob>(list[1]);
        Assert.Equal(Convert.ToBase64String(Bytes), a.Data);
        Assert.Equal("image/png", a.MimeType);
        Assert.Equal("one.png", a.DisplayName);
        Assert.Equal(Convert.ToBase64String(new byte[] { 9, 8, 7 }), b.Data);
        Assert.Equal("image/jpeg", b.MimeType);
        Assert.Equal("two.jpg", b.DisplayName);
    }

    // AC-6
    [Fact]
    public void Replay_ImageBlobWithData_YieldsDecodedImage()
    {
        var e = ReplayOne(UserWith("look", new AttachmentBlob
        {
            Data = Convert.ToBase64String(Bytes), MimeType = "image/png", DisplayName = "s.png"
        }));
        Assert.Equal(ChatEntryKind.User, e.Kind);
        Assert.Equal("look", e.Text);
        var img = Assert.Single(e.Images);
        Assert.Equal(Bytes, img.Data);
        Assert.Equal("image/png", img.MimeType);
        Assert.Equal("s.png", img.DisplayName);
        Assert.False(img.IsPlaceholder);
    }

    [Fact]
    public void Replay_InvalidBase64_YieldsPlaceholderWithName()
    {
        var e = ReplayOne(UserWith("x", new AttachmentBlob
        {
            Data = "!!!not base64!!!", MimeType = "image/jpeg", DisplayName = "bad.jpg"
        }));
        var img = Assert.Single(e.Images);
        Assert.True(img.IsPlaceholder);
        Assert.Equal("bad.jpg", img.DisplayName);
    }

    [Fact]
    public void Replay_MissingData_YieldsPlaceholderWithName()
    {
        var e = ReplayOne(UserWith("x", new AttachmentBlob
        {
            MimeType = "image/webp", DisplayName = "gone.webp"
        }));
        var img = Assert.Single(e.Images);
        Assert.True(img.IsPlaceholder);
        Assert.Equal("gone.webp", img.DisplayName);
    }

    [Fact]
    public void Replay_NonImageBlob_Skipped()
    {
        var e = ReplayOne(UserWith("x", new AttachmentBlob
        {
            Data = Convert.ToBase64String(Bytes), MimeType = "application/pdf", DisplayName = "d.pdf"
        }));
        Assert.Empty(e.Images);
    }

    [Fact]
    public void Replay_MixedAttachments_KeepsOnlyImagesInOrder()
    {
        var e = ReplayOne(UserWith("x",
            new AttachmentBlob { Data = Convert.ToBase64String(Bytes), MimeType = "image/gif", DisplayName = "1.gif" },
            new AttachmentBlob { Data = "AAAA", MimeType = "text/plain", DisplayName = "t.txt" },
            new AttachmentBlob { MimeType = "image/png", DisplayName = "2.png" }));
        Assert.Equal(2, e.Images.Count);
        Assert.Equal("1.gif", e.Images[0].DisplayName);
        Assert.False(e.Images[0].IsPlaceholder);
        Assert.Equal("2.png", e.Images[1].DisplayName);
        Assert.True(e.Images[1].IsPlaceholder);
    }

    [Fact]
    public void Replay_NullAttachments_NoImages()
    {
        var e = ReplayOne(new UserMessageEvent { Data = new UserMessageData { Content = "plain" } });
        Assert.Empty(e.Images);
        Assert.Equal("plain", e.Text);
    }

    [Fact]
    public void Replay_EmptyAttachmentList_NoImages()
    {
        var e = ReplayOne(UserWith("plain"));
        Assert.Empty(e.Images);
    }
}
