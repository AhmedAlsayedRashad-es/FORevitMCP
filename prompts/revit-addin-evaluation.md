# Revit add-in evaluation prompt

Copy the block below to the agent. Fill in the 3 values at the top.

```text
ADDIN_SOURCE = <path to the add-in folder, e.g. Z:\R&D\FirstOptionTools\AlignTags>
REVIT_PORT   = <Routes port from revit_instances; ask me if more than one Revit runs>
PREFIX       = <short name for test elements, e.g. AT>

Goal: test every function of the Revit add-in at ADDIN_SOURCE inside live Revit through the
firstoption-revit MCP, split the tests into small reusable library commands, find bugs with
evidence, confirm each fix in Revit, then apply the fix to the source.

PHASE 1 - READ
1. Read all .cs/.xaml/.csproj files (skip bin, obj, .vs). List each function unit: commands,
   helpers, selection filters, view-model logic, UI rules (e.g. slider rounding).
2. Mark parts you cannot drive through the MCP: licence/auth checks, WPF windows,
   PickObject/PickPoint. Do not test them. Replace each pick with an arg or a computed value.
3. Note the #if branches per Revit version. Test the branch for the running Revit version.

PHASE 2 - TEST ENVIRONMENT
4. Run library_search first. Reuse commands that fit.
5. Build the smallest model that exercises the add-in: named views "<PREFIX>_TEST ...",
   elements and annotations in the layouts the tool expects. Always pass port=REVIT_PORT and a
   command_name.
6. Read the result back (ids, directions, bounding boxes, counts). Check traps:
   - Section boxes: Revit can flip BasisX/BasisZ. Check ViewDirection points at the elements.
   - New tags have Attached leaders: GetLeaderEnd throws until LeaderEndCondition = Free.
   - Units are feet (mm / 304.8).

PHASE 3 - SMALL TEST TOOLS (one library command per function group)
7. Write each test as a C# body (revit_execute_csharp). Copy the source code VERBATIM as local
   functions, so the test checks the real logic. Read inputs from args, with defaults that find
   the test views by name.
8. Kinds of test:
   a. Unit: pure math and helpers, known inputs and exact expected outputs, edge cases
      (null, empty, parallel lines, 0/90/180/270 deg, far points).
   b. Integration: run the command logic end to end on the test elements in a TransactionGroup
      and RollBack. Loop over every option combination (directions, orders, auto/manual values)
      and every view type.
   c. Quality metric: measure the visible result, not only "no exception" (e.g. leader
      crossings, overlaps, spacing, angles). Use more than one layout (row, column, scattered).
9. Print one line per check: PASS/FAIL name [measured value]. Print totals first.
   Take a before/after snapshot (element count, one position) to prove the rollback.
10. When a test runs clean, library_save it (clear description, args, notes with the expected
    known FAILs and the Revit version). Run it again with library_run to check the saved copy.
    For commands that change the model: run with safe args, check the result by a query, then
    revit_undo. Rolled-back test runs have no Revit undo step, so revit_undo can report
    "out of order" after a correct undo: check by query, not by that status.

PHASE 4 - REPORT FINDINGS
11. Group the findings: BUG (wrong result, with numbers: case, expected, got), LIMIT (design
    gap), MINOR (no visible effect). Give file:line for each.
12. For a visual problem, build a demo: one view per variant (old / fixed / proposed), red
    detail circles at each defect, counts in the view names, all views on one sheet. Change the
    active view only outside a transaction.

PHASE 5 - CONFIRM, THEN FIX
13. For each BUG, write the candidate fix inside the test harness first. Run the full matrix
    for the old and the fixed code side by side. Accept the fix only if it removes the defect
    and breaks no other case.
14. Show me the old/fixed numbers. Apply the fix to ADDIN_SOURCE after that. Ask me first if
    the fix removes or changes a UI option or a behaviour a user sees.
15. Edit the source minimally in its own style (indent, naming, #if branches for all versions).
    Remove code only when the fix makes it dead.
16. Build check: copy the project to the scratchpad (skip bin/obj/.vs), keep relative HintPath
    references valid, and remove the PostBuild step that copies the DLL into
    %AppData%\Autodesk\Revit\Addins. Build every configuration that has its own #if branch:
    dotnet build for net8.0 configs, Visual Studio MSBuild (vswhere) for .NET Framework configs.
    Never build inside ADDIN_SOURCE.
17. Run the saved test tools again against the fixed logic. Update the saved commands
    (overwrite=true) so they match the source.

PHASE 6 - SHARE
18. github_push only when I ask. Put the command names in the commit message.

RULES
- Do not delete or change elements you did not create. Undo verification runs.
- Do not run the add-in's auth or installer steps.
- A modal dialog in Revit blocks the MCP: on a timeout, ask me to check Revit.
- Report in short numbered sentences: Summary, Done, Problems (only unsolved, broken,
  or blocking), Recommendations (only decisions for me).
```
