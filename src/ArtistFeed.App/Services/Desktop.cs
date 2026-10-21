using System.Diagnostics;

namespace ArtistFeed.App.Services;

public static class Desktop
{
    /// <summary>Open a web address or folder with the system's default app.</summary>
    public static void Open(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        if (!(target.StartsWith("https://") || target.StartsWith("http://") || Directory.Exists(target))) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", target);
            else Process.Start("xdg-open", target);
        }
        catch (Exception) { }
    }
}
