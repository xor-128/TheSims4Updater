using System.Buffers;
using System.Collections.Concurrent;
using Force.Crc32;

namespace TheSims4Updater;

static class CrcCache
{
    private const string CacheFileName = "crc_caches.txt";
    private static readonly ConcurrentDictionary<string, (uint Crc, long LastModified)> Cache = new();
    private static readonly object FileLock = new();
    static CrcCache()
    {
        LoadCacheFromFile();
    }

    static uint ComputeCrc32(string filePath, CancellationToken token)
    {
        const int bufferSize = 1048576; // Use a larger buffer size (64 KB)
        using var stream = File.OpenRead(filePath);
        var crc32 = new Crc32Algorithm();
        // Use ArrayPool to minimize allocations
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (token.IsCancellationRequested)
                    return 0;

                crc32.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            crc32.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            // Directly return the CRC without reversing
            byte[] hash = crc32.Hash;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(hash);

            return BitConverter.ToUInt32(hash, 0);
        }
        finally
        {
            // Return the buffer to the pool
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static bool CheckFileCrc(string fileName, uint expectedCrc, CancellationToken token)
    {
        if (!File.Exists(fileName))
        {
            return false;
        }
        try
        {
            // Get the last modification date of the file
            long lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(fileName).ToUniversalTime()).ToUnixTimeMilliseconds();
            // Check if the file is in the cache and if the modification date matches
            if (Cache.TryGetValue(fileName, out var cachedEntry) && cachedEntry.LastModified == lastModified)
            {
                // Use cached CRC value
                uint cachedCrc = cachedEntry.Crc;
                bool isMatch = cachedCrc == expectedCrc;
                if (!isMatch)
                    Console.WriteLine($"File {fileName}: CRC mismatch (expected {expectedCrc:X8}, got {cachedCrc:X8}).");
                return isMatch;
            }
            // Recalculate CRC if not in cache or modification date changed
            uint computedCrc = ComputeCrc32(fileName, token);

            if (token.IsCancellationRequested)
                return false;

            // Update the cache with the new CRC and modification date
            Cache[fileName] = (computedCrc, lastModified);
            SaveCacheToFile();
            // Compare with expected CRC
            bool isMatchRecalculated = computedCrc == expectedCrc;
            if (!isMatchRecalculated)
                Console.WriteLine($"File {fileName}: CRC mismatch (expected {expectedCrc:X8}, got {computedCrc:X8}).");
            return isMatchRecalculated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing file {fileName}: {ex.Message}");
            return false;
        }
    }
    private static void LoadCacheFromFile()
    {
        lock (FileLock)
        {
            if (!File.Exists(CacheFileName))
                return;
            try
            {
                foreach (var line in File.ReadAllLines(CacheFileName))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 3 &&
                        long.TryParse(parts[1], out var lastModified) &&
                        uint.TryParse(parts[2], out var crc))
                    {
                        Cache[parts[0]] = (crc, lastModified);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading cache file: {ex.Message}");
            }
        }
    }
    private static void SaveCacheToFile()
    {
        lock (FileLock)
        {
            try
            {
                using var writer = new StreamWriter(CacheFileName, false);
                foreach (var entry in Cache)
                {
                    writer.WriteLine($"{entry.Key}|{entry.Value.LastModified}|{entry.Value.Crc}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving cache file: {ex.Message}");
            }
        }
    }
}