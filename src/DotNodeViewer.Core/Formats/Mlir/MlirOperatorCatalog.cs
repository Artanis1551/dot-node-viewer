using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Formats.Mlir;

/// <summary>
/// Maps dialect-qualified MLIR operations across ML compiler dialects (<c>torch.*</c>,
/// <c>torch.quantized_decomposed.*</c>, <c>torch.torchao.*</c>, <c>stablehlo.*</c>, <c>vhlo.*</c>,
/// <c>mhlo.*</c>, <c>chlo.*</c>, <c>xla.*</c>, <c>tt.*</c>, <c>ttg.*</c>, <c>ttng.*</c>,
/// <c>tosa.*</c>, <c>linalg.*</c>, <c>iree_input.*</c>, <c>flow.*</c>, <c>hal.*</c>,
/// <c>arith.*</c>, <c>math.*</c>, <c>tensor.*</c>, <c>memref.*</c>, <c>scf.*</c>, <c>cf.*</c>,
/// <c>func.*</c>, <c>builtin.*</c>) into <see cref="OperatorInfo"/> records.
/// </summary>
internal static class MlirOperatorCatalog
{
    /// <summary>
    /// Creates an <see cref="OperatorInfo"/> with extracted dialect domain and semantic <see cref="OperatorCategory"/>.
    /// </summary>
    public static OperatorInfo CreateOperatorInfo(string qualifiedOpName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedOpName))
        {
            return new OperatorInfo("unknown", OperatorCategory.Unknown);
        }

        (string domain, string shortName) = SplitDialectAndOp(qualifiedOpName);
        OperatorCategory category = Categorize(domain, shortName, qualifiedOpName);
        return new OperatorInfo(qualifiedOpName, category, domain);
    }

    /// <summary>
    /// Splits a dialect-qualified operation name into its dialect domain and operation mnemonic.
    /// </summary>
    public static (string Domain, string ShortName) SplitDialectAndOp(string qualifiedOpName)
    {
        if (qualifiedOpName.StartsWith("torch.quantized_decomposed.", StringComparison.Ordinal))
        {
            return ("torch.quantized_decomposed", qualifiedOpName["torch.quantized_decomposed.".Length..]);
        }

        if (qualifiedOpName.StartsWith("torch.torchao.", StringComparison.Ordinal))
        {
            return ("torch.torchao", qualifiedOpName["torch.torchao.".Length..]);
        }

        int firstDot = qualifiedOpName.IndexOf('.');
        if (firstDot <= 0 || firstDot >= qualifiedOpName.Length - 1)
        {
            return ("builtin", qualifiedOpName);
        }

        string domain = qualifiedOpName[..firstDot];
        string op = qualifiedOpName[(firstDot + 1)..];
        return (domain, op);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the operation represents a constant or parameter materialization.
    /// </summary>
    public static bool IsConstantOp(string qualifiedOpName)
    {
        (string domain, string op) = SplitDialectAndOp(qualifiedOpName);
        return op is "constant" or "const" or "constant_like" or "vtensor.literal" or "tensor.literal"
            || (domain == "torch" && op.StartsWith("constant.", StringComparison.Ordinal))
            || (domain == "vhlo" && op.StartsWith("constant", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the operation terminates a function or region by returning/yielding values.
    /// </summary>
    public static bool IsReturnOrYieldOp(string qualifiedOpName) =>
        qualifiedOpName is "func.return" or "tt.return" or "llvm.return" or "util.return"
            or "return" or "scf.yield" or "scf.condition" or "stablehlo.return"
            or "mhlo.return" or "vhlo.return_v1" or "vhlo.return"
            or "tosa.yield" or "linalg.yield" or "iree_input.return" or "flow.return";

    private static OperatorCategory Categorize(string domain, string shortName, string qualifiedOpName)
    {
        string d = domain.ToLowerInvariant();
        string op = shortName.ToLowerInvariant();

        if (d is "torch.quantized_decomposed" or "torch.torchao")
        {
            return OperatorCategory.Quantization;
        }

        if (IsConstantOp(qualifiedOpName))
        {
            return OperatorCategory.Constant;
        }

        if (d is "scf" or "cf" or "func" or "builtin")
        {
            if (d == "builtin" && op == "unrealized_conversion_cast")
            {
                return OperatorCategory.Shape;
            }

            return OperatorCategory.ControlFlow;
        }

        if (op is "if" or "while" or "for" or "case" or "cond_br" or "br" or "call" or "return" or "yield")
        {
            return OperatorCategory.ControlFlow;
        }

        // Quantization across dialects
        if (op.Contains("quantize", StringComparison.Ordinal)
            || op.Contains("dequantize", StringComparison.Ordinal)
            || op is "rescale" or "uniform_quantize" or "uniform_dequantize" or "fake_quant"
            || op.StartsWith("choose_qparams", StringComparison.Ordinal))
        {
            return OperatorCategory.Quantization;
        }

        // Triton GPU kernels (tt.*, ttg.*, ttng.*)
        if (d is "tt" or "ttg" or "ttng")
        {
            return op switch
            {
                "func" or "return" or "call" => OperatorCategory.ControlFlow,
                "dot" or "dot_scaled" or "warp_specialize" => OperatorCategory.Layer,
                "load" or "store" or "atomic_rmw" or "atomic_cas" or "make_tensor_ptr"
                    or "advance" or "descriptor_load" or "descriptor_store" => OperatorCategory.Data,
                "reshape" or "trans" or "broadcast" or "expand_dims" or "splat"
                    or "cat" or "split" or "join" or "view" or "convert_layout" => OperatorCategory.Shape,
                "make_range" or "get_program_id" or "get_num_programs" => OperatorCategory.Constant,
                _ => OperatorCategory.Tensor,
            };
        }

        // IREE / HAL / Flow data movement & dispatch
        if (d is "iree_input" or "flow" or "hal")
        {
            if (op.Contains("dispatch", StringComparison.Ordinal)
                || op.Contains("command_buffer", StringComparison.Ordinal)
                || op.Contains("executable", StringComparison.Ordinal))
            {
                return OperatorCategory.Custom;
            }

            if (op.Contains("tensor.load", StringComparison.Ordinal)
                || op.Contains("tensor.store", StringComparison.Ordinal)
                || op.Contains("buffer", StringComparison.Ordinal)
                || op.Contains("import", StringComparison.Ordinal)
                || op.Contains("export", StringComparison.Ordinal)
                || op.Contains("transfer", StringComparison.Ordinal))
            {
                return OperatorCategory.Data;
            }
        }

        // Pooling
        if (op.Contains("pool", StringComparison.Ordinal)
            || op is "reduce_window" or "aten.max_pool2d" or "aten.avg_pool2d" or "aten.adaptive_avg_pool2d")
        {
            return OperatorCategory.Pooling;
        }

        // Normalization
        if (op.Contains("batch_norm", StringComparison.Ordinal)
            || op.Contains("layer_norm", StringComparison.Ordinal)
            || op.Contains("group_norm", StringComparison.Ordinal)
            || op.Contains("instance_norm", StringComparison.Ordinal)
            || op.Contains("rms_norm", StringComparison.Ordinal)
            || op is "batch_norm_inference" or "batch_norm_training")
        {
            return OperatorCategory.Normalization;
        }

        // Activation
        if (op is "relu" or "relu6" or "leaky_relu" or "gelu" or "silu" or "swish"
            or "sigmoid" or "logistic" or "tanh" or "softmax" or "log_softmax"
            or "clamp" or "elu" or "selu" or "hardswish" or "hardsigmoid"
            or "aten.relu" or "aten.gelu" or "aten.sigmoid" or "aten.tanh" or "aten.softmax.int"
            || op.StartsWith("aten.relu", StringComparison.Ordinal)
            || op.StartsWith("aten.silu", StringComparison.Ordinal))
        {
            return OperatorCategory.Activation;
        }

        // Core Compute Layers
        if (op.Contains("conv", StringComparison.Ordinal)
            || op.Contains("matmul", StringComparison.Ordinal)
            || op.Contains("dot_general", StringComparison.Ordinal)
            || op.Contains("fully_connected", StringComparison.Ordinal)
            || op.Contains("linear", StringComparison.Ordinal)
            || op is "dot" or "mm" or "bmm" or "addmm" or "aten.mm" or "aten.bmm" or "aten.linear" or "aten.addmm"
            or "generic" or "rnn" or "lstm" or "gru" or "einsum" or "fft"
            || op.StartsWith("aten.conv", StringComparison.Ordinal))
        {
            return OperatorCategory.Layer;
        }

        // Shape / View / Layout
        if (op is "reshape" or "transpose" or "broadcast" or "broadcast_in_dim" or "dynamic_broadcast_in_dim"
            or "slice" or "dynamic_slice" or "concatenate" or "concat" or "pad" or "iota"
            or "squeeze" or "unsqueeze" or "flatten" or "permute" or "expand" or "repeat"
            or "gather" or "scatter" or "torch_gather" or "get_dimension_size" or "reverse"
            or "tile" or "extract_slice" or "insert_slice" or "collapse_shape" or "expand_shape"
            or "cast" or "bitcast_convert" or "convert"
            || d == "tensor"
            || d == "memref")
        {
            if (d == "memref" && op is "load" or "store" or "copy" or "alloc" or "dealloc")
            {
                return OperatorCategory.Data;
            }

            return OperatorCategory.Shape;
        }

        // Elementwise & reductions
        if (d is "arith" or "math" or "stablehlo" or "vhlo" or "mhlo" or "chlo" or "tosa" or "linalg" or "torch" or "xla")
        {
            return OperatorCategory.Tensor;
        }

        return OperatorCategory.Unknown;
    }
}
