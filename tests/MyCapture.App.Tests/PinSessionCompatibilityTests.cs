using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSessionCompatibilityTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Fact]
    public void StoreLoadsSessionWithoutCollapseFieldsAsExpanded()
    {
        string root = OwnedTestDirectory.Create("mycapture-pin-session-compat-");
        try
        {
            PinSessionSnapshot original = SaveCollapsedSession(root);
            JsonObject manifest = ReadManifest(root);
            Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
            JsonObject pin = Assert.Single(manifest["pins"]!.AsArray())!.AsObject();
            Assert.True(pin.Remove("isCollapsed"));
            Assert.True(pin.Remove("collapsedOpacity"));
            WriteManifest(root, manifest);

            PinSessionLoadResult loaded = new PinSessionStore(root, NullLogger.Instance).LoadDetailed();

            Assert.False(loaded.ManifestUnreadable);
            PinSessionSnapshot restored = Assert.Single(loaded.Pins);
            Assert.False(restored.Pin.IsCollapsed);
            Assert.Null(restored.Pin.CollapsedOpacity);
            Assert.Equal(original.Pin with { IsCollapsed = false, CollapsedOpacity = null }, restored.Pin);
            Assert.Equal(Png, restored.PngBytes);
            Assert.False(restored.ProtectionPending);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void StoreLoadsSessionWithUnknownManifestAndPinFields()
    {
        string root = OwnedTestDirectory.Create("mycapture-pin-session-compat-");
        try
        {
            PinSessionSnapshot original = SaveCollapsedSession(root);
            JsonObject manifest = ReadManifest(root);
            Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
            manifest["futureSessionMetadata"] = new JsonObject { ["value"] = "unknown to this reader" };
            JsonObject pin = Assert.Single(manifest["pins"]!.AsArray())!.AsObject();
            // Exercise the real store's handling of the same kind of additive record field
            // that an older reader encounters when this build writes collapse metadata.
            pin["futurePinMetadata"] = new JsonObject { ["enabled"] = true };
            WriteManifest(root, manifest);

            PinSessionLoadResult loaded = new PinSessionStore(root, NullLogger.Instance).LoadDetailed();

            Assert.False(loaded.ManifestUnreadable);
            PinSessionSnapshot restored = Assert.Single(loaded.Pins);
            Assert.Equal(original.Pin, restored.Pin);
            Assert.Equal(Png, restored.PngBytes);
            Assert.False(restored.ProtectionPending);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static PinSessionSnapshot SaveCollapsedSession(string root)
    {
        var snapshot = new PinSessionSnapshot(new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Image,
            null, "", false, 800, 600, 1.75, .7, null, IsCollapsed: true, CollapsedOpacity: .3), Png);
        PinSessionSaveResult saved = new PinSessionStore(root, NullLogger.Instance).SaveDetailed([snapshot]);
        Assert.True(saved.IsComplete);
        Assert.Equal(snapshot.Pin.Id, Assert.Single(saved.SavedPinIds));
        return snapshot;
    }

    private static JsonObject ReadManifest(string root)
    {
        byte[] plaintext = PinSessionProtection.Read(File.ReadAllBytes(Path.Combine(root, "session.json")),
            PinSessionFilePurpose.Manifest, out bool isProtected);
        Assert.True(isProtected);
        return JsonNode.Parse(plaintext)!.AsObject();
    }

    private static void WriteManifest(string root, JsonObject manifest) =>
        File.WriteAllBytes(Path.Combine(root, "session.json"), PinSessionProtection.Protect(
            Encoding.UTF8.GetBytes(manifest.ToJsonString()), PinSessionFilePurpose.Manifest));
}
