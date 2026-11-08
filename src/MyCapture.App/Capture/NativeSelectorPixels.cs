using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace MyCapture.App.Capture;

/// <summary>Exact 8-bit dimmer composition on packed BGR32 pixels, without a second frame buffer.</summary>
internal readonly struct NativeSelectorPixels
{
    private readonly uint _weight, _blueBias, _greenBias, _redBias;

    internal NativeSelectorPixels(Color dimmer, int alpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(alpha);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 255);
        _weight = (uint)(255 - alpha);
        _blueBias = (uint)(dimmer.B * alpha + 127);
        _greenBias = (uint)(dimmer.G * alpha + 127);
        _redBias = (uint)(dimmer.R * alpha + 127);
    }

    internal void Dim(Span<uint> pixels, bool useSimd = true)
    {
        int index = 0;
        if (useSimd && Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
        {
            var mask = new Vector<uint>(0x00ff00ffu);
            var greenMask = new Vector<uint>(255u);
            var weight = new Vector<uint>(_weight);
            var rbBias = new Vector<uint>(_blueBias | (_redBias << 16));
            var greenBias = new Vector<uint>(_greenBias);
            var rbOne = new Vector<uint>(0x00010001u);
            var alpha = new Vector<uint>(0xff000000u);
            ref uint first = ref MemoryMarshal.GetReference(pixels);
            for (; index <= pixels.Length - Vector<uint>.Count; index += Vector<uint>.Count)
            {
                Vector<uint> input = Vector.LoadUnsafe(ref first, (nuint)index);
                // B and R each occupy a 16-bit lane; the weighted sum never carries between lanes.
                Vector<uint> rb = (input & mask) * weight + rbBias;
                Vector<uint> green = ((input >> 8) & greenMask) * weight + greenBias;
                // Exact floor(x / 255) for these sums (0..65152), including round-to-nearest bias.
                rb = ((rb + rbOne + ((rb >> 8) & mask)) >> 8) & mask;
                green = (green + Vector<uint>.One + (green >> 8)) >> 8;
                (rb | (green << 8) | alpha).StoreUnsafe(ref first, (nuint)index);
            }
        }
        for (; index < pixels.Length; index++)
        {
            uint pixel = pixels[index];
            uint blue = ((pixel & 255) * _weight + _blueBias) / 255;
            uint green = (((pixel >> 8) & 255) * _weight + _greenBias) / 255;
            uint red = (((pixel >> 16) & 255) * _weight + _redBias) / 255;
            pixels[index] = 0xff000000u | blue | (green << 8) | (red << 16);
        }
    }

    internal static void MakeOpaque(Span<uint> pixels)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
        {
            var alpha = new Vector<uint>(0xff000000u);
            ref uint first = ref MemoryMarshal.GetReference(pixels);
            for (; index <= pixels.Length - Vector<uint>.Count; index += Vector<uint>.Count)
                (Vector.LoadUnsafe(ref first, (nuint)index) | alpha).StoreUnsafe(ref first, (nuint)index);
        }
        for (; index < pixels.Length; index++) pixels[index] |= 0xff000000u;
    }
}
