using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.IO.Pickle;

/// <summary>
/// A safe, non-executing virtual machine for Python Pickle protocols 2 through 5.
/// Never invokes arbitrary code or reflection.
/// </summary>
internal sealed class PickleMachine
{
    private readonly Stream _stream;
    private readonly Func<PyObject, PyObject?>? _persistentLoad;
    private readonly List<PyObject> _stack = new();
    private readonly List<int> _marks = new();
    private readonly Dictionary<long, PyObject> _memo = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PickleMachine"/> class.
    /// </summary>
    /// <param name="stream">The readable pickle byte stream.</param>
    /// <param name="persistentLoad">Optional persistent loader callback for <c>BINPERSID</c> / <c>PERSID</c>.</param>
    public PickleMachine(Stream stream, Func<PyObject, PyObject?>? persistentLoad = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _persistentLoad = persistentLoad;
    }

    /// <summary>
    /// Executes the pickle VM until a <c>STOP</c> opcode is encountered and returns the root object.
    /// </summary>
    public PyObject Load()
    {
        while (true)
        {
            long opcodeOffset = _stream.CanSeek ? _stream.Position : -1;
            int b = _stream.ReadByte();
            if (b < 0)
            {
                throw new ModelParseException("Unexpected end of pickle stream before STOP opcode.", fileOffset: opcodeOffset);
            }

            byte opcode = (byte)b;
            switch (opcode)
            {
                case 0x80: // PROTO
                {
                    int proto = _stream.ReadByte();
                    if (proto < 0)
                    {
                        throw new ModelParseException("Truncated PROTO opcode in pickle stream.", fileOffset: opcodeOffset);
                    }
                    break;
                }

                case 0x95: // FRAME
                {
                    Span<byte> frameLenBuf = stackalloc byte[8];
                    ReadExact(frameLenBuf, opcodeOffset);
                    // Frame length is an optimization hint in pickle protocol 4; proceed.
                    break;
                }

                case 0x2E: // STOP ('.')
                {
                    if (_stack.Count == 0)
                    {
                        throw new ModelParseException("Pickle stack is empty at STOP opcode.", fileOffset: opcodeOffset);
                    }
                    return _stack[^1];
                }

                case 0x28: // MARK ('(')
                {
                    _marks.Add(_stack.Count);
                    break;
                }

                case 0x30: // POP ('0')
                {
                    Pop(opcodeOffset);
                    break;
                }

                case 0x31: // POP_MARK ('1')
                {
                    int mark = PopMark(opcodeOffset);
                    while (_stack.Count > mark)
                    {
                        _stack.RemoveAt(_stack.Count - 1);
                    }
                    break;
                }

                case 0x32: // DUP ('2')
                {
                    if (_stack.Count == 0)
                    {
                        throw new ModelParseException("Cannot DUP on empty pickle stack.", fileOffset: opcodeOffset);
                    }
                    _stack.Add(_stack[^1]);
                    break;
                }

                case 0x4E: // NONE ('N')
                {
                    _stack.Add(PyNone.Instance);
                    break;
                }

                case 0x88: // NEWTRUE
                {
                    _stack.Add(new PyBool(true));
                    break;
                }

                case 0x89: // NEWFALSE
                {
                    _stack.Add(new PyBool(false));
                    break;
                }

                case 0x49: // INT ('I')
                {
                    string line = ReadLine(opcodeOffset);
                    if (string.Equals(line, "01", StringComparison.Ordinal))
                    {
                        _stack.Add(new PyBool(true));
                    }
                    else if (string.Equals(line, "00", StringComparison.Ordinal))
                    {
                        _stack.Add(new PyBool(false));
                    }
                    else if (long.TryParse(line, CultureInfo.InvariantCulture, out long val))
                    {
                        _stack.Add(new PyInt(val));
                    }
                    else
                    {
                        _stack.Add(new PyInt(0));
                    }
                    break;
                }

                case 0x4A: // BININT ('J')
                {
                    Span<byte> buf = stackalloc byte[4];
                    ReadExact(buf, opcodeOffset);
                    _stack.Add(new PyInt(BinaryPrimitives.ReadInt32LittleEndian(buf)));
                    break;
                }

                case 0x4B: // BININT1 ('K')
                {
                    int val = _stream.ReadByte();
                    if (val < 0) throw new ModelParseException("Truncated BININT1.", fileOffset: opcodeOffset);
                    _stack.Add(new PyInt((byte)val));
                    break;
                }

                case 0x4D: // BININT2 ('M')
                {
                    Span<byte> buf = stackalloc byte[2];
                    ReadExact(buf, opcodeOffset);
                    _stack.Add(new PyInt(BinaryPrimitives.ReadUInt16LittleEndian(buf)));
                    break;
                }

                case 0x4C: // LONG ('L')
                {
                    string line = ReadLine(opcodeOffset);
                    if (line.EndsWith('L')) line = line[..^1];
                    if (long.TryParse(line, CultureInfo.InvariantCulture, out long val))
                    {
                        _stack.Add(new PyInt(val));
                    }
                    else if (BigInteger.TryParse(line, CultureInfo.InvariantCulture, out var bi))
                    {
                        _stack.Add(new PyInt((long)bi));
                    }
                    else
                    {
                        _stack.Add(new PyInt(0));
                    }
                    break;
                }

                case 0x8A: // LONG1
                {
                    int len = _stream.ReadByte();
                    if (len < 0) throw new ModelParseException("Truncated LONG1 length.", fileOffset: opcodeOffset);
                    if (len == 0)
                    {
                        _stack.Add(new PyInt(0));
                        break;
                    }
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyInt(DecodeSignedLittleEndian(bytes)));
                    break;
                }

                case 0x8B: // LONG4
                {
                    Span<byte> lenBuf = stackalloc byte[4];
                    ReadExact(lenBuf, opcodeOffset);
                    int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                    if (len < 0) throw new ModelParseException($"Invalid negative LONG4 length ({len}).", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyInt(DecodeSignedLittleEndian(bytes)));
                    break;
                }

                case 0x46: // FLOAT ('F')
                {
                    string line = ReadLine(opcodeOffset);
                    double val = double.TryParse(line, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
                    _stack.Add(new PyFloat(val));
                    break;
                }

                case 0x47: // BINFLOAT ('G')
                {
                    Span<byte> buf = stackalloc byte[8];
                    ReadExact(buf, opcodeOffset);
                    _stack.Add(new PyFloat(BinaryPrimitives.ReadDoubleBigEndian(buf)));
                    break;
                }

                case 0x53: // STRING ('S')
                {
                    string line = ReadLine(opcodeOffset);
                    string parsed = UnescapePythonString(line);
                    _stack.Add(new PyString(parsed));
                    break;
                }

                case 0x54: // BINSTRING ('T')
                {
                    Span<byte> lenBuf = stackalloc byte[4];
                    ReadExact(lenBuf, opcodeOffset);
                    int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                    if (len < 0) throw new ModelParseException($"Negative BINSTRING length ({len}).", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyString(Encoding.Latin1.GetString(bytes)));
                    break;
                }

                case 0x55: // SHORT_BINSTRING ('U')
                {
                    int len = _stream.ReadByte();
                    if (len < 0) throw new ModelParseException("Truncated SHORT_BINSTRING.", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyString(Encoding.Latin1.GetString(bytes)));
                    break;
                }

                case 0x8C: // SHORT_BINUTF8
                {
                    int len = _stream.ReadByte();
                    if (len < 0) throw new ModelParseException("Truncated SHORT_BINUTF8.", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyString(Encoding.UTF8.GetString(bytes)));
                    break;
                }

                case 0x58: // BINUNICODE ('X')
                {
                    Span<byte> lenBuf = stackalloc byte[4];
                    ReadExact(lenBuf, opcodeOffset);
                    uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyString(Encoding.UTF8.GetString(bytes)));
                    break;
                }

                case 0x8D: // BINUNICODE8
                {
                    Span<byte> lenBuf = stackalloc byte[8];
                    ReadExact(lenBuf, opcodeOffset);
                    ulong len = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);
                    if (len > int.MaxValue) throw new ModelParseException($"BINUNICODE8 exceeds max buffer limit ({len}).", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[(int)len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyString(Encoding.UTF8.GetString(bytes)));
                    break;
                }

                case 0x56: // UNICODE ('V')
                {
                    string line = ReadLine(opcodeOffset);
                    _stack.Add(new PyString(line));
                    break;
                }

                case 0x42: // BINBYTES ('B')
                {
                    Span<byte> lenBuf = stackalloc byte[4];
                    ReadExact(lenBuf, opcodeOffset);
                    uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyBytes(bytes));
                    break;
                }

                case 0x43: // SHORT_BINBYTES ('C')
                {
                    int len = _stream.ReadByte();
                    if (len < 0) throw new ModelParseException("Truncated SHORT_BINBYTES.", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyBytes(bytes));
                    break;
                }

                case 0x8E: // BINBYTES8
                case 0x96: // BYTEARRAY8
                {
                    Span<byte> lenBuf = stackalloc byte[8];
                    ReadExact(lenBuf, opcodeOffset);
                    ulong len = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);
                    if (len > int.MaxValue) throw new ModelParseException($"BINBYTES8 exceeds max length ({len}).", fileOffset: opcodeOffset);
                    byte[] bytes = new byte[(int)len];
                    ReadExact(bytes, opcodeOffset);
                    _stack.Add(new PyBytes(bytes));
                    break;
                }

                case 0x29: // EMPTY_TUPLE (')')
                {
                    _stack.Add(new PyTuple(Array.Empty<PyObject>()));
                    break;
                }

                case 0x85: // TUPLE1
                {
                    var item = Pop(opcodeOffset);
                    _stack.Add(new PyTuple(new[] { item }));
                    break;
                }

                case 0x86: // TUPLE2
                {
                    var bItem = Pop(opcodeOffset);
                    var aItem = Pop(opcodeOffset);
                    _stack.Add(new PyTuple(new[] { aItem, bItem }));
                    break;
                }

                case 0x87: // TUPLE3
                {
                    var cItem = Pop(opcodeOffset);
                    var bItem = Pop(opcodeOffset);
                    var aItem = Pop(opcodeOffset);
                    _stack.Add(new PyTuple(new[] { aItem, bItem, cItem }));
                    break;
                }

                case 0x74: // TUPLE ('t')
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    _stack.Add(new PyTuple(items));
                    break;
                }

                case 0x5D: // EMPTY_LIST (']')
                {
                    _stack.Add(new PyList(new List<PyObject>()));
                    break;
                }

                case 0x6C: // LIST ('l')
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    _stack.Add(new PyList(items));
                    break;
                }

                case 0x61: // APPEND ('a')
                {
                    var item = Pop(opcodeOffset);
                    var listObj = Pop(opcodeOffset);
                    if (listObj is PyList pyList)
                    {
                        pyList.Items.Add(item);
                        _stack.Add(pyList);
                    }
                    else
                    {
                        _stack.Add(listObj);
                    }
                    break;
                }

                case 0x65: // APPENDS ('e')
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    if (_stack.Count == 0 || _stack[^1] is not PyList targetList)
                    {
                        throw new ModelParseException("Expected PyList below MARK for APPENDS opcode.", fileOffset: opcodeOffset);
                    }
                    targetList.Items.AddRange(items);
                    break;
                }

                case 0x7D: // EMPTY_DICT ('}')
                {
                    _stack.Add(new PyDict());
                    break;
                }

                case 0x64: // DICT ('d')
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    var dict = new PyDict();
                    for (int i = 0; i + 1 < items.Count; i += 2)
                    {
                        dict.Items[items[i]] = items[i + 1];
                    }
                    _stack.Add(dict);
                    break;
                }

                case 0x73: // SETITEM ('s')
                {
                    var value = Pop(opcodeOffset);
                    var key = Pop(opcodeOffset);
                    var dictObj = Pop(opcodeOffset);
                    if (dictObj is PyDict dict)
                    {
                        dict.Items[key] = value;
                        _stack.Add(dict);
                    }
                    else
                    {
                        _stack.Add(dictObj);
                    }
                    break;
                }

                case 0x75: // SETITEMS ('u')
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    if (_stack.Count == 0 || _stack[^1] is not PyDict targetDict)
                    {
                        throw new ModelParseException("Expected PyDict below MARK for SETITEMS opcode.", fileOffset: opcodeOffset);
                    }
                    for (int i = 0; i + 1 < items.Count; i += 2)
                    {
                        targetDict.Items[items[i]] = items[i + 1];
                    }
                    break;
                }

                case 0x94: // MEMOIZE
                {
                    if (_stack.Count == 0)
                    {
                        throw new ModelParseException("Cannot MEMOIZE on empty pickle stack.", fileOffset: opcodeOffset);
                    }
                    _memo[_memo.Count] = _stack[^1];
                    break;
                }

                case 0x70: // PUT ('p')
                {
                    string line = ReadLine(opcodeOffset);
                    if (long.TryParse(line, CultureInfo.InvariantCulture, out long idx))
                    {
                        if (_stack.Count == 0) throw new ModelParseException("Empty stack on PUT.", fileOffset: opcodeOffset);
                        _memo[idx] = _stack[^1];
                    }
                    break;
                }

                case 0x71: // BINPUT ('q')
                {
                    int idx = _stream.ReadByte();
                    if (idx < 0) throw new ModelParseException("Truncated BINPUT.", fileOffset: opcodeOffset);
                    if (_stack.Count == 0) throw new ModelParseException("Empty stack on BINPUT.", fileOffset: opcodeOffset);
                    _memo[idx] = _stack[^1];
                    break;
                }

                case 0x72: // LONG_BINPUT ('r')
                {
                    Span<byte> buf = stackalloc byte[4];
                    ReadExact(buf, opcodeOffset);
                    uint idx = BinaryPrimitives.ReadUInt32LittleEndian(buf);
                    if (_stack.Count == 0) throw new ModelParseException("Empty stack on LONG_BINPUT.", fileOffset: opcodeOffset);
                    _memo[idx] = _stack[^1];
                    break;
                }

                case 0x67: // GET ('g')
                {
                    string line = ReadLine(opcodeOffset);
                    if (!long.TryParse(line, CultureInfo.InvariantCulture, out long idx) || !_memo.TryGetValue(idx, out var val))
                    {
                        throw new ModelParseException($"Pickle memo index '{line}' not found.", fileOffset: opcodeOffset);
                    }
                    _stack.Add(val);
                    break;
                }

                case 0x68: // BINGET ('h')
                {
                    int idx = _stream.ReadByte();
                    if (idx < 0 || !_memo.TryGetValue(idx, out var val))
                    {
                        throw new ModelParseException($"Pickle memo index {idx} not found.", fileOffset: opcodeOffset);
                    }
                    _stack.Add(val);
                    break;
                }

                case 0x6A: // LONG_BINGET ('j')
                {
                    Span<byte> buf = stackalloc byte[4];
                    ReadExact(buf, opcodeOffset);
                    uint idx = BinaryPrimitives.ReadUInt32LittleEndian(buf);
                    if (!_memo.TryGetValue(idx, out var val))
                    {
                        throw new ModelParseException($"Pickle memo index {idx} not found.", fileOffset: opcodeOffset);
                    }
                    _stack.Add(val);
                    break;
                }

                case 0x63: // GLOBAL ('c')
                {
                    string module = ReadLine(opcodeOffset);
                    string name = ReadLine(opcodeOffset);
                    _stack.Add(new PyGlobal(module, name));
                    break;
                }

                case 0x93: // STACK_GLOBAL
                {
                    var nameObj = Pop(opcodeOffset);
                    var moduleObj = Pop(opcodeOffset);
                    string name = nameObj is PyString ns ? ns.Value : nameObj.ToString()!;
                    string module = moduleObj is PyString ms ? ms.Value : moduleObj.ToString()!;
                    _stack.Add(new PyGlobal(module, name));
                    break;
                }

                case 0x81: // NEWOBJ
                {
                    var args = Pop(opcodeOffset);
                    var cls = Pop(opcodeOffset);
                    if (cls is PyGlobal glob)
                    {
                        _stack.Add(new PyInstance(glob, new Dictionary<string, PyObject>(StringComparer.Ordinal), null));
                    }
                    else
                    {
                        _stack.Add(new PyInstance(new PyGlobal("unknown", cls.ToString()!), new Dictionary<string, PyObject>(StringComparer.Ordinal), null));
                    }
                    break;
                }

                case 0x92: // NEWOBJ_EX
                {
                    var kwargs = Pop(opcodeOffset);
                    var args = Pop(opcodeOffset);
                    var cls = Pop(opcodeOffset);
                    if (cls is PyGlobal glob)
                    {
                        _stack.Add(new PyInstance(glob, new Dictionary<string, PyObject>(StringComparer.Ordinal), null));
                    }
                    else
                    {
                        _stack.Add(new PyInstance(new PyGlobal("unknown", cls.ToString()!), new Dictionary<string, PyObject>(StringComparer.Ordinal), null));
                    }
                    break;
                }

                case 0x62: // BUILD ('b')
                {
                    var state = Pop(opcodeOffset);
                    var obj = Pop(opcodeOffset);
                    if (obj is PyInstance inst)
                    {
                        ApplyStateToInstance(inst, state);
                        _stack.Add(inst);
                    }
                    else if (obj is PyDict dict && state is PyDict stateDict)
                    {
                        foreach (var kv in stateDict.Items)
                        {
                            dict.Items[kv.Key] = kv.Value;
                        }
                        _stack.Add(dict);
                    }
                    else
                    {
                        _stack.Add(obj);
                    }
                    break;
                }

                case 0x52: // REDUCE ('R')
                {
                    var argsObj = Pop(opcodeOffset);
                    var callableObj = Pop(opcodeOffset);
                    var argsTuple = argsObj as PyTuple ?? new PyTuple(new[] { argsObj });
                    var reduced = ReduceCallable(callableObj, argsTuple);
                    _stack.Add(reduced);
                    break;
                }

                case 0x51: // BINPERSID ('Q')
                {
                    var persid = Pop(opcodeOffset);
                    var loaded = _persistentLoad != null ? _persistentLoad(persid) : null;
                    _stack.Add(loaded ?? persid);
                    break;
                }

                case 0x50: // PERSID ('P')
                {
                    string pidStr = ReadLine(opcodeOffset);
                    var persid = new PyString(pidStr);
                    var loaded = _persistentLoad != null ? _persistentLoad(persid) : null;
                    _stack.Add(loaded ?? persid);
                    break;
                }

                case 0x8F: // EMPTY_SET
                {
                    _stack.Add(new PyList(new List<PyObject>()));
                    break;
                }

                case 0x90: // ADDITEMS
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    if (_stack.Count > 0 && _stack[^1] is PyList pyList)
                    {
                        pyList.Items.AddRange(items);
                    }
                    break;
                }

                case 0x91: // FROZENSET
                {
                    int mark = PopMark(opcodeOffset);
                    var items = PopRange(mark);
                    _stack.Add(new PyTuple(items));
                    break;
                }

                default:
                {
                    throw new ModelParseException(
                        $"Unsupported pickle opcode 0x{opcode:X2} at stream offset 0x{opcodeOffset:X}.",
                        fileOffset: opcodeOffset);
                }
            }
        }
    }

    private static void ApplyStateToInstance(PyInstance inst, PyObject state)
    {
        if (state is PyDict dict)
        {
            foreach (var kv in dict.Items)
            {
                if (kv.Key is PyString s)
                {
                    inst.Dict[s.Value] = kv.Value;
                }
            }
        }
        else if (state is PyTuple tuple && tuple.Items.Count > 0)
        {
            if (tuple.Items[0] is PyDict d0)
            {
                foreach (var kv in d0.Items)
                {
                    if (kv.Key is PyString s)
                    {
                        inst.Dict[s.Value] = kv.Value;
                    }
                }
            }
        }
    }

    private static PyObject ReduceCallable(PyObject callable, PyTuple args)
    {
        if (callable is PyGlobal glob)
        {
            // torch._utils._rebuild_tensor_v2 or torch._utils._rebuild_tensor
            if (string.Equals(glob.Module, "torch._utils", StringComparison.Ordinal) &&
                (string.Equals(glob.Name, "_rebuild_tensor_v2", StringComparison.Ordinal) ||
                 string.Equals(glob.Name, "_rebuild_tensor", StringComparison.Ordinal)))
            {
                return RebuildTensor(args);
            }

            // torch._utils._rebuild_parameter
            if (string.Equals(glob.Module, "torch._utils", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "_rebuild_parameter", StringComparison.Ordinal))
            {
                // args: (data, requires_grad, backward_hooks)
                return args.Items.Count > 0 ? args.Items[0] : new PyInstance(glob, new(), args);
            }

            // torch._utils._rebuild_qtensor
            if (string.Equals(glob.Module, "torch._utils", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "_rebuild_qtensor", StringComparison.Ordinal))
            {
                return RebuildTensor(args);
            }

            // collections.OrderedDict
            if (string.Equals(glob.Module, "collections", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "OrderedDict", StringComparison.Ordinal))
            {
                var dict = new PyDict();
                if (args.Items.Count > 0 && args.Items[0] is PyList list)
                {
                    foreach (var item in list.Items)
                    {
                        if (item is PyTuple kvTuple && kvTuple.Items.Count >= 2)
                        {
                            dict.Items[kvTuple.Items[0]] = kvTuple.Items[1];
                        }
                    }
                }
                return dict;
            }

            // torch.Size
            if (string.Equals(glob.Module, "torch", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "Size", StringComparison.Ordinal))
            {
                return args.Items.Count > 0 ? args.Items[0] : new PyTuple(Array.Empty<PyObject>());
            }

            // torch.device
            if (string.Equals(glob.Module, "torch", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "device", StringComparison.Ordinal))
            {
                return args.Items.Count > 0 ? args.Items[0] : new PyString("cpu");
            }

            // _codecs.encode
            if (string.Equals(glob.Module, "_codecs", StringComparison.Ordinal) &&
                string.Equals(glob.Name, "encode", StringComparison.Ordinal))
            {
                return args.Items.Count > 0 ? args.Items[0] : PyNone.Instance;
            }

            return new PyInstance(glob, new Dictionary<string, PyObject>(StringComparer.Ordinal), args);
        }

        return new PyTuple(args.Items);
    }

    private static PyObject RebuildTensor(PyTuple args)
    {
        // args: (storage, storage_offset, size, stride, ...)
        if (args.Items.Count < 3)
        {
            return new PyInstance(new PyGlobal("torch", "Tensor"), new(), args);
        }

        var storageObj = args.Items[0];
        long storageOffset = args.Items[1] is PyInt pi ? pi.Value : 0;
        var sizeObj = args.Items[2];

        List<TensorDimension> dims = new();
        long numel = 1;
        if (sizeObj is PyTuple sizeTuple)
        {
            foreach (var d in sizeTuple.Items)
            {
                if (d is PyInt dimInt)
                {
                    dims.Add(new TensorDimension.Fixed(dimInt.Value));
                    numel *= dimInt.Value;
                }
                else
                {
                    dims.Add(TensorDimension.Dynamic.Instance);
                }
            }
        }
        var shape = new TensorShape(dims);

        List<long>? strides = null;
        if (args.Items.Count >= 4 && args.Items[3] is PyTuple strideTuple)
        {
            strides = new List<long>(strideTuple.Items.Count);
            foreach (var st in strideTuple.Items)
            {
                strides.Add(st is PyInt si ? si.Value : 0);
            }
        }

        if (storageObj is PyStorageRef storageRef)
        {
            int elemSize = GetStorageElementByteSize(storageRef.DataType);
            long sliceOffset = storageRef.StreamOffset + storageOffset * elemSize;
            long sliceLength = numel * elemSize;
            var slice = new TensorDataSlice(sliceOffset, sliceLength, storageRef.DataType, TensorDataEncoding.Raw);
            return new PyTensor(slice, shape, storageRef.DataType, strides, storageOffset, storageRef.Key);
        }

        // Fallback placeholder slice when storage ref is not resolved (e.g. synthetic test)
        var dummySlice = new TensorDataSlice(0, 0, DataType.Float32, TensorDataEncoding.Raw);
        return new PyTensor(dummySlice, shape, DataType.Float32, strides, storageOffset, "0");
    }

    private static int GetStorageElementByteSize(DataType dt)
    {
        return dt switch
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

    private PyObject Pop(long offset)
    {
        if (_stack.Count == 0)
        {
            throw new ModelParseException("Pickle stack underflow.", fileOffset: offset);
        }
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return top;
    }

    private int PopMark(long offset)
    {
        if (_marks.Count == 0)
        {
            throw new ModelParseException("No MARK found on pickle mark stack.", fileOffset: offset);
        }
        int mark = _marks[^1];
        _marks.RemoveAt(_marks.Count - 1);
        return mark;
    }

    private List<PyObject> PopRange(int fromIndex)
    {
        int count = _stack.Count - fromIndex;
        if (count < 0) return new List<PyObject>();
        var items = new List<PyObject>(count);
        for (int i = fromIndex; i < _stack.Count; i++)
        {
            items.Add(_stack[i]);
        }
        _stack.RemoveRange(fromIndex, count);
        return items;
    }

    private void ReadExact(Span<byte> destination, long offset)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read <= 0)
            {
                throw new ModelParseException($"Unexpected end of pickle stream while reading {destination.Length} bytes.", fileOffset: offset);
            }
            total += read;
        }
    }

    private string ReadLine(long offset)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0)
            {
                throw new ModelParseException("Unexpected end of stream while reading pickle text line.", fileOffset: offset);
            }
            if (b == '\n')
            {
                break;
            }
            if (b != '\r')
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    private static string UnescapePythonString(string s)
    {
        if (s.Length >= 2 && ((s.StartsWith('\'') && s.EndsWith('\'')) || (s.StartsWith('"') && s.EndsWith('"'))))
        {
            s = s[1..^1];
        }

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                char c = s[i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case '"': sb.Append('"'); break;
                    case 'x':
                        if (i + 2 < s.Length && int.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexVal))
                        {
                            sb.Append((char)hexVal);
                            i += 2;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static long DecodeSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return 0;
        if (bytes.Length <= 8)
        {
            Span<byte> buf = stackalloc byte[8];
            bytes.CopyTo(buf);
            byte sign = (bytes[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
            for (int i = bytes.Length; i < 8; i++)
            {
                buf[i] = sign;
            }
            return BinaryPrimitives.ReadInt64LittleEndian(buf);
        }
        var bi = new BigInteger(bytes);
        return (long)bi;
    }
}
