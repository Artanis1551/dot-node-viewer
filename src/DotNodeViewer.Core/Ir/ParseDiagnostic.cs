using System.Diagnostics.CodeAnalysis;

namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Specifies the severity level of a <see cref="ParseDiagnostic"/> emitted during model parsing.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error is the canonical severity level name in the frozen IR contract.")]
public enum DiagnosticSeverity
{
    /// <summary>
    /// Indicates an informational note about model structure or fallback behavior.
    /// </summary>
    Info,

    /// <summary>
    /// Indicates a non-fatal warning where partial metadata or weight slices could not be extracted.
    /// </summary>
    Warning,

    /// <summary>
    /// Indicates a recoverable error encountered while parsing a specific node, attribute, or subgraph.
    /// </summary>
    Error,
}

/// <summary>
/// Represents a non-fatal diagnostic message emitted while parsing a neural network model.
/// </summary>
/// <param name="Severity">One of the enumeration values that specifies the diagnostic severity.</param>
/// <param name="Code">A stable machine-readable diagnostic code (for example, <c>"DNV_TORCH_SCRIPT_FALLBACK"</c>).</param>
/// <param name="Message">A human-readable description explaining the condition.</param>
/// <param name="Location">An optional node name, tensor name, or byte offset identifying where the diagnostic occurred.</param>
public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Location = null);

