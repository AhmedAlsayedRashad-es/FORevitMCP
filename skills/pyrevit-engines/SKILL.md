---
name: pyrevit-engines
description: Choose the script language for Revit work. pyRevit runs IronPython, CPython 3, C# and VB.NET scripts, and the FirstOption MCP runs IronPython and C# live. Use when you search for Revit API samples or decide how to write a Revit command, so you know that C# samples work directly and when Python fits better.
---

# pyRevit script engines

## What pyRevit can run

| Engine | How pyRevit knows | Live through the FirstOption MCP |
|---|---|---|
| IronPython (2.7 default; 3.4 optional) | `script.py` | Yes: `revit_execute_python` |
| CPython 3 (pythonnet) | `script.py` with `#! python3` on line 1 | No. The library has no Revit tab, so do not save CPython commands |
| C# | `script.cs` with a class that implements `IExternalCommand` | Yes: `revit_execute_csharp` (Roslyn in the FirstOption add-in) |
| VB.NET | `script.vb` | No |

`revit_status` shows the Python engine of the bridge (`python`) and if the C# runner is ready (`csharpRunner`).

## Search for samples in both languages

Most Revit API samples (Autodesk docs, The Building Coder, forums, GitHub) are C#. You can run them almost as they are with `revit_execute_csharp`: take the method body, remove the `Transaction` block (the MCP opens one), and `return` the result.

Search terms that work: `Revit API C# <task>`, `pyRevit <task>`, `RevitAPI <ClassName> example`.

## Choose

- **C#**: the sample is C#; heavy geometry; thousands of elements; `out`/`ref` parameters; generic methods; interfaces such as `IFamilyLoadOptions`, `ISelectionFilter`, `IFailuresPreprocessor`; you want compile errors before the run.
- **IronPython**: short queries, parameter edits, fast trial and error.
- **CPython**: you need numpy, pandas, requests, or other CPython packages. Save it as a button; the user clicks it.

## C# to IronPython

| C# | IronPython |
|---|---|
| `new FilteredElementCollector(doc).OfClass(typeof(Wall))` | `DB.FilteredElementCollector(doc).OfClass(DB.Wall)` |
| `.Cast<Wall>().Where(w => w.Width > 1)` | `[w for w in collector if w.Width > 1]` |
| `new List<ElementId>()` | `from System.Collections.Generic import List` then `List[DB.ElementId]()` |
| `doc.LoadFamily(path, out Family fam)` | `import clr` / `ref = clr.Reference[DB.Family]()` / `doc.LoadFamily(path, ref)` / `fam = ref.Value` |
| `$"{a} {b}"` | `"{} {}".format(a, b)` (IronPython 2.7 has no f-strings) |
| `using (var t = new Transaction(doc, "x")) { t.Start(); ...; t.Commit(); }` | nothing: the MCP transaction wraps the code |
| `class X : IFamilyLoadOptions` | `class X(DB.IFamilyLoadOptions):` works, but C# is safer |

## IronPython traps

- Strings with non-ASCII text need `u"..."` in IronPython 2.7.
- Overloads: when IronPython picks the wrong overload, call `Method.Overloads[Type1, Type2](...)`, or use C#.
- `len()` does not work on every .NET collection; use `.Count` or `list(x)`.
