using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FirstOption.RevitMcp.Addin.CSharp
{
    /// <summary>
    /// Compiles and runs a C# method body inside Revit. The pyRevit bridge (startup.py) finds this type by
    /// reflection and calls RunJson on the Revit main thread (inside a pyRevit Routes handler that asks for uiapp).
    /// Keep the signature stable: public static string RunJson(object uiapp, string requestJson).
    /// </summary>
    public static class CSharpRunner
    {
        private static readonly string[] DefaultUsings =
        {
            "using System;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            "using System.Text;",
            "using Autodesk.Revit.DB;",
            "using Autodesk.Revit.UI;",
            "using Autodesk.Revit.UI.Selection;",
        };

        private static readonly object Gate = new object();
        private static List<MetadataReference> _references;
        private static int _referencesAssemblyCount;

        public static string RunJson(object uiappObject, string requestJson)
        {
            var sw = Stopwatch.StartNew();
            var response = new Dictionary<string, object> { ["language"] = "csharp" };
            var output = new StringWriter();
            var ok = true;
            try
            {
                var uiapp = (UIApplication)uiappObject;
                var request = MiniJson.Parse(requestJson) as Dictionary<string, object> ?? new Dictionary<string, object>();
                var code = request.TryGetValue("code", out var c) ? c as string ?? "" : "";
                var useTransaction = !request.TryGetValue("use_transaction", out var u) || !(u is bool) || (bool)u;
                var transactionName = request.TryGetValue("transaction_name", out var t) && t is string ts && ts.Length > 0 ? ts : "FirstOption MCP";
                var args = request.TryGetValue("args", out var a) && a is Dictionary<string, object> ad ? ad : new Dictionary<string, object>();

                var uidoc = uiapp.ActiveUIDocument;
                var doc = uidoc?.Document;
                response["document"] = doc?.Title;

                var method = Compile(code, out var diagnostics);
                if (method == null)
                {
                    ok = false;
                    response["error"] = "C# compile failed (" + diagnostics.Count + " error(s)). Line numbers are lines of your code.";
                    response["diagnostics"] = diagnostics;
                }
                else
                {
                    Transaction tx = null;
                    try
                    {
                        if (useTransaction && doc != null && !doc.IsModifiable)
                        {
                            tx = new Transaction(doc, transactionName);
                            tx.Start();
                            response["transaction"] = transactionName;
                        }

                        object result;
                        try
                        {
                            result = method.Invoke(null, new object[] { uiapp, uidoc, doc, uiapp.Application, args, output });
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                            throw;
                        }

                        if (tx != null)
                        {
                            var status = tx.Commit();
                            if (status != TransactionStatus.Committed)
                            {
                                ok = false;
                                response["error"] = "The transaction ended with status " + status;
                            }
                        }
                        response["result"] = Describe(result, 0);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        if (tx != null && tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        response["error"] = ex.GetType().Name + ": " + ex.Message;
                        response["traceback"] = ex.ToString();
                    }
                    finally
                    {
                        tx?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                response["error"] = ex.GetType().Name + ": " + ex.Message;
                response["traceback"] = ex.ToString();
            }

            response["ok"] = ok;
            response["output"] = output.ToString();
            response["durationMs"] = (long)sw.Elapsed.TotalMilliseconds;
            return MiniJson.Write(response);
        }

        private static MethodInfo Compile(string code, out List<string> diagnostics)
        {
            for (var attempt = 0; ; attempt++)
            {
                var bytes = Emit(code, References(), out diagnostics);
                if (bytes != null) return Assembly.Load(bytes).GetType("FoMcpDynamic.Script").GetMethod("Run");

                // CS0009: another add-in ships an assembly that Roslyn cannot read (obfuscated, bad strong-name key).
                // Drop it from the references and compile again.
                var bad = diagnostics.Where(d => d.StartsWith("CS0009"))
                    .Select(d => BadMetadata.Match(d)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
                if (bad.Count == 0 || attempt >= 5) return null;
                lock (Gate)
                {
                    foreach (var path in bad) Excluded.Add(path);
                    _references = null;
                }
            }
        }

        private static readonly Regex BadMetadata = new Regex("Metadata file '(.+?)' could not be opened");
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Wraps the body and compiles it. Returns the assembly bytes, or null with the errors. Separate from loading so tests can run it outside Revit.</summary>
        internal static byte[] Emit(string code, IEnumerable<MetadataReference> references, out List<string> diagnostics)
        {
            diagnostics = new List<string>();
            var lines = (code ?? "").Replace("\r\n", "\n").Split('\n');
            var usings = new List<string>();
            var first = 0;
            for (; first < lines.Length; first++)
            {
                var trimmed = lines[first].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("(")) { usings.Add(trimmed); continue; }
                break;
            }
            var body = string.Join("\n", lines.Skip(first));

            var source =
                string.Join("\n", DefaultUsings.Concat(usings).Distinct()) + "\n" +
                "namespace FoMcpDynamic {\n" +
                "public static class Script {\n" +
                "public static object Run(UIApplication uiapp, UIDocument uidoc, Document doc, Autodesk.Revit.ApplicationServices.Application app, IDictionary<string, object> args, System.IO.TextWriter Console) {\n" +
                "#line " + (first + 1) + " \"body.cs\"\n" +
                body + "\n" +
                "#line default\n" +
                "return null;\n}\n}\n}\n";

            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "FoMcpScript_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                if (emit.Success) return ms.ToArray();

                foreach (var d in emit.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Take(30))
                {
                    var pos = d.Location.GetMappedLineSpan();
                    diagnostics.Add((pos.IsValid && pos.Path == "body.cs" ? "line " + (pos.StartLinePosition.Line + 1) + ", col " + (pos.StartLinePosition.Character + 1) + ": " : "") + d.Id + ": " + d.GetMessage());
                }
                return null;
            }
        }

        /// <summary>Everything loaded in Revit (Revit API, .NET, other add-ins) can be used from a script.</summary>
        private static List<MetadataReference> References()
        {
            lock (Gate)
            {
                // make sure the common assemblies are loaded before we list them
                GC.KeepAlive(typeof(Enumerable));
                GC.KeepAlive(typeof(Document));
                GC.KeepAlive(typeof(UIApplication));
                GC.KeepAlive(typeof(Uri));

                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                if (_references != null && loaded.Length == _referencesAssemblyCount) return _references;

                var byName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
                foreach (var asm in loaded)
                {
                    if (asm.IsDynamic) continue;
                    string location;
                    try { location = asm.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(location) || !File.Exists(location)) continue;
                    var name = asm.GetName();
                    if (name.Name.StartsWith("FoMcpScript_")) continue;
                    if (byName.TryGetValue(name.Name, out var existing) && existing.GetName().Version >= name.Version) continue;
                    byName[name.Name] = asm;
                }

                var paths = byName.Values.Select(x => x.Location).ToList();

                // .NET 8: add the framework reference facades (System.Runtime, netstandard, ...) that are not loaded yet
                if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                {
                    foreach (var path in tpa.Split(Path.PathSeparator))
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (byName.ContainsKey(name)) continue;
                        if (name.StartsWith("System.") || name == "netstandard" || name == "mscorlib" || name == "Microsoft.CSharp" || name == "Microsoft.Win32.Primitives")
                            paths.Add(path);
                    }
                }
                else
                {
                    // .NET Framework 4.8
                    var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                    foreach (var file in new[] { "netstandard.dll", "System.Runtime.dll", "Facades\\netstandard.dll", "Facades\\System.Runtime.dll" })
                    {
                        var path = Path.Combine(runtimeDir, file);
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (File.Exists(path) && !byName.ContainsKey(name) && !paths.Any(p => Path.GetFileNameWithoutExtension(p) == name)) paths.Add(path);
                    }
                }

                var references = new List<MetadataReference>();
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (Excluded.Contains(path)) continue;
                    try
                    {
                        AssemblyName.GetAssemblyName(path);   // skip native files
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch
                    {
                        // not a managed assembly
                    }
                }

                _references = references;
                _referencesAssemblyCount = loaded.Length;
                return _references;
            }
        }

        private static object Describe(object value, int depth)
        {
            switch (value)
            {
                case null: return null;
                case string _:
                case bool _:
                case int _:
                case long _:
                case double _:
                case float _:
                case decimal _:
                    return value;
                case ElementId id: return IdValue(id);
                case XYZ p: return new List<object> { p.X, p.Y, p.Z };
                case Element e:
                    return new Dictionary<string, object>
                    {
                        ["id"] = IdValue(e.Id),
                        ["name"] = Safe(() => e.Name),
                        ["category"] = Safe(() => e.Category?.Name),
                        ["class"] = e.GetType().Name,
                    };
            }
            if (depth > 3) return value.ToString();
            if (value is IDictionary dict)
            {
                var d = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in dict) d[Convert.ToString(kv.Key)] = Describe(kv.Value, depth + 1);
                return d;
            }
            if (value is IEnumerable list)
            {
                var items = new List<object>();
                foreach (var item in list)
                {
                    if (items.Count >= 500) break;
                    items.Add(Describe(item, depth + 1));
                }
                return items;
            }
            return value.ToString();
        }

#if REVIT2021 || REVIT2022 || REVIT2023
        private static object IdValue(ElementId id) => id.IntegerValue;
#else
        private static object IdValue(ElementId id) => id.Value;
#endif

        private static string Safe(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }
}
