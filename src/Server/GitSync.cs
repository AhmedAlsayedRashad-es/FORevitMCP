using System.Diagnostics;
using System.Text;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Server;

public sealed class PushResult
{
    public bool Ok { get; set; }
    public string Repo { get; set; }
    public string Branch { get; set; }
    public string Commit { get; set; }
    public string Url { get; set; }
    public int Files { get; set; }
    public string Message { get; set; }
    public bool NothingToPush { get; set; }
}

/// <summary>Commits the library folder and pushes it to the GitHub repo from settings.json. Uses the git CLI.</summary>
public static class GitSync
{
    public static async Task<PushResult> PushAsync(string message, string client, CancellationToken ct)
    {
        var s = McpSettings.Load();
        if (!s.GitHubConfigured)
            throw new ToolError("GitHub is not set.", "Ask the user to open Revit > First Option > AI Bridge > GitHub Settings, and fill in the repository owner and name.");

        var root = s.EffectiveLibraryPath;
        Directory.CreateDirectory(root);
        var branch = s.EffectiveBranch;
        var repo = $"{s.GitHubOwner.Trim()}/{s.GitHubRepo.Trim()}";
        var remote = string.IsNullOrWhiteSpace(s.RemoteUrl) ? $"https://github.com/{repo}.git" : s.RemoteUrl.Trim();
        var token = s.GetToken();
        var auth = string.IsNullOrEmpty(token)
            ? Array.Empty<string>()
            : new[] { "-c", "http.https://github.com/.extraheader=AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + token)) };

        await Git(root, ct, true, "--version");
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            await Git(root, ct, true, "init");
            await Git(root, ct, true, "symbolic-ref", "HEAD", "refs/heads/" + branch);
        }

        var current = await Git(root, ct, false, "remote", "get-url", "origin");
        if (current.Code != 0) await Git(root, ct, true, "remote", "add", "origin", remote);
        else if (!string.Equals(current.Out.Trim(), remote, StringComparison.OrdinalIgnoreCase)) await Git(root, ct, true, "remote", "set-url", "origin", remote);

        await Git(root, ct, true, "add", "-A");
        var status = await Git(root, ct, true, "status", "--porcelain");
        var files = status.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        if (files > 0)
        {
            var name = string.IsNullOrWhiteSpace(s.AuthorName) ? "FirstOption MCP" : s.AuthorName.Trim();
            var email = string.IsNullOrWhiteSpace(s.AuthorEmail) ? "mcp@firstoption.local" : s.AuthorEmail.Trim();
            await Git(root, ct, true, "-c", "user.name=" + name, "-c", "user.email=" + email, "commit", "-m", string.IsNullOrWhiteSpace(message) ? "Update command library" : message);
        }
        else if ((await Git(root, ct, false, "rev-parse", "HEAD")).Code != 0)
        {
            return new PushResult { Ok = true, NothingToPush = true, Repo = repo, Branch = branch, Message = "The library is empty. Save a command first." };
        }

        var push = await Git(root, ct, false, auth.Concat(new[] { "push", "-u", "origin", "HEAD:refs/heads/" + branch }).ToArray());
        if (push.Code != 0 && (push.Err.Contains("rejected") || push.Err.Contains("fetch first") || push.Err.Contains("non-fast-forward")))
        {
            await Git(root, ct, true, auth.Concat(new[] { "pull", "--rebase", "origin", branch }).ToArray());
            push = await Git(root, ct, false, auth.Concat(new[] { "push", "-u", "origin", "HEAD:refs/heads/" + branch }).ToArray());
        }
        if (push.Code != 0)
            throw new ToolError("git push failed: " + Mask(push.Err, token),
                "Check the token in GitHub Settings (it needs Contents: read and write on " + repo + "), and that the repository exists.");

        var sha = (await Git(root, ct, true, "rev-parse", "HEAD")).Out.Trim();
        var upToDate = files == 0 && push.Err.Contains("Everything up-to-date");
        var result = new PushResult
        {
            Ok = true,
            Repo = repo,
            Branch = branch,
            Commit = sha.Length >= 7 ? sha.Substring(0, 7) : sha,
            Url = $"https://github.com/{repo}/commit/{sha}",
            Files = files,
            NothingToPush = upToDate,
            Message = upToDate ? "Nothing new. GitHub is up to date." : message,
        };

        if (!upToDate)
        {
            ActivityLog.Append(new ActivityEntry
            {
                Kind = ActivityKinds.GitHubPush,
                Client = client,
                Ok = true,
                Message = message,
                Repo = repo,
                Commit = result.Commit,
                Url = result.Url,
                Files = files,
            });
        }
        return result;
    }

    public static async Task<object> StatusAsync(CancellationToken ct)
    {
        var s = McpSettings.Load();
        var root = s.EffectiveLibraryPath;
        var isRepo = Directory.Exists(Path.Combine(root, ".git"));
        string pending = null, last = null;
        if (isRepo)
        {
            var st = await Git(root, ct, false, "status", "--porcelain");
            pending = st.Code == 0 ? st.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString() : null;
            var lg = await Git(root, ct, false, "log", "-1", "--format=%h %cI %s");
            last = lg.Code == 0 ? lg.Out.Trim() : null;
        }
        return new
        {
            configured = s.GitHubConfigured,
            repo = s.GitHubConfigured ? $"{s.GitHubOwner}/{s.GitHubRepo}" : null,
            branch = s.EffectiveBranch,
            tokenSaved = s.HasToken,
            autoPush = s.AutoPush,
            libraryPath = root,
            gitRepo = isRepo,
            pendingChanges = pending,
            lastCommit = last,
            hint = s.GitHubConfigured ? null : "Ask the user to open Revit > First Option > AI Bridge > GitHub Settings.",
        };
    }

    private sealed record GitOut(int Code, string Out, string Err);

    private static async Task<GitOut> Git(string cwd, CancellationToken ct, bool throwOnError, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,   // never let git inherit the MCP stdio pipe
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolError("git is not installed or not on PATH.", "Install Git for Windows: winget install --id Git.Git -e");
        }

        using (p)
        {
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new ToolError("git " + args.LastOrDefault(a => !a.StartsWith("http.")) + " did not finish in 2 minutes.");
            }
            var r = new GitOut(p.ExitCode, await outTask, await errTask);
            if (throwOnError && r.Code != 0)
                throw new ToolError("git " + string.Join(" ", args.Where(a => !a.Contains("extraheader"))) + " failed (exit " + r.Code + "): " + (r.Err + r.Out).Trim());
            return r;
        }
    }

    private static string Mask(string text, string token) =>
        string.IsNullOrEmpty(token) ? text.Trim() : text.Replace(token, "***").Trim();
}
