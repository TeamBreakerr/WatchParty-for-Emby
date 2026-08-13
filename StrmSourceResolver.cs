using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public static class StrmSourceResolver
    {
        private const int MaxNestedStrmDepth = 8;

        public static async Task<string> ResolveAsync(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                throw new ArgumentException("The media item has no path.", nameof(itemPath));
            }

            var currentPath = itemPath.Trim();
            var visitedPaths = new HashSet<string>(GetPathComparer());

            for (var depth = 0; depth < MaxNestedStrmDepth; depth++)
            {
                if (!IsExistingLocalStrm(currentPath))
                {
                    return currentPath;
                }

                var fullPath = Path.GetFullPath(currentPath);
                if (!visitedPaths.Add(fullPath))
                {
                    throw new InvalidDataException($"Circular STRM reference detected at '{fullPath}'.");
                }

                var contents = await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false);
                var resolvedSource = contents
                    .Select(line => line.Trim().TrimStart('\uFEFF'))
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal));

                if (string.IsNullOrWhiteSpace(resolvedSource))
                {
                    throw new InvalidDataException($"STRM file '{fullPath}' does not contain a playable source.");
                }

                currentPath = ResolveRelativeSource(fullPath, resolvedSource);
            }

            throw new InvalidDataException($"STRM nesting exceeds the supported depth of {MaxNestedStrmDepth}.");
        }

        private static bool IsExistingLocalStrm(string path)
        {
            return string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path);
        }

        private static string ResolveRelativeSource(string containingStrmPath, string resolvedSource)
        {
            if (Path.IsPathRooted(resolvedSource)
                || Uri.TryCreate(resolvedSource, UriKind.Absolute, out _))
            {
                return resolvedSource;
            }

            var containingDirectory = Path.GetDirectoryName(containingStrmPath);
            return string.IsNullOrEmpty(containingDirectory)
                ? resolvedSource
                : Path.GetFullPath(Path.Combine(containingDirectory, resolvedSource));
        }

        private static StringComparer GetPathComparer()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
    }
}
