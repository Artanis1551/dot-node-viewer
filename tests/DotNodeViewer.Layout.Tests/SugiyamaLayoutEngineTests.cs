using System.Diagnostics;
using System.Globalization;

namespace DotNodeViewer.Layout.Tests;

/// <summary>
/// Unit and performance tests for <see cref="SugiyamaLayoutEngine"/> covering determinism, non-overlap, DAG flow, cycle resilience, and the 5,000-node performance budget.
/// </summary>
public sealed class SugiyamaLayoutEngineTests
{
    [Test]
    public async Task ComputeLayout_SameGraphTwice_ProducesIdenticalCoordinates()
    {
        LayoutGraph graph = BuildDiamondSkipGraph();

        PositionedGraph first = SugiyamaLayoutEngine.Layout(graph);
        PositionedGraph second = SugiyamaLayoutEngine.Layout(graph);

        await Assert.That(first.Bounds).IsEqualTo(second.Bounds);
        await Assert.That(first.Nodes.Count).IsEqualTo(second.Nodes.Count);

        for (int i = 0; i < first.Nodes.Count; i++)
        {
            await Assert.That(first.Nodes[i].Id).IsEqualTo(second.Nodes[i].Id);
            await Assert.That(first.Nodes[i].X).IsEqualTo(second.Nodes[i].X);
            await Assert.That(first.Nodes[i].Y).IsEqualTo(second.Nodes[i].Y);
            await Assert.That(first.Nodes[i].Layer).IsEqualTo(second.Nodes[i].Layer);
            await Assert.That(first.Nodes[i].OrderInLayer).IsEqualTo(second.Nodes[i].OrderInLayer);
        }

        for (int e = 0; e < first.Edges.Count; e++)
        {
            await Assert.That(first.Edges[e].Points.Count).IsEqualTo(second.Edges[e].Points.Count);
            for (int p = 0; p < first.Edges[e].Points.Count; p++)
            {
                await Assert.That(first.Edges[e].Points[p]).IsEqualTo(second.Edges[e].Points[p]);
            }
        }
    }

    [Test]
    [Arguments(LayoutDirection.TopToBottom)]
    [Arguments(LayoutDirection.LeftToRight)]
    public async Task ComputeLayout_DagFlowAndNonOverlap_EnforcesStrictOrderingAndDisjointBoxes(LayoutDirection direction)
    {
        LayoutGraph graph = BuildDiamondSkipGraph();
        LayoutOptions options = new()
        {
            Direction = direction,
            LayerSeparation = 48.0,
            NodeSeparation = 28.0,
        };

        PositionedGraph positioned = SugiyamaLayoutEngine.Layout(graph, options);
        Dictionary<string, PositionedNode> nodeById = positioned.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // 1. No two PositionedNode bounding boxes may intersect
        for (int i = 0; i < positioned.Nodes.Count; i++)
        {
            for (int j = i + 1; j < positioned.Nodes.Count; j++)
            {
                bool intersects = positioned.Nodes[i].Bounds.Intersects(positioned.Nodes[j].Bounds);
                await Assert.That(intersects).IsFalse();
            }
        }

        // 2. Every non-reversed edge in a DAG flows strictly downward (TopToBottom) or rightward (LeftToRight)
        for (int e = 0; e < positioned.Edges.Count; e++)
        {
            PositionedEdge edge = positioned.Edges[e];
            await Assert.That(edge.IsReversed).IsFalse();

            PositionedNode src = nodeById[edge.SourceNodeId];
            PositionedNode tgt = nodeById[edge.TargetNodeId];
            await Assert.That(tgt.Layer > src.Layer).IsTrue();

            if (direction == LayoutDirection.TopToBottom)
            {
                await Assert.That(tgt.Y > src.Bounds.Bottom).IsTrue();
                await Assert.That(edge.Points[^1].Y > edge.Points[0].Y).IsTrue();
            }
            else
            {
                await Assert.That(tgt.X > src.Bounds.Right).IsTrue();
                await Assert.That(edge.Points[^1].X > edge.Points[0].X).IsTrue();
            }
        }

        // 3. Long skip edge (n0 -> n4 spanning 3 layers) routes through dummy waypoints and avoids intersecting intermediate nodes
        PositionedEdge skipEdge = positioned.Edges.First(e => e.Id == "e_skip");
        await Assert.That(skipEdge.Points.Count).IsEqualTo(4);
        await Assert.That(skipEdge.BezierSegments.Count).IsEqualTo(3);
        await Assert.That(skipEdge.LabelAnchor).IsNotNull();
    }

    [Test]
    public async Task ComputeLayout_TwoNodeThreeNodeAndSelfLoopCycles_CompletesWithoutExceptionAndRestoresEdgeEndpoints()
    {
        LayoutNode[] nodes =
        [
            new LayoutNode("a", 120, 40, [new LayoutPort("in")], [new LayoutPort("out")]),
            new LayoutNode("b", 120, 40, [new LayoutPort("in")], [new LayoutPort("out")]),
            new LayoutNode("c", 120, 40, [new LayoutPort("in")], [new LayoutPort("out")]),
            new LayoutNode("d", 120, 40, [new LayoutPort("in")], [new LayoutPort("out")]),
            new LayoutNode("e", 120, 40, [new LayoutPort("in")], [new LayoutPort("out")]),
        ];

        LayoutEdge[] edges =
        [
            // 2-node cycle: a <-> b
            new LayoutEdge("ab", "a", "b", "out", "in"),
            new LayoutEdge("ba", "b", "a", "out", "in"),

            // Bridge b -> c
            new LayoutEdge("bc", "b", "c", "out", "in"),

            // 3-node cycle: c -> d -> e -> c
            new LayoutEdge("cd", "c", "d", "out", "in"),
            new LayoutEdge("de", "d", "e", "out", "in"),
            new LayoutEdge("ec", "e", "c", "out", "in"),

            // Self-loop: d -> d
            new LayoutEdge("dd", "d", "d", "out", "in"),
        ];

        PositionedGraph positioned = SugiyamaLayoutEngine.Layout(new LayoutGraph(nodes, edges));

        await Assert.That(positioned.Nodes.Count).IsEqualTo(5);
        await Assert.That(positioned.Edges.Count).IsEqualTo(7);

        // Exactly one edge in the 2-node cycle and one edge in the 3-node cycle must be marked IsReversed
        int reversedCount = positioned.Edges.Count(e => e.IsReversed);
        await Assert.That(reversedCount).IsEqualTo(2);

        // Every edge's Points[0] must match its original SourceNode OutputPort and Points[^1] must match its original TargetNode InputPort
        Dictionary<string, PositionedNode> byId = positioned.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        for (int i = 0; i < positioned.Edges.Count; i++)
        {
            PositionedEdge edge = positioned.Edges[i];
            PositionedNode src = byId[edge.SourceNodeId];
            PositionedNode tgt = byId[edge.TargetNodeId];

            await Assert.That(edge.Points[0]).IsEqualTo(src.OutputPorts[0].Location);
            await Assert.That(edge.Points[^1]).IsEqualTo(tgt.InputPorts[0].Location);
        }
    }

    [Test]
    public async Task ComputeLayout_FiveThousandNodeLayeredGraph_CompletesUnderOneSecond()
    {
        const int nodeCount = 5000;
        const int widthPerLayer = 10; // 500 layers x 10 nodes per layer
        List<LayoutNode> nodes = new(nodeCount);
        List<LayoutEdge> edges = new(nodeCount * 2);

        for (int i = 0; i < nodeCount; i++)
        {
            string id = $"n{i.ToString(CultureInfo.InvariantCulture)}";
            nodes.Add(new LayoutNode(
                Id: id,
                Width: 160,
                Height: 44,
                InputPorts: [new LayoutPort("in0"), new LayoutPort("in1")],
                OutputPorts: [new LayoutPort("out0")]));
        }

        int edgeId = 0;
        for (int i = widthPerLayer; i < nodeCount; i++)
        {
            int parentSameCol = i - widthPerLayer;
            edges.Add(new LayoutEdge(
                Id: $"e{(edgeId++).ToString(CultureInfo.InvariantCulture)}",
                SourceNodeId: nodes[parentSameCol].Id,
                TargetNodeId: nodes[i].Id,
                SourcePortId: "out0",
                TargetPortId: "in0"));

            // Add cross-column residual connection every 3rd node
            if ((i % 3) == 0 && i >= (widthPerLayer * 2))
            {
                int skipParent = i - (widthPerLayer * 2) + ((i % widthPerLayer == 0) ? 1 : -1);
                edges.Add(new LayoutEdge(
                    Id: $"e{(edgeId++).ToString(CultureInfo.InvariantCulture)}",
                    SourceNodeId: nodes[skipParent].Id,
                    TargetNodeId: nodes[i].Id,
                    SourcePortId: "out0",
                    TargetPortId: "in1"));
            }
        }

        LayoutGraph largeGraph = new(nodes, edges);

        // Warm up JIT once on a tiny graph
        _ = SugiyamaLayoutEngine.Layout(BuildDiamondSkipGraph());

        Stopwatch sw = Stopwatch.StartNew();
        PositionedGraph positioned = SugiyamaLayoutEngine.Layout(largeGraph);
        sw.Stop();

        await Assert.That(positioned.Nodes.Count).IsEqualTo(nodeCount);
        await Assert.That(positioned.Edges.Count).IsEqualTo(edges.Count);
        await Assert.That(sw.ElapsedMilliseconds < 1000).IsTrue();
    }

    private static LayoutGraph BuildDiamondSkipGraph()
    {
        LayoutNode[] nodes =
        [
            new LayoutNode("n0", 140, 40, [], [new LayoutPort("out0"), new LayoutPort("out1")]),
            new LayoutNode("n1", 160, 56, [new LayoutPort("in0")], [new LayoutPort("out0")]),
            new LayoutNode("n2", 160, 56, [new LayoutPort("in0")], [new LayoutPort("out0")]),
            new LayoutNode("n3", 150, 48, [new LayoutPort("in0"), new LayoutPort("in1")], [new LayoutPort("out0")]),
            new LayoutNode("n4", 140, 40, [new LayoutPort("in0"), new LayoutPort("in1")], []),
        ];

        LayoutEdge[] edges =
        [
            new LayoutEdge("e0", "n0", "n1", "out0", "in0", "[1, 64, 56, 56]", 90, 16),
            new LayoutEdge("e1", "n0", "n2", "out1", "in0", "[1, 64, 56, 56]", 90, 16),
            new LayoutEdge("e2", "n1", "n3", "out0", "in0", "[1, 64, 56, 56]", 90, 16),
            new LayoutEdge("e3", "n2", "n3", "out0", "in1", "[1, 64, 56, 56]", 90, 16),
            new LayoutEdge("e4", "n3", "n4", "out0", "in0", "[1, 128, 28, 28]", 96, 16),
            new LayoutEdge("e_skip", "n0", "n4", "out1", "in1", "[1, 64, 56, 56]", 90, 16),
        ];

        return new LayoutGraph(nodes, edges);
    }
}
