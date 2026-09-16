---
name: revit-command-library
description: Reuse and grow the FirstOption Revit command library. Use before you write new Revit code (search first), and right after Revit code works (save it with a clear description and inputs), so that later agents, Claude or Codex, find it and run it again.
---

# Revit command library

The library is a folder and a git repository. Each command has `command.json` (metadata), `body.py` or `body.cs` (your code) and a wrapper script. Revit has no ribbon tab for it; run a command with `library_run`.

## Before you write code

1. `library_search` with the task verb and the element: `create grid`, `rename views`, `door family`, `export schedule`.
2. Try one more search with other words when nothing comes back.
3. Read the best hit with `library_get`. Look at `inputs`, `notes`, `lastRunOk` and `testedRevitVersions`.
4. When it fits, `library_run` it with `args_json`. When it almost fits, adapt the code and run it with `revit_execute_*`.

## After code works

Save it when it can help again. Do not save one-off questions, failed code, or code with ids or paths from this model only.

`library_save` fields:

- `name`: snake_case, verb first: `create_wall_grid`, `rename_views_by_level`, `place_door_family`.
- `description`: what it does and what it needs (open project, active floor plan, selection, family document).
- `language`: `ironpython` or `csharp`. Do not save `cpython`: nothing can run it.
- `code`: the exact code that ran.
- `inputs`: every `args` key with type, unit and default: `spacing_mm (float, 6000), count_x (int, 5), level (str, lowest level)`.
- `tags`: 2-5 lower-case words.
- `notes`: limits, Revit versions, known problems.
- `overwrite=true` only when you improve the same command with the same inputs. Otherwise use a new name.

## Write code that can be saved

- Read inputs from `args` with defaults: `spacing = float(args.get("spacing_mm", 6000)) / 304.8` (Python) or `args.TryGetValue("spacing_mm", out var s)` (C#).
- An agent can run it later with empty `args`, so the defaults must work.
- Find elements by name, category or selection, never by a fixed ElementId.
- Print a short summary and set `result` (Python) or `return` a value (C#).

## Share

When GitHub auto-push is on, `library_save` pushes and the Revit panel shows a notice. When it is off, use the `revit-github-sync` skill when the user wants to share.
