# Writes native minidumps for the media cold-open probe (diagnostic only).
#
# The probe starts this script before it opens any media and waits for "ready", so compiling
# the helper below costs nothing during the open being observed. Each stdin line
# "<pid>|<dump path>" writes one dump; each result is one stdout line,
# "done|<path>|<ms>" or "failed|<path>|<message>". The script exits at end of input.
#
# The dumps hold thread stacks, thread and handle data and the memory map, not the heaps, so
# they stay small and still show where every native thread waits.

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class MediaProbeDump
{
    // MiniDumpWithHandleData | WithUnloadedModules | WithProcessThreadData | WithFullMemoryInfo
    // | WithThreadInfo | IgnoreInaccessibleMemory
    private const uint DumpType = 0x00000004 | 0x00000020 | 0x00000100 | 0x00000800 | 0x00001000 | 0x00020000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint type,
        IntPtr exception, IntPtr userStream, IntPtr callback);

    public static long Write(int processId, string path)
    {
        var clock = Stopwatch.StartNew();
        using (Process process = Process.GetProcessById(processId))
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (!MiniDumpWriteDump(process.Handle, (uint)processId, file.SafeFileHandle, DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        return clock.ElapsedMilliseconds;
    }
}
'@

[Console]::Out.WriteLine('ready')
[Console]::Out.Flush()

while ($null -ne ($line = [Console]::In.ReadLine())) {
    $parts = $line.Split('|', 2)
    if ($parts.Count -ne 2) { continue }
    $targetPath = $parts[1]
    try {
        $ms = [MediaProbeDump]::Write([int]$parts[0], $targetPath)
        [Console]::Out.WriteLine("done|$targetPath|$ms ms")
    }
    catch {
        $message = $_.Exception.Message -replace '[\r\n|]', ' '
        [Console]::Out.WriteLine("failed|$targetPath|$message")
    }

    [Console]::Out.Flush()
}
