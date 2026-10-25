namespace AutoPilot.Services
{
    /// <summary>How a file changed.</summary>
    public enum DiffFileStatus { Added, Modified, Deleted, Renamed }

    /// <summary>Kind of one rendered diff line.</summary>
    public enum DiffLineKind { Hunk, Context, Added, Removed, Meta }

    /// <summary>One diff line. <paramref name="Text"/> has no leading '+', '-' or ' ' marker (Hunk: the full @@ line).</summary>
    public sealed record DiffLine(DiffLineKind Kind, string Text);

    /// <summary>The diff of one file. <paramref name="Path"/> is the new path (the old path for deletions); <paramref name="OldPath"/> is set only for renames.</summary>
    public sealed record DiffFile(string Path, string? OldPath, DiffFileStatus Status, int Additions, int Deletions, bool IsBinary, IReadOnlyList<DiffLine> Lines);

    /// <summary>All changes of a conversation branch relative to the commit it branched from.</summary>
    public sealed record ConversationDiff(string Branch, string BaseCommit, IReadOnlyList<DiffFile> Files, bool Truncated)
    {
        /// <summary>Total added lines over all files.</summary>
        public int TotalAdditions => Files.Sum(f => f.Additions);

        /// <summary>Total removed lines over all files.</summary>
        public int TotalDeletions => Files.Sum(f => f.Deletions);
    }

    /// <summary>Parses the output of <c>git diff --no-color -M</c>.</summary>
    public static class UnifiedDiffParser
    {
        /// <summary>Parses a unified git diff into per-file entries. Empty input yields an empty list.</summary>
        public static IReadOnlyList<DiffFile> Parse(string diff)
        {
            var files = new List<DiffFile>();
            if (string.IsNullOrEmpty(diff))
            {
                return files;
            }

            FileBuilder? current = null;
            foreach (var rawLine in diff.Split('\n'))
            {
                var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    if (current is not null)
                    {
                        files.Add(current.Build());
                    }

                    current = new FileBuilder(line["diff --git ".Length..]);
                    continue;
                }

                current?.Add(line);
            }

            if (current is not null)
            {
                files.Add(current.Build());
            }

            return files;
        }

        private sealed class FileBuilder
        {
            private readonly List<DiffLine> _lines = [];
            private readonly string? _headerPath;
            private bool _inHunks;
            private bool _isNew;
            private bool _isDeleted;
            private bool _isBinary;
            private string? _renameFrom;
            private string? _renameTo;
            private string? _oldPath;
            private string? _newPath;
            private int _additions;
            private int _deletions;

            public FileBuilder(string header) => _headerPath = ParseHeaderPath(header);

            public void Add(string line)
            {
                if (_inHunks)
                {
                    if (line.StartsWith("@@", StringComparison.Ordinal))
                    {
                        _lines.Add(new DiffLine(DiffLineKind.Hunk, line));
                    }
                    else if (line.StartsWith('+'))
                    {
                        _additions++;
                        _lines.Add(new DiffLine(DiffLineKind.Added, line[1..]));
                    }
                    else if (line.StartsWith('-'))
                    {
                        _deletions++;
                        _lines.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
                    }
                    else if (line.StartsWith('\\'))
                    {
                        _lines.Add(new DiffLine(DiffLineKind.Meta, line));
                    }
                    else if (line.StartsWith(' '))
                    {
                        _lines.Add(new DiffLine(DiffLineKind.Context, line[1..]));
                    }
                    else if (line.Length == 0)
                    {
                        // Trailing empty element of the split, or a context line whose single space was stripped.
                        _lines.Add(new DiffLine(DiffLineKind.Context, ""));
                    }

                    return;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    _inHunks = true;
                    _lines.Add(new DiffLine(DiffLineKind.Hunk, line));
                }
                else if (line.StartsWith("new file mode", StringComparison.Ordinal))
                {
                    _isNew = true;
                }
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                {
                    _isDeleted = true;
                }
                else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    _renameFrom = Unquote(line["rename from ".Length..]);
                }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    _renameTo = Unquote(line["rename to ".Length..]);
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    _isBinary = true;
                }
                else if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    _oldPath = ParseMarkerPath(line["--- ".Length..], "a/");
                }
                else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    _newPath = ParseMarkerPath(line["+++ ".Length..], "b/");
                }
            }

            public DiffFile Build()
            {
                // Trailing empty split element is not a real context line.
                if (_inHunks && _lines.Count > 0 && _lines[^1] is { Kind: DiffLineKind.Context, Text.Length: 0 })
                {
                    _lines.RemoveAt(_lines.Count - 1);
                }

                var status = _renameFrom is not null || _renameTo is not null ? DiffFileStatus.Renamed
                    : _isNew ? DiffFileStatus.Added
                    : _isDeleted ? DiffFileStatus.Deleted
                    : DiffFileStatus.Modified;

                string? oldPath = null;
                string path;
                if (status == DiffFileStatus.Renamed)
                {
                    path = _renameTo ?? _newPath ?? _headerPath ?? "";
                    oldPath = _renameFrom ?? _oldPath;
                }
                else if (status == DiffFileStatus.Deleted)
                {
                    path = _oldPath ?? _headerPath ?? _newPath ?? "";
                }
                else
                {
                    path = _newPath ?? _oldPath ?? _headerPath ?? "";
                }

                return new DiffFile(path, oldPath, status, _additions, _deletions, _isBinary, _lines);
            }

            private static string? ParseMarkerPath(string value, string prefix)
            {
                value = value.TrimEnd('\t');
                if (value == "/dev/null")
                {
                    return null;
                }

                value = Unquote(value);
                return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
            }

            /// <summary>Best-effort path from "a/P b/P" (ambiguous with spaces; the ---/+++ and rename lines override it when present).</summary>
            private static string? ParseHeaderPath(string header)
            {
                if (header.StartsWith('"'))
                {
                    return null;
                }

                if (header.StartsWith("a/", StringComparison.Ordinal) && (header.Length - 5) % 2 == 0 && header.Length >= 5)
                {
                    var length = (header.Length - 5) / 2;
                    var left = header.Substring(2, length);
                    if (header.Substring(2 + length) == " b/" + left)
                    {
                        return left;
                    }
                }

                var separator = header.IndexOf(" b/", StringComparison.Ordinal);
                return separator >= 0 ? header[(separator + 3)..] : null;
            }

            private static string Unquote(string value)
            {
                value = value.TrimEnd('\t');
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                {
                    return value;
                }

                var inner = value[1..^1];
                var bytes = new List<byte>();
                for (var i = 0; i < inner.Length; i++)
                {
                    var c = inner[i];
                    if (c != '\\' || i + 1 >= inner.Length)
                    {
                        if (char.IsHighSurrogate(c) && i + 1 < inner.Length && char.IsLowSurrogate(inner[i + 1]))
                        {
                            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(inner.Substring(i, 2)));
                            i++;
                        }
                        else
                        {
                            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                        }

                        continue;
                    }

                    var next = inner[++i];
                    if (next is >= '0' and <= '7' && i + 2 < inner.Length && inner[i + 1] is >= '0' and <= '7' && inner[i + 2] is >= '0' and <= '7')
                    {
                        bytes.Add(Convert.ToByte(inner.Substring(i, 3), 8));
                        i += 2;
                        continue;
                    }

                    bytes.Add(next switch { 't' => (byte)'\t', 'n' => (byte)'\n', 'r' => (byte)'\r', _ => (byte)next });
                }

                return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            }
        }
    }
}
