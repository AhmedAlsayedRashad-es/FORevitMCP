Create UI mockups for a Revit add-in named "FirstOption Revit MCP". Write ONLY into the ./mockups folder. Do not create or change any other file.

Context: An AI agent (Claude Code or Codex CLI) talks to a .NET MCP server. The MCP server sends IronPython or C# code to Revit through pyRevit Routes (HTTP, default port 48884). A C# Revit add-in shows a dockable panel and a settings window. The add-in lives on the "First Option" ribbon tab in Revit.

Deliver static HTML + inline CSS files (no external assets, no JS frameworks; small vanilla JS is allowed). Style: Revit 2025/2026 light UI look (Segoe UI, 12px, gray panels, thin borders), with a teal accent #0F766E for First Option. Each mockup must be sized like the real WPF window.

1. mockups/01-ribbon.html - The "First Option" ribbon tab with a panel "AI Bridge" and three large buttons: "MCP Panel" (toggles the dockable panel), "Command Library" (opens the library folder), "GitHub Settings".

2. mockups/02-main-panel.html - Dockable pane, 340px wide x 720px tall, title "FirstOption MCP". Sections top to bottom:
   a. Status card: pyRevit Routes state (green dot "online" / red "offline"), Port 48884, Revit version, active document, pyRevit version, Python engine (IronPython 2.7.12), "C# runner: ready (Roslyn)", last check time, "Refresh" button.
   b. MCP activity card: last agent call time and client, number of commands this session (and failed count), library: "12 commands - firstoption/revit-commands".
   c. Banner: "Uploaded to GitHub" - "3 files to firstoption/revit-commands @ a1b2c3d", commit message, time and client, a "View commit" link and a close x. Show that this banner appears when the MCP pushes to GitHub.
   d. "Executed commands" list: each row shows a status mark (ok / error), command name or "ad-hoc script", then "claude - IronPython - 120 ms" or "codex - C# - 1.4 s", and the time. Selecting a row shows a details area with the code (monospace, scroll) and the output or error text. Include 6 realistic sample rows (for example "create_wall_grid", "place_door_family", an error row "rename_views" with a traceback, one C# row "count_walls").
   e. Footer buttons: "Open log folder", "Clear list", "GitHub Settings".

3. mockups/03-github-settings.html - Modal window 520x600 "GitHub Settings": fields Repository owner, Repository name, Branch (default main), Personal access token (password box + "Test connection" button + status text), "Remove the saved token" link, Local library folder (text + "Browse..."), Commit author name, Commit author email, checkbox "Allow MCP to push automatically after saving a command", checkbox "Show notification in the panel after each push". Buttons "Save" and "Cancel". A note: "The token is stored encrypted for the current Windows user (DPAPI). Use a fine-grained token with Contents: Read and write on this repository only."

4. mockups/index.html - one page that links to the three mockups and shows them in iframes side by side.

Keep all text in plain English. After writing, list the files you created.
