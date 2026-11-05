using System.Runtime.InteropServices;

namespace MyCapture.Platform.Recording;

public sealed partial class MediaFoundationVideoEncoder
{
    // Only the internal diagnostic constructor sets this field. Public options and defaults
    // remain unchanged; the experiment supplies exactly one encoder attribute.
    private readonly uint? _diagnosticQualityVsSpeed;

    private void SetDiagnosticQualityVsSpeed(IMFMediaType inType, uint effort)
    {
        Check(MFCreateAttributes(out IMFAttributes parameters, 1), nameof(MFCreateAttributes));
        IntPtr pointer = IntPtr.Zero;
        try
        {
            Guid key = CODECAPI_AVEncCommonQualityVsSpeed;
            Check(parameters.SetUINT32(ref key, effort), "IMFAttributes.SetUINT32(diagnostic quality vs speed)");
            pointer = Marshal.GetComInterfaceForObject(parameters, typeof(IMFAttributes));
            Check(_writer!.SetInputMediaType(_streamIndex, inType, pointer), nameof(IMFSinkWriter.SetInputMediaType));
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            Marshal.ReleaseComObject(parameters);
        }
    }

    /// <summary>Test opt-in getters only; never called by normal encoding or the fast-finalize probe.</summary>
    internal IReadOnlyList<CodecTransformObservation> CaptureCodecParameters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null) throw new InvalidOperationException("The diagnostic encoder has no writer.");
        // Borrowed interface alias: ownership remains with the encoder.
        var extended = (IDiagnosticSinkWriterEx)_writer;
        var observations = new List<CodecTransformObservation>();
        for (int index = 0; index < 8; index++)
        {
            IDiagnosticTransform? transform = null;
            try
            {
                int hr = extended.GetTransformForStream(_streamIndex, index, out Guid category, out transform);
                if (hr < 0 || transform is null)
                {
                    observations.Add(new(index, category, hr, null, null, "Enumeration ended"));
                    return observations;
                }
                var identity = new Dictionary<string, object?>();
                ReadDiagnosticTransform(transform, identity);
                try
                {
                    // The cast only queries this returned transform's interface. Do not release
                    // the alias separately from the one owned transform reference below.
                    var api = (IDiagnosticCodecApi)transform;
                    var parameters = new Dictionary<string, CodecParameterObservation>();
                    foreach ((string name, Guid key) in new[]
                    {
                        ("QualityVsSpeed", CODECAPI_AVEncCommonQualityVsSpeed),
                        ("RateControlMode", CODECAPI_AVEncCommonRateControlMode),
                        ("Quality", CODECAPI_AVEncCommonQuality),
                        ("MeanBitRate", new Guid("f7222374-2144-4815-b550-a37f8e12ee52")),
                        ("GopSize", new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1")),
                        ("BPictureCount", new Guid("8d390aac-dc5c-4200-b57f-814d04babab2")),
                    })
                    {
                        Guid property = key;
                        int supported = api.IsSupported(ref property);
                        int modifiable = api.IsModifiable(ref property);
                        parameters.Add(name, new(supported, modifiable,
                            ReadCodecValue(api, key, useDefault: false), ReadCodecValue(api, key, useDefault: true)));
                    }
                    observations.Add(new(index, category, hr, identity, parameters, null));
                }
                catch (Exception error)
                {
                    observations.Add(new(index, category, hr, identity, null,
                        $"{error.GetType().Name} (0x{error.HResult:X8})"));
                }
            }
            finally { if (transform is not null) Marshal.ReleaseComObject(transform); }
        }
        throw new InvalidOperationException("Codec diagnostic transform enumeration exceeded its bound.");
    }

    private static CodecValueObservation ReadCodecValue(IDiagnosticCodecApi api, Guid key, bool useDefault)
    {
        // VARIANT is 24 bytes on Win64 and 16 on Win32. Initialize to VT_EMPTY before
        // every getter, retain the native VARTYPE, and clear even unsupported results.
        int size = IntPtr.Size == 8 ? 24 : 16;
        IntPtr value = Marshal.AllocCoTaskMem(size);
        int hr = unchecked((int)0x80004005), clearHr = hr;
        ushort type = 0;
        object? scalar = null;
        for (int offset = 0; offset < size; offset += 8) Marshal.WriteInt64(value, offset, 0);
        try
        {
            hr = useDefault ? api.GetDefaultValue(ref key, value) : api.GetValue(ref key, value);
            type = unchecked((ushort)Marshal.ReadInt16(value));
            if (hr >= 0)
                scalar = type switch
                {
                    2 => Marshal.ReadInt16(value, 8),
                    3 => Marshal.ReadInt32(value, 8),
                    5 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(value, 8)),
                    11 => Marshal.ReadInt16(value, 8) != 0,
                    18 => unchecked((ushort)Marshal.ReadInt16(value, 8)),
                    19 => unchecked((uint)Marshal.ReadInt32(value, 8)),
                    20 => Marshal.ReadInt64(value, 8),
                    21 => unchecked((ulong)Marshal.ReadInt64(value, 8)),
                    _ => null,
                };
        }
        finally
        {
            try { clearHr = DiagnosticVariantClear(value); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        return new(hr, type, scalar, clearHr);
    }

    [DllImport("oleaut32.dll", EntryPoint = "VariantClear", ExactSpelling = true)]
    private static extern int DiagnosticVariantClear(IntPtr value);
}

internal sealed record CodecTransformObservation(int Index, Guid Category, int HResult,
    IReadOnlyDictionary<string, object?>? Identity,
    IReadOnlyDictionary<string, CodecParameterObservation>? Parameters, string? Error);
internal sealed record CodecParameterObservation(int SupportedHResult, int ModifiableHResult,
    CodecValueObservation Current, CodecValueObservation Default);
internal sealed record CodecValueObservation(int HResult, ushort VariantType, object? Value, int ClearHResult);

// Native prefix through GetValue, from Windows SDK strmif.h. No setter is exposed.
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDiagnosticCodecApi
{
    [PreserveSig] int IsSupported(ref Guid property);
    [PreserveSig] int IsModifiable(ref Guid property);
    [PreserveSig] int GetParameterRange(ref Guid property, IntPtr minimum, IntPtr maximum, IntPtr step);
    [PreserveSig] int GetParameterValues(ref Guid property, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(ref Guid property, IntPtr value);
    [PreserveSig] int GetValue(ref Guid property, IntPtr value);
}
