namespace AutoPilot.Services
{
    /// <summary>
    /// An image attached to a prompt. A replayed attachment whose bytes are not available has empty
    /// <see cref="Data"/>; the UI shows a placeholder label with <see cref="DisplayName"/> for it.
    /// </summary>
    public sealed record PromptImage(string MimeType, byte[] Data, string DisplayName)
    {
        /// <summary>Largest accepted size of one image, in bytes.</summary>
        public const long MaxBytes = 5 * 1024 * 1024;

        /// <summary>Largest accepted number of images per message.</summary>
        public const int MaxCount = 5;

        /// <summary>The accepted image MIME types.</summary>
        public static IReadOnlyList<string> SupportedMimeTypes { get; } = ["image/png", "image/jpeg", "image/gif", "image/webp"];

        /// <summary>True when <paramref name="mimeType"/> is an accepted image type (case-insensitive).</summary>
        public static bool IsSupportedMimeType(string? mimeType) =>
            !string.IsNullOrWhiteSpace(mimeType)
            && SupportedMimeTypes.Contains(mimeType.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>True when there are no bytes to display (a replayed attachment without data).</summary>
        public bool IsPlaceholder => Data.Length == 0;

        /// <summary>Returns an error message when an image cannot be attached, otherwise null.</summary>
        public static string? Validate(string? mimeType, long size, int existingCount)
        {
            if (existingCount >= MaxCount)
            {
                return $"You can attach at most {MaxCount} images per message.";
            }

            if (!IsSupportedMimeType(mimeType))
            {
                return "Only PNG, JPEG, GIF and WEBP images can be attached.";
            }

            if (size <= 0)
            {
                return "The pasted image is empty.";
            }

            if (size > MaxBytes)
            {
                return $"Images can be at most {MaxBytes / (1024 * 1024)} MB.";
            }

            return null;
        }

        private string? _dataUrl;

        /// <summary>
        /// The image as a <c>data:</c> URL for use in an <c>img</c> element. Computed once and cached,
        /// since it is requested on every render.
        /// </summary>
        public string ToDataUrl() =>
            _dataUrl ??= $"data:{MimeType};base64,{Convert.ToBase64String(Data)}";

        // Explicit equality (same semantics as the synthesized one: Data compares by reference) so the
        // cache field is not part of it.
        public bool Equals(PromptImage? other) =>
            other is not null
            && MimeType == other.MimeType
            && ReferenceEquals(Data, other.Data)
            && DisplayName == other.DisplayName;

        public override int GetHashCode() =>
            HashCode.Combine(MimeType, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Data), DisplayName);
    }
}
