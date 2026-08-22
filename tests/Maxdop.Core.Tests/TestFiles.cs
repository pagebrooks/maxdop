namespace Maxdop.Core.Tests;

/// <summary>
/// Locates files under <c>tests/fixtures</c>. They are copied to the output directory rather
/// than located by walking up from the source tree, so the suite runs the same way from CI as
/// it does locally.
/// </summary>
internal static class TestFiles
{
    internal static string Path(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture '{name}' was not copied to the test output.", path);
        }

        return path;
    }

    internal static string Read(string name) => File.ReadAllText(Path(name));

    /// <summary>
    /// The <c>tests/fixtures</c> directory in the source tree, found by walking up from the test
    /// binary. Needed only by golden tests, which write regenerated snapshots back to source.
    /// </summary>
    internal static string SourceFixturesDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "tests", "fixtures");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate tests/fixtures from the test output directory.");
        }
    }
}
