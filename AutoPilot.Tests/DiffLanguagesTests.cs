using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class DiffLanguagesTests
    {
        [Theory]
        [InlineData("a.cs", "csharp")]
        [InlineData("a.razor", "xml")]
        [InlineData("a.cshtml", "xml")]
        [InlineData("a.xml", "xml")]
        [InlineData("a.html", "xml")]
        [InlineData("a.htm", "xml")]
        [InlineData("a.csproj", "xml")]
        [InlineData("a.props", "xml")]
        [InlineData("a.targets", "xml")]
        [InlineData("a.slnx", "xml")]
        [InlineData("a.config", "xml")]
        [InlineData("a.svg", "xml")]
        [InlineData("a.js", "javascript")]
        [InlineData("a.mjs", "javascript")]
        [InlineData("a.cjs", "javascript")]
        [InlineData("a.jsx", "javascript")]
        [InlineData("a.ts", "typescript")]
        [InlineData("a.tsx", "typescript")]
        [InlineData("a.css", "css")]
        [InlineData("a.scss", "scss")]
        [InlineData("a.json", "json")]
        [InlineData("a.yml", "yaml")]
        [InlineData("a.yaml", "yaml")]
        [InlineData("a.py", "python")]
        [InlineData("a.ps1", "powershell")]
        [InlineData("a.psm1", "powershell")]
        [InlineData("a.sh", "bash")]
        [InlineData("a.bash", "bash")]
        [InlineData("a.sql", "sql")]
        [InlineData("a.md", "markdown")]
        public void FromPath_maps_known_extensions(string path, string expected) =>
            Assert.Equal(expected, DiffLanguages.FromPath(path));

        [Theory]
        [InlineData("Program.CS", "csharp")]
        [InlineData("Page.RAZOR", "xml")]
        [InlineData("x.Yaml", "yaml")]
        [InlineData("README.MD", "markdown")]
        public void FromPath_is_case_insensitive(string path, string expected) =>
            Assert.Equal(expected, DiffLanguages.FromPath(path));

        [Theory]
        [InlineData("src/Services/Foo.cs", "csharp")]
        [InlineData("AutoPilot/Components/Chat/ChatBot.razor.js", "javascript")]
        [InlineData("AutoPilot/Components/Chat/ChatBot.razor.css", "css")]
        [InlineData("dir.with.dots/script.sh", "bash")]
        public void FromPath_handles_directories_and_multiple_dots(string path, string expected) =>
            Assert.Equal(expected, DiffLanguages.FromPath(path));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Makefile")]
        [InlineData("src/LICENSE")]
        [InlineData("notes.txt")]
        [InlineData("archive.cs.bak")]
        [InlineData("dir.cs/file")]
        [InlineData("file.")]
        [InlineData(".gitignore")]
        public void FromPath_returns_null_for_unknown(string? path) =>
            Assert.Null(DiffLanguages.FromPath(path));
    }
}
