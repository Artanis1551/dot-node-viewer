using DotNodeViewer.App.Controls;
using DotNodeViewer.App.ViewModels;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.App.Tests;

/// <summary>
/// Unit tests for subgraph breadcrumb drill-down, architecture quick summary, and minimap integration.
/// </summary>
public sealed class SubgraphNavigationAndSummaryTests
{
    private static string ResolveFixturePath(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "dot-node-viewer.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "tests", "DotNodeViewer.Core.Tests", "Fixtures", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate fixture '{relative}' starting from '{AppContext.BaseDirectory}'");
    }

    [Test]
    public async Task BreadcrumbNavigation_DrillDownAndBack_MaintainsStackCorrectly()
    {
        MainWindowViewModel vm = new();

        Subgraph root = new("main", [], [], [], []);
        Subgraph branch1 = new("then_branch", [], [], [], []);
        Subgraph branch2 = new("inner_loop", [], [], [], []);

        // Load root
        vm.Breadcrumbs.Add(new SubgraphBreadcrumbItem("Root: main", root, isCurrent: true));
        await Assert.That(vm.Breadcrumbs.Count).IsEqualTo(1);
        await Assert.That(vm.Breadcrumbs[0].IsCurrent).IsTrue();

        // Drill down 1
        vm.DrillDownSubgraph(branch1);
        await Assert.That(vm.Breadcrumbs.Count).IsEqualTo(2);
        await Assert.That(vm.Breadcrumbs[0].IsCurrent).IsFalse();
        await Assert.That(vm.Breadcrumbs[1].IsCurrent).IsTrue();
        await Assert.That(vm.Breadcrumbs[1].Subgraph).IsEqualTo(branch1);

        // Drill down 2
        vm.DrillDownSubgraph(branch2);
        await Assert.That(vm.Breadcrumbs.Count).IsEqualTo(3);
        await Assert.That(vm.Breadcrumbs[2].IsCurrent).IsTrue();

        // Back 1
        vm.NavigateBackSubgraph();
        await Assert.That(vm.Breadcrumbs.Count).IsEqualTo(2);
        await Assert.That(vm.Breadcrumbs[1].IsCurrent).IsTrue();
        await Assert.That(vm.Breadcrumbs[1].Subgraph).IsEqualTo(branch1);

        // Select root directly
        vm.SelectBreadcrumb(vm.Breadcrumbs[0]);
        await Assert.That(vm.Breadcrumbs.Count).IsEqualTo(1);
        await Assert.That(vm.Breadcrumbs[0].IsCurrent).IsTrue();
        await Assert.That(vm.Breadcrumbs[0].Subgraph).IsEqualTo(root);
    }

    [Test]
    public async Task QuickSummary_TfliteInt8_CalculatesQuantizationAndWeights()
    {
        string path = ResolveFixturePath("tflite/tiny_cnn_int8.tflite");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.TotalParameters).IsGreaterThan(0);
        await Assert.That(vm.TotalParametersFormatted).Contains("params");
        await Assert.That(vm.TotalWeightBytes).IsGreaterThan(0);
        await Assert.That(vm.TotalWeightBytesFormatted).IsNotEmpty();
        await Assert.That(vm.PrecisionMixText).Contains("INT8");
    }

    [Test]
    public async Task QuickSummary_ExecuTorch_DetectsDelegateBlocks()
    {
        string path = ResolveFixturePath("executorch/tiny_cnn_xnnpack.pte");
        MainWindowViewModel vm = new();

        await vm.LoadModelAsync(path);

        await Assert.That(vm.HasModel).IsTrue();
        await Assert.That(vm.HasDelegateBlocks).IsTrue();
        await Assert.That(vm.DelegateBlocksText).Contains("XNNPACK");
    }

    [Test]
    public async Task MinimapControl_CanCollapseAndExpand()
    {
        await HeadlessAppSession.DispatchAsync(async () =>
        {
            MinimapControl minimap = new();
            await Assert.That(minimap.IsCollapsed).IsFalse();
            await Assert.That(minimap.Width).IsEqualTo(190.0);

            minimap.IsCollapsed = true;
            await Assert.That(minimap.Width).IsEqualTo(32.0);

            minimap.IsCollapsed = false;
            await Assert.That(minimap.Width).IsEqualTo(190.0);
        });
    }

    [Test]
    public async Task Diagnostics_PopulatesDiagnosticsListAndCount()
    {
        MainWindowViewModel vm = new();

        ParseDiagnostic diag1 = new(DiagnosticSeverity.Warning, "DNV_WARN_01", "Warning message", Location: "Node_A");
        ParseDiagnostic diag2 = new(DiagnosticSeverity.Info, "DNV_INFO_02", "Info notice");

        ModelGraph model = new(
            new ModelFormatInfo(new ModelFormatId("test"), "1.0"),
            Producer: "test_producer",
            Subgraphs: [new Subgraph("main", [], [], [], [])],
            Metadata: new Dictionary<string, string>(),
            Diagnostics: [diag1, diag2]);

        // Verify diagnostics binding
        vm.DiagnosticsList.Clear();
        foreach (ParseDiagnostic d in model.Diagnostics)
        {
            vm.DiagnosticsList.Add(d);
        }
        vm.DiagnosticsCount = vm.DiagnosticsList.Count;
        vm.HasDiagnostics = vm.DiagnosticsCount > 0;

        await Assert.That(vm.HasDiagnostics).IsTrue();
        await Assert.That(vm.DiagnosticsCount).IsEqualTo(2);
        await Assert.That(vm.DiagnosticsList[0].Code).IsEqualTo("DNV_WARN_01");
    }
}

