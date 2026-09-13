using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirstOption.RevitMcp.Server;

public sealed class ToolError : Exception
{
    public string Hint { get; }
    public ToolError(string message, string hint = null) : base(message) { Hint = hint; }
}

public static class Json
{
    public static readonly string Version = typeof(Json).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Render(object value) => JsonSerializer.Serialize(value, Options);

    public static string Error(Exception e) => e switch
    {
        ToolError te => Render(new { ok = false, error = te.Message, hint = te.Hint }),
        _ => Render(new { ok = false, error = e.GetType().Name + ": " + e.Message }),
    };

    /// <summary>Runs a tool body and turns every exception into a JSON error, so the agent always gets a readable answer.</summary>
    public static async Task<string> Guard(Func<Task<object>> body)
    {
        try { return Render(await body()); }
        catch (Exception e) { return Error(e); }
    }
}
