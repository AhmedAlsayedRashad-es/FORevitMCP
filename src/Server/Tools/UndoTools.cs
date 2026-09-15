using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using FirstOption.RevitMcp.Shared;
using ModelContextProtocol.Server;

namespace FirstOption.RevitMcp.Server.Tools;

[McpServerToolType]
public sealed class UndoTools(RoutesClient routes)
{
    [McpServerTool(Name = "revit_undo_history", ReadOnly = true), Description(
        "The Revit undo list of the active document, as the FirstOption add-in tracked it, top entry first: agent runs (runId, name, element changes) and changes by the user or other add-ins, done or undone, the baseline, and whether Revit cleared the list (Synchronize with Central). " +
        "For undone entries it checks that added elements are gone and deleted or modified elements exist again. Call it before revit_undo, and after the user undid something by hand.")]
    public Task<string> History(
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("Maximum entries (default 30).")] int limit = 30,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var inst = await RequireJournal(port, ct);
        var r = await routes.PostAsync(inst.Port, "undo-history", new { limit }, 60, ct);
        r["port"] = inst.Port;
        return r;
    });

    [McpServerTool(Name = "revit_baseline"), Description(
        "Mark the current state of the active document. Later, revit_undo with to_baseline=true undoes every change made after this point. Call it before a task with several steps that change the model.")]
    public Task<string> Baseline(
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var inst = await RequireJournal(port, ct);
        var r = await routes.PostAsync(inst.Port, "undo-baseline", new { }, 60, ct);
        r["port"] = inst.Port;
        return r;
    });

    [McpServerTool(Name = "revit_undo"), Description(
        "Undo agent runs in Revit in the correct order and check the result. Every agent run is one entry in the Revit undo list. " +
        "Give one target: runs (the last N agent runs, default 1), to_run_id (that run and everything after it), or to_baseline (everything after revit_baseline). " +
        "It refuses when changes by the user lie above the target, because Undo removes them too: ask the user, then pass include_user_changes=true. " +
        "mode=auto presses Undo in Revit one step at a time, stops at any step it did not expect, and checks the elements. mode=manual changes nothing and returns instructions for the user. " +
        "Never reverse a run by writing new code.")]
    public Task<string> Undo(
        McpServer server,
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("Undo the last N agent runs (default 1).")] int? runs = null,
        [Description("Undo this run (runId from the run answer or revit_undo_history) and everything after it.")] string to_run_id = null,
        [Description("Undo everything after revit_baseline.")] bool to_baseline = false,
        [Description("Allow the undo to also remove changes that are not from the agent. Only after the user agrees.")] bool include_user_changes = false,
        [Description("auto (press Undo in Revit) or manual (instructions only).")] string mode = "auto",
        [Description("Seconds to wait for the undo to finish (default 180).")] int timeout_seconds = 180,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var targets = (runs.HasValue ? 1 : 0) + (string.IsNullOrWhiteSpace(to_run_id) ? 0 : 1) + (to_baseline ? 1 : 0);
        if (targets > 1) throw new ToolError("Give only one target: runs, to_run_id or to_baseline.");
        mode = (mode ?? "auto").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "manual")) throw new ToolError("mode must be auto or manual.");

        var inst = await RequireJournal(port, ct);
        var client = RevitTools.ClientName(server);
        var sw = Stopwatch.StartNew();
        var what = to_baseline ? "to baseline" : !string.IsNullOrWhiteSpace(to_run_id) ? "to " + to_run_id : (runs ?? 1) + " run(s)";

        var plan = await routes.PostAsync(inst.Port, "undo", new
        {
            runs = runs ?? 1,
            toRunId = string.IsNullOrWhiteSpace(to_run_id) ? null : to_run_id.Trim(),
            toBaseline = to_baseline,
            includeUserChanges = include_user_changes,
            execute = mode == "auto",
        }, 60, ct);
        plan["port"] = inst.Port;

        if (!IsOk(plan) || mode == "manual" || plan["nothingToUndo"] != null)
        {
            Log(ActivityKinds.Undo, client, inst, IsOk(plan) && mode == "auto", "undo " + what + " (" + mode + ")", plan, sw);
            return plan;
        }

        JsonObject status;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeout_seconds, 10, 1800));
        while (true)
        {
            await Task.Delay(700, ct);
            try
            {
                status = await routes.PostAsync(inst.Port, "undo-status", new { }, 60, ct);
            }
            catch (ToolError e)
            {
                status = new JsonObject { ["ok"] = false, ["state"] = "unknown", ["error"] = e.Message, ["hint"] = "Revit is busy. Call revit_undo_history to see where the undo is." };
                break;
            }
            if (status["state"]?.ToString() != "running") break;
            if (DateTime.UtcNow > deadline)
            {
                status["hint"] = "The undo is still running in Revit. Call revit_undo_history to follow it.";
                break;
            }
        }

        var state = status["state"]?.ToString();
        var verified = status["verification"]?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        status["ok"] = state == "done" && verified;
        status["plan"] = plan["plan"]?.DeepClone();
        status["port"] = inst.Port;
        if (state == "done" && !verified)
            status["hint"] ??= "Revit undid the planned entries, but the check found elements that are not back. Show 'verification' to the user.";
        else if (state == "failed")
            status["hint"] ??= "The undo stopped. Show 'error' and 'instructions' to the user, and call revit_undo_history.";

        Log(ActivityKinds.Undo, client, inst, status["ok"]!.GetValue<bool>(), "undo " + what + " (auto): " + state, status, sw);
        return status;
    });

    [McpServerTool(Name = "revit_reset"), Description(
        "Close the active model WITHOUT saving and open its last saved file again. Every change since the last save is lost, also the changes of the user, and the undo list is cleared. " +
        "Use revit_undo first; use this only when the user asks for it. Without confirm=true it changes nothing and tells you what would be lost. Not for workshared or never-saved models.")]
    public Task<string> Reset(
        McpServer server,
        [Description("Routes port. Optional when only one Revit runs.")] int? port = null,
        [Description("true only after the user agreed to lose all unsaved changes.")] bool confirm = false,
        [Description("Seconds to wait for Revit (default 600).")] int timeout_seconds = 600,
        CancellationToken ct = default) => Json.Guard(async () =>
    {
        var inst = await RequireJournal(port, ct);
        if (!confirm)
        {
            var h = await routes.PostAsync(inst.Port, "undo-history", new { limit = 20 }, 60, ct);
            return new
            {
                ok = false,
                needsConfirmation = true,
                document = h["document"]?.ToString() ?? inst.Document,
                message = "revit_reset closes the model without saving and opens the last saved file. Every unsaved change is lost, also the user's own changes, and the undo list is cleared. " +
                          "Prefer revit_undo. Ask the user, and call again with confirm=true only after the user agrees.",
                undoList = h["entries"]?.DeepClone(),
            };
        }

        var client = RevitTools.ClientName(server);
        var sw = Stopwatch.StartNew();
        var r = await routes.PostAsync(inst.Port, "reset", new { }, timeout_seconds, ct);
        r["port"] = inst.Port;
        Log(ActivityKinds.Reset, client, inst, IsOk(r), "reset: reopen the last saved file", r, sw);
        return r;
    });

    private async Task<RevitInstance> RequireJournal(int? port, CancellationToken ct)
    {
        var inst = await routes.ResolveAsync(port, ct);
        if (!inst.UndoJournal)
            throw new ToolError($"Undo tracking is not loaded in Revit {inst.RevitVersion} (port {inst.Port}).",
                "It needs the updated FirstOption MCP Revit add-in and FirstOptionMCP.extension: run scripts\\install.ps1 and restart Revit. Until then the user can press Undo in Revit by hand.");
        return inst;
    }

    private static bool IsOk(JsonObject r) => r["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static void Log(string kind, string client, RevitInstance inst, bool ok, string message, JsonObject result, Stopwatch sw)
    {
        try
        {
            ActivityLog.Append(new ActivityEntry
            {
                Kind = kind,
                Client = client,
                Port = inst.Port,
                CommandName = kind,
                Message = message,
                Output = result.ToJsonString(Json.Indented),
                Error = result["error"]?.ToString(),
                Ok = ok,
                DurationMs = RoutesClient.Elapsed(sw),
                Document = result["document"]?.ToString() ?? inst.Document,
            });
        }
        catch
        {
            // the log is for the Revit panel; never fail the tool because of it
        }
    }
}
