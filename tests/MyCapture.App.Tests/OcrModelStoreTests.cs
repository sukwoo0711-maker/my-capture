using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrModelStoreTests
{
    [Fact]
    public async Task Ensure_WritesVerifiedPayloadAndSkipsWhenPresent()
    {
        string root = OwnedTestDirectory.Create("ocr-models-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(payload));
            var file = new OcrModelFile("unit.bin", "https://example.test/unit.bin", hash, 16);
            var handler = new ScriptedHandler { Body = payload };
            using var http = new HttpClient(handler);
            var store = new OcrModelStore(paths, http, NullLogger.Instance);

            Assert.False(store.IsPresent(file));
            await store.EnsureFileAsync(file, CancellationToken.None);
            Assert.True(store.IsPresent(file));
            Assert.Equal(1, handler.Calls);
            await store.EnsureFileAsync(file, CancellationToken.None);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Ensure_RejectsHashMismatch()
    {
        string root = OwnedTestDirectory.Create("ocr-models-bad-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var file = new OcrModelFile(
                "bad.bin",
                "https://example.test/bad.bin",
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                4);
            using var http = new HttpClient(new ScriptedHandler { Body = [1, 2, 3, 4, 5, 6, 7, 8] });
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            await Assert.ThrowsAsync<CryptographicException>(() => store.EnsureFileAsync(file, CancellationToken.None));
            Assert.False(store.IsPresent(file));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
            });
        }
    }
}
