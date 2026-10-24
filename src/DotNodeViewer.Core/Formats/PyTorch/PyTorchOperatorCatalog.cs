using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.PyTorch;

/// <summary>
/// Catalogs PyTorch and TorchScript operators, semantic categories, and tensor storage data types.
/// </summary>
internal static class PyTorchOperatorCatalog
{
    /// <summary>
    /// Maps a PyTorch operator name or nn.Module class name to its semantic <see cref="OperatorCategory"/>.
    /// </summary>
    public static OperatorCategory MapOperatorCategory(string op)
    {
        ArgumentNullException.ThrowIfNull(op);

        // Normalize leading torch. or aten:: prefixes
        string normalized = op;
        if (normalized.StartsWith("torch.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["torch.".Length..];
        }
        else if (normalized.StartsWith("aten::", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["aten::".Length..];
        }
        else if (normalized.StartsWith("prim::", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["prim::".Length..];
        }

        return normalized switch
        {
            // Layers
            "_convolution" or "convolution" or "conv1d" or "conv2d" or "conv3d" or "conv_transpose2d" or
            "Conv1d" or "Conv2d" or "Conv3d" or "ConvTranspose2d" or
            "linear" or "Linear" or "dense" or "matmul" or "bmm" or "addmm" or
            "embedding" or "Embedding" or "lstm" or "LSTM" or "gru" or "GRU" => OperatorCategory.Layer,

            // Normalization
            "batch_norm" or "BatchNorm1d" or "BatchNorm2d" or "BatchNorm3d" or
            "layer_norm" or "LayerNorm" or "group_norm" or "GroupNorm" or
            "instance_norm" or "InstanceNorm2d" => OperatorCategory.Normalization,

            // Activations
            "relu" or "relu_" or "ReLU" or "gelu" or "GELU" or "silu" or "SiLU" or
            "sigmoid" or "Sigmoid" or "tanh" or "Tanh" or "leaky_relu" or "LeakyReLU" or
            "softmax" or "Softmax" or "log_softmax" or "LogSoftmax" or "elu" or "ELU" or
            "selu" or "SELU" or "hardswish" or "Hardswish" or "hardsigmoid" or "Hardsigmoid" => OperatorCategory.Activation,

            // Pooling
            "max_pool2d" or "MaxPool2d" or "avg_pool2d" or "AvgPool2d" or
            "adaptive_avg_pool2d" or "AdaptiveAvgPool2d" or "adaptive_max_pool2d" or "AdaptiveMaxPool2d" => OperatorCategory.Pooling,

            // Shape / Tensor manipulation
            "flatten" or "Flatten" or "reshape" or "Reshape" or "view" or "squeeze" or "unsqueeze" or
            "permute" or "transpose" or "t" or "slice" or "split" or "cat" or "concat" or
            "chunk" or "stack" or "expand" or "gather" or "scatter" or "select" => OperatorCategory.Shape,

            // Math / Arithmetic
            "add" or "add_" or "sub" or "sub_" or "mul" or "mul_" or "div" or "div_" or
            "neg" or "abs" or "pow" or "exp" or "log" or "sqrt" or "rsqrt" or
            "sum" or "mean" or "min" or "max" or "argmax" or "argmin" or "clamp" or
            "gt" or "lt" or "ge" or "le" or "eq" or "ne" => OperatorCategory.Tensor,

            // Control flow
            "If" or "Loop" or "Cond" or "While" or "switch" => OperatorCategory.ControlFlow,

            // Constants
            "Constant" or "GetAttr" => OperatorCategory.Constant,

            _ => OperatorCategory.Custom
        };
    }

    /// <summary>
    /// Maps a PyTorch Storage class name to the corresponding <see cref="DataType"/>.
    /// </summary>
    public static DataType MapStorageClassToDataType(string storageName)
    {
        ArgumentNullException.ThrowIfNull(storageName);

        if (storageName.EndsWith("FloatStorage", StringComparison.OrdinalIgnoreCase))
        {
            return storageName.StartsWith("Half", StringComparison.OrdinalIgnoreCase)
                ? DataType.Float16
                : storageName.StartsWith("BFloat16", StringComparison.OrdinalIgnoreCase)
                    ? DataType.BFloat16
                    : storageName.StartsWith("Double", StringComparison.OrdinalIgnoreCase)
                        ? DataType.Float64
                        : DataType.Float32;
        }

        if (storageName.EndsWith("HalfStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Float16;
        if (storageName.EndsWith("BFloat16Storage", StringComparison.OrdinalIgnoreCase)) return DataType.BFloat16;
        if (storageName.EndsWith("DoubleStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Float64;
        if (storageName.EndsWith("LongStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Int64;
        if (storageName.EndsWith("IntStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Int32;
        if (storageName.EndsWith("ShortStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Int16;
        if (storageName.EndsWith("CharStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Int8;
        if (storageName.EndsWith("ByteStorage", StringComparison.OrdinalIgnoreCase)) return DataType.UInt8;
        if (storageName.EndsWith("BoolStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Bool;
        if (storageName.EndsWith("QInt8Storage", StringComparison.OrdinalIgnoreCase)) return DataType.Int8;
        if (storageName.EndsWith("QUInt8Storage", StringComparison.OrdinalIgnoreCase)) return DataType.UInt8;
        if (storageName.EndsWith("ComplexFloatStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Complex64;
        if (storageName.EndsWith("ComplexDoubleStorage", StringComparison.OrdinalIgnoreCase)) return DataType.Complex128;

        return DataType.Float32;
    }

    /// <summary>
    /// Computes the storage byte size for a single element of <paramref name="dataType"/>.
    /// </summary>
    public static int GetDataTypeByteSize(DataType dataType)
    {
        return dataType switch
        {
            DataType.Float32 or DataType.Int32 or DataType.UInt32 => 4,
            DataType.Float64 or DataType.Int64 or DataType.UInt64 => 8,
            DataType.Float16 or DataType.BFloat16 or DataType.Int16 or DataType.UInt16 => 2,
            DataType.Int8 or DataType.UInt8 or DataType.Bool => 1,
            DataType.Complex64 => 8,
            DataType.Complex128 => 16,
            _ => 4
        };
    }
}
