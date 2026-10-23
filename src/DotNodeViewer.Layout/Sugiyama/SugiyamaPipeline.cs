namespace DotNodeViewer.Layout.Sugiyama;

/// <summary>
/// Holds the internal mutable state of the Sugiyama layered graph layout pipeline across
/// cycle removal, layer ranking, dummy-node normalization, crossing minimization, coordinate assignment, and edge routing.
/// </summary>
internal sealed class SugiyamaPipeline
{
    private readonly LayoutGraph _input;
    private readonly LayoutOptions _options;
    private readonly int _realNodeCount;
    private readonly Dictionary<string, int> _nodeIndexById;

    // Real + dummy vertex properties (evaluated in canonical TopToBottom space: Width = secondary axis, Height = primary flow axis)
    private readonly List<double> _vertexWidth;
    private readonly List<double> _vertexHeight;
    private readonly List<int> _vertexRank;
    private readonly List<int> _vertexOrder;
    private readonly List<double> _vertexX; // Center X in canonical TopToBottom space
    private readonly List<double> _vertexY; // Center Y in canonical TopToBottom space

    // Original edge properties (size = _input.Edges.Count)
    private readonly int[] _edgeOrigSource;
    private readonly int[] _edgeOrigTarget;
    private readonly int[] _edgeDagSource;
    private readonly int[] _edgeDagTarget;
    private readonly double[] _edgeSourcePortFactor; // [-0.45 .. +0.45] relative to vertex width
    private readonly double[] _edgeTargetPortFactor;
    private readonly bool[] _edgeIsReversed;
    private readonly bool[] _edgeIsSelfLoop;
    private readonly int[][] _edgeChainVertices; // [u, dummy1, dummy2, ..., v] in DAG order

    // Normalized unit-length segments between adjacent ranks
    private readonly List<SegmentEdge> _segments = [];
    private List<int>[] _downAdj = [];
    private List<int>[] _upAdj = [];
    private int[][] _layers = [];

    public SugiyamaPipeline(LayoutGraph input, LayoutOptions options)
    {
        _input = input;
        _options = options;
        _realNodeCount = input.Nodes.Count;
        _nodeIndexById = new Dictionary<string, int>(_realNodeCount, StringComparer.Ordinal);

        _vertexWidth = new List<double>(_realNodeCount * 2);
        _vertexHeight = new List<double>(_realNodeCount * 2);
        _vertexRank = new List<int>(_realNodeCount * 2);
        _vertexOrder = new List<int>(_realNodeCount * 2);
        _vertexX = new List<double>(_realNodeCount * 2);
        _vertexY = new List<double>(_realNodeCount * 2);

        bool isLeftToRight = options.Direction == LayoutDirection.LeftToRight;
        for (int i = 0; i < _realNodeCount; i++)
        {
            LayoutNode node = input.Nodes[i];
            _nodeIndexById[node.Id] = i;

            // In canonical TopToBottom space, Width is across the layer and Height is along the rank direction.
            double w = Math.Max(1.0, isLeftToRight ? node.Height : node.Width);
            double h = Math.Max(1.0, isLeftToRight ? node.Width : node.Height);
            _vertexWidth.Add(w);
            _vertexHeight.Add(h);
            _vertexRank.Add(0);
            _vertexOrder.Add(0);
            _vertexX.Add(0.0);
            _vertexY.Add(0.0);
        }

        int edgeCount = input.Edges.Count;
        _edgeOrigSource = new int[edgeCount];
        _edgeOrigTarget = new int[edgeCount];
        _edgeDagSource = new int[edgeCount];
        _edgeDagTarget = new int[edgeCount];
        _edgeSourcePortFactor = new double[edgeCount];
        _edgeTargetPortFactor = new double[edgeCount];
        _edgeIsReversed = new bool[edgeCount];
        _edgeIsSelfLoop = new bool[edgeCount];
        _edgeChainVertices = new int[edgeCount][];

        for (int e = 0; e < edgeCount; e++)
        {
            LayoutEdge edge = input.Edges[e];
            if (!_nodeIndexById.TryGetValue(edge.SourceNodeId, out int u))
            {
                u = -1;
            }

            if (!_nodeIndexById.TryGetValue(edge.TargetNodeId, out int v))
            {
                v = -1;
            }

            _edgeOrigSource[e] = u;
            _edgeOrigTarget[e] = v;
            _edgeDagSource[e] = u;
            _edgeDagTarget[e] = v;
            _edgeIsSelfLoop[e] = u < 0 || v < 0 || u == v;
            _edgeChainVertices[e] = [];

            if (u >= 0)
            {
                _edgeSourcePortFactor[e] = ResolvePortRelativeFactor(input.Nodes[u].OutputPorts, edge.SourcePortId);
            }

            if (v >= 0)
            {
                _edgeTargetPortFactor[e] = ResolvePortRelativeFactor(input.Nodes[v].InputPorts, edge.TargetPortId);
            }
        }
    }

    public LayoutGraph Input => _input;

    public LayoutOptions Options => _options;

    public int RealNodeCount => _realNodeCount;

    public int TotalVertexCount => _vertexWidth.Count;

    public int[][] Layers => _layers;

    public List<double> VertexWidth => _vertexWidth;

    public List<double> VertexHeight => _vertexHeight;

    public List<int> VertexRank => _vertexRank;

    public List<int> VertexOrder => _vertexOrder;

    public List<double> VertexX => _vertexX;

    public List<double> VertexY => _vertexY;

    public int[] EdgeOrigSource => _edgeOrigSource;

    public int[] EdgeOrigTarget => _edgeOrigTarget;

    public bool[] EdgeIsReversed => _edgeIsReversed;

    public bool[] EdgeIsSelfLoop => _edgeIsSelfLoop;

    public int[][] EdgeChainVertices => _edgeChainVertices;

    public List<SegmentEdge> Segments => _segments;

    public List<int>[] DownAdj => _downAdj;

    public List<int>[] UpAdj => _upAdj;

    /// <summary>
    /// Step 1: Iterative DFS cycle removal that reverses back-edges so the working graph is a strict DAG.
    /// </summary>
    public void RemoveCycles()
    {
        if (_realNodeCount <= 1)
        {
            return;
        }

        List<int>[] outEdges = new List<int>[_realNodeCount];
        for (int i = 0; i < _realNodeCount; i++)
        {
            outEdges[i] = [];
        }

        for (int e = 0; e < _edgeOrigSource.Length; e++)
        {
            if (!_edgeIsSelfLoop[e])
            {
                outEdges[_edgeOrigSource[e]].Add(e);
            }
        }

        // State: 0 = unvisited, 1 = on active DFS stack, 2 = completed
        byte[] state = new byte[_realNodeCount];
        int[] dfsOrder = new int[_realNodeCount];
        int dfsClock = 0;
        Stack<(int Node, int EdgeListIndex)> stack = new(_realNodeCount);

        for (int start = 0; start < _realNodeCount; start++)
        {
            if (state[start] != 0)
            {
                continue;
            }

            state[start] = 1;
            dfsOrder[start] = ++dfsClock;
            stack.Push((start, 0));

            while (stack.Count > 0)
            {
                (int u, int edgeIdx) = stack.Pop();
                List<int> incident = outEdges[u];

                if (edgeIdx < incident.Count)
                {
                    stack.Push((u, edgeIdx + 1));
                    int e = incident[edgeIdx];
                    int v = _edgeOrigTarget[e];

                    if (state[v] == 1)
                    {
                        // Back-edge to an ancestor on the active DFS stack -> reverse!
                        _edgeIsReversed[e] = true;
                        _edgeDagSource[e] = v;
                        _edgeDagTarget[e] = u;
                    }
                    else if (state[v] == 0)
                    {
                        state[v] = 1;
                        dfsOrder[v] = ++dfsClock;
                        stack.Push((v, 0));
                    }
                }
                else
                {
                    state[u] = 2;
                }
            }
        }
    }

    /// <summary>
    /// Step 2: Assigns non-negative integer ranks to all real nodes via longest-path initialization followed by tight-tree slack refinement.
    /// </summary>
    public void AssignLayers()
    {
        if (_realNodeCount == 0)
        {
            return;
        }

        List<int>[] dagOut = new List<int>[_realNodeCount];
        List<int>[] dagIn = new List<int>[_realNodeCount];
        int[] inDegree = new int[_realNodeCount];

        for (int i = 0; i < _realNodeCount; i++)
        {
            dagOut[i] = [];
            dagIn[i] = [];
        }

        for (int e = 0; e < _edgeDagSource.Length; e++)
        {
            if (_edgeIsSelfLoop[e])
            {
                continue;
            }

            int u = _edgeDagSource[e];
            int v = _edgeDagTarget[e];
            dagOut[u].Add(v);
            dagIn[v].Add(u);
            inDegree[v]++;
        }

        // Kahn's topological sort + longest-path initial ranking
        Queue<int> queue = new(_realNodeCount);
        for (int i = 0; i < _realNodeCount; i++)
        {
            if (inDegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        int[] topoOrder = new int[_realNodeCount];
        int topoCount = 0;

        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            topoOrder[topoCount++] = u;
            int nextRank = _vertexRank[u] + 1;

            List<int> children = dagOut[u];
            for (int i = 0; i < children.Count; i++)
            {
                int v = children[i];
                if (nextRank > _vertexRank[v])
                {
                    _vertexRank[v] = nextRank;
                }

                if (--inDegree[v] == 0)
                {
                    queue.Enqueue(v);
                }
            }
        }

        // Safety fallback if any residual cycle survived (should not happen after RemoveCycles)
        if (topoCount < _realNodeCount)
        {
            for (int i = 0; i < _realNodeCount; i++)
            {
                if (inDegree[i] > 0)
                {
                    topoOrder[topoCount++] = i;
                }
            }
        }

        // Tight-tree / slack-minimization pass: pull nodes with slack downward/upward to minimize total edge span
        int maxIter = Math.Max(1, _options.RankingRefinementIterations);
        for (int iter = 0; iter < maxIter; iter++)
        {
            bool changed = false;

            // Bottom-up pass: pull source/intermediate nodes closer to their children when out-degree > in-degree
            for (int t = topoCount - 1; t >= 0; t--)
            {
                int u = topoOrder[t];
                List<int> children = dagOut[u];
                List<int> parents = dagIn[u];
                if (children.Count == 0)
                {
                    continue;
                }

                // Only pull down if u has no parents (e.g., a GraphInput or secondary branch start)
                // or if pulling u down strictly reduces total edge span (children.Count > parents.Count)
                if (parents.Count == 0 || children.Count > parents.Count)
                {
                    int minChildRank = int.MaxValue;
                    for (int i = 0; i < children.Count; i++)
                    {
                        int r = _vertexRank[children[i]];
                        if (r < minChildRank)
                        {
                            minChildRank = r;
                        }
                    }

                    int targetRank = minChildRank - 1;
                    if (targetRank > _vertexRank[u])
                    {
                        _vertexRank[u] = targetRank;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        // Normalize minimum rank to 0
        int minRank = int.MaxValue;
        for (int i = 0; i < _realNodeCount; i++)
        {
            if (_vertexRank[i] < minRank)
            {
                minRank = _vertexRank[i];
            }
        }

        if (minRank > 0 && minRank != int.MaxValue)
        {
            for (int i = 0; i < _realNodeCount; i++)
            {
                _vertexRank[i] -= minRank;
            }
        }
    }

    /// <summary>
    /// Step 3: Inserts dummy vertices along edges spanning more than one rank (<c>rankDelta &gt; 1</c>) and constructs the initial layer lists.
    /// </summary>
    public void NormalizeAndBuildLayers()
    {
        if (_realNodeCount == 0)
        {
            _layers = [];
            return;
        }

        int maxRank = 0;
        for (int i = 0; i < _realNodeCount; i++)
        {
            if (_vertexRank[i] > maxRank)
            {
                maxRank = _vertexRank[i];
            }
        }

        double dummyW = Math.Max(4.0, _options.DummyNodeWidth);

        for (int e = 0; e < _edgeDagSource.Length; e++)
        {
            if (_edgeIsSelfLoop[e])
            {
                continue;
            }

            int u = _edgeDagSource[e];
            int v = _edgeDagTarget[e];
            int rankU = _vertexRank[u];
            int rankV = _vertexRank[v];
            int span = rankV - rankU;

            double srcPortFactor = _edgeIsReversed[e] ? _edgeTargetPortFactor[e] : _edgeSourcePortFactor[e];
            double tgtPortFactor = _edgeIsReversed[e] ? _edgeSourcePortFactor[e] : _edgeTargetPortFactor[e];

            if (span <= 1)
            {
                _edgeChainVertices[e] = [u, v];
                _segments.Add(new SegmentEdge(u, v, srcPortFactor, tgtPortFactor));
                continue;
            }

            int[] chain = new int[span + 1];
            chain[0] = u;
            chain[span] = v;

            int prev = u;
            double prevPortFactor = srcPortFactor;

            for (int r = rankU + 1; r < rankV; r++)
            {
                int dummyId = _vertexWidth.Count;
                _vertexWidth.Add(dummyW);
                _vertexHeight.Add(0.0);
                _vertexRank.Add(r);
                _vertexOrder.Add(0);
                _vertexX.Add(0.0);
                _vertexY.Add(0.0);

                chain[r - rankU] = dummyId;
                _segments.Add(new SegmentEdge(prev, dummyId, prevPortFactor, 0.0));
                prev = dummyId;
                prevPortFactor = 0.0;
            }

            _segments.Add(new SegmentEdge(prev, v, prevPortFactor, tgtPortFactor));
            _edgeChainVertices[e] = chain;
        }

        int totalVertices = _vertexWidth.Count;
        List<int>[] layerLists = new List<int>[maxRank + 1];
        for (int r = 0; r <= maxRank; r++)
        {
            layerLists[r] = [];
        }

        for (int v = 0; v < totalVertices; v++)
        {
            int r = _vertexRank[v];
            _vertexOrder[v] = layerLists[r].Count;
            layerLists[r].Add(v);
        }

        _layers = new int[maxRank + 1][];
        for (int r = 0; r <= maxRank; r++)
        {
            _layers[r] = [.. layerLists[r]];
        }

        // Build per-vertex up/down segment adjacency lists
        _downAdj = new List<int>[totalVertices];
        _upAdj = new List<int>[totalVertices];
        for (int v = 0; v < totalVertices; v++)
        {
            _downAdj[v] = [];
            _upAdj[v] = [];
        }

        for (int s = 0; s < _segments.Count; s++)
        {
            SegmentEdge seg = _segments[s];
            _downAdj[seg.UpperVertex].Add(s);
            _upAdj[seg.LowerVertex].Add(s);
        }
    }

    /// <summary>
    /// Step 4: Minimizes edge crossings across layers using port-aware barycenter/median sweeps and adjacent-exchange transposition.
    /// </summary>
    public void MinimizeCrossings()
    {
        if (_layers.Length <= 1)
        {
            return;
        }

        int totalVertices = _vertexWidth.Count;
        double[] weights = new double[totalVertices];
        int sweeps = Math.Max(1, _options.CrossingSweepIterations);

        for (int iter = 0; iter < sweeps; iter++)
        {
            if ((iter & 1) == 0)
            {
                // Top-down sweep (layer 1 .. L-1)
                for (int r = 1; r < _layers.Length; r++)
                {
                    OrderLayerByBarycenter(_layers[r], useUpperNeighbors: true, weights);
                    TransposeAdjacentInLayer(_layers[r], useUpperNeighbors: true);
                }
            }
            else
            {
                // Bottom-up sweep (layer L-2 .. 0)
                for (int r = _layers.Length - 2; r >= 0; r--)
                {
                    OrderLayerByBarycenter(_layers[r], useUpperNeighbors: false, weights);
                    TransposeAdjacentInLayer(_layers[r], useUpperNeighbors: false);
                }
            }
        }
    }

    private static double ResolvePortRelativeFactor(IReadOnlyList<LayoutPort>? ports, string? portId)
    {
        if (ports is null || ports.Count <= 1 || string.IsNullOrEmpty(portId))
        {
            return 0.0;
        }

        for (int i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Id, portId, StringComparison.Ordinal))
            {
                double rel = ports[i].RelativeOffset;
                if (!double.IsNaN(rel))
                {
                    return (Math.Clamp(rel, 0.0, 1.0) - 0.5) * 0.8;
                }

                double t = (i + 1.0) / (ports.Count + 1.0);
                return (t - 0.5) * 0.8;
            }
        }

        return 0.0;
    }

    private void OrderLayerByBarycenter(int[] layer, bool useUpperNeighbors, double[] weights)
    {
        if (layer.Length <= 1)
        {
            return;
        }

        for (int i = 0; i < layer.Length; i++)
        {
            int v = layer[i];
            List<int> incidentSegments = useUpperNeighbors ? _upAdj[v] : _downAdj[v];
            if (incidentSegments.Count == 0)
            {
                weights[v] = _vertexOrder[v];
                continue;
            }

            double sum = 0.0;
            for (int k = 0; k < incidentSegments.Count; k++)
            {
                SegmentEdge seg = _segments[incidentSegments[k]];
                if (useUpperNeighbors)
                {
                    sum += _vertexOrder[seg.UpperVertex] + seg.UpperPortFactor + (seg.LowerPortFactor * 0.25);
                }
                else
                {
                    sum += _vertexOrder[seg.LowerVertex] + seg.LowerPortFactor + (seg.UpperPortFactor * 0.25);
                }
            }

            weights[v] = sum / incidentSegments.Count;
        }

        // Stable insertion sort on (weights[v], existing _vertexOrder[v]) for deterministic tie-breaking
        for (int i = 1; i < layer.Length; i++)
        {
            int keyVertex = layer[i];
            double keyWeight = weights[keyVertex];
            int j = i - 1;
            while (j >= 0 && weights[layer[j]] > keyWeight + 1e-9)
            {
                layer[j + 1] = layer[j];
                j--;
            }

            layer[j + 1] = keyVertex;
        }

        for (int i = 0; i < layer.Length; i++)
        {
            _vertexOrder[layer[i]] = i;
        }
    }

    private void TransposeAdjacentInLayer(int[] layer, bool useUpperNeighbors)
    {
        if (layer.Length <= 1 || layer.Length > 128)
        {
            // Skip O(k^2) pairwise transpose on huge layers to keep 5,000-node graphs sub-50ms
            return;
        }

        bool improved = true;
        int pass = 0;
        while (improved && pass < 2)
        {
            improved = false;
            pass++;
            for (int i = 0; i < layer.Length - 1; i++)
            {
                int v = layer[i];
                int w = layer[i + 1];
                int cBefore = CountPairCrossings(v, w, useUpperNeighbors);
                if (cBefore == 0)
                {
                    continue;
                }

                int cAfter = CountPairCrossings(w, v, useUpperNeighbors);
                if (cAfter < cBefore)
                {
                    layer[i] = w;
                    layer[i + 1] = v;
                    _vertexOrder[w] = i;
                    _vertexOrder[v] = i + 1;
                    improved = true;
                }
            }
        }
    }

    private int CountPairCrossings(int leftVertex, int rightVertex, bool useUpperNeighbors)
    {
        List<int> leftSegs = useUpperNeighbors ? _upAdj[leftVertex] : _downAdj[leftVertex];
        List<int> rightSegs = useUpperNeighbors ? _upAdj[rightVertex] : _downAdj[rightVertex];
        if (leftSegs.Count == 0 || rightSegs.Count == 0)
        {
            return 0;
        }

        int crossings = 0;
        for (int i = 0; i < leftSegs.Count; i++)
        {
            SegmentEdge sL = _segments[leftSegs[i]];
            double posL = useUpperNeighbors
                ? _vertexOrder[sL.UpperVertex] + sL.UpperPortFactor
                : _vertexOrder[sL.LowerVertex] + sL.LowerPortFactor;

            for (int j = 0; j < rightSegs.Count; j++)
            {
                SegmentEdge sR = _segments[rightSegs[j]];
                double posR = useUpperNeighbors
                    ? _vertexOrder[sR.UpperVertex] + sR.UpperPortFactor
                    : _vertexOrder[sR.LowerVertex] + sR.LowerPortFactor;

                if (posL > posR + 1e-6)
                {
                    crossings++;
                }
            }
        }

        return crossings;
    }

    /// <summary>
    /// Step 5: Assigns balanced, non-overlapping coordinates to all real and dummy vertices using
    /// Brandes–Köpf directional median alignment and block compaction.
    /// </summary>
    public void AssignCoordinates()
    {
        if (_layers.Length == 0 || _realNodeCount == 0)
        {
            return;
        }

        int numLayers = _layers.Length;
        double nodeSep = Math.Max(4.0, _options.NodeSeparation);
        double layerSep = Math.Max(8.0, _options.LayerSeparation);
        double margin = Math.Max(0.0, _options.Margin);

        // 1. Assign primary-axis (Y in TopToBottom) coordinates per layer
        double[] layerHeight = new double[numLayers];
        double[] layerCenterY = new double[numLayers];
        double currentTop = margin;

        for (int r = 0; r < numLayers; r++)
        {
            int[] layer = _layers[r];
            double maxH = 0.0;
            for (int i = 0; i < layer.Length; i++)
            {
                double h = _vertexHeight[layer[i]];
                if (h > maxH)
                {
                    maxH = h;
                }
            }

            layerHeight[r] = maxH;
            layerCenterY[r] = currentTop + (maxH * 0.5);
            for (int i = 0; i < layer.Length; i++)
            {
                _vertexY[layer[i]] = layerCenterY[r];
            }

            currentTop += maxH + layerSep;
        }

        // 2. Initialize secondary-axis (X in TopToBottom) coordinates centered around 0.0
        for (int r = 0; r < numLayers; r++)
        {
            int[] layer = _layers[r];
            if (layer.Length == 0)
            {
                continue;
            }

            double totalW = 0.0;
            for (int i = 0; i < layer.Length; i++)
            {
                totalW += _vertexWidth[layer[i]];
            }

            totalW += (layer.Length - 1) * nodeSep;
            double cursor = -totalW * 0.5;
            for (int i = 0; i < layer.Length; i++)
            {
                int v = layer[i];
                double w = _vertexWidth[v];
                _vertexX[v] = cursor + (w * 0.5);
                cursor += w + nodeSep;
            }
        }

        // 3. Brandes–Köpf / weighted-median directional alignment & compaction sweeps
        int totalVertices = _vertexWidth.Count;
        double[] desiredX = new double[totalVertices];
        List<double> neighborPositions = new(16);

        int coordIterations = Math.Max(4, _options.CrossingSweepIterations);
        for (int iter = 0; iter < coordIterations; iter++)
        {
            bool topDown = (iter & 1) == 0;
            bool leftToRightBias = (iter & 2) == 0;

            if (topDown)
            {
                for (int r = 1; r < numLayers; r++)
                {
                    AlignLayerToNeighbors(_layers[r], useUpper: true, includeOpposite: iter >= 2, neighborPositions, desiredX);
                    ProjectNonOverlapping(_layers[r], desiredX, nodeSep, leftToRightBias);
                }
            }
            else
            {
                for (int r = numLayers - 2; r >= 0; r--)
                {
                    AlignLayerToNeighbors(_layers[r], useUpper: false, includeOpposite: iter >= 2, neighborPositions, desiredX);
                    ProjectNonOverlapping(_layers[r], desiredX, nodeSep, leftToRightBias);
                }
            }
        }

        // Final top-down + bottom-up polish pass to straighten vertical runs
        for (int r = 1; r < numLayers; r++)
        {
            AlignLayerToNeighbors(_layers[r], useUpper: true, includeOpposite: true, neighborPositions, desiredX);
            ProjectNonOverlapping(_layers[r], desiredX, nodeSep, leftToRightBias: true);
        }

        // 4. Shift X so minimum left border starts at Margin
        double minLeft = double.PositiveInfinity;
        for (int v = 0; v < totalVertices; v++)
        {
            double left = _vertexX[v] - (_vertexWidth[v] * 0.5);
            if (left < minLeft)
            {
                minLeft = left;
            }
        }

        if (!double.IsInfinity(minLeft))
        {
            double shiftX = margin - minLeft;
            for (int v = 0; v < totalVertices; v++)
            {
                _vertexX[v] += shiftX;
            }
        }
    }

    /// <summary>
    /// Step 6: Constructs the final <see cref="PositionedGraph"/>, computing port anchors, routing polylines and cubic Bézier curves
    /// through dummy node chains, and restoring reversed feedback edges.
    /// </summary>
    public PositionedGraph BuildPositionedGraph()
    {
        double margin = Math.Max(0.0, _options.Margin);
        if (_realNodeCount == 0)
        {
            return new PositionedGraph([], [], new BoundingBox(0, 0, margin * 2, margin * 2), _options.Direction);
        }

        bool isLeftToRight = _options.Direction == LayoutDirection.LeftToRight;
        PositionedNode[] positionedNodes = new PositionedNode[_realNodeCount];
        Dictionary<string, LayoutPoint>[] inputPortCoords = new Dictionary<string, LayoutPoint>[_realNodeCount];
        Dictionary<string, LayoutPoint>[] outputPortCoords = new Dictionary<string, LayoutPoint>[_realNodeCount];

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxRight = double.NegativeInfinity;
        double maxBottom = double.NegativeInfinity;

        for (int i = 0; i < _realNodeCount; i++)
        {
            LayoutNode raw = _input.Nodes[i];

            // In LeftToRight mode, primary flow axis (Y in canonical space) becomes X, and secondary axis (X in canonical space) becomes Y.
            double centerX = isLeftToRight ? _vertexY[i] : _vertexX[i];
            double centerY = isLeftToRight ? _vertexX[i] : _vertexY[i];
            double width = raw.Width;
            double height = raw.Height;
            double left = centerX - (width * 0.5);
            double top = centerY - (height * 0.5);

            PositionedPort[] inPorts = BuildPositionedPorts(
                raw.InputPorts,
                left,
                top,
                width,
                height,
                isInput: true,
                isLeftToRight,
                out Dictionary<string, LayoutPoint> inMap);

            PositionedPort[] outPorts = BuildPositionedPorts(
                raw.OutputPorts,
                left,
                top,
                width,
                height,
                isInput: false,
                isLeftToRight,
                out Dictionary<string, LayoutPoint> outMap);

            inputPortCoords[i] = inMap;
            outputPortCoords[i] = outMap;

            positionedNodes[i] = new PositionedNode(
                Id: raw.Id,
                X: left,
                Y: top,
                Width: width,
                Height: height,
                Layer: _vertexRank[i],
                OrderInLayer: _vertexOrder[i],
                InputPorts: inPorts,
                OutputPorts: outPorts);

            if (left < minX)
            {
                minX = left;
            }

            if (top < minY)
            {
                minY = top;
            }

            if (left + width > maxRight)
            {
                maxRight = left + width;
            }

            if (top + height > maxBottom)
            {
                maxBottom = top + height;
            }
        }

        // Route all edges
        int edgeCount = _input.Edges.Count;
        PositionedEdge[] positionedEdges = new PositionedEdge[edgeCount];

        for (int e = 0; e < edgeCount; e++)
        {
            LayoutEdge rawEdge = _input.Edges[e];
            int origU = _edgeOrigSource[e];
            int origV = _edgeOrigTarget[e];

            if (origU < 0 || origV < 0)
            {
                positionedEdges[e] = new PositionedEdge(
                    rawEdge.Id,
                    rawEdge.SourceNodeId,
                    rawEdge.TargetNodeId,
                    rawEdge.SourcePortId,
                    rawEdge.TargetPortId,
                    IsReversed: false,
                    Points: [],
                    BezierSegments: [],
                    LabelAnchor: null,
                    Label: rawEdge.Label);
                continue;
            }

            LayoutPoint startPt = ResolvePortAnchor(
                positionedNodes[origU],
                outputPortCoords[origU],
                rawEdge.SourcePortId,
                isInput: false,
                isLeftToRight);

            LayoutPoint endPt = ResolvePortAnchor(
                positionedNodes[origV],
                inputPortCoords[origV],
                rawEdge.TargetPortId,
                isInput: true,
                isLeftToRight);

            List<LayoutPoint> points;
            if (origU == origV)
            {
                // Self-loop routing around the right/bottom side of the node
                PositionedNode selfNode = positionedNodes[origU];
                double loopOutX = selfNode.Bounds.Right + (_options.NodeSeparation * 0.6);
                double loopTopY = selfNode.Y + (selfNode.Height * 0.25);
                double loopBotY = selfNode.Y + (selfNode.Height * 0.75);
                points =
                [
                    startPt,
                    new LayoutPoint(loopOutX, loopBotY),
                    new LayoutPoint(loopOutX, loopTopY),
                    endPt,
                ];
            }
            else
            {
                int[] chain = _edgeChainVertices[e];
                bool reversed = _edgeIsReversed[e];
                points = new List<LayoutPoint>(chain.Length);
                points.Add(startPt);

                if (chain.Length > 2)
                {
                    if (!reversed)
                    {
                        for (int k = 1; k < chain.Length - 1; k++)
                        {
                            int dummyIdx = chain[k];
                            double dx = isLeftToRight ? _vertexY[dummyIdx] : _vertexX[dummyIdx];
                            double dy = isLeftToRight ? _vertexX[dummyIdx] : _vertexY[dummyIdx];
                            points.Add(new LayoutPoint(dx, dy));
                        }
                    }
                    else
                    {
                        for (int k = chain.Length - 2; k >= 1; k--)
                        {
                            int dummyIdx = chain[k];
                            double dx = isLeftToRight ? _vertexY[dummyIdx] : _vertexX[dummyIdx];
                            double dy = isLeftToRight ? _vertexX[dummyIdx] : _vertexY[dummyIdx];
                            points.Add(new LayoutPoint(dx, dy));
                        }
                    }
                }

                points.Add(endPt);
            }

            for (int p = 0; p < points.Count; p++)
            {
                LayoutPoint pt = points[p];
                if (pt.X < minX)
                {
                    minX = pt.X;
                }

                if (pt.Y < minY)
                {
                    minY = pt.Y;
                }

                if (pt.X > maxRight)
                {
                    maxRight = pt.X;
                }

                if (pt.Y > maxBottom)
                {
                    maxBottom = pt.Y;
                }
            }

            CubicBezierSegment[] beziers = BuildBezierSegments(points, isLeftToRight);
            LayoutPoint? labelAnchor = !string.IsNullOrEmpty(rawEdge.Label)
                ? ComputePolylineMidpoint(points)
                : null;

            positionedEdges[e] = new PositionedEdge(
                Id: rawEdge.Id,
                SourceNodeId: rawEdge.SourceNodeId,
                TargetNodeId: rawEdge.TargetNodeId,
                SourcePortId: rawEdge.SourcePortId,
                TargetPortId: rawEdge.TargetPortId,
                IsReversed: _edgeIsReversed[e],
                Points: points,
                BezierSegments: beziers,
                LabelAnchor: labelAnchor,
                Label: rawEdge.Label);
        }

        BoundingBox totalBounds = new(
            X: Math.Min(0.0, minX - margin),
            Y: Math.Min(0.0, minY - margin),
            Width: Math.Max(0.0, maxRight + margin),
            Height: Math.Max(0.0, maxBottom + margin));

        return new PositionedGraph(
            Nodes: positionedNodes,
            Edges: positionedEdges,
            Bounds: totalBounds,
            Direction: _options.Direction);
    }

    private static PositionedPort[] BuildPositionedPorts(
        IReadOnlyList<LayoutPort>? ports,
        double nodeLeft,
        double nodeTop,
        double nodeWidth,
        double nodeHeight,
        bool isInput,
        bool isLeftToRight,
        out Dictionary<string, LayoutPoint> map)
    {
        if (ports is null || ports.Count == 0)
        {
            map = new Dictionary<string, LayoutPoint>(0, StringComparer.Ordinal);
            return [];
        }

        map = new Dictionary<string, LayoutPoint>(ports.Count, StringComparer.Ordinal);
        PositionedPort[] result = new PositionedPort[ports.Count];

        for (int i = 0; i < ports.Count; i++)
        {
            LayoutPort port = ports[i];
            double t = !double.IsNaN(port.RelativeOffset)
                ? Math.Clamp(port.RelativeOffset, 0.0, 1.0)
                : (i + 1.0) / (ports.Count + 1.0);

            double px;
            double py;
            if (!isLeftToRight)
            {
                px = nodeLeft + (nodeWidth * (0.1 + (0.8 * t)));
                py = isInput ? nodeTop : nodeTop + nodeHeight;
            }
            else
            {
                px = isInput ? nodeLeft : nodeLeft + nodeWidth;
                py = nodeTop + (nodeHeight * (0.1 + (0.8 * t)));
            }

            result[i] = new PositionedPort(port.Id, px, py);
            map[port.Id] = new LayoutPoint(px, py);
        }

        return result;
    }

    private static LayoutPoint ResolvePortAnchor(
        PositionedNode node,
        Dictionary<string, LayoutPoint> portMap,
        string? portId,
        bool isInput,
        bool isLeftToRight)
    {
        if (!string.IsNullOrEmpty(portId) && portMap.TryGetValue(portId, out LayoutPoint pt))
        {
            return pt;
        }

        if (!isLeftToRight)
        {
            return new LayoutPoint(
                node.X + (node.Width * 0.5),
                isInput ? node.Y : node.Y + node.Height);
        }

        return new LayoutPoint(
            isInput ? node.X : node.X + node.Width,
            node.Y + (node.Height * 0.5));
    }

    private static CubicBezierSegment[] BuildBezierSegments(List<LayoutPoint> points, bool isLeftToRight)
    {
        if (points.Count < 2)
        {
            return [];
        }

        CubicBezierSegment[] segments = new CubicBezierSegment[points.Count - 1];
        for (int i = 0; i < points.Count - 1; i++)
        {
            LayoutPoint p0 = points[i];
            LayoutPoint p1 = points[i + 1];

            LayoutPoint c1;
            LayoutPoint c2;
            if (!isLeftToRight)
            {
                double dy = (p1.Y - p0.Y) * 0.45;
                c1 = new LayoutPoint(p0.X, p0.Y + dy);
                c2 = new LayoutPoint(p1.X, p1.Y - dy);
            }
            else
            {
                double dx = (p1.X - p0.X) * 0.45;
                c1 = new LayoutPoint(p0.X + dx, p0.Y);
                c2 = new LayoutPoint(p1.X - dx, p1.Y);
            }

            segments[i] = new CubicBezierSegment(p0, c1, c2, p1);
        }

        return segments;
    }

    private static LayoutPoint ComputePolylineMidpoint(List<LayoutPoint> points)
    {
        if (points.Count == 1)
        {
            return points[0];
        }

        if (points.Count == 2)
        {
            return new LayoutPoint(
                (points[0].X + points[1].X) * 0.5,
                (points[0].Y + points[1].Y) * 0.5);
        }

        int mid = points.Count / 2;
        return points[mid];
    }

    private void AlignLayerToNeighbors(
        int[] layer,
        bool useUpper,
        bool includeOpposite,
        List<double> scratch,
        double[] desiredX)
    {
        for (int i = 0; i < layer.Length; i++)
        {
            int v = layer[i];
            scratch.Clear();

            List<int> primarySegs = useUpper ? _upAdj[v] : _downAdj[v];
            AddSegmentTargetPositions(v, primarySegs, useUpper, weight: 2, scratch);

            if (includeOpposite)
            {
                List<int> secondarySegs = useUpper ? _downAdj[v] : _upAdj[v];
                AddSegmentTargetPositions(v, secondarySegs, !useUpper, weight: 1, scratch);
            }

            if (scratch.Count == 0)
            {
                desiredX[v] = _vertexX[v];
                continue;
            }

            scratch.Sort();
            int mid = scratch.Count / 2;
            double median = (scratch.Count & 1) == 1
                ? scratch[mid]
                : (scratch[mid - 1] + scratch[mid]) * 0.5;

            desiredX[v] = median;
        }
    }

    private void AddSegmentTargetPositions(
        int currentVertex,
        List<int> segmentIndices,
        bool neighborIsUpper,
        int weight,
        List<double> positions)
    {
        double currentWidth = _vertexWidth[currentVertex];
        bool currentIsDummy = currentVertex >= _realNodeCount;

        for (int i = 0; i < segmentIndices.Count; i++)
        {
            SegmentEdge seg = _segments[segmentIndices[i]];
            int neighbor = neighborIsUpper ? seg.UpperVertex : seg.LowerVertex;
            double neighborPortFactor = neighborIsUpper ? seg.UpperPortFactor : seg.LowerPortFactor;
            double selfPortFactor = neighborIsUpper ? seg.LowerPortFactor : seg.UpperPortFactor;

            double targetPos = _vertexX[neighbor]
                + (neighborPortFactor * _vertexWidth[neighbor])
                - (selfPortFactor * currentWidth);

            bool neighborIsDummy = neighbor >= _realNodeCount;
            int copies = (currentIsDummy && neighborIsDummy) ? weight * 2 : weight;
            for (int c = 0; c < copies; c++)
            {
                positions.Add(targetPos);
            }
        }
    }

    private void ProjectNonOverlapping(int[] layer, double[] desiredX, double nodeSep, bool leftToRightBias)
    {
        int n = layer.Length;
        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            int v = layer[0];
            _vertexX[v] = desiredX[v];
            return;
        }

        // Block-based 1D isotonic regression / compaction so adjacent vertices maintain distance >= (w_i + w_{i+1})/2 + nodeSep
        // while staying centered on their desired median coordinates.
        if (leftToRightBias)
        {
            // Forward pass: enforce x[i] >= x[i-1] + minSep
            _vertexX[layer[0]] = desiredX[layer[0]];
            for (int i = 1; i < n; i++)
            {
                int prev = layer[i - 1];
                int curr = layer[i];
                double minSep = ((_vertexWidth[prev] + _vertexWidth[curr]) * 0.5) + nodeSep;
                double minAllowed = _vertexX[prev] + minSep;
                _vertexX[curr] = Math.Max(desiredX[curr], minAllowed);
            }

            // Backward pull-back pass toward desiredX without violating left neighbors
            for (int i = n - 2; i >= 0; i--)
            {
                int curr = layer[i];
                int next = layer[i + 1];
                double minSep = ((_vertexWidth[curr] + _vertexWidth[next]) * 0.5) + nodeSep;
                double maxAllowed = _vertexX[next] - minSep;
                if (_vertexX[curr] > maxAllowed)
                {
                    _vertexX[curr] = maxAllowed;
                }
            }
        }
        else
        {
            // Right-to-left pass: enforce x[i] <= x[i+1] - minSep
            _vertexX[layer[n - 1]] = desiredX[layer[n - 1]];
            for (int i = n - 2; i >= 0; i--)
            {
                int curr = layer[i];
                int next = layer[i + 1];
                double minSep = ((_vertexWidth[curr] + _vertexWidth[next]) * 0.5) + nodeSep;
                double maxAllowed = _vertexX[next] - minSep;
                _vertexX[curr] = Math.Min(desiredX[curr], maxAllowed);
            }

            // Forward push pass
            for (int i = 1; i < n; i++)
            {
                int prev = layer[i - 1];
                int curr = layer[i];
                double minSep = ((_vertexWidth[prev] + _vertexWidth[curr]) * 0.5) + nodeSep;
                double minAllowed = _vertexX[prev] + minSep;
                if (_vertexX[curr] < minAllowed)
                {
                    _vertexX[curr] = minAllowed;
                }
            }
        }

        // Center shift if the compacted layer drifted away from the mean of desiredX
        double sumDesired = 0.0;
        double sumActual = 0.0;
        for (int i = 0; i < n; i++)
        {
            int v = layer[i];
            sumDesired += desiredX[v];
            sumActual += _vertexX[v];
        }

        double delta = (sumDesired - sumActual) / n;
        for (int i = 0; i < n; i++)
        {
            _vertexX[layer[i]] += delta;
        }
    }

    internal readonly record struct SegmentEdge(
        int UpperVertex,
        int LowerVertex,
        double UpperPortFactor,
        double LowerPortFactor);
}

