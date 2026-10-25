using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class DiffLanguagesAcceptanceTests
    {
        [Theory]
        [InlineData(".cs", "csharp")]
        [InlineData(".razor", "xml")]
        [InlineData(".cshtml", "xml")]
        [InlineData(".xml", "xml")]
        [InlineData(".html", "xml")]
        [InlineData(".htm", "xml")]
        [InlineData(".csproj", "xml")]
        [InlineData(".props", "xml")]
        [InlineData(".targets", "xml")]
        [InlineData(".slnx", "xml")]
        [InlineData(".config", "xml")]
        [InlineData(".svg", "xml")]
        [InlineData(".js", "javascript")]
        [InlineData(".mjs", "javascript")]
        [InlineData(".cjs", "javascript")]
        [InlineData(".jsx", "javascript")]
        [InlineData(".ts", "typescript")]
        [InlineData(".tsx", "typescript")]
        [InlineData(".css", "css")]
        [InlineData(".scss", "scss")]
        [InlineData(".json", "json")]
        [InlineData(".yml", "yaml")]
        [InlineData(".yaml", "yaml")]
        [InlineData(".py", "python")]
        [InlineData(".ps1", "powershell")]
        [InlineData(".psm1", "powershell")]
        [InlineData(".sh", "bash")]
        [InlineData(".bash", "bash")]
        [InlineData(".sql", "sql")]
        [InlineData(".md", "markdown")]
        public void Known_extensions_map_in_any_case_and_with_directories(string ext, string expected)
        {
            Assert.Equal(expected, DiffLanguages.FromPath("file" + ext));
            Assert.Equal(expected, DiffLanguages.FromPath("file" + ext.ToUpperInvariant()));
            Assert.Equal(expected, DiffLanguages.FromPath("Name." + char.ToUpperInvariant(ext[1]) + ext.Substring(2)));
            Assert.Equal(expected, DiffLanguages.FromPath("src/app/Foo" + ext));
            Assert.Equal(expected, DiffLanguages.FromPath(@"src\app\Foo" + ext));
            Assert.Equal(expected, DiffLanguages.FromPath("dir.with.dots/file.name.v2" + ext));
            Assert.Equal(expected, DiffLanguages.FromPath("a/b/c/" + ext.ToUpperInvariant().Replace(".", "x.")));
        }

        [Theory]
        [InlineData("src/app/Foo.CS", "csharp")]
        [InlineData("src/app/Foo.Cs", "csharp")]
        [InlineData("a/src/Foo.cs", "csharp")]
        [InlineData("b/src/Foo.cs", "csharp")]
        [InlineData("a/AutoPilot/Pages/Index.RAZOR", "xml")]
        [InlineData("AutoPilot.Tests/Foo.Tests.csproj", "xml")]
        [InlineData("web/site.min.js", "javascript")]
        [InlineData("README.MD", "markdown")]
        [InlineData("scripts\\build.PS1", "powershell")]
        [InlineData("C:\\repo\\x.Yaml", "yaml")]
        public void Specific_paths(string path, string expected)
        {
            Assert.Equal(expected, DiffLanguages.FromPath(path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Makefile")]
        [InlineData("src/Makefile")]
        [InlineData("dir.with.dots/file")]
        [InlineData("dir.cs/file")]
        [InlineData("dir.cs\\file")]
        [InlineData("a.js/README")]
        [InlineData("file.txt")]
        [InlineData("file.unknown")]
        [InlineData("file.")]
        [InlineData("file.cs.txt")]
        [InlineData("file.csx")]
        [InlineData("file.jsonl")]
        [InlineData("file.mdx")]
        [InlineData("file.c")]
        [InlineData("src/app/foo.exe")]
        public void Unknown_or_missing_extension_returns_null(string? path)
        {
            Assert.Null(DiffLanguages.FromPath(path));
        }
    }
}
