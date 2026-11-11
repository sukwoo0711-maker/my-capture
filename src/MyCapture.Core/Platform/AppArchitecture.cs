using System.Runtime.InteropServices;

namespace MyCapture.Core.Platform;

/// <summary>Architecture of this application process, independent of the host OS.</summary>
public static class AppArchitecture
{
    public static bool IsX86 => RuntimeInformation.ProcessArchitecture == Architecture.X86;
    public static string Name => NameOf(RuntimeInformation.ProcessArchitecture);
    public static string RuntimeIdentifier => "win-" + Name;
    public static bool SupportsNeuralOcr => !IsX86;

    /// <summary>
    /// Release name of a process architecture. The x64 build emulated on an ARM64 PC is an x64
    /// process and stays x64; only the native build reports arm64.
    /// </summary>
    internal static string NameOf(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };
}
