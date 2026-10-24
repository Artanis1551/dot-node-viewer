using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Keras;

/// <summary>
/// Catalogs Keras layer types, operator categories, and data types.
/// </summary>
internal static class KerasOperatorCatalog
{
    /// <summary>
    /// Maps a Keras layer class name to its semantic <see cref="OperatorCategory"/>.
    /// </summary>
    public static OperatorCategory MapLayerCategory(string className)
    {
        ArgumentNullException.ThrowIfNull(className);

        return className switch
        {
            // Layers (Linear, Convolutional, Recurrent, Attention)
            "Dense" or "EinsumDense" or "Embedding" or
            "Conv1D" or "Conv2D" or "Conv3D" or
            "Conv1DTranspose" or "Conv2DTranspose" or "Conv3DTranspose" or
            "DepthwiseConv1D" or "DepthwiseConv2D" or
            "SeparableConv1D" or "SeparableConv2D" or
            "LSTM" or "GRU" or "SimpleRNN" or "Bidirectional" or
            "MultiHeadAttention" or "Attention" or "AdditiveAttention" => OperatorCategory.Layer,

            // Normalization
            "BatchNormalization" or "LayerNormalization" or "UnitNormalization" or
            "GroupNormalization" or "SpectralNormalization" => OperatorCategory.Normalization,

            // Activations
            "Activation" or "ReLU" or "LeakyReLU" or "PReLU" or "ELU" or
            "ThresholdedReLU" or "Softmax" => OperatorCategory.Activation,

            // Pooling
            "MaxPooling1D" or "MaxPooling2D" or "MaxPooling3D" or
            "AveragePooling1D" or "AveragePooling2D" or "AveragePooling3D" or
            "GlobalMaxPooling1D" or "GlobalMaxPooling2D" or "GlobalMaxPooling3D" or
            "GlobalAveragePooling1D" or "GlobalAveragePooling2D" or "GlobalAveragePooling3D" => OperatorCategory.Pooling,

            // Shape / Tensor manipulation
            "Reshape" or "Flatten" or "RepeatVector" or "Permute" or
            "Cropping1D" or "Cropping2D" or "Cropping3D" or
            "ZeroPadding1D" or "ZeroPadding2D" or "ZeroPadding3D" or
            "UpSampling1D" or "UpSampling2D" or "UpSampling3D" => OperatorCategory.Shape,

            // Math / Arithmetic / Merging
            "Add" or "Subtract" or "Multiply" or "Average" or
            "Maximum" or "Minimum" or "Concatenate" or "Dot" => OperatorCategory.Tensor,

            // Regularization / Custom
            "Dropout" or "SpatialDropout1D" or "SpatialDropout2D" or "SpatialDropout3D" or
            "GaussianDropout" or "GaussianNoise" or "ActivityRegularization" => OperatorCategory.Custom,

            _ => OperatorCategory.Custom
        };
    }

    /// <summary>
    /// Maps a Keras dtype string (for example, <c>"float32"</c> or <c>"int64"</c>) to <see cref="DataType"/>.
    /// </summary>
    public static DataType MapDataType(string? dtype)
    {
        if (string.IsNullOrWhiteSpace(dtype))
        {
            return DataType.Float32;
        }

        return dtype.ToLowerInvariant() switch
        {
            "float32" or "float" => DataType.Float32,
            "float16" or "half" => DataType.Float16,
            "bfloat16" => DataType.BFloat16,
            "float64" or "double" => DataType.Float64,
            "int64" or "int64_t" => DataType.Int64,
            "int32" or "int" => DataType.Int32,
            "int16" => DataType.Int16,
            "int8" => DataType.Int8,
            "uint8" => DataType.UInt8,
            "uint16" => DataType.UInt16,
            "uint32" => DataType.UInt32,
            "uint64" => DataType.UInt64,
            "bool" or "boolean" => DataType.Bool,
            _ => DataType.Float32
        };
    }
}

