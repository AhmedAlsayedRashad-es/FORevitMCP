using System.Diagnostics;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Server;

/// <summary>`FirstOption.RevitMcp doctor` — prints one line per check.</summary>
public static class Doctor
{
    public static async Task<int> RunAsync()
    {
        var s = McpSettings.Load();
        Line("server", true, "FirstOption.RevitMcp " + Json.Version + " on .NET " + Environment.Version);
        Line("settings", File.Exists(AppPaths.SettingsFile), AppPaths.SettingsFile + (File.Exists(AppPaths.SettingsFile) ? "" : " (not created yet; defaults are used)"));
        Line("library", Directory.Exists(s.EffectiveLibraryPath), s.EffectiveLibraryPath);
        Line("git", Which("git", "--version", out var git), git);
        Line("pyrevit cli", Which("pyrevit", "--version", out var pyr), pyr);
        Line("github", s.GitHubConfigured, s.GitHubConfigured ? $"{s.GitHubOwner}/{s.GitHubRepo} ({s.EffectiveBranch}) token={(s.HasToken ? "saved" : "none")} autoPush={s.AutoPush}" : "not set (Revit > First Option > GitHub Settings)");

        var list = await new RoutesClient().ProbeAsync(CancellationToken.None);
        Line("revit", list.Count > 0, list.Count == 0
            ? $"no bridge on {s.EffectiveHost}:{s.PortStart}-{s.PortStart + s.PortCount - 1}"
            : string.Join("; ", list.Select(i => $"port {i.Port}: Revit {i.RevitVersion}, {i.Document ?? "no document"}, pyRevit {i.PyRevit}, C# runner {(i.CSharpRunner ? "yes" : "no")}")));
        return 0;
    }

    private static void Line(string name, bool ok, string detail) =>
        Console.WriteLine($"[{(ok ? " ok " : "warn")}] {name,-12} {detail}");

    private static bool Which(string exe, string arg, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            output = text.Trim().Split('\n').FirstOrDefault()?.Trim();
            return p.ExitCode == 0;
        }
        catch
        {
            output = "not found on PATH";
            return false;
        }
    }
}
