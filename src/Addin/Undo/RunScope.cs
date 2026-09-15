using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace FirstOption.RevitMcp.Addin.Undo
{
    /// <summary>
    /// Wraps one agent run in a TransactionGroup, so the whole run is one named entry in the Revit undo list
    /// (Assimilate on success) and a failed run leaves no change (RollBack, also for use_transaction=false).
    /// CSharpRunner uses it directly; startup.py finds it by reflection. Keep the public members stable.
    /// </summary>
    public sealed class RunScope
    {
        private static long _seq;

        private TransactionGroup _group;
        private bool _ended;
        private readonly List<string> _notes = new List<string>();

        internal Document Doc;
        internal bool UsesGroup;
        internal bool HadCommits;
        /// <summary>The commits inside the undo group that are still in the model. A rolled-back inner TransactionGroup removes its commits.</summary>
        internal readonly List<UndoEntry> GroupCommits = new List<UndoEntry>();
        internal readonly UndoEntry Pending = new UndoEntry();
        internal readonly List<UndoEntry> Pushed = new List<UndoEntry>();
        internal readonly List<string> SideEffects = new List<string>();

        public string RunId { get; private set; }
        public string UndoName { get; private set; }
        public string Error { get; private set; }

        public static RunScope Begin(object uiappObject, string transactionName, string language, bool undoGroup)
        {
            var scope = new RunScope();
            var seq = System.Threading.Interlocked.Increment(ref _seq);
            scope.RunId = "run-" + seq;
            scope.UndoName = (string.IsNullOrWhiteSpace(transactionName) ? "FirstOption MCP" : transactionName.Trim()) + " #" + seq;
            try
            {
                var doc = (uiappObject as UIApplication)?.ActiveUIDocument?.Document;
                if (doc == null) return scope;
                scope.Doc = doc;
                UndoJournal.BeginRun(scope);   // commits from now on belong to this run
                if (!undoGroup)
                    scope._notes.Add("undo_group=false: every transaction of this run is a separate entry in the Revit undo list.");
                else if (doc.IsModifiable)
                    scope._notes.Add("A transaction was already open, so the run has no undo group.");
                else if (doc.IsReadOnly)
                    scope._notes.Add("The document is read-only.");
                else
                {
                    scope._group = new TransactionGroup(doc, scope.UndoName);
                    scope._group.Start();
                    scope.UsesGroup = true;
                }
            }
            catch (Exception ex)
            {
                scope._notes.Add("The undo group did not start: " + ex.Message);
                scope.UsesGroup = false;
                scope._group?.Dispose();
                scope._group = null;
            }
            return scope;
        }

        /// <summary>Call once, after the inner Transaction ended. ok=true merges the run into one undo entry; ok=false rolls it all back.</summary>
        public void End(bool ok)
        {
            if (_ended) return;
            _ended = true;
            var assimilated = false;
            try
            {
                if (_group != null && _group.HasStarted() && !_group.HasEnded())
                {
                    if (ok)
                    {
                        var status = _group.Assimilate();
                        assimilated = status == TransactionStatus.Committed;
                        if (!assimilated) Error = "The undo group ended with status " + status + ". The run was rolled back.";
                    }
                    else
                    {
                        _group.RollBack();
                    }
                }
            }
            catch (Exception ex)
            {
                Error = "The undo group did not close (" + ex.Message + "). The run was rolled back. Does the code leave a transaction open?";
                try
                {
                    if (_group.HasStarted() && !_group.HasEnded()) _group.RollBack();
                }
                catch
                {
                    // nothing more to do
                }
            }
            finally
            {
                _group?.Dispose();
                if (Doc != null) UndoJournal.EndRun(this, assimilated);
                if (UsesGroup && !ok) _notes.Add("The run failed. The undo group rolled back every change of the run.");
            }
        }

        internal void OnCommit(DocState state, List<string> names, DocumentChangedEventArgs e)
        {
            if (UsesGroup)
            {
                if (!HadCommits) state.DropRedo();
                HadCommits = true;
                var commit = new UndoEntry();
                commit.Names.AddRange(names);
                commit.AddChanges(e);
                GroupCommits.Add(commit);
                return;
            }
            var entry = new UndoEntry { Seq = UndoJournal.NextSeq(), FromMcp = true, RunId = RunId };
            entry.Names.AddRange(names);
            entry.AddChanges(e);
            state.Push(entry);
            Pushed.Add(entry);
        }

        /// <summary>
        /// The code of the run rolled back a TransactionGroup. Revit lists the entries that the group removed, newest first
        /// (tested in Revit 2026), so the same commits are removed here: a run that rolled back all its changes gets no entry.
        /// An assimilated inner group is listed by its group name, which the journal never saw; the matching stops there,
        /// and UndoJob skips such an entry when Revit shows that it had no undo step.
        /// </summary>
        internal void OnGroupRolledBack(DocState state, List<string> names)
        {
            foreach (var name in names)
            {
                if (UsesGroup)
                {
                    var last = GroupCommits.Count - 1;
                    if (last < 0 || !GroupCommits[last].Names.Contains(name)) return;
                    GroupCommits.RemoveAt(last);
                }
                else
                {
                    var top = state.Entries.Count > 0 ? state.Entries[state.Entries.Count - 1] : null;
                    if (top == null || top.Undone || !Pushed.Contains(top) || !top.Names.Contains(name)) return;
                    state.Entries.RemoveAt(state.Entries.Count - 1);
                    Pushed.Remove(top);
                }
            }
        }

        internal void AddSideEffect(string text)
        {
            if (!SideEffects.Contains(text)) SideEffects.Add(text);
        }

        public Dictionary<string, object> Describe()
        {
            var d = new Dictionary<string, object> { ["runId"] = RunId };
            if (Doc == null) return d;
            var exact = Pushed.FirstOrDefault(x => x.Exact);
            d["undoName"] = exact?.Name;
            d["undoEntries"] = Pushed.Count;
            d["changes"] = new Dictionary<string, object>
            {
                ["added"] = Pushed.Sum(x => x.Added.Count),
                ["modified"] = Pushed.Sum(x => x.Modified.Count),
                ["deleted"] = Pushed.Sum(x => x.Deleted.Count),
            };
            if (SideEffects.Count > 0) d["sideEffects"] = SideEffects.ToList();
            if (_notes.Count > 0) d["undoNotes"] = _notes.Distinct().ToList();
            return d;
        }

        public string DescribeJson() => MiniJson.Write(Describe());
    }
}
