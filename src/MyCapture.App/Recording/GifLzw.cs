using System.IO;

namespace MyCapture.App.Recording;

/// <summary>
/// GIF variable-length-code LZW for palette indices (GIF89a appendix F). The platform encoder
/// still chooses every palette and index; this codec only reads its indices back and writes a
/// masked copy, so it never changes a colour.
/// </summary>
internal static class GifLzw
{
    private const int MaximumBits = 12;
    private const int MaximumCode = 1 << MaximumBits;
    private const int HashSize = 5003; // prime, about 1.2 times the 4,096-entry table

    /// <summary>
    /// Encodes <paramref name="indices"/> as a GIF image data block: the LZW minimum code size
    /// byte, data sub-blocks of at most 255 bytes and the zero terminator.
    /// </summary>
    internal static byte[] Encode(ReadOnlySpan<byte> indices, int minimumCodeSize)
    {
        if (minimumCodeSize is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCodeSize));
        }

        if (indices.IsEmpty)
        {
            throw new ArgumentException("An image needs at least one pixel.", nameof(indices));
        }

        var writer = new BitWriter(minimumCodeSize, indices.Length);
        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;
        int codeBits = minimumCodeSize + 1;
        int nextCode = clearCode + 2;
        int[] hashKeys = new int[HashSize];
        short[] hashCodes = new short[HashSize];
        Array.Fill(hashKeys, -1);

        writer.Write(clearCode, codeBits);
        int prefix = indices[0];
        ThrowIfOutOfRange(prefix, clearCode);
        for (int position = 1; position < indices.Length; position++)
        {
            int symbol = indices[position];
            ThrowIfOutOfRange(symbol, clearCode);
            int key = (symbol << MaximumBits) | prefix;
            int slot = (symbol << 4) ^ prefix; // xor hashing, as in compress(1)
            int step = slot == 0 ? 1 : HashSize - slot;
            bool found = false;
            while (hashKeys[slot] >= 0)
            {
                if (hashKeys[slot] == key)
                {
                    prefix = hashCodes[slot];
                    found = true;
                    break;
                }

                slot -= step;
                if (slot < 0)
                {
                    slot += HashSize;
                }
            }

            if (found)
            {
                continue;
            }

            writer.Write(prefix, codeBits);
            prefix = symbol;
            if (nextCode < MaximumCode)
            {
                // A decoder adds the entry one code later; widening once the next code needs
                // another bit keeps both sides in step.
                if (nextCode == 1 << codeBits && codeBits < MaximumBits)
                {
                    codeBits++;
                }

                hashKeys[slot] = key;
                hashCodes[slot] = (short)nextCode++;
            }
            else
            {
                writer.Write(clearCode, codeBits);
                Array.Fill(hashKeys, -1);
                codeBits = minimumCodeSize + 1;
                nextCode = clearCode + 2;
            }
        }

        writer.Write(prefix, codeBits);
        if (nextCode == 1 << codeBits && codeBits < MaximumBits)
        {
            codeBits++;
        }

        writer.Write(endCode, codeBits);
        return writer.Finish();
    }

    /// <summary>
    /// Decodes an image data block (minimum code size byte, sub-blocks, terminator) into
    /// exactly <paramref name="pixelCount"/> indices.
    /// </summary>
    internal static byte[] Decode(ReadOnlySpan<byte> imageData, int pixelCount)
    {
        if (imageData.Length < 2)
        {
            throw new InvalidDataException("The GIF image data block is truncated.");
        }

        int minimumCodeSize = imageData[0];
        if (minimumCodeSize is < 2 or > 8)
        {
            throw new InvalidDataException($"Invalid GIF LZW minimum code size {minimumCodeSize}.");
        }

        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;
        var prefixes = new short[MaximumCode];
        var suffixes = new byte[MaximumCode];
        var firsts = new byte[MaximumCode];
        var stack = new byte[MaximumCode + 1];
        for (int code = 0; code < clearCode; code++)
        {
            suffixes[code] = (byte)code;
            firsts[code] = (byte)code;
            prefixes[code] = -1;
        }

        byte[] output = new byte[pixelCount];
        int written = 0;
        int codeBits = minimumCodeSize + 1;
        int nextCode = clearCode + 2;
        int previous = -1;
        int bitBuffer = 0;
        int bitCount = 0;
        int index = 1;
        int blockRemaining = 0;
        while (true)
        {
            while (bitCount < codeBits)
            {
                if (blockRemaining == 0)
                {
                    if (index >= imageData.Length)
                    {
                        throw new InvalidDataException("The GIF image data ended before its end code.");
                    }

                    blockRemaining = imageData[index++];
                    if (blockRemaining == 0)
                    {
                        throw new InvalidDataException("The GIF image data ended before its end code.");
                    }
                }

                if (index >= imageData.Length)
                {
                    throw new InvalidDataException("The GIF image data sub-block is truncated.");
                }

                bitBuffer |= imageData[index++] << bitCount;
                bitCount += 8;
                blockRemaining--;
            }

            int code = bitBuffer & ((1 << codeBits) - 1);
            bitBuffer >>= codeBits;
            bitCount -= codeBits;
            if (code == clearCode)
            {
                codeBits = minimumCodeSize + 1;
                nextCode = clearCode + 2;
                previous = -1;
                continue;
            }

            if (code == endCode)
            {
                break;
            }

            int emit = code;
            int depth = 0;
            if (previous < 0)
            {
                if (code >= clearCode)
                {
                    throw new InvalidDataException("The GIF image data starts with an undefined code.");
                }
            }
            else if (code == nextCode)
            {
                // The code being defined: previous string plus its own first symbol.
                stack[depth++] = firsts[previous];
                emit = previous;
            }
            else if (code > nextCode)
            {
                throw new InvalidDataException("The GIF image data refers to an undefined code.");
            }

            byte first = firsts[emit];
            for (int walk = emit; walk >= 0; walk = prefixes[walk])
            {
                stack[depth++] = suffixes[walk];
            }

            if (written + depth > pixelCount)
            {
                throw new InvalidDataException("The GIF image data holds more pixels than its rectangle.");
            }

            while (depth > 0)
            {
                output[written++] = stack[--depth];
            }

            if (previous >= 0 && nextCode < MaximumCode)
            {
                prefixes[nextCode] = (short)previous;
                suffixes[nextCode] = first;
                firsts[nextCode] = firsts[previous];
                nextCode++;
                if (nextCode == 1 << codeBits && codeBits < MaximumBits)
                {
                    codeBits++;
                }
            }

            previous = code;
        }

        if (written != pixelCount)
        {
            throw new InvalidDataException("The GIF image data holds fewer pixels than its rectangle.");
        }

        return output;
    }

    private static void ThrowIfOutOfRange(int symbol, int clearCode)
    {
        if (symbol >= clearCode)
        {
            throw new ArgumentException("A palette index does not fit the LZW minimum code size.", nameof(symbol));
        }
    }

    /// <summary>Packs codes least-significant bit first into 255-byte data sub-blocks.</summary>
    private sealed class BitWriter
    {
        private readonly MemoryStream _stream;
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _buffer;
        private int _bits;

        internal BitWriter(int minimumCodeSize, int pixelCount)
        {
            _stream = new MemoryStream(Math.Max(64, pixelCount / 2));
            _stream.WriteByte((byte)minimumCodeSize);
        }

        internal void Write(int code, int bits)
        {
            _buffer |= code << _bits;
            _bits += bits;
            while (_bits >= 8)
            {
                Put((byte)_buffer);
                _buffer >>= 8;
                _bits -= 8;
            }
        }

        internal byte[] Finish()
        {
            if (_bits > 0)
            {
                Put((byte)_buffer);
                _buffer = 0;
                _bits = 0;
            }

            Flush();
            _stream.WriteByte(0);
            return _stream.ToArray();
        }

        private void Put(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == _block.Length)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_blockLength > 0)
            {
                _stream.WriteByte((byte)_blockLength);
                _stream.Write(_block, 0, _blockLength);
                _blockLength = 0;
            }
        }
    }
}
