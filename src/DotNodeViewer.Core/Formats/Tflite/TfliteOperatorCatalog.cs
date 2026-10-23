using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Tflite;

/// <summary>
/// Maps TensorFlow Lite (<c>TFL3</c>) <c>BuiltinOperator</c> codes, <c>TensorType</c> codes, and enum options into the Unified Graph IR.
/// </summary>
internal static class TfliteOperatorCatalog
{
    /// <summary>
    /// Resolves the canonical TFLite operator name from its <c>BuiltinOperator</c> code and optional <paramref name="customCode"/>.
    /// </summary>
    /// <param name="builtinCode">The resolved <c>BuiltinOperator</c> code.</param>
    /// <param name="customCode">The custom operator code string when <paramref name="builtinCode"/> is <c>32</c> (<c>CUSTOM</c>).</param>
    /// <returns>The operator display name.</returns>
    public static string GetOperatorName(int builtinCode, string? customCode = null)
    {
        if (builtinCode == 32 && !string.IsNullOrWhiteSpace(customCode))
        {
            return $"CUSTOM({customCode})";
        }

        return builtinCode switch
        {
            0 => "ADD",
            1 => "AVERAGE_POOL_2D",
            2 => "CONCATENATION",
            3 => "CONV_2D",
            4 => "DEPTHWISE_CONV_2D",
            5 => "DEPTH_TO_SPACE",
            6 => "DEQUANTIZE",
            7 => "EMBEDDING_LOOKUP",
            8 => "FLOOR",
            9 => "FULLY_CONNECTED",
            10 => "HASHTABLE_LOOKUP",
            11 => "L2_NORMALIZATION",
            12 => "L2_POOL_2D",
            13 => "LOCAL_RESPONSE_NORMALIZATION",
            14 => "LOGISTIC",
            15 => "LSH_PROJECTION",
            16 => "LSTM",
            17 => "MAX_POOL_2D",
            18 => "MUL",
            19 => "RELU",
            20 => "RELU_N1_TO_1",
            21 => "RELU6",
            22 => "RESHAPE",
            23 => "RESIZE_BILINEAR",
            24 => "RNN",
            25 => "SOFTMAX",
            26 => "SPACE_TO_DEPTH",
            27 => "SVDF",
            28 => "TANH",
            29 => "CONCAT_EMBEDDINGS",
            30 => "SKIP_GRAM",
            31 => "CALL",
            32 => "CUSTOM",
            33 => "EMBEDDING_LOOKUP_SPARSE",
            34 => "PAD",
            35 => "UNIDIRECTIONAL_SEQUENCE_RNN",
            36 => "GATHER",
            37 => "BATCH_TO_SPACE_ND",
            38 => "SPACE_TO_BATCH_ND",
            39 => "TRANSPOSE",
            40 => "MEAN",
            41 => "SUB",
            42 => "DIV",
            43 => "SQUEEZE",
            44 => "UNIDIRECTIONAL_SEQUENCE_LSTM",
            45 => "STRIDED_SLICE",
            46 => "BIDIRECTIONAL_SEQUENCE_RNN",
            47 => "EXP",
            48 => "TOPK_V2",
            49 => "SPLIT",
            50 => "LOG_SOFTMAX",
            51 => "DELEGATE",
            52 => "BIDIRECTIONAL_SEQUENCE_LSTM",
            53 => "CAST",
            54 => "PRELU",
            55 => "MAXIMUM",
            56 => "ARG_MAX",
            57 => "MINIMUM",
            58 => "LESS",
            59 => "NEG",
            60 => "PADV2",
            61 => "GREATER",
            62 => "GREATER_EQUAL",
            63 => "LESS_EQUAL",
            64 => "SELECT",
            65 => "SLICE",
            66 => "SIN",
            67 => "TRANSPOSE_CONV",
            68 => "SPARSE_TO_DENSE",
            69 => "TILE",
            70 => "EXPAND_DIMS",
            71 => "EQUAL",
            72 => "NOT_EQUAL",
            73 => "LOG",
            74 => "SUM",
            75 => "SQRT",
            76 => "RSQRT",
            77 => "SHAPE",
            78 => "POW",
            79 => "ARG_MIN",
            80 => "FAKE_QUANT",
            81 => "REDUCE_PROD",
            82 => "REDUCE_MAX",
            83 => "PACK",
            84 => "LOGICAL_OR",
            85 => "ONE_HOT",
            86 => "LOGICAL_AND",
            87 => "LOGICAL_NOT",
            88 => "UNPACK",
            89 => "REDUCE_MIN",
            90 => "FLOOR_DIV",
            91 => "REDUCE_ANY",
            92 => "SQUARE",
            93 => "ZEROS_LIKE",
            94 => "FILL",
            95 => "FLOOR_MOD",
            96 => "RANGE",
            97 => "RESIZE_NEAREST_NEIGHBOR",
            98 => "LEAKY_RELU",
            99 => "SQUARED_DIFFERENCE",
            100 => "MIRROR_PAD",
            101 => "ABS",
            102 => "SPLIT_V",
            103 => "UNIQUE",
            104 => "CEIL",
            105 => "REVERSE_V2",
            106 => "ADD_N",
            107 => "GATHER_ND",
            108 => "COS",
            109 => "WHERE",
            110 => "RANK",
            111 => "ELU",
            112 => "REVERSE_SEQUENCE",
            113 => "MATRIX_DIAG",
            114 => "QUANTIZE",
            115 => "MATRIX_SET_DIAG",
            116 => "ROUND",
            117 => "HARD_SWISH",
            118 => "IF",
            119 => "WHILE",
            120 => "NON_MAX_SUPPRESSION_V4",
            121 => "NON_MAX_SUPPRESSION_V5",
            122 => "SCATTER_ND",
            123 => "SELECT_V2",
            124 => "DENSIFY",
            125 => "SEGMENT_SUM",
            126 => "BATCH_MATMUL",
            128 => "CUMSUM",
            129 => "CALL_ONCE",
            130 => "BROADCAST_TO",
            131 => "RFFT2D",
            132 => "CONV_3D",
            133 => "IMAG",
            134 => "REAL",
            135 => "COMPLEX_ABS",
            136 => "HASHTABLE",
            137 => "HASHTABLE_FIND",
            138 => "HASHTABLE_IMPORT",
            139 => "HASHTABLE_SIZE",
            140 => "REDUCE_ALL",
            141 => "CONV_3D_TRANSPOSE",
            142 => "VAR_HANDLE",
            143 => "READ_VARIABLE",
            144 => "ASSIGN_VARIABLE",
            145 => "BROADCAST_ARGS",
            146 => "RANDOM_STANDARD_NORMAL",
            147 => "BUCKETIZE",
            148 => "RANDOM_UNIFORM",
            149 => "MULTINOMIAL",
            150 => "GELU",
            151 => "DYNAMIC_UPDATE_SLICE",
            152 => "RELU_0_TO_1",
            153 => "UNSORTED_SEGMENT_PROD",
            154 => "UNSORTED_SEGMENT_MAX",
            155 => "UNSORTED_SEGMENT_SUM",
            156 => "ATAN2",
            157 => "UNSORTED_SEGMENT_MIN",
            158 => "SIGN",
            159 => "BITCAST",
            160 => "BITWISE_XOR",
            161 => "RIGHT_SHIFT",
            _ => $"OPCODE_{builtinCode}",
        };
    }

    /// <summary>
    /// Classifies a TFLite <c>BuiltinOperator</c> code into a semantic <see cref="OperatorCategory"/>.
    /// </summary>
    /// <param name="builtinCode">The resolved <c>BuiltinOperator</c> code.</param>
    /// <returns>The mapped <see cref="OperatorCategory"/>.</returns>
    public static OperatorCategory Categorize(int builtinCode) => builtinCode switch
    {
        // Core layers
        3 or 4 or 7 or 9 or 16 or 24 or 27 or 33 or 35 or 36 or 44 or 46 or 52 or 67 or 107 or 122 or 126 or 132 or 141
            => OperatorCategory.Layer,

        // Activations
        14 or 19 or 20 or 21 or 25 or 28 or 50 or 54 or 98 or 111 or 117 or 150 or 152
            => OperatorCategory.Activation,

        // Normalization
        11 or 13
            => OperatorCategory.Normalization,

        // Pooling
        1 or 12 or 17
            => OperatorCategory.Pooling,

        // Quantization
        6 or 80 or 114
            => OperatorCategory.Quantization,

        // Shape & layout manipulation
        2 or 5 or 22 or 23 or 26 or 34 or 37 or 38 or 39 or 43 or 45 or 49 or 60 or 65 or 69 or 70 or 77 or 83 or 88 or 97 or 100 or 102 or 105 or 110 or 112 or 130 or 151
            => OperatorCategory.Shape,

        // Control flow
        31 or 118 or 119 or 129
            => OperatorCategory.ControlFlow,

        // Custom / hardware delegate operators
        32 or 51
            => OperatorCategory.Custom,

        // Variable / resource data movement
        136 or 137 or 138 or 139 or 142 or 143 or 144
            => OperatorCategory.Data,

        // Arithmetic, reductions, comparisons & element-wise tensor ops
        0 or 8 or 18 or 40 or 41 or 42 or 47 or 48 or 53 or 55 or 56 or 57 or 58 or 59 or 61 or 62 or 63 or 64 or 66 or 71 or 72 or 73 or 74 or 75 or 76 or 78 or 79 or 81 or 82 or 84 or 85 or 86 or 87 or 89 or 90 or 91 or 92 or 93 or 94 or 95 or 96 or 99 or 101 or 104 or 106 or 108 or 109 or 116 or 123 or 125 or 128 or 140 or 156 or 158 or 159 or 160 or 161
            => OperatorCategory.Tensor,

        _ => OperatorCategory.Unknown,
    };

    /// <summary>
    /// Maps a TFLite <c>TensorType</c> integer code to the Unified Graph IR <see cref="DataType"/>.
    /// </summary>
    /// <param name="tfliteTensorType">The integer value of <c>Tensor.type</c>.</param>
    /// <returns>The corresponding <see cref="DataType"/>.</returns>
    public static DataType MapDataType(int tfliteTensorType) => tfliteTensorType switch
    {
        0 => DataType.Float32,
        1 => DataType.Float16,
        2 => DataType.Int32,
        3 => DataType.UInt8,
        4 => DataType.Int64,
        5 => DataType.String,
        6 => DataType.Bool,
        7 => DataType.Int16,
        8 => DataType.Complex64,
        9 => DataType.Int8,
        10 => DataType.Float64,
        11 => DataType.Complex128,
        12 => DataType.UInt64,
        13 => DataType.Resource,
        14 => DataType.Variant,
        15 => DataType.UInt32,
        16 => DataType.UInt16,
        17 => DataType.Int4,
        18 => DataType.BFloat16,
        _ => DataType.Unknown,
    };

    /// <summary>
    /// Maps a TFLite <c>ActivationFunctionType</c> code to its canonical string name.
    /// </summary>
    public static string FormatActivationFunction(byte code) => code switch
    {
        0 => "NONE",
        1 => "RELU",
        2 => "RELU_N1_TO_1",
        3 => "RELU6",
        4 => "TANH",
        5 => "SIGN_BIT",
        _ => $"UNKNOWN_{code}",
    };

    /// <summary>
    /// Maps a TFLite <c>Padding</c> enum code to its canonical string name.
    /// </summary>
    public static string FormatPadding(byte code) => code switch
    {
        0 => "SAME",
        1 => "VALID",
        _ => $"UNKNOWN_{code}",
    };

    /// <summary>
    /// Gets the formal input port name for a TFLite operator at the specified positional <paramref name="index"/>.
    /// </summary>
    public static string GetInputPortName(int builtinCode, int index) => builtinCode switch
    {
        3 or 4 or 67 => index switch // CONV_2D, DEPTHWISE_CONV_2D, TRANSPOSE_CONV
        {
            0 => "input",
            1 => "filter",
            2 => "bias",
            _ => $"input_{index}",
        },
        9 => index switch // FULLY_CONNECTED
        {
            0 => "input",
            1 => "weights",
            2 => "bias",
            _ => $"input_{index}",
        },
        0 or 18 or 41 or 42 => index switch // ADD, MUL, SUB, DIV
        {
            0 => "x",
            1 => "y",
            _ => $"input_{index}",
        },
        22 => index switch // RESHAPE
        {
            0 => "tensor",
            1 => "shape",
            _ => $"input_{index}",
        },
        40 => index switch // MEAN
        {
            0 => "input",
            1 => "axis",
            _ => $"input_{index}",
        },
        45 => index switch // STRIDED_SLICE
        {
            0 => "input",
            1 => "begin",
            2 => "end",
            3 => "strides",
            _ => $"input_{index}",
        },
        118 => index == 0 ? "cond" : $"input_{index}", // IF
        _ => $"input_{index}",
    };

    /// <summary>
    /// Gets the formal output port name for a TFLite operator at the specified positional <paramref name="index"/>.
    /// </summary>
    public static string GetOutputPortName(int builtinCode, int index) =>
        index == 0 ? "output" : $"output_{index}";
}

