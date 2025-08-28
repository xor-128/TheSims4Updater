using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IniParser;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace TheSims4Updater;

public static class GameUpdater
{
    public class Patch
    {
        public string FromVersion { get; set; }
        public string ToVersion { get; set; }
        public List<string> Urls { get; set; }
        public long TotalSplitBytes { get; set; }
        public long TotalBytes { get; set; }
    }

    private const string MainChannelJson = "https://gist.githubusercontent.com/anadius/b6c97f1adfa05b656469eda79a6487d8/raw/master.json";
    private const string GameIniPath = @".\Game\Bin\Default.ini";
    private const string GameExePath = @".\Game\Bin\TS4_x64.exe";
    private static readonly string DownloadFolder = Environment.CurrentDirectory;
    private static readonly string TempFolder;
    private static readonly JsonElement MainDocument;
    private static readonly JsonElement MetadataDocument;
    private static readonly List<Patch> Patches;
        
    public static readonly string? CurrentGameVersion;
    public static readonly string? LatestGameVersion;
    public static readonly bool ShouldPerformFullInstall;
        
    static GameUpdater()
    {
        MainDocument = FetchJsonData(MainChannelJson);
        string metadataUrl = MainDocument.GetProperty("metadata").GetString()!;
            
        MetadataDocument = FetchJsonData(metadataUrl);
        CurrentGameVersion = GetGameVersion();
            
        var (patches, latestVersion) = ExtractPatches();
            
        Patches = patches;
        LatestGameVersion = latestVersion;

        ShouldPerformFullInstall = CurrentGameVersion == null;

        TempFolder = Path.Combine(DownloadFolder, "_Temp");

        if (!Directory.Exists(TempFolder))
            Directory.CreateDirectory(TempFolder);
    }

    public static async Task<bool> PerformPatches()
    {
        if (Patches.Count == 0)
        {
            Console.WriteLine($"There is no available patches, seems like it's up-to date.");
            return true;
        }

        Console.WriteLine($"Found {Patches.Count} patches to download.");

        foreach (var patch in Patches)
        {
            Console.WriteLine($"Downloading patch {patch.FromVersion} -> {patch.ToVersion}...");
                
            for (var index = 0; index < patch.Urls.Count; index++)
            {
                var url = patch.Urls[index];

                await ProcessDownload(url, DownloadFolder, patch.ToVersion, patch.TotalSplitBytes, index, patch.TotalBytes);
            }

            Console.WriteLine($"Patch {patch.ToVersion} downloaded successfully. Installing the patch...");

            try
            {
                // Open the downloaded file as a ZIP archive
                using (var archive = ArchiveFactory.Open(Path.GetFileName(patch.Urls.First())))
                {
                    // Iterate through the entries in the archive
                    foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    {
                        entry.WriteToDirectory(TempFolder, new ExtractionOptions
                        {
                            ExtractFullPath = false,
                            Overwrite = true
                        });

                        var fileName = Path.GetFileName(entry.Key);

                        if (fileName.Substring(fileName.LastIndexOf(".") + 1).StartsWith("p-"))
                        {
                            var originalFilePath = entry.Key.Substring(0, entry.Key.LastIndexOf("."));

                            var commandLine =
                                $"-d -s \"{Path.Combine(DownloadFolder, originalFilePath)}\" \"{Path.Combine(TempFolder, fileName)}\" \"{Path.Combine(DownloadFolder, originalFilePath + ".updated")}\"";
                            var xdeltaProcess = Process.Start("xdelta3.exe", commandLine);

                            await xdeltaProcess.WaitForExitAsync();

                            if (xdeltaProcess.ExitCode != 0)
                            {
                                Console.WriteLine(
                                    $"xdelta3 returned non-zero exit code: {xdeltaProcess.ExitCode}");
                                return false;
                            }

                            File.Delete(Path.Combine(DownloadFolder, originalFilePath));
                            File.Move(Path.Combine(DownloadFolder, originalFilePath + ".updated"),
                                Path.Combine(DownloadFolder, originalFilePath));
                        }
                        else
                        {
                            var source = Path.Combine(TempFolder, fileName);
                            var dest = Path.Combine(DownloadFolder, entry.Key!);

                            var directory = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }

                            File.Move(source, dest, true);
                        }


                        File.Delete(Path.Combine(TempFolder, fileName));
                    }
                }

                Console.WriteLine(
                    $"Extraction and installation of {patch.FromVersion} -> {patch.ToVersion} completed successfully.");

                patch.Urls.ForEach(x => File.Delete(Path.Combine(DownloadFolder, Path.GetFileName(x))));
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred during patch {patch.ToVersion} extraction: {e.Message}");
                return false;
            }
        }

        return true;
    }
    public static async Task<bool> PerformFullInstallation()
    {
        try
        {
            Console.WriteLine("Installing Base Game...");
                
            if (!await DownloadLinkSection(GetSectionFullName("Base Game"), true))
                return false;
                
            Console.WriteLine("Base Game installed successfully!");
            Console.WriteLine("Installing Full Patch...");

            if (!await DownloadLinkSection(GetSectionFullName("Full Patch"), true))
                return false;
                
            Console.WriteLine("Full Patch installed successfully!");
            Console.WriteLine("Installing Extra tools...");

            if (!await DownloadLinkSection(GetSectionFullName("Extra tools"), true))
                return false;
                
            Console.WriteLine("Extra tools installed successfully!");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }

        return true;
    }

    public static async Task<bool> PerformDlcInstallation()
    {
        var dlcsList = GetSectionsByName("DLC");

        for (var i = 0; i < dlcsList.Length; i++)
        {
            var sectionName = dlcsList[i];
            var dlcName = sectionName.Substring(0, sectionName.LastIndexOf(' '));

            if (await PerformCrcCheck(sectionName))
                continue;

            Console.WriteLine($"Installing {dlcName}...");
                
            if (!await DownloadLinkSection(sectionName, true)) 
                return false;
                
            Console.WriteLine($"{dlcName} installed successfully!");
        }

        return true;
    }

    public static async Task<bool> PerformCrackInstallation()
    {
        try
        {
            var crackDetails = MainDocument.GetProperty("crack");

            var fileDetails = crackDetails.GetProperty("files").EnumerateObject();
            var verified = true;
            foreach (var fileElement in fileDetails)
            {
                var fileLocation = Path.Combine(DownloadFolder, fileElement.Name);
                var md5hash = fileElement.Value.EnumerateArray().ElementAt(0).GetString();

                if (await GetFileMd5Async(fileLocation) != md5hash)
                {
                    Console.WriteLine("Crack is changed. Performing installation...");
                    verified = false;
                    break;
                }
            }

            if (verified)
            {
                Console.WriteLine("Crack already up-to date. Skipping...");
                return true;
            }

            Console.WriteLine("Crack is downloading...");

            string crackFileName = crackDetails.GetProperty("filename").GetString()!;
            string crackPassword = crackDetails.GetProperty("pass").GetString()!;
            string crackDestinationPath = Path.Combine(DownloadFolder, crackFileName);
            var crackLinks = crackDetails.GetProperty("links").EnumerateArray();
                
            if (File.Exists(crackDestinationPath))
            {
                Console.WriteLine("Crack already downloaded, skipping...");
            }
            else
            {
                Console.WriteLine($"Downloading crack: {crackFileName}");
                foreach (var crack in crackLinks)
                {
                    try
                    {
                        string crackUrl = crack.GetString()!.Replace("https://raw_link.local/", "");
                        await DownloadFile(crackUrl, crackDestinationPath, 0, crackDetails.GetProperty("size").GetInt64());
                        break;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
                
            await ExtractArchiveAsync(crackDestinationPath, DownloadFolder, crackPassword);
                
            File.Delete(crackDestinationPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred during crack installation: {ex.Message}");
            return false;
        }

        Console.WriteLine("Crack downloaded and installed successfully.");

        return true;
    }


    static (List<Patch>, string) ExtractPatches()
    {
        var patches = new List<Patch>();
        string foundHighestVersion = "0.0.0.0";
        foreach (var patchElement in MainDocument.GetProperty("links").EnumerateObject())
        {
            string patchKey = patchElement.Name;
            long totalBytes = MetadataDocument.GetProperty(patchKey)[0].GetInt64();
            if (patchKey.StartsWith("Patch"))
            {
                string[] versionInfo = patchKey.Split(' ');
                string fromVersion = versionInfo[3];
                string toVersion = versionInfo[1];
                    
                if (CurrentGameVersion != null && CompareVersions(CurrentGameVersion, fromVersion) <= 0)
                {
                    var urls = patchElement.Value[1][0].EnumerateArray().Select(url => url.GetString()!.Replace("https://raw_link.local/", "")).ToList();
                    patches.Add(new Patch { FromVersion = fromVersion, ToVersion = toVersion, Urls = urls, TotalBytes = totalBytes, TotalSplitBytes = patchElement.Value[0].GetInt64() });
                }

                if (CompareVersions(foundHighestVersion, toVersion) <= 0)
                    foundHighestVersion = toVersion;
            }
        }
        return (patches.OrderBy(p => p.FromVersion).ToList(), foundHighestVersion);
    }

    static async Task<bool> DownloadLinkSection(string sectionName, bool shouldExtract, string? extractLocation = null)
    {
        var downloadName = sectionName.Substring(0, sectionName.LastIndexOf(' '));

        try
        {
            var sectionArray = MainDocument.GetProperty("links").GetProperty(sectionName).EnumerateArray();

            var sectionUrls = sectionArray.ElementAt(1).EnumerateArray()
                .Select(url => url.GetString()!.Replace("https://raw_link.local/", ""));
                
            long splitSize = sectionArray.ElementAt(0).GetInt64();
            long totalSize = MetadataDocument.GetProperty(sectionName)[0].GetInt64();

            var downloadFolder = extractLocation ?? DownloadFolder;

            var urls = sectionUrls as string[] ?? sectionUrls.ToArray();

            for (int index = 0; index < urls.Length; index++)
            {
                string url = urls[index];

                await ProcessDownload(url, downloadFolder, downloadName, splitSize, index, totalSize);
            }

            if (shouldExtract)
            {
                var sectionFirstFile = Path.GetFileName(urls.First());

                await ExtractArchiveAsync(sectionFirstFile, downloadFolder);

                Console.WriteLine($"Installation of {downloadName} completed successfully.");

                foreach (var zipFile in urls.Select(Path.GetFileName))
                {
                    File.Delete(zipFile);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error occurred while installing {downloadName}, Error: {e}");
            return false;
        }

        return true;
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationFolder, string? password = null)
    {
        try
        {
            // Open the archive file
            using (var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = password }))
            {
                // Iterate through the entries in the archive
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    // Extract each entry to the specified folder
                    entry.WriteToDirectory(destinationFolder, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }
            Console.WriteLine($"Extraction of {archivePath} completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred during extraction: {ex.Message}");
        }
    }


    private static async Task<bool> ProcessDownload(string url, string downloadFolder, string downloadName, long splitSize, int index, long totalSize)
    {
        string fileName = Path.GetFileName(url);

        if (File.Exists(fileName) &&
            (index < totalSize / splitSize
                ? new FileInfo(fileName).Length == splitSize
                : new FileInfo(fileName).Length == totalSize % splitSize))
        {
            Console.WriteLine($"This {fileName} already downloaded. Skipping...");
            return true;
        }
        string destinationPath = Path.Combine(downloadFolder, fileName);
        Console.WriteLine($"Downloading {downloadName}: {fileName}");
        await DownloadFile(url, destinationPath, splitSize * index, totalSize);
        return true;
    }

    public static async Task<string> GetFileMd5Async(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }
        using (var md5 = MD5.Create())
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
        {
            var hashBytes = await md5.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToUpperInvariant();
        }
    }

    private static async Task<bool> PerformCrcCheck(string sectionName)
    {
        var downloadName = sectionName.Substring(0, sectionName.LastIndexOf(' '));

        string base64Metadata = MetadataDocument.GetProperty(sectionName)[2].GetString()!;
        var crcChecker = new CrcChecker(base64Metadata);
        var filesCrcList = crcChecker.ExtractFilesCrcList();
        Console.WriteLine($"Checking the CRC of the installed {downloadName}...");
        bool allMatch = await CrcChecker.CheckFilesCrcAsync(filesCrcList.ToArray());
        if (allMatch)
        {
            Console.WriteLine($"{downloadName} is already installed, skipping...");
            return true;
        }
        return false;
    }

    static string GetSectionFullName(string sectionName)
    {
        return MainDocument.GetProperty("links")
            .EnumerateObject().First(x => x.Name.StartsWith(sectionName)).Name;
    }
    static string[] GetSectionsByName(string sectionName)
    {
        return MainDocument.GetProperty("links")
            .EnumerateObject().Where(x => x.Name.StartsWith(sectionName)).Select(x => x.Name).ToArray();
    }

    public static int CompareVersions(string version1, string version2)
    {
        var v1 = version1.Split('.').Select(int.Parse).ToArray();
        var v2 = version2.Split('.').Select(int.Parse).ToArray();
        for (int i = 0; i < Math.Min(v1.Length, v2.Length); i++)
        {
            if (v1[i] != v2[i])
            {
                return v1[i].CompareTo(v2[i]);
            }
        }
        return v1.Length.CompareTo(v2.Length);
    }

    static JsonElement FetchJsonData(string url)
    {
        using HttpClient client = new HttpClient();
        var responseTask = client.GetStringAsync(url);
        var response = responseTask.GetAwaiter().GetResult();
        return JsonDocument.Parse(response).RootElement;
    }

    static string? GetFileVersion(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }
        var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
        return versionInfo.FileVersion;
    }
    static async Task DownloadFile(string url, string destinationPath, long downloadedBytes, long totalSize)
    {
        using HttpClient client = new HttpClient();
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var totalBytesDownloaded = 0L;
        var bufferTotalBytes = 0L;
        var stopwatch = Stopwatch.StartNew();
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[8192];
        int bytesRead;
        DateTime lastLog = DateTime.Now;

        Console.Write($"\rDownloaded: {totalBytesDownloaded / 1024}/{totalBytes / 1024} KB | File: {0:F2}%, Total: {0:F2}%, Speed: {0:F2} KB/s");

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {

            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalBytesDownloaded += bytesRead;
            bufferTotalBytes += bytesRead;
            if (totalSize > 0 && (DateTime.Now.Subtract(lastLog).TotalSeconds > 10))
            {
                double percentage = (double)(downloadedBytes + totalBytesDownloaded) / totalSize * 100;
                double percentageFile = (double)totalBytesDownloaded / totalBytes * 100;
                double speed = bufferTotalBytes / stopwatch.Elapsed.TotalSeconds / 1024; // Speed in KB/s
                stopwatch.Restart();
                bufferTotalBytes = 0;
                Console.Write($"\rDownloaded: {totalBytesDownloaded / 1024}/{totalBytes / 1024} KB | File: {percentageFile:F2}%, Total: {percentage:F2}%, Speed: {speed:F2} KB/s");
                lastLog = DateTime.Now;
            }
        }

        Console.WriteLine();
    }
    static string? GetGameVersion()
    {
        var parser = new FileIniDataParser();
        string iniFilePath = Path.Combine(DownloadFolder, GameIniPath);
        string exeFilePath = Path.Combine(DownloadFolder, GameExePath);
            
        if (File.Exists(iniFilePath))
        {
            var iniData = parser.ReadFile(iniFilePath);
            return iniData["Version"]["gameversion"];
        }
            
        if (File.Exists(exeFilePath))
        {
            return GetFileVersion(exeFilePath);
        }
            
        return null;
    }
}