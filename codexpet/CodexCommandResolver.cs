using System.Diagnostics;
using System.IO;

namespace codexpet;

public sealed record CodexCommand(string FileName, string Arguments, bool RunsThroughShell)
{
    public static CodexCommand Create(string codexPath, string appServerArguments)
    {
        var extension = Path.GetExtension(codexPath).ToLowerInvariant();
        return extension switch
        {
            ".cmd" or ".bat" => new CodexCommand(
                "cmd.exe",
                $"/d /s /c \"\"{codexPath}\" {appServerArguments}\"",
                true),
            ".ps1" => new CodexCommand(
                ResolvePowerShell(),
                $"-NoProfile -ExecutionPolicy Bypass -File \"{codexPath}\" {appServerArguments}",
                true),
            _ => new CodexCommand(codexPath, appServerArguments, false)
        };
    }

    private static string ResolvePowerShell()
    {
        var systemPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(systemPowerShell) ? systemPowerShell : "powershell.exe";
    }
}

public static class CodexCommandResolver
{
    public static string ResolveCodexPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (IsRunnableFile(explicitPath))
        {
            return explicitPath!;
        }

        var candidates = new List<string>();
        candidates.AddRange(Where("codex.cmd"));
        candidates.AddRange(Where("codex.bat"));
        candidates.AddRange(Where("codex.ps1"));
        candidates.AddRange(Where("codex.exe"));
        candidates.AddRange(Where("codex"));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "npm", "codex.cmd"));
            candidates.Add(Path.Combine(appData, "npm", "codex.ps1"));
        }

        var distinct = candidates
            .Where(IsRunnableFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScoreCandidate)
            .ToArray();

        return distinct.FirstOrDefault() ?? "codex";
    }

    private static IEnumerable<string> Where(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsRunnableFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static int ScoreCandidate(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var score = extension switch
        {
            ".cmd" => 0,
            ".bat" => 5,
            ".ps1" => 10,
            ".exe" => 20,
            _ => 30
        };

        if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score;
    }
}
