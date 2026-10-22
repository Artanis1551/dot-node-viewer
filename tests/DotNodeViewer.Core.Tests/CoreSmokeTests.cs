namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Verifies baseline assembly wiring for <see cref="CoreLibraryInfo"/>.
/// </summary>
public sealed class CoreSmokeTests
{
    [Test]
    public async Task CoreLibraryInfo_Name_ReturnsExpectedAssemblyName()
    {
        await Assert.That(CoreLibraryInfo.Name).IsEqualTo("DotNodeViewer.Core");
    }
}

