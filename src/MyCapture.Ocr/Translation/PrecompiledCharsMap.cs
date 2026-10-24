using System.Buffers.Binary;
using System.IO;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// SentencePiece normalization rules (normalizer_spec.precompiled_charsmap): a little-endian
/// uint32 trie size in bytes, a Darts-clone double array of little-endian uint32 units, and the
/// NUL-terminated UTF-8 replacement strings the trie values point to. Immutable after parsing.
/// </summary>
internal sealed class PrecompiledCharsMap
{
    private readonly uint[] _units;
    private readonly byte[] _replacements;

    private PrecompiledCharsMap(uint[] units, byte[] replacements)
    {
        _units = units;
        _replacements = replacements;
    }

    internal static PrecompiledCharsMap Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length <= sizeof(uint))
        {
            throw new InvalidDataException("The SentencePiece normalization rules are truncated.");
        }

        uint trieBytes = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        if (trieBytes == 0 || trieBytes % sizeof(uint) != 0 || trieBytes > (uint)(blob.Length - sizeof(uint)))
        {
            throw new InvalidDataException("The SentencePiece normalization trie size is invalid.");
        }

        ReadOnlySpan<byte> trie = blob.Slice(sizeof(uint), (int)trieBytes);
        var units = new uint[trie.Length / sizeof(uint)];
        for (int i = 0; i < units.Length; i++)
        {
            units[i] = BinaryPrimitives.ReadUInt32LittleEndian(trie[(i * sizeof(uint))..]);
        }

        return new PrecompiledCharsMap(units, blob[(sizeof(uint) + trie.Length)..].ToArray());
    }

    /// <summary>
    /// Finds the longest rule whose source is a prefix of <paramref name="input"/> (Darts-clone
    /// commonPrefixSearch). Returns the matched byte count, or 0 when no rule applies. The
    /// replacement is empty for a rule that deletes its source.
    /// </summary>
    internal int MatchLongestPrefix(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> replacement)
    {
        uint[] units = _units;
        int longest = 0;
        uint value = 0;
        uint node = Offset(units[0]);
        for (int i = 0; i < input.Length; i++)
        {
            node ^= input[i];
            uint unit = UnitAt(units, node);
            if (Label(unit) != input[i])
            {
                break;
            }

            node ^= Offset(unit);
            if (HasLeaf(unit))
            {
                longest = i + 1;
                value = UnitAt(units, node) & 0x7FFF_FFFFu;
            }
        }

        if (longest == 0)
        {
            replacement = default;
            return 0;
        }

        if (value > (uint)_replacements.Length)
        {
            throw new InvalidDataException("A SentencePiece normalization rule points outside its replacement table.");
        }

        ReadOnlySpan<byte> rest = _replacements.AsSpan((int)value);
        int end = rest.IndexOf((byte)0);
        replacement = end < 0 ? rest : rest[..end];
        return longest;
    }

    private static uint UnitAt(uint[] units, uint index) =>
        index < (uint)units.Length
            ? units[index]
            : throw new InvalidDataException("The SentencePiece normalization trie is corrupt.");

    // Darts-clone unit layout: label in bits 0-7 (bit 31 marks a value unit), has-leaf in bit 8,
    // offset in bits 10-31 scaled by 256 when bit 9 is set.
    private static bool HasLeaf(uint unit) => ((unit >> 8) & 1) != 0;

    private static uint Label(uint unit) => unit & 0x8000_00FFu;

    private static uint Offset(uint unit) => (unit >> 10) << (int)((unit & (1u << 9)) >> 6);
}
