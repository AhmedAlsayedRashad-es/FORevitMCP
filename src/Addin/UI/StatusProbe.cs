using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FirstOption.RevitMcp.Shared;

namespace FirstOption.RevitMcp.Addin.UI
{
    internal sealed class BridgeStatus
    {
        public int Port;
        public int Pid;
        public string RevitVersion;
        public string Document;
        public string PyRevit;
        public string Python;
        public bool CSharpRunner;
    }

    internal sealed class ProbeResult
    {
        public BridgeStatus Mine;
        public List<BridgeStatus> Others = new List<BridgeStatus>();
    }

    /// <summary>
    /// Asks every Routes port for /fo-mcp/status/ and keeps the one whose process id is this Revit.
    /// The status route does not need the Revit API context, so calling it from Revit does not block.
    /// Always call from a background thread.
    /// </summary>
    internal static class StatusProbe
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMilliseconds(1200),
        };

        public static ProbeResult Find(McpSettings settings, int pid)
        {
            var tasks = Enumerable.Range(settings.PortStart, settings.PortCount)
                .Select(port => ProbeAsync(settings.EffectiveHost, port))
                .ToArray();
            Task.WaitAll(tasks);

            var result = new ProbeResult();
            foreach (var status in tasks.Select(t => t.Result).Where(s => s != null))
            {
                if (status.Pid == pid && result.Mine == null) result.Mine = status;
                else result.Others.Add(status);
            }
            return result;
        }

        private static async Task<BridgeStatus> ProbeAsync(string host, int port)
        {
            try
            {
                var text = await Http.GetStringAsync("http://" + host + ":" + port + "/" + AppPaths.RoutesApiName + "/status/").ConfigureAwait(false);
                if (!(MiniJson.Parse(text) is Dictionary<string, object> d) || !d.ContainsKey("bridge")) return null;
                return new BridgeStatus
                {
                    Port = port,
                    Pid = d.TryGetValue("pid", out var p) && p is long l ? (int)l : 0,
                    RevitVersion = d.TryGetValue("revitVersion", out var v) ? v as string : null,
                    Document = d.TryGetValue("document", out var doc) ? doc as string : null,
                    PyRevit = d.TryGetValue("pyrevit", out var pr) ? pr as string : null,
                    Python = d.TryGetValue("python", out var py) ? py as string : null,
                    CSharpRunner = d.TryGetValue("csharpRunner", out var cs) && cs is bool b && b,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
