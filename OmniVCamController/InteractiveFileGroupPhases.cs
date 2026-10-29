using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private enum InteractiveGroupInterruptionMode { AfterMiddle, Any, ImmediateMiddle, WholeGroup }
        private enum InteractiveGroupPhase { Middle, Start, End }

        private sealed class InteractiveFileGroupRun
        {
            public Guid PlaybackUnitId { get; } = Guid.NewGuid();
            public Guid RouteId { get; set; }
            public InteractiveMediaItem Media { get; set; }
            public InteractiveFileGroupLoop Loop { get; set; }
            public InteractivePlaybackEntry Outro { get; set; }
            public bool Ending { get; set; }
            public int CompletedMiddleFiles { get; set; }
            public List<InteractivePlaybackEntry> DefaultHandoffMiddles { get; set; }
        }

        private static bool HasInteractiveGroupBoundaries(InteractiveMediaItem media)
        {
            return media.IsFileGroup && (media.ForcePhasedPlayback || media.StartFilePaths.Count > 0 || media.EndFilePaths.Count > 0);
        }

        private static IEnumerable<string> EnumerateInteractiveMediaFiles(InteractiveMediaItem media)
        {
            return media.FilePaths.Concat(media.StartFilePaths).Concat(media.EndFilePaths);
        }

        private static string InteractiveGroupInterruptionKey(InteractiveGroupInterruptionMode mode)
        {
            return mode == InteractiveGroupInterruptionMode.WholeGroup ? "FileGroupInterruptNone" :
                mode == InteractiveGroupInterruptionMode.Any ? "FileGroupInterruptAny" :
                mode == InteractiveGroupInterruptionMode.ImmediateMiddle ? "FileGroupInterruptMiddle" : "FileGroupInterruptDefault";
        }

        private string SelectInteractiveBoundaryPath(InteractiveMediaItem media, bool start)
        {
            var paths = start ? media.StartFilePaths : media.EndFilePaths;
            if (paths.Count == 0) return null;
            if (start ? media.StartFileRandom : media.EndFileRandom)
                return SelectInteractiveWeightedFile(paths, start ? media.StartFileRules : media.EndFileRules,
                    start ? media.Selection.RecentStartFiles : media.Selection.RecentEndFiles, paths.Count, random);
            int index = (start ? media.Selection.NextStartFileIndex : media.Selection.NextEndFileIndex) % paths.Count;
            if (start) media.Selection.NextStartFileIndex = (index + 1) % paths.Count;
            else media.Selection.NextEndFileIndex = (index + 1) % paths.Count;
            return paths[index];
        }

        private InteractivePlaybackEntry CreateInteractivePhasedEntry(InteractiveFileGroupRun run, string path, InteractiveGroupPhase phase)
        {
            string options = NormalizeInteractiveLoopOption(NormalizeQueueOptions(path, run.Media.Options, false), false);
            return new InteractivePlaybackEntry
            {
                PhaseRun = run, Phase = phase, GroupLoop = run.Loop, RouteId = run.RouteId,
                DefaultPlaybackId = run.Loop?.DefaultPlaybackId ?? Guid.Empty,
                DefaultSwitchInterruptionMode = run.Media.DefaultSwitchInterruptionMode,
                IsGroupDefault = run.Loop?.IsGroupDefault == true, ObsPlayback = run.Loop?.ObsPlayback,
                MediaId = run.Media.RuntimeId, PlaybackUnitId = run.PlaybackUnitId,
                PreventInterrupt = run.Media.FileGroupInterruptionMode != InteractiveGroupInterruptionMode.Any,
                Title = run.Media.Title + " / " + Path.GetFileName(path), Path = path, Options = options,
                InputSummary = BuildInputSummary(path, options), GroupIteration = run.Loop?.Iteration ?? 0
            };
        }

        private List<InteractivePlaybackEntry> BuildInteractivePhasedGroupEntries(InteractiveMediaItem media, InteractiveFileGroupLoop loop)
        {
            var run = loop?.PhaseRun;
            bool first = run == null;
            if (first)
            {
                var snapshot = SnapshotInteractiveFileGroup(media);
                run = new InteractiveFileGroupRun { Media = snapshot };
                if (snapshot.Loop) loop = new InteractiveFileGroupLoop { Media = snapshot, PhaseRun = run };
                run.Loop = loop;
            }
            var entries = new List<InteractivePlaybackEntry>();
            if (first)
            {
                string start = SelectInteractiveBoundaryPath(run.Media, true);
                if (start != null) entries.Add(CreateInteractivePhasedEntry(run, start, InteractiveGroupPhase.Start));
                string end = SelectInteractiveBoundaryPath(run.Media, false);
                if (end != null) run.Outro = CreateInteractivePhasedEntry(run, end, InteractiveGroupPhase.End);
            }
            var paths = SelectInteractiveMediaPaths(run.Media, random);
            if (run.Media.FileGroupInterruptionMode == InteractiveGroupInterruptionMode.AfterMiddle)
            {
                int minimum = Math.Min(10000, Math.Max(0, run.Media.FileGroupMinimumMiddleFiles));
                while (paths.Count < minimum) paths.Add(SelectInteractiveMediaPath(run.Media, random));
            }
            var middle = paths.Select(path => CreateInteractivePhasedEntry(run, path, InteractiveGroupPhase.Middle)).ToList();
            if (middle.Count > 0)
            {
                middle[middle.Count - 1].GroupEnd = loop != null;
            }
            entries.AddRange(middle);
            if (run.Outro != null) entries.Add(run.Outro);
            return entries;
        }

        private void CountCompletedInteractiveMiddle(InteractivePlaybackEntry next)
        {
            var previous = interactiveCurrent;
            if (previous == null || previous == next || previous.PhaseRun == null ||
                previous.Phase != InteractiveGroupPhase.Middle || previous.PhaseMiddleCompleted) return;
            previous.PhaseMiddleCompleted = true;
            if (previous.PhaseRun.CompletedMiddleFiles < int.MaxValue) previous.PhaseRun.CompletedMiddleFiles++;
        }

        private void QueueInteractivePhasedContinuation()
        {
            var run = interactiveCurrent?.PhaseRun;
            if (run == null || run.Ending || run.Loop == null || run.Loop.Cancelled || !interactiveCurrent.GroupEnd ||
                run.Outro == null || interactiveQueue.Count != 1 || interactiveQueue.Peek() != run.Outro) return;
            // The intro is per invocation. Keep the selected outro reserved while only the middle repeats.
            interactiveQueue.Clear();
            run.Loop.Iteration++;
            foreach (var entry in BuildInteractivePhasedGroupEntries(run.Media, run.Loop)) interactiveQueue.Enqueue(entry);
        }

        private void MarkInteractivePhasedReplacement(InteractivePlaybackEntry next, InteractivePlaybackEntry discarded)
        {
            next.RequiresOutputConfirmation = true;
            next.MinimumBackendFrameId = Math.Max(next.MinimumBackendFrameId, Math.Max(interactiveObservedBackendFrameId,
                Math.Max(Math.Max(interactiveCurrent?.BackendFrameId ?? 0, interactiveCurrent?.MinimumBackendFrameId ?? 0),
                    Math.Max(discarded.BackendFrameId, discarded.MinimumBackendFrameId))));
        }

        private async Task<bool> TryWaitForInteractivePhasedHandoffAsync(InteractiveTriggerRequest request)
        {
            var run = interactiveActive ? interactiveCurrent?.PhaseRun : null;
            var mode = InteractiveInterruptionMode(interactiveCurrent, request);
            if (run == null || mode == InteractiveGroupInterruptionMode.Any) return false;
            var future = new[] { interactivePending }.Concat(interactiveQueue).Where(entry => entry != null &&
                entry.PhaseRun == run && entry.Phase == InteractiveGroupPhase.Middle &&
                entry.GroupIteration == interactiveCurrent.GroupIteration).ToList();
            if (request.IsDefaultPlaybackSwitch && interactiveCurrent.IsGroupDefault && run.DefaultHandoffMiddles == null)
                run.DefaultHandoffMiddles = future.ToList();
            // A real project can supersede a waiting default switch and must then
            // recover the library-protected tail that the switch would have skipped.
            if (run.DefaultHandoffMiddles != null)
            {
                var original = run.DefaultHandoffMiddles;
                int currentIndex = original.IndexOf(interactiveCurrent);
                var restored = original.Skip(currentIndex + 1).Where(entry => entry != interactiveCurrent && !entry.PhaseMiddleCompleted).ToList();
                restored.AddRange(future.Where(entry => !original.Contains(entry)));
                future = restored.Select(entry => entry.BackendFrameId > 0 && entry != interactivePending ? RetainInteractiveComboPending(entry) : entry).ToList();
                run.DefaultHandoffMiddles = future.ToList();
            }
            run.Ending = true;
            if (run.Loop != null) run.Loop.Cancelled = true;
            var tail = new List<InteractivePlaybackEntry>();
            if (mode == InteractiveGroupInterruptionMode.WholeGroup &&
                interactiveCurrent.Phase != InteractiveGroupPhase.End)
            {
                // Complete this pass, including any tail reserved before an earlier default switch.
                tail.AddRange(future);
            }
            if (mode == InteractiveGroupInterruptionMode.AfterMiddle &&
                interactiveCurrent.Phase != InteractiveGroupPhase.End)
            {
                int minimum = Math.Min(10000, Math.Max(0, run.Media.FileGroupMinimumMiddleFiles));
                int needed = Math.Max(0, minimum - run.CompletedMiddleFiles - (interactiveCurrent.Phase == InteractiveGroupPhase.Middle ? 1 : 0));
                tail.AddRange(future.Take(needed));
                // A switch-only override must not change normal selection counts. Extend
                // the protected tail only when this switch actually requests a minimum.
                if (request.IsDefaultPlaybackSwitch && interactiveCurrent.IsGroupDefault)
                    while (tail.Count < needed)
                    {
                        var middle = CreateInteractivePhasedEntry(run, SelectInteractiveMediaPath(run.Media, random), InteractiveGroupPhase.Middle);
                        middle.GroupIteration = interactiveCurrent.GroupIteration;
                        tail.Add(middle);
                    }
            }
            if (interactiveCurrent.Phase != InteractiveGroupPhase.End && run.Outro != null) tail.Add(run.Outro);
            bool immediate = interactiveCurrent.Phase == InteractiveGroupPhase.Middle &&
                mode == InteractiveGroupInterruptionMode.ImmediateMiddle;
            var handoff = new InteractiveProtectedHandoff { FirstEntry = request.Entries[0], Request = request };
            interactiveProtectedHandoff = handoff;
            AppendInteractiveLog(T("InteractiveWaitingProtected") + interactiveCurrent.Title);
            if (immediate)
            {
                tail.AddRange(request.Entries);
                await PlayInteractiveEntriesImmediatelyAsync(tail);
            }
            else
            {
                var discarded = interactivePending;
                bool keepPending = discarded != null && tail.Count > 0 && tail[0] == discarded;
                interactiveQueue.Clear();
                if (keepPending) tail.RemoveAt(0);
                else
                {
                    interactivePending = null;
                    interactivePendingSource = string.Empty;
                }
                tail.AddRange(request.Entries);
                if (!keepPending && discarded != null) MarkInteractivePhasedReplacement(tail[0], discarded);
                foreach (var entry in tail) interactiveQueue.Enqueue(entry);
                interactiveStatusRevision++;
                await EnsureInteractiveNextQueuedAsync();
            }
            // Serial trigger dispatch resumes only after the requested destination is confirmed at output.
            await handoff.Completion.Task;
            return true;
        }

        private static void CopyInteractiveGroupBoundaries(InteractiveMediaItem source, InteractiveMediaItem target)
        {
            target.StartFilePaths = source.StartFilePaths.ToList(); target.EndFilePaths = source.EndFilePaths.ToList();
            target.StartFileRandom = source.StartFileRandom; target.EndFileRandom = source.EndFileRandom;
            target.FileGroupInterruptionMode = source.FileGroupInterruptionMode;
            target.FileGroupMinimumMiddleFiles = Math.Min(10000, Math.Max(0, source.FileGroupMinimumMiddleFiles));
            target.FileRules = source.FileRules.Clone(); target.StartFileRules = source.StartFileRules.Clone(); target.EndFileRules = source.EndFileRules.Clone();
        }

        private static void SaveInteractiveGroupBoundaries(XElement element, InteractiveMediaItem media)
        {
            element.SetAttributeValue("interruptionMode", media.FileGroupInterruptionMode.ToString());
            element.SetAttributeValue("minimumMiddleFiles", media.FileGroupMinimumMiddleFiles);
            var start = SaveInteractiveFileChoices("StartFiles", media.StartFilePaths, media.StartFileRules);
            start.SetAttributeValue("selection", media.StartFileRandom ? "random" : "sequential");
            var end = SaveInteractiveFileChoices("EndFiles", media.EndFilePaths, media.EndFileRules);
            end.SetAttributeValue("selection", media.EndFileRandom ? "random" : "sequential");
            element.Add(start, end);
        }

        private static void LoadInteractiveGroupBoundaries(XElement element, InteractiveMediaItem media)
        {
            var start = element.Element("StartFiles"); var end = element.Element("EndFiles");
            media.StartFilePaths = NormalizeInteractiveGroupFiles(start?.Elements("File").Select(file => GetAttribute(file, "path", string.Empty)) ?? Enumerable.Empty<string>());
            media.EndFilePaths = NormalizeInteractiveGroupFiles(end?.Elements("File").Select(file => GetAttribute(file, "path", string.Empty)) ?? Enumerable.Empty<string>());
            media.StartFileRules = LoadInteractiveFileChoiceRules(start); media.EndFileRules = LoadInteractiveFileChoiceRules(end);
            media.StartFileRandom = GetAttribute(start ?? new XElement("StartFiles"), "selection", "sequential") == "random";
            media.EndFileRandom = GetAttribute(end ?? new XElement("EndFiles"), "selection", "sequential") == "random";
            int minimum;
            media.FileGroupMinimumMiddleFiles = int.TryParse(GetAttribute(element, "minimumMiddleFiles", "0"), out minimum) ?
                Math.Max(0, Math.Min(10000, minimum)) : 0;
            InteractiveGroupInterruptionMode mode;
            media.FileGroupInterruptionMode = Enum.TryParse(GetAttribute(element, "interruptionMode", string.Empty), out mode) &&
                Enum.IsDefined(typeof(InteractiveGroupInterruptionMode), mode) ? mode : InteractiveGroupInterruptionMode.AfterMiddle;
        }

        private sealed partial class FileGroupEditDialog
        {
            private InteractiveMediaItem phaseSettings;
            private Button phaseButton;
            private readonly Label middleFilesLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top };
            private void RefreshBoundarySettings()
            {
                preventInterruptBox.Visible = !HasInteractiveGroupBoundaries(phaseSettings);
                middleFilesLabel.Text = T(HasInteractiveGroupBoundaries(phaseSettings) ? "FileGroupMiddleFiles" : "InteractiveFileGroupFiles");
                phaseButton.Text = TF("FileGroupBoundaryCounts", phaseSettings.StartFilePaths.Count, phaseSettings.EndFilePaths.Count);
            }
            private void EditMiddleFileRules()
            {
                using (var dialog = new FileGroupFileRulesDialog(files, phaseSettings.FileRules))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) phaseSettings.FileRules = dialog.Result;
                }
            }
            private void EditBoundarySettings()
            {
                phaseSettings.FilePaths = files.ToList();
                using (var dialog = new FileGroupBoundaryEditDialog(phaseSettings))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    dialog.Apply(phaseSettings);
                }
                RefreshBoundarySettings();
            }
        }

        private sealed class FileGroupBoundaryEditDialog : Form
        {
            private readonly List<string> startFiles;
            private readonly List<string> endFiles;
            private readonly ListBox startFilesBox = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
            private readonly ListBox endFilesBox = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
            private readonly ComboBox startModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            private readonly ComboBox endModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            private InteractiveFileChoiceRules startRules, endRules;
            private readonly NumericUpDown minimumMiddleBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Width = 90 };
            private readonly ComboBox interruptionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

            public FileGroupBoundaryEditDialog(InteractiveMediaItem media)
            {
                startFiles = media.StartFilePaths.ToList(); endFiles = media.EndFilePaths.ToList();
                startModeInitial = media.StartFileRandom; endModeInitial = media.EndFileRandom;
                startRules = media.StartFileRules.Clone(); endRules = media.EndFileRules.Clone();
                Text = T("FileGroupBoundarySettings"); ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                Size = new Size(940, 680); MinimumSize = new Size(820, 620);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 5 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(BuildBoundaryPanel(true), 0, 0); root.Controls.Add(BuildBoundaryPanel(false), 1, 0);
                interruptionBox.Items.AddRange(new object[] { T("FileGroupInterruptDefault"), T("FileGroupInterruptAny"), T("FileGroupInterruptMiddle"), T("FileGroupInterruptNone") });
                interruptionBox.SelectedIndex = (int)media.FileGroupInterruptionMode;
                var policy = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
                policy.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); policy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                policy.Controls.Add(new Label { Text = T("FileGroupInterruption"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                policy.Controls.Add(interruptionBox, 1, 0); root.Controls.Add(policy, 0, 1); root.SetColumnSpan(policy, 2);
                minimumMiddleBox.Value = Math.Max(0, Math.Min(minimumMiddleBox.Maximum, media.FileGroupMinimumMiddleFiles));
                minimumMiddleBox.Enabled = interruptionBox.SelectedIndex == 0;
                interruptionBox.SelectedIndexChanged += (_, __) => minimumMiddleBox.Enabled = interruptionBox.SelectedIndex == 0;
                var minimumPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                minimumPanel.Controls.Add(new Label { Text = T("FileGroupMinimumMiddleFiles"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
                minimumPanel.Controls.Add(minimumMiddleBox);
                minimumPanel.Controls.Add(new Label { Text = T("FileGroupMinimumMiddleHint"), AutoSize = true, MaximumSize = new Size(700, 0) });
                root.Controls.Add(minimumPanel, 0, 2); root.SetColumnSpan(minimumPanel, 2);
                var hint = new Label { Text = T("FileGroupBoundaryHint"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8) };
                root.Controls.Add(hint, 0, 3); root.SetColumnSpan(hint, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 4); root.SetColumnSpan(buttons, 2);
                Controls.Add(root); AcceptButton = ok; CancelButton = cancel;
            }

            private Control BuildBoundaryPanel(bool start)
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(4) };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.Controls.Add(new Label { Text = T(start ? "FileGroupStartFiles" : "FileGroupEndFiles"), AutoSize = true }, 0, 0);
                var mode = start ? startModeBox : endModeBox;
                mode.Items.AddRange(new object[] { T("InteractiveSequential"), T("InteractiveRandom") });
                panel.Controls.Add(mode, 0, 1);
                mode.SelectedIndex = (start ? startModeInitial : endModeInitial) ? 1 : 0;
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(MakeButton(T("AddFiles"), (_, __) => AddFiles(start)));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveFile(start)));
                bar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveFile(start, -1)));
                bar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveFile(start, 1)));
                bar.Controls.Add(MakeButton(T("FileGroupFileRules"), (_, __) => EditFileRules(start)));
                panel.Controls.Add(bar, 0, 2); panel.Controls.Add(start ? startFilesBox : endFilesBox, 0, 3);
                RefreshFiles(start, -1);
                return panel;
            }

            private bool startModeInitial, endModeInitial;
            private void AddFiles(bool start)
            {
                using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("InteractiveMediaFilter") })
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(start, dialog.FileNames);
            }
            private void AddPaths(bool start, IEnumerable<string> paths)
            {
                var list = start ? startFiles : endFiles;
                var normalized = NormalizeInteractiveGroupFiles(list.Concat(paths)); list.Clear(); list.AddRange(normalized);
                RefreshFiles(start, list.Count - 1);
            }
            private void RemoveFile(bool start)
            {
                var box = start ? startFilesBox : endFilesBox; var list = start ? startFiles : endFiles;
                if (box.SelectedIndex < 0) return;
                int index = box.SelectedIndex; list.RemoveAt(index); RefreshFiles(start, Math.Min(index, list.Count - 1));
            }
            private void MoveFile(bool start, int delta)
            {
                var box = start ? startFilesBox : endFilesBox; var list = start ? startFiles : endFiles;
                int index = box.SelectedIndex, target = index + delta;
                if (index < 0 || target < 0 || target >= list.Count) return;
                string path = list[index]; list.RemoveAt(index); list.Insert(target, path); RefreshFiles(start, target);
            }
            private void RefreshFiles(bool start, int index)
            {
                var box = start ? startFilesBox : endFilesBox; var list = start ? startFiles : endFiles;
                box.Items.Clear(); foreach (string path in list) box.Items.Add(Path.GetFileName(path) + "  |  " + path);
                box.SelectedIndex = index;
            }
            private void EditFileRules(bool start)
            {
                using (var dialog = new FileGroupFileRulesDialog(start ? startFiles : endFiles, start ? startRules : endRules))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (start) startRules = dialog.Result; else endRules = dialog.Result;
                }
            }
            public void Apply(InteractiveMediaItem media)
            {
                media.StartFilePaths = startFiles.ToList(); media.EndFilePaths = endFiles.ToList();
                media.StartFileRandom = startModeBox.SelectedIndex == 1; media.EndFileRandom = endModeBox.SelectedIndex == 1;
                media.FileGroupInterruptionMode = (InteractiveGroupInterruptionMode)interruptionBox.SelectedIndex;
                media.FileGroupMinimumMiddleFiles = (int)minimumMiddleBox.Value;
                media.StartFileRules = startRules.Clone(); media.EndFileRules = endRules.Clone();
            }
        }
    }
}
