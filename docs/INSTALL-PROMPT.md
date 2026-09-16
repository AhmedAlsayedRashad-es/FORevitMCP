# Install prompt

Give this prompt to a user. The user pastes it into Claude Code (or Codex CLI) on the Windows computer that runs Revit.

---

```text
Install the FirstOption Revit MCP on this Windows computer. Use cmd syntax for every command you show me.

Repository: https://github.com/AhmedAlsayedRashad-es/FORevitMCP

Do these steps in order. Stop and tell me when a step fails. Do not skip a step.

0. Check what is already installed. Compare this computer with the expected structure below. Use "dir" and "claude mcp get firstoption-revit" (or "codex mcp list"). Only read; do not change files in this step.

   EXPECTED STRUCTURE

   %LOCALAPPDATA%\First Option\RevitMCP\
   ├─ Server\
   │  ├─ FirstOption.RevitMcp.exe                  (required)
   │  ├─ FirstOption.RevitMcp.dll                  (required)
   │  ├─ FirstOption.RevitMcp.deps.json            (required)
   │  └─ FirstOption.RevitMcp.runtimeconfig.json   (required)
   ├─ Revit Add-in\
   │  └─ <version>\  one folder for each Revit version on this computer (2020-2026)
   │     ├─ FirstOption.RevitMcp.Addin.dll         (required)
   │     ├─ Microsoft.CodeAnalysis.dll             (required, C# runner)
   │     └─ Microsoft.CodeAnalysis.CSharp.dll      (required, C# runner)
   ├─ pyRevit Bridge\
   │  └─ FirstOptionMCP.extension\
   │     └─ startup.py                             (required)
   ├─ Command Library\                             (created by the MCP server; can be missing on a new install)
   ├─ Activity Log\activity.jsonl                  (created on the first run; can be missing)
   └─ Settings\settings.json                       (created by GitHub Settings; can be missing)

   %APPDATA%\Autodesk\Revit\Addins\<version>\FirstOption.RevitMcp.addin   (required, one for each Revit version)

   %USERPROFILE%\.claude\skills\   (Claude Code)   and/or   %USERPROFILE%\.codex\skills\   (Codex)
   ├─ revit-mcp\SKILL.md                           (required)
   ├─ pyrevit-engines\SKILL.md                     (required)
   ├─ revit-command-library\SKILL.md               (required)
   ├─ revit-families-and-3d\SKILL.md               (required)
   └─ revit-github-sync\SKILL.md                   (required)

   MCP registration: "firstoption-revit" points to
   %LOCALAPPDATA%\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe     (required)

   pyRevit: "pyrevit extensions paths" lists %LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge, and "pyrevit configs routes" says "Enabled".   (required)

   Give me a table: each required item, found or missing. Then decide:
   - Every required item is found: skip steps 2 to 4 and go to step 5.
   - Only skills are missing: do step 2 to get the repository, then in the repository folder run the xcopy command from README.md section 5, then go to step 5.
   - Only the MCP registration is missing: run the "claude mcp add" or "codex mcp add" command from README.md section 3, then go to step 5.
   - Only the pyRevit items are missing: go to step 6.
   - Anything else is missing: do every step from step 1.

1. Check the applications. Run: dotnet --list-sdks, git --version, pyrevit --version, claude --version, codex --version.
   - A .NET 8 SDK, Git and the pyRevit CLI are required. Revit 2020-2026 and pyRevit 5.x or later must be installed.
   - When one is missing, give me the winget command from docs/REQUIRED-APPS.md and wait until I install it.
2. Clone the repository to %USERPROFILE%\FORevitMCP. If the folder exists, run git pull in it.
3. Tell me to close Revit and every other Claude Code and Codex session. Wait until I confirm.
4. In the repository folder, run:
   powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RegisterClaude -RegisterCodex
   (Use only -RegisterClaude when Codex is not installed, or only -RegisterCodex when Claude Code is not installed.)
   The script builds the server and the add-in, copies the pyRevit bridge, turns on pyRevit Routes, copies the skills, and registers the MCP server.
5. Run the check:
   "%LOCALAPPDATA%\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe" doctor
   Show me every line that is not "ok", and fix it with the Troubleshooting table in README.md.
6. Run: pyrevit extensions paths
   The list must contain %LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge. If not, run:
   pyrevit extensions paths add "%LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge"
   pyrevit configs routes enable
7. Tell me to start Revit, open a model, and allow Revit in Windows Firewall if it asks. Then tell me to open First Option > AI Bridge > MCP Panel and check that it shows "pyRevit Routes online". Wait until I confirm.
8. Tell me to restart Claude Code (or Codex) so that it loads the new MCP server and skills. After the restart, I will ask you to test it.

At the end, give me a short list: what is installed, what failed, and what I must do by hand.
```

---

## Test prompt (after the restart)

```text
Test the FirstOption Revit MCP. Call revit_instances. Then run a read-only Python script with revit_execute_python (use_transaction=false) that returns the document title and the number of walls. Do not change the model. Tell me the Revit version, the port, whether the C# runner is loaded, and the result.
```
