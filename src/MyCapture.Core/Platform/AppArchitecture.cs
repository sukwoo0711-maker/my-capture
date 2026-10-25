using System.Runtime.InteropServices;

namespace MyCapture.Core.Platform;

/// <summary>Architecture of this application process, independent of the host OS.</summary>
public static class AppArchitecture
{
    public static bool IsX86 => RuntimeInformation.ProcessArchitecture == Architecture.X86;
    public static string Name => IsX86 ? "x86" : "x64";
    public static string RuntimeIdentifier => "win-" + Name;
    public static bool SupportsNeuralOcr => !IsX86;
}
