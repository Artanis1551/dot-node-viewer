using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.ExecuTorch;

/// <summary>
/// Maps ExecuTorch scalar types, operator names, and backend delegates into the Unified Graph IR.
/// </summary>
internal static class ExecuTorchOperatorCatalog
{
    /// <summary>
    /// Maps an ExecuTorch FlatBuffers <c>ScalarType</c> code to a Unified Graph IR <see cref="DataType"/>.
    /// </summary>
    /// <param name="scalarType">The numeric scalar type identifier.</param>
    /// <returns>The corresponding <see cref="DataType"/>.</returns>
    public static DataType MapScalarType(sbyte scalarType) => scalarType switch
    {
        0 => DataType.UInt8,         // BYTE
        1 => DataType.Int8,          // CHAR
        2 => DataType.Int16,         // SHORT
        3 => DataType.Int32,         // INT
        4 => DataType.Int64,         // LONG
        5 => DataType.Float16,       // HALF
        6 => DataType.Float32,       // FLOAT
        7 => DataType.Float64,       // DOUBLE
        11 => DataType.Bool,         // BOOL
        12 => DataType.Int8,         // QINT8
        13 => DataType.UInt8,        // QUINT8
        14 => DataType.Int32,        // QINT32
        15 => DataType.BFloat16,     // BFLOAT16
        16 => DataType.UInt4,        // QUINT4X2
        17 => DataType.UInt4,        // QUINT2X4
        22 => DataType.UInt16,       // BITS16
        23 => DataType.Float8E5M2,   // FLOAT8E5M2
        24 => DataType.Float8E4M3,   // FLOAT8E4M3FN
        25 => DataType.Float8E5M2,   // FLOAT8E5M2FNUZ
        26 => DataType.Float8E4M3,   // FLOAT8E4M3FNUZ
        27 => DataType.UInt16,       // UINT16
        28 => DataType.UInt32,       // UINT32
        29 => DataType.UInt64,       // UINT64
        _ => DataType.Unknown,
    };

    /// <summary>
    /// Computes the storage element size in bytes for a <see cref="DataType"/>.
    /// </summary>
    /// <param name="dataType">The tensor element data type.</param>
    /// <returns>The size in bytes of a single element.</returns>
    public static int GetElementByteSize(DataType dataType) => dataType switch
    {
        DataType.Bool or DataType.UInt8 or DataType.Int8 or DataType.Float8E4M3 or DataType.Float8E5M2 => 1,
        DataType.UInt16 or DataType.Int16 or DataType.Float16 or DataType.BFloat16 => 2,
        DataType.UInt32 or DataType.Int32 or DataType.Float32 => 4,
        DataType.UInt64 or DataType.Int64 or DataType.Float64 or DataType.Complex64 => 8,
        DataType.Complex128 => 16,
        _ => 1,
    };

    /// <summary>
    /// Classifies an ExecuTorch operator or delegate into a semantic <see cref="OperatorCategory"/>.
    /// </summary>
    /// <param name="fullOpName">The fully qualified operator name (such as <c>"aten::linear.out"</c> or <c>"Delegate (XnnpackBackend)"</c>).</param>
    /// <returns>The mapped <see cref="OperatorCategory"/>.</returns>
    public static OperatorCategory Categorize(string fullOpName)
    {
        if (string.IsNullOrWhiteSpace(fullOpName))
        {
            return OperatorCategory.Unknown;
        }

        if (fullOpName.StartsWith("Delegate (", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorCategory.Custom;
        }

        string op = fullOpName;
        if (op.StartsWith("aten::", StringComparison.OrdinalIgnoreCase))
        {
            op = op[6..];
        }
        else if (op.StartsWith("executorch_prim::", StringComparison.OrdinalIgnoreCase))
        {
            op = op[17..];
        }

        int dotIdx = op.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx >= 0)
        {
            op = op[..dotIdx];
        }

        return op.ToLowerInvariant() switch
        {
            // Core trainable layers
            "linear" or "conv1d" or "conv2d" or "conv3d" or "conv_transpose1d" or "conv_transpose2d" or "conv_transpose3d"
                or "addmm" or "mm" or "bmm" or "matmul" or "embedding" or "gru" or "lstm" or "rnn_tanh" or "rnn_relu"
                => OperatorCategory.Layer,

            // Activations
            "relu" or "relu6" or "sigmoid" or "tanh" or "gelu" or "silu" or "hardswish" or "hardsigmoid"
                or "leaky_relu" or "softmax" or "log_softmax" or "elu" or "prelu" or "selu" or "celu"
                or "softplus" or "threshold"
                => OperatorCategory.Activation,

            // Normalizations
            "batch_norm" or "native_batch_norm" or "layer_norm" or "native_layer_norm"
                or "group_norm" or "native_group_norm" or "instance_norm"
                => OperatorCategory.Normalization,

            // Pooling
            "max_pool1d" or "max_pool2d" or "max_pool3d" or "max_pool1d_with_indices" or "max_pool2d_with_indices"
                or "avg_pool1d" or "avg_pool2d" or "avg_pool3d"
                or "adaptive_avg_pool1d" or "adaptive_avg_pool2d" or "adaptive_avg_pool3d" or "adaptive_max_pool2d"
                => OperatorCategory.Pooling,

            // Quantization
            "quantize" or "dequantize" or "fake_quantize" or "quantized_decomposed"
                or "quantize_per_tensor" or "quantize_per_channel"
                or "dequantize_per_tensor" or "dequantize_per_channel" or "choose_qparams"
                => OperatorCategory.Quantization,

            // Shape and view manipulations
            "view" or "view_copy" or "reshape" or "permute" or "permute_copy"
                or "transpose" or "transpose_copy" or "squeeze" or "squeeze_copy"
                or "unsqueeze" or "unsqueeze_copy" or "slice" or "slice_copy"
                or "split" or "split_with_sizes" or "cat" or "concat" or "expand" or "flatten"
                or "t" or "t_copy" or "narrow" or "narrow_copy" or "select" or "select_copy"
                => OperatorCategory.Shape,

            // Element-wise tensor operations
            "add" or "sub" or "mul" or "div" or "neg" or "pow" or "exp" or "log" or "sqrt" or "rsqrt"
                or "abs" or "clamp" or "cos" or "sin" or "tan" or "where" or "sum" or "mean" or "prod"
                or "max" or "min" or "argmax" or "argmin" or "eq" or "ne" or "gt" or "lt" or "ge" or "le"
                or "bitwise_and" or "bitwise_or" or "bitwise_xor" or "clone" or "copy" or "move"
                or "scatter" or "gather" or "index" or "index_select"
                => OperatorCategory.Tensor,

            // Constants
            "full" or "full_like" or "zeros" or "zeros_like" or "ones" or "ones_like"
                or "empty" or "empty_like" or "arange"
                => OperatorCategory.Constant,

            // Control flow
            "jump_false" or "cond" or "while_loop"
                => OperatorCategory.ControlFlow,

            _ => OperatorCategory.Unknown,
        };
    }
}

