using System.Runtime.InteropServices;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// A continuous in-box Audio Resampler DSP. One instance belongs to one source generation
/// and its MTA thread. It retains FIR history across packets. Output remains indexed from
/// the first input sample: the DSP's buffering delays availability, never presentation time.
/// </summary>
internal sealed class MediaFoundationRecordingResampler : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly MediaFoundationLifetime.Lease _lease;
    private IRecordingResamplerTransform? _transform;
    private long _inputFrames;
    internal int SampleRate { get; }
    internal long OutputFrames { get; private set; }

    internal MediaFoundationRecordingResampler(int sampleRate)
    {
        SampleRate = sampleRate;
        _lease = MediaFoundationPlatform.Acquire();
        try
        {
            _transform = (IRecordingResamplerTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("f447b69e-1884-4a7e-8055-346f74d6edb3"), throwOnError: true)!)!;
            IMFMediaType? input = null, output = null;
            try
            {
                input = CreateType(sampleRate); output = CreateType(RecordingAudioFormat.SampleRate);
                Check(_transform.SetInputType(0, input, 0)); Check(_transform.SetOutputType(0, output, 0));
            }
            finally { Release(input); Release(output); }
            Check(_transform.ProcessMessage(0x10000000, IntPtr.Zero)); // BEGIN_STREAMING
            Check(_transform.ProcessMessage(0x10000003, IntPtr.Zero)); // START_OF_STREAM
        }
        catch { Dispose(); throw; }
    }

    internal float[] Process(ReadOnlySpan<float> stereo, bool drain = false)
    {
        RequireOwner();
        if (stereo.Length % 2 != 0) throw new ArgumentException("Complete stereo frames are required.", nameof(stereo));
        if (_transform is null) throw new ObjectDisposedException(nameof(MediaFoundationRecordingResampler));
        List<float> result = [];
        if (!stereo.IsEmpty)
        {
            Check(MFCreateMemoryBuffer((uint)(stereo.Length * sizeof(float)), out IMFMediaBuffer inputBuffer));
            IMFSample? input = null;
            try
            {
                Check(inputBuffer.Lock(out IntPtr pointer, out _, out _));
                try { unsafe { stereo.CopyTo(new Span<float>((void*)pointer, stereo.Length)); } }
                finally { Check(inputBuffer.Unlock()); }
                Check(inputBuffer.SetCurrentLength((uint)(stereo.Length * sizeof(float))));
                Check(MFCreateSample(out input));
                Check(input.AddBuffer(inputBuffer));
                Check(input.SetSampleTime((long)((Int128)_inputFrames * 10_000_000 / SampleRate)));
                Check(input.SetSampleDuration((long)((Int128)(stereo.Length / 2) * 10_000_000 / SampleRate)));
                Check(_transform.ProcessInput(0, input, 0));
                _inputFrames += stereo.Length / 2;
            }
            finally { Release(input); Release(inputBuffer); }
        }
        if (drain)
        {
            Check(_transform.ProcessMessage(0x10000002, IntPtr.Zero)); // END_OF_STREAM
            Check(_transform.ProcessMessage(1, IntPtr.Zero)); // DRAIN
        }
        for (int iteration = 0; iteration < 128; iteration++)
        {
            Check(MFCreateMemoryBuffer(64 * 1024, out IMFMediaBuffer outputBuffer));
            IMFSample? output = null;
            var data = new ResamplerOutput[1];
            IntPtr suppliedSample = IntPtr.Zero;
            try
            {
                Check(MFCreateSample(out output));
                Check(output.AddBuffer(outputBuffer));
                suppliedSample = Marshal.GetComInterfaceForObject(output, typeof(IMFSample));
                data[0].Sample = suppliedSample;
                int hr = _transform.ProcessOutput(0, 1, data, out _);
                if (hr == unchecked((int)0xC00D6D72)) break; // NEED_MORE_INPUT
                Check(hr);
                Check(outputBuffer.Lock(out IntPtr pointer, out _, out uint length));
                try
                {
                    if (length % 8 != 0) throw new InvalidOperationException("Resampler returned partial stereo PCM.");
                    float[] block = new float[length / sizeof(float)];
                    Marshal.Copy(pointer, block, 0, block.Length);
                    result.AddRange(block);
                }
                finally { Check(outputBuffer.Unlock()); }
                if (iteration == 127) throw new InvalidOperationException("Resampler exceeded its bounded output budget.");
            }
            finally
            {
                if (data[0].Events != IntPtr.Zero) Marshal.Release(data[0].Events);
                if (data[0].Sample != IntPtr.Zero && data[0].Sample != suppliedSample) Marshal.Release(data[0].Sample);
                if (suppliedSample != IntPtr.Zero) Marshal.Release(suppliedSample);
                Release(output); Release(outputBuffer);
            }
        }
        OutputFrames += result.Count / 2;
        return result.ToArray();
    }

    private static IMFMediaType CreateType(int rate)
    {
        Check(MFCreateMediaType(out IMFMediaType type));
        try
        {
            SetGuid(type, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", "73647561-0000-0010-8000-00aa00389b71");
            SetGuid(type, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", "00000003-0000-0010-8000-00aa00389b71");
            SetUInt(type, "37e48bf5-645e-4c5b-89de-ada9e29b696a", 2);
            SetUInt(type, "5faeeae7-0290-4c31-9e8a-c534f68d9dba", (uint)rate);
            SetUInt(type, "f2deb57f-40fa-4764-aa33-ed4f2d1ff669", 32);
            SetUInt(type, "322de230-9eeb-43bd-ab7a-ff412251541d", 8);
            SetUInt(type, "1aab75c8-cfef-451c-ab95-ac034b8e1731", checked((uint)rate * 8));
            SetUInt(type, "55fb5765-644a-4caf-8479-938983bb1588", 3); // stereo channel mask
            return type;
        }
        catch { Release(type); throw; }
    }

    private static void SetGuid(IMFMediaType type, string key, string value)
    { Guid k = new(key), v = new(value); Check(type.SetGUID(ref k, ref v)); }
    private static void SetUInt(IMFMediaType type, string key, uint value)
    { Guid k = new(key); Check(type.SetUINT32(ref k, value)); }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private void RequireOwner()
    { if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Resampler crossed its owner thread."); }
    public void Dispose()
    {
        RequireOwner();
        Release(_transform); _transform = null; _lease.Dispose();
    }

    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint bytes, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample sample);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ResamplerOutput { public uint Stream; public IntPtr Sample; public uint Status; public IntPtr Events; }

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingResamplerTransform
{
    [PreserveSig] int GetStreamLimits(out uint a, out uint b, out uint c, out uint d);
    [PreserveSig] int GetStreamCount(out uint a, out uint b);
    [PreserveSig] int GetStreamIDs(uint a, IntPtr b, uint c, IntPtr d);
    [PreserveSig] int GetInputStreamInfo(uint id, IntPtr info);
    [PreserveSig] int GetOutputStreamInfo(uint id, IntPtr info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(uint id);
    [PreserveSig] int AddInputStreams(uint count, IntPtr ids);
    [PreserveSig] int GetInputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(uint id, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint id, IntPtr value);
    [PreserveSig] int ProcessMessage(uint message, IntPtr parameter);
    [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ResamplerOutput[] buffers, out uint status);
}
