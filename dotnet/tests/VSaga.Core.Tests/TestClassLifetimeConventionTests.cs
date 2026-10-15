using System.Text.RegularExpressions;

namespace VSaga.Core.Tests;

/// <summary>
/// xunit v2 (the version every test project here runs) disposes a test class only through <c>IDisposable</c> or
/// <c>IAsyncLifetime</c>; a class that declares just <c>IAsyncDisposable</c> compiles, looks disposed, and is never
/// disposed. Its hosts, providers and connections then leak into the rest of the run, and a process-wide static (a
/// meter's readings, say) carries one test's state into the next. This reads the source of every test project and
/// refuses a class with tests that makes that mistake, so it cannot come back unnoticed.
/// </summary>
public sealed partial class TestClassLifetimeConventionTests
{
    [GeneratedRegex(@"^[ \t]*(?:public|internal)[ \t]+(?:(?:sealed|partial|abstract|static)[ \t]+)*class[ \t]+(?<name>\w+)[^\r\n{]*?:[ \t]*(?<bases>[^\r\n{]*)", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ClassWithBases();

    private static string RepositoryTests()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VSaga.slnx")))
                return Path.Combine(dir.FullName, "tests");
        }

        throw new InvalidOperationException("VSaga.slnx was not found above the test assembly, so the test sources cannot be read.");
    }

    private static IEnumerable<string> SourcesWithTests() =>
        Directory.EnumerateFiles(RepositoryTests(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The classes in <paramref name="source"/> that declare tests and an <c>IAsyncDisposable</c> that xunit v2 would never call.</summary>
    internal static IReadOnlyList<string> NeverDisposed(string source)
    {
        var declarations = ClassWithBases().Matches(source);
        var offenders = new List<string>();

        for (var i = 0; i < declarations.Count; i++)
        {
            var declaration = declarations[i];
            var bases = declaration.Groups["bases"].Value;
            if (!bases.Contains("IAsyncDisposable", StringComparison.Ordinal)
                || bases.Contains("IAsyncLifetime", StringComparison.Ordinal)
                || BareIDisposable().IsMatch(bases))
            {
                continue;
            }

            // The class's text runs to the next class declaration, so a helper declared after it does not lend it its tests.
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : source.Length;
            var body = source[declaration.Index..end];
            if (body.Contains("[Fact", StringComparison.Ordinal) || body.Contains("[Theory", StringComparison.Ordinal))
                offenders.Add(declaration.Groups["name"].Value);
        }

        return offenders;
    }

    [GeneratedRegex(@"(?<![\w.])IDisposable\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BareIDisposable();

    [Fact]
    public void NoTestClassDeclaresOnlyIAsyncDisposable()
    {
        var offenders = SourcesWithTests()
            .SelectMany(path => NeverDisposed(File.ReadAllText(path)).Select(name => $"{Path.GetFileName(path)}: {name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "xunit v2 never calls IAsyncDisposable on a test class; implement IAsyncLifetime (and forward to DisposeAsync if the class owns disposable fields):\n  "
            + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("public sealed class A : IAsyncDisposable\n{\n    [Fact] public void T() { }\n}", "A")]
    [InlineData("public class A : Base, IAsyncDisposable\n{\n    [Theory] public void T() { }\n}", "A")]
    [InlineData("public sealed class A : IAsyncLifetime, IAsyncDisposable\n{\n    [Fact] public void T() { }\n}", null)]
    [InlineData("public sealed class A : IAsyncDisposable, IDisposable\n{\n    [Fact] public void T() { }\n}", null)]
    [InlineData("public sealed class A : IAsyncDisposable\n{\n    public void NotATest() { }\n}", null)]
    [InlineData("public sealed class A\n{\n    [Fact] public void T() { }\n}\ninternal sealed class H : IAsyncDisposable\n{\n}", null)]
    public void TheScan_FlagsOnlyATestClassThatXunitWouldNeverDispose(string source, string? expected)
    {
        Assert.Equal(expected is null ? [] : [expected], NeverDisposed(source));
    }
}
