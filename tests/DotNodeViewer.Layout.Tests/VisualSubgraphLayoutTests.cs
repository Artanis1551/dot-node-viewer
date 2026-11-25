using System.Diagnostics;
using System.Globalization;
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
    public async Task VisualSubgraphLayout_FindNodeAtPointAndEdgesById_WorksImmediatelyOnFreshLayout()
    {
        string fixturePath = ResolveFixturePath("tflite", "tiny_cnn_int8.tflite");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_int8.tflite" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);

        // EdgesById must be populated immediately on a fresh layout before calling any adjacency query
        await Assert.That(layout.EdgesById.Count).IsEqualTo(layout.Edges.Count);
        for (int i = 0; i < layout.Edges.Count; i++)
        {
            VisualEdge edge = layout.Edges[i];
            await Assert.That(layout.EdgesById.TryGetValue(edge.Id, out VisualEdge? found)).IsTrue();
            await Assert.That(found).IsSameReferenceAs(edge);
        }

        // FindNodeAt(LayoutPoint) overload matches node center
        VisualNode target = layout.Nodes[0];
        VisualNode? hit = layout.FindNodeAt(new LayoutPoint(target.Bounds.CenterX, target.Bounds.CenterY));
        await Assert.That(hit).IsSameReferenceAs(target);
    }

    [Test]
    public async Task VisualSubgraphLayout_GetVisibleElementsBufferOverloads_AllocateZeroBytesWhenWarm()
    {
        string fixturePath = ResolveFixturePath("tflite", "tiny_cnn_int8.tflite");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_int8.tflite" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);
        BoundingBox viewport = layout.Bounds;
        BoundingBox expandedViewport = new(viewport.X - 20, viewport.Y - 20, viewport.Width + 40, viewport.Height + 40);

        List<VisualNode> nodeBuffer = new(layout.Nodes.Count);
        List<VisualEdge> edgeBuffer = new(layout.Edges.Count);

        // Warm up JIT
        layout.GetVisibleElements(viewport, nodeBuffer, edgeBuffer);
        layout.GetVisibleElements(viewport, expandedViewport, nodeBuffer, edgeBuffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        layout.GetVisibleElements(viewport, expandedViewport, nodeBuffer, edgeBuffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0L);
        await Assert.That(nodeBuffer.Count).IsEqualTo(layout.Nodes.Count);
        await Assert.That(edgeBuffer.Count).IsEqualTo(layout.Edges.Count);
    }

    [Test]
    public async Task PositionedEdge_Bounds_IncludesControlPointsAndLabelAnchorAndPreservesRecordEquality()
    {
        LayoutPoint[] points = [new(100, 100), new(100, 200)];
        CubicBezierSegment[] beziers =
        [
            new(new LayoutPoint(100, 100), new LayoutPoint(60, 130), new LayoutPoint(150, 170), new LayoutPoint(100, 200))
        ];
        LayoutPoint labelAnchor = new(165, 150);

        PositionedEdge edge1 = new("e0", "n0", "n1", "out", "in", false, points, beziers, labelAnchor, "shape");
        PositionedEdge edge2 = new("e0", "n0", "n1", "out", "in", false, points, beziers, labelAnchor, "shape");

        // Record equality before reading Bounds
        await Assert.That(edge1 == edge2).IsTrue();

        BoundingBox b = edge1.Bounds;
        await Assert.That(b.X).IsEqualTo(60.0);
        await Assert.That(b.Y).IsEqualTo(100.0);
        await Assert.That(b.Right).IsEqualTo(165.0);
        await Assert.That(b.Bottom).IsEqualTo(200.0);

        // Record equality after reading Bounds on edge1 only
        await Assert.That(edge1 == edge2).IsTrue();
        await Assert.That(edge1.GetHashCode()).IsEqualTo(edge2.GetHashCode());
    }

    [Test]
    public async Task VisualSubgraphLayout_ParallelEdgesSelfLoopAndNullGuards_BehaveCorrectly()
    {
        Subgraph emptySubgraph = new("synthetic", [], [], [], []);
        PositionedNode nodeA = new("a", 0, 0, 100, 40, 0, 0, [], [new PositionedPort("out", 50, 40)]);
        PositionedNode nodeB = new("b", 0, 100, 100, 40, 1, 0, [new PositionedPort("in", 50, 100)], [new PositionedPort("out", 50, 140)]);
        PositionedNode nodeIsolated = new("c", 200, 0, 100, 40, 0, 1, [], []);

        VisualNode vA = CreateVisualNode(nodeA);
        VisualNode vB = CreateVisualNode(nodeB);
        VisualNode vC = CreateVisualNode(nodeIsolated);

        VisualEdge e0 = CreateVisualEdge("e0", "a", "b", [new LayoutPoint(50, 40), new LayoutPoint(50, 100)]);
        VisualEdge e1 = CreateVisualEdge("e1", "a", "b", [new LayoutPoint(50, 40), new LayoutPoint(50, 100)]);
        VisualEdge eSelf = CreateVisualEdge("e_self", "b", "b", [new LayoutPoint(50, 140), new LayoutPoint(130, 120), new LayoutPoint(50, 100)]);

        PositionedGraph positioned = new(
            [nodeA, nodeB, nodeIsolated],
            [e0.Layout, e1.Layout, eSelf.Layout],
            new BoundingBox(0, 0, 300, 150),
            LayoutDirection.TopToBottom);

        Dictionary<string, VisualNode> nodesById = new(StringComparer.Ordinal)
        {
            [vA.Id] = vA,
            [vB.Id] = vB,
            [vC.Id] = vC,
        };

        VisualSubgraphLayout layout = new(emptySubgraph, positioned, [vA, vB, vC], [e0, e1, eSelf], nodesById);

        // Parallel edges preserved in GetConnectedEdges, deduplicated in GetPredecessors/GetSuccessors
        IReadOnlyList<VisualEdge> connectedB = layout.GetConnectedEdges("b");
        await Assert.That(connectedB.Count).IsEqualTo(3);
        await Assert.That(connectedB.Count(e => e.Id == "e_self")).IsEqualTo(1);

        IReadOnlyList<VisualEdge> incomingB = layout.GetIncomingEdges("b");
        IReadOnlyList<VisualEdge> outgoingB = layout.GetOutgoingEdges("b");
        await Assert.That(incomingB.Count).IsEqualTo(3);
        await Assert.That(outgoingB.Count).IsEqualTo(1);

        IReadOnlyList<VisualNode> predsB = layout.GetPredecessors("b");
        IReadOnlyList<VisualNode> succsB = layout.GetSuccessors("b");
        IReadOnlyList<VisualNode> adjB = layout.GetAdjacentNodes("b");
        await Assert.That(predsB.Count).IsEqualTo(2);
        await Assert.That(succsB.Count).IsEqualTo(1);
        await Assert.That(adjB.Count).IsEqualTo(2);

        // Isolated and unknown nodes return empty lists
        await Assert.That(layout.GetConnectedEdges("c")).IsEmpty();
        await Assert.That(layout.GetAdjacentNodes("c")).IsEmpty();
        await Assert.That(layout.GetConnectedEdges("does_not_exist")).IsEmpty();

        // Null guards throw ArgumentNullException
        await Assert.That(() => layout.GetConnectedEdges(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => layout.GetIncomingEdges(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => layout.GetOutgoingEdges(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => layout.GetAdjacentNodes(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => layout.GetPredecessors(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => layout.GetSuccessors(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task VisualSubgraphLayout_ConcurrentQueries_AreThreadSafe()
    {
        string fixturePath = ResolveFixturePath("tflite", "tiny_cnn_int8.tflite");
        await using FileStream stream = new(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModelGraph modelGraph = await ModelFormatRegistry.Default.ReadAsync(
            stream,
            new ModelReadOptions { FileName = "tiny_cnn_int8.tflite" });

        VisualSubgraphLayout layout = ModelGraphLayoutAdapter.Default.CreateLayout(modelGraph);
        int expectedNodes = layout.Nodes.Count;
        int expectedEdges = layout.Edges.Count;

        Parallel.For(0, 64, i =>
        {
            VisualNode node = layout.Nodes[i % expectedNodes];
            if (layout.EdgesById.Count != expectedEdges)
            {
                throw new InvalidOperationException("EdgesById count mismatch under concurrency.");
            }

            VisualNode? hit = layout.FindNodeAt(node.Bounds.CenterX, node.Bounds.CenterY);
            if (hit is null || hit.Id != node.Id)
            {
                throw new InvalidOperationException($"FindNodeAt failed for '{node.Id}'.");
            }

            VisibleLayoutElements visible = layout.GetVisibleElements(layout.Bounds);
            if (visible.Nodes.Count != expectedNodes || visible.Edges.Count != expectedEdges)
            {
                throw new InvalidOperationException("GetVisibleElements count mismatch under concurrency.");
            }

            _ = layout.GetConnectedEdges(node.Id);
            _ = layout.GetAdjacentNodes(node.Id);
        });
    }

    [Test]
    public async Task VisualSubgraphLayout_FiveThousandNodeGraph_IndexesAndCullsFast()
    {
        const int nodeCount = 5000;
        const int widthPerLayer = 10;
        List<LayoutNode> layoutNodes = new(nodeCount);
        List<LayoutEdge> layoutEdges = new(nodeCount);

        for (int i = 0; i < nodeCount; i++)
        {
            layoutNodes.Add(new LayoutNode(
                $"n{i.ToString(CultureInfo.InvariantCulture)}",
                140,
                40,
                [new LayoutPort("in")],
                [new LayoutPort("out")]));

            if (i >= widthPerLayer)
            {
                layoutEdges.Add(new LayoutEdge(
                    $"e{i.ToString(CultureInfo.InvariantCulture)}",
                    $"n{(i - widthPerLayer).ToString(CultureInfo.InvariantCulture)}",
                    $"n{i.ToString(CultureInfo.InvariantCulture)}",
                    "out",
                    "in"));
            }
        }

        PositionedGraph positioned = SugiyamaLayoutEngine.Layout(new LayoutGraph(layoutNodes, layoutEdges));
        VisualNode[] vNodes = new VisualNode[positioned.Nodes.Count];
        Dictionary<string, VisualNode> nodesById = new(positioned.Nodes.Count, StringComparer.Ordinal);
        for (int i = 0; i < positioned.Nodes.Count; i++)
        {
            vNodes[i] = CreateVisualNode(positioned.Nodes[i]);
            nodesById[vNodes[i].Id] = vNodes[i];
        }

        VisualEdge[] vEdges = new VisualEdge[positioned.Edges.Count];
        for (int i = 0; i < positioned.Edges.Count; i++)
        {
            PositionedEdge pe = positioned.Edges[i];
            vEdges[i] = CreateVisualEdge(pe.Id, pe.SourceNodeId, pe.TargetNodeId, pe.Points, pe);
        }

        Subgraph dummySubgraph = new("large", [], [], [], []);
        Stopwatch sw = Stopwatch.StartNew();
        VisualSubgraphLayout layout = new(dummySubgraph, positioned, vNodes, vEdges, nodesById);
        sw.Stop();

        await Assert.That(sw.ElapsedMilliseconds < 1000).IsTrue();

        VisualNode probeTarget = layout.Nodes[2500];
        VisualNode? hit = layout.FindNodeAt(probeTarget.Bounds.CenterX, probeTarget.Bounds.CenterY);
        await Assert.That(hit).IsSameReferenceAs(probeTarget);

        BoundingBox localRegion = new(
            probeTarget.Bounds.X - 50,
            probeTarget.Bounds.Y - 50,
            probeTarget.Bounds.Width + 100,
            probeTarget.Bounds.Height + 100);
        VisibleLayoutElements culled = layout.GetVisibleElements(localRegion);
        await Assert.That(culled.Nodes.Count > 0 && culled.Nodes.Count < 50).IsTrue();
        await Assert.That(culled.Nodes.Any(n => n.Id == probeTarget.Id)).IsTrue();
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

    private static VisualNode CreateVisualNode(PositionedNode positioned) =>
        new(
            Id: positioned.Id,
            Kind: VisualNodeKind.Operator,
            GraphNode: null,
            BoundaryPort: null,
            Title: positioned.Id,
            Subtitle: null,
            Category: OperatorCategory.Layer,
            CollapsedParameters: [],
            FusedActivationBadge: null,
            Layout: positioned);

    private static VisualEdge CreateVisualEdge(
        string id,
        string sourceId,
        string targetId,
        IReadOnlyList<LayoutPoint> points,
        PositionedEdge? existingPositioned = null)
    {
        TensorEdge tensor = new(
            Name: id,
            Kind: TensorKind.Activation,
            Descriptor: new TensorDescriptor(DataType.Float32, null));

        PositionedEdge positioned = existingPositioned ?? new PositionedEdge(
            Id: id,
            SourceNodeId: sourceId,
            TargetNodeId: targetId,
            SourcePortId: null,
            TargetPortId: null,
            IsReversed: false,
            Points: points,
            BezierSegments: [],
            LabelAnchor: null,
            Label: null);

        return new VisualEdge(
            Id: id,
            Tensor: tensor,
            ShapeLabel: string.Empty,
            IsQuantized: false,
            Layout: positioned);
    }
}
