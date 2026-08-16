using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public sealed class RequestBodyTooLargeException : Exception
    {
    }

    /// <summary>
    /// Reads an HTTP request body with a hard byte limit, including chunked requests.
    /// </summary>
    public static class ExternalRequestBodyReader
    {
        public static async Task<string> ReadAsync(
            Stream input,
            Encoding encoding,
            int maxBytes,
            CancellationToken token)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (maxBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            var buffer = new byte[8192];
            var totalBytes = 0;

            using (var content = new MemoryStream())
            {
                while (true)
                {
                    var bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytes += bytesRead;
                    if (totalBytes > maxBytes)
                    {
                        throw new RequestBodyTooLargeException();
                    }

                    await content.WriteAsync(buffer, 0, bytesRead, token);
                }

                return (encoding ?? Encoding.UTF8).GetString(content.ToArray());
            }
        }
    }
}
