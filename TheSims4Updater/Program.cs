namespace TheSims4Updater
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var gameVersion = GameUpdater.CurrentGameVersion;
                var latestVersion = GameUpdater.LatestGameVersion;

                Console.WriteLine(GameUpdater.ShouldPerformFullInstall
                    ? "Game is not installed. Performing a full installation..."
                    : $"Current game version: {gameVersion}");
                
                if (!GameUpdater.ShouldPerformFullInstall)
                {
                    Console.WriteLine($"Latest version available: {latestVersion}");
                    if (gameVersion != latestVersion)
                    {
                        Console.WriteLine("Game version is outdated. Consider updating.");
                    }
                    else
                    {
                        Console.WriteLine("Game is up-to-date.");
                    }

                    await GameUpdater.PerformPatches();
                }
                else
                {
                    await GameUpdater.PerformFullInstallation();
                }

                // Step 4: Download all DLCs
                Console.WriteLine("Downloading DLCs...");
                await GameUpdater.PerformDlcInstallation();

                Console.WriteLine("All DLCs downloaded and installed successfully.");

                await GameUpdater.PerformCrackInstallation();

                Console.WriteLine("Game updated successfully.");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
