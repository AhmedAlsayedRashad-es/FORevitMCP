# Install prompt

Give this prompt to a user. The user pastes it into Claude Code (or Codex CLI) on the Windows computer that runs Revit. The agent does the whole install.

---

```text
Install the FirstOption Revit MCP on this Windows computer. Do every step yourself: run the commands, install what is missing, fix what fails, and test the result. Do not give me steps to do by hand.

Repository: https://github.com/AhmedAlsayedRashad-es/FORevitMCP

Ask me only in these two cases, and do all other work without questions:
- Revit is open. Closing Revit can lose unsaved work, so ask me once before you close it.
- A thing that only a person can do blocks the install: Revit is not installed (it needs an Autodesk license), a Windows admin (UAC) prompt, or a sign-in (for example gh auth login for a private repository).
When a step fails, read the error, fix the cause, and run the step again. Stop only after 3 failed fixes of the same step, and then tell me the error.

STEP 0. Check what is already installed. Only read in this step.
Compare this computer with the expected structure. Use dir, "claude mcp get firstoption-revit" or "codex mcp list", "pyrevit extensions paths" and "pyrevit configs routes".

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
   ├─ Command Library\                             (the MCP server creates it; can be missing)
   ├─ Activity Log\activity.jsonl                  (created on the first run; can be missing)
   └─ Settings\settings.json                       (created by GitHub Settings; can be missing)

   %APPDATA%\Autodesk\Revit\Addins\<version>\FirstOption.RevitMcp.addin   (required, one for each Revit version)

   %USERPROFILE%\.claude\skills\ (Claude Code) and %USERPROFILE%\.codex\skills\ (Codex)
   ├─ revit-mcp\SKILL.md                           (required)
   ├─ pyrevit-engines\SKILL.md                     (required)
   ├─ revit-command-library\SKILL.md               (required)
   ├─ revit-families-and-3d\SKILL.md               (required)
   └─ revit-github-sync\SKILL.md                   (required)

   MCP registration: "firstoption-revit" runs
   %LOCALAPPDATA%\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe    (required)

   pyRevit: "pyrevit extensions paths" lists %LOCALAPPDATA%\First Option\RevitMCP\pyRevit Bridge,
   and "pyrevit configs routes" says "Enabled".                              (required)

Decide from the result:
- Every required item is found: go to STEP 6.
- Only skills are missing: do STEP 2, run the xcopy command from README.md section 5, then go to STEP 6.
- Only the MCP registration is missing: run the "claude mcp add" or "codex mcp add" command from README.md section 3, then go to STEP 6.
- Only the pyRevit items are missing: run the two commands from README.md section 4, then go to STEP 6.
- Anything else is missing: do every step from STEP 1.

STEP 1. Install the applications.
Run: dotnet --list-sdks, git --version, pyrevit --version, claude --version, codex --version.
- Revit 2020-2026 must be installed. You cannot install Revit; if no Revit is found, tell me and stop.
- Install every other missing application yourself with the winget command from docs/REQUIRED-APPS.md (use the raw file from GitHub if the repository is not cloned yet): .NET 8 SDK, Git, pyRevit, pyRevit CLI. Add --accept-source-agreements --accept-package-agreements.
- After an install, open a new shell or read PATH again from the registry, then check the version again.

STEP 2. Get the repository.
Clone it to %USERPROFILE%\FORevitMCP. If the folder exists, run git pull in it.

STEP 3. Stop the programs that lock the files.
- Stop every FirstOption.RevitMcp.exe process (taskkill /IM FirstOption.RevitMcp.exe /F). Your own firstoption-revit connection can stop too; this is expected.
- If Revit.exe runs, ask me once. After I say yes, close it (taskkill /IM Revit.exe) and wait until the process is gone. Use /F only if it does not close in 60 seconds.

STEP 4. Run the install script.
In the repository folder, run:
   powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RegisterClaude -RegisterCodex
Use only -RegisterClaude when Codex is not installed, and only -RegisterCodex when Claude Code is not installed.
If the script stops with an error, fix the cause (see the Troubleshooting table in README.md) and run it again.

STEP 5. Check the files again.
Do the STEP 0 check again. Every required item must be found. Fix each missing item and check again.

STEP 6. Start Revit and test.
- Start the newest Revit.exe on this computer (find it in %ProgramFiles%\Autodesk\Revit <year>).
- Wait until Routes answers: call http://127.0.0.1:48884/fo-mcp/status/ every 10 seconds, for up to 5 minutes. Use curl -s.
- Run: "%LOCALAPPDATA%\First Option\RevitMCP\Server\FirstOption.RevitMcp.exe" doctor
  Every line must be "ok". The lines "settings" and "github" can be "warn" (GitHub is not set up yet). Fix every other line and run doctor again.
- If Revit does not answer, run "pyrevit extensions paths" and "pyrevit configs routes", fix them, close Revit, and start it again.

STEP 7. Report.
Give me a short table: each item, and its state (installed, already there, or fixed). Tell me that the firstoption-revit tools and the skills are ready in the next Claude Code or Codex session.
```

---

## Test prompt (in the next session)

```text
Test the FirstOption Revit MCP. Call revit_instances. Then run a read-only Python script with revit_execute_python (use_transaction=false) that returns the document title and the number of walls. Do not change the model. Tell me the Revit version, the port, whether the C# runner is loaded, and the result.
```
