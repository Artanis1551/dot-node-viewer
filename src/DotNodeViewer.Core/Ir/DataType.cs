using System.Diagnostics.CodeAnalysis;

namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Specifies the element data type of a tensor or scalar attribute in the Unified Graph IR.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "DataType enum members intentionally represent numeric and primitive tensor element types.")]
public enum DataType
{
    /// <summary>
    /// An unspecified or unknown element type.
    /// </summary>
    Unknown,

    /// <summary>
    /// An 8-bit boolean value.
    /// </summary>
    Bool,

    /// <summary>
    /// A signed 4-bit integer value.
    /// </summary>
    Int4,

    /// <summary>
    /// An unsigned 4-bit integer value.
    /// </summary>
    UInt4,

    /// <summary>
    /// A signed 8-bit integer value.
    /// </summary>
    Int8,

    /// <summary>
    /// An unsigned 8-bit integer value.
    /// </summary>
    UInt8,

    /// <summary>
    /// A signed 16-bit integer value.
    /// </summary>
    Int16,

    /// <summary>
    /// An unsigned 16-bit integer value.
    /// </summary>
    UInt16,

    /// <summary>
    /// A signed 32-bit integer value.
    /// </summary>
    Int32,

    /// <summary>
    /// An unsigned 32-bit integer value.
    /// </summary>
    UInt32,

    /// <summary>
    /// A signed 64-bit integer value.
    /// </summary>
    Int64,

    /// <summary>
    /// An unsigned 64-bit integer value.
    /// </summary>
    UInt64,

    /// <summary>
    /// An 8-bit floating-point value using the E4M3 format (1 sign bit, 4 exponent bits, 3 mantissa bits).
    /// </summary>
    Float8E4M3,

    /// <summary>
    /// An 8-bit floating-point value using the E5M2 format (1 sign bit, 5 exponent bits, 2 mantissa bits).
    /// </summary>
    Float8E5M2,

    /// <summary>
    /// An IEEE 754 16-bit half-precision floating-point value.
    /// </summary>
    Float16,

    /// <summary>
    /// A 16-bit brain floating-point (<c>bfloat16</c>) value.
    /// </summary>
    BFloat16,

    /// <summary>
    /// An IEEE 754 32-bit single-precision floating-point value.
    /// </summary>
    Float32,

    /// <summary>
    /// An IEEE 754 64-bit double-precision floating-point value.
    /// </summary>
    Float64,

    /// <summary>
    /// A 64-bit complex number composed of two 32-bit floating-point values.
    /// </summary>
    Complex64,

    /// <summary>
    /// A 128-bit complex number composed of two 64-bit floating-point values.
    /// </summary>
    Complex128,

    /// <summary>
    /// A variable-length UTF-8 or byte string element.
    /// </summary>
    String,

    /// <summary>
    /// An opaque runtime resource handle (such as TensorFlow <c>DT_RESOURCE</c>).
    /// </summary>
    Resource,

    /// <summary>
    /// A dynamic variant container element (such as TensorFlow <c>DT_VARIANT</c>).
    /// </summary>
    Variant,

    /// <summary>
    /// A quantized signed 8-bit integer value (<c>qint8</c>).
    /// </summary>
    QuantizedInt8,

    /// <summary>
    /// A quantized unsigned 8-bit integer value (<c>quint8</c>).
    /// </summary>
    QuantizedUInt8,

    /// <summary>
    /// A quantized signed 16-bit integer value (<c>qint16</c>).
    /// </summary>
    QuantizedInt16,

    /// <summary>
    /// A quantized signed 32-bit integer value (<c>qint32</c>).
    /// </summary>
    QuantizedInt32,
}

