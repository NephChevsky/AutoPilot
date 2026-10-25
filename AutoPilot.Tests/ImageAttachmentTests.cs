using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests
{
    public class ImageAttachmentTests
    {
        [Fact]
        public void ToAttachments_returns_null_without_images()
        {
            Assert.Null(AgentChatSession.ToAttachments(null));
            Assert.Null(AgentChatSession.ToAttachments([]));
        }

        [Fact]
        public void ToAttachments_maps_blob_fields()
        {
            var result = AgentChatSession.ToAttachments([new PromptImage("image/png", [1, 2, 3], "a.png"), new PromptImage("image/gif", [4], "b.gif")]);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            var blob = Assert.IsType<AttachmentBlob>(result[0]);
            Assert.Equal("AQID", blob.Data);
            Assert.Equal("image/png", blob.MimeType);
            Assert.Equal("a.png", blob.DisplayName);
            Assert.Equal("image/gif", Assert.IsType<AttachmentBlob>(result[1]).MimeType);
        }

        [Fact]
        public void Replay_populates_images_from_blob_attachments()
        {
            var transcript = new ChatTranscript();
            transcript.Replay(
            [
                new UserMessageEvent
                {
                    Data = new UserMessageData
                    {
                        Content = "look",
                        Attachments = [new AttachmentBlob { Data = "AQID", MimeType = "image/png", DisplayName = "a.png" }],
                    },
                },
            ]);

            var entry = Assert.Single(transcript.GetEntries());
            var image = Assert.Single(entry.Images);
            Assert.Equal([1, 2, 3], image.Data);
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal("a.png", image.DisplayName);
            Assert.False(image.IsPlaceholder);
        }

        [Fact]
        public void Replay_uses_placeholder_when_data_is_missing_or_invalid()
        {
            var transcript = new ChatTranscript();
            transcript.Replay(
            [
                new UserMessageEvent
                {
                    Data = new UserMessageData
                    {
                        Content = "look",
                        Attachments =
                        [
                            new AttachmentBlob { MimeType = "image/jpeg", DisplayName = "gone.jpg", AssetId = "sha256:abc" },
                            new AttachmentBlob { Data = "***", MimeType = "image/png", DisplayName = "bad.png" },
                        ],
                    },
                },
            ]);

            var images = Assert.Single(transcript.GetEntries()).Images;
            Assert.Equal(2, images.Count);
            Assert.All(images, i => Assert.True(i.IsPlaceholder));
            Assert.Equal("gone.jpg", images[0].DisplayName);
        }

        [Fact]
        public void Replay_uses_placeholder_for_oversized_data_and_decodes_at_the_limit()
        {
            var atLimit = Convert.ToBase64String(new byte[PromptImage.MaxBytes]);
            var tooBig = Convert.ToBase64String(new byte[PromptImage.MaxBytes + 1]);

            var images = ChatTranscript.ReplayImages(
            [
                new AttachmentBlob { Data = atLimit, MimeType = "image/png", DisplayName = "ok.png" },
                new AttachmentBlob { Data = tooBig, MimeType = "image/png", DisplayName = "big.png" },
            ]);

            Assert.Equal(2, images.Count);
            Assert.False(images[0].IsPlaceholder);
            Assert.True(images[1].IsPlaceholder);
            Assert.Equal("big.png", images[1].DisplayName);
        }

        [Fact]
        public void Replay_ignores_non_image_blobs_and_missing_attachments()
        {
            var transcript = new ChatTranscript();
            transcript.Replay(
            [
                new UserMessageEvent { Data = new UserMessageData { Content = "plain" } },
                new UserMessageEvent
                {
                    Data = new UserMessageData
                    {
                        Content = "doc",
                        Attachments = [new AttachmentBlob { Data = "AQID", MimeType = "application/pdf", DisplayName = "a.pdf" }],
                    },
                },
            ]);

            Assert.All(transcript.GetEntries(), e => Assert.Empty(e.Images));
        }
    }
}
