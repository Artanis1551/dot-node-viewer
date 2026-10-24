using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Layout.Tests;

/// <summary>
/// Verifies the spatial querying, viewport culling, and 1-hop adjacency capabilities of <see cref="VisualSubgraphLayout"/>.
/// </summary>
public sealed class VisualSubgraphLayoutTests
{
    [Test]
    public async Task VisualSubgraphLayout_SpatialQueries_FindNodeAtAndRegionCulling()
    {
        string fixturePath = ResolveFixturePath("onnx", "tiny_mlp.onnx");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_mlp.onnx" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);
        await Assert.That(layout.Nodes.Count > 0).IsTrue();
        await Assert.That(layout.Edges.Count > 0).IsTrue();

        // 1. Point hit-testing: querying node center must return that node
        VisualNode firstNode = layout.Nodes[0];
        BoundingBox bounds = firstNode.Bounds;
        double centerX = bounds.X + (bounds.Width * 0.5);
        double centerY = bounds.Y + (bounds.Height * 0.5);

        VisualNode? hitNode = layout.FindNodeAt(centerX, centerY);
        await Assert.That(hitNode).IsNotNull();
        await Assert.That(hitNode!.Id).IsEqualTo(firstNode.Id);

        // 2. Querying a point far outside the graph must return null
        VisualNode? missNode = layout.FindNodeAt(-10000.0, -10000.0);
        await Assert.That(missNode).IsNull();

        // 3. Viewport culling covering full graph must return all nodes and edges
        BoundingBox fullBounds = layout.Bounds;
        IReadOnlyList<VisualNode> allVisibleNodes = layout.FindNodesInRegion(fullBounds);
        await Assert.That(allVisibleNodes.Count).IsEqualTo(layout.Nodes.Count);

        IReadOnlyList<VisualEdge> allVisibleEdges = layout.FindEdgesInRegion(fullBounds);
        await Assert.That(allVisibleEdges.Count).IsEqualTo(layout.Edges.Count);

        VisibleLayoutElements visibleElements = layout.GetVisibleElements(fullBounds);
        await Assert.That(visibleElements.Nodes.Count).IsEqualTo(layout.Nodes.Count);
        await Assert.That(visibleElements.Edges.Count).IsEqualTo(layout.Edges.Count);

        // 4. Viewport far outside must return empty collections
        BoundingBox outside = new(-5000, -5000, 100, 100);
        VisibleLayoutElements emptyElements = layout.GetVisibleElements(outside);
        await Assert.That(emptyElements.Nodes.Count).IsEqualTo(0);
        await Assert.That(emptyElements.Edges.Count).IsEqualTo(0);
    }

    [Test]
    public async Task VisualSubgraphLayout_AdjacencyQueries_PredecessorsSuccessorsAndConnectedEdges()
    {
        string fixturePath = ResolveFixturePath("onnx", "tiny_mlp.onnx");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_mlp.onnx" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);

        // Find an operator node that has both incoming and outgoing edges
        VisualNode? middleOp = layout.Nodes.FirstOrDefault(n =>
            n.Kind == VisualNodeKind.Operator &&
            layout.GetIncomingEdges(n.Id).Count > 0 &&
            layout.GetOutgoingEdges(n.Id).Count > 0);

        await Assert.That(middleOp).IsNotNull();
        string nodeId = middleOp!.Id;

        IReadOnlyList<VisualEdge> inEdges = layout.GetIncomingEdges(nodeId);
        IReadOnlyList<VisualEdge> outEdges = layout.GetOutgoingEdges(nodeId);
        IReadOnlyList<VisualEdge> connectedEdges = layout.GetConnectedEdges(nodeId);

        await Assert.That(inEdges.Count > 0).IsTrue();
        await Assert.That(outEdges.Count > 0).IsTrue();
        await Assert.That(connectedEdges.Count).IsEqualTo(inEdges.Count + outEdges.Count);

        // Verify incoming edges have TargetNodeId == nodeId
        for (int i = 0; i < inEdges.Count; i++)
        {
            await Assert.That(inEdges[i].Layout.TargetNodeId).IsEqualTo(nodeId);
        }

        // Verify outgoing edges have SourceNodeId == nodeId
        for (int i = 0; i < outEdges.Count; i++)
        {
            await Assert.That(outEdges[i].Layout.SourceNodeId).IsEqualTo(nodeId);
        }

        // Verify predecessors and successors
        IReadOnlyList<VisualNode> predecessors = layout.GetPredecessors(nodeId);
        IReadOnlyList<VisualNode> successors = layout.GetSuccessors(nodeId);
        IReadOnlyList<VisualNode> adjacent = layout.GetAdjacentNodes(nodeId);

        await Assert.That(predecessors.Count > 0).IsTrue();
        await Assert.That(successors.Count > 0).IsTrue();
        await Assert.That(adjacent.Count).IsEqualTo(predecessors.Count + successors.Count);

        // Verify each predecessor is the source of at least one inEdge
        for (int i = 0; i < predecessors.Count; i++)
        {
            string predId = predecessors[i].Id;
            bool matched = inEdges.Any(e => e.Layout.SourceNodeId == predId);
            await Assert.That(matched).IsTrue();
        }

        // Verify each successor is the target of at least one outEdge
        for (int i = 0; i < successors.Count; i++)
        {
            string succId = successors[i].Id;
            bool matched = outEdges.Any(e => e.Layout.TargetNodeId == succId);
            await Assert.That(matched).IsTrue();
        }
    }

    [Test]
    public async Task VisualSubgraphLayout_PrecomputedEdgeBounds_EnclosesAllPoints()
    {
        string fixturePath = ResolveFixturePath("onnx", "tiny_mlp.onnx");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_mlp.onnx" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);

        for (int i = 0; i < layout.Edges.Count; i++)
        {
            VisualEdge edge = layout.Edges[i];
            BoundingBox bounds = edge.Bounds;

            // Bounds must enclose every polyline point
            for (int p = 0; p < edge.Layout.Points.Count; p++)
            {
                LayoutPoint pt = edge.Layout.Points[p];
                await Assert.That(pt.X >= bounds.X - 1e-4).IsTrue();
                await Assert.That(pt.X <= bounds.Right + 1e-4).IsTrue();
                await Assert.That(pt.Y >= bounds.Y - 1e-4).IsTrue();
                await Assert.That(pt.Y <= bounds.Bottom + 1e-4).IsTrue();
            }
        }
    }

    private static string ResolveFixturePath(string formatFolder, string fileName)
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "tests", "DotNodeViewer.Core.Tests", "Fixtures", formatFolder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent!;
        }

        throw new FileNotFoundException($"Fixture '{fileName}' not found in format folder '{formatFolder}'.");
    }
}
