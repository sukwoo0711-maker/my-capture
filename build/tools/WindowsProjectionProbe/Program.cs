using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// This companion is never compiled into or copied to MyCapture. It loads the exact
// projection bytes in PublishRoot. It does not load the app, show a window, contact
// the Store, inspect package data, change startup state, or read the screen/clipboard.
internal static class Program
{
    private const string ProjectionName = "Microsoft.Windows.SDK.NET";
    private const string RuntimeName = "WinRT.Runtime";

    private static readonly string[] ProductTypes =
    [
        "Windows.ApplicationModel.Activation.ActivationKind",
        "Windows.ApplicationModel.Activation.IActivatedEventArgs",
        "Windows.ApplicationModel.AppInstance", "Windows.ApplicationModel.StartupTask",
        "Windows.ApplicationModel.StartupTaskState",
        "Windows.Foundation.IAsyncOperation`1", "Windows.Foundation.IAsyncOperationWithProgress`2",
        "Windows.Foundation.AsyncOperationProgressHandler`2", "Windows.Foundation.TypedEventHandler`2",
        "Windows.Foundation.IMemoryBufferReference", "Windows.Foundation.Rect",
        "Windows.Foundation.Metadata.ApiInformation", "Windows.Globalization.Language",
        "Windows.Graphics.SizeInt32", "Windows.Graphics.DirectX.DirectXPixelFormat",
        "Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice",
        "Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface",
        "Windows.Graphics.Capture.Direct3D11CaptureFrame", "Windows.Graphics.Capture.Direct3D11CaptureFramePool",
        "Windows.Graphics.Capture.GraphicsCaptureAccess", "Windows.Graphics.Capture.GraphicsCaptureAccessKind",
        "Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.Capture.GraphicsCaptureSession",
        "Windows.Graphics.Imaging.BitmapAlphaMode", "Windows.Graphics.Imaging.BitmapBuffer",
        "Windows.Graphics.Imaging.BitmapBufferAccessMode", "Windows.Graphics.Imaging.BitmapDecoder",
        "Windows.Graphics.Imaging.BitmapPixelFormat", "Windows.Graphics.Imaging.BitmapPlaneDescription",
        "Windows.Graphics.Imaging.SoftwareBitmap", "Windows.Media.Ocr.OcrEngine",
        "Windows.Media.Ocr.OcrLine", "Windows.Media.Ocr.OcrResult", "Windows.Media.Ocr.OcrWord",
        "Windows.Networking.Connectivity.ConnectionCost", "Windows.Networking.Connectivity.ConnectionProfile",
        "Windows.Networking.Connectivity.NetworkCostType", "Windows.Networking.Connectivity.NetworkInformation",
        "Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus",
        "Windows.Services.Store.StoreContext", "Windows.Services.Store.StorePackageUpdate",
        "Windows.Services.Store.StorePackageUpdateResult", "Windows.Services.Store.StorePackageUpdateState",
        "Windows.Services.Store.StorePackageUpdateStatus", "Windows.Services.Store.StoreRateAndReviewResult",
        "Windows.Services.Store.StoreRateAndReviewStatus", "Windows.Storage.ApplicationData",
        "Windows.Storage.StorageFolder", "Windows.Storage.Streams.IBuffer",
        "Windows.Storage.Streams.InMemoryRandomAccessStream", "Windows.Storage.Streams.IRandomAccessStream",
        "Windows.System.Power.EnergySaverStatus", "Windows.System.Power.PowerManager",
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: WindowsProjectionProbe PUBLISH_ROOT OUTPUT_ROOT");
            return 2;
        }
        string publish = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
        string output = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
        if (output.Equals(publish, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(publish + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("OUTPUT_ROOT must be outside PUBLISH_ROOT.");
            return 2;
        }
        Directory.CreateDirectory(output);
        var report = new StringBuilder();
        var evidence = new List<object>();
        int checks = 0;
        void Pass(string name) { checks++; report.AppendLine($"PASS: {name}"); }
        int exit = 0;
        try
        {
            report.AppendLine("MyCapture Windows projection companion probe");
            report.AppendLine($"PublishRoot: {publish}");
            report.AppendLine("Coverage: metadata/generic ABI initialization plus read-only local power/network APIs.");
            report.AppendLine($"Runtime: .NET companion {RuntimeInformation.ProcessArchitecture}; the eight app self-tests validate the published app.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This probe requires Windows.");

            foreach (string name in new[] { RuntimeName, ProjectionName })
            {
                string path = Path.Combine(publish, name + ".dll");
                if (!File.Exists(path)) throw new FileNotFoundException("Published WinRT assembly missing.", path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Published WinRT assembly cannot be a reparse point: " + path);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                if (!string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal)
                    || !string.Equals(Path.GetFullPath(assembly.Location), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different WinRT assembly was loaded: " + assembly.Location);
                evidence.Add(new { Name = name, Path = path, Bytes = new FileInfo(path).Length, Sha256 = hash });
                Pass($"loaded exact publish assembly {name}; SHA256={hash}");
            }
            Assembly projection = Assembly.Load(ProjectionName);
            Assembly runtime = Assembly.Load(RuntimeName);
            Type Find(string name) => projection.GetType(name, false) ?? runtime.GetType(name, true)!;
            foreach (string name in ProductTypes)
            {
                Type type = Find(name);
                _ = type.GetInterfaces();
                _ = type.GetCustomAttributesData();
                Pass("product boundary type " + name);
            }

            // These operations are metadata-only; the APIs that could show consent, change
            // autostart or install an update are never invoked by this probe.
            (string Type, string Member)[] requiredMembers =
            [
                ("Windows.Services.Store.StoreContext", "GetDefault"),
                ("Windows.Services.Store.StoreContext", "GetAppAndOptionalStorePackageUpdatesAsync"),
                ("Windows.Services.Store.StoreContext", "RequestDownloadAndInstallStorePackageUpdatesAsync"),
                ("Windows.Services.Store.StoreContext", "RequestRateAndReviewAppAsync"),
                ("Windows.ApplicationModel.StartupTask", "GetAsync"),
                ("Windows.ApplicationModel.StartupTask", "RequestEnableAsync"),
                ("Windows.ApplicationModel.StartupTask", "Disable"),
                ("Windows.ApplicationModel.AppInstance", "GetActivatedEventArgs"),
                ("Windows.Storage.ApplicationData", "Current"),
                ("Windows.Storage.StorageFolder", "Path"),
                ("Windows.Graphics.Capture.GraphicsCaptureItem", "FromAbi"),
                ("Windows.Graphics.Capture.GraphicsCaptureItem", "Closed"),
                ("Windows.Graphics.Capture.Direct3D11CaptureFramePool", "FrameArrived"),
            ];
            foreach (var member in requiredMembers)
            {
                Type type = Find(member.Type);
                if (type.GetMember(member.Member, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Length == 0)
                    throw new MissingMemberException(member.Type, member.Member);
                Pass($"boundary member {member.Type}.{member.Member}");
            }

            Type Async(string name) => Find("Windows.Foundation.IAsyncOperation`1").MakeGenericType(Find(name));
            Type storeList = typeof(IReadOnlyList<>).MakeGenericType(Find("Windows.Services.Store.StorePackageUpdate"));
            Type[] contracts =
            [
                Async("Windows.ApplicationModel.StartupTask"), Async("Windows.ApplicationModel.StartupTaskState"),
                Async("Windows.Media.Ocr.OcrResult"), Async("Windows.Graphics.Imaging.BitmapDecoder"),
                Async("Windows.Graphics.Imaging.SoftwareBitmap"),
                Async("Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus"),
                Async("Windows.Services.Store.StoreRateAndReviewResult"),
                Find("Windows.Foundation.IAsyncOperation`1").MakeGenericType(storeList),
                Find("Windows.Foundation.IAsyncOperationWithProgress`2").MakeGenericType(
                    Find("Windows.Services.Store.StorePackageUpdateResult"), Find("Windows.Services.Store.StorePackageUpdateStatus")),
                Find("Windows.Foundation.IAsyncOperationWithProgress`2").MakeGenericType(typeof(uint), typeof(uint)),
                Find("Windows.Foundation.AsyncOperationProgressHandler`2").MakeGenericType(
                    Find("Windows.Services.Store.StorePackageUpdateResult"), Find("Windows.Services.Store.StorePackageUpdateStatus")),
                Find("Windows.Foundation.TypedEventHandler`2").MakeGenericType(Find("Windows.Graphics.Capture.Direct3D11CaptureFramePool"), typeof(object)),
                Find("Windows.Foundation.TypedEventHandler`2").MakeGenericType(Find("Windows.Graphics.Capture.GraphicsCaptureItem"), typeof(object)),
                storeList,
                typeof(IReadOnlyList<>).MakeGenericType(Find("Windows.Globalization.Language")),
                typeof(IReadOnlyList<>).MakeGenericType(Find("Windows.Media.Ocr.OcrLine")),
                typeof(IReadOnlyList<>).MakeGenericType(Find("Windows.Media.Ocr.OcrWord")),
                Find("Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice"),
                Find("Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface"),
                Find("Windows.Foundation.IMemoryBufferReference"),
            ];
            Type marshaler = runtime.GetType("WinRT.Marshaler`1", true)!;
            foreach (Type contract in contracts)
            {
                Type closed = marshaler.MakeGenericType(contract);
                RuntimeHelpers.RunClassConstructor(closed.TypeHandle);
                FieldInfo abi = closed.GetField("AbiType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingFieldException(closed.FullName, "AbiType");
                if (abi.GetValue(null) is not Type) throw new InvalidOperationException("WinRT ABI type was not initialized: " + contract);
                Pass("generic ABI initialized " + contract);
            }

            object language = Activator.CreateInstance(Find("Windows.Globalization.Language"), "en-US")!;
            if (Find("Windows.Globalization.Language").GetProperty("LanguageTag")!.GetValue(language) is not string tag
                || !tag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Language projection did not round-trip en-US.");
            Pass("globalization Language constructor/property");
            VerifySyntheticImaging(Find, projection, runtime, Pass);

            object? power = Find("Windows.System.Power.PowerManager").GetProperty("EnergySaverStatus")!.GetValue(null);
            Pass("local power status: " + power);
            object? profile = Find("Windows.Networking.Connectivity.NetworkInformation")
                .GetMethod("GetInternetConnectionProfile", Type.EmptyTypes)!.Invoke(null, null);
            object? cost = profile is null ? null : Find("Windows.Networking.Connectivity.ConnectionProfile")
                .GetMethod("GetConnectionCost", Type.EmptyTypes)!.Invoke(profile, null);
            object? costKind = cost is null ? null : Find("Windows.Networking.Connectivity.ConnectionCost")
                .GetProperty("NetworkCostType")!.GetValue(cost);
            Pass("local connection cost: " + (costKind?.ToString() ?? "no profile"));
            report.AppendLine("NOT EXERCISED: Store activation, update/rating responses, package folders/activation and startup-task OS calls require an isolated MSIX test identity.");
            report.AppendLine("NOT EXERCISED: COM graphics device/surface round-trip, frame events and OCR engine recognition; run the original OCR and recording app self-tests on the desktop.");
            report.AppendLine("RESULT: PASS");
        }
        catch (Exception ex)
        {
            exit = 1;
            report.AppendLine("RESULT: FAIL");
            report.AppendLine(ex.ToString());
        }
        File.WriteAllText(Path.Combine(output, "projection-probe-report.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "projection-probe-result.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, PublishRoot = publish, Result = exit == 0 ? "PASS" : "FAIL", ExitCode = exit,
            ChecksPassed = checks, Runtime = ".NET companion; published runtime requires original app tests",
            Assemblies = evidence,
            NotExercised = new[] { "Store/package/startup OS calls", "graphics device/frame events", "OCR recognition" },
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.Write(report);
        return exit;
    }

    private static void VerifySyntheticImaging(Func<string, Type> find, Assembly projection, Assembly runtime, Action<string> pass)
    {
        Type softwareType = find("Windows.Graphics.Imaging.SoftwareBitmap");
        object format = Enum.Parse(find("Windows.Graphics.Imaging.BitmapPixelFormat"), "Bgra8");
        object alpha = Enum.Parse(find("Windows.Graphics.Imaging.BitmapAlphaMode"), "Premultiplied");
        using (var stream = (IDisposable)Activator.CreateInstance(find("Windows.Storage.Streams.InMemoryRandomAccessStream"))!)
        {
            byte[] png = CreateSyntheticPng();
            Type extensions = projection.GetType("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions", true)!;
            MethodInfo asBuffer = extensions.GetMethods().Single(method => method.Name == "AsBuffer"
                && method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(byte[]));
            object buffer = asBuffer.Invoke(null, [png])!;
            object write = stream.GetType().GetMethod("WriteAsync")!.Invoke(stream, [buffer])!;
            object? written = CompleteAsync(projection, write, [typeof(uint), typeof(uint)]);
            if (!Equals(written, (uint)png.Length)) throw new IOException("WinRT stream did not write the complete PNG.");
            stream.GetType().GetMethod("Seek")!.Invoke(stream, [0UL]);
            Type decoderType = find("Windows.Graphics.Imaging.BitmapDecoder");
            MethodInfo create = decoderType.GetMethods().Single(method => method.Name == "CreateAsync" && method.GetParameters().Length == 1);
            object decoder = CompleteAsync(projection, create.Invoke(null, [stream])!, [decoderType])!;
            MethodInfo decode = decoderType.GetMethods().Single(method => method.Name == "GetSoftwareBitmapAsync" && method.GetParameters().Length == 2);
            using var bitmap = (IDisposable)CompleteAsync(projection, decode.Invoke(decoder, [format, alpha])!, [softwareType])!;
            if (!Equals(softwareType.GetProperty("PixelWidth")!.GetValue(bitmap), 1)
                || !Equals(softwareType.GetProperty("PixelHeight")!.GetValue(bitmap), 1))
                throw new InvalidOperationException("WinRT bitmap decoding did not produce the synthetic 1x1 image.");
            pass("synthetic PNG: AsBuffer/WriteAsync/Seek/BitmapDecoder/GetSoftwareBitmapAsync (1x1)");
        }

        using var direct = (IDisposable)Activator.CreateInstance(softwareType, format, 2, 2, alpha)!;
        object mode = Enum.Parse(find("Windows.Graphics.Imaging.BitmapBufferAccessMode"), "Write");
        using var locked = (IDisposable)softwareType.GetMethod("LockBuffer")!.Invoke(direct, [mode])!;
        using var reference = (IDisposable)locked.GetType().GetMethod("CreateReference")!.Invoke(locked, null)!;
        object native = runtime.GetType("WinRT.IWinRTObject", true)!.GetProperty("NativeObject")!.GetValue(reference)!;
        nint unknown = (nint)native.GetType().GetProperty("ThisPtr")!.GetValue(native)!;
        Guid iid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint access));
        try
        {
            nint vtable = Marshal.ReadIntPtr(access);
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBuffer>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getBuffer(access, out nint pixels, out uint capacity));
            object plane = locked.GetType().GetMethod("GetPlaneDescription")!.Invoke(locked, [0])!;
            int Read(string name) => (int)plane.GetType().GetField(name)!.GetValue(plane)!;
            int start = Read("StartIndex"), stride = Read("Stride");
            if (pixels == 0 || start < 0 || stride < 8 || (long)start + stride + 8 > capacity)
                throw new InvalidOperationException("Native bitmap memory bounds are invalid.");
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 8; x++)
                {
                    int offset = checked(start + y * stride + x);
                    byte sample = (byte)(17 + y * 8 + x);
                    Marshal.WriteByte(pixels, offset, sample);
                    if (Marshal.ReadByte(pixels, offset) != sample)
                        throw new InvalidOperationException("Native bitmap pixel round-trip failed.");
                }
            pass("direct bitmap: LockBuffer/CreateReference/IWinRTObject/IMemoryBufferByteAccess synthetic pixels");
        }
        finally { Marshal.Release(access); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuffer(nint instance, out nint pixels, out uint capacity);

    private static object? CompleteAsync(Assembly projection, object operation, Type[] arguments)
    {
        Type extensions = projection.GetType("System.WindowsRuntimeSystemExtensions", true)!;
        MethodInfo method = extensions.GetMethods().Single(candidate => candidate.Name == "AsTask"
            && candidate.IsGenericMethodDefinition && candidate.GetGenericArguments().Length == arguments.Length
            && candidate.GetParameters() is [{ ParameterType: var parameter }]
            && parameter.GetGenericTypeDefinition().FullName == (arguments.Length == 1
                ? "Windows.Foundation.IAsyncOperation`1" : "Windows.Foundation.IAsyncOperationWithProgress`2"));
        var task = (Task)method.MakeGenericMethod(arguments).Invoke(null, [operation])!;
        task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static byte[] CreateSyntheticPng()
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Number(uint value) { png.WriteByte((byte)(value >> 24)); png.WriteByte((byte)(value >> 16)); png.WriteByte((byte)(value >> 8)); png.WriteByte((byte)value); }
        void Chunk(string name, byte[] data)
        {
            byte[] type = Encoding.ASCII.GetBytes(name);
            Number((uint)data.Length); png.Write(type); png.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0);
            }
            Number(~crc);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write([0, 70, 120, 190, 255]);
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return png.ToArray();
    }
}
