using System.ComponentModel;
using FirstOption.RevitMcp.Shared;
using ModelContextProtocol.Server;

namespace FirstOption.RevitMcp.Server.Tools;

[McpServerToolType]
public sealed class LibraryTools(RoutesClient routes)
{
    [McpServerTool(Name = "library_search", ReadOnly = true), Description(
        "Search the saved Revit command library (name, title, description, tags). Call this BEFORE you write new Revit code, and reuse or adapt a command when one fits. " +
        "Commands can be ironpython, cpython or csharp. An empty query lists all commands.")]
    public Task<string> Search(
        [Description("Words to look for, for example 'wall grid' or 'family extrusion'.")] string query = null,
        [Description("Optional filter: ironpython, cpython or csharp.")] string language = null,
        [Description("Maximum results (default 20).")] int limit = 20,
        CancellationToken ct = default) => Json.Guard(() =>
    {
        var results = CommandLibrary.Search(query, language, limit);
        return Task.FromResult<object>(new
        {
            library = CommandLibrary.Root,
            count = results.Count,
            results,
            hint = results.Count == 0 ? "Nothing found. Write the code, run it, and when it works save it with library_save." : "Call library_get to read the code.",
        });
    });

    [McpServerTool(Name = "library_get", ReadOnly = true), Description("Read one saved command: metadata (description, inputs, notes, runs, tested Revit versions) and its code body.")]
    public Task<string> Get([Description("Command name (snake_case).")] string name, CancellationToken ct = default) => Json.Guard(() =>
    {
        var (meta, code, dir) = CommandLibrary.Get(name);
        return Task.FromResult<object>(new { meta, code, folder = dir });
    });

    [McpServerTool(Name = "library_save"), Description(
        "Save code that WORKED in Revit to the command library, so any agent can find and run it again later. " +
        "It writes a pyRevit button (script.py or script.cs) plus metadata, and updates index.json and README.md. " +
        "When GitHub auto-push is on, it also pushes to GitHub and the Revit panel shows a notice. " +
        "Save only after a successful run. Write a clear description and list the inputs (args keys, selection, active view).")]
    public Task<string> Save(
        McpServer server,
        [Description("snake_case name, for example create_wall_grid.")] string name,
        [Description("What the command does and what it needs.")] string description,
        [Description("ironpython, cpython or csharp.")] string language,
        [Description("The code body exactly as it ran (Python script, or C# method body).")] string code,
        [Description("Button title. Default: from the name.")] string title = null,
        [Description("Search tags, for example [\"walls\",\"grid\"].")] string[] tags = null,
        [Description("The args keys and other inputs, for example 'args: spacing_ft (float), levels (list of names); needs a floor plan view'.")] string inputs = null,
        [Description("Limits, Revit version notes, known problems.")] string notes = null,
        [Description("Replace a command with the same name.")] bool overwrite = false,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var client = RevitTools.ClientName(server);
        var (meta, dir) = CommandLibrary.Save(name, title, description, language, code, tags, inputs, notes, overwrite, client);
        ActivityLog.Append(new ActivityEntry { Kind = ActivityKinds.LibrarySave, Client = client, Ok = true, CommandName = meta.Name, Language = meta.Language, Message = meta.Description });

        object push = null;
        var s = McpSettings.Load();
        if (s.AutoPush && s.GitHubConfigured)
        {
            try { push = await GitSync.PushAsync($"{(overwrite ? "Update" : "Add")} command {meta.Name}", client, ct); }
            catch (Exception e) { push = new { ok = false, error = e.Message, hint = (e as ToolError)?.Hint }; }
        }

        return new
        {
            ok = true,
            saved = meta.Name,
            meta.Language,
            folder = dir,
            push,
            hint = push == null
                ? (s.GitHubConfigured ? "Auto-push is off. Call github_push when the user wants to upload." : "GitHub is not set; the command is saved on this computer only.")
                : null,
        };
    });

    [McpServerTool(Name = "library_run"), Description(
        "Run a saved ironpython or csharp command in Revit with optional args. The run count and tested Revit versions are updated. cpython commands run only as pyRevit buttons.")]
    public Task<string> Run(
        McpServer server,
        [Description("Command name.")] string name,
        [Description("JSON object passed as args.")] string args_json = null,
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("Wrap the run in one Transaction (default true).")] bool use_transaction = true,
        [Description("Seconds to wait for Revit (default 120).")] int timeout_seconds = 120,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var (meta, code, _) = CommandLibrary.Get(name);
        if (meta.Language == "cpython")
            throw new ToolError($"'{name}' is a CPython command. The MCP runs IronPython and C# only.",
                "Ask the user to click the button on the 'FO Library' tab, or save an IronPython version of the command.");

        var result = await RevitTools.ExecuteAsync(routes, server, meta.Language, code, meta.Name, port, use_transaction, null, args_json, timeout_seconds, ct);
        var node = result as System.Text.Json.Nodes.JsonObject;
        var ok = node?["ok"]?.GetValue<bool>() ?? false;
        CommandLibrary.RecordRun(meta.Name, ok, node?["revitVersion"]?.ToString());
        return result;
    });

    [McpServerTool(Name = "library_info", ReadOnly = true), Description("Where the command library is, how many commands it has, and how to show its buttons in pyRevit.")]
    public Task<string> Info(CancellationToken ct = default) => Json.Guard(() =>
    {
        var root = CommandLibrary.Root;
        var all = CommandLibrary.All(root).ToList();
        return Task.FromResult<object>(new
        {
            library = root,
            count = all.Count,
            byLanguage = all.GroupBy(c => c.Meta.Language).ToDictionary(g => g.Key, g => g.Count()),
            pyRevitExtension = Path.Combine(root, CommandLibrary.ExtensionFolder),
            showButtons = $"pyrevit extensions paths add \"{root}\"  (then reload pyRevit; the buttons are on the 'FO Library' tab)",
        });
    });
}
