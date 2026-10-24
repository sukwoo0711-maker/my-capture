using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace MyCapture.Ocr.Translation;

/// <summary>Translates a batch of sentences; the seam the translation service and its tests share.</summary>
internal interface ISentenceTranslator : IDisposable
{
    /// <summary>Source tokens the text occupies, including the end-of-sentence token.</summary>
    int CountTokens(string text);

    IReadOnlyList<string> Translate(IReadOnlyList<string> sentences, CancellationToken cancellationToken);
}

/// <summary>File names of one Marian model inside its folder.</summary>
internal sealed record MarianModelFiles(
    string Encoder, string Decoder, string DecoderWithPast, string SourceVocabulary, string TargetVocabulary);

/// <summary>
/// OPUS-MT tc-big English to Korean as ONNX: the encoder, the first-step decoder and the decoder
/// that reuses the key/value cache, with greedy decoding over a batch. The source side has its
/// own SentencePiece vocabulary ("sepvoc") whose piece ids feed the encoder directly; vocab.json
/// is the target vocabulary only. The merged decoder (an If node choosing the cache branch) is
/// not used: its int8 weights were dequantized at every step and ran three to four times slower.
/// </summary>
internal sealed class MarianTranslator : ISentenceTranslator
{
    /// <summary>Longest source the encoder receives, end-of-sentence included.</summary>
    internal const int MaxSourceTokens = 256;

    private const string PastPrefix = "past_key_values.";

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly SentencePieceModel _source;
    private readonly string?[] _targetPieces;
    private readonly int _targetEos;
    private readonly int _targetUnknown;
    private readonly int _pad;
    private readonly string[] _decoderOutputs;
    private readonly string[] _decoderWithPastOutputs;

    private MarianTranslator(InferenceSession encoder, InferenceSession decoder, InferenceSession decoderWithPast,
        SentencePieceModel source, TargetVocabulary target)
    {
        _encoder = encoder;
        _decoder = decoder;
        _decoderWithPast = decoderWithPast;
        _source = source;
        _targetPieces = target.Pieces;
        _targetEos = target.EndOfSentence;
        _targetUnknown = target.Unknown;
        _pad = target.Pad;
        _decoderOutputs = [.. decoder.OutputNames];
        _decoderWithPastOutputs = [.. decoderWithPast.OutputNames];
    }

    /// <summary>Threads for one translation: half the logical processors, at most four.</summary>
    internal static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    internal static MarianTranslator Load(string directory, MarianModelFiles files, int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(files);
        OnnxRuntimeInitialization.Shared.EnsureInitialized();
        SentencePieceModel source = SentencePieceModel.Load(Path.Combine(directory, files.SourceVocabulary));
        TargetVocabulary target = TargetVocabulary.Load(Path.Combine(directory, files.TargetVocabulary));
        if (source.EndOfSentenceId < 0) throw new InvalidDataException("The source vocabulary has no end-of-sentence piece.");

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
        };
        InferenceSession? encoder = null, decoder = null, decoderWithPast = null;
        try
        {
            encoder = new InferenceSession(Path.Combine(directory, files.Encoder), options);
            decoder = new InferenceSession(Path.Combine(directory, files.Decoder), options);
            decoderWithPast = new InferenceSession(Path.Combine(directory, files.DecoderWithPast), options);
            return new MarianTranslator(encoder, decoder, decoderWithPast, source, target);
        }
        catch
        {
            encoder?.Dispose();
            decoder?.Dispose();
            decoderWithPast?.Dispose();
            throw;
        }
    }

    public int CountTokens(string text) => _source.EncodeToIds(text).Length + 1;

    public IReadOnlyList<string> Translate(IReadOnlyList<string> sentences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        if (sentences.Count == 0) return [];
        int[][] sources = [.. sentences.Select(EncodeSource)];
        return [.. Generate(sources, cancellationToken).Select(Decode)];
    }

    private int[] EncodeSource(string text)
    {
        int[] pieces = _source.EncodeToIds(text);
        int length = Math.Min(pieces.Length, MaxSourceTokens - 1);
        var ids = new int[length + 1];
        pieces.AsSpan(0, length).CopyTo(ids);
        ids[length] = _source.EndOfSentenceId;
        return ids;
    }

    private List<int>[] Generate(IReadOnlyList<int[]> sources, CancellationToken cancellationToken)
    {
        int batch = sources.Count;
        int sourceLength = sources.Max(source => source.Length);
        var inputIds = new long[batch * sourceLength];
        var mask = new long[batch * sourceLength];
        for (int row = 0; row < batch; row++)
        {
            for (int column = 0; column < sourceLength; column++)
            {
                bool real = column < sources[row].Length;
                inputIds[row * sourceLength + column] = real ? sources[row][column] : _pad;
                mask[row * sourceLength + column] = real ? 1 : 0;
            }
        }

        long[] sourceShape = [batch, sourceLength];
        using OrtValue idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, sourceShape);
        using OrtValue maskValue = OrtValue.CreateTensorValueFromMemory(mask, sourceShape);
        using var runOptions = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> encoded = _encoder.Run(
            runOptions, ["input_ids", "attention_mask"], [idsValue, maskValue], ["last_hidden_state"]);
        OrtValue hidden = encoded[0];

        var outputs = new List<int>[batch];
        for (int row = 0; row < batch; row++) outputs[row] = [];
        var finished = new bool[batch];
        var next = new long[batch];
        // Marian starts decoding from the padding token.
        Array.Fill(next, _pad);
        int maxNewTokens = Math.Min(512, sourceLength * 2 + 16);

        IDisposableReadOnlyCollection<OrtValue>? first = null;
        IDisposableReadOnlyCollection<OrtValue>? previous = null;
        Dictionary<string, OrtValue>? firstByName = null;
        Dictionary<string, OrtValue>? previousByName = null;
        try
        {
            for (int step = 0; step < maxNewTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InferenceSession session = step == 0 ? _decoder : _decoderWithPast;
                string[] outputNames = step == 0 ? _decoderOutputs : _decoderWithPastOutputs;
                using OrtValue stepIds = OrtValue.CreateTensorValueFromMemory((long[])next.Clone(), [batch, 1]);
                var names = new List<string>(session.InputNames.Count);
                var values = new List<OrtValue>(session.InputNames.Count);
                foreach (string name in session.InputNames)
                {
                    names.Add(name);
                    values.Add(name switch
                    {
                        "input_ids" => stepIds,
                        "encoder_attention_mask" => maskValue,
                        "encoder_hidden_states" => hidden,
                        // Cross-attention keys come from the first step; self-attention keys grow each step.
                        _ when name.StartsWith(PastPrefix, StringComparison.Ordinal) =>
                            (name.Contains(".encoder.", StringComparison.Ordinal) ? firstByName : previousByName)!
                                ["present." + name[PastPrefix.Length..]],
                        _ => throw new InvalidDataException($"Unexpected decoder input {name}."),
                    });
                }

                IDisposableReadOnlyCollection<OrtValue> result = session.Run(runOptions, names, values, outputNames);
                var byName = new Dictionary<string, OrtValue>(outputNames.Length, StringComparer.Ordinal);
                for (int index = 0; index < outputNames.Length; index++) byName[outputNames[index]] = result[index];

                bool done = PickNextTokens(byName["logits"], next, finished, outputs);
                if (step == 0)
                {
                    first = result;
                    firstByName = byName;
                }
                else if (previous is not null && !ReferenceEquals(previous, first))
                {
                    previous.Dispose();
                }

                previous = result;
                previousByName = byName;
                if (done) break;
            }
        }
        finally
        {
            if (previous is not null && !ReferenceEquals(previous, first)) previous.Dispose();
            first?.Dispose();
        }

        return outputs;
    }

    /// <summary>Greedy choice per unfinished row; the padding token is never generated.</summary>
    private bool PickNextTokens(OrtValue logitsValue, long[] next, bool[] finished, List<int>[] outputs)
    {
        ReadOnlySpan<float> logits = logitsValue.GetTensorDataAsSpan<float>();
        long[] shape = logitsValue.GetTensorTypeAndShape().Shape;
        int positions = (int)shape[1];
        int vocabulary = (int)shape[2];
        bool done = true;
        for (int row = 0; row < next.Length; row++)
        {
            if (finished[row])
            {
                next[row] = _pad;
                continue;
            }

            ReadOnlySpan<float> scores = logits.Slice((row * positions + positions - 1) * vocabulary, vocabulary);
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int token = 0; token < scores.Length; token++)
            {
                if (token == _pad || !(scores[token] > bestScore)) continue;
                bestScore = scores[token];
                best = token;
            }

            next[row] = best;
            if (best == _targetEos || best < 0)
            {
                finished[row] = true;
                next[row] = _pad;
            }
            else
            {
                outputs[row].Add(best);
                done = false;
            }
        }
        return done;
    }

    private string Decode(List<int> ids)
    {
        var text = new StringBuilder();
        foreach (int id in ids)
        {
            if (id == _targetEos || id == _pad || id == _targetUnknown) continue;
            if ((uint)id < (uint)_targetPieces.Length && _targetPieces[id] is { } piece) text.Append(piece);
        }
        return text.Replace('▁', ' ').ToString().Trim();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
    }

    /// <summary>vocab.json of the target side: piece to id.</summary>
    private sealed record TargetVocabulary(string?[] Pieces, int EndOfSentence, int Pad, int Unknown)
    {
        internal static TargetVocabulary Load(string path)
        {
            Dictionary<string, int> map;
            using (FileStream stream = File.OpenRead(path))
            {
                map = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
                    ?? throw new InvalidDataException("The target vocabulary is empty.");
            }

            if (map.Count == 0 || map.Values.Any(id => id < 0 || id > 1_000_000))
                throw new InvalidDataException("The target vocabulary has invalid ids.");
            var pieces = new string?[map.Values.Max() + 1];
            foreach ((string piece, int id) in map) pieces[id] = piece;
            return new TargetVocabulary(pieces, Require(map, "</s>"), Require(map, "<pad>"),
                map.TryGetValue("<unk>", out int unknown) ? unknown : -1);
        }

        private static int Require(Dictionary<string, int> map, string piece) =>
            map.TryGetValue(piece, out int id) ? id : throw new InvalidDataException($"The target vocabulary has no {piece}.");
    }
}
