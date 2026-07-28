using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private List<InteractiveMediaItem> interactiveMedia = new List<InteractiveMediaItem>();
        private List<InteractiveRouteItem> interactiveRoutes = new List<InteractiveRouteItem>();
        private readonly Queue<InteractivePlaybackEntry> interactiveQueue = new Queue<InteractivePlaybackEntry>();
        private InteractivePlaybackEntry interactiveCurrent;
        private InteractivePlaybackEntry interactivePending;
        private string interactivePendingSource = string.Empty;
        private bool interactiveActive;
        private bool processingInteractiveTriggers;
        private readonly Queue<InteractiveTriggerRequest> interactiveTriggerRequests = new Queue<InteractiveTriggerRequest>();
        private InteractivePlaybackForm interactiveForm;

        private void BuildInteractiveWindow()
        {
            interactiveForm = new InteractivePlaybackForm(this);
            interactiveForm.FormClosing += (_, e) =>
            {
                if (!closingMainForm)
                {
                    e.Cancel = true;
                    interactiveForm.Hide();
                }
            };
        }

        private void OpenInteractiveWindow()
        {
            if (interactiveForm == null || interactiveForm.IsDisposed) BuildInteractiveWindow();
            interactiveForm.ReloadAll();
            interactiveForm.Show();
            interactiveForm.BringToFront();
        }

        private void ClearInteractiveRuntime(bool clearTriggerRequests = true)
        {
            interactiveActive = false;
            interactiveCurrent = null;
            interactivePending = null;
            interactivePendingSource = string.Empty;
            interactiveQueue.Clear();
            if (clearTriggerRequests) interactiveTriggerRequests.Clear();
            interactiveForm?.RefreshRuntime();
        }

        private bool HandleInteractiveBackendEvent(string eventLine)
        {
            if (!interactiveActive) return false;
            string inputSummary = ParseStatusString(eventLine, "input=");
            if (eventLine.StartsWith("INPUT_EOF", StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleInteractiveEofAsync(inputSummary);
                return true;
            }
            if (eventLine.StartsWith("INPUT_ENDED", StringComparison.OrdinalIgnoreCase))
            {
                _ = HandleInteractiveEndedAsync(inputSummary);
                return true;
            }
            return false;
        }

        private async Task HandleInteractiveEofAsync(string inputSummary)
        {
            if (!interactiveActive) return;
            if (interactiveCurrent != null && !InputSummariesEqual(inputSummary, interactiveCurrent.InputSummary)) return;

            if (interactivePending == null && interactiveQueue.Count > 0)
                await EnsureInteractiveNextQueuedAsync();
        }

        private async Task HandleInteractiveEndedAsync(string inputSummary)
        {
            if (!interactiveActive) return;
            if (interactivePending != null &&
                (string.IsNullOrEmpty(interactivePendingSource) || InputSummariesEqual(inputSummary, interactivePendingSource)))
            {
                interactiveCurrent = interactivePending;
                interactivePending = null;
                interactivePendingSource = string.Empty;
                AppendInteractiveLog(T("InteractiveNowPlaying") + interactiveCurrent.Title);
                interactiveForm?.RefreshRuntime();
                await EnsureInteractiveNextQueuedAsync();
                return;
            }

            if (interactiveCurrent != null && InputSummariesEqual(inputSummary, interactiveCurrent.InputSummary) &&
                interactivePending == null && interactiveQueue.Count == 0 && !interactiveCurrent.Loop)
            {
                AppendInteractiveLog(T("InteractiveSequenceCompleted"));
                interactiveActive = false;
                interactiveCurrent = null;
                interactiveForm?.RefreshRuntime();
            }
        }

        private void RequestInteractiveRoute(InteractiveRouteItem route, string source)
        {
            if (route == null) return;
            List<InteractivePlaybackEntry> entries = BuildInteractiveEntries(route);
            if (entries.Count == 0)
            {
                AppendInteractiveLog(source + " - " + T("InteractiveNoPlayableMedia"));
                return;
            }

            if (!interactiveActive && interactiveTriggerRequests.Count == 0) BeginInteractiveMode();

            interactiveTriggerRequests.Enqueue(new InteractiveTriggerRequest
            {
                RouteName = route.Name,
                Immediate = route.Immediate,
                Source = source,
                Entries = entries
            });
            AppendInteractiveLog(source + " - " + route.Name + " [" +
                (route.Immediate ? "PLAY" : "PLAY_NEXT") + "] " +
                string.Join(" → ", entries.Select(FormatInteractiveEntry)));
            interactiveForm?.RefreshRuntime();
            if (!processingInteractiveTriggers) _ = ProcessInteractiveTriggerRequestsAsync();
        }

        private void BeginInteractiveMode()
        {
            ClearQueuedPlaybackState(true);
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            SetPlayoutTimerRunning(false);
            UpdatePlayoutStatusLabel();
            interactiveActive = true;
        }

        private async Task ProcessInteractiveTriggerRequestsAsync()
        {
            if (processingInteractiveTriggers) return;
            processingInteractiveTriggers = true;
            try
            {
                while (interactiveTriggerRequests.Count > 0)
                {
                    InteractiveTriggerRequest request = interactiveTriggerRequests.Dequeue();
                    if (request.Immediate)
                        await StartInteractiveImmediatelyAsync(request);
                    else
                        await AppendInteractiveRouteAsync(request);
                    interactiveForm?.RefreshRuntime();
                }
            }
            finally
            {
                processingInteractiveTriggers = false;
            }
        }

        private async Task StartInteractiveImmediatelyAsync(InteractiveTriggerRequest request)
        {
            ClearQueuedPlaybackState(true);
            interactiveQueue.Clear();
            interactivePending = null;
            interactivePendingSource = string.Empty;
            interactiveActive = true;
            interactiveCurrent = request.Entries[0];
            foreach (InteractivePlaybackEntry entry in request.Entries.Skip(1)) interactiveQueue.Enqueue(entry);

            string command = BuildInteractivePlayCommand("PLAY", interactiveCurrent);
            string reply = await SendRawCommandAsync(command);
            AppendInteractiveLog("> " + command + Environment.NewLine + "< " + reply);
            if (reply == null || !reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                AppendInteractiveLog(T("InteractiveImmediateFailed"));
                ClearInteractiveRuntime(false);
                return;
            }
            currentStatusInput = interactiveCurrent.InputSummary;
            currentActualStatusInput = interactiveCurrent.InputSummary;
            await EnsureInteractiveNextQueuedAsync();
        }

        private async Task AppendInteractiveRouteAsync(InteractiveTriggerRequest request)
        {
            interactiveActive = true;

            while (interactiveQueue.Count > 0 && interactiveQueue.Last().Loop)
            {
                var retained = interactiveQueue.Take(interactiveQueue.Count - 1).ToList();
                interactiveQueue.Clear();
                foreach (InteractivePlaybackEntry item in retained) interactiveQueue.Enqueue(item);
            }

            if (interactivePending != null && interactivePending.Loop)
            {
                AppendInteractiveLog(T("InteractiveReplacedLoop") + interactivePending.Title);
                interactivePending = null;
                interactivePendingSource = string.Empty;
            }

            foreach (InteractivePlaybackEntry entry in request.Entries) interactiveQueue.Enqueue(entry);
            await EnsureInteractiveNextQueuedAsync();
        }

        private async Task EnsureInteractiveNextQueuedAsync()
        {
            if (!interactiveActive || interactivePending != null || interactiveQueue.Count == 0) return;
            InteractivePlaybackEntry next = interactiveQueue.Dequeue();
            string sourceSummary = interactiveCurrent != null
                ? interactiveCurrent.InputSummary
                : (!string.IsNullOrEmpty(currentActualStatusInput) ? currentActualStatusInput : currentStatusInput);
            string command = BuildInteractivePlayCommand("PLAY_NEXT", next);
            string reply = await SendRawCommandAsync(command);
            AppendInteractiveLog("> " + command + Environment.NewLine + "< " + reply);
            if (reply != null && reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                interactivePending = next;
                interactivePendingSource = sourceSummary;
            }
            else
            {
                var retained = interactiveQueue.ToList();
                interactiveQueue.Clear();
                interactiveQueue.Enqueue(next);
                foreach (InteractivePlaybackEntry item in retained) interactiveQueue.Enqueue(item);
                AppendInteractiveLog(T("InteractivePlayNextFailed"));
            }
            interactiveForm?.RefreshRuntime();
        }

        private async Task StopInteractiveAsync()
        {
            string reply = await SendRawCommandAsync("PLAY_NEXT");
            AppendInteractiveLog("> PLAY_NEXT" + Environment.NewLine + "< " + reply);
            ClearInteractiveRuntime();
            AppendInteractiveLog(T("InteractiveQueueCleared"));
        }

        private List<InteractivePlaybackEntry> BuildInteractiveEntries(InteractiveRouteItem route)
        {
            var result = new List<InteractivePlaybackEntry>();
            foreach (Guid mediaId in route.MediaIds)
            {
                InteractiveMediaItem media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == mediaId);
                if (media == null || string.IsNullOrWhiteSpace(media.Path)) continue;
                string options = NormalizeInteractiveLoopOption(
                    NormalizeQueueOptions(media.Path, media.Options, false), media.Loop);
                result.Add(new InteractivePlaybackEntry
                {
                    MediaId = media.RuntimeId,
                    Title = string.IsNullOrWhiteSpace(media.Title) ? GetInputTitle(media.Path) : media.Title,
                    Path = media.Path,
                    Options = options,
                    Loop = media.Loop,
                    InputSummary = BuildInputSummary(media.Path, options)
                });
            }
            return result;
        }

        private static string NormalizeInteractiveLoopOption(string options, bool loop)
        {
            var entries = (options ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            entries.RemoveAll(value => value.Equals("loop", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("loop=", StringComparison.OrdinalIgnoreCase));
            if (loop) entries.Add("loop=1");
            return string.Join(",", entries);
        }

        private static string BuildInteractivePlayCommand(string verb, InteractivePlaybackEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.Options)
                ? verb + " " + entry.Path
                : verb + " " + entry.Path + "\t" + entry.Options;
        }

        private static string FormatInteractiveEntry(InteractivePlaybackEntry entry)
        {
            return entry.Title + (entry.Loop ? " ↻" : string.Empty);
        }

        private void AppendInteractiveLog(string text)
        {
            interactiveForm?.AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            AppendLog("[interactive] " + text);
        }

        private string DescribeInteractiveQueue()
        {
            var parts = new List<string>();
            if (interactiveCurrent != null) parts.Add(T("InteractivePlaying") + FormatInteractiveEntry(interactiveCurrent));
            if (interactivePending != null) parts.Add(T("InteractiveNext") + FormatInteractiveEntry(interactivePending));
            parts.AddRange(interactiveQueue.Select((entry, index) => (index + 1) + ". " + FormatInteractiveEntry(entry)));
            parts.AddRange(interactiveTriggerRequests.Select(request => T("InteractivePendingAction") + request.RouteName));
            return parts.Count == 0 ? T("InteractiveQueueEmpty") : string.Join(Environment.NewLine, parts);
        }

        private sealed class InteractivePlaybackForm : Form
        {
            private const int HotkeyBaseId = 0x5100;
            private readonly MainForm owner;
            private readonly ListView mediaView = new ListView();
            private readonly ListView routeView = new ListView();
            private readonly FlowLayoutPanel routeButtons = new FlowLayoutPanel();
            private readonly TextBox queueBox = new TextBox();
            private readonly TextBox operationBox = new TextBox();
            private readonly Label connectionLabel = new Label();
            private readonly Dictionary<int, InteractiveRouteItem> registeredRoutes = new Dictionary<int, InteractiveRouteItem>();

            public InteractivePlaybackForm(MainForm owner)
            {
                this.owner = owner;
                Text = T("InteractivePlayback");
                Size = new Size(1180, 780);
                MinimumSize = new Size(900, 620);
                StartPosition = FormStartPosition.CenterParent;
                ShowIcon = false;
                BuildUi();
            }

            private void BuildUi()
            {
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
                Controls.Add(root);

                var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
                connectionLabel.AutoSize = true;
                connectionLabel.Font = new Font(Font, FontStyle.Bold);
                connectionLabel.Padding = new Padding(0, 6, 12, 0);
                header.Controls.Add(connectionLabel);
                header.Controls.Add(MakeButton(T("SaveXml"), (_, __) => owner.SaveAutoConfig()));
                header.Controls.Add(MakeButton(T("InteractiveClearQueue"), async (_, __) => await owner.StopInteractiveAsync()));
                root.Controls.Add(header, 0, 0);

                routeButtons.Dock = DockStyle.Fill;
                routeButtons.AutoSize = true;
                routeButtons.WrapContents = true;
                routeButtons.Padding = new Padding(0, 6, 0, 6);
                root.Controls.Add(routeButtons, 0, 1);

                var lists = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 550 };
                lists.Panel1.Controls.Add(BuildMediaPanel());
                lists.Panel2.Controls.Add(BuildRoutePanel());
                root.Controls.Add(lists, 0, 2);

                var bottom = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 550 };
                bottom.Panel1.Controls.Add(BuildTextPanel(T("InteractiveQueuePanel"), queueBox, true));
                bottom.Panel2.Controls.Add(BuildTextPanel(T("InteractiveOperations"), operationBox, true));
                root.Controls.Add(bottom, 0, 3);

                mediaView.Columns.Add(T("ColumnTitle"), 150);
                mediaView.Columns.Add(T("InteractiveLoop"), 60);
                mediaView.Columns.Add(T("ColumnOptions"), 150);
                mediaView.Columns.Add(T("ColumnPath"), 420);
                routeView.Columns.Add(T("InteractiveProject"), 160);
                routeView.Columns.Add(T("InteractiveHotkey"), 120);
                routeView.Columns.Add(T("InteractiveCommand"), 90);
                routeView.Columns.Add(T("InteractiveSequence"), 430);
                routeView.DoubleClick += (_, __) => TriggerSelected(T("InteractiveDoubleClickedProject"));
            }

            private Control BuildMediaPanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveMedia"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("AddFiles"), (_, __) => AddMediaFiles()));
                bar.Controls.Add(MakeButton(T("InteractiveEdit"), (_, __) => EditSelectedMedia()));
                bar.Controls.Add(MakeButton(T("InteractiveToggleLoop"), (_, __) => ToggleSelectedMediaLoop()));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedMedia()));
                panel.Controls.Add(bar, 0, 0);
                ConfigureList(mediaView);
                panel.Controls.Add(mediaView, 0, 1);
                return panel;
            }

            private Control BuildRoutePanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveProjects"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("InteractiveAdd"), (_, __) => EditRoute(null)));
                bar.Controls.Add(MakeButton(T("InteractiveEdit"), (_, __) => EditSelectedRoute()));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedRoute()));
                bar.Controls.Add(MakeButton(T("InteractiveTrigger"), (_, __) => TriggerSelected(T("InteractiveClickedProject"))));
                panel.Controls.Add(bar, 0, 0);
                ConfigureList(routeView);
                panel.Controls.Add(routeView, 0, 1);
                return panel;
            }

            private static Control BuildTextPanel(string title, TextBox box, bool readOnly)
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(0, 5, 0, 4) }, 0, 0);
                box.Dock = DockStyle.Fill;
                box.Multiline = true;
                box.ReadOnly = readOnly;
                box.ScrollBars = ScrollBars.Vertical;
                panel.Controls.Add(box, 0, 1);
                return panel;
            }

            private static void ConfigureList(ListView view)
            {
                view.Dock = DockStyle.Fill;
                view.View = View.Details;
                view.FullRowSelect = true;
                view.GridLines = true;
                view.HideSelection = false;
                EnableDoubleBuffering(view);
            }

            public void ReloadAll()
            {
                connectionLabel.Text = T("InteractiveConnection") +
                    (owner.activeState == null ? "-" : owner.GetConnectionTabTitle(owner.activeState));
                RefreshMedia();
                RefreshRoutes();
                RefreshRuntime();
                RegisterRouteHotkeys();
            }

            public void ActivateHotkeys()
            {
                IntPtr unused = Handle;
                ReloadAll();
            }

            private void RefreshMedia()
            {
                mediaView.BeginUpdate();
                mediaView.Items.Clear();
                foreach (InteractiveMediaItem media in owner.interactiveMedia)
                {
                    var row = new ListViewItem(media.Title);
                    row.SubItems.Add(media.Loop ? T("InteractiveYes") : string.Empty);
                    row.SubItems.Add(media.Options);
                    row.SubItems.Add(media.Path);
                    row.Tag = media;
                    if (media.Loop) row.BackColor = Color.FromArgb(225, 245, 225);
                    mediaView.Items.Add(row);
                }
                mediaView.EndUpdate();
            }

            private void RefreshRoutes()
            {
                routeView.BeginUpdate();
                routeView.Items.Clear();
                routeButtons.Controls.Clear();
                foreach (InteractiveRouteItem route in owner.interactiveRoutes)
                {
                    string sequence = owner.DescribeRoute(route);
                    var row = new ListViewItem(route.Name);
                    row.SubItems.Add(FormatHotkey(route.Hotkey));
                    row.SubItems.Add(route.Immediate ? "PLAY" : "PLAY_NEXT");
                    row.SubItems.Add(sequence);
                    row.Tag = route;
                    routeView.Items.Add(row);

                    var captured = route;
                    var button = MakeButton(route.Name + (string.IsNullOrEmpty(FormatHotkey(route.Hotkey)) ? string.Empty : "  [" + FormatHotkey(route.Hotkey) + "]"),
                        (_, __) => owner.RequestInteractiveRoute(captured, T("InteractiveClickedProject")));
                    button.Padding = new Padding(10, 5, 10, 5);
                    routeButtons.Controls.Add(button);
                }
                routeView.EndUpdate();
            }

            public void RefreshRuntime()
            {
                if (IsDisposed) return;
                queueBox.Text = owner.DescribeInteractiveQueue();
            }

            public void AppendOperation(string text)
            {
                if (IsDisposed) return;
                bool followTail = operationBox.SelectionStart + operationBox.SelectionLength >= operationBox.TextLength;
                int selectionStart = operationBox.SelectionStart;
                int selectionLength = operationBox.SelectionLength;
                operationBox.AppendText(text + Environment.NewLine);
                if (operationBox.Lines.Length > 1000)
                    operationBox.Lines = operationBox.Lines.Skip(operationBox.Lines.Length - 1000).ToArray();
                if (followTail)
                {
                    operationBox.SelectionStart = operationBox.TextLength;
                    operationBox.SelectionLength = 0;
                    operationBox.ScrollToCaret();
                }
                else
                {
                    operationBox.SelectionStart = Math.Min(selectionStart, operationBox.TextLength);
                    operationBox.SelectionLength = Math.Min(selectionLength,
                        operationBox.TextLength - operationBox.SelectionStart);
                }
            }

            private void AddMediaFiles()
            {
                using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("InteractiveMediaFilter") })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    foreach (string path in dialog.FileNames)
                    {
                        owner.interactiveMedia.Add(new InteractiveMediaItem
                        {
                            Path = path,
                            Title = Path.GetFileNameWithoutExtension(path)
                        });
                    }
                }
                RefreshMedia();
                owner.SaveActiveConnectionState();
            }

            private InteractiveMediaItem SelectedMedia()
            {
                return mediaView.SelectedItems.Count == 1 ? mediaView.SelectedItems[0].Tag as InteractiveMediaItem : null;
            }

            private void EditSelectedMedia()
            {
                InteractiveMediaItem media = SelectedMedia();
                if (media == null) return;
                using (var dialog = new MediaEditDialog(media))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    dialog.Apply(media);
                }
                RefreshMedia();
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void ToggleSelectedMediaLoop()
            {
                foreach (ListViewItem row in mediaView.SelectedItems)
                {
                    var media = row.Tag as InteractiveMediaItem;
                    if (media != null) media.Loop = !media.Loop;
                }
                RefreshMedia();
                RefreshRoutes();
                owner.SaveActiveConnectionState();
            }

            private void RemoveSelectedMedia()
            {
                var ids = mediaView.SelectedItems.Cast<ListViewItem>().Select(row => row.Tag as InteractiveMediaItem)
                    .Where(item => item != null).Select(item => item.RuntimeId).ToList();
                if (ids.Count == 0) return;
                owner.interactiveMedia.RemoveAll(item => ids.Contains(item.RuntimeId));
                foreach (InteractiveRouteItem route in owner.interactiveRoutes) route.MediaIds.RemoveAll(ids.Contains);
                RefreshMedia();
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private InteractiveRouteItem SelectedRoute()
            {
                return routeView.SelectedItems.Count == 1 ? routeView.SelectedItems[0].Tag as InteractiveRouteItem : null;
            }

            private void EditSelectedRoute()
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route != null) EditRoute(route);
            }

            private void EditRoute(InteractiveRouteItem route)
            {
                using (var dialog = new RouteEditDialog(owner.interactiveMedia, route))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (route == null)
                    {
                        route = new InteractiveRouteItem();
                        owner.interactiveRoutes.Add(route);
                    }
                    dialog.Apply(route);
                }
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void RemoveSelectedRoute()
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route == null) return;
                owner.interactiveRoutes.Remove(route);
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void TriggerSelected(string source)
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route != null) owner.RequestInteractiveRoute(route, source);
            }

            private void RegisterRouteHotkeys()
            {
                if (!IsHandleCreated) return;
                foreach (int id in registeredRoutes.Keys.ToList()) UnregisterHotKey(Handle, id);
                registeredRoutes.Clear();
                int index = 0;
                foreach (InteractiveRouteItem route in owner.interactiveRoutes)
                {
                    if (route.Hotkey == null || route.Hotkey.KeyCode == Keys.None) continue;
                    int id = HotkeyBaseId + index++;
                    if (RegisterHotKey(Handle, id, (uint)route.Hotkey.Modifiers, (uint)route.Hotkey.KeyCode))
                        registeredRoutes[id] = route;
                    else
                        AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + T("InteractiveHotkeyRegisterFailed") + FormatHotkey(route.Hotkey) + " - " + route.Name);
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                RegisterRouteHotkeys();
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                foreach (int id in registeredRoutes.Keys.ToList()) UnregisterHotKey(Handle, id);
                registeredRoutes.Clear();
                base.OnHandleDestroyed(e);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_HOTKEY = 0x0312;
                if (m.Msg == WM_HOTKEY && registeredRoutes.TryGetValue(m.WParam.ToInt32(), out InteractiveRouteItem route))
                {
                    owner.RequestInteractiveRoute(route, T("InteractiveHotkeySource") + FormatHotkey(route.Hotkey));
                    return;
                }
                base.WndProc(ref m);
            }

            [DllImport("user32.dll")]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll")]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }

        private string DescribeRoute(InteractiveRouteItem route)
        {
            return string.Join(" → ", route.MediaIds.Select(id => interactiveMedia.FirstOrDefault(item => item.RuntimeId == id))
                .Where(item => item != null).Select(item => item.Title + (item.Loop ? " ↻" : string.Empty)));
        }

        private sealed class MediaEditDialog : Form
        {
            private readonly TextBox titleBox = new TextBox();
            private readonly TextBox pathBox = new TextBox();
            private readonly TextBox optionsBox = new TextBox();
            private readonly CheckBox loopBox = new CheckBox();

            public MediaEditDialog(InteractiveMediaItem item)
            {
                Text = T("InteractiveEditMedia");
                ShowIcon = false;
                Size = new Size(700, 260);
                StartPosition = FormStartPosition.CenterParent;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(10) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(grid);
                AddDialogRow(grid, T("Title"), titleBox, 0);
                AddDialogRow(grid, T("ColumnPath"), pathBox, 1);
                AddDialogRow(grid, T("Options"), optionsBox, 2);
                loopBox.Text = T("InteractiveLoopPlayback");
                loopBox.AutoSize = true;
                loopBox.Anchor = AnchorStyles.Left;
                loopBox.Margin = new Padding(3, 6, 3, 6);
                grid.Controls.Add(loopBox, 1, 3);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 1, 4);
                AcceptButton = ok;
                CancelButton = cancel;
                titleBox.Text = item.Title;
                pathBox.Text = item.Path;
                optionsBox.Text = item.Options;
                loopBox.Checked = item.Loop;
            }

            public void Apply(InteractiveMediaItem item)
            {
                item.Title = titleBox.Text.Trim();
                item.Path = pathBox.Text.Trim();
                item.Options = optionsBox.Text.Trim();
                item.Loop = loopBox.Checked;
            }
        }

        private sealed class RouteEditDialog : Form
        {
            private readonly List<InteractiveMediaItem> media;
            private readonly TextBox nameBox = new TextBox();
            private readonly ComboBox mediaBox = new ComboBox();
            private readonly ListBox sequenceBox = new ListBox();
            private readonly HotkeyTextBox hotkeyBox = new HotkeyTextBox();
            private readonly ComboBox commandBox = new ComboBox();
            private readonly List<Guid> sequence = new List<Guid>();

            public RouteEditDialog(List<InteractiveMediaItem> media, InteractiveRouteItem route)
            {
                this.media = media;
                Text = T("InteractiveEditProject");
                ShowIcon = false;
                Size = new Size(650, 540);
                MinimumSize = new Size(520, 430);
                StartPosition = FormStartPosition.CenterParent;
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 2, Padding = new Padding(10) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);
                AddDialogRow(root, T("InteractiveName"), nameBox, 0);
                AddDialogRow(root, T("InteractiveHotkey"), hotkeyBox, 1);
                commandBox.DropDownStyle = ComboBoxStyle.DropDownList;
                commandBox.Items.AddRange(new object[] { "PLAY_NEXT", "PLAY" });
                commandBox.SelectedIndex = 0;
                AddDialogRow(root, T("InteractiveTriggerMode"), commandBox, 2);

                var sequencePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                var addBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                mediaBox.DropDownStyle = ComboBoxStyle.DropDownList;
                mediaBox.Width = 250;
                foreach (InteractiveMediaItem item in media) mediaBox.Items.Add(new MediaChoice(item));
                if (mediaBox.Items.Count > 0) mediaBox.SelectedIndex = 0;
                addBar.Controls.Add(mediaBox);
                addBar.Controls.Add(MakeButton(T("InteractiveAddToSequence"), (_, __) => AddSelectedMedia()));
                addBar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSequenceItem()));
                addBar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSequenceItem(-1)));
                addBar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSequenceItem(1)));
                sequencePanel.Controls.Add(addBar, 0, 0);
                sequenceBox.Dock = DockStyle.Fill;
                sequencePanel.Controls.Add(sequenceBox, 0, 1);
                root.Controls.Add(new Label { Text = T("InteractiveSequence"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 5, 0, 0) }, 0, 3);
                root.Controls.Add(sequencePanel, 1, 3);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
                var ok = MakeButton(T("InteractiveOk"), (_, __) =>
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text) || sequence.Count == 0) return;
                    DialogResult = DialogResult.OK;
                });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                root.Controls.Add(buttons, 1, 4);
                AcceptButton = ok;
                CancelButton = cancel;

                if (route != null)
                {
                    nameBox.Text = route.Name;
                    hotkeyBox.Value = route.Hotkey == null ? null : route.Hotkey.Clone();
                    commandBox.SelectedItem = route.Immediate ? "PLAY" : "PLAY_NEXT";
                    sequence.AddRange(route.MediaIds.Where(id => media.Any(item => item.RuntimeId == id)));
                    RefreshSequence();
                }
            }

            private void AddSelectedMedia()
            {
                var choice = mediaBox.SelectedItem as MediaChoice;
                if (choice == null) return;
                sequence.Add(choice.Item.RuntimeId);
                RefreshSequence();
                sequenceBox.SelectedIndex = sequenceBox.Items.Count - 1;
            }

            private void RemoveSequenceItem()
            {
                int index = sequenceBox.SelectedIndex;
                if (index < 0) return;
                sequence.RemoveAt(index);
                RefreshSequence();
                if (sequence.Count > 0) sequenceBox.SelectedIndex = Math.Min(index, sequence.Count - 1);
            }

            private void MoveSequenceItem(int delta)
            {
                int index = sequenceBox.SelectedIndex;
                int target = index + delta;
                if (index < 0 || target < 0 || target >= sequence.Count) return;
                Guid id = sequence[index];
                sequence.RemoveAt(index);
                sequence.Insert(target, id);
                RefreshSequence();
                sequenceBox.SelectedIndex = target;
            }

            private void RefreshSequence()
            {
                sequenceBox.Items.Clear();
                foreach (Guid id in sequence)
                {
                    InteractiveMediaItem item = media.FirstOrDefault(value => value.RuntimeId == id);
                    sequenceBox.Items.Add(item == null ? "?" : item.Title + (item.Loop ? " ↻" : string.Empty));
                }
            }

            public void Apply(InteractiveRouteItem route)
            {
                route.Name = nameBox.Text.Trim();
                route.Hotkey = hotkeyBox.Value == null ? null : hotkeyBox.Value.Clone();
                route.Immediate = string.Equals(commandBox.SelectedItem as string, "PLAY", StringComparison.Ordinal);
                route.MediaIds = sequence.ToList();
            }
        }

        private sealed class HotkeyTextBox : TextBox
        {
            private InteractiveHotkey value;

            public HotkeyTextBox()
            {
                ReadOnly = true;
                ShortcutsEnabled = false;
                KeyDown += CaptureKey;
                PreviewKeyDown += (_, e) => e.IsInputKey = true;
            }

            public InteractiveHotkey Value
            {
                get { return value; }
                set { this.value = value; Text = FormatHotkey(value); }
            }

            private void CaptureKey(object sender, KeyEventArgs e)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Escape)
                {
                    Value = null;
                    return;
                }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
                HotkeyModifiers modifiers = HotkeyModifiers.None;
                if (e.Control) modifiers |= HotkeyModifiers.Control;
                if (e.Shift) modifiers |= HotkeyModifiers.Shift;
                if (e.Alt) modifiers |= HotkeyModifiers.Alt;
                Value = new InteractiveHotkey { KeyCode = e.KeyCode, Modifiers = modifiers };
            }
        }

        private static void AddDialogRow(TableLayoutPanel grid, string label, Control control, int row)
        {
            control.Dock = DockStyle.Fill;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 0, 0) }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        private static string FormatHotkey(InteractiveHotkey hotkey)
        {
            if (hotkey == null || hotkey.KeyCode == Keys.None) return string.Empty;
            var parts = new List<string>();
            if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((hotkey.Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
            parts.Add(hotkey.KeyCode.ToString());
            return string.Join("+", parts);
        }

        private static InteractiveHotkey ParseInteractiveHotkey(string keyText, string modifierText)
        {
            if (!Enum.TryParse(keyText, true, out Keys key) || key == Keys.None) return null;
            if (!Enum.TryParse(modifierText, true, out HotkeyModifiers modifiers)) modifiers = HotkeyModifiers.None;
            return new InteractiveHotkey { KeyCode = key, Modifiers = modifiers };
        }

        private sealed class MediaChoice
        {
            public MediaChoice(InteractiveMediaItem item) { Item = item; }
            public InteractiveMediaItem Item { get; }
            public override string ToString() { return Item.Title + (Item.Loop ? " ↻" : string.Empty); }
        }

        private sealed class InteractiveMediaItem
        {
            public Guid RuntimeId { get; set; } = Guid.NewGuid();
            public string Title { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
            public bool Loop { get; set; }
        }

        private sealed class InteractiveRouteItem
        {
            public string Name { get; set; } = string.Empty;
            public bool Immediate { get; set; }
            public InteractiveHotkey Hotkey { get; set; }
            public List<Guid> MediaIds { get; set; } = new List<Guid>();
        }

        private sealed class InteractiveHotkey
        {
            public Keys KeyCode { get; set; }
            public HotkeyModifiers Modifiers { get; set; }
            public InteractiveHotkey Clone() { return new InteractiveHotkey { KeyCode = KeyCode, Modifiers = Modifiers }; }
        }

        [Flags]
        private enum HotkeyModifiers : uint
        {
            None = 0,
            Alt = 1,
            Control = 2,
            Shift = 4,
            Win = 8
        }

        private sealed class InteractivePlaybackEntry
        {
            public Guid MediaId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
            public bool Loop { get; set; }
            public string InputSummary { get; set; } = string.Empty;
        }

        private sealed class InteractiveTriggerRequest
        {
            public string RouteName { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public bool Immediate { get; set; }
            public List<InteractivePlaybackEntry> Entries { get; set; } = new List<InteractivePlaybackEntry>();
        }
    }
}
