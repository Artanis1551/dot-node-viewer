using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Onnx;

/// <summary>
/// Maps standard ONNX (<c>ai.onnx</c>) operator types, formal port names, and <c>TensorProto.DataType</c> codes into the Unified Graph IR.
/// </summary>
internal static class OnnxOperatorCatalog
{
    /// <summary>
    /// Classifies an ONNX operator into a semantic <see cref="OperatorCategory"/>.
    /// </summary>
    /// <param name="opType">The ONNX operator type name (for example, <c>"Conv"</c> or <c>"Relu"</c>).</param>
    /// <param name="domain">The optional operator domain (empty or <c>"ai.onnx"</c> for the default ONNX opset).</param>
    /// <returns>The mapped <see cref="OperatorCategory"/>.</returns>
    public static OperatorCategory Categorize(string opType, string? domain = null)
    {
        if (string.IsNullOrEmpty(opType))
        {
            return OperatorCategory.Unknown;
        }

        switch (opType)
        {
            // Core compute & trainable layers
            case "Conv":
            case "ConvTranspose":
            case "ConvInteger":
            case "QLinearConv":
            case "DeformConv":
            case "Gemm":
            case "MatMul":
            case "MatMulInteger":
            case "QLinearMatMul":
            case "Linear":
            case "RNN":
            case "GRU":
            case "LSTM":
            case "Attention":
            case "MultiHeadAttention":
            case "Einsum":
            case "Gather":
            case "GatherElements":
            case "GatherND":
            case "Scatter":
            case "ScatterElements":
            case "ScatterND":
            case "Compress":
            case "EmbedLayerNormalization":
                return OperatorCategory.Layer;

            // Non-linear activations
            case "Relu":
            case "LeakyRelu":
            case "PRelu":
            case "Elu":
            case "Celu":
            case "Selu":
            case "ThresholdedRelu":
            case "Sigmoid":
            case "HardSigmoid":
            case "Tanh":
            case "Gelu":
            case "FastGelu":
            case "BiasGelu":
            case "Softmax":
            case "LogSoftmax":
            case "Hardmax":
            case "Softplus":
            case "Softsign":
            case "Mish":
            case "Swish":
            case "HardSwish":
            case "Erf":
            case "Clip":
            case "Shrink":
            case "Binarizer":
                return OperatorCategory.Activation;

            // Normalization layers
            case "BatchNormalization":
            case "LayerNormalization":
            case "SimplifiedLayerNormalization":
            case "SkipLayerNormalization":
            case "InstanceNormalization":
            case "LpNormalization":
            case "GroupNormalization":
            case "LRN":
            case "MeanVarianceNormalization":
                return OperatorCategory.Normalization;

            // Spatial & global pooling
            case "MaxPool":
            case "AveragePool":
            case "LpPool":
            case "GlobalMaxPool":
            case "GlobalAveragePool":
            case "GlobalLpPool":
            case "MaxRoiPool":
            case "RoiAlign":
            case "MaxUnpool":
            case "Col2Im":
                return OperatorCategory.Pooling;

            // Quantization & dequantization
            case "QuantizeLinear":
            case "DequantizeLinear":
            case "DynamicQuantizeLinear":
                return OperatorCategory.Quantization;

            // Shape, view & layout manipulation
            case "Reshape":
            case "Transpose":
            case "Concat":
            case "Split":
            case "Slice":
            case "Squeeze":
            case "Unsqueeze":
            case "Flatten":
            case "Expand":
            case "Tile":
            case "Pad":
            case "Shape":
            case "Size":
            case "DepthToSpace":
            case "SpaceToDepth":
            case "Resize":
            case "Upsample":
            case "CenterCropPad":
            case "AffineGrid":
            case "GridSample":
                return OperatorCategory.Shape;

            // Constants
            case "Constant":
            case "ConstantOfShape":
                return OperatorCategory.Constant;

            // Control flow & sequence operators
            case "If":
            case "Loop":
            case "Scan":
            case "SequenceConstruct":
            case "SequenceAt":
            case "SequenceInsert":
            case "SequenceErase":
            case "SequenceEmpty":
            case "SequenceLength":
            case "ConcatFromSequence":
            case "SplitToSequence":
                return OperatorCategory.ControlFlow;

            // Element-wise tensor arithmetic, casting & reductions
            case "Add":
            case "Sub":
            case "Mul":
            case "Div":
            case "Pow":
            case "Mod":
            case "Neg":
            case "Abs":
            case "Sqrt":
            case "Exp":
            case "Log":
            case "Sin":
            case "Cos":
            case "Tan":
            case "Asin":
            case "Acos":
            case "Atan":
            case "Sinh":
            case "Cosh":
            case "Asinh":
            case "Acosh":
            case "Atanh":
            case "Ceil":
            case "Floor":
            case "Round":
            case "Sign":
            case "Reciprocal":
            case "Sum":
            case "Mean":
            case "Max":
            case "Min":
            case "Where":
            case "Cast":
            case "CastLike":
            case "BitCast":
            case "Equal":
            case "Greater":
            case "GreaterOrEqual":
            case "Less":
            case "LessOrEqual":
            case "And":
            case "Or":
            case "Not":
            case "Xor":
            case "BitwiseAnd":
            case "BitwiseOr":
            case "BitwiseNot":
            case "BitwiseXor":
            case "BitShift":
            case "ReduceMean":
            case "ReduceSum":
            case "ReduceMax":
            case "ReduceMin":
            case "ReduceProd":
            case "ReduceL1":
            case "ReduceL2":
            case "ReduceLogSum":
            case "ReduceLogSumExp":
            case "ReduceSumSquare":
            case "ArgMax":
            case "ArgMin":
            case "TopK":
            case "CumSum":
            case "NonZero":
            case "OneHot":
            case "Range":
            case "EyeLike":
            case "Trilu":
            case "Det":
            case "Dropout":
            case "Identity":
            case "IsNaN":
            case "IsInf":
                return OperatorCategory.Tensor;

            default:
                if (!string.IsNullOrEmpty(domain)
                    && !string.Equals(domain, "ai.onnx", StringComparison.Ordinal)
                    && !string.Equals(domain, "ai.onnx.ml", StringComparison.Ordinal))
                {
                    return OperatorCategory.Custom;
                }

                return OperatorCategory.Unknown;
        }
    }

    /// <summary>
    /// Maps an ONNX <c>TensorProto.DataType</c> integer code to the Unified Graph IR <see cref="DataType"/>.
    /// </summary>
    /// <param name="onnxElemType">The integer value of <c>TensorProto.data_type</c> or <c>TypeProto.Tensor.elem_type</c>.</param>
    /// <returns>The corresponding <see cref="DataType"/>.</returns>
    public static DataType MapDataType(int onnxElemType) => onnxElemType switch
    {
        1 => DataType.Float32,
        2 => DataType.UInt8,
        3 => DataType.Int8,
        4 => DataType.UInt16,
        5 => DataType.Int16,
        6 => DataType.Int32,
        7 => DataType.Int64,
        8 => DataType.String,
        9 => DataType.Bool,
        10 => DataType.Float16,
        11 => DataType.Float64,
        12 => DataType.UInt32,
        13 => DataType.UInt64,
        14 => DataType.Complex64,
        15 => DataType.Complex128,
        16 => DataType.BFloat16,
        17 or 18 => DataType.Float8E4M3,
        19 or 20 => DataType.Float8E5M2,
        21 => DataType.UInt4,
        22 => DataType.Int4,
        _ => DataType.Unknown,
    };

    /// <summary>
    /// Gets the formal input port name for an ONNX operator at the specified positional <paramref name="index"/>.
    /// </summary>
    /// <param name="opType">The ONNX operator type name.</param>
    /// <param name="index">The zero-based input index.</param>
    /// <returns>The formal parameter name (such as <c>"X"</c>, <c>"W"</c>, or <c>"B"</c>).</returns>
    public static string GetInputPortName(string opType, int index) => opType switch
    {
        "Conv" or "ConvTranspose" => index switch
        {
            0 => "X",
            1 => "W",
            2 => "B",
            _ => $"input_{index}",
        },
        "Gemm" => index switch
        {
            0 => "A",
            1 => "B",
            2 => "C",
            _ => $"input_{index}",
        },
        "MatMul" => index switch
        {
            0 => "A",
            1 => "B",
            _ => $"input_{index}",
        },
        "Add" or "Sub" or "Mul" or "Div" or "Pow" => index switch
        {
            0 => "A",
            1 => "B",
            _ => $"input_{index}",
        },
        "BatchNormalization" => index switch
        {
            0 => "X",
            1 => "scale",
            2 => "B",
            3 => "input_mean",
            4 => "input_var",
            _ => $"input_{index}",
        },
        "LayerNormalization" => index switch
        {
            0 => "X",
            1 => "Scale",
            2 => "B",
            _ => $"input_{index}",
        },
        "Relu" or "LeakyRelu" or "Sigmoid" or "Tanh" or "Gelu" or "Softmax" or "LogSoftmax"
            or "MaxPool" or "AveragePool" or "GlobalAveragePool" or "GlobalMaxPool"
            or "Flatten" or "Identity" or "Dropout" => index switch
        {
            0 => "X",
            _ => $"input_{index}",
        },
        "Clip" => index switch
        {
            0 => "input",
            1 => "min",
            2 => "max",
            _ => $"input_{index}",
        },
        "Reshape" => index switch
        {
            0 => "data",
            1 => "shape",
            _ => $"input_{index}",
        },
        "Transpose" or "Squeeze" or "Unsqueeze" => index switch
        {
            0 => "data",
            1 => "axes",
            _ => $"input_{index}",
        },
        "Slice" => index switch
        {
            0 => "data",
            1 => "starts",
            2 => "ends",
            3 => "axes",
            4 => "steps",
            _ => $"input_{index}",
        },
        "Concat" => $"inputs[{index}]",
        "QuantizeLinear" => index switch
        {
            0 => "x",
            1 => "y_scale",
            2 => "y_zero_point",
            _ => $"input_{index}",
        },
        "DequantizeLinear" => index switch
        {
            0 => "x",
            1 => "x_scale",
            2 => "x_zero_point",
            _ => $"input_{index}",
        },
        "If" => index == 0 ? "cond" : $"input_{index}",
        "Loop" => index switch
        {
            0 => "M",
            1 => "cond",
            _ => $"v_initial[{index - 2}]",
        },
        _ => $"input_{index}",
    };

    /// <summary>
    /// Gets the formal output port name for an ONNX operator at the specified positional <paramref name="index"/>.
    /// </summary>
    /// <param name="opType">The ONNX operator type name.</param>
    /// <param name="index">The zero-based output index.</param>
    /// <returns>The formal output parameter name (such as <c>"Y"</c> or <c>"C"</c>).</returns>
    public static string GetOutputPortName(string opType, int index) => opType switch
    {
        "Conv" or "ConvTranspose" or "Gemm" or "MatMul"
            or "Relu" or "LeakyRelu" or "Sigmoid" or "Tanh" or "Gelu" or "Softmax" or "LogSoftmax"
            or "MaxPool" or "AveragePool" or "GlobalAveragePool" or "GlobalMaxPool"
            or "Flatten" or "Identity" or "Dropout" or "LayerNormalization"
            when index == 0 => "Y",
        "BatchNormalization" => index switch
        {
            0 => "Y",
            1 => "running_mean",
            2 => "running_var",
            _ => $"output_{index}",
        },
        "Add" or "Sub" or "Mul" or "Div" or "Pow" when index == 0 => "C",
        "Reshape" when index == 0 => "reshaped",
        "Transpose" when index == 0 => "transposed",
        "Concat" when index == 0 => "concat_result",
        "Slice" or "Clip" or "Constant" when index == 0 => "output",
        "QuantizeLinear" or "DequantizeLinear" when index == 0 => "y",
        "If" => $"outputs[{index}]",
        _ => $"output_{index}",
    };
}
