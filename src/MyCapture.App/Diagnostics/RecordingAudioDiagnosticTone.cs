using System.Runtime.InteropServices;
using MyCapture.Core.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Explicit diagnostic playback only. It never changes endpoint volume or mute.</summary>
internal sealed class RecordingAudioDiagnosticTone : IDisposable
{
    internal const double AmplitudeDbfs = -60;
    private const int Frequency = 1_000;
    private readonly object _gate = new();
    private IntPtr _device, _header, _samples;
    private bool _prepared, _submitted;

    private RecordingAudioDiagnosticTone() { }

    /// <summary>One second, starting at phase zero; an exact integer number of cycles loops continuously.</summary>
    internal static short[] CreateSamples()
    {
        var samples = new short[RecordingAudioFormat.SampleRate * RecordingAudioFormat.Channels];
        const int framesPerCycle = RecordingAudioFormat.SampleRate / Frequency;
        double amplitude = short.MaxValue * Math.Pow(10, AmplitudeDbfs / 20);
        for (int frame = 0; frame < RecordingAudioFormat.SampleRate; frame++)
        {
            short value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * (frame % framesPerCycle) / framesPerCycle));
            samples[frame * 2] = samples[frame * 2 + 1] = value;
        }
        return samples;
    }

    /// <summary>Opens the default shared playback path only when the caller explicitly requests the tone.</summary>
    internal static RecordingAudioDiagnosticTone Start()
    {
        var tone = new RecordingAudioDiagnosticTone();
        try { tone.Open(); return tone; }
        catch (Exception startFailure)
        {
            try { tone.Dispose(); }
            catch (Exception closeFailure)
            {
                throw new AggregateException("Diagnostic tone startup and native cleanup both failed.", startFailure, closeFailure);
            }
            throw;
        }
    }

    private void Open()
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = RecordingAudioFormat.Channels,
            SamplesPerSecond = RecordingAudioFormat.SampleRate,
            AverageBytesPerSecond = RecordingAudioFormat.SampleRate * RecordingAudioFormat.BytesPerFrame,
            BlockAlign = RecordingAudioFormat.BytesPerFrame,
            BitsPerSample = RecordingAudioFormat.BitsPerSample,
            ExtraSize = 0,
        };
        Check(WaveOutOpen(out IntPtr device, uint.MaxValue, ref format, IntPtr.Zero, UIntPtr.Zero, 0), "waveOutOpen");
        _device = device;
        short[] pcm = CreateSamples();
        int byteCount = checked(pcm.Length * sizeof(short));
        _samples = Marshal.AllocHGlobal(byteCount);
        Marshal.Copy(pcm, 0, _samples, pcm.Length);
        _header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(new WaveHeader
        {
            Data = _samples,
            BufferLength = (uint)byteCount,
            BytesRecorded = 0,
            User = UIntPtr.Zero,
            Flags = 0x4 | 0x8, // WHDR_BEGINLOOP | WHDR_ENDLOOP
            Loops = uint.MaxValue,
            Next = IntPtr.Zero,
            Reserved = UIntPtr.Zero,
        }, _header, false);
        Check(WaveOutPrepareHeader(_device, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutPrepareHeader");
        _prepared = true;
        Check(WaveOutWrite(_device, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutWrite");
        _submitted = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // A failed reset/unprepare leaves buffers owned and permits a later Dispose retry.
            // Freeing memory still referenced by winmm would create a native use-after-free.
            if (_submitted)
            {
                Check(WaveOutReset(_device), "waveOutReset");
                _submitted = false;
            }
            if (_prepared)
            {
                Check(WaveOutUnprepareHeader(_device, _header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutUnprepareHeader");
                _prepared = false;
            }
            if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
            if (_samples != IntPtr.Zero) { Marshal.FreeHGlobal(_samples); _samples = IntPtr.Zero; }
            if (_device != IntPtr.Zero)
            {
                Check(WaveOutClose(_device), "waveOutClose");
                _device = IntPtr.Zero;
            }
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Diagnostic tone {operation} failed (MMRESULT {result}).");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    // DWORD_PTR fields and pointer fields are native-sized: WAVEHDR is 32 bytes on x86, 48 on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen", ExactSpelling = true)]
    private static extern uint WaveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format,
        IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader", ExactSpelling = true)]
    private static extern uint WaveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite", ExactSpelling = true)]
    private static extern uint WaveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutReset", ExactSpelling = true)]
    private static extern uint WaveOutReset(IntPtr device);
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader", ExactSpelling = true)]
    private static extern uint WaveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutClose", ExactSpelling = true)]
    private static extern uint WaveOutClose(IntPtr device);
}
