using DotNodeViewer.Core.Abstractions;
using DotNodeViewer.Core.IO.Pickle;
using DotNodeViewer.Core.Ir;

namespace DotNodeViewer.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PickleMachine"/> safe VM execution, PyTorch reducers, and security invariants.
/// </summary>
public sealed class PickleMachineTests
{
    [Test]
    public async Task Load_BasicPickleOpcodes_DecodesCorrectValues()
    {
        // Pickle byte stream constructing:
        // PROTO 2
        // {'key': [1, 2, 'text', True, None]}
        // STOP
        using MemoryStream ms = new();
        ms.WriteByte(0x80); ms.WriteByte(0x02); // PROTO 2
        ms.WriteByte(0x7D); // EMPTY_DICT
        ms.WriteByte(0x94); // MEMOIZE (memo[0] = dict)

        // push key
        ms.WriteByte(0x8C); ms.WriteByte(0x03); ms.Write("key"u8); // SHORT_BINUTF8 'key'

        // push list
        ms.WriteByte(0x5D); // EMPTY_LIST
        ms.WriteByte(0x28); // MARK
        ms.WriteByte(0x4B); ms.WriteByte(1); // BININT1 1
        ms.WriteByte(0x4B); ms.WriteByte(2); // BININT1 2
        ms.WriteByte(0x8C); ms.WriteByte(0x04); ms.Write("text"u8); // SHORT_BINUTF8 'text'
        ms.WriteByte(0x88); // NEWTRUE
        ms.WriteByte(0x4E); // NONE
        ms.WriteByte(0x65); // APPENDS (into list)

        ms.WriteByte(0x73); // SETITEM (dict['key'] = list)
        ms.WriteByte(0x2E); // STOP

        ms.Position = 0;
        var machine = new PickleMachine(ms);
        var result = machine.Load();

        await Assert.That(result).IsTypeOf<PyDict>();
        var dict = (PyDict)result;
        await Assert.That(dict.TryGetStringKey("key", out var listVal)).IsTrue();
        await Assert.That(listVal).IsTypeOf<PyList>();

        var list = (PyList)listVal!;
        await Assert.That(list.Items.Count).IsEqualTo(5);
        await Assert.That(list.Items[0]).IsEqualTo(new PyInt(1));
        await Assert.That(list.Items[1]).IsEqualTo(new PyInt(2));
        await Assert.That(list.Items[2]).IsEqualTo(new PyString("text"));
        await Assert.That(list.Items[3]).IsEqualTo(new PyBool(true));
        await Assert.That(list.Items[4]).IsEqualTo(PyNone.Instance);
    }

    [Test]
    public async Task Load_HostileGlobal_ProducesSymbolicDescriptorWithoutExecuting()
    {
        // GLOBAL 'os' 'system'
        // REDUCE ('rm -rf /',)
        using MemoryStream ms = new();
        ms.WriteByte(0x80); ms.WriteByte(0x02);
        ms.Write("cos\nsystem\n"u8); // GLOBAL os.system
        ms.WriteByte(0x8C); ms.WriteByte(10); ms.Write("rm -rf /; "u8);
        ms.WriteByte(0x85); // TUPLE1
        ms.WriteByte(0x52); // REDUCE
        ms.WriteByte(0x2E); // STOP

        ms.Position = 0;
        var machine = new PickleMachine(ms);
        var result = machine.Load();

        // The VM must NOT invoke reflection or process execution!
        // It must safely return a PyInstance or symbolic representation
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsTypeOf<PyInstance>();
        var inst = (PyInstance)result;
        await Assert.That(inst.Cls.Module).IsEqualTo("os");
        await Assert.That(inst.Cls.Name).IsEqualTo("system");
    }

    [Test]
    public async Task Load_RebuildTensorV2WithStorageRef_ReconstructsLazyTensorSlice()
    {
        // Tests _rebuild_tensor_v2 with storage resolver
        using MemoryStream ms = new();
        ms.WriteByte(0x80); ms.WriteByte(0x02);
        ms.Write("ctorch._utils\n_rebuild_tensor_v2\n"u8);
        ms.WriteByte(0x28); // MARK
        // PERSID ('storage', FloatStorage, '0', 'cpu', 6)
        ms.WriteByte(0x28); // MARK
        ms.WriteByte(0x8C); ms.WriteByte(7); ms.Write("storage"u8);
        ms.Write("ctorch\nFloatStorage\n"u8);
        ms.WriteByte(0x8C); ms.WriteByte(1); ms.Write("0"u8);
        ms.WriteByte(0x8C); ms.WriteByte(3); ms.Write("cpu"u8);
        ms.WriteByte(0x4B); ms.WriteByte(6); // numel 6
        ms.WriteByte(0x74); // TUPLE
        ms.WriteByte(0x51); // BINPERSID

        ms.WriteByte(0x4B); ms.WriteByte(0); // storage_offset = 0

        // size = (2, 3)
        ms.WriteByte(0x28); ms.WriteByte(0x4B); ms.WriteByte(2); ms.WriteByte(0x4B); ms.WriteByte(3); ms.WriteByte(0x74);

        // stride = (3, 1)
        ms.WriteByte(0x28); ms.WriteByte(0x4B); ms.WriteByte(3); ms.WriteByte(0x4B); ms.WriteByte(1); ms.WriteByte(0x74);

        ms.WriteByte(0x89); // requires_grad = False
        ms.Write("ccollections\nOrderedDict\n)R"u8); // backward_hooks = OrderedDict()
        ms.WriteByte(0x74); // TUPLE (arguments)
        ms.WriteByte(0x52); // REDUCE
        ms.WriteByte(0x2E); // STOP

        ms.Position = 0;
        var machine = new PickleMachine(ms, persid =>
        {
            return new PyStorageRef("FloatStorage", "0", "cpu", 6, DataType.Float32, 1024, 24);
        });

        var result = machine.Load();
        await Assert.That(result).IsTypeOf<PyTensor>();
        var tensor = (PyTensor)result;

        await Assert.That(tensor.DataType).IsEqualTo(DataType.Float32);
        await Assert.That(tensor.Shape.Rank).IsEqualTo(2);
        await Assert.That(tensor.Shape.Dimensions[0]).IsEqualTo(new TensorDimension.Fixed(2));
        await Assert.That(tensor.Shape.Dimensions[1]).IsEqualTo(new TensorDimension.Fixed(3));
        await Assert.That(tensor.Slice.StreamOffset).IsEqualTo(1024);
        await Assert.That(tensor.Slice.ByteLength).IsEqualTo(24);
        await Assert.That(tensor.Slice.Encoding).IsEqualTo(TensorDataEncoding.Raw);
    }

    [Test]
    public async Task Load_TruncatedPickleStream_ThrowsModelParseException()
    {
        byte[] truncated = [0x80, 0x02, 0x4B]; // BININT1 without argument byte
        using MemoryStream ms = new(truncated);
        var machine = new PickleMachine(ms);

        await Assert.That(() => machine.Load())
            .Throws<ModelParseException>();
    }
}
