using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// The external weights of one Marian model folder, shared by its three sessions. The decoder with past reads only
/// weights the first-step decoder also has, and all three read the same embedding; on their own, each session copies
/// every weight it uses and prepacks its own copy, so one model held about 170 MB twice. Here each weight file is
/// mapped read-only once and every external initializer is handed to the sessions over that mapping: the originals
/// stay file-backed, and the container keeps one prepacked copy of each weight for all three sessions.
/// </summary>
internal sealed class MarianSharedWeights : IDisposable
{
    private readonly string _directory;
    private readonly PrePackedWeightsContainer _prepacked = new();
    private readonly Dictionary<string, MappedWeightFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string File, long Offset, long Length), OrtValue> _values = [];

    internal MarianSharedWeights(string directory) => _directory = directory;

    /// <summary>A session whose external initializers come from the shared mappings, configured by <paramref name="configure"/>.</summary>
    internal InferenceSession CreateSession(string modelPath, Action<SessionOptions> configure)
    {
        using var options = new SessionOptions();
        configure(options);
        foreach (OnnxExternalInitializer initializer in OnnxExternalInitializer.Read(modelPath))
        {
            if (ValueFor(initializer) is { } value) options.AddInitializer(initializer.Name, value);
        }
        return new InferenceSession(modelPath, options, _prepacked);
    }

    /// <summary>
    /// The initializer's bytes as a tensor over the mapped file, or null when its description does not match the file;
    /// ONNX Runtime then reads that one from the file itself, as without sharing.
    /// </summary>
    private OrtValue? ValueFor(OnnxExternalInitializer initializer)
    {
        if (ElementType(initializer.DataType) is not { } type || initializer.Location != Path.GetFileName(initializer.Location))
            return null;
        long elements = 1;
        foreach (long dimension in initializer.Dimensions)
        {
            if (dimension < 0) return null;
            elements = checked(elements * dimension);
        }
        long bytes = checked(elements * ElementSize(type));
        if (initializer.Length is { } stated && stated != bytes) return null;
        var key = (initializer.Location, initializer.Offset, bytes);
        if (_values.TryGetValue(key, out OrtValue? shared)) return shared;
        if (!_files.TryGetValue(initializer.Location, out MappedWeightFile? file))
        {
            string path = Path.Combine(_directory, initializer.Location);
            if (!File.Exists(path)) return null;
            file = new MappedWeightFile(path);
            _files.Add(initializer.Location, file);
        }
        if (initializer.Offset < 0 || initializer.Offset > file.Length - bytes) return null;
        OrtValue value = OrtValue.CreateTensorValueWithData(OrtMemoryInfo.DefaultInstance, type,
            initializer.Dimensions, file.Address + (nint)initializer.Offset, bytes);
        _values.Add(key, value);
        return value;
    }

    private static TensorElementType? ElementType(int onnxType) => onnxType switch
    {
        1 => TensorElementType.Float,
        2 => TensorElementType.UInt8,
        3 => TensorElementType.Int8,
        6 => TensorElementType.Int32,
        7 => TensorElementType.Int64,
        _ => null,
    };

    private static int ElementSize(TensorElementType type) => type switch
    {
        TensorElementType.UInt8 or TensorElementType.Int8 => 1,
        TensorElementType.Float or TensorElementType.Int32 => 4,
        _ => 8,
    };

    /// <summary>Disposed after the sessions that use the initializers.</summary>
    public void Dispose()
    {
        foreach (OrtValue value in _values.Values) value.Dispose();
        _values.Clear();
        _prepacked.Dispose();
        foreach (MappedWeightFile file in _files.Values) file.Dispose();
        _files.Clear();
    }

    /// <summary>One weight file mapped read-only for the translator's lifetime. Deleting the model releases the translator first.</summary>
    private sealed unsafe class MappedWeightFile : IDisposable
    {
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private byte* _pointer;

        internal MappedWeightFile(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                Length = stream.Length;
                _mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read,
                    HandleInheritability.None, leaveOpen: false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            MemoryMappedViewAccessor? view = null;
            try
            {
                view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            }
            catch
            {
                view?.Dispose();
                _mapping.Dispose();
                throw;
            }
            _view = view;
        }

        internal long Length { get; }

        internal nint Address => (nint)(_pointer + _view.PointerOffset);

        public void Dispose()
        {
            if (_pointer != null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }
            _view.Dispose();
            _mapping.Dispose();
        }
    }
}

/// <summary>
/// An initializer of an ONNX graph whose bytes live in an external file, read from the model with a minimal protobuf
/// reader: only the top-level graph's initializers, which is where these exported models keep their weights.
/// </summary>
internal sealed record OnnxExternalInitializer(string Name, int DataType, long[] Dimensions, string Location, long Offset, long? Length)
{
    internal static IReadOnlyList<OnnxExternalInitializer> Read(string modelPath) => Parse(File.ReadAllBytes(modelPath));

    internal static IReadOnlyList<OnnxExternalInitializer> Parse(ReadOnlySpan<byte> model)
    {
        var found = new List<OnnxExternalInitializer>();
        var reader = new Reader(model);
        while (reader.Next(out int field, out int wire))
        {
            // ModelProto.graph = 7, GraphProto.initializer = 5.
            if (field != 7 || wire != 2)
            {
                reader.Skip(wire);
                continue;
            }
            var graph = new Reader(reader.Bytes());
            while (graph.Next(out int graphField, out int graphWire))
            {
                if (graphField == 5 && graphWire == 2)
                {
                    if (Tensor(graph.Bytes()) is { } initializer) found.Add(initializer);
                }
                else graph.Skip(graphWire);
            }
        }
        return found;
    }

    private static OnnxExternalInitializer? Tensor(ReadOnlySpan<byte> tensor)
    {
        var dimensions = new List<long>();
        int dataType = 0;
        string? name = null, location = null;
        long offset = 0;
        long? length = null;
        bool external = false;
        var reader = new Reader(tensor);
        while (reader.Next(out int field, out int wire))
        {
            switch (field, wire)
            {
                case (1, 0):
                    dimensions.Add((long)reader.Varint());
                    break;
                case (1, 2):
                    var packed = new Reader(reader.Bytes());
                    while (!packed.End) dimensions.Add((long)packed.Varint());
                    break;
                case (2, 0):
                    dataType = (int)reader.Varint();
                    break;
                case (8, 2):
                    name = Encoding.UTF8.GetString(reader.Bytes());
                    break;
                case (13, 2):
                    (string? key, string? value) = Entry(reader.Bytes());
                    if (key == "location") location = value;
                    else if (key == "offset" && long.TryParse(value, out long parsedOffset)) offset = parsedOffset;
                    else if (key == "length" && long.TryParse(value, out long parsedLength)) length = parsedLength;
                    break;
                case (14, 0):
                    external = reader.Varint() == 1;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
        return external && name is not null && location is not null
            ? new OnnxExternalInitializer(name, dataType, [.. dimensions], location, offset, length) : null;
    }

    private static (string? Key, string? Value) Entry(ReadOnlySpan<byte> entry)
    {
        string? key = null, value = null;
        var reader = new Reader(entry);
        while (reader.Next(out int field, out int wire))
        {
            if (field == 1 && wire == 2) key = Encoding.UTF8.GetString(reader.Bytes());
            else if (field == 2 && wire == 2) value = Encoding.UTF8.GetString(reader.Bytes());
            else reader.Skip(wire);
        }
        return (key, value);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        internal readonly bool End => _position >= _data.Length;

        internal bool Next(out int field, out int wire)
        {
            if (End)
            {
                field = wire = 0;
                return false;
            }
            ulong key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        internal ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length) throw new InvalidDataException("The model ends inside a number.");
                byte next = _data[_position++];
                result |= (ulong)(next & 0x7F) << shift;
                if (next < 0x80) return result;
            }
            throw new InvalidDataException("The model has an overlong number.");
        }

        internal ReadOnlySpan<byte> Bytes()
        {
            ulong length = Varint();
            if (length > (ulong)(_data.Length - _position)) throw new InvalidDataException("The model ends inside a field.");
            ReadOnlySpan<byte> bytes = _data.Slice(_position, (int)length);
            _position += (int)length;
            return bytes;
        }

        internal void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    Varint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    Bytes();
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException($"The model uses the unsupported wire type {wire}.");
            }
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _position) throw new InvalidDataException("The model ends inside a field.");
            _position += count;
        }
    }
}
