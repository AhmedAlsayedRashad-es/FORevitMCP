using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FirstOption.RevitMcp.Shared;
using ModelContextProtocol.Server;

namespace FirstOption.RevitMcp.Server.Tools;

[McpServerToolType]
public sealed class RevitTools(RoutesClient routes)
{
    [McpServerTool(Name = "revit_instances", ReadOnly = true), Description(
        "List the running Revit sessions that answer through pyRevit Routes with the FirstOption MCP bridge: port, Revit version, active document, pyRevit version, Python engine, and whether the C# runner (Revit add-in) is loaded. Call this first. When more than one is listed, ask the user which port to use.")]
    public Task<string> Instances(CancellationToken ct) => Json.Guard(async () =>
    {
        var s = McpSettings.Load();
        var list = await routes.ProbeAsync(ct);
        return new
        {
            count = list.Count,
            host = s.EffectiveHost,
            ports = $"{s.PortStart}-{s.PortStart + s.PortCount - 1}",
            instances = list,
            hint = list.Count == 0
                ? "Start Revit with pyRevit, turn on the Routes server (pyRevit Settings, or 'pyrevit configs routes enable'), and reload pyRevit."
                : list.Count > 1 ? "Ask the user which port, then pass port=<port>." : null,
        };
    });

    [McpServerTool(Name = "revit_status", ReadOnly = true), Description(
        "Status of one Revit session (port optional when only one runs) plus the languages you can use, the command library folder, and the GitHub state.")]
    public Task<string> Status(
        [Description("Routes port of the Revit session. Optional when only one Revit runs.")] int? port = null,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var inst = await routes.ResolveAsync(port, ct);
        var s = McpSettings.Load();
        return new
        {
            instance = inst,
            languages = new
            {
                ironpython = "Live: revit_execute_python. Engine: " + (inst.Python ?? "unknown") + ". Check the version before you use Python 3 syntax.",
                csharp = inst.CSharpRunner ? "Live: revit_execute_csharp (Roslyn inside the FirstOption add-in)." : "Not live here: the FirstOption Revit add-in is not loaded. C# can still be saved as a pyRevit script.cs button.",
                cpython = "pyRevit buttons only ('#! python3'). Not live through the MCP.",
            },
            library = s.EffectiveLibraryPath,
            github = s.GitHubConfigured ? $"{s.GitHubOwner}/{s.GitHubRepo} ({s.EffectiveBranch}), autoPush={s.AutoPush}" : "not set",
        };
    });

    [McpServerTool(Name = "revit_execute_python"), Description(
        "Run Python code inside Revit (pyRevit engine, normally IronPython) on the Revit main thread. " +
        "Names you get: doc, uidoc, uiapp, app, DB (Autodesk.Revit.DB), UI (Autodesk.Revit.UI), args (dict from args_json). " +
        "print() output is returned. Set a variable named result to return a value. " +
        "By default the code runs inside one Transaction that commits on success and rolls back on error; pass use_transaction=false when the code opens its own transactions, or works on a family document it creates. " +
        "Search the command library (library_search) before you write new code.")]
    public Task<string> ExecutePython(
        McpServer server,
        [Description("Python source. Keep it IronPython-compatible unless revit_status shows a Python 3 engine.")] string code,
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("Wrap the code in one Transaction (default true).")] bool use_transaction = true,
        [Description("Name of the Transaction in the Revit undo list.")] string transaction_name = null,
        [Description("JSON object that the code reads as args.")] string args_json = null,
        [Description("Seconds to wait for Revit (default 120).")] int timeout_seconds = 120,
        CancellationToken ct = default) =>
        Json.Guard(() => ExecuteAsync(routes, server, "ironpython", code, null, port, use_transaction, transaction_name, args_json, timeout_seconds, ct));

    [McpServerTool(Name = "revit_execute_csharp"), Description(
        "Compile and run C# inside Revit with Roslyn (needs the FirstOption Revit add-in). Write a method BODY, not a class: " +
        "you get uiapp, uidoc, doc, app, args (IDictionary<string, object>) and Console (a TextWriter; Console.WriteLine output is returned). " +
        "'return x;' returns a value. Put extra 'using X;' lines at the top. Use local functions for helpers. " +
        "Default usings: System, System.Collections.Generic, System.Linq, System.Text, Autodesk.Revit.DB, Autodesk.Revit.UI, Autodesk.Revit.UI.Selection. " +
        "Transactions work as in revit_execute_python. Compile errors come back with line numbers of your body.")]
    public Task<string> ExecuteCSharp(
        McpServer server,
        [Description("C# method body.")] string code,
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("Wrap the code in one Transaction (default true).")] bool use_transaction = true,
        [Description("Name of the Transaction in the Revit undo list.")] string transaction_name = null,
        [Description("JSON object that the code reads as args.")] string args_json = null,
        [Description("Seconds to wait for Revit (default 120).")] int timeout_seconds = 120,
        CancellationToken ct = default) =>
        Json.Guard(() => ExecuteAsync(routes, server, "csharp", code, null, port, use_transaction, transaction_name, args_json, timeout_seconds, ct));

    internal static async Task<object> ExecuteAsync(RoutesClient routes, McpServer server, string language, string code, string commandName,
        int? port, bool useTransaction, string transactionName, string argsJson, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ToolError("The code is empty.");

        JsonNode args = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { args = JsonNode.Parse(argsJson); }
            catch (JsonException e) { throw new ToolError("args_json is not valid JSON: " + e.Message); }
            if (args is not JsonObject) throw new ToolError("args_json must be a JSON object, for example {\"level\": \"Level 1\"}.");
        }

        var inst = await routes.ResolveAsync(port, ct);
        if (language == "csharp" && !inst.CSharpRunner)
            throw new ToolError($"The C# runner is not loaded in Revit {inst.RevitVersion} (port {inst.Port}).",
                "Install the FirstOption MCP Revit add-in for this Revit version and restart Revit. Until then use revit_execute_python.");

        var client = ClientName(server);
        var sw = Stopwatch.StartNew();
        JsonObject r;
        try
        {
            r = await routes.PostAsync(inst.Port, language == "csharp" ? "execute-csharp" : "execute", new
            {
                code,
                use_transaction = useTransaction,
                transaction_name = string.IsNullOrWhiteSpace(transactionName) ? "FirstOption MCP" + (commandName != null ? ": " + commandName : "") : transactionName,
                args,
            }, timeoutSeconds, ct);
        }
        catch (ToolError e)
        {
            Log(client, inst, language, commandName, code, null, e.Message, false, RoutesClient.Elapsed(sw), inst.Document);
            throw;
        }

        var ok = r["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var error = Join(r["error"]?.ToString(), r["traceback"]?.ToString(), r["diagnostics"] is JsonArray d ? string.Join("\n", d.Select(x => x?.ToString())) : null);
        var duration = r["durationMs"] is JsonValue dv && dv.TryGetValue<long>(out var ms) ? ms : RoutesClient.Elapsed(sw);
        Log(client, inst, language, commandName, code, r["output"]?.ToString(), error, ok, duration, r["document"]?.ToString() ?? inst.Document);

        r["port"] = inst.Port;
        r["revitVersion"] ??= inst.RevitVersion;
        if (!ok) r["hint"] ??= "Read the error, fix the code, and run it again. Nothing was committed when the transaction rolled back.";
        return r;
    }

    internal static string ClientName(McpServer server)
    {
        var name = server?.ClientInfo?.Name ?? "agent";
        if (name.Contains("claude", StringComparison.OrdinalIgnoreCase)) return "claude";
        if (name.Contains("codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        return name;
    }

    private static void Log(string client, RevitInstance inst, string language, string commandName, string code, string output, string error, bool ok, long durationMs, string document)
    {
        try
        {
            ActivityLog.Append(new ActivityEntry
            {
                Kind = ActivityKinds.Execute,
                Client = client,
                Port = inst.Port,
                Language = language,
                CommandName = commandName,
                Code = code,
                Output = output,
                Error = error,
                Ok = ok,
                DurationMs = durationMs,
                Document = document,
            });
        }
        catch
        {
            // the log is for the Revit panel; never fail a run because of it
        }
    }

    private static string Join(params string[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? null : string.Join("\n", list);
    }
}
