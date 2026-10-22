namespace DotNodeViewer.Layout.Tests;

/// <summary>
/// Verifies baseline assembly wiring for <see cref="LayoutLibraryInfo"/>.
/// </summary>
public sealed class LayoutSmokeTests
{
    [Test]
    public async Task LayoutLibraryInfo_NameAndCoreDependency_ReturnExpectedNames()
    {
        await Assert.That(LayoutLibraryInfo.Name).IsEqualTo("DotNodeViewer.Layout");
        await Assert.That(LayoutLibraryInfo.CoreDependencyName).IsEqualTo("DotNodeViewer.Core");
    }
}

