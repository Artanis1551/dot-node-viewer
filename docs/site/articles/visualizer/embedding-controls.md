# How-To Guide: Embedding `GraphCanvas` in Avalonia Applications

`DotNodeViewer.App` provides a standalone, reusable `GraphCanvas` control that .NET developers can embed directly into their own Avalonia desktop or WebAssembly applications to visualize neural network architectures and directed acyclic graphs.

---

## 1. Project References & Dependencies

To embed `GraphCanvas`, add project references to `DotNodeViewer.Core`, `DotNodeViewer.Layout`, and `DotNodeViewer.App`:

```xml
<ItemGroup>
  <ProjectReference Include="..\DotNodeViewer.Core\DotNodeViewer.Core.csproj" />
  <ProjectReference Include="..\DotNodeViewer.Layout\DotNodeViewer.Layout.csproj" />
  <ProjectReference Include="..\DotNodeViewer.App\DotNodeViewer.App.csproj" />
</ItemGroup>
```

---

## 2. Merging Theme Dictionaries

`GraphCanvas` and the visualizer controls utilize theme tokens defined in `DotNodeViewer.App`. Merge the token and palette dictionaries in your `App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="MyAwesomeApp.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>

  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="avares://DotNodeViewer.App/Themes/Tokens.axaml" />
        <ResourceInclude Source="avares://DotNodeViewer.App/Themes/NodePalette.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

---

## 3. Adding `GraphCanvas` to XAML

Import the `DotNodeViewer.App.Controls` namespace in your Avalonia Window or UserControl:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="using:DotNodeViewer.App.Controls"
             xmlns:views="using:DotNodeViewer.App.Views"
             xmlns:vm="using:MyAwesomeApp.ViewModels"
             x:Class="MyAwesomeApp.Views.ModelInspectorView"
             x:DataType="vm:ModelInspectorViewModel">

  <Grid ColumnDefinitions="*,Auto,360">

    <!-- Interactive Graph Canvas -->
    <Grid Grid.Column="0">
      <controls:GraphCanvas x:Name="Canvas"
                            Layout="{Binding Layout}"
                            SelectedNode="{Binding SelectedNode, Mode=TwoWay}" />

      <!-- Floating Zoom Controls -->
      <Border HorizontalAlignment="Right"
              VerticalAlignment="Bottom"
              Margin="16"
              Background="{DynamicResource ShellSurfaceBaseBrush}"
              BorderBrush="{DynamicResource ShellBorderMutedBrush}"
              BorderThickness="1"
              CornerRadius="8"
              Padding="4">
        <StackPanel Orientation="Horizontal" Spacing="4">
          <Button Content="+" Command="{Binding #Canvas.ZoomInCommand}" />
          <Button Content="-" Command="{Binding #Canvas.ZoomOutCommand}" />
          <Button Content="Fit" Command="{Binding #Canvas.ZoomToFitCommand}" />
          <Button Content="1:1" Command="{Binding #Canvas.ResetViewCommand}" />
        </StackPanel>
      </Border>
    </Grid>

    <GridSplitter Grid.Column="1" Width="1" Background="{DynamicResource ShellBorderMutedBrush}" />

    <!-- Optional Built-in Properties Sidebar -->
    <views:PropertiesPanelView Grid.Column="2" DataContext="{Binding}" />

  </Grid>

</UserControl>
```

---

## 4. ViewModel Pipeline

Load a model asynchronously using `ModelFormatRegistry`, compute its layered layout using `ModelGraphLayoutAdapter`, and bind the resulting `VisualSubgraphLayout` to `GraphCanvas.Layout`:

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DotNodeViewer.App.Rendering;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;
using DotNodeViewer.Layout;

namespace MyAwesomeApp.ViewModels;

public sealed partial class ModelInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private VisualSubgraphLayout? _layout;

    [ObservableProperty]
    private VisualNode? _selectedNode;

    public async Task LoadModelFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(filePath);

        // 1. Parse model stream into Unified Graph IR
        ModelGraph model = await ModelFormatRegistry.Default.ReadAsync(stream, options: null, cancellationToken);

        // 2. Compute layered graph layout using platform font metrics
        ModelGraphLayoutAdapter layoutAdapter = new(textMeasurer: AvaloniaTextMeasurer.Default);
        VisualSubgraphLayout layout = await Task.Run(() => layoutAdapter.CreateLayout(model, options: null, cancellationToken), cancellationToken);

        // 3. Assign to observable property
        Layout = layout;
    }
}
```

---

## 5. Key `GraphCanvas` Properties & Commands

| Member | Type | Description |
|---|---|---|
| `Layout` | `VisualSubgraphLayout?` | The positioned graph layout to render. |
| `SelectedNode` | `VisualNode?` | The currently selected node (`TwoWay` binding supported). |
| `HoveredNode` | `VisualNode?` | The node currently under the pointer. |
| `Zoom` | `double` | Current zoom scale factor (clamps between `0.05` and `4.0`). |
| `PanOffset` | `Vector` | Screen translation offset in logical pixels. |
| `ZoomPercentText`| `string` | Read-only formatted percentage string (e.g. `"100%"`). |
| `ZoomInCommand` | `IRelayCommand` | Zooms into the canvas center by 25%. |
| `ZoomOutCommand` | `IRelayCommand` | Zooms out of the canvas center by 20%. |
| `ZoomToFitCommand`| `IRelayCommand`| Centers and scales the graph bounding box to fit the viewport. |
| `ResetViewCommand`| `IRelayCommand`| Resets zoom to 100% and centers the origin. |
| `ScreenToWorld(Point)` | Method | Converts screen pixel coordinates to layout space. |
| `WorldToScreen(Point)` | Method | Converts layout coordinates to screen pixels. |

