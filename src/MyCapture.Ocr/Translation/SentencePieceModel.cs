using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// SentencePiece Unigram tokenizer for an OPUS-MT source model (source.spm), read directly from
/// the ModelProto wire format. Ids match Microsoft.ML.Tokenizers 2.0.1, including its departures
/// from C++ SentencePiece: a rule that deletes a character keeps it, consecutive unknown characters
/// stay separate ids, and path scores add up in single precision. Unpaired UTF-16 surrogates are
/// read as U+FFFD. Immutable and safe for concurrent encoding.
/// </summary>
internal sealed class SentencePieceModel
{
    private const long MaxModelBytes = 64L * 1024 * 1024;
    private const float UnknownPenalty = 10f;
    private const byte Space = (byte)' ';

    private static readonly byte[] SpaceSymbol = [0xE2, 0x96, 0x81]; // U+2581, the escaped space.
    private static readonly byte[] SpaceByte = [Space];
    private static readonly byte[] ReplacementCharacter = [0xEF, 0xBF, 0xBD]; // U+FFFD.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string[] _pieces;
    private readonly float[] _scores;
    private readonly PieceTrie _trie;
    private readonly PrecompiledCharsMap? _charsMap;
    private readonly bool _addDummyPrefix;
    private readonly bool _removeExtraWhitespaces;
    private readonly bool _escapeWhitespaces;
    private readonly float _unknownScore;

    private SentencePieceModel(List<PieceSpec> pieces, TrainerSpec trainer, NormalizerSpec normalizer)
    {
        if (pieces.Count == 0)
        {
            throw new InvalidDataException("The SentencePiece model has no pieces.");
        }

        if (trainer.ModelType != 1)
        {
            throw new InvalidDataException($"Only Unigram SentencePiece models are supported (model_type {trainer.ModelType}).");
        }

        if (trainer.ByteFallback || trainer.TreatWhitespaceAsSuffix)
        {
            throw new InvalidDataException("SentencePiece byte fallback and suffix whitespace are not supported.");
        }

        if ((uint)trainer.UnknownId >= (uint)pieces.Count || pieces[trainer.UnknownId].Type != PieceType.Unknown)
        {
            throw new InvalidDataException("The SentencePiece unknown id does not name the unknown piece.");
        }

        if (trainer.BeginOfSentenceId >= pieces.Count || trainer.EndOfSentenceId >= pieces.Count)
        {
            throw new InvalidDataException("The SentencePiece sentence boundary ids are outside the vocabulary.");
        }

        _pieces = new string[pieces.Count];
        _scores = new float[pieces.Count];
        var keys = new List<(byte[] Key, int Id)>(pieces.Count);
        float minScore = float.MaxValue;
        int normalCount = 0;
        for (int id = 0; id < pieces.Count; id++)
        {
            PieceSpec piece = pieces[id];
            _pieces[id] = piece.Text;
            _scores[id] = piece.Score;
            switch (piece.Type)
            {
                case PieceType.Normal:
                case PieceType.Unused:
                    if (piece.Utf8.Contains((byte)0))
                    {
                        throw new InvalidDataException("A SentencePiece piece contains a NUL character.");
                    }

                    // Unused pieces stay in the trie as non-candidates and, as in Microsoft.ML.Tokenizers,
                    // still lower the score floor that unknown characters are measured against.
                    keys.Add((piece.Utf8, piece.Type == PieceType.Normal ? id : -1));
                    minScore = Math.Min(minScore, piece.Score);
                    normalCount += piece.Type == PieceType.Normal ? 1 : 0;
                    break;
                case PieceType.UserDefined:
                    throw new InvalidDataException("SentencePiece user-defined pieces are not supported.");
            }
        }

        if (normalCount == 0)
        {
            throw new InvalidDataException("The SentencePiece model has no normal pieces.");
        }

        _trie = PieceTrie.Build(keys);
        _unknownScore = minScore - UnknownPenalty;
        _charsMap = normalizer.CharsMap.Length == 0 ? null : PrecompiledCharsMap.Parse(normalizer.CharsMap);
        _addDummyPrefix = normalizer.AddDummyPrefix;
        _removeExtraWhitespaces = normalizer.RemoveExtraWhitespaces;
        _escapeWhitespaces = normalizer.EscapeWhitespaces;
        UnknownId = trainer.UnknownId;
        BeginOfSentenceId = trainer.BeginOfSentenceId < 0 ? -1 : trainer.BeginOfSentenceId;
        EndOfSentenceId = trainer.EndOfSentenceId < 0 ? -1 : trainer.EndOfSentenceId;
    }

    internal int PieceCount => _pieces.Length;

    internal int UnknownId { get; }

    /// <summary>The id of &lt;s&gt;, or -1 when the model has none.</summary>
    internal int BeginOfSentenceId { get; }

    /// <summary>The id of &lt;/s&gt;, or -1 when the model has none.</summary>
    internal int EndOfSentenceId { get; }

    /// <summary>Reads a model file; anything over 64 MiB is rejected before it is read.</summary>
    internal static SentencePieceModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxModelBytes)
        {
            throw new InvalidDataException("The SentencePiece model file is too large.");
        }

        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Parse(data);
    }

    /// <summary>
    /// Reads a serialized ModelProto: pieces (1), trainer_spec (2) and normalizer_spec (3).
    /// Unknown fields are skipped, and repeated spec messages merge as protobuf requires.
    /// </summary>
    internal static SentencePieceModel Parse(ReadOnlySpan<byte> modelProto)
    {
        var pieces = new List<PieceSpec>();
        TrainerSpec? trainer = null;
        NormalizerSpec? normalizer = null;
        var reader = new ProtoReader(modelProto);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (wireType != ProtoReader.LengthDelimited || field is < 1 or > 3)
            {
                reader.SkipField(field, wireType);
                continue;
            }

            ReadOnlySpan<byte> message = reader.ReadLengthDelimited();
            switch (field)
            {
                case 1:
                    pieces.Add(PieceSpec.Read(message));
                    break;
                case 2:
                    (trainer ??= new TrainerSpec()).Merge(message);
                    break;
                default:
                    (normalizer ??= new NormalizerSpec()).Merge(message);
                    break;
            }
        }

        // Every SentencePiece model writes both specs after its pieces; a file without them was cut short.
        if (trainer is null || normalizer is null)
        {
            throw new InvalidDataException("The SentencePiece model is missing its trainer or normalizer spec.");
        }

        return new SentencePieceModel(pieces, trainer, normalizer);
    }

    internal string PieceAt(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, _pieces.Length);
        return _pieces[id];
    }

    /// <summary>
    /// The normalized text the segmentation runs on, with spaces escaped as U+2581.
    /// </summary>
    internal string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ByteBuilder normalized = NormalizeText(text);
        return Encoding.UTF8.GetString(normalized.Written);
    }

    /// <summary>
    /// Segments <paramref name="text"/> into piece ids without adding &lt;s&gt; or &lt;/s&gt;.
    /// </summary>
    internal int[] EncodeToIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ByteBuilder normalized = NormalizeText(text);
        return normalized.Length == 0 ? [] : Segment(normalized.Written);
    }

    private ByteBuilder NormalizeText(string text)
    {
        // Encoding.UTF8 replaces each unpaired surrogate with U+FFFD, as Microsoft.ML.Tokenizers does.
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var output = new ByteBuilder(utf8.Length + (utf8.Length >> 1) + 8);
        NormalizeUtf8(utf8, ref output);
        return output;
    }

    /// <summary>
    /// SentencePiece Normalizer::Normalize over valid UTF-8 as Microsoft.ML.Tokenizers implements it:
    /// leading whitespace dropped, a dummy U+2581 prefix, runs of spaces collapsed and escaped, and
    /// trailing spaces removed.
    /// </summary>
    private void NormalizeUtf8(ReadOnlySpan<byte> input, ref ByteBuilder output)
    {
        if (_removeExtraWhitespaces)
        {
            // As in Microsoft.ML.Tokenizers, only a one-byte source is skipped here; a wider space such
            // as U+3000 is dropped by the run collapsing below instead, with the same result.
            while (!input.IsEmpty)
            {
                int consumed = NormalizePrefix(input, out ReadOnlySpan<byte> normalized);
                if (consumed != 1 || normalized[0] != Space)
                {
                    break;
                }

                input = input[consumed..];
            }
        }

        if (input.IsEmpty)
        {
            return;
        }

        if (_addDummyPrefix)
        {
            output.Append(_escapeWhitespaces ? SpaceSymbol : SpaceByte);
        }

        bool previousIsSpace = _removeExtraWhitespaces;
        while (!input.IsEmpty)
        {
            int consumed = NormalizePrefix(input, out ReadOnlySpan<byte> normalized);
            while (previousIsSpace && !normalized.IsEmpty && normalized[0] == Space)
            {
                normalized = normalized[1..];
            }

            if (!normalized.IsEmpty)
            {
                foreach (byte value in normalized)
                {
                    if (_escapeWhitespaces && value == Space)
                    {
                        output.Append(SpaceSymbol);
                    }
                    else
                    {
                        output.Append(value);
                    }
                }

                previousIsSpace = normalized[^1] == Space;
            }

            input = input[consumed..];
            if (!_removeExtraWhitespaces)
            {
                previousIsSpace = false;
            }
        }

        if (_removeExtraWhitespaces)
        {
            ReadOnlySpan<byte> space = _escapeWhitespaces ? SpaceSymbol : SpaceByte;
            while (output.Written.EndsWith(space))
            {
                output.Length -= space.Length;
            }
        }
    }

    /// <summary>
    /// Normalizes the longest matching rule, or else one character, at the start of
    /// <paramref name="input"/>; returns the number of bytes consumed.
    /// </summary>
    private int NormalizePrefix(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> normalized)
    {
        ReadOnlySpan<byte> replacement = default;
        int matched = _charsMap is null ? 0 : _charsMap.MatchLongestPrefix(input, out replacement);
        if (matched == 0)
        {
            int length = Utf8CharLength(input);
            if (length == 0)
            {
                // A malformed byte becomes U+FFFD and is consumed alone.
                normalized = ReplacementCharacter;
                return 1;
            }

            normalized = input[..length];
            return length;
        }

        // Microsoft.ML.Tokenizers cannot tell an empty replacement from "no rule", so a rule that
        // deletes its source (control characters in nmt_nfkc) leaves the source unchanged.
        normalized = replacement.IsEmpty ? input[..matched] : replacement;
        return matched;
    }

    /// <summary>
    /// Viterbi over the piece lattice (SentencePiece Model::EncodeOptimized) in single precision,
    /// keeping the first candidate on ties. A character no single piece covers becomes an unknown
    /// node scored below every piece.
    /// </summary>
    private int[] Segment(ReadOnlySpan<byte> normalized)
    {
        int size = normalized.Length;
        var best = new BestPath[size + 1];
        best.AsSpan().Fill(new BestPath(-1, -1, 0f));
        for (int startsAt = 0; startsAt < size;)
        {
            float scoreTillHere = best[startsAt].Score;
            int charLength = Math.Min(OneCharLength(normalized[startsAt]), size - startsAt);
            bool hasSingleNode = false;
            int node = PieceTrie.Root;
            for (int keyPos = startsAt; keyPos < size; keyPos++)
            {
                node = _trie.Child(node, normalized[keyPos]);
                if (node < 0)
                {
                    break;
                }

                int id = _trie.PieceId(node);
                if (id < 0)
                {
                    continue;
                }

                int endsAt = keyPos + 1;
                float candidate = _scores[id] + scoreTillHere;
                ref BestPath target = ref best[endsAt];
                if (target.StartsAt == -1 || candidate > target.Score)
                {
                    target = new BestPath(id, startsAt, candidate);
                }

                hasSingleNode |= endsAt - startsAt == charLength;
            }

            if (!hasSingleNode)
            {
                float candidate = _unknownScore + scoreTillHere;
                ref BestPath target = ref best[startsAt + charLength];
                if (target.StartsAt == -1 || candidate > target.Score)
                {
                    target = new BestPath(UnknownId, startsAt, candidate);
                }
            }

            startsAt += charLength;
        }

        int count = 0;
        for (int endsAt = size; endsAt > 0; endsAt = best[endsAt].StartsAt)
        {
            count++;
        }

        int[] ids = new int[count];
        for (int endsAt = size; endsAt > 0; endsAt = best[endsAt].StartsAt)
        {
            ids[--count] = best[endsAt].Id;
        }

        return ids;
    }

    private static int OneCharLength(byte lead) => lead switch
    {
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };

    /// <summary>
    /// Length of the well-formed UTF-8 character at the start of <paramref name="input"/>, or 0 when
    /// it is malformed (SentencePiece IsValidDecodeUTF8).
    /// </summary>
    private static int Utf8CharLength(ReadOnlySpan<byte> input)
    {
        byte lead = input[0];
        if (lead < 0x80)
        {
            return 1;
        }

        if (input.Length >= 2 && (lead & 0xE0) == 0xC0)
        {
            int codePoint = ((lead & 0x1F) << 6) | (input[1] & 0x3F);
            return IsTrail(input[1]) && codePoint >= 0x80 ? 2 : 0;
        }

        if (input.Length >= 3 && (lead & 0xF0) == 0xE0)
        {
            int codePoint = ((lead & 0x0F) << 12) | ((input[1] & 0x3F) << 6) | (input[2] & 0x3F);
            return IsTrail(input[1]) && IsTrail(input[2]) && codePoint >= 0x800 && IsScalar(codePoint) ? 3 : 0;
        }

        if (input.Length >= 4 && (lead & 0xF8) == 0xF0)
        {
            int codePoint = ((lead & 0x07) << 18) | ((input[1] & 0x3F) << 12) | ((input[2] & 0x3F) << 6) | (input[3] & 0x3F);
            return IsTrail(input[1]) && IsTrail(input[2]) && IsTrail(input[3]) && codePoint >= 0x10000 && IsScalar(codePoint)
                ? 4
                : 0;
        }

        return 0;

        static bool IsTrail(byte value) => (value & 0xC0) == 0x80;

        static bool IsScalar(int codePoint) => codePoint is < 0xD800 or (>= 0xE000 and <= 0x10FFFF);
    }

    private enum PieceType
    {
        Normal = 1,
        Unknown = 2,
        Control = 3,
        UserDefined = 4,
        Unused = 5,
        Byte = 6,
    }

    private readonly record struct BestPath(int Id, int StartsAt, float Score);

    /// <summary>One ModelProto.SentencePiece: piece (1), score (2) and type (3).</summary>
    private sealed class PieceSpec
    {
        private PieceSpec(string text, byte[] utf8, float score, PieceType type)
        {
            Text = text;
            Utf8 = utf8;
            Score = score;
            Type = type;
        }

        internal string Text { get; }

        internal byte[] Utf8 { get; }

        internal float Score { get; }

        internal PieceType Type { get; }

        internal static PieceSpec Read(ReadOnlySpan<byte> message)
        {
            ReadOnlySpan<byte> utf8 = default;
            float score = 0f;
            ulong type = (ulong)PieceType.Normal;
            var reader = new ProtoReader(message);
            while (reader.TryReadTag(out int field, out int wireType))
            {
                if (field == 1 && wireType == ProtoReader.LengthDelimited)
                {
                    utf8 = reader.ReadLengthDelimited();
                }
                else if (field == 2 && wireType == ProtoReader.Fixed32)
                {
                    score = BitConverter.UInt32BitsToSingle(reader.ReadFixed32());
                }
                else if (field == 3 && wireType == ProtoReader.Varint)
                {
                    type = reader.ReadVarint();
                }
                else
                {
                    reader.SkipField(field, wireType);
                }
            }

            if (utf8.IsEmpty)
            {
                throw new InvalidDataException("A SentencePiece piece is empty.");
            }

            if (type is < (ulong)PieceType.Normal or > (ulong)PieceType.Byte)
            {
                throw new InvalidDataException($"Unknown SentencePiece piece type {type}.");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(utf8);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("A SentencePiece piece is not valid UTF-8.", ex);
            }

            return new PieceSpec(text, utf8.ToArray(), score, (PieceType)type);
        }
    }

    /// <summary>The TrainerSpec fields that change encoding, with their proto2 defaults.</summary>
    private sealed class TrainerSpec
    {
        internal int ModelType { get; private set; } = 1;

        internal bool TreatWhitespaceAsSuffix { get; private set; }

        internal bool ByteFallback { get; private set; }

        internal int UnknownId { get; private set; }

        internal int BeginOfSentenceId { get; private set; } = 1;

        internal int EndOfSentenceId { get; private set; } = 2;

        internal void Merge(ReadOnlySpan<byte> message)
        {
            var reader = new ProtoReader(message);
            while (reader.TryReadTag(out int field, out int wireType))
            {
                if (wireType != ProtoReader.Varint)
                {
                    reader.SkipField(field, wireType);
                    continue;
                }

                // int32 and enum values are the low 32 bits of the varint; -1 arrives sign-extended.
                ulong value = reader.ReadVarint();
                switch (field)
                {
                    case 3:
                        ModelType = unchecked((int)value);
                        break;
                    case 24:
                        TreatWhitespaceAsSuffix = value != 0;
                        break;
                    case 35:
                        ByteFallback = value != 0;
                        break;
                    case 40:
                        UnknownId = unchecked((int)value);
                        break;
                    case 41:
                        BeginOfSentenceId = unchecked((int)value);
                        break;
                    case 42:
                        EndOfSentenceId = unchecked((int)value);
                        break;
                }
            }
        }
    }

    /// <summary>The NormalizerSpec fields that change encoding, with their proto2 defaults.</summary>
    private sealed class NormalizerSpec
    {
        internal byte[] CharsMap { get; private set; } = [];

        internal bool AddDummyPrefix { get; private set; } = true;

        internal bool RemoveExtraWhitespaces { get; private set; } = true;

        internal bool EscapeWhitespaces { get; private set; } = true;

        internal void Merge(ReadOnlySpan<byte> message)
        {
            var reader = new ProtoReader(message);
            while (reader.TryReadTag(out int field, out int wireType))
            {
                if (field == 2 && wireType == ProtoReader.LengthDelimited)
                {
                    CharsMap = reader.ReadLengthDelimited().ToArray();
                }
                else if (field is >= 3 and <= 5 && wireType == ProtoReader.Varint)
                {
                    bool value = reader.ReadVarint() != 0;
                    switch (field)
                    {
                        case 3:
                            AddDummyPrefix = value;
                            break;
                        case 4:
                            RemoveExtraWhitespaces = value;
                            break;
                        default:
                            EscapeWhitespaces = value;
                            break;
                    }
                }
                else
                {
                    reader.SkipField(field, wireType);
                }
            }
        }
    }

    /// <summary>
    /// Byte trie over piece spellings. Children of a node are contiguous and sorted by label, so a
    /// walk from the root enumerates the pieces that prefix the text from shortest to longest.
    /// </summary>
    private sealed class PieceTrie
    {
        internal const int Root = 0;

        private readonly int[] _edgeStart;
        private readonly byte[] _edgeLabels;
        private readonly int[] _edgeTargets;
        private readonly int[] _pieceIds;
        private readonly int[] _rootChildren;

        private PieceTrie(int[] edgeStart, byte[] edgeLabels, int[] edgeTargets, int[] pieceIds)
        {
            _edgeStart = edgeStart;
            _edgeLabels = edgeLabels;
            _edgeTargets = edgeTargets;
            _pieceIds = pieceIds;
            _rootChildren = new int[256];
            _rootChildren.AsSpan().Fill(-1);
            for (int edge = edgeStart[Root]; edge < edgeStart[Root + 1]; edge++)
            {
                _rootChildren[edgeLabels[edge]] = edgeTargets[edge];
            }
        }

        /// <summary>Builds the trie breadth first; an id of -1 marks a key that is not a candidate.</summary>
        internal static PieceTrie Build(List<(byte[] Key, int Id)> keys)
        {
            keys.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
            for (int i = 1; i < keys.Count; i++)
            {
                if (keys[i - 1].Key.AsSpan().SequenceEqual(keys[i].Key))
                {
                    throw new InvalidDataException("A SentencePiece piece is defined twice.");
                }
            }

            var edgeStart = new List<int>();
            var edgeLabels = new List<byte>();
            var edgeTargets = new List<int>();
            var pieceIds = new List<int>();
            var pending = new Queue<(int First, int End, int Depth)>();
            pending.Enqueue((0, keys.Count, 0));
            int nextNode = Root + 1;
            while (pending.TryDequeue(out (int First, int End, int Depth) range))
            {
                // Keys in the range share their first Depth bytes; a key of exactly that length sorts first.
                (int first, int end, int depth) = range;
                int pieceId = -1;
                if (first < end && keys[first].Key.Length == depth)
                {
                    pieceId = keys[first].Id;
                    first++;
                }

                pieceIds.Add(pieceId);
                edgeStart.Add(edgeLabels.Count);
                while (first < end)
                {
                    byte label = keys[first].Key[depth];
                    int next = first + 1;
                    while (next < end && keys[next].Key[depth] == label)
                    {
                        next++;
                    }

                    edgeLabels.Add(label);
                    edgeTargets.Add(nextNode++);
                    pending.Enqueue((first, next, depth + 1));
                    first = next;
                }
            }

            edgeStart.Add(edgeLabels.Count);
            return new PieceTrie([.. edgeStart], [.. edgeLabels], [.. edgeTargets], [.. pieceIds]);
        }

        /// <summary>The child reached by <paramref name="label"/>, or -1.</summary>
        internal int Child(int node, byte label)
        {
            if (node == Root)
            {
                return _rootChildren[label];
            }

            int start = _edgeStart[node];
            int index = _edgeLabels.AsSpan(start, _edgeStart[node + 1] - start).IndexOf(label);
            return index < 0 ? -1 : _edgeTargets[start + index];
        }

        /// <summary>The candidate piece spelled by the path to <paramref name="node"/>, or -1.</summary>
        internal int PieceId(int node) => _pieceIds[node];
    }

    /// <summary>Growable buffer for normalized UTF-8.</summary>
    private ref struct ByteBuilder
    {
        private byte[] _bytes;

        internal ByteBuilder(int capacity)
        {
            _bytes = new byte[capacity];
            Length = 0;
        }

        internal int Length { get; set; }

        internal readonly ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

        internal void Append(byte value)
        {
            if (Length == _bytes.Length)
            {
                Array.Resize(ref _bytes, _bytes.Length * 2);
            }

            _bytes[Length++] = value;
        }

        internal void Append(ReadOnlySpan<byte> value)
        {
            if (value.Length > _bytes.Length - Length)
            {
                Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, Length + value.Length));
            }

            value.CopyTo(_bytes.AsSpan(Length));
            Length += value.Length;
        }
    }

    /// <summary>Protobuf wire-format reader over a span; throws InvalidDataException on malformed input.</summary>
    private ref struct ProtoReader
    {
        internal const int Varint = 0;
        internal const int Fixed64 = 1;
        internal const int LengthDelimited = 2;
        internal const int StartGroup = 3;
        internal const int EndGroup = 4;
        internal const int Fixed32 = 5;

        private const int MaxGroupDepth = 64;

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        internal ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        internal bool TryReadTag(out int field, out int wireType)
        {
            if (_position >= _data.Length)
            {
                field = 0;
                wireType = 0;
                return false;
            }

            ulong tag = ReadVarint();
            if (tag > uint.MaxValue || tag >> 3 == 0)
            {
                throw Malformed("an invalid field tag");
            }

            field = (int)(tag >> 3);
            wireType = (int)(tag & 7);
            return true;
        }

        internal ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                {
                    throw Malformed("a truncated varint");
                }

                byte next = _data[_position++];
                value |= (ulong)(next & 0x7F) << shift;
                if (next < 0x80)
                {
                    return value;
                }
            }

            throw Malformed("a varint longer than ten bytes");
        }

        internal uint ReadFixed32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Take(sizeof(uint)));
        }

        internal ReadOnlySpan<byte> ReadLengthDelimited()
        {
            ulong length = ReadVarint();
            if (length > (ulong)(_data.Length - _position))
            {
                throw Malformed("a field longer than the data");
            }

            return Take((int)length);
        }

        internal void SkipField(int field, int wireType) => SkipField(field, wireType, 0);

        private void SkipField(int field, int wireType, int depth)
        {
            switch (wireType)
            {
                case Varint:
                    ReadVarint();
                    break;
                case Fixed64:
                    Take(sizeof(ulong));
                    break;
                case LengthDelimited:
                    ReadLengthDelimited();
                    break;
                case StartGroup:
                    SkipGroup(field, depth + 1);
                    break;
                case Fixed32:
                    Take(sizeof(uint));
                    break;
                default:
                    throw Malformed($"wire type {wireType} for field {field}");
            }
        }

        private void SkipGroup(int field, int depth)
        {
            if (depth > MaxGroupDepth)
            {
                throw Malformed("groups nested too deeply");
            }

            while (TryReadTag(out int innerField, out int innerWireType))
            {
                if (innerWireType == EndGroup)
                {
                    if (innerField != field)
                    {
                        throw Malformed("a mismatched group end");
                    }

                    return;
                }

                SkipField(innerField, innerWireType, depth);
            }

            throw Malformed("an unterminated group");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _data.Length - _position)
            {
                throw Malformed("a truncated field");
            }

            ReadOnlySpan<byte> bytes = _data.Slice(_position, count);
            _position += count;
            return bytes;
        }

        private static InvalidDataException Malformed(string detail) =>
            new($"The SentencePiece model is not a valid protobuf message: {detail}.");
    }
}
