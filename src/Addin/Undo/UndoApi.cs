using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FirstOption.RevitMcp.Addin.Undo
{
    /// <summary>
    /// The undo routes of the pyRevit bridge (startup.py finds this type by reflection and calls the methods on the Revit main thread).
    /// Every method takes (UIApplication, request JSON) and returns JSON. Keep the signatures stable.
    /// </summary>
    public static class UndoApi
    {
        public static string HistoryJson(object uiapp, string requestJson) => Guard(uiapp, requestJson, History);
        public static string BaselineJson(object uiapp, string requestJson) => Guard(uiapp, requestJson, Baseline);
        public static string UndoJson(object uiapp, string requestJson) => Guard(uiapp, requestJson, Undo);
        public static string StatusJson(object uiapp, string requestJson) => Guard(uiapp, requestJson, Status);
        public static string ResetJson(object uiapp, string requestJson) => Guard(uiapp, requestJson, Reset);

        private static Dictionary<string, object> History(UIApplication uiapp, Dictionary<string, object> req)
        {
            var doc = ActiveDocument(uiapp);
            var limit = (int)Math.Max(1, Math.Min(200, Long(req, "limit", 30)));
            lock (UndoJournal.Gate)
            {
                var state = UndoJournal.StateOf(doc, true);
                // undone entries are all on top; check them from the oldest up, so an element added by an older undone entry counts as correctly gone
                var checks = new Dictionary<UndoEntry, Dictionary<string, object>>();
                var addedByOlderUndone = new HashSet<long>();
                foreach (var e in state.Entries.Where(x => x.Undone))
                {
                    checks[e] = UndoJob.Check(doc, e, addedByOlderUndone);
                    addedByOlderUndone.UnionWith(e.Added);
                }
                var entries = new List<object>();
                foreach (var e in Enumerable.Reverse(state.Entries).Take(limit))
                {
                    var item = Describe(e);
                    if (checks.TryGetValue(e, out var check)) item["check"] = check;
                    entries.Add(item);
                }
                var d = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["document"] = doc.Title,
                    ["entries"] = entries,
                    ["total"] = state.Entries.Count,
                    ["agentRunsInUndoList"] = state.Entries.Where(x => x.FromMcp && !x.Undone).Select(x => x.RunId).Distinct().Count(),
                    ["baseline"] = BaselineInfo(state),
                    ["note"] = "Top entry first: the next Undo removes it. 'check' shows, for undone entries, that added elements are gone and deleted or modified elements exist again.",
                };
                if (state.HistoryLost != null) d["historyLost"] = state.HistoryLost;
                if (UndoJournal.Job != null) d["undoJob"] = UndoJournal.Job.Summary();
                return d;
            }
        }

        private static Dictionary<string, object> Baseline(UIApplication uiapp, Dictionary<string, object> req)
        {
            var doc = ActiveDocument(uiapp);
            lock (UndoJournal.Gate)
            {
                var state = UndoJournal.StateOf(doc, true);
                var top = state.DoneTopDown().FirstOrDefault();
                state.BaselineSeq = top?.Seq ?? 0;
                state.BaselineUtc = DateTime.UtcNow;
                state.BaselineBroken = null;
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["document"] = doc.Title,
                    ["baseline"] = BaselineInfo(state),
                    ["message"] = "Baseline set. revit_undo with to_baseline=true undoes every change made after this point.",
                };
            }
        }

        private static Dictionary<string, object> Undo(UIApplication uiapp, Dictionary<string, object> req)
        {
            var doc = ActiveDocument(uiapp);
            var includeUser = Bool(req, "includeUserChanges", false);
            var execute = Bool(req, "execute", true);
            lock (UndoJournal.Gate)
            {
                if (UndoJournal.Job != null && UndoJournal.Job.Status == UndoJob.Running)
                    return Refuse("An undo is already running.", "Call revit_undo_history to follow it.");

                var state = UndoJournal.StateOf(doc, true);
                var done = state.DoneTopDown();
                int count;

                if (Bool(req, "toBaseline", false))
                {
                    if (!state.BaselineSeq.HasValue)
                        return Refuse("There is no baseline for '" + doc.Title + "'" + (state.HistoryLost != null ? " (" + state.HistoryLost + ")" : "") + ".",
                            "Use runs or to_run_id, or call revit_baseline before the next task.");
                    if (state.BaselineBroken != null) return Refuse(state.BaselineBroken, "Use runs or to_run_id.");
                    count = done.TakeWhile(x => x.Seq > state.BaselineSeq.Value).Count();
                }
                else if (req.TryGetValue("toRunId", out var r) && r is string runId && runId.Length > 0)
                {
                    var lowest = done.FindLastIndex(x => x.RunId == runId);
                    if (lowest < 0)
                    {
                        var undone = state.Entries.Any(x => x.RunId == runId && x.Undone);
                        if (undone) return Nothing(doc, "Run " + runId + " is already undone.");
                        return Refuse("Run " + runId + " is not in the undo list of '" + doc.Title + "'" + (state.HistoryLost != null ? " (" + state.HistoryLost + ")" : "") + ".",
                            "The run made no model change, ran in another document, or the undo list was cleared. Call revit_undo_history.");
                    }
                    count = lowest + 1;
                }
                else
                {
                    var runs = (int)Math.Max(1, Long(req, "runs", 1));
                    var seen = new List<string>();
                    foreach (var e in done)
                    {
                        if (!e.FromMcp || seen.Contains(e.RunId)) continue;
                        seen.Add(e.RunId);
                        if (seen.Count == runs) break;
                    }
                    if (seen.Count < runs)
                        return Refuse("The undo list of '" + doc.Title + "' has only " + seen.Count + " agent run(s)" + (state.HistoryLost != null ? " (" + state.HistoryLost + ")" : "") + ".",
                            "Call revit_undo_history to see the list.");
                    count = done.FindLastIndex(x => x.RunId == seen[seen.Count - 1]) + 1;
                }

                if (count == 0) return Nothing(doc, "Nothing to undo: there is no change after the target.");

                var targets = done.Take(count).ToList();
                var plan = targets.Select(Describe).Cast<object>().ToList();
                var instructions = Instructions(targets);
                var others = targets.Where(x => !x.FromMcp).ToList();
                if (others.Count > 0 && !includeUser)
                {
                    var refused = Refuse(
                        "Undo would also remove " + others.Count + " change(s) that are not from the agent: " + string.Join("; ", others.Select(x => "'" + x.Name + "'")) + ".",
                        "Ask the user. When the user agrees to lose these changes, call again with include_user_changes=true.");
                    refused["plan"] = plan;
                    return refused;
                }

                var result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["document"] = doc.Title,
                    ["plan"] = plan,
                    ["instructions"] = instructions,
                };
                if (!execute)
                {
                    result["mode"] = "manual";
                    result["message"] = "Nothing was undone. Give the instructions to the user, then call revit_undo_history to check the result.";
                    return result;
                }

                if (!uiapp.CanPostCommand(UndoJob.UndoCommand))
                    return Refuse("Revit does not allow Undo now (an edit mode or a dialog).", instructions);

                var job = new UndoJob { State = state, Targets = targets, IncludeUser = includeUser, Instructions = instructions };
                UndoJournal.Job = job;
                job.Post(uiapp);
                result["mode"] = "auto";
                result["state"] = job.Status;
                return result;
            }
        }

        private static Dictionary<string, object> Status(UIApplication uiapp, Dictionary<string, object> req)
        {
            lock (UndoJournal.Gate)
            {
                var job = UndoJournal.Job;
                if (job == null) return new Dictionary<string, object> { ["ok"] = true, ["state"] = "none" };
                job.Pump(uiapp);
                if (job.Status == UndoJob.Done)
                {
                    try { job.VerifyOnce(job.State.Doc); }
                    catch (Exception ex) { job.Verification = new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message }; }
                }
                var d = job.Summary();
                d["ok"] = job.Status != UndoJob.Failed;
                return d;
            }
        }

        private static Dictionary<string, object> Reset(UIApplication uiapp, Dictionary<string, object> req)
        {
            var doc = ActiveDocument(uiapp);
            lock (UndoJournal.Gate)
            {
                if (UndoJournal.Job != null && UndoJournal.Job.Status == UndoJob.Running) return Refuse("An undo is running.", "Wait for it to end.");
            }
            if (doc.IsWorkshared)
                return Refuse("'" + doc.Title + "' is workshared. Reset does not close workshared models.", "Ask the user to use Undo, or close the local file without saving and open it again.");
            var path = doc.PathName;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Refuse("'" + doc.Title + "' was never saved to a file, so there is nothing to reopen.", "Use revit_undo.");
            var title = doc.Title;
            var temp = Path.Combine(Path.GetTempPath(), "FirstOption.RevitMcp.reset." + Guid.NewGuid().ToString("N") + ".rvt");
            var step = "create a temporary project";
            try
            {
                // Revit cannot close the active document, so a temporary project becomes active first.
                var template = uiapp.Application.DefaultProjectTemplate;
                Document placeholder = !string.IsNullOrEmpty(template) && File.Exists(template)
                    ? uiapp.Application.NewProjectDocument(template)
                    : null;
                if (placeholder != null)
                {
                    placeholder.SaveAs(temp);
                    placeholder.Close(false);
                }
                else
                {
                    File.Copy(path, temp);
                }

                step = "activate the temporary project";
                uiapp.OpenAndActivateDocument(temp);

                step = "close '" + title + "' without saving";
                if (!doc.Close(false)) throw new InvalidOperationException("Revit did not close the document.");

                step = "open '" + path + "'";
                uiapp.OpenAndActivateDocument(path);

                step = "close the temporary project";
                foreach (var d in uiapp.Application.Documents.Cast<Document>().ToList())
                {
                    if (string.Equals(d.PathName, temp, StringComparison.OrdinalIgnoreCase)) d.Close(false);
                }
            }
            catch (Exception ex)
            {
                return Refuse("Reset stopped at the step: " + step + ". " + ex.GetType().Name + ": " + ex.Message,
                    "Look at Revit. If '" + title + "' is closed, open '" + path + "' by hand.");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* still open */ }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["reopened"] = path,
                ["document"] = uiapp.ActiveUIDocument?.Document?.Title,
                ["message"] = "The model is back to the last saved file. Every unsaved change was discarded, and the undo list is empty.",
            };
        }

        // --- helpers -----------------------------------------------------------------------------------------------

        private static string Instructions(List<UndoEntry> targets)
        {
            var lowest = targets[targets.Count - 1];
            var text = "In Revit, click the arrow next to Undo in the Quick Access Toolbar and select '" + lowest.Name + "'" +
                       (targets.Count > 1 ? " (" + targets.Count + " entries from the top)" : "") +
                       ". Revit undoes it and everything above it.";
            if (targets.Count == 1) text += " Or press Ctrl+Z once.";
            return text;
        }

        private static Dictionary<string, object> Describe(UndoEntry e)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = e.Name,
                ["source"] = e.FromMcp ? "agent" : "user or other add-in",
                ["state"] = e.Undone ? "undone" : "done",
                ["time"] = e.TimeUtc.ToLocalTime().ToString("HH:mm:ss"),
                ["changes"] = new Dictionary<string, object> { ["added"] = e.Added.Count, ["modified"] = e.Modified.Count, ["deleted"] = e.Deleted.Count },
            };
            if (e.RunId != null) d["runId"] = e.RunId;
            if (e.FromMcp && !e.Exact) d["note"] = "part of a run with undo_group=false";
            if (e.SideEffects.Count > 0) d["sideEffects"] = e.SideEffects.ToList();
            return d;
        }

        private static Dictionary<string, object> BaselineInfo(DocState state)
        {
            if (!state.BaselineSeq.HasValue) return null;
            var d = new Dictionary<string, object>
            {
                ["setAt"] = state.BaselineUtc?.ToLocalTime().ToString("HH:mm:ss"),
                ["entriesAfter"] = state.Entries.Count(x => !x.Undone && x.Seq > state.BaselineSeq.Value),
            };
            if (state.BaselineBroken != null) d["broken"] = state.BaselineBroken;
            return d;
        }

        private static Dictionary<string, object> Nothing(Document doc, string message) =>
            new Dictionary<string, object> { ["ok"] = true, ["document"] = doc.Title, ["nothingToUndo"] = true, ["message"] = message };

        private static Dictionary<string, object> Refuse(string error, string hint) =>
            new Dictionary<string, object> { ["ok"] = false, ["error"] = error, ["hint"] = hint };

        private static Document ActiveDocument(UIApplication uiapp) =>
            uiapp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No document is open in Revit.");

        private static long Long(Dictionary<string, object> req, string key, long fallback)
        {
            if (!req.TryGetValue(key, out var v) || v == null) return fallback;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            return long.TryParse(Convert.ToString(v), out var p) ? p : fallback;
        }

        private static bool Bool(Dictionary<string, object> req, string key, bool fallback) =>
            req.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        private static string Guard(object uiappObject, string requestJson, Func<UIApplication, Dictionary<string, object>, Dictionary<string, object>> body)
        {
            try
            {
                var req = MiniJson.Parse(requestJson) as Dictionary<string, object> ?? new Dictionary<string, object>();
                return MiniJson.Write(body((UIApplication)uiappObject, req));
            }
            catch (Exception ex)
            {
                return MiniJson.Write(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message, ["traceback"] = ex.ToString() });
            }
        }
    }
}
