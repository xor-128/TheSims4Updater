using System.Text;

namespace TheSims4Updater;

public class CrcChecker
{
    private readonly byte[] _zipBytes;
    public CrcChecker(string base64String)
    {
        _zipBytes = Convert.FromBase64String(base64String);
    }
    public List<(string FileName, uint ExpectedCrc)> ExtractFilesCrcList()
    {
        var allItems = FindOccurrences(_zipBytes, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 });
        var filesCrcList = new List<(string FileName, uint ExpectedCrc)>();
        foreach (var itemOffset in allItems)
        {
            uint crc32 = BitConverter.ToUInt32(_zipBytes, itemOffset - 0x10);
            string fileName = GetStringUntilByte(_zipBytes, itemOffset + 0x0e, 0x1F);
            filesCrcList.Add((fileName, crc32));
        }
        return filesCrcList;
    }
    public static async Task<bool> CheckFilesCrcAsync((string FileName, uint ExpectedCrc)[] filesWithCrc)
    {
        var cts = new CancellationTokenSource();
        var tasks = new List<Task<bool>>();
        foreach (var (fileName, expectedCrc) in filesWithCrc)
        {
            if (cts.Token.IsCancellationRequested)
                break;
            tasks.Add(Task.Run(() =>
            {
                bool result = CrcCache.CheckFileCrc(fileName, expectedCrc, cts.Token);
                if (!result)
                    cts.Cancel(); // Cancel remaining tasks if a mismatch is found
                return result;
            }, cts.Token));
        }
        try
        {
            var results = await Task.WhenAll(tasks);
            return results.All(result => result);
        }
        catch (OperationCanceledException)
        {
            return false; // Return false if any task was canceled
        }
    }
    private static List<int> FindOccurrences(byte[] data, byte[] pattern)
    {
        List<int> offsets = new List<int>();
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                offsets.Add(i);
            }
        }
        return offsets;
    }
    private static string GetStringUntilByte(byte[] data, int startOffset, byte stopByte)
    {
        int endOffset = startOffset;
        while (endOffset < data.Length)
        {
            if (data[endOffset] < stopByte)
            {
                break;
            }
            endOffset++;
        }
        byte[] subArray = new byte[endOffset - startOffset];
        Array.Copy(data, startOffset, subArray, 0, endOffset - startOffset);
        return Encoding.UTF8.GetString(subArray);
    }
}