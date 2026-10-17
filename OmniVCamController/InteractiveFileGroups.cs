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
        private static bool CanPlayInteractiveMedia(InteractiveMediaItem item)
        {
            return item != null && (item.IsFileGroup
                ? item.FilePaths.Count > 0 && item.FilePaths.All(path => !string.IsNullOrWhiteSpace(path))
                : !string.IsNullOrWhiteSpace(item.Path));
        }

        private static List<string> NormalizeInteractiveGroupFiles(IEnumerable<string> paths)
        {
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string SelectInteractiveMediaPath(InteractiveMediaItem item, Random random)
        {
            if (!item.IsFileGroup) return item.Path;
            if (item.FilePaths.Count == 0) return null;
            if (!item.FileGroupRandom)
            {
                int index = item.NextFileIndex % item.FilePaths.Count;
                item.NextFileIndex = (index + 1) % item.FilePaths.Count;
                return item.FilePaths[index];
            }
            if (!item.FileGroupNoRepeat) return item.FilePaths[random.Next(item.FilePaths.Count)];
            // Draw without replacement. A new round starts only after all files were selected.
            if (item.RemainingFiles.Count == 0) item.RemainingFiles.AddRange(item.FilePaths);
            int selected = random.Next(item.RemainingFiles.Count);
            string path = item.RemainingFiles[selected];
            item.RemainingFiles.RemoveAt(selected);
            return path;
        }

        private static List<string> SelectInteractiveMediaPaths(InteractiveMediaItem item, Random random)
        {
            if (!item.IsFileGroup || !item.FileGroupPlayAll)
                return new List<string> { SelectInteractiveMediaPath(item, random) };
            var paths = item.FilePaths.ToList();
            if (item.FileGroupRandom)
            {
                for (int i = paths.Count - 1; i > 0; i--)
                {
                    int selected = random.Next(i + 1);
                    string path = paths[i];
                    paths[i] = paths[selected];
                    paths[selected] = path;
                }
            }
            return paths;
        }

        private sealed class InteractiveFileGroupSelection
        {
            public int NextFileIndex { get; set; }
            public List<string> RemainingFiles { get; } = new List<string>();
        }

        private sealed class InteractiveFileGroupLoop
        {
            public InteractiveMediaItem Media { get; set; }
            public int Iteration { get; set; }
            public bool Cancelled { get; set; }
        }

        private static InteractiveMediaItem SnapshotInteractiveFileGroup(InteractiveMediaItem item)
        {
            var snapshot = new InteractiveMediaItem
            {
                RuntimeId = item.RuntimeId, Title = item.Title, Options = item.Options, Loop = item.Loop,
                PreventInterrupt = item.PreventInterrupt,
                IsFileGroup = true, FilePaths = item.FilePaths.ToList(), FileGroupRandom = item.FileGroupRandom,
                FileGroupPlayAll = item.FileGroupPlayAll, FileGroupNoRepeat = item.FileGroupNoRepeat,
                Selection = item.Selection
            };
            return snapshot;
        }

        private sealed class InteractiveProtectedHandoff
        {
            public InteractivePlaybackEntry FirstEntry;
            public InteractiveTriggerRequest Request;
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private InteractiveProtectedHandoff PrepareInteractiveProtectedHandoff(InteractiveTriggerRequest request)
        {
            // Protect this invocation, including all members of an all-files group, but not its next repeat.
            interactiveStatusRevision++;
            if (interactiveCurrent.GroupLoop != null) interactiveCurrent.GroupLoop.Cancelled = true;
            var retained = interactiveQueue.Where(entry => entry.PlaybackUnitId == interactiveCurrent.PlaybackUnitId).ToList();
            interactiveQueue.Clear();
            foreach (var entry in retained) interactiveQueue.Enqueue(entry);
            if (interactivePending != null && interactivePending.PlaybackUnitId != interactiveCurrent.PlaybackUnitId)
            {
                request.Entries[0].RequiresOutputConfirmation = true;
                request.Entries[0].MinimumBackendFrameId = Math.Max(interactiveCurrent.BackendFrameId, interactivePending.BackendFrameId);
                interactivePending = null;
                interactivePendingSource = string.Empty;
            }
            foreach (var entry in request.Entries) interactiveQueue.Enqueue(entry);
            var handoff = new InteractiveProtectedHandoff { FirstEntry = request.Entries[0], Request = request };
            interactiveProtectedHandoff = handoff;
            return handoff;
        }

        private async Task WaitForProtectedInteractiveMediaAsync(InteractiveTriggerRequest request)
        {
            var handoff = PrepareInteractiveProtectedHandoff(request);
            AppendInteractiveLog(T("InteractiveWaitingProtected") + interactiveCurrent.Title);
            await EnsureInteractiveNextQueuedAsync();
            // Keep subsequent triggers in order until this request actually starts playing.
            await handoff.Completion.Task;
        }

        private void CompleteInteractiveProtectedHandoff()
        {
            var handoff = interactiveProtectedHandoff;
            if (handoff == null || !handoff.Request.Entries.Contains(interactiveCurrent)) return;
            interactiveProtectedHandoff = null;
            handoff.Completion.TrySetResult(true);
        }

        private void CancelInteractiveProtectedHandoff()
        {
            var handoff = interactiveProtectedHandoff;
            interactiveProtectedHandoff = null;
            handoff?.Completion.TrySetResult(false);
        }

        private static bool SameInteractiveGroupPass(InteractivePlaybackEntry left, InteractivePlaybackEntry right)
        {
            return left != null && right != null && left.GroupLoop != null &&
                left.GroupLoop == right.GroupLoop && left.GroupIteration == right.GroupIteration;
        }

        private void QueueInteractiveGroupContinuation()
        {
            if (interactivePending != null || interactiveQueue.Count > 0 || interactiveCurrent == null ||
                !interactiveCurrent.GroupEnd || interactiveCurrent.GroupLoop == null || interactiveCurrent.GroupLoop.Cancelled) return;
            var group = interactiveCurrent.GroupLoop;
            group.Iteration++;
            foreach (var entry in BuildInteractiveMediaEntries(group.Media, group)) interactiveQueue.Enqueue(entry);
        }

        private InteractivePlaybackEntry PrepareInteractiveRouteAppend()
        {
            // Finish a group pass already playing; discard an unstarted loop destination or prefetched next pass.
            if (interactiveCurrent != null && interactiveCurrent.GroupLoop != null) interactiveCurrent.GroupLoop.Cancelled = true;
            var retained = interactiveQueue.ToList();
            while (retained.Count > 0)
            {
                var tail = retained[retained.Count - 1];
                if (tail.Loop) { retained.RemoveAt(retained.Count - 1); continue; }
                if (tail.GroupLoop == null || !tail.GroupEnd) break;
                tail.GroupLoop.Cancelled = true;
                if (SameInteractiveGroupPass(tail, interactiveCurrent)) break;
                do { retained.RemoveAt(retained.Count - 1); }
                while (retained.Count > 0 && SameInteractiveGroupPass(retained[retained.Count - 1], tail));
            }
            interactiveQueue.Clear();
            foreach (var entry in retained) interactiveQueue.Enqueue(entry);
            if (interactivePending != null && (interactivePending.Loop ||
                (interactivePending.GroupLoop != null && interactiveQueue.Count == 0 && !SameInteractiveGroupPass(interactivePending, interactiveCurrent))))
            {
                var replaced = interactivePending;
                if (replaced.GroupLoop != null) replaced.GroupLoop.Cancelled = true;
                interactivePending = null;
                interactivePendingSource = string.Empty;
                return replaced;
            }
            return null;
        }

        private static string FormatInteractiveMediaTitle(InteractiveMediaItem item)
        {
            return (item.IsFileGroup ? T("InteractiveFileGroupTag") + " " : string.Empty) + item.Title +
                (item.Loop ? " \u21bb" : string.Empty);
        }

        private static string DescribeInteractiveFileGroup(InteractiveMediaItem item)
        {
            string mode = T(item.FileGroupRandom ? "InteractiveRandom" : "InteractiveSequential");
            if (item.FileGroupRandom && item.FileGroupNoRepeat && !item.FileGroupPlayAll) mode += ", " + T("InteractiveFileGroupNoRepeat");
            string scope = T(item.FileGroupPlayAll ? "InteractiveFileGroupAll" : "InteractiveFileGroupOne");
            return TF("InteractiveFileGroupSummary", item.FilePaths.Count, mode + ", " + scope);
        }

        private static XElement SaveInteractiveMediaElement(InteractiveMediaItem item)
        {
            var element = new XElement("Item", new XAttribute("id", item.RuntimeId),
                new XAttribute("title", item.Title), new XAttribute("path", item.Path),
                new XAttribute("options", item.Options), new XAttribute("loop", item.Loop ? "1" : "0"),
                new XAttribute("preventInterrupt", item.PreventInterrupt ? "1" : "0"));
            if (item.IsFileGroup)
            {
                element.SetAttributeValue("type", "fileGroup");
                element.SetAttributeValue("selection", item.FileGroupRandom ? "random" : "sequential");
                element.SetAttributeValue("noRepeat", item.FileGroupNoRepeat ? "1" : "0");
                element.SetAttributeValue("playAll", item.FileGroupPlayAll ? "1" : "0");
                element.Add(new XElement("Files", item.FilePaths.Select(path => new XElement("File", new XAttribute("path", path)))));
            }
            return element;
        }

        private static InteractiveMediaItem LoadInteractiveMediaElement(XElement element)
        {
            Guid id;
            if (!Guid.TryParse(GetAttribute(element, "id", null), out id)) id = Guid.NewGuid();
            var item = new InteractiveMediaItem
            {
                RuntimeId = id,
                Title = GetAttribute(element, "title", string.Empty),
                Path = GetAttribute(element, "path", string.Empty),
                Options = GetAttribute(element, "options", string.Empty),
                Loop = GetAttribute(element, "loop", "0") == "1",
                PreventInterrupt = GetAttribute(element, "preventInterrupt", "0") == "1",
                IsFileGroup = GetAttribute(element, "type", string.Empty) == "fileGroup",
                FileGroupRandom = GetAttribute(element, "selection", "sequential") == "random",
                FileGroupNoRepeat = GetAttribute(element, "noRepeat", "1") == "1",
                FileGroupPlayAll = GetAttribute(element, "playAll", "0") == "1"
            };
            XElement files = element.Element("Files");
            if (item.IsFileGroup && files != null)
                item.FilePaths = NormalizeInteractiveGroupFiles(files.Elements("File").Select(file => GetAttribute(file, "path", string.Empty)));
            return item;
        }

        private sealed class FileGroupEditDialog : Form
        {
            private readonly TextBox titleBox = new TextBox();
            private readonly TextBox optionsBox = new TextBox();
            private readonly ComboBox selectionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            private readonly ComboBox scopeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            private readonly CheckBox preventInterruptBox = new CheckBox { AutoSize = true };
            private readonly CheckBox noRepeatBox = new CheckBox { AutoSize = true };
            private readonly ListBox filesBox = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
            private readonly List<string> files;

            public FileGroupEditDialog(InteractiveMediaItem item)
            {
                files = NormalizeInteractiveGroupFiles(item.FilePaths);
                Text = T("InteractiveEditFileGroup");
                ShowIcon = false;
                Size = new Size(850, 660);
                MinimumSize = new Size(700, 560);
                StartPosition = FormStartPosition.CenterParent;
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(10) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int row = 0; row < 6; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);
                AddDialogRow(root, T("Title"), titleBox, 0);
                AddDialogRow(root, T("Options"), optionsBox, 1);
                selectionBox.Items.AddRange(new object[] { T("InteractiveSequential"), T("InteractiveRandom") });
                selectionBox.SelectedIndex = item.FileGroupRandom ? 1 : 0;
                AddDialogRow(root, T("InteractiveFileGroupPlayback"), selectionBox, 2);
                scopeBox.Items.AddRange(new object[] { T("InteractiveFileGroupOne"), T("InteractiveFileGroupAll") });
                scopeBox.SelectedIndex = item.FileGroupPlayAll ? 1 : 0;
                AddDialogRow(root, T("InteractiveFileGroupScope"), scopeBox, 3);
                var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                noRepeatBox.Text = T("InteractiveFileGroupNoRepeat");
                noRepeatBox.Checked = item.FileGroupNoRepeat;
                selectionBox.SelectedIndexChanged += (_, __) => UpdatePlaybackOptions();
                scopeBox.SelectedIndexChanged += (_, __) => UpdatePlaybackOptions();
                UpdatePlaybackOptions();
                flags.Controls.Add(noRepeatBox);
                preventInterruptBox.Text = T("InteractivePreventInterrupt");
                preventInterruptBox.Checked = item.PreventInterrupt;
                flags.Controls.Add(preventInterruptBox);
                root.Controls.Add(flags, 1, 4);
                var hint = new Label { Text = T("InteractiveFileGroupHint"), AutoSize = true, MaximumSize = new Size(660, 0) };
                root.Controls.Add(hint, 0, 5);
                root.SetColumnSpan(hint, 2);
                root.Controls.Add(new Label { Text = T("InteractiveFileGroupFiles"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 6);
                var filePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                filePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                filePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(MakeButton(T("AddFiles"), (_, __) => AddFiles()));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveFile()));
                bar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveFile(-1)));
                bar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveFile(1)));
                filePanel.Controls.Add(bar, 0, 0);
                filePanel.Controls.Add(filesBox, 0, 1);
                root.Controls.Add(filePanel, 1, 6);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) =>
                {
                    if (string.IsNullOrWhiteSpace(titleBox.Text) || files.Count == 0)
                    {
                        MessageBox.Show(this, T("InteractiveFileGroupRequired"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                root.Controls.Add(buttons, 1, 7);
                AcceptButton = ok;
                CancelButton = cancel;
                titleBox.Text = item.Title;
                optionsBox.Text = item.Options;
                RefreshFiles(files.Count > 0 ? 0 : -1);
            }

            private void UpdatePlaybackOptions()
            {
                noRepeatBox.Enabled = selectionBox.SelectedIndex == 1 && scopeBox.SelectedIndex == 0;
            }

            private void AddFiles()
            {
                using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("InteractiveMediaFilter") })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
                }
            }

            private void AddPaths(IEnumerable<string> paths)
            {
                files.AddRange(paths);
                var normalized = NormalizeInteractiveGroupFiles(files);
                files.Clear();
                files.AddRange(normalized);
                RefreshFiles(files.Count - 1);
            }

            private void RemoveFile()
            {
                int index = filesBox.SelectedIndex;
                if (index < 0) return;
                files.RemoveAt(index);
                RefreshFiles(Math.Min(index, files.Count - 1));
            }

            private void MoveFile(int delta)
            {
                int index = filesBox.SelectedIndex, target = index + delta;
                if (index < 0 || target < 0 || target >= files.Count) return;
                string path = files[index];
                files.RemoveAt(index);
                files.Insert(target, path);
                RefreshFiles(target);
            }

            private void RefreshFiles(int index)
            {
                filesBox.Items.Clear();
                foreach (string path in files) filesBox.Items.Add(Path.GetFileName(path) + "  |  " + path);
                filesBox.SelectedIndex = index;
            }

            public void Apply(InteractiveMediaItem item)
            {
                item.IsFileGroup = true;
                item.Path = string.Empty;
                item.Title = titleBox.Text.Trim();
                item.Options = optionsBox.Text.Trim();
                item.FilePaths = files.ToList();
                item.FileGroupRandom = selectionBox.SelectedIndex == 1;
                item.FileGroupPlayAll = scopeBox.SelectedIndex == 1;
                item.FileGroupNoRepeat = noRepeatBox.Checked;
                item.PreventInterrupt = preventInterruptBox.Checked;
                item.Selection = new InteractiveFileGroupSelection();
            }
        }
    }
}
