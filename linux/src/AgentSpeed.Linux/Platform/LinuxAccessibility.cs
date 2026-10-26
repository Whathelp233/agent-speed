using System.Diagnostics;

namespace AgentSpeed.Linux.Platform;

internal static class LinuxAccessibility
{
    public static bool ReduceMotion { get; private set; } = Read("enable-animations") == "false";
    public static bool HighContrast { get; private set; } = Read("gtk-theme")?.Contains("HighContrast", StringComparison.Ordinal) == true;
    public static bool PrefersDark { get; private set; } = ResolveDark(Read("color-scheme"), Read("gtk-theme"));
    internal static bool ResolveDark(string? scheme, string? theme) => scheme?.Trim('\'') switch
    {
        "prefer-dark" => true,
        "prefer-light" => false,
        _ => theme?.Contains("dark", StringComparison.OrdinalIgnoreCase) == true,
    };
    public static bool Refresh()
    {
        var reduce = Read("enable-animations") == "false";
        var theme = Read("gtk-theme");
        var contrast = theme?.Contains("HighContrast", StringComparison.Ordinal) == true;
        var dark = ResolveDark(Read("color-scheme"), theme);
        var changed = reduce != ReduceMotion || contrast != HighContrast || dark != PrefersDark;
        ReduceMotion = reduce; HighContrast = contrast; PrefersDark = dark; return changed;
    }
    private static string? Read(string key)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings")
            { ArgumentList = { "get", "org.gnome.desktop.interface", key }, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null || !process.WaitForExit(1000)) { process?.Kill(); return null; }
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
