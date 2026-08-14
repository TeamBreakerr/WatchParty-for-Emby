using System;
using System.IO;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public static class StrmFileSynchronizer
    {
        public static async Task<bool> WriteIfChangedAsync(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A STRM file path is required.", nameof(path));
            }

            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            if (File.Exists(path))
            {
                var existingContents = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                if (string.Equals(existingContents, contents, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            await File.WriteAllTextAsync(path, contents).ConfigureAwait(false);
            return true;
        }
    }
}
