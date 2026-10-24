using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.IO.Pickle;

/// <summary>
/// Base class for all Python object representations decoded by <see cref="PickleMachine"/>.
/// </summary>
internal abstract record PyObject;

/// <summary>
/// Represents Python <c>None</c>.
/// </summary>
internal sealed record PyNone : PyObject
{
    public static readonly PyNone Instance = new();
    private PyNone() { }
    public override string ToString() => "None";
}

/// <summary>
/// Represents a Python boolean value.
/// </summary>
internal sealed record PyBool(bool Value) : PyObject
{
    public override string ToString() => Value ? "True" : "False";
}

/// <summary>
/// Represents a Python integer value.
/// </summary>
internal sealed record PyInt(long Value) : PyObject
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents a Python floating-point value.
/// </summary>
internal sealed record PyFloat(double Value) : PyObject
{
    public override string ToString() => Value.ToString("G", CultureInfo.InvariantCulture);
}

/// <summary>
/// Represents a Python string (ASCII or Unicode) value.
/// </summary>
internal sealed record PyString(string Value) : PyObject
{
    public override string ToString() => $"\"{Value}\"";
}

/// <summary>
/// Represents raw Python bytes.
/// </summary>
internal sealed record PyBytes(ReadOnlyMemory<byte> Value) : PyObject
{
    public override string ToString() => $"<bytes: {Value.Length}>";
}

/// <summary>
/// Represents an immutable Python tuple of items.
/// </summary>
internal sealed record PyTuple(IReadOnlyList<PyObject> Items) : PyObject
{
    public override string ToString() => $"({string.Join(", ", Items.Select(x => x.ToString()))})";
}

/// <summary>
/// Represents a mutable Python list of items.
/// </summary>
internal sealed record PyList(List<PyObject> Items) : PyObject
{
    public override string ToString() => $"[{string.Join(", ", Items.Select(x => x.ToString()))}]";
}

/// <summary>
/// Represents a Python dictionary mapping keys to values.
/// </summary>
internal sealed record PyDict : PyObject
{
    /// <summary>
    /// Gets the dictionary items.
    /// </summary>
    public Dictionary<PyObject, PyObject> Items { get; } = new();

    /// <summary>
    /// Attempts to look up a string-keyed entry in the dictionary.
    /// </summary>
    public bool TryGetStringKey(string key, [NotNullWhen(true)] out PyObject? value)
    {
        foreach (var (k, v) in Items)
        {
            if (k is PyString s && string.Equals(s.Value, key, StringComparison.Ordinal))
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    public override string ToString() => $"{{{string.Join(", ", Items.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
}

/// <summary>
/// Represents a symbolic Python global reference (<c>module.name</c>).
/// </summary>
internal sealed record PyGlobal(string Module, string Name) : PyObject
{
    public override string ToString() => $"{Module}.{Name}";
}

/// <summary>
/// Represents an unpickled Python class instance.
/// </summary>
internal sealed record PyInstance(PyGlobal Cls, Dictionary<string, PyObject> Dict, PyObject? State) : PyObject
{
    public override string ToString() => $"{Cls}({Dict.Count} attributes)";
}

/// <summary>
/// Represents a resolved PyTorch tensor storage reference located in a ZIP container entry.
/// </summary>
internal sealed record PyStorageRef(
    string StorageType,
    string Key,
    string Device,
    long Numel,
    DataType DataType,
    long StreamOffset,
    long ByteLength) : PyObject;

/// <summary>
/// Represents a fully reconstructed PyTorch tensor with a lazy <see cref="TensorDataSlice"/>.
/// </summary>
internal sealed record PyTensor(
    TensorDataSlice Slice,
    TensorShape Shape,
    DataType DataType,
    IReadOnlyList<long>? Strides,
    long StorageOffset,
    string StorageKey) : PyObject;
