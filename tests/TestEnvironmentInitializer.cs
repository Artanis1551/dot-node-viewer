using System.Runtime.CompilerServices;

namespace DotNodeViewer.Tests;

/// <summary>
/// Configures hermetic environment settings for <c>Microsoft.Testing.Platform</c> before test host startup.
/// </summary>
internal static class TestEnvironmentInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("TESTINGPLATFORM_TELEMETRY_OPTOUT", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
    }
}

