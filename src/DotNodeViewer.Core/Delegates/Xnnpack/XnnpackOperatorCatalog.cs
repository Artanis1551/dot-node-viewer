using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Delegates.Xnnpack;

/// <summary>
/// Identifies the FlatBuffers table layout corresponding to a serialized <c>fb_xnnpack.XNodeUnion</c> entry.
/// </summary>
internal enum XnnpackTableKind
{
    Unknown = 0,
    Unary1x1,
    Binary2x1,
    FullyConnected,
    Conv,
    Pooling2d,
    StaticTranspose,
    StaticResizeBilinear2d,
    StaticConstantPad,
    StaticReshape,
    ArgMaxPooling2d,
    LeakyReLU,
    Elu,
    Concat2,
    Concat3,
    Concat4,
    Concat5,
    StaticSlice,
    ScaledDotProductAttention,
    DepthToSpace2d,
    StaticReduce,
}

/// <summary>
/// Describes a resolved XNNPACK operator name, semantic <see cref="OperatorCategory"/>, and FlatBuffers table kind.
/// </summary>
/// <param name="Name">The canonical XNNPACK operator name.</param>
/// <param name="Category">The semantic Unified Graph IR category.</param>
/// <param name="TableKind">The table layout used to decode the node's fields and ports.</param>
internal readonly record struct XnnpackOpSpec(
    string Name,
    OperatorCategory Category,
    XnnpackTableKind TableKind);

/// <summary>
/// Maps <c>fb_xnnpack.XNodeUnion</c> union discriminants and <c>XNNDatatype</c> codes into Unified Graph IR primitives.
/// </summary>
internal static class XnnpackOperatorCatalog
{
    /// <summary>
    /// Resolves an <c>fb_xnnpack.XNodeUnion</c> union discriminant (1-based) to its operator specification.
    /// </summary>
    /// <param name="unionDiscriminant">The 1-based union type byte from <c>XNode.xnode_union_type</c>.</param>
    /// <returns>An <see cref="XnnpackOpSpec"/> describing the operator.</returns>
    public static XnnpackOpSpec Resolve(byte unionDiscriminant) => unionDiscriminant switch
    {
        1 => new("XNNAdd", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        2 => new("XNNFullyConnected", OperatorCategory.Layer, XnnpackTableKind.FullyConnected),
        3 => new("XNNSoftmax", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        4 => new("XNNSigmoid", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        5 => new("XNNStaticTranspose", OperatorCategory.Shape, XnnpackTableKind.StaticTranspose),
        6 => new("XNNClamp", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        7 => new("XNNConv2d", OperatorCategory.Layer, XnnpackTableKind.Conv),
        8 => new("XNNDiv", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        9 => new("XNNStaticResizeBilinear2D", OperatorCategory.Shape, XnnpackTableKind.StaticResizeBilinear2d),
        10 => new("XNNStaticConstantPad", OperatorCategory.Shape, XnnpackTableKind.StaticConstantPad),
        11 => new("XNNAvgPooling2d", OperatorCategory.Pooling, XnnpackTableKind.Pooling2d),
        12 => new("XNNMinimum", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        13 => new("XNNDepthwiseConv2d", OperatorCategory.Layer, XnnpackTableKind.Conv),
        14 => new("XNNMaxPooling2d", OperatorCategory.Pooling, XnnpackTableKind.Pooling2d),
        15 => new("XNNMultiply", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        16 => new("XNNSubtract", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        17 => new("XNNFloor", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        18 => new("XNNConvert", OperatorCategory.Quantization, XnnpackTableKind.Unary1x1),
        19 => new("XNNGlobalAvgPooling2d", OperatorCategory.Pooling, XnnpackTableKind.Unary1x1),
        20 => new("XNNStaticReshape", OperatorCategory.Shape, XnnpackTableKind.StaticReshape),
        21 => new("XNNArgMaxPooling2d", OperatorCategory.Pooling, XnnpackTableKind.ArgMaxPooling2d),
        22 => new("XNNSquareRoot", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        23 => new("XNNCeiling", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        24 => new("XNNHardswish", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        25 => new("XNNLeakyReLU", OperatorCategory.Activation, XnnpackTableKind.LeakyReLU),
        26 => new("XNNMaximum", OperatorCategory.Tensor, XnnpackTableKind.Binary2x1),
        27 => new("XNNNegate", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        28 => new("XNNSquare", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        29 => new("XNNELU", OperatorCategory.Activation, XnnpackTableKind.Elu),
        30 => new("XNNAbs", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        31 => new("XNNPReLU", OperatorCategory.Activation, XnnpackTableKind.Binary2x1),
        32 => new("XNNConcatenate2", OperatorCategory.Shape, XnnpackTableKind.Concat2),
        33 => new("XNNConcatenate3", OperatorCategory.Shape, XnnpackTableKind.Concat3),
        34 => new("XNNConcatenate4", OperatorCategory.Shape, XnnpackTableKind.Concat4),
        35 => new("XNNStaticSlice", OperatorCategory.Shape, XnnpackTableKind.StaticSlice),
        36 => new("XNNScaledDotProductAttention", OperatorCategory.Layer, XnnpackTableKind.ScaledDotProductAttention),
        37 => new("XNNBatchMatrixMultiply", OperatorCategory.Layer, XnnpackTableKind.Binary2x1),
        38 => new("XNNConcatenate5", OperatorCategory.Shape, XnnpackTableKind.Concat5),
        39 => new("XNNConvTranspose2d", OperatorCategory.Layer, XnnpackTableKind.Conv),
        40 => new("XNNReciprocalSquareRoot", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        41 => new("XNNLog", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        42 => new("XNNGelu", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        43 => new("XNNTanh", OperatorCategory.Activation, XnnpackTableKind.Unary1x1),
        44 => new("XNNExp", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        45 => new("XNNSin", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        46 => new("XNNCopy", OperatorCategory.Data, XnnpackTableKind.Unary1x1),
        47 => new("XNNCos", OperatorCategory.Tensor, XnnpackTableKind.Unary1x1),
        49 => new("XNNDepthToSpace2d", OperatorCategory.Shape, XnnpackTableKind.DepthToSpace2d),
        50 => new("_XNNStaticReduce", OperatorCategory.Tensor, XnnpackTableKind.StaticReduce),
        _ => new(
            string.Create(CultureInfo.InvariantCulture, $"XNNUnknown_{unionDiscriminant}"),
            OperatorCategory.Unknown,
            XnnpackTableKind.Unknown),
    };

    /// <summary>
    /// Maps an <c>fb_xnnpack.XNNDatatype</c> value to a Unified Graph IR <see cref="DataType"/>.
    /// </summary>
    /// <param name="xnnDatatype">The 16-bit <c>XNNDatatype</c> code.</param>
    /// <returns>The mapped <see cref="DataType"/>.</returns>
    public static DataType MapDataType(short xnnDatatype) => xnnDatatype switch
    {
        1 => DataType.Float32,    // xnn_datatype_fp32
        2 => DataType.Float16,    // xnn_datatype_fp16
        3 => DataType.Int8,       // xnn_datatype_qint8
        4 => DataType.UInt8,      // xnn_datatype_quint8
        5 => DataType.Int32,      // xnn_datatype_qint32
        6 => DataType.Int8,       // xnn_datatype_qcint8
        7 => DataType.Int32,      // xnn_datatype_qcint32
        8 => DataType.Int4,       // xnn_datatype_qcint4
        9 => DataType.Int8,       // xnn_datatype_qdint8
        10 => DataType.Int4,      // xnn_datatype_qbint4
        11 => DataType.Int8,      // xnn_datatype_qpint8
        12 => DataType.Int32,     // xnn_datatype_int32
        13 => DataType.Float32,   // xnn_datatype_pfp32
        14 => DataType.BFloat16,  // xnn_datatype_bf16
        _ => DataType.Unknown,
    };

    /// <summary>
    /// Returns whether the specified <c>XNNDatatype</c> code represents a 4-bit quantized tensor type.
    /// </summary>
    public static bool IsFourBitType(short xnnDatatype) => xnnDatatype is 8 or 10;
}
