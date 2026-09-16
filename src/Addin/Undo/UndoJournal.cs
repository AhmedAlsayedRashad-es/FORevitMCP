using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace FirstOption.RevitMcp.Addin.Undo
{
    /// <summary>One entry of the Revit undo list, as the journal sees it.</summary>
    internal sealed class UndoEntry
    {
        public const int MaxIds = 50000;

        public long Seq;
        public readonly List<string> Names = new List<string>();
        public bool FromMcp;
        public string RunId;
        /// <summary>One assimilated agent run with a unique name. An undo event matches it by name, so its state is exact.</summary>
        public bool Exact;
        public bool Undone;
        public DateTime TimeUtc = DateTime.UtcNow;
        public readonly HashSet<long> Added = new HashSet<long>();
        public readonly HashSet<long> Modified = new HashSet<long>();
        public readonly HashSet<long> Deleted = new HashSet<long>();
        public bool IdsCut;
        public readonly List<string> SideEffects = new List<string>();

        public string Name => string.Join(" + ", Names);

        public bool Matches(IList<string> names) => names.Any(n => Names.Contains(n));

        public void AddChanges(DocumentChangedEventArgs e)
        {
            if (!Copy(e.GetAddedElementIds(), Added)) IdsCut = true;
            if (!Copy(e.GetModifiedElementIds(), Modified)) IdsCut = true;
            if (!Copy(e.GetDeletedElementIds(), Deleted)) IdsCut = true;
        }

        public void AddChanges(UndoEntry other)
        {
            if (other.IdsCut) IdsCut = true;
            if (!Copy(other.Added, Added)) IdsCut = true;
            if (!Copy(other.Modified, Modified)) IdsCut = true;
            if (!Copy(other.Deleted, Deleted)) IdsCut = true;
        }

        private static bool Copy(IEnumerable<long> ids, HashSet<long> into)
        {
            foreach (var id in ids)
            {
                if (into.Count >= MaxIds) return false;
                into.Add(id);
            }
            return true;
        }

        private static bool Copy(ICollection<ElementId> ids, HashSet<long> into)
        {
            foreach (var id in ids)
            {
                if (into.Count >= MaxIds) return false;
                into.Add(Ids.Of(id));
            }
            return true;
        }
    }

    /// <summary>The journal of one open document. Entries are bottom first; undone entries stay on top until the next commit.</summary>
    internal sealed class DocState
    {
        public Document Doc;
        public readonly List<UndoEntry> Entries = new List<UndoEntry>();
        public long? BaselineSeq;
        public DateTime? BaselineUtc;
        public string BaselineBroken;
        public string HistoryLost;

        /// <summary>True between a commit from outside the agent and the next Idling. Commits in that window are one Revit command
        /// (for example a TransactionGroup of another add-in), so they become one entry. Too few entries is safe: the undo stops early.</summary>
        public bool MergeOpen;

        public List<UndoEntry> DoneTopDown()
        {
            var list = Entries.Where(x => !x.Undone).ToList();
            list.Reverse();
            return list;
        }

        /// <summary>A new commit clears the redo list in Revit.</summary>
        public void DropRedo()
        {
            if (BaselineSeq.HasValue && BaselineBroken == null && Entries.Any(x => x.Undone && x.Seq <= BaselineSeq.Value))
                BaselineBroken = "The model went below the baseline with Undo and then changed. Undo cannot reach the baseline any more.";
            Entries.RemoveAll(x => x.Undone);
        }

        public void Push(UndoEntry entry)
        {
            DropRedo();
            Entries.Add(entry);
        }

        public void Lose(string reason)
        {
            Entries.Clear();
            BaselineSeq = null;
            BaselineUtc = null;
            BaselineBroken = null;
            MergeOpen = false;
            HistoryLost = reason + " (" + DateTime.Now.ToString("HH:mm:ss") + ")";
        }
    }

    internal static class Ids
    {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        public static long Of(ElementId id) => id.IntegerValue;
        public static ElementId To(long value) => new ElementId((int)value);
#else
        public static long Of(ElementId id) => id.Value;
        public static ElementId To(long value) => new ElementId(value);
#endif
    }

    /// <summary>
    /// Follows the Revit undo list of every open document through DocumentChanged (commit, undo, redo), so the MCP can tell which
    /// entries came from agent runs, undo them in the right order, and check the result. All Revit events arrive on the main thread.
    /// </summary>
    internal static class UndoJournal
    {
        internal static readonly object Gate = new object();
        private static readonly List<DocState> States = new List<DocState>();
        private static long _seq;

        internal static RunScope ActiveRun;
        internal static UndoJob Job;

        internal static void Attach(UIControlledApplication application)
        {
            try
            {
                var c = application.ControlledApplication;
                c.DocumentChanged += OnDocumentChanged;
                c.DocumentClosing += OnDocumentClosing;
                c.DocumentSaving += OnDocumentSaving;
                c.DocumentSavingAs += OnDocumentSavingAs;
                c.DocumentSynchronizingWithCentral += OnSynchronizing;
                c.DocumentSynchronizedWithCentral += OnSynchronized;
#if !REVIT2020
                c.DocumentReloadedLatest += OnReloadedLatest;   // Revit 2021 and later only
#endif
                application.Idling += OnIdling;
            }
            catch
            {
                // undo tracking is optional; the add-in still loads
            }
        }

        internal static void Detach(UIControlledApplication application)
        {
            try
            {
                var c = application.ControlledApplication;
                c.DocumentChanged -= OnDocumentChanged;
                c.DocumentClosing -= OnDocumentClosing;
                c.DocumentSaving -= OnDocumentSaving;
                c.DocumentSavingAs -= OnDocumentSavingAs;
                c.DocumentSynchronizingWithCentral -= OnSynchronizing;
                c.DocumentSynchronizedWithCentral -= OnSynchronized;
#if !REVIT2020
                c.DocumentReloadedLatest -= OnReloadedLatest;
#endif
                application.Idling -= OnIdling;
            }
            catch
            {
                // Revit is closing
            }
        }

        internal static long NextSeq() => ++_seq;

        internal static DocState StateOf(Document doc, bool create)
        {
            if (doc == null) return null;
            foreach (var s in States)
            {
                try { if (s.Doc.Equals(doc)) return s; }
                catch { /* a closed document */ }
            }
            if (!create) return null;
            var state = new DocState { Doc = doc };
            States.Add(state);
            return state;
        }

        internal static string Title(Document doc)
        {
            try { return doc.Title; } catch { return "(closed document)"; }
        }

        // --- runs --------------------------------------------------------------------------------------------------

        internal static void BeginRun(RunScope run)
        {
            lock (Gate) ActiveRun = run;
        }

        internal static void EndRun(RunScope run, bool assimilated)
        {
            lock (Gate)
            {
                if (ReferenceEquals(ActiveRun, run)) ActiveRun = null;
                var state = StateOf(run.Doc, true);
                if (state == null) return;

                if (run.UsesGroup)
                {
                    // No commit left (none, or an inner TransactionGroup rolled them all back): Revit has no undo step for the run.
                    if (run.GroupCommits.Count == 0 || !assimilated)
                    {
                        if (run.HadCommits) state.DropRedo();   // the inner commits cleared the redo list before the rollback
                        return;
                    }
                    var entry = run.Pending;
                    foreach (var commit in run.GroupCommits) entry.AddChanges(commit);
                    entry.Seq = NextSeq();
                    entry.Names.Clear();
                    entry.Names.Add(run.UndoName);
                    entry.FromMcp = true;
                    entry.RunId = run.RunId;
                    entry.Exact = true;
                    entry.TimeUtc = DateTime.UtcNow;
                    entry.SideEffects.AddRange(run.SideEffects);
                    state.Push(entry);
                    run.Pushed.Add(entry);
                }
                else if (run.Pushed.Count > 0)
                {
                    run.Pushed[run.Pushed.Count - 1].SideEffects.AddRange(run.SideEffects);
                }
            }
        }

        // --- Revit events ------------------------------------------------------------------------------------------

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                var names = e.GetTransactionNames().ToList();
                lock (Gate)
                {
                    var run = ActiveRun;
                    var inRun = run != null && run.Doc != null && run.Doc.Equals(doc);
                    if (run != null && !inRun)
                        run.AddSideEffect("It also changed the document '" + Title(doc) + "'. Undo in '" + Title(run.Doc) + "' does not reverse changes inside '" + Title(doc) + "'.");

                    var state = StateOf(doc, true);
                    switch (e.Operation)
                    {
                        case UndoOperation.TransactionCommitted:
                            if (inRun) run.OnCommit(state, names, e);
                            else OnOtherCommit(state, names, e);
                            break;
                        case UndoOperation.TransactionUndone:
                            OnUndone(state, names);
                            break;
                        case UndoOperation.TransactionRedone:
                            OnRedone(state, names);
                            break;
                        case UndoOperation.TransactionGroupRolledBack:
                            if (inRun) run.OnGroupRolledBack(state, names);
                            else DropRolledBack(state, names);
                            break;
                    }
                }
            }
            catch
            {
                // never break a Revit command because of the journal
            }
        }

        private static void OnOtherCommit(DocState state, List<string> names, DocumentChangedEventArgs e)
        {
            var top = state.Entries.Count > 0 ? state.Entries[state.Entries.Count - 1] : null;
            if (state.MergeOpen && top != null && !top.FromMcp && !top.Undone)
            {
                foreach (var n in names) if (!top.Names.Contains(n)) top.Names.Add(n);
                top.AddChanges(e);
                return;
            }
            var entry = new UndoEntry { Seq = NextSeq() };
            entry.Names.AddRange(names);
            entry.AddChanges(e);
            state.Push(entry);
            state.MergeOpen = true;
        }

        private static void OnUndone(DocState state, List<string> names)
        {
            var done = state.DoneTopDown();
            if (done.Count > 0 && names.Count > 0)
            {
                var exact = done.FirstOrDefault(x => x.Exact && x.Matches(names));
                if (exact != null)
                {
                    var index = done.IndexOf(exact);
                    // Revit undoes the newest step first: an agent run above this one had no step in the Revit undo list
                    // (its code rolled back all its changes), so it is not a real entry.
                    foreach (var phantom in done.Take(index).Where(x => x.Exact).ToList()) state.Entries.Remove(phantom);
                    for (var i = 0; i <= index; i++) done[i].Undone = true;
                }
                else if (!done[0].Exact)
                {
                    // Not an agent run. Revit can merge several commits into one undo step, so this is a best guess;
                    // the next exact match corrects it.
                    done[0].Undone = true;
                }
                else
                {
                    state.Lose("An undo of '" + string.Join(", ", names) + "' did not match the journal");
                }
            }
            Job?.OnUndone(state, names);
        }

        private static void OnRedone(DocState state, List<string> names)
        {
            var undone = state.Entries.Where(x => x.Undone).ToList();   // lowest first: the next redo
            if (undone.Count == 0 || names.Count == 0) return;
            var exact = undone.FirstOrDefault(x => x.Exact && x.Matches(names));
            if (exact != null)
            {
                var index = undone.IndexOf(exact);
                if (undone.Take(index).Any(x => x.Exact))
                    state.Lose("A redo of '" + string.Join(", ", names) + "' did not follow the order in the journal");
                else
                    for (var i = 0; i <= index; i++) undone[i].Undone = false;
            }
            else if (!undone[0].Exact)
            {
                undone[0].Undone = false;
            }
            else
            {
                state.Lose("A redo of '" + string.Join(", ", names) + "' did not match the journal");
            }
        }

        private static void DropRolledBack(DocState state, List<string> names)
        {
            // Another add-in rolled back its TransactionGroup: its inner commits are not in the undo list.
            var top = state.Entries.Count > 0 ? state.Entries[state.Entries.Count - 1] : null;
            if (top != null && !top.FromMcp && !top.Undone && top.Matches(names))
                state.Entries.RemoveAt(state.Entries.Count - 1);
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                lock (Gate)
                {
                    var state = StateOf(e.Document, false);
                    if (state == null) return;
                    States.Remove(state);
                    if (Job != null && ReferenceEquals(Job.State, state)) Job.Fail("The document closed during the undo.");
                }
            }
            catch
            {
            }
        }

        private static void OnDocumentSaving(object sender, DocumentSavingEventArgs e) =>
            SideEffect("It saved '" + Title(e.Document) + "' to disk. Undo changes the open model, not the saved file.");

        private static void OnDocumentSavingAs(object sender, DocumentSavingAsEventArgs e) =>
            SideEffect("It saved '" + Title(e.Document) + "' as '" + e.PathName + "'. Undo does not change files on disk.");

        private static void OnSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e) =>
            SideEffect("It synchronized '" + Title(e.Document) + "' with central. Other users can already have the changes, and the undo list is cleared.");

        private static void OnSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs e) =>
            Lose(e.Document, "Synchronize with Central cleared the Revit undo list");

#if !REVIT2020
        private static void OnReloadedLatest(object sender, DocumentReloadedLatestEventArgs e) =>
            Lose(e.Document, "Reload Latest cleared the Revit undo list");
#endif

        private static void SideEffect(string text)
        {
            try
            {
                lock (Gate) ActiveRun?.AddSideEffect(text);
            }
            catch
            {
            }
        }

        private static void Lose(Document doc, string reason)
        {
            try
            {
                lock (Gate) StateOf(doc, true)?.Lose(reason);
            }
            catch
            {
            }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                lock (Gate)
                {
                    foreach (var s in States) s.MergeOpen = false;
                    var job = Job;
                    if (job == null || job.Status != UndoJob.Running) return;
                    e.SetRaiseWithoutDelay();
                    job.Pump(sender as UIApplication);
                }
            }
            catch
            {
            }
        }
    }
}
