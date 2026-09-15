using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Server;

public sealed class RevitInstance
{
    public int Port { get; set; }
    public int Pid { get; set; }
    public string RevitVersion { get; set; }
    public string RevitBuild { get; set; }
    public string Document { get; set; }
    public string PyRevit { get; set; }
    public string Python { get; set; }
    public bool CSharpRunner { get; set; }
    public bool UndoJournal { get; set; }
    public string Bridge { get; set; }
}

/// <summary>HTTP client for the pyRevit Routes API "fo-mcp" that the FirstOptionMCP.extension registers.</summary>
public sealed class RoutesClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<List<RevitInstance>> ProbeAsync(CancellationToken ct)
    {
        var s = McpSettings.Load();
        var ports = Enumerable.Range(s.PortStart, s.PortCount);
        var found = await Task.WhenAll(ports.Select(p => ProbeOneAsync(s.EffectiveHost, p, ct)));
        return found.Where(i => i != null).OrderBy(i => i.Port).ToList();
    }

    public async Task<RevitInstance> ResolveAsync(int? port, CancellationToken ct)
    {
        var s = McpSettings.Load();
        if (port.HasValue)
        {
            return await ProbeOneAsync(s.EffectiveHost, port.Value, ct)
                ?? throw new ToolError(
                    $"No FirstOption MCP bridge answers on {s.EffectiveHost}:{port}.",
                    "Call revit_instances to see the live ports. The Revit panel (First Option > AI Bridge > MCP Panel) shows the port of that Revit.");
        }

        var all = await ProbeAsync(ct);
        if (all.Count == 1) return all[0];
        if (all.Count == 0)
            throw new ToolError(
                $"No Revit answers on {s.EffectiveHost}:{s.PortStart}-{s.PortStart + s.PortCount - 1}.",
                "Start Revit with pyRevit. In pyRevit Settings turn on the Routes server, or run 'pyrevit configs routes enable', then reload pyRevit. The FirstOptionMCP.extension must be in the pyRevit extension paths.");
        throw new ToolError(
            "More than one Revit is running: ports " + string.Join(", ", all.Select(i => $"{i.Port} ({i.Document ?? "no document"}, Revit {i.RevitVersion})")) + ".",
            "Ask the user which Revit to use, then pass port=<port>.");
    }

    public async Task<JsonObject> PostAsync(int port, string route, object body, int timeoutSeconds, CancellationToken ct)
    {
        var s = McpSettings.Load();
        var url = $"http://{s.EffectiveHost}:{port}/{AppPaths.RoutesApiName}/{route}/";
        var json = JsonSerializer.Serialize(body, Json.Options);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");   // pyRevit parses the body only for this exact value

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 3600)));
        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(url, content, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolError(
                $"Revit did not answer in {timeoutSeconds} s.",
                "Revit can be busy, or a modal dialog is open in Revit. A dialog blocks the Revit API. Ask the user to close it. The code can still finish later; check the model before you run it again.");
        }
        catch (HttpRequestException e)
        {
            throw new ToolError("Cannot connect to Revit on port " + port + ": " + e.Message, "Call revit_instances.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            JsonNode node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException) { node = null; }

            if (node is JsonObject obj)
            {
                if (!response.IsSuccessStatusCode && obj["ok"] == null) obj["ok"] = false;
                return obj;
            }
            throw new ToolError($"pyRevit Routes returned HTTP {(int)response.StatusCode}: {Trim(text, 1500)}",
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "The route is missing. Update FirstOptionMCP.extension and reload pyRevit."
                    : null);
        }
    }

    private static async Task<RevitInstance> ProbeOneAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            var text = await Http.GetStringAsync($"http://{host}:{port}/{AppPaths.RoutesApiName}/status/", cts.Token);
            var n = JsonNode.Parse(text);
            if (n?["bridge"] == null) return null;
            return new RevitInstance
            {
                Port = port,
                Pid = n["pid"]?.GetValue<int>() ?? 0,
                RevitVersion = n["revitVersion"]?.ToString(),
                RevitBuild = n["revitBuild"]?.ToString(),
                Document = n["document"]?.ToString(),
                PyRevit = n["pyrevit"]?.ToString(),
                Python = n["python"]?.ToString(),
                CSharpRunner = n["csharpRunner"]?.GetValue<bool>() ?? false,
                UndoJournal = n["undoJournal"]?.GetValue<bool>() ?? false,
                Bridge = n["bridge"]?.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    public static string Trim(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

    public static long Elapsed(Stopwatch sw) => (long)sw.Elapsed.TotalMilliseconds;
}
