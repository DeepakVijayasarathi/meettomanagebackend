using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Services;
using Xunit;

namespace iucs.meettomanage.tests
{
    public class RecordingModeTests
    {
        [Theory]
        [InlineData(null, "server")]
        [InlineData("{}", "server")]
        [InlineData("{\"recordingMode\":\"browser\"}", "browser")]
        [InlineData("{\"recordingMode\":\" Smart \"}", "smart")]
        [InlineData("{\"recordingMode\":\"cloud\"}", "server")]
        [InlineData("not json", "server")]
        public void RecordingModeDefaultsToTheServer(string? configJson, string expected)
        {
            Assert.Equal(expected, SessionService.ReadRecordingMode(configJson));
        }

        [Fact]
        public void ServerRecordingAddressesPassThroughUnchanged()
        {
            var storage = new FakeStorage();
            Assert.Equal("https://rec.example.com/a.mp4", RecordingPlayback.Url(storage, "https://rec.example.com/a.mp4"));
            Assert.Empty(storage.Signed);
        }

        [Fact]
        public void BrowserRecordingsAreServedThroughASignedAddress()
        {
            var storage = new FakeStorage();
            var key = "0123456789abcdef0123456789abcdef.mp4";
            Assert.Equal($"https://signed/{key}", RecordingPlayback.Url(storage, key));
            Assert.Equal(("video/mp4", TimeSpan.FromHours(6)), storage.Signed.Single());
        }

        [Fact]
        public void AnUnknownReferenceIsLeftAsRegistered()
        {
            var storage = new FakeStorage { Throws = true };
            Assert.Equal("legacy/path.mp4", RecordingPlayback.Url(storage, "legacy/path.mp4"));
            Assert.Equal("x.mp4", RecordingPlayback.Url(null, "x.mp4"));
        }

        private sealed class FakeStorage : IDirectUploadStorage
        {
            public bool Throws { get; set; }
            public List<(string? Type, TimeSpan ValidFor)> Signed { get; } = [];

            public string GetReadUrl(string key, TimeSpan validFor, string? contentType)
            {
                if (Throws) throw new InvalidOperationException("not ours");
                Signed.Add((contentType, validFor));
                return $"https://signed/{key}";
            }

            public Task<DirectUploadSession> StartMultipartAsync(string originalFileName, string? contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IReadOnlyList<PresignedPart> GetPartUrls(string key, string uploadId, IEnumerable<int> partNumbers) => throw new NotSupportedException();
            public Task<long> CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<UploadedPart> parts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<UploadedPart>> ListPartsAsync(string key, string uploadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
