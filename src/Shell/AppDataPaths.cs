using System.IO;

namespace PrecisionScoresDesktop.Shell;

// Cross-platform paths for the app's writable data. Honours the OS
// convention automatically via SpecialFolder.LocalApplicationData:
//   Windows → %LOCALAPPDATA%\PrecisionScoresDesktop\
//   macOS   → ~/Library/Application Support/PrecisionScoresDesktop/
internal static class AppDataPaths
{
    private const string AppFolder = "PrecisionScoresDesktop";

    public static string Root() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder);

    public static string LogsDirectory() => Path.Combine(Root(), "logs");

    public static string DatabaseFile() => Path.Combine(Root(), "offline.db");

    public static string ScannerBinaryDirectory() => Path.Combine(Root(), "bin");
}
