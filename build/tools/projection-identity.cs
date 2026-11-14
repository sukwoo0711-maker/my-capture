#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property ImportDirectoryBuildProps=false
#:property ImportDirectoryBuildTargets=false
#:property NuGetAudit=false
#:property RestoreSources=$(MSBuildThisFileDirectory)
#:property UseSharedCompilation=false
#:property TreatWarningsAsErrors=true
#:property PublishAot=false
// A file-based app imports Directory.Build.props before these properties apply, so the repository's
// x64 PlatformTarget default reached this tool: on the ARM64 runner its x64 apphost could not load
// the arm64 hostfxr (0x800700C1). Build it architecture-neutral and run it through the muxer.
#:property PlatformTarget=AnyCPU
#:property UseAppHost=false

// Read CLI metadata directly. Loading both assemblies into an AppDomain would reject
// their identical assembly identity even when their module identities are different.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

try
{
    if (args.Length != 2) throw new ArgumentException("Usage: projection-identity.cs PUBLISHED_DLL ORIGINAL_DLL");
    Guid published = ReadModuleIdentity(args[0]);
    Guid original = ReadModuleIdentity(args[1]);
    bool verify = published != original;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        RequiresVerification = verify,
        State = verify ? "trimmed-or-modified" : "original-sdk",
        Reason = "Published module identity compared with the original SDK module using PE metadata.",
        PublishedMvid = published.ToString("D"),
        OriginalMvid = original.ToString("D"),
    }));
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, RequiresVerification = true, State = "unknown",
        Reason = error.Message, PublishedMvid = "", OriginalMvid = "",
    }));
    return 2;
}

static Guid ReadModuleIdentity(string path)
{
    using FileStream file = File.OpenRead(Path.GetFullPath(path));
    using var image = new PEReader(file);
    if (!image.HasMetadata) throw new BadImageFormatException("The projection has no CLI metadata.");
    MetadataReader metadata = image.GetMetadataReader();
    Guid identity = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    if (identity == Guid.Empty) throw new BadImageFormatException("The projection has no module identity.");
    return identity;
}
