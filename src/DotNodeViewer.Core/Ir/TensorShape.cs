using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotNodeViewer.Core.Ir;

/// <summary>
/// Represents a single dimension along an axis of a <see cref="TensorShape"/>.
/// </summary>
/// <remarks>
/// <see cref="TensorDimension"/> is a closed discriminated union consisting of <see cref="Fixed"/>,
/// <see cref="Symbolic"/>, and <see cref="Dynamic"/>.
/// </remarks>
public abstract record TensorDimension
{
    private TensorDimension()
    {
    }

    /// <summary>
    /// Formats the dimension as a canonical string (<c>"224"</c> for fixed, the symbol name for symbolic, or <c>"?"</c> for dynamic).
    /// </summary>
    /// <returns>The canonical string representation of this dimension.</returns>
    public string ToDisplayString() => this switch
    {
        Fixed fixedDim => fixedDim.Value.ToString(CultureInfo.InvariantCulture),
        Symbolic symbolicDim => symbolicDim.Name,
        Dynamic => "?",
        _ => "?",
    };

    /// <summary>
    /// Represents a statically known, fixed-size dimension.
    /// </summary>
    /// <param name="Value">The non-negative size of the dimension.</param>
    public sealed record Fixed(long Value) : TensorDimension;

    /// <summary>
    /// Represents a named dynamic or symbolic dimension (for example, <c>"batch"</c> or <c>"s0"</c>).
    /// </summary>
    /// <param name="Name">The symbolic name of the dimension.</param>
    public sealed record Symbolic(string Name) : TensorDimension;

    /// <summary>
    /// Represents an unnamed dynamic or wildcard dimension (for example, <c>-1</c> or <c>?</c>).
    /// </summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Dynamic is the frozen IR discriminated-union case name.")]
    public sealed record Dynamic : TensorDimension
    {
        /// <summary>
        /// Gets a shared singleton instance of an unnamed dynamic dimension.
        /// </summary>
        /// <value>A shared <see cref="Dynamic"/> instance.</value>
        public static Dynamic Instance { get; } = new();
    }
}

/// <summary>
/// Represents the ordered dimensions of a ranked tensor.
/// </summary>
/// <param name="Dimensions">The ordered list of dimensions. An empty list represents a rank-0 scalar tensor.</param>
public sealed record TensorShape(IReadOnlyList<TensorDimension> Dimensions)
{
    /// <summary>
    /// Gets a shared <see cref="TensorShape"/> instance representing a rank-0 scalar tensor.
    /// </summary>
    /// <value>A rank-0 scalar <see cref="TensorShape"/>.</value>
    public static TensorShape Scalar { get; } = new(Array.Empty<TensorDimension>());

    /// <summary>
    /// Gets the number of dimensions (rank) of the tensor.
    /// </summary>
    /// <value>The count of elements in <see cref="Dimensions"/>.</value>
    public int Rank => Dimensions.Count;

    /// <summary>
    /// Gets a value that indicates whether this shape represents a rank-0 scalar tensor.
    /// </summary>
    /// <value><see langword="true"/> if <see cref="Dimensions"/> is empty; otherwise, <see langword="false"/>.</value>
    public bool IsScalar => Dimensions.Count == 0;

    /// <summary>
    /// Formats the tensor shape as a bracketed dimension list (for example, <c>"[batch, 3, 224, 224]"</c> or <c>"[]"</c> for scalars).
    /// </summary>
    /// <returns>A human-readable string representation of the shape.</returns>
    public string ToDisplayString()
    {
        if (Dimensions.Count == 0)
        {
            return "[]";
        }

        string[] parts = new string[Dimensions.Count];
        for (int i = 0; i < Dimensions.Count; i++)
        {
            parts[i] = Dimensions[i].ToDisplayString();
        }

        return $"[{string.Join(", ", parts)}]";
    }
}

