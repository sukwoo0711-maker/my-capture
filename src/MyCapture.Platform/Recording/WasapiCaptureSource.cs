using System.Runtime.InteropServices;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>Shared eConsole endpoint capture; no render client is ever created here.</summary>
internal sealed class WasapiCaptureSource(RecordingAudioSourceKind kind) : IRecordingAudioPacketSource
{
    public void Run(Action<RecordingAudioPacket> packet, Action<RecordingAudioSourceEvent> state,
        CancellationToken cancellationToken)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        IRecordingDeviceEnumerator? enumerator = null;
        IRecordingDevice? device = null;
        IRecordingAudioClient? client = null;
        IRecordingCaptureClient? capture = null;
        IntPtr formatPointer = IntPtr.Zero;
        EndpointNotification? notification = null;
        bool started = false, registered = false;
        using var ready = new AutoResetEvent(false);
        try
        {
            Check(initialized);
            cancellationToken.ThrowIfCancellationRequested();
            enumerator = (IRecordingDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"), true)!)!;
            int flow = kind == RecordingAudioSourceKind.System ? 0 : 1;
            Check(enumerator.GetDefaultAudioEndpoint(flow, 0, out device));
            Check(device.GetId(out string id));
            notification = new(flow, id, ready);
            Check(enumerator.RegisterEndpointNotificationCallback(notification));
            registered = true;
            cancellationToken.ThrowIfCancellationRequested();
            Guid iid = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out object audioClient));
            client = (IRecordingAudioClient)audioClient;
            cancellationToken.ThrowIfCancellationRequested();
            Check(client.GetMixFormat(out formatPointer));
            var format = RecordingNativeAudioFormat.Read(formatPointer);
            // Event capture plus a 10 ms watchdog also services silent loopback endpoints.
            uint flags = 0x00040000u | (kind == RecordingAudioSourceKind.System ? 0x00020000u : 0u);
            Check(client.Initialize(0, flags, 1_000_000, 0, formatPointer, IntPtr.Zero));
            cancellationToken.ThrowIfCancellationRequested();
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            iid = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
            Check(client.GetService(ref iid, out object service));
            capture = (IRecordingCaptureClient)service;
            cancellationToken.ThrowIfCancellationRequested();
            Check(client.Start());
            started = true;
            state(RecordingAudioSourceEvent.Opened);
            WaitHandle[] waits = [cancellationToken.WaitHandle, ready];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (notification.Changed) throw new InvalidOperationException("Default endpoint or format changed.");
                for (int drained = 0; drained < 64 && !cancellationToken.IsCancellationRequested; drained++)
                {
                    Check(capture.GetNextPacketSize(out uint available));
                    if (available == 0) break;
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint packetFlags,
                        out ulong position, out ulong qpc));
                    try
                    {
                        if (frames > format.SampleRate || position > long.MaxValue || qpc > long.MaxValue)
                            throw new InvalidOperationException("Invalid native packet dimensions or timestamp.");
                        float[] stereo = (packetFlags & 2) != 0 ? new float[checked((int)frames * 2)]
                            : format.Decode(data, checked((int)frames));
                        var mapped = ((packetFlags & 2) != 0 ? RecordingAudioPacketFlags.Silent : 0)
                            | ((packetFlags & 1) != 0 ? RecordingAudioPacketFlags.Discontinuity : 0)
                            | ((packetFlags & 4) != 0 ? RecordingAudioPacketFlags.TimestampError : 0);
                        packet(new(stereo, format.SampleRate, (long)position, (long)qpc, mapped));
                    }
                    finally { Check(capture.ReleaseBuffer(frames)); }
                }
                _ = WaitHandle.WaitAny(waits, 10);
            }
        }
        finally
        {
            // These calls can be stalled by a broken driver. Only this background owner waits;
            // the recording session has already closed its generation and joins for <=100 ms.
            try { if (started) _ = client?.Stop(); }
            finally
            {
                if (registered && notification is not null && enumerator is not null)
                    _ = enumerator.UnregisterEndpointNotificationCallback(notification);
                Release(capture); Release(client); Release(device); Release(enumerator);
                if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
                if (started) state(RecordingAudioSourceEvent.Closed);
                if (initialized >= 0) CoUninitialize();
                GC.KeepAlive(notification);
            }
        }
    }

    public void Dispose() { }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointNotification(int flow, string endpoint, EventWaitHandle signal) : IRecordingEndpointNotification
    {
        private int _changed;
        internal bool Changed => Volatile.Read(ref _changed) != 0;
        private int Change() { Interlocked.Exchange(ref _changed, 1); try { signal.Set(); } catch (ObjectDisposedException) { } return 0; }
        public int OnDeviceStateChanged(string id, uint state) => id == endpoint ? Change() : 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => id == endpoint ? Change() : 0;
        public int OnDefaultDeviceChanged(int changedFlow, int role, string? id) => changedFlow == flow && role == 0 ? Change() : 0;
        public int OnPropertyValueChanged(string id, RecordingPropertyKey key) => id == endpoint ? Change() : 0;
    }
}

/// <summary>Native format validation and sample decoding are pure and hardware-free testable.</summary>
internal sealed record RecordingNativeAudioFormat(int SampleRate, int Channels, int Bits,
    int ValidBits, bool IsFloat, uint ChannelMask)
{
    internal static RecordingNativeAudioFormat Read(IntPtr format)
    {
        int tag = (ushort)Marshal.ReadInt16(format), channels = (ushort)Marshal.ReadInt16(format, 2);
        int rate = Marshal.ReadInt32(format, 4), align = (ushort)Marshal.ReadInt16(format, 12);
        int bits = (ushort)Marshal.ReadInt16(format, 14), validBits = bits;
        uint mask = channels == 1 ? 4u : channels == 2 ? 3u : 0;
        if (tag == 0xfffe)
        {
            if ((ushort)Marshal.ReadInt16(format, 16) < 22) throw new NotSupportedException("Incomplete extensible audio format.");
            validBits = (ushort)Marshal.ReadInt16(format, 18);
            mask = (uint)Marshal.ReadInt32(format, 20);
            Guid subtype = Marshal.PtrToStructure<Guid>(format + 24);
            tag = subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3
                : subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1 : -1;
        }
        if (rate is < 8_000 or > 192_000 || channels is < 1 or > 8 || align != channels * bits / 8
            || validBits <= 0 || validBits > bits || !(tag == 3 && bits == 32 && validBits == 32
                || tag == 1 && bits is 16 or 24 or 32))
            throw new NotSupportedException("Unsupported native audio format.");
        if (channels > 2 && ((mask & ~0x63fu) != 0 || System.Numerics.BitOperations.PopCount(mask) != channels))
            throw new NotSupportedException("Unsupported native speaker mask.");
        return new(rate, channels, bits, validBits, tag == 3, mask);
    }

    internal unsafe float[] Decode(IntPtr data, int frames)
    {
        if (data == IntPtr.Zero && frames > 0) throw new InvalidOperationException("A nonsilent packet has no data.");
        float[] native = new float[checked(frames * Channels)], stereo = new float[checked(frames * 2)];
        int bytes = Bits / 8;
        for (int i = 0; i < native.Length; i++)
        {
            byte* pointer = (byte*)data + i * bytes;
            // Extensible PCM valid bits are left-aligned within the sample container.
            float value = IsFloat ? *(float*)pointer : Bits switch
            {
                16 => (*(short*)pointer >> (16 - ValidBits)) / MathF.Pow(2, ValidBits - 1),
                24 => (((pointer[0] | pointer[1] << 8 | pointer[2] << 16) << 8 >> 8) >> (24 - ValidBits)) / MathF.Pow(2, ValidBits - 1),
                _ => (*(int*)pointer >> (32 - ValidBits)) / MathF.Pow(2, ValidBits - 1),
            };
            native[i] = float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }
        RecordingAudioMixer.Downmix(native, Channels, ChannelMask, stereo);
        return stereo;
    }
}

[StructLayout(LayoutKind.Sequential)] internal struct RecordingPropertyKey { public Guid Format; public uint Id; }
[ComVisible(true), Guid("7991eec9-7e89-4d85-8390-6c703cec60c0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingEndpointNotification
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, RecordingPropertyKey key);
}
[ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IRecordingDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IRecordingDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IRecordingEndpointNotification callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IRecordingEndpointNotification callback);
}
[ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingAudioClient
{
    [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
[ComImport, Guid("c8adbd64-e71e-48a0-a4de-185c395cd317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
