using System.Runtime.InteropServices;
using System.Text;

namespace MyCapture.App.Tests;

/// <summary>
/// The HLSL compiler every supported Windows ships (<c>d3dcompiler_47.dll</c>), so a test can prove
/// a checked-in pixel-shader blob is exactly what its checked-in source compiles to.
/// </summary>
internal static class ShaderCompiler
{
    private const uint OptimizationLevel3 = 1 << 15;

    internal static byte[] Compile(string source, string entryPoint, string target)
    {
        byte[] text = Encoding.ASCII.GetBytes(source);
        int result = D3DCompile(text, (nuint)text.Length, null, IntPtr.Zero, IntPtr.Zero, entryPoint, target,
            OptimizationLevel3, 0, out ID3DBlob? code, out ID3DBlob? errors);
        try
        {
            if (result < 0 || code is null)
            {
                throw new InvalidOperationException($"HLSL compile failed (0x{result:X8}): {(errors is null ? "" : AsText(errors))}");
            }

            return AsBytes(code);
        }
        finally
        {
            if (code is not null) Marshal.ReleaseComObject(code);
            if (errors is not null) Marshal.ReleaseComObject(errors);
        }
    }

    /// <summary>The instruction listing without the compiler's comment header, which names its version.</summary>
    internal static string Disassemble(byte[] bytecode)
    {
        int result = D3DDisassemble(bytecode, (nuint)bytecode.Length, 0, null, out ID3DBlob? listing);
        try
        {
            if (result < 0 || listing is null)
            {
                throw new InvalidOperationException($"Shader disassembly failed (0x{result:X8}).");
            }

            return string.Join('\n', AsText(listing)
                .Split('\n')
                .Select(line => line.TrimEnd('\r', ' ', '\0'))
                .Where(line => line.Length > 0 && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }
        finally
        {
            if (listing is not null) Marshal.ReleaseComObject(listing);
        }
    }

    private static byte[] AsBytes(ID3DBlob blob)
    {
        byte[] bytes = new byte[(int)blob.GetBufferSize()];
        Marshal.Copy(blob.GetBufferPointer(), bytes, 0, bytes.Length);
        return bytes;
    }

    private static string AsText(ID3DBlob blob) => Encoding.ASCII.GetString(AsBytes(blob)).TrimEnd('\0');

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nuint sourceSize, string? sourceName, IntPtr defines,
        IntPtr include, string entryPoint, string target, uint flags1, uint flags2,
        out ID3DBlob? code, out ID3DBlob? errors);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DDisassemble(byte[] source, nuint sourceSize, uint flags, string? comments,
        out ID3DBlob? disassembly);

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }
}
