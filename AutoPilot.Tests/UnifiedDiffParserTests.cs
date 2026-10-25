using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class UnifiedDiffParserTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("\n")]
        public void Parse_empty_input_returns_empty_list(string input) =>
            Assert.Empty(UnifiedDiffParser.Parse(input));

        [Fact]
        public void Parse_modified_file_with_multiple_hunks()
        {
            var diff = string.Join('\n',
                "diff --git a/src/a.txt b/src/a.txt",
                "index 111..222 100644",
                "--- a/src/a.txt",
                "+++ b/src/a.txt",
                "@@ -1,3 +1,3 @@",
                " one",
                "-two",
                "+TWO",
                " three",
                "@@ -10,2 +10,3 @@ void M()",
                " ten",
                "+eleven",
                " twelve",
                "");

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.Equal("src/a.txt", file.Path);
            Assert.Null(file.OldPath);
            Assert.Equal(DiffFileStatus.Modified, file.Status);
            Assert.Equal(2, file.Additions);
            Assert.Equal(1, file.Deletions);
            Assert.False(file.IsBinary);
            Assert.Equal(2, file.Lines.Count(l => l.Kind == DiffLineKind.Hunk));
            Assert.Equal(new DiffLine(DiffLineKind.Removed, "two"), file.Lines[2]);
            Assert.Equal(new DiffLine(DiffLineKind.Added, "TWO"), file.Lines[3]);
            Assert.Equal(new DiffLine(DiffLineKind.Context, "one"), file.Lines[1]);
            Assert.Equal(new DiffLine(DiffLineKind.Hunk, "@@ -10,2 +10,3 @@ void M()"), file.Lines[5]);
            Assert.Equal(new DiffLine(DiffLineKind.Context, "twelve"), file.Lines[^1]);
            Assert.Equal(9, file.Lines.Count);
        }

        [Fact]
        public void Parse_added_and_deleted_files()
        {
            var diff = string.Join('\n',
                "diff --git a/new.txt b/new.txt",
                "new file mode 100644",
                "index 0000000..111",
                "--- /dev/null",
                "+++ b/new.txt",
                "@@ -0,0 +1 @@",
                "+hello",
                "\\ No newline at end of file",
                "diff --git a/old.txt b/old.txt",
                "deleted file mode 100644",
                "index 111..0000000",
                "--- a/old.txt",
                "+++ /dev/null",
                "@@ -1 +0,0 @@",
                "-bye",
                "");

            var files = UnifiedDiffParser.Parse(diff);

            Assert.Equal(2, files.Count);
            Assert.Equal("new.txt", files[0].Path);
            Assert.Equal(DiffFileStatus.Added, files[0].Status);
            Assert.Equal(1, files[0].Additions);
            Assert.Equal(new DiffLine(DiffLineKind.Meta, "\\ No newline at end of file"), files[0].Lines[^1]);
            Assert.Equal("old.txt", files[1].Path);
            Assert.Equal(DiffFileStatus.Deleted, files[1].Status);
            Assert.Equal(1, files[1].Deletions);
        }

        [Fact]
        public void Parse_rename_with_changes_and_pure_rename()
        {
            var diff = string.Join('\n',
                "diff --git a/old name.txt b/new name.txt",
                "similarity index 80%",
                "rename from old name.txt",
                "rename to new name.txt",
                "index 111..222 100644",
                "--- a/old name.txt",
                "+++ b/new name.txt",
                "@@ -1 +1 @@",
                "-a",
                "+b",
                "diff --git a/x.txt b/y.txt",
                "similarity index 100%",
                "rename from x.txt",
                "rename to y.txt",
                "");

            var files = UnifiedDiffParser.Parse(diff);

            Assert.Equal(2, files.Count);
            Assert.Equal(DiffFileStatus.Renamed, files[0].Status);
            Assert.Equal("new name.txt", files[0].Path);
            Assert.Equal("old name.txt", files[0].OldPath);
            Assert.Equal(1, files[0].Additions);
            Assert.Equal(DiffFileStatus.Renamed, files[1].Status);
            Assert.Equal("y.txt", files[1].Path);
            Assert.Equal("x.txt", files[1].OldPath);
            Assert.Empty(files[1].Lines);
        }

        [Fact]
        public void Parse_binary_file()
        {
            var diff = string.Join('\n',
                "diff --git a/img.bin b/img.bin",
                "index 111..222 100644",
                "Binary files a/img.bin and b/img.bin differ",
                "");

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.True(file.IsBinary);
            Assert.Equal("img.bin", file.Path);
            Assert.Equal(0, file.Additions);
            Assert.Empty(file.Lines);
        }

        [Fact]
        public void Parse_quoted_path_keeps_non_bmp_characters_and_decodes_octal_escapes()
        {
            var diff = string.Join('\n',
                "diff --git \"a/old.txt\" \"b/x\\303\\251 \U0001F600.txt\"",
                "similarity index 100%",
                "rename from old.txt",
                "rename to \"x\\303\\251 \U0001F600.txt\"",
                "");

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.Equal("x\u00e9 \U0001F600.txt", file.Path);
        }

        [Fact]
        public void Parse_path_with_spaces_and_no_hunks_uses_header()
        {
            var diff = "diff --git a/my dir/my file.txt b/my dir/my file.txt\nnew file mode 100644\nindex 0000000..e69de29\n";

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.Equal("my dir/my file.txt", file.Path);
            Assert.Equal(DiffFileStatus.Added, file.Status);
        }

        [Fact]
        public void Parse_content_lines_that_look_like_headers_are_content()
        {
            var diff = string.Join('\n',
                "diff --git a/a.md b/a.md",
                "--- a/a.md",
                "+++ b/a.md",
                "@@ -1,2 +1,2 @@",
                "--- old",
                "+++ new",
                "");

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.Equal(1, file.Additions);
            Assert.Equal(1, file.Deletions);
            Assert.Equal("a.md", file.Path);
        }

        [Fact]
        public void Parse_handles_crlf_line_endings()
        {
            var diff = "diff --git a/a b/a\r\n--- a/a\r\n+++ b/a\r\n@@ -1 +1 @@\r\n-x\r\n+y\r\n";

            var file = Assert.Single(UnifiedDiffParser.Parse(diff));

            Assert.Equal("a", file.Path);
            Assert.Equal(new DiffLine(DiffLineKind.Added, "y"), file.Lines[^1]);
        }
    }
}
