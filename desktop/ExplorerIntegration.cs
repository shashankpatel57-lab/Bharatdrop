using Microsoft.Win32;

namespace FileSetuDesktop;

internal static class ExplorerIntegration
{
    private const string FileKey = @"Software\Classes\*\shell\FileSetu";
    private const string DirKey = @"Software\Classes\Directory\shell\FileSetu";

    public static void Install(string exePath)
    {
        InstallOne(FileKey, exePath);
        InstallOne(DirKey, exePath);
    }

    private static void InstallOne(string keyPath, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("", "Send with FileSetu");
        key.SetValue("Icon", exePath);
        using var command = key.CreateSubKey("command");
        command.SetValue("", $"\"{exePath}\" --send \"%1\"");
    }

    public static void Remove()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(FileKey, false); } catch {}
        try { Registry.CurrentUser.DeleteSubKeyTree(DirKey, false); } catch {}
    }

    public static bool IsInstalled()
    {
        using var a = Registry.CurrentUser.OpenSubKey(FileKey);
        using var b = Registry.CurrentUser.OpenSubKey(DirKey);
        return a != null && b != null;
    }
}
