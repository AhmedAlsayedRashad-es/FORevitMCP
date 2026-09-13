using System.ComponentModel;
using ModelContextProtocol.Server;

namespace FirstOption.RevitMcp.Server.Tools;

[McpServerToolType]
public sealed class GitHubTools
{
    [McpServerTool(Name = "github_status", ReadOnly = true), Description(
        "GitHub settings for the command library (repo, branch, token saved yes/no, auto-push) and the local git state (pending changes, last commit). The user sets these in Revit > First Option > GitHub Settings.")]
    public Task<string> Status(CancellationToken ct = default) => Json.Guard(() => GitSync.StatusAsync(ct));

    [McpServerTool(Name = "github_push"), Description(
        "Commit all library changes and push them to the GitHub repo from the settings. The Revit panel shows a notice with the commit. Push when the user asks, or after you save commands when auto-push is off and the user wants them shared.")]
    public Task<string> Push(
        McpServer server,
        [Description("Commit message, for example 'Add create_wall_grid and place_door_family'.")] string message,
        CancellationToken ct = default) =>
        Json.Guard(async () => await GitSync.PushAsync(message, RevitTools.ClientName(server), ct));
}
