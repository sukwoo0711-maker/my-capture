using System.IO;
using System.Text.Json;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// Marian models either use SentencePiece ids directly (sepvoc) or map the piece text into
/// a shared encoder/decoder vocabulary. The ko-en model uses the latter, including a different EOS id.
/// </summary>
internal sealed class MarianSourceVocabulary
{
    private readonly SentencePieceModel _source;
    private readonly int[]? _modelIds;
    private readonly int _endOfSentence;

    internal MarianSourceVocabulary(SentencePieceModel source, IReadOnlyDictionary<string, int>? modelVocabulary = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.EndOfSentenceId < 0)
            throw new InvalidDataException("The source vocabulary has no end-of-sentence piece.");
        _endOfSentence = source.EndOfSentenceId;
        if (modelVocabulary is null) return;
        if (modelVocabulary.Count == 0 || modelVocabulary.Values.Any(id => id < 0 || id > 1_000_000) ||
            modelVocabulary.Values.Distinct().Count() != modelVocabulary.Count)
            throw new InvalidDataException("The shared source vocabulary has invalid or duplicate ids.");
        if (!modelVocabulary.TryGetValue("<unk>", out int unknown) ||
            !modelVocabulary.TryGetValue("</s>", out _endOfSentence))
            throw new InvalidDataException("The shared source vocabulary has no unknown or end-of-sentence token.");
        _modelIds = new int[source.PieceCount];
        for (int id = 0; id < _modelIds.Length; id++)
            _modelIds[id] = modelVocabulary.TryGetValue(source.PieceAt(id), out int mapped) ? mapped : unknown;
    }

    internal static MarianSourceVocabulary Load(SentencePieceModel source, string? vocabularyPath)
    {
        if (vocabularyPath is null) return new(source);
        using FileStream stream = File.OpenRead(vocabularyPath);
        if (stream.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("The shared source vocabulary is too large.");
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
            ?? throw new InvalidDataException("The shared source vocabulary is empty.");
        return new(source, vocabulary);
    }

    internal int[] Encode(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        int[] pieces = _source.EncodeToIds(text);
        int length = Math.Min(pieces.Length, maxTokens - 1);
        var ids = new int[length + 1];
        if (_modelIds is null) pieces.AsSpan(0, length).CopyTo(ids);
        else for (int index = 0; index < length; index++) ids[index] = _modelIds[pieces[index]];
        ids[length] = _endOfSentence;
        return ids;
    }
}
