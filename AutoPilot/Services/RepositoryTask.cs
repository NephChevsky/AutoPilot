using System.Text.Json.Serialization;

namespace AutoPilot.Services
{
    /// <summary>A user-created task on a repository that can be sent to the agent later.</summary>
    public sealed class RepositoryTask
    {
        private string? _id;

        /// <summary>Generated (GUID "N" format) on first access when not set.</summary>
        public string Id
        {
            get => _id ??= Guid.NewGuid().ToString("N");
            set => _id = value;
        }

        // True when an id was set explicitly (e.g. read from the file); lets the store skip id-less entries.
        internal bool HasId => !string.IsNullOrWhiteSpace(_id);

        public string RepositoryPath { get; set; } = "";

        public string RepositoryName { get; set; } = "";

        public string Title { get; set; } = "";

        public string? Description { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? SentAt { get; set; }

        public string? SessionId { get; set; }

        /// <summary>Name of the agent that created the task; null for tasks created by the user.</summary>
        public string? CreatedBy { get; set; }

        /// <summary>Id of the conversation in which an agent created the task; null for user-created tasks.</summary>
        public string? SourceSessionId { get; set; }

        [JsonIgnore]
        public bool IsAgentCreated => !string.IsNullOrWhiteSpace(CreatedBy);

        [JsonIgnore]
        public bool IsSent => SentAt is not null;

        /// <summary>The prompt sent to the agent: the title, plus a blank line and the description when there is one.</summary>
        public string ToPrompt() =>
            string.IsNullOrWhiteSpace(Description) ? Title : Title + "\n\n" + Description;

        internal RepositoryTask Clone() => (RepositoryTask)MemberwiseClone();
    }
}
