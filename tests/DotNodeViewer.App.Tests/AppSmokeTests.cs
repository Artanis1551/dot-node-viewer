namespace DotNodeViewer.App.Tests;

/// <summary>
/// Verifies baseline assembly wiring for <see cref="AppLibraryInfo"/>.
/// </summary>
public sealed class AppSmokeTests
{
    [Test]
    public async Task AppLibraryInfo_Dependencies_ReturnExpectedNames()
    {
        await Assert.That(AppLibraryInfo.Name).IsEqualTo("DotNodeViewer.App");
        await Assert.That(AppLibraryInfo.CoreName).IsEqualTo("DotNodeViewer.Core");
        await Assert.That(AppLibraryInfo.LayoutName).IsEqualTo("DotNodeViewer.Layout");
    }
}

