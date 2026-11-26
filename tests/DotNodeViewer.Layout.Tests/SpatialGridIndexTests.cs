using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Layout.Tests;

/// <summary>
/// Unit tests for <see cref="SpatialGridIndex"/> verifying <c>O(1)</c> spatial queries, region culling,
/// inclusive edge intersection, extent clamping, and zero-allocation buffer reuse.
/// </summary>
public sealed class SpatialGridIndexTests
{
    [Test]
    public async Task EmptyIndex_ReturnsNullAndZeroCount()
    {
        SpatialGridIndex index = SpatialGridIndex.Empty;

        await Assert.That(index.Count).IsEqualTo(0);
        await Assert.That(index.EdgeCount).IsEqualTo(0);
        await Assert.That(index.FindNodeAt(100, 100)).IsNull();
        await Assert.That(index.FindNodesInRegion(0, 0, 500, 500).Count).IsEqualTo(0);
        await Assert.That(index.FindEdgesInRegion(new BoundingBox(0, 0, 500, 500)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task FindNodeAt_InsideCornerOutsideAndNonFinite_ReturnsExpectedNode()
    {
        VisualNode node = CreateNode("node:0:Conv_1", 100, 200, 160, 80);
        SpatialGridIndex index = new([node], cellSize: 128.0);

        await Assert.That(index.Count).IsEqualTo(1);

        // Point strictly inside
        VisualNode? hitInside = index.FindNodeAt(150, 240);
        await Assert.That(hitInside).IsNotNull();
        await Assert.That(hitInside!.Id).IsEqualTo("node:0:Conv_1");

        // Top-left corner boundary
        VisualNode? hitCorner = index.FindNodeAt(100, 200);
        await Assert.That(hitCorner).IsNotNull();
        await Assert.That(hitCorner!.Id).IsEqualTo("node:0:Conv_1");

        // Points outside & non-finite coordinates
        await Assert.That(index.FindNodeAt(90, 200)).IsNull();
        await Assert.That(index.FindNodeAt(270, 200)).IsNull();
        await Assert.That(index.FindNodeAt(150, 300)).IsNull();
        await Assert.That(index.FindNodeAt(double.NaN, 240)).IsNull();
        await Assert.That(index.FindNodeAt(150, double.PositiveInfinity)).IsNull();
    }

    [Test]
    public async Task FindNodeAt_OverlappingNodes_ReturnsLaterNode()
    {
        VisualNode back = CreateNode("n_back", 0, 0, 120, 80);
        VisualNode front = CreateNode("n_front", 40, 20, 120, 80);

        SpatialGridIndex index = new([back, front], cellSize: 64.0);

        VisualNode? hitOverlap = index.FindNodeAt(60, 40);
        await Assert.That(hitOverlap).IsNotNull();
        await Assert.That(hitOverlap!.Id).IsEqualTo("n_front");

        VisualNode? hitBackOnly = index.FindNodeAt(10, 10);
        await Assert.That(hitBackOnly).IsNotNull();
        await Assert.That(hitBackOnly!.Id).IsEqualTo("n_back");
    }

    [Test]
    public async Task FindNodesInRegion_ReturnsOnlyIntersectingNodesWithoutDuplicates()
    {
        VisualNode node1 = CreateNode("n1", 0, 0, 100, 100);
        VisualNode node2 = CreateNode("n2", 200, 0, 100, 100);
        VisualNode node3 = CreateNode("n3", 500, 500, 100, 100);

        SpatialGridIndex index = new([node1, node2, node3], cellSize: 64.0);

        // Region covering n1 (multi-cell) and part of n2
        List<VisualNode> hits = index.FindNodesInRegion(-10, -10, 250, 150);

        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits.Select(n => n.Id)).Contains("n1");
        await Assert.That(hits.Select(n => n.Id)).Contains("n2");
        await Assert.That(hits.Select(n => n.Id)).DoesNotContain("n3");
    }

    [Test]
    public async Task FindEdgesInRegion_StraightAxisAlignedAndEmptyEdges_HandledAccurately()
    {
        // Straight vertical edge (Width == 0) spanning multiple grid cells
        VisualEdge vertical = CreateEdge(
            "e_vert",
            "n1",
            "n2",
            [new LayoutPoint(120, 50), new LayoutPoint(120, 350)]);

        // Straight horizontal edge (Height == 0)
        VisualEdge horizontal = CreateEdge(
            "e_horiz",
            "n2",
            "n3",
            [new LayoutPoint(100, 400), new LayoutPoint(450, 400)]);

        // Empty-geometry edge (should be ignored)
        VisualEdge empty = CreateEdge("e_empty", "n1", "n3", []);

        SpatialGridIndex index = new([], [vertical, horizontal, empty], cellSize: 64.0);
        await Assert.That(index.EdgeCount).IsEqualTo(3);

        // Region touching vertical edge at X = 120 (zero-width edge still intersects!)
        List<VisualEdge> vertHits = index.FindEdgesInRegion(new BoundingBox(120, 100, 80, 80));
        await Assert.That(vertHits.Count).IsEqualTo(1);
        await Assert.That(vertHits[0].Id).IsEqualTo("e_vert");

        // Region touching horizontal edge at Y = 400 (zero-height edge still intersects!)
        List<VisualEdge> horizHits = index.FindEdgesInRegion(new BoundingBox(200, 360, 100, 40));
        await Assert.That(horizHits.Count).IsEqualTo(1);
        await Assert.That(horizHits[0].Id).IsEqualTo("e_horiz");

        // Region at origin should not match empty edge
        List<VisualEdge> originHits = index.FindEdgesInRegion(new BoundingBox(-10, -10, 20, 20));
        await Assert.That(originHits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RegionQueries_DegenerateAndExtremeViewports_ClampAndReturnExpectedResults()
    {
        VisualNode node = CreateNode("n1", 50, 50, 100, 60);
        VisualEdge edge = CreateEdge("e1", "n1", "n1", [new LayoutPoint(100, 50), new LayoutPoint(100, 110)]);
        SpatialGridIndex index = new([node], [edge], cellSize: 80.0);

        List<VisualNode> nodeDest = [node];
        List<VisualEdge> edgeDest = [edge];

        // Negative dimensions & non-finite bounds clear destination and return empty
        index.FindNodesInRegion(new BoundingBox(0, 0, -10, 100), nodeDest);
        index.FindEdgesInRegion(new BoundingBox(double.NaN, 0, 100, 100), edgeDest);
        await Assert.That(nodeDest.Count).IsEqualTo(0);
        await Assert.That(edgeDest.Count).IsEqualTo(0);

        // Extremely large zoomed-out viewport clamps cleanly to indexed cell bounds without int overflow
        index.FindNodesInRegion(new BoundingBox(-1_000_000, -1_000_000, 2_000_000, 2_000_000), nodeDest);
        index.FindEdgesInRegion(new BoundingBox(-1_000_000, -1_000_000, 2_000_000, 2_000_000), edgeDest);
        await Assert.That(nodeDest.Count).IsEqualTo(1);
        await Assert.That(edgeDest.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FindNodesAndEdgesInRegion_WithReusableBuffers_AllocatesZeroBytesWhenWarm()
    {
        List<VisualNode> nodes = new(64);
        List<VisualEdge> edges = new(64);
        for (int i = 0; i < 64; i++)
        {
            double x = (i % 8) * 140.0;
            double y = (i / 8) * 120.0;
            nodes.Add(CreateNode($"n{i}", x, y, 110, 60));
            edges.Add(CreateEdge($"e{i}", $"n{i}", $"n{i}", [new LayoutPoint(x + 55, y), new LayoutPoint(x + 55, y + 60)]));
        }

        SpatialGridIndex index = new(nodes, edges, cellSize: 160.0);
        BoundingBox region = new(0, 0, 800, 600);
        List<VisualNode> nodeBuffer = new(64);
        List<VisualEdge> edgeBuffer = new(64);

        // Warm up JIT and ensure List capacities are established before measuring
        index.FindNodesInRegion(region, nodeBuffer);
        index.FindEdgesInRegion(region, edgeBuffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        index.FindNodesInRegion(region, nodeBuffer);
        index.FindEdgesInRegion(region, edgeBuffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0L);
        await Assert.That(nodeBuffer.Count > 0).IsTrue();
        await Assert.That(edgeBuffer.Count > 0).IsTrue();
    }

    private static VisualNode CreateNode(string id, double x, double y, double width, double height)
    {
        PositionedNode layout = new(
            Id: id,
            X: x,
            Y: y,
            Width: width,
            Height: height,
            Layer: 0,
            OrderInLayer: 0,
            InputPorts: [],
            OutputPorts: []);

        return new VisualNode(
            Id: id,
            Kind: VisualNodeKind.Operator,
            GraphNode: null,
            BoundaryPort: null,
            Title: id,
            Subtitle: null,
            Category: OperatorCategory.Layer,
            CollapsedParameters: [],
            FusedActivationBadge: null,
            Layout: layout);
    }

    private static VisualEdge CreateEdge(string id, string sourceId, string targetId, IReadOnlyList<LayoutPoint> points)
    {
        TensorEdge tensor = new(
            Name: id,
            Kind: TensorKind.Activation,
            Descriptor: new TensorDescriptor(DataType.Float32, null));

        PositionedEdge positioned = new(
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
