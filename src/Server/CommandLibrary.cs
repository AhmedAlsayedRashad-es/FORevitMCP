using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Server;

public sealed class CommandMeta
{
    public string Name { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Language { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Inputs { get; set; }
    public string Notes { get; set; }
    public string CreatedBy { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public int Runs { get; set; }
    public bool? LastRunOk { get; set; }
    public DateTime? LastRun { get; set; }
    public List<string> TestedRevitVersions { get; set; } = new();
}

/// <summary>
/// The command library is a folder (a git repo when GitHub is set) that is also a pyRevit extension:
///   &lt;library&gt;/FirstOptionLibrary.extension/FO Library.tab/Commands.panel/&lt;name&gt;.pushbutton/
///       command.json   metadata the agent searches
///       body.py|body.cs the agent's code, exactly as it ran through the MCP
///       script.py|script.cs  a pyRevit button wrapper around the body
/// plus index.json and README.md at the root, written again on every save.
/// </summary>
public static class CommandLibrary
{
    public const string ExtensionFolder = AppPaths.LibraryExtensionFolder;

    public static readonly string[] Languages = { "ironpython", "cpython", "csharp" };

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{2,60}$");

    public static string Root => McpSettings.Load().EffectiveLibraryPath;

    public static string PanelDir(string root) => AppPaths.LibraryPanelDir(root);

    public static string CommandDir(string root, string name) => Path.Combine(PanelDir(root), name + ".pushbutton");

    public static IEnumerable<(CommandMeta Meta, string Dir)> All(string root)
    {
        var panel = PanelDir(root);
        if (!Directory.Exists(panel)) yield break;
        foreach (var dir in Directory.GetDirectories(panel, "*.pushbutton").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var meta = ReadMeta(dir);
            if (meta != null) yield return (meta, dir);
        }
    }

    public static CommandMeta ReadMeta(string dir)
    {
        var file = Path.Combine(dir, "command.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<CommandMeta>(File.ReadAllText(file), Json.Options); }
        catch { return null; }
    }

    public static (CommandMeta Meta, string Code, string Dir) Get(string name)
    {
        var root = Root;
        var dir = CommandDir(root, name);
        var meta = ReadMeta(dir) ?? throw new ToolError($"Command '{name}' is not in the library.", "Call library_search to find the right name.");
        var body = Path.Combine(dir, BodyFile(meta.Language));
        return (meta, File.Exists(body) ? File.ReadAllText(body) : "", dir);
    }

    public static List<object> Search(string query, string language, int limit)
    {
        var root = Root;
        var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        return All(root)
            .Where(c => string.IsNullOrWhiteSpace(language) || string.Equals(c.Meta.Language, language, StringComparison.OrdinalIgnoreCase))
            .Select(c =>
            {
                var hay = string.Join(" ", c.Meta.Name, c.Meta.Title, c.Meta.Description, string.Join(" ", c.Meta.Tags ?? new()), c.Meta.Language).ToLowerInvariant();
                var score = words.Length == 0 ? 1 : words.Count(w => hay.Contains(w)) + (words.Any(w => c.Meta.Name.Contains(w)) ? 1 : 0);
                return (c.Meta, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.Meta.Runs)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => (object)new
            {
                x.Meta.Name,
                x.Meta.Title,
                x.Meta.Language,
                x.Meta.Description,
                x.Meta.Tags,
                x.Meta.Inputs,
                x.Meta.Runs,
                x.Meta.LastRunOk,
                x.Meta.TestedRevitVersions,
            })
            .ToList();
    }

    public static (CommandMeta Meta, string Dir) Save(string name, string title, string description, string language, string code,
        IEnumerable<string> tags, string inputs, string notes, bool overwrite, string client)
    {
        name = (name ?? "").Trim();
        if (!NamePattern.IsMatch(name))
            throw new ToolError($"Bad name '{name}'.", "Use snake_case: lower-case letters, digits and '_', 3-61 chars, starting with a letter. Example: create_wall_grid.");
        language = (language ?? "").Trim().ToLowerInvariant();
        if (language == "python") language = "ironpython";
        if (language == "c#" || language == "cs") language = "csharp";
        if (!Languages.Contains(language))
            throw new ToolError($"Bad language '{language}'.", "Use ironpython, cpython or csharp.");
        if (string.IsNullOrWhiteSpace(code)) throw new ToolError("The code is empty.");
        if (string.IsNullOrWhiteSpace(description)) throw new ToolError("The description is empty.", "Write one or two sentences: what the command does, and what it needs (selection, active view, family document, args).");

        var root = Root;
        var dir = CommandDir(root, name);
        var old = ReadMeta(dir);
        if (old != null && !overwrite)
            throw new ToolError($"Command '{name}' exists.", "Call library_get to compare. Pass overwrite=true to replace it, or choose a new name.");

        Directory.CreateDirectory(dir);
        foreach (var stale in new[] { "body.py", "body.cs", "script.py", "script.cs" })
        {
            var p = Path.Combine(dir, stale);
            if (File.Exists(p)) File.Delete(p);
        }

        var now = DateTime.UtcNow;
        var meta = new CommandMeta
        {
            Name = name,
            Title = string.IsNullOrWhiteSpace(title) ? TitleFromName(name) : title.Trim(),
            Description = description.Trim(),
            Language = language,
            Tags = (tags ?? Enumerable.Empty<string>()).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList(),
            Inputs = inputs,
            Notes = notes,
            CreatedBy = old?.CreatedBy ?? client,
            Created = old?.Created ?? now,
            Updated = now,
            Runs = old?.Runs ?? 0,
            LastRunOk = old?.LastRunOk,
            LastRun = old?.LastRun,
            TestedRevitVersions = old?.TestedRevitVersions ?? new(),
        };

        File.WriteAllText(Path.Combine(dir, BodyFile(language)), code.Replace("\r\n", "\n"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, language == "csharp" ? "script.cs" : "script.py"), Wrappers.Button(meta, code), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "bundle.yaml"), $"title: \"{Yaml(meta.Title)}\"\ntooltip: \"{Yaml(meta.Description)}\"\nauthor: \"FirstOption MCP\"\n", new UTF8Encoding(false));
        WriteMeta(dir, meta);
        WriteIndex(root);
        return (meta, dir);
    }

    public static void RecordRun(string name, bool ok, string revitVersion)
    {
        var root = Root;
        var dir = CommandDir(root, name);
        var meta = ReadMeta(dir);
        if (meta == null) return;
        meta.Runs++;
        meta.LastRunOk = ok;
        meta.LastRun = DateTime.UtcNow;
        if (ok && !string.IsNullOrWhiteSpace(revitVersion) && !meta.TestedRevitVersions.Contains(revitVersion))
            meta.TestedRevitVersions.Add(revitVersion);
        WriteMeta(dir, meta);
        WriteIndex(root);
    }

    public static string BodyFile(string language) => language == "csharp" ? "body.cs" : "body.py";

    private static void WriteMeta(string dir, CommandMeta meta) =>
        File.WriteAllText(Path.Combine(dir, "command.json"), JsonSerializer.Serialize(meta, Json.Indented), new UTF8Encoding(false));

    private static void WriteIndex(string root)
    {
        var all = All(root).Select(c => c.Meta).ToList();
        File.WriteAllText(Path.Combine(root, "index.json"), JsonSerializer.Serialize(new
        {
            generated = DateTime.UtcNow,
            count = all.Count,
            commands = all.Select(m => new { m.Name, m.Title, m.Language, m.Description, m.Tags, m.Inputs, m.Runs, m.LastRunOk, m.TestedRevitVersions }),
        }, Json.Indented), new UTF8Encoding(false));

        var sb = new StringBuilder();
        sb.AppendLine("# FirstOption Revit Command Library");
        sb.AppendLine();
        sb.AppendLine("The FirstOption Revit MCP writes this file. Do not edit it by hand.");
        sb.AppendLine("Each command is a pyRevit button in `" + ExtensionFolder + "` and an MCP tool target (`library_run`).");
        sb.AppendLine();
        sb.AppendLine("| Command | Language | Description | Runs | Tested in |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var m in all)
            sb.AppendLine($"| `{m.Name}` | {m.Language} | {m.Description.Replace("|", "\\|").Replace("\n", " ")} | {m.Runs} | {string.Join(", ", m.TestedRevitVersions)} |");
        File.WriteAllText(Path.Combine(root, "README.md"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string TitleFromName(string name) =>
        string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));

    private static string Yaml(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
