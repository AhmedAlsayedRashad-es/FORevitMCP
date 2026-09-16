# Install prompt

Give this prompt to a user. The user pastes it into Claude Code (or Codex CLI) on the Windows computer that runs Revit.

---

```text
Install the FirstOption Revit MCP on this Windows computer. Use cmd syntax for every command you show me.

Repository: https://github.com/AhmedAlsayedRashad-es/FORevitMCP

Do these steps in order. Stop and tell me when a step fails. Do not skip a step.

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
