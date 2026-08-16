using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ExternalRequestBodyReaderTests
    {
        [Fact]
        public async Task StreamOverByteLimitIsRejectedWithoutRelyingOnContentLength()
        {
            using (var stream = new MemoryStream(new byte[1025]))
            {
                await Assert.ThrowsAsync<RequestBodyTooLargeException>(() =>
                    ExternalRequestBodyReader.ReadAsync(
                        stream,
                        Encoding.UTF8,
                        1024,
                        CancellationToken.None));
            }
        }

        [Fact]
        public async Task StreamAtExactByteLimitIsAccepted()
        {
            var bytes = Encoding.UTF8.GetBytes("hello");
            using (var stream = new MemoryStream(bytes))
            {
                var body = await ExternalRequestBodyReader.ReadAsync(
                    stream,
                    Encoding.UTF8,
                    bytes.Length,
                    CancellationToken.None);

                Assert.Equal("hello", body);
            }
        }

        [Fact]
        public async Task LimitIsMeasuredInBytesRatherThanDecodedCharacters()
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("你")))
            {
                await Assert.ThrowsAsync<RequestBodyTooLargeException>(() =>
                    ExternalRequestBodyReader.ReadAsync(
                        stream,
                        Encoding.UTF8,
                        2,
                        CancellationToken.None));
            }
        }
    }
}
