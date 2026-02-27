using System.IO;
using System.Windows;
using D2RExtractor.Native;
using D2RExtractor.Services;

namespace D2RExtractor;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LoggingService.Initialize();

        if (!CascLib.IsDllPresent())
        {
            System.Windows.MessageBox.Show(
                "CascLib.dll not found.\n\n" +
                "This application requires CascLib.dll (x64) to read D2R's CASC game archives.\n\n" +
                "How to obtain it:\n" +
                "  1. Download Ladik's CASC Viewer from https://www.zezula.net/en/casc/main.html\n" +
                "  2. Extract the zip — it contains CascLib.dll\n" +
                "  3. Copy CascLib.dll next to D2RExtractor.exe\n\n" +
                $"Expected location:\n{Path.Combine(AppContext.BaseDirectory, "CascLib.dll")}",
                "D2R Extractor — Missing Dependency",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            // Allow the app to continue — user can fix and relaunch.
        }
    }
}
