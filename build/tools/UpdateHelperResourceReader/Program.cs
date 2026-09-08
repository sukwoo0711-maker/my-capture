using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// The acceptance harness sets the verified publish directory as the working directory.
// Read metadata only: never load or execute the candidate assembly in the tool process.
using var input = File.OpenRead("MyCapture.dll");
using var pe = new PEReader(input);
MetadataReader metadata = pe.GetMetadataReader();
var matches = metadata.ManifestResources.Select(metadata.GetManifestResource)
    .Where(resource => metadata.GetString(resource.Name) == "MyCapture.UpdateHelper.ps1").ToArray();
if (matches.Length != 1 || !matches[0].Implementation.IsNil || pe.PEHeaders.CorHeader is null)
    throw new InvalidDataException("Expected exactly one embedded updater helper.");
int address = checked(pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress + (int)matches[0].Offset);
BlobReader reader = pe.GetSectionData(address).GetReader();
int length = reader.ReadInt32();
if (length <= 0 || length > 1024 * 1024 || length > reader.RemainingBytes)
    throw new InvalidDataException("Embedded updater helper has an invalid length.");
Console.Write(Convert.ToBase64String(reader.ReadBytes(length)));
