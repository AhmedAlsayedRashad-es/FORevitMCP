# FirstOption Revit MCP

Claude Code and Codex CLI work in a live Revit model. The agent runs IronPython or C# inside Revit through pyRevit Routes, saves the code that works in a command library, and uploads the library to GitHub. A Revit panel on the **First Option** ribbon tab shows what happens.

```mermaid
flowchart LR
    A["Claude Code / Codex CLI"] -- "MCP (stdio)" --> S["FirstOption.RevitMcp.exe<br/>.NET 8 + MCP C# SDK"]
    S -- "HTTP JSON" --> R["pyRevit Routes<br/>fo-mcp API (startup.py)"]
    R -- "IronPython exec" --> M[("Revit model")]
    R -- "reflection" --> C["FirstOption add-in<br/>Roslyn C# runner"]
    C --> M
    S -- "files" --> L["Command library<br/>pyRevit extension + git repo"]
    S -- "git push" --> G["GitHub"]
    S -- "activity.jsonl" --> P["Revit panel<br/>First Option > AI Bridge"]
```

## What is in this folder

| Folder | What it is |
|---|---|
| `src/Server` | The MCP server (`FirstOption.RevitMcp.exe`), .NET 8, [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.2.0, stdio |
| `src/Addin` | The Revit add-in: ribbon tab "First Option", dockable MCP panel, GitHub Settings window, Roslyn C# runner. Revit 2020-2026 (2020: .NET Framework 4.7.2, 2021-2024: .NET Framework 4.8, 2025-2026: .NET 8) |
| `src/Shared` | Settings, paths and the activity log, shared by the server and the add-in |
| `pyrevit/FirstOptionMCP.extension` | The bridge: `startup.py` registers the pyRevit Routes API `fo-mcp` |
| `skills` | Agent skills for Claude Code and Codex (same `SKILL.md` format) |
| `scripts/install.ps1` | Builds and installs everything |
| `docs/REQUIRED-APPS.md` | The applications you need and how to install them |
| `mockups` | The Codex CLI prompt and script for the UI mockups |
| `tests` | Mock Routes server, MCP smoke test, C# runner test |

## 1. Install the applications

See [docs/REQUIRED-APPS.md](docs/REQUIRED-APPS.md): Revit, pyRevit (+ CLI), .NET 8 SDK, Git, Node.js, Claude Code, Codex CLI, GitHub CLI (optional).

## 2. Build and install

Close Revit and every Claude Code and Codex session (the script stops when they run). Then, in PowerShell, in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RegisterClaude -RegisterCodex
```

The script does these steps (use `-DryRun` to see them first, `-RevitVersions 2025,2026` to limit the versions):

1. Moves the files from the old folders of version 0.1.0 (`%LOCALAPPDATA%\FirstOption\RevitMCP`, `Documents\FirstOption\RevitCommandLibrary`, the add-in folders in `%APPDATA%\Autodesk\Revit\Addins`) and deletes those folders.
2. Publishes the MCP server to `%LOCALAPPDATA%\First Option\RevitMCP\Server`.
3. Builds the add-in for Revit 2020 to 2026, whether or not that Revit is on the computer, copies it to `%LOCALAPPDATA%\First Option\RevitMCP\Revit Add-in\<version>`, and writes `FirstOption.RevitMcp.addin` in `%APPDATA%\Autodesk\Revit\Addins\<version>`.
4. Copies the pyRevit bridge to `%LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge`, adds the bridge and the command library to the pyRevit extension paths, and turns on pyRevit Routes.
5. Copies the skills to `~\.claude\skills` and `~\.codex\skills`.
6. Registers the MCP server in Claude Code and Codex. A registration that already exists gets the new server path.

Run the script again after each change to the code. Revit and the agents use the installed copy, not this folder.

## 3. Register the MCP server by hand (when you did not use -RegisterClaude / -RegisterCodex)

### Claude Code

```powershell
claude mcp add --scope user firstoption-revit -- "$env:LOCALAPPDATA\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe"
claude mcp list
```

For one project only, use `--scope project`. Claude Code then writes `.mcp.json` in that project.

### Codex CLI

```powershell
codex mcp add firstoption-revit -- "$env:LOCALAPPDATA\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe"
codex mcp list
```

Revit work can take longer than the Codex default tool timeout. Add the timeouts in `~\.codex\config.toml`:

```toml
[mcp_servers.firstoption-revit]
command = 'C:\Users\<you>\AppData\Local\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe'
startup_timeout_sec = 20
tool_timeout_sec = 300
```

### Check

```powershell
& "$env:LOCALAPPDATA\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe" doctor
```

## 4. pyRevit setup (the script does this when the `pyrevit` CLI is on PATH)

```powershell
pyrevit extensions paths add "$env:LOCALAPPDATA\First Option\RevitMCP\pyRevit Bridge"
pyrevit extensions paths add "$env:LOCALAPPDATA\First Option\RevitMCP\Command Library"
pyrevit configs routes enable
```

Then start Revit, or click pyRevit > Reload. Windows Firewall can ask about Revit the first time Routes starts; allow it.

## 5. Skills

| Skill | Use |
|---|---|
| `revit-mcp` | The main loop: find Revit, search the library, run, fix, save. Rules and errors. |
| `pyrevit-engines` | pyRevit runs IronPython, CPython 3, C# and VB.NET. When to use C#, how to translate C# samples. |
| `revit-command-library` | Search before writing; how to save a command so other agents can use it. |
| `revit-families-and-3d` | DirectShape, family documents, load and place instances. |
| `revit-github-sync` | Push the library to GitHub. |

Copy them by hand:

```powershell
Copy-Item skills\* "$HOME\.claude\skills" -Recurse -Force
Copy-Item skills\* "$HOME\.codex\skills" -Recurse -Force
```

## 6. Use it

1. Start Revit and open a model.
2. Click **First Option > AI Bridge > MCP Panel**. The panel must show **pyRevit Routes online** and a port.
3. In Claude Code or Codex, ask for Revit work, for example:
   - "List the levels and the number of walls on each level."
   - "Create a 5 x 4 grid of structural columns, 6 m spacing, on Level 1. Save it as a command."
   - "Build a generic model family: a 1000 x 500 x 800 mm box with a Width parameter. Load it and place one at 0,0,0."
   - "Push the new commands to GitHub."

## MCP tools

| Tool | What it does |
|---|---|
| `revit_instances` | Lists the Revit sessions that answer (port, version, document, pyRevit, Python engine, C# runner) |
| `revit_status` | One session, plus the languages you can use, the library folder and the GitHub state |
| `revit_execute_python` | Runs Python in Revit (`doc`, `uidoc`, `uiapp`, `app`, `DB`, `UI`, `args`; `result`), inside one transaction by default |
| `revit_execute_csharp` | Compiles and runs a C# method body in Revit with Roslyn (`uiapp`, `uidoc`, `doc`, `app`, `args`, `Console`) |
| `revit_undo_history` | The Revit undo list as the add-in tracked it: agent runs and user changes, done or undone, with an element check for undone entries |
| `revit_baseline` | Marks the current state; `revit_undo to_baseline=true` goes back to it |
| `revit_undo` | Undoes the last agent runs (`runs`, `to_run_id` or `to_baseline`) in order, presses Undo in Revit step by step, and checks the elements; `mode=manual` gives instructions only |
| `revit_reset` | Closes the model without saving and opens the last saved file (asks for `confirm=true`) |
| `library_search` | Searches saved commands |
| `library_get` | Reads a command's metadata and code |
| `library_save` | Saves working code as a command (pyRevit button + metadata); auto-pushes when that is on |
| `library_run` | Runs a saved IronPython or C# command with `args_json` |
| `library_info` | Library folder, command count, how to show the buttons |
| `github_status` | GitHub settings and local git state |
| `github_push` | Commits and pushes the library; the Revit panel shows a notice |

## Undo agent runs

- Every run (`revit_execute_python`, `revit_execute_csharp`, `library_run`) is wrapped in a `TransactionGroup` and merged into **one** entry in the Revit undo list, named `FirstOption MCP #N`, `FirstOption MCP: <command_name> #N`, or `<transaction_name> #N`. `command_name` is required; the Revit MCP panel shows it. The answer has `runId`, `undoName` and the element counts.
- A failed run rolls back completely, also with `use_transaction=false`.
- The add-in follows the undo list of every open document (`DocumentChanged`: commit, undo, redo). It knows which entries are agent runs and which are changes by the user.
- `revit_undo` refuses when user changes lie above the target. With `include_user_changes=true` it goes on.
- In `mode=auto` it posts Revit's own Undo command one step at a time, checks the name of each undone entry, and stops at the first step it did not expect. Then it checks that added elements are gone and deleted or modified elements exist again.
- Undo cannot reverse: a save to disk, Synchronize with Central (it also clears the undo list), and changes in other documents. A run answer lists these in `sideEffects`.
- Pass `undo_group=false` only when the code must save, synchronize or close the document.

## Languages

pyRevit can run IronPython, CPython 3, C# and VB.NET scripts. Through this MCP:

| Language | Live run | Saved as |
|---|---|---|
| IronPython | `revit_execute_python` (the engine that loads `startup.py`) | `script.py` + `body.py` |
| C# | `revit_execute_csharp` (needs the add-in) | `script.cs` (pyRevit `IExternalCommand` wrapper) + `body.cs` |
| CPython 3 | No | `script.py` with `#! python3` + `body.py` |

## The command library

Default folder: `%LOCALAPPDATA%\First Option\RevitMCP\Command Library` (change it in GitHub Settings).

```
Command Library/
  README.md                          table of commands (written by the MCP)
  index.json                         the same, for agents
  FirstOptionLibrary.extension/
    FO Library.tab/
      Commands.panel/
        create_wall_grid.pushbutton/
          command.json               description, inputs, tags, runs, tested Revit versions
          body.py                    the code as it ran through the MCP
          script.py                  pyRevit button wrapper (transaction + same names as the MCP)
          bundle.yaml                button title and tooltip
```

## The Revit panel

**First Option > AI Bridge** has three buttons:

- **MCP Panel**: shows or hides the dockable panel. It shows the pyRevit Routes state, the port of this Revit, the Revit, pyRevit and Python versions, the C# runner state, the last agent call, the executed commands (select one to see its code and output), and a notice when the MCP uploads to GitHub.
- **Command Library**: opens the library folder.
- **GitHub Settings**: repository owner, name, branch, token (encrypted with Windows DPAPI for the current user), library folder, commit author, auto-push, and push notices. **Test connection** checks the repository and the push permission.

## Files on this computer

All files are in one folder, `%LOCALAPPDATA%\First Option\RevitMCP`:

| Folder | What it holds | Written by |
|---|---|---|
| `Server\` | The MCP server, `FirstOption.RevitMcp.exe` | `install.ps1` |
| `Revit Add-in\<version>\` | The Revit add-in for each Revit version | `install.ps1` |
| `pyRevit Bridge\` | The pyRevit extension with `startup.py` (the `fo-mcp` Routes API) | `install.ps1` |
| `Command Library\` | The saved commands (a pyRevit extension) | MCP server (`library_save`) |
| `Activity Log\activity.jsonl` | Every run, with name, code, output and error (the panel reads it) | MCP server |
| `Settings\settings.json` | GitHub and Routes settings (the MCP reads it on each call) | GitHub Settings window |

Only two things are outside this folder, because Revit and the agents look only in their own folders:

| File | Why |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<version>\FirstOption.RevitMcp.addin` | Revit loads add-ins from here. The file points to `Revit Add-in\<version>`. |
| `~\.claude\skills`, `~\.codex\skills` | Claude Code and Codex read skills from here. |

Advanced settings in `settings.json`: `routesHost` (default `127.0.0.1`), `portStart` (48884), `portCount` (10), `remoteUrl` (a git remote that is not github.com).

## Troubleshooting

| Problem | Fix |
|---|---|
| Panel: "pyRevit Routes offline" | pyRevit > Settings > Routes: turn on the server, Save, Reload. Check `pyrevit extensions paths` lists `%LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge`. |
| Panel: bridge answers for another Revit | Reload pyRevit in this Revit. |
| Tool: "The C# runner is not loaded" | Install the add-in for this Revit version (`install.ps1 -RevitVersions 2026`) and restart Revit. |
| Install ends with "Install incomplete" | Read the FAIL lines it prints; each line says what to do. The script checks every file it installed. |
| Install says "No Revit 2021-2026 was found" | Revit is installed in a folder the script does not know. Give the versions yourself: `install.ps1 -RevitVersions 2025,2026`. |
| Revit shows an add-in error at start | An old manifest points to a folder that is gone. Run `install.ps1` again; it deletes such manifests. |
| Tool times out | A dialog is open in Revit, or Revit is busy. Close the dialog. |
| Routes on a different host | Set `routesHost` in `settings.json` to the host in pyRevit Settings > Routes. |
| `git push failed` | Check the token and the repository in GitHub Settings; use Test connection. |
| Library buttons do not show | `pyrevit extensions paths add "<library folder>"`, then Reload. The tab is "FO Library". |

## Development

```powershell
dotnet build src\Server -c Release
dotnet build src\Addin -c Release -p:RevitVersion=2026   # 2025-2026: net8.0-windows, 2021-2024: net48
python tests\smoke_mcp.py                                # MCP end to end, against a mock Routes server
dotnet run --project tests\RunnerCompileTest -c Release  # Roslyn runner, outside Revit
```

Regenerate the UI mockups with Codex CLI:

```powershell
powershell -ExecutionPolicy Bypass -File mockups\generate-mockups.ps1
```
