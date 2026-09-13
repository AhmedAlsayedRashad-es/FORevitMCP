namespace FirstOption.RevitMcp.Server;

public static class ToolDocs
{
    public const string Instructions =
@"FirstOption Revit MCP runs code inside a live Revit through pyRevit Routes, and keeps a library of commands that worked.

Languages. pyRevit runs three kinds of scripts, and you can use all of them:
- IronPython (pyRevit default engine): live through revit_execute_python.
- C# (.NET): live through revit_execute_csharp (Roslyn in the FirstOption Revit add-in). Choose C# for heavy geometry, large loops, strong typing, or when a sample is in C#.
- CPython 3 ('#! python3' first line): pyRevit buttons only; use it for numpy-style libraries. Not live through this MCP.
Search the web and the Revit API docs for C# samples too; they map directly to revit_execute_csharp.

Workflow:
1. revit_instances (ask the user for the port when more than one Revit runs).
2. library_search before you write code. Reuse with library_run, or read with library_get and adapt.
3. Run small steps. Read output and errors. Fix and run again.
4. When the code works, library_save it with a clear description, inputs and tags.
5. github_push when the user wants to share (or auto-push does it).

Rules:
- Revit units are internal feet. Convert with UnitUtils or divide mm by 304.8.
- The code runs in one Transaction by default. Pass use_transaction=false when the code opens its own transactions or creates/edits a family document.
- A modal dialog in Revit blocks the API. When a call times out, ask the user to check Revit.
- Do not delete or purge elements the user did not ask for.";
}
