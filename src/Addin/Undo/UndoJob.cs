using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FirstOption.RevitMcp.Addin.Undo
{
    /// <summary>
    /// Presses Undo in Revit, one step at a time: post the Undo command, wait for DocumentChanged (TransactionUndone), check the
    /// name, then post the next step from Idling. It stops at the first step it did not expect, and never undoes more than the plan.
    /// </summary>
    internal sealed class UndoJob
    {
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);

        public DocState State;
        public List<UndoEntry> Targets;   // top-down
        public bool IncludeUser;
        public string Instructions;

        public int Next;
        public bool NeedPost;
        public int Posts;
        public DateTime LastPostUtc;
        public string Status = Running;
        public string Error;
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime? EndedUtc;
        public readonly List<string> Steps = new List<string>();
        public Dictionary<string, object> Verification;

        public static RevitCommandId UndoCommand => RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);

        public void Post(UIApplication uiapp)
        {
            NeedPost = false;
            Posts++;
            LastPostUtc = DateTime.UtcNow;
            uiapp.PostCommand(UndoCommand);
        }

        /// <summary>Called from DocumentChanged (TransactionUndone), inside the journal lock.</summary>
        public void OnUndone(DocState state, List<string> names)
        {
            if (Status != Running || !ReferenceEquals(state, State)) return;
            var text = names.Count == 0 ? "(no name)" : string.Join(", ", names);
            Steps.Add(text);
            LastPostUtc = DateTime.UtcNow;

            var matched = -1;
            for (var i = Next; i < Targets.Count; i++)
            {
                if (Targets[i].Exact && Targets[i].Matches(names)) { matched = i; break; }
            }

            var notInPlan = "Revit undid '" + text + "', which is not in the plan. Press Redo (Ctrl+Y) once in Revit to bring it back.";
            var current = Targets[Next];
            if (matched >= 0)
            {
                // Revit undoes the newest step first, so the entries planned above this agent run had no step in the Revit
                // undo list (for example a run whose code rolled back all its changes). They are gone too.
                for (var i = Next; i < matched; i++)
                {
                    if (Targets[i].Exact) Steps.Add("(" + Targets[i].RunId + " had no step in the Revit undo list: its changes were rolled back)");
                }
                Next = matched + 1;
            }
            else if (Targets.Any(t => t.Exact && t.Matches(names)))
            {
                Fail("Revit undid '" + text + "' out of order.");
                return;
            }
            else if (!current.Exact)
            {
                if (current.Matches(names) || (!current.FromMcp && IncludeUser))
                {
                    // a TransactionGroup of another add-in has its group name in the undo list, not the inner names the journal saw
                    Next++;
                }
                else
                {
                    Fail(notInPlan);
                    return;
                }
            }
            else if (!IncludeUser || Targets.Any(t => t.FromMcp && t.Matches(names)))
            {
                Fail(notInPlan);
                return;
            }
            // else: the journal merged two separate user commits into one entry; keep going until the next agent run

            if (Next >= Targets.Count)
            {
                Status = Done;
                EndedUtc = DateTime.UtcNow;
            }
            else if (Posts > Targets.Count * 2 + 10)
            {
                Fail("The undo took more steps than planned, so it stopped.");
            }
            else
            {
                NeedPost = true;
            }
        }

        /// <summary>Called from Idling and from the status route. Posts the next step, or stops on a timeout.</summary>
        public void Pump(UIApplication uiapp)
        {
            if (Status != Running || uiapp == null) return;
            var active = uiapp.ActiveUIDocument?.Document;
            if (active == null || !active.Equals(State.Doc))
            {
                Fail("The active document changed. Activate '" + UndoJournal.Title(State.Doc) + "' and call revit_undo again.");
                return;
            }
            if (NeedPost)
            {
                if (uiapp.CanPostCommand(UndoCommand)) Post(uiapp);
                else if (DateTime.UtcNow - LastPostUtc > StepTimeout) Fail("Revit does not allow Undo now.");
                return;
            }
            if (DateTime.UtcNow - LastPostUtc > StepTimeout)
                Fail("Revit did not finish an Undo step in " + (int)StepTimeout.TotalSeconds + " s. A dialog can be open, or Revit is in an edit mode (sketch, group, family).");
        }

        public void Fail(string error)
        {
            if (Status != Running) return;
            Status = Failed;
            Error = error;
            NeedPost = false;
            EndedUtc = DateTime.UtcNow;
        }

        public void VerifyOnce(Document doc)
        {
            if (Status != Done || Verification != null) return;
            var items = new List<object>();
            var allOk = true;
            for (var i = 0; i < Targets.Count; i++)
            {
                var t = Targets[i];
                var check = Check(doc, t, new HashSet<long>(Targets.Skip(i + 1).SelectMany(x => x.Added)));
                if (!(bool)check["ok"]) allOk = false;
                check["name"] = t.Name;
                check["source"] = t.FromMcp ? "agent" : "user or other add-in";
                if (t.RunId != null) check["runId"] = t.RunId;
                items.Add(check);
            }
            Verification = new Dictionary<string, object> { ["ok"] = allOk, ["entries"] = items };
        }

        /// <summary>
        /// After an undo, elements the entry added are gone, and elements it deleted or modified exist again, unless an older entry
        /// that is also undone added them (<paramref name="addedByOlderUndone"/>): then they are correctly gone too.
        /// </summary>
        public static Dictionary<string, object> Check(Document doc, UndoEntry entry, HashSet<long> addedByOlderUndone)
        {
            bool ExpectedBack(long id) => !entry.Added.Contains(id) && !addedByOlderUndone.Contains(id);
            var addedLeft = entry.Added.Where(id => Exists(doc, id)).Take(20).ToList();
            var deletedMissing = entry.Deleted.Where(id => ExpectedBack(id) && !Exists(doc, id)).Take(20).ToList();
            var modifiedMissing = entry.Modified.Where(id => ExpectedBack(id) && !entry.Deleted.Contains(id) && !Exists(doc, id)).Take(20).ToList();
            var result = new Dictionary<string, object>
            {
                ["ok"] = addedLeft.Count == 0 && deletedMissing.Count == 0 && modifiedMissing.Count == 0,
                ["checkedIds"] = entry.Added.Count + entry.Deleted.Count + entry.Modified.Count,
            };
            if (addedLeft.Count > 0) result["addedStillPresent"] = addedLeft;
            if (deletedMissing.Count > 0) result["deletedNotRestored"] = deletedMissing;
            if (modifiedMissing.Count > 0) result["modifiedMissing"] = modifiedMissing;
            if (entry.IdsCut) result["note"] = "More than " + UndoEntry.MaxIds + " ids changed; only the first ones were checked.";
            return result;
        }

        private static bool Exists(Document doc, long id)
        {
            try { return doc.GetElement(Ids.To(id)) != null; }
            catch { return false; }
        }

        public Dictionary<string, object> Summary()
        {
            var d = new Dictionary<string, object>
            {
                ["state"] = Status,
                ["document"] = UndoJournal.Title(State.Doc),
                ["planned"] = Targets.Select(t => t.Name).ToList(),
                ["remaining"] = Targets.Skip(Next).Select(t => t.Name).ToList(),
                ["undoSteps"] = Steps.ToList(),
                ["startedAt"] = StartedUtc.ToLocalTime().ToString("HH:mm:ss"),
            };
            if (EndedUtc.HasValue) d["endedAt"] = EndedUtc.Value.ToLocalTime().ToString("HH:mm:ss");
            if (Error != null) d["error"] = Error;
            if (Status == Failed) d["instructions"] = Instructions;
            if (Verification != null) d["verification"] = Verification;
            return d;
        }
    }
}
