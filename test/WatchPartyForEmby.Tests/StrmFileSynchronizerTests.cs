using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class StrmFileSynchronizerTests : IDisposable
    {
        private readonly string _testDirectory;

        public StrmFileSynchronizerTests()
        {
            _testDirectory = Path.Combine(
                Path.GetTempPath(),
                "watch-party-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);
        }

        [Fact]
        public async Task LeavesAnUnchangedStrmFileUntouched()
        {
            var path = Path.Combine(_testDirectory, "episode.strm");
            const string contents = "https://example.test/episode.mkv\n";
            var originalWriteTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await File.WriteAllTextAsync(path, contents);
            File.SetLastWriteTimeUtc(path, originalWriteTime);

            var changed = await StrmFileSynchronizer.WriteIfChangedAsync(path, contents);

            Assert.False(changed);
            Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
        }

        [Fact]
        public async Task CreatesANewStrmFile()
        {
            var path = Path.Combine(_testDirectory, "new-episode.strm");
            const string contents = "https://example.test/new-episode.mkv\n";

            var changed = await StrmFileSynchronizer.WriteIfChangedAsync(path, contents);

            Assert.True(changed);
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }

        [Fact]
        public async Task ReplacesAnExistingStrmFileWhenItsContentsChange()
        {
            var path = Path.Combine(_testDirectory, "changed-episode.strm");
            const string oldContents = "https://example.test/old-episode.mkv\n";
            const string newContents = "https://example.test/new-episode.mkv\n";
            await File.WriteAllTextAsync(path, oldContents);

            var changed = await StrmFileSynchronizer.WriteIfChangedAsync(path, newContents);

            Assert.True(changed);
            Assert.Equal(newContents, await File.ReadAllTextAsync(path));
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
    }
}
