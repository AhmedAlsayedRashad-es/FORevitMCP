# Required applications

Install these on the Windows computer that runs Revit. Run the commands in PowerShell.

| # | Application | Why | Install |
|---|---|---|---|
| 1 | Autodesk Revit 2020-2026 | The model. The add-in has builds for 2020-2026. | Autodesk Access, or the Autodesk account portal |
| 2 | pyRevit (5.x or later) | Runs the bridge (pyRevit Routes) and the library buttons | `winget install --id pyRevit.pyRevit -e` or the installer from https://github.com/pyrevitlabs/pyRevit/releases |
| 3 | pyRevit CLI | `pyrevit` command for extension paths and the Routes setting. The main installer usually adds it; install it only when `pyrevit --version` fails. | `winget install --id pyRevit.pyRevit.CLI -e` |
| 4 | .NET 8 SDK | Builds the MCP server and the Revit add-in (Revit 2020-2026) | `winget install --id Microsoft.DotNet.SDK.8 -e` |
| 5 | Git for Windows | The MCP commits and pushes the command library | `winget install --id Git.Git -e` |
| 6 | Node.js LTS | Needed for the Codex CLI (npm) | `winget install --id OpenJS.NodeJS.LTS -e` |
| 7 | Claude Code CLI | Agent 1 | `irm https://claude.ai/install.ps1 \| iex` (or `npm install -g @anthropic-ai/claude-code`), then run `claude` and sign in |
| 8 | Codex CLI | Agent 2 | `npm install -g @openai/codex`, then `codex login` |
| 9 | GitHub CLI (optional) | Create the library repository from the terminal | `winget install --id GitHub.cli -e`, then `gh auth login` |
| 10 | GitHub account and repository | Where the library goes | Create an empty repository, for example `revit-commands` (private is fine) |
| 11 | GitHub fine-grained token | Push access for the MCP | GitHub > Settings > Developer settings > Fine-grained tokens. Repository access: only the library repository. Permission: Contents = Read and write. |
| 12 | Python 3 (optional) | Only to run `tests/smoke_mcp.py` | `winget install --id Python.Python.3.12 -e` |

## Check the installation

Open a new PowerShell window (so that PATH is fresh), then run:

```powershell
dotnet --list-sdks
git --version
pyrevit --version
claude --version
codex --version
```

## pyRevit settings that this project needs

1. Turn on the Routes server:
   ```powershell
   pyrevit configs routes enable
   ```
   Or in Revit: pyRevit tab > Settings > Routes > turn on the server, then Save & Reload.
2. The first time Routes starts, Windows Firewall can ask for permission. Allow it for private networks.
3. The default port is 48884. A second Revit uses 48885, and so on.
