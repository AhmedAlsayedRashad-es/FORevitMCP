using FirstOption.RevitMcp.Server;
using FirstOption.RevitMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// FirstOption.RevitMcp — verbs:
//   (none) | serve   the MCP server over stdio (what Claude Code and Codex launch)
//   doctor           environment checks
//   version          print the version
var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";
switch (verb)
{
    case "serve":
        break;
    case "doctor":
        return await Doctor.RunAsync();
    case "version":
    case "--version":
        Console.WriteLine("FirstOption.RevitMcp " + Json.Version);
        return 0;
    default:
        Console.Error.WriteLine("unknown verb '" + verb + "'; use: serve | doctor | version");
        return 2;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout is the MCP channel
builder.Services.AddSingleton<RoutesClient>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "firstoption-revit", Version = Json.Version };
        o.ServerInstructions = ToolDocs.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<RevitTools>()
    .WithTools<LibraryTools>()
    .WithTools<GitHubTools>();

await builder.Build().RunAsync();
return 0;
