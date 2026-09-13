using System.Reflection;
using Microsoft.CodeAnalysis;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var addinDir = args.Length > 0 ? args[0] : Path.Combine(root, "src", "Addin", "bin", "Release", "R2026");
var revitApiDir = args.Length > 1
    ? args[1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "revit_all_main_versions_api_x64", "2026.0.0", "lib", "net8.0");

var failures = 0;
void Check(string name, bool ok, object detail = null)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : "  -> " + detail));
    if (!ok) failures++;
}

var addin = Assembly.LoadFrom(Path.Combine(addinDir, "FirstOption.RevitMcp.Addin.dll"));
var runner = addin.GetType("FirstOption.RevitMcp.Addin.CSharp.CSharpRunner", true)!;
Check("RunJson(object, string) is public static and returns string",
    runner.GetMethod("RunJson", BindingFlags.Public | BindingFlags.Static, new[] { typeof(object), typeof(string) })?.ReturnType == typeof(string));

// References: the .NET 8 framework of this process + the Revit 2026 API (metadata only).
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
    .Concat(new[] { Path.Combine(revitApiDir, "RevitAPI.dll"), Path.Combine(revitApiDir, "RevitAPIUI.dll") })
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
    .ToList();

var emit = runner.GetMethod("Emit", BindingFlags.NonPublic | BindingFlags.Static)!;
byte[] Emit(string code, out List<string> diagnostics)
{
    var parameters = new object[] { code, references, null };
    var bytes = (byte[])emit.Invoke(null, parameters);
    diagnostics = (List<string>)parameters[2];
    return bytes;
}

// 1. A body that uses Revit API types, an extra using, LINQ, a local function and Console compiles.
var b1 = Emit("// count walls\n" +
              "using Autodesk.Revit.DB.Structure;\n" +
              "double Ft(double mm) => mm / 304.8;\n" +
              "var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();\n" +
              "var t = StructuralType.NonStructural;\n" +
              "Console.WriteLine($\"{walls.Count} {t} {Ft(1000):0.00} {args.Count} {uidoc?.Document?.Title}\");\n" +
              "return walls.Select(w => w.Id).ToList();", out var d1);
Check("Revit API body compiles", b1 != null, string.Join(" | ", d1));

// 2. A body that always returns still compiles (the wrapper's trailing return is only a warning).
var b2 = Emit("return 42;", out var d2);
Check("body with only 'return' compiles", b2 != null, string.Join(" | ", d2));

// 3. Compile errors point at the line of the agent's code (usings count as lines).
var b3 = Emit("using System.IO;\nvar a = 1;\nvar b = a +;\nreturn b;", out var d3);
Check("compile error is reported", b3 == null && d3.Count > 0, string.Join(" | ", d3));
Check("error line number is the body line (3)", d3.Any(x => x.StartsWith("line 3,")), string.Join(" | ", d3));

// 4. Unknown Revit member gives a readable error.
var b4 = Emit("var x = doc.NoSuchMember;\nreturn x;", out var d4);
Check("unknown member error names CS1061", b4 == null && d4.Any(x => x.Contains("CS1061") && x.StartsWith("line 1,")), string.Join(" | ", d4));

// 5. The emitted assembly has the Script.Run method the runner invokes.
if (b1 != null)
{
    using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(b1));
    var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
    var hasRun = reader.MethodDefinitions.Any(h => reader.GetString(reader.GetMethodDefinition(h).Name) == "Run");
    var hasScript = reader.TypeDefinitions.Any(h => reader.GetString(reader.GetTypeDefinition(h).Name) == "Script");
    Check("emitted assembly has FoMcpDynamic.Script.Run", hasRun && hasScript);
}

// 6. MiniJson round trip used by RunJson.
var mini = addin.GetType("FirstOption.RevitMcp.Addin.MiniJson", true)!;
var parsed = mini.GetMethod("Parse")!.Invoke(null, new object[] { "{\"code\":\"x\\n\\\"q\\\" \\u00e9\",\"use_transaction\":false,\"args\":{\"n\":3,\"f\":1.5,\"l\":[1,\"a\",null]}}" }) as Dictionary<string, object>;
Check("MiniJson parses", parsed != null && (string)parsed["code"] == "x\n\"q\" é" && (bool)parsed["use_transaction"] == false
                         && ((Dictionary<string, object>)parsed["args"])["n"] is long && ((Dictionary<string, object>)parsed["args"])["f"] is double, parsed);
var written = (string)mini.GetMethod("Write")!.Invoke(null, new object[] { parsed })!;
Check("MiniJson writes", written.Contains("\"use_transaction\":false") && written.Contains("\\\"q\\\"") && written.Contains("[1,\"a\",null]"), written);

Console.WriteLine(failures == 0 ? "\nALL PASSED" : "\nFAILED: " + failures);
return failures == 0 ? 0 : 1;
