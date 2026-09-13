---
name: revit-mcp
description: Work in a live Autodesk Revit model through the FirstOption Revit MCP (pyRevit Routes). Use when the user asks to create, change, query, or check anything in Revit (walls, views, sheets, parameters, families, 3D geometry) and the firstoption-revit tools (revit_*, library_*, github_*) are available.
---

# Revit through the FirstOption MCP

The MCP sends your code to a running Revit. pyRevit Routes runs it on the Revit main thread and returns the output.

## The loop

1. Call `revit_instances`. When more than one Revit runs, ask the user for the port.
2. Call `library_search` with 2-3 words (verb + element). When a command fits, `library_get` it, then `library_run` it with `args_json`.
3. Otherwise write code. Start with a read-only step (collect, count, print). Change the model only after you know what is there.
4. Run with `revit_execute_python` or `revit_execute_csharp`. Read `output`, `error`, `traceback` or `diagnostics`. Fix and run again.
5. When the code works, save it with `library_save` (see the `revit-command-library` skill).

## Choose the language

pyRevit runs IronPython, CPython 3 and C#. This MCP runs IronPython and C# live. See the `pyrevit-engines` skill.

- C# (`revit_execute_csharp`): Revit API samples in C#, heavy geometry, many elements, `out`/`ref` or generic methods.
- IronPython (`revit_execute_python`): quick queries and small edits.

## Rules

- Revit internal units are feet. `mm / 304.8 = ft`.
- The code runs inside one Transaction by default. It commits on success and rolls back on error.
- Pass `use_transaction=false` when the code opens its own transactions, creates or edits a family document, or calls `doc.EditFamily`.
- Do not open dialogs (`TaskDialog.Show`, pyRevit `forms`, WinForms). A dialog blocks the call until the user closes it.
- Print short summaries. The output is cut at 50 000 characters.
- When a call times out, ask the user to look at Revit. Do not run a change again blindly; it can make duplicates.
- Do not delete, purge, sync with central, or save the user's model unless the user asks.
- ElementId: `id.Value` in Revit 2024+, `id.IntegerValue` in Revit 2021-2023.

## Python names you get

`doc`, `uidoc`, `uiapp`, `app`, `DB` (Autodesk.Revit.DB), `UI` (Autodesk.Revit.UI), `args` (dict). Set `result` to return a value.

```python
walls = DB.FilteredElementCollector(doc).OfClass(DB.Wall).WhereElementIsNotElementType().ToElements()
print("walls: {}".format(len(walls)))
result = [w.Name for w in walls][:20]
```

## C# body you write

Write a method body, not a class. You get `uiapp`, `uidoc`, `doc`, `app`, `args` (IDictionary<string, object>) and `Console` (a TextWriter). Put extra `using` lines at the top. Use local functions for helpers.

```csharp
using Autodesk.Revit.DB.Structure;
var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
foreach (var l in levels) Console.WriteLine($"{l.Name}: {l.Elevation * 304.8:0} mm");
return levels.Count;
```

## Errors

| Message | What to do |
|---|---|
| No Revit answers on 127.0.0.1:48884-48893 | pyRevit Routes is off, or the extension is not loaded. Ask the user to turn on Routes in pyRevit Settings and reload pyRevit. |
| The C# runner is not loaded | The FirstOption Revit add-in is not installed for this Revit version. Use Python. |
| Starting a new transaction is not permitted | Your code opens a transaction inside the MCP transaction. Run again with `use_transaction=false`. |
| The transaction ended with status RolledBack | Revit refused the change (a failure message). Check the input and the model state. |
| Revit did not answer in N s | Revit is busy or a dialog is open. Ask the user. |
