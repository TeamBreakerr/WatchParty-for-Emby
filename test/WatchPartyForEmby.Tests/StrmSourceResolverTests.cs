using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class StrmSourceResolverTests : IDisposable
    {
        private readonly string _testDirectory;

        public StrmSourceResolverTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "watch-party-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);
        }

        [Fact]
        public async Task ReturnsDirectMediaPathUnchanged()
        {
            const string mediaPath = "/media/movies/example.mkv";

            var result = await StrmSourceResolver.ResolveAsync(mediaPath);

            Assert.Equal(mediaPath, result);
        }

        [Fact]
        public async Task ReadsFirstPlayableSourceFromStrm()
        {
            var strmPath = CreateStrm("\uFEFF#EXTM3U\n\n  https://example.test/movie.mkv?token=abc  \n");

            var result = await StrmSourceResolver.ResolveAsync(strmPath);

            Assert.Equal("https://example.test/movie.mkv?token=abc", result);
        }

        [Fact]
        public async Task ResolvesNestedLocalStrmFiles()
        {
            var innerPath = CreateStrm("https://example.test/video.mkv\n", "inner.strm");
            var outerPath = CreateStrm(innerPath + "\n", "outer.STRM");

            var result = await StrmSourceResolver.ResolveAsync(outerPath);

            Assert.Equal("https://example.test/video.mkv", result);
        }

        [Fact]
        public async Task ResolvesRelativeNestedStrmFromContainingDirectory()
        {
            CreateStrm("https://example.test/relative-video.mkv\n", "inner.strm");
            var outerPath = CreateStrm("inner.strm\n", "outer.strm");

            var result = await StrmSourceResolver.ResolveAsync(outerPath);

            Assert.Equal("https://example.test/relative-video.mkv", result);
        }

        [Fact]
        public async Task MakesRelativeMediaPathAbsoluteToContainingStrm()
        {
            var strmPath = CreateStrm("../media/movie.mkv\n");

            var result = await StrmSourceResolver.ResolveAsync(strmPath);

            Assert.Equal(Path.GetFullPath(Path.Combine(_testDirectory, "../media/movie.mkv")), result);
        }

        [Fact]
        public async Task RejectsEmptyStrm()
        {
            var strmPath = CreateStrm("\n# only a comment\n");

            await Assert.ThrowsAsync<InvalidDataException>(() => StrmSourceResolver.ResolveAsync(strmPath));
        }

        [Fact]
        public async Task RejectsCircularStrmReferences()
        {
            var firstPath = Path.Combine(_testDirectory, "first.strm");
            var secondPath = Path.Combine(_testDirectory, "second.strm");
            await File.WriteAllTextAsync(firstPath, secondPath);
            await File.WriteAllTextAsync(secondPath, firstPath);

            await Assert.ThrowsAsync<InvalidDataException>(() => StrmSourceResolver.ResolveAsync(firstPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        private string CreateStrm(string contents, string fileName = "movie.strm")
        {
            var path = Path.Combine(_testDirectory, fileName);
            File.WriteAllText(path, contents);
            return path;
        }
    }
}
