using System.Security.Cryptography;

namespace MyCapture.Core.Pin;

internal enum PinSessionFilePurpose : byte { Manifest = 1, Image = 2 }

/// <summary>Versioned, purpose-bound DPAPI envelope for session files; legacy bytes stay readable.</summary>
internal static class PinSessionProtection
{
    private static ReadOnlySpan<byte> Magic => "MCPIN\0"u8;
    internal const int HeaderLength = 8;
    private const byte Version = 1;

    internal static byte[] Protect(byte[] plaintext, PinSessionFilePurpose purpose)
    {
        byte[] header = CreateHeader(purpose);
        byte[] ciphertext = ProtectedData.Protect(plaintext, header, DataProtectionScope.CurrentUser);
        byte[] envelope = new byte[header.Length + ciphertext.Length];
        header.CopyTo(envelope, 0);
        ciphertext.CopyTo(envelope, header.Length);
        return envelope;
    }

    internal static byte[] Read(byte[] fileBytes, PinSessionFilePurpose purpose, out bool isProtected)
    {
        ReadOnlySpan<byte> bytes = fileBytes;
        isProtected = bytes.StartsWith(Magic)
            || (bytes.Length > 0 && bytes.Length < Magic.Length && Magic.StartsWith(bytes));
        if (!isProtected) return fileBytes;

        byte[] header = CreateHeader(purpose);
        if (bytes.Length <= HeaderLength || !bytes[..HeaderLength].SequenceEqual(header))
            throw new CryptographicException("Unsupported pin session protection header.");
        return ProtectedData.Unprotect(fileBytes[HeaderLength..], header, DataProtectionScope.CurrentUser);
    }

    private static byte[] CreateHeader(PinSessionFilePurpose purpose)
    {
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[6] = Version;
        header[7] = (byte)purpose;
        return header;
    }
}
