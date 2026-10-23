using System.Text.RegularExpressions;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies that the repository domain glossary (<c>GLOSSARY.md</c>) and the DocFX documentation
/// site article (<c>docs/site/articles/glossary.md</c>) maintain single-source-of-truth integrity.
/// </summary>
public sealed class GlossaryDocumentationTests
{
    private static readonly string RepositoryRoot = LocateRepositoryRoot();

    [Test]
    public async Task RootGlossary_ExistsAndContainsStandardSections()
    {
        string glossaryPath = Path.Combine(RepositoryRoot, "GLOSSARY.md");
        await Assert.That(File.Exists(glossaryPath)).IsTrue();

        string content = await File.ReadAllTextAsync(glossaryPath);
        await Assert.That(content.Contains("## Core Domain Terms", StringComparison.Ordinal)).IsTrue();
        await Assert.That(content.Contains("## Layout & Visualization Terms", StringComparison.Ordinal)).IsTrue();

        // Extract markdown table rows
        MatchCollection rows = Regex.Matches(content, @"^\|\s*\*\*([^*]+)\*\*\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|$", RegexOptions.Multiline);
        await Assert.That(rows.Count >= 20).IsTrue();

        foreach (Match match in rows)
        {
            string term = match.Groups[1].Value.Trim();
            string definition = match.Groups[2].Value.Trim();
            string location = match.Groups[3].Value.Trim();

            await Assert.That(string.IsNullOrWhiteSpace(term)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(definition)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(location)).IsFalse();
        }
    }

    [Test]
    public async Task SiteGlossaryArticle_IncludesRootGlossaryDirectly()
    {
        string siteGlossaryPath = Path.Combine(RepositoryRoot, "docs", "site", "articles", "glossary.md");
        await Assert.That(File.Exists(siteGlossaryPath)).IsTrue();

        string content = await File.ReadAllTextAsync(siteGlossaryPath);

        // DocFX markdown transclusion directive referencing root GLOSSARY.md
        // Ensures docs/site/articles/glossary.md never drifts from GLOSSARY.md
        bool hasInclude = Regex.IsMatch(content, @"\[!include\[\]\([^)]*GLOSSARY\.md\)\s*\]");
        await Assert.That(hasInclude).IsTrue();
    }

    private static string LocateRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "dot-node-viewer.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "GLOSSARY.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
