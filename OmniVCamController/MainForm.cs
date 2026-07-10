using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm : Form
    {
        private const string MediaFileFilter = "Media files|*.mp4;*.mov;*.mkv;*.ts;*.m2ts;*.avi;*.flv;*.wmv;*.mxf;*.vob;*.dat;*.mpg;*.mpeg;*.asf;*.rm;*.rmvb;*.mp3;*.wav;*.aac|All files|*.*";
        private const string AutoConfigFileName = "OmniVCamController.xml";
        private const string NowPlayingFileName = "OmniVCamNowPlaying.xml";

        private System.Windows.Forms.Timer statusTimer;
        private System.Windows.Forms.Timer playoutTimer;
        private System.Windows.Forms.Timer logFlushTimer;

        private readonly List<ConnectionTabState> connectionStates = new List<ConnectionTabState>();
        private ConnectionTabState activeState;
        private bool switchingConnectionTab;

        private List<PlaylistItem> playlist = new List<PlaylistItem>();
        private List<ScheduledPlaylistItem> scheduledPlaylist = new List<ScheduledPlaylistItem>();
        private List<FavoriteInputItem> favoriteInputs = new List<FavoriteInputItem>();
        private readonly Random random = new Random();
        private int currentIndex = -1;
        private int currentScheduledIndex = -1;
        private bool playoutRunning;
        private bool playingScheduled;
        private bool waitingForScheduledStart;
        private DateTime scheduledSlotEndAt = DateTime.MinValue;
        private DateTime currentStartedAt;
        private long currentPositionSeconds;
        private long currentDurationSeconds;
        private long currentSizeBytes;
        private string currentStatusState = "stopped";
        private string currentStatusInput = string.Empty;
        private bool advancingPlayout;
        private DateTime lastAutoAdvanceAt = DateTime.MinValue;
        private long pendingSeekSeconds = -1;
        private DateTime pendingSeekUntil = DateTime.MinValue;
        private bool updatingProgress;
        private bool draggingProgress;
        private bool suppressSeekValueEvent;
        private bool controlsReady;
        private bool syncingInitialCameraSettings;
        private Button manualPlayButton;
        private Form playoutForm;
        private bool closingMainForm;
        private readonly TcpCommandClient commandClient;
        private readonly StringBuilder pendingLogText = new StringBuilder();
        private readonly int LOG_MAX_LINES = 1000;
        private DateTime lastLogFlushAt = DateTime.UtcNow;

        public MainForm()
        {
            InitializeComponent();
            commandClient = new TcpCommandClient(AppendRemoteLog);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildManualPanel(mainContentPanel);
            BuildPlayoutWindow();
            hwDecodeBox.Items.AddRange(new object[] { "none", "dxva2", "d3d11va", "cuda", "qsv" });
            hwDecodeBox.Text = "none";
            scaleModeBox.Items.AddRange(new object[] { "letterbox", "fill" });
            scaleModeBox.Text = "letterbox";
            displayAspectBox.Items.AddRange(new object[] { "auto", "16:9", "4:3", "1:1" });
            displayAspectBox.Text = "auto";
            playoutModeBox.Items.AddRange(new object[] { "Sequential", "Random" });
            playoutModeBox.SelectedIndex = 0;
            scheduleTypeBox.Items.AddRange(new object[] { "One-time", "Weekly" });
            scheduleTypeBox.SelectedIndex = 0;
            scheduleEndActionBox.Items.AddRange(new object[] { "Replay until end", "Wait until end", "Continue immediately" });
            scheduleEndActionBox.SelectedIndex = 1;
            scheduleStartActionBox.Items.AddRange(new object[] { "Start immediately", "Wait current item" });
            scheduleStartActionBox.SelectedIndex = 0;
            scheduledStartPicker.Value = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute).AddMinutes(1);
            scheduleDateTimePicker.Value = DateTime.Now.AddMinutes(1);
            scheduleTimePicker.Value = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute).AddMinutes(1);
            scheduleEndPicker.Value = DateTime.Now.AddMinutes(31);
            InitializeConnectionTabs();
            scheduleEndBox.CheckedChanged += (_, __) => UpdateScheduleEndControls();
            LoadAutoConfig();
            UpdateScheduleEndControls();
            controlsReady = true;

            statusTimer.Tick += async (_, __) => await RefreshStatusAsync();
            statusTimer.Start();
            logFlushTimer = new System.Windows.Forms.Timer { Interval = 500 };
            logFlushTimer.Tick += (_, __) => FlushPendingLog();
            logFlushTimer.Start();
            playoutTimer.Tick += async (_, __) => await PlayoutTickAsync();
            FormClosing += (_, __) =>
            {
                closingMainForm = true;
                if (playoutForm != null && !playoutForm.IsDisposed) playoutForm.Close();
                FlushPendingLog();
                SaveActiveConnectionState();
                commandClient.Dispose();
                SaveAutoConfig();
            };
        }

        private void InitializeConnectionTabs()
        {
            Controls.Remove(mainContentPanel);
            Controls.Remove(connectionTabs);

            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var hostPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(4, 3, 4, 0)
            };
            hostPanel.Controls.Add(connectionTabs);
            hostPanel.Controls.Add(addTabButton);
            hostPanel.Controls.Add(removeTabButton);

            connectionTabs.Width = 680;
            connectionTabs.Height = 28;
            connectionTabs.Dock = DockStyle.None;
            mainContentPanel.Dock = DockStyle.Fill;
            shell.Controls.Add(hostPanel, 0, 0);
            shell.Controls.Add(mainContentPanel, 0, 1);
            Controls.Add(shell);

            addTabButton.Click += (_, __) => AddConnectionTab(null, true);
            removeTabButton.Click += (_, __) => RemoveCurrentConnectionTab();
            connectionTabs.SelectedIndexChanged += (_, __) => SwitchToSelectedConnectionTab();

            AddConnectionTab(CreateDefaultConnectionState(), true);
        }

        private ConnectionTabState CreateDefaultConnectionState()
        {
            return new ConnectionTabState
            {
                Name = "OmniVCam",
                Host = hostBox.Text,
                Port = portBox.Value,
                Input = inputBox.Text,
                Title = titleBox.Text,
                Options = optionsBox.Text,
                HwDecode = hwDecodeBox.Text,
                ScaleMode = scaleModeBox.Text,
                DisplayAspect = displayAspectBox.Text,
                VideoFilter = videoFilterBox.Text,
                AudioFilter = audioFilterBox.Text,
                VideoIndex = videoIndexBox.Value,
                AudioIndex = audioIndexBox.Value,
                Shift = shiftBox.Value,
                Seek = seekBox.Value,
                ByteSeek = byteSeekBox.Checked,
                WriteNowPlayingXml = writeNowPlayingXmlBox.Checked,
                PlayoutMode = playoutModeBox.Text,
                AutoAdvance = autoAdvanceBox.Checked,
                ScheduledStartEnabled = scheduledStartBox.Checked,
                ScheduledStart = scheduledStartPicker.Value,
                ScheduleType = scheduleTypeBox.Text,
                ScheduleDateTime = scheduleDateTimePicker.Value,
                ScheduleTime = scheduleTimePicker.Value,
                ScheduleEndEnabled = scheduleEndBox.Checked,
                ScheduleEnd = scheduleEndPicker.Value,
                ScheduledTitle = scheduledTitleBox.Text,
                ScheduledOptions = scheduledOptionsBox.Text,
                ScheduleEndAction = scheduleEndActionBox.Text,
                ScheduleStartAction = scheduleStartActionBox.Text,
                WeekDays = GetSelectedWeekDays(),
                CurrentStatusState = currentStatusState,
                CurrentStatusInput = currentStatusInput,
                ScheduledSlotEndAt = scheduledSlotEndAt,
                CurrentStartedAt = currentStartedAt,
                PendingSeekUntil = pendingSeekUntil,
                LastAutoAdvanceAt = lastAutoAdvanceAt
            };
        }

        private void AddConnectionTab(ConnectionTabState state, bool select)
        {
            if (state == null)
            {
                SaveActiveConnectionState();
                state = CreateDefaultConnectionState();
                state.Name = "OmniVCam";
                state.Port = connectionStates.Count == 0 ? portBox.Value : connectionStates[connectionStates.Count - 1].Port + 1;
            }

            connectionStates.Add(state);
            var page = new TabPage(state.Name) { Tag = state };
            connectionTabs.TabPages.Add(page);
            UpdateConnectionTabTitle(state);
            if (select) connectionTabs.SelectedTab = page;
            removeTabButton.Enabled = connectionStates.Count > 1;
        }

        private void RemoveCurrentConnectionTab()
        {
            if (connectionStates.Count <= 1 || connectionTabs.SelectedTab == null) return;
            SaveActiveConnectionState();
            var state = connectionTabs.SelectedTab.Tag as ConnectionTabState;
            int index = connectionTabs.SelectedIndex;
            if (state != null) connectionStates.Remove(state);
            connectionTabs.TabPages.RemoveAt(index);
            connectionTabs.SelectedIndex = Math.Max(0, Math.Min(index, connectionTabs.TabPages.Count - 1));
            removeTabButton.Enabled = connectionStates.Count > 1;
            SwitchToSelectedConnectionTab();
        }

        private void SwitchToSelectedConnectionTab()
        {
            if (switchingConnectionTab || connectionTabs.SelectedTab == null) return;
            switchingConnectionTab = true;
            try
            {
                if (activeState != null) SaveActiveConnectionState();
                commandClient.Reset();
                activeState = connectionTabs.SelectedTab.Tag as ConnectionTabState;
                if (activeState == null) return;
                LoadConnectionStateToControls(activeState);
            }
            finally
            {
                switchingConnectionTab = false;
            }
        }

        private void SaveActiveConnectionState()
        {
            if (activeState == null) return;
            activeState.Host = hostBox.Text;
            activeState.Port = portBox.Value;
            activeState.Input = inputBox.Text;
            activeState.Title = titleBox.Text;
            activeState.Options = optionsBox.Text;
            activeState.HwDecode = hwDecodeBox.Text;
            activeState.ScaleMode = scaleModeBox.Text;
            activeState.DisplayAspect = displayAspectBox.Text;
            activeState.VideoFilter = videoFilterBox.Text;
            activeState.AudioFilter = audioFilterBox.Text;
            activeState.VideoIndex = videoIndexBox.Value;
            activeState.AudioIndex = audioIndexBox.Value;
            activeState.Shift = shiftBox.Value;
            activeState.Seek = seekBox.Value;
            activeState.ByteSeek = byteSeekBox.Checked;
            activeState.WriteNowPlayingXml = writeNowPlayingXmlBox.Checked;
            activeState.PlayoutMode = playoutModeBox.Text;
            activeState.AutoAdvance = autoAdvanceBox.Checked;
            activeState.ScheduledStartEnabled = scheduledStartBox.Checked;
            activeState.ScheduledStart = scheduledStartPicker.Value;
            activeState.ScheduleType = scheduleTypeBox.Text;
            activeState.ScheduleDateTime = scheduleDateTimePicker.Value;
            activeState.ScheduleTime = scheduleTimePicker.Value;
            activeState.ScheduleEndEnabled = scheduleEndBox.Checked;
            activeState.ScheduleEnd = scheduleEndPicker.Value;
            activeState.ScheduledTitle = scheduledTitleBox.Text;
            activeState.ScheduledOptions = scheduledOptionsBox.Text;
            activeState.ScheduleEndAction = scheduleEndActionBox.Text;
            activeState.ScheduleStartAction = scheduleStartActionBox.Text;
            activeState.WeekDays = GetSelectedWeekDays();
            activeState.Playlist = playlist;
            activeState.ScheduledPlaylist = scheduledPlaylist;
            activeState.FavoriteInputs = favoriteInputs;
            activeState.CurrentIndex = currentIndex;
            activeState.CurrentScheduledIndex = currentScheduledIndex;
            activeState.PlayoutRunning = playoutRunning;
            activeState.PlayingScheduled = playingScheduled;
            activeState.WaitingForScheduledStart = waitingForScheduledStart;
            activeState.ScheduledSlotEndAt = scheduledSlotEndAt;
            activeState.CurrentStartedAt = currentStartedAt;
            activeState.CurrentPositionSeconds = currentPositionSeconds;
            activeState.CurrentDurationSeconds = currentDurationSeconds;
            activeState.CurrentSizeBytes = currentSizeBytes;
            activeState.CurrentStatusState = currentStatusState;
            activeState.CurrentStatusInput = currentStatusInput;
            activeState.AdvancingPlayout = advancingPlayout;
            activeState.LastAutoAdvanceAt = lastAutoAdvanceAt;
            activeState.PendingSeekSeconds = pendingSeekSeconds;
            activeState.PendingSeekUntil = pendingSeekUntil;
            activeState.ProgressValue = progressBar.Value;
            FlushPendingLog();
            activeState.LogText = logBox.Text;
            activeState.SelectedPlaylistIndices = GetSelectedIndices(playlistView);
            activeState.SelectedScheduledPlaylistIndices = GetSelectedIndices(scheduledPlaylistView);
            activeState.SelectedFavoriteInputIndices = GetSelectedIndices(favoriteInputView);
            UpdateConnectionTabTitle(activeState);
        }

        private void LoadConnectionStateToControls(ConnectionTabState state)
        {
            controlsReady = false;
            playlist = state.Playlist;
            scheduledPlaylist = state.ScheduledPlaylist;
            favoriteInputs = state.FavoriteInputs;
            currentIndex = state.CurrentIndex;
            currentScheduledIndex = state.CurrentScheduledIndex;
            playoutRunning = state.PlayoutRunning;
            playingScheduled = state.PlayingScheduled;
            waitingForScheduledStart = state.WaitingForScheduledStart;
            scheduledSlotEndAt = state.ScheduledSlotEndAt;
            currentStartedAt = state.CurrentStartedAt;
            currentPositionSeconds = state.CurrentPositionSeconds;
            currentDurationSeconds = state.CurrentDurationSeconds;
            currentSizeBytes = state.CurrentSizeBytes;
            currentStatusState = state.CurrentStatusState;
            currentStatusInput = state.CurrentStatusInput;
            advancingPlayout = state.AdvancingPlayout;
            lastAutoAdvanceAt = state.LastAutoAdvanceAt;
            pendingSeekSeconds = state.PendingSeekSeconds;
            pendingSeekUntil = state.PendingSeekUntil;

            hostBox.Text = state.Host;
            portBox.Value = Clamp(state.Port, portBox.Minimum, portBox.Maximum);
            inputBox.Text = state.Input;
            titleBox.Text = state.Title;
            optionsBox.Text = state.Options;
            hwDecodeBox.Text = state.HwDecode;
            if (scaleModeBox.Items.Contains(state.ScaleMode)) scaleModeBox.Text = state.ScaleMode;
            displayAspectBox.Text = state.DisplayAspect;
            videoFilterBox.Text = state.VideoFilter;
            audioFilterBox.Text = state.AudioFilter;
            videoIndexBox.Value = Clamp(state.VideoIndex, videoIndexBox.Minimum, videoIndexBox.Maximum);
            audioIndexBox.Value = Clamp(state.AudioIndex, audioIndexBox.Minimum, audioIndexBox.Maximum);
            shiftBox.Value = Clamp(state.Shift, shiftBox.Minimum, shiftBox.Maximum);
            seekBox.Value = Clamp(state.Seek, seekBox.Minimum, seekBox.Maximum);
            byteSeekBox.Checked = state.ByteSeek;
            writeNowPlayingXmlBox.Checked = state.WriteNowPlayingXml;
            if (playoutModeBox.Items.Contains(state.PlayoutMode)) playoutModeBox.Text = state.PlayoutMode;
            autoAdvanceBox.Checked = state.AutoAdvance;
            scheduledStartBox.Checked = state.ScheduledStartEnabled;
            scheduledStartPicker.Value = ClampDateTimePicker(scheduledStartPicker, state.ScheduledStart);
            if (scheduleTypeBox.Items.Contains(state.ScheduleType)) scheduleTypeBox.Text = state.ScheduleType;
            scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, state.ScheduleDateTime);
            scheduleTimePicker.Value = ClampDateTimePicker(scheduleTimePicker, state.ScheduleTime);
            scheduleEndBox.Checked = state.ScheduleEndEnabled;
            scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, state.ScheduleEnd);
            scheduledTitleBox.Text = state.ScheduledTitle;
            scheduledOptionsBox.Text = state.ScheduledOptions;
            if (scheduleEndActionBox.Items.Contains(state.ScheduleEndAction)) scheduleEndActionBox.Text = state.ScheduleEndAction;
            if (scheduleStartActionBox.Items.Contains(state.ScheduleStartAction)) scheduleStartActionBox.Text = state.ScheduleStartAction;
            SetSelectedWeekDays(state.WeekDays);
            logBox.Text = state.LogText;
            positionLabel.Text = FormatSeconds(currentPositionSeconds) + " / " + (currentDurationSeconds > 0 ? FormatSeconds(currentDurationSeconds) : "--:--:--");
            progressBar.Value = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, state.ProgressValue));
            controlsReady = true;

            RefreshPlaylistView();
            RefreshScheduledPlaylistView();
            RefreshFavoriteInputView();
            RestoreSelectedIndices(playlistView, state.SelectedPlaylistIndices);
            RestoreSelectedIndices(scheduledPlaylistView, state.SelectedScheduledPlaylistIndices);
            RestoreSelectedIndices(favoriteInputView, state.SelectedFavoriteInputIndices);
            inputBox.Text = state.Input;
            titleBox.Text = state.Title;
            optionsBox.Text = state.Options;
            scheduledTitleBox.Text = state.ScheduledTitle;
            scheduledOptionsBox.Text = state.ScheduledOptions;
            UpdateScheduleEndControls();
            UpdatePlayoutStatusLabel();
            UpdatePlayoutTimerState();
            UpdateConnectionTabTitle(state);
        }

        private static List<int> GetSelectedIndices(ListView view)
        {
            return view.SelectedIndices.Cast<int>().ToList();
        }

        private static void RestoreSelectedIndices(ListView view, List<int> indices)
        {
            foreach (ListViewItem item in view.Items) item.Selected = false;
            if (indices == null) return;
            foreach (int index in indices)
            {
                if (index >= 0 && index < view.Items.Count) view.Items[index].Selected = true;
            }
            if (indices.Count > 0 && indices[0] >= 0 && indices[0] < view.Items.Count) view.Items[indices[0]].EnsureVisible();
        }

        private string GetConnectionTabTitle(ConnectionTabState state)
        {
            string endpoint = state.Host + ":" + state.Port.ToString("0");
            string baseName = string.IsNullOrWhiteSpace(state.Name) ? endpoint : state.Name;
            int marker = baseName.IndexOf(" [", StringComparison.Ordinal);
            if (marker >= 0) baseName = baseName.Substring(0, marker);
            return baseName;
        }

        private void UpdateConnectionTabTitle(ConnectionTabState state)
        {
            TabPage page = connectionTabs.TabPages.Cast<TabPage>().FirstOrDefault(tab => ReferenceEquals(tab.Tag, state));
            if (page == null) return;
            string endpoint = state.Host + ":" + state.Port.ToString("0");
            page.Text = GetConnectionTabTitle(state) + " [" + endpoint + "]";
        }

        private void UpdatePlayoutTimerState()
        {
            bool anyRunning = connectionStates.Any(state => state.PlayoutRunning) || playoutRunning;
            if (anyRunning)
            {
                if (!playoutTimer.Enabled) playoutTimer.Start();
            }
            else if (playoutTimer.Enabled)
            {
                playoutTimer.Stop();
            }
        }

        private void BuildManualPanel(Control parent)
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            parent.Controls.Add(root);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.Controls.Add(grid, 0, 0);

            AddRow(grid, "Host", hostBox, "Port", portBox);
            hostBox.Leave += (_, __) =>
            {
                SaveActiveConnectionState();
            };
            portBox.ValueChanged += (_, __) =>
            {
                if (controlsReady) SaveActiveConnectionState();
            };
            AddWideRow(grid, "Input", CreateInputPicker());
            AddWideRow(grid, "Title", titleBox);
            AddWideRow(grid, "Options", optionsBox);
            AddRow(grid, "HW decode", CreateHwDecodeControl(), "Scale mode", CreateScaleModeControl());
            AddRow(grid, "Display AR", CreateDisplayAspectControl(), "Shift us", CreateShiftControl());
            AddRow(grid, "Video filter", CreateVideoFilterControl(), "Audio filter", CreateAudioFilterControl());
            AddRow(grid, "Video index", videoIndexBox, "Audio index", audioIndexBox);
            AddRow(grid, "Position", positionLabel, "Seek seconds", seekBox);
            AddProgressRow(grid);
            progressBar.MouseDown += ProgressBar_MouseDown;
            progressBar.MouseMove += ProgressBar_MouseMove;
            progressBar.MouseUp += async (_, e) => await ProgressBar_MouseUpAsync(e);
            shiftBox.ValueChanged += async (_, __) =>
            {
                if (controlsReady) await SendCommandAsync($"SET_SHIFT {(long)shiftBox.Value}");
            };
            videoIndexBox.ValueChanged += async (_, __) =>
            {
                if (controlsReady) await SendIndexesAsync();
            };
            audioIndexBox.ValueChanged += async (_, __) =>
            {
                if (controlsReady) await SendIndexesAsync();
            };
            seekBox.ValueChanged += async (_, __) =>
            {
                if (controlsReady && !suppressSeekValueEvent) await SeekBySecondsAsync((long)seekBox.Value);
            };
            ConfigureFileDrop(inputBox, async files => await DropFilesToInputAsync(files));
            ConfigureFileDrop(playlistView, async files => await DropFilesToPlaylistAsync(files));
            scaleModeBox.SelectedIndexChanged += async (_, __) =>
            {
                if (controlsReady) await SendScaleModeAsync();
            };
            displayAspectBox.Leave += async (_, __) =>
            {
                if (controlsReady) await SendDisplayAspectAsync();
            };
            displayAspectBox.SelectedIndexChanged += async (_, __) =>
            {
                if (controlsReady) await SendDisplayAspectAsync();
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            buttons.Controls.Add(MakeButton("Ping", async (_, __) => await SendCommandAsync("PING")));
            buttons.Controls.Add(MakeButton("Open", async (_, __) => await PlayManualAsync()));
            manualPlayButton = MakeButton("Play", async (_, __) => await ToggleManualPlayPauseAsync());
            buttons.Controls.Add(manualPlayButton);
            buttons.Controls.Add(MakeButton("Stop", async (_, __) => await StopAllAsync()));
            buttons.Controls.Add(MakeButton("Reopen", async (_, __) => await SendCommandAsync("REOPEN")));
            buttons.Controls.Add(MakeButton("Set video index", async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton("Set audio index", async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton("Save XML", (_, __) => SaveAutoConfig()));
            buttons.Controls.Add(MakeButton("Open playout", (_, __) => OpenPlayoutWindow()));
            grid.Controls.Add(buttons, 0, grid.RowCount);
            grid.SetColumnSpan(buttons, 4);
            grid.RowCount++;

            root.Controls.Add(CreateLogAndFavoriteInputsPanel(), 0, 1);
        }

        private Control CreateLogAndFavoriteInputsPanel()
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };
            split.SizeChanged += (_, __) => split.SplitterDistance = Math.Max(1, split.ClientSize.Width / 2);
            split.Panel1.Controls.Add(logBox);
            split.Panel2.Controls.Add(CreateFavoriteInputsPanel());
            return split;
        }

        private Control CreateFavoriteInputsPanel()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8, 0, 0, 0) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            toolbar.Controls.Add(new Label { Text = "Favorite inputs", AutoSize = true, Padding = new Padding(0, 5, 6, 0) });
            toolbar.Controls.Add(MakeButton("Add current", (_, __) => AddCurrentFavoriteInput()));
            toolbar.Controls.Add(MakeButton("Remove", (_, __) => RemoveSelectedFavoriteInput()));
            toolbar.Controls.Add(MakeButton("Up", (_, __) => MoveSelectedFavoriteInput(-1)));
            toolbar.Controls.Add(MakeButton("Down", (_, __) => MoveSelectedFavoriteInput(1)));
            root.Controls.Add(toolbar, 0, 0);

            favoriteInputView.Columns.Add("Title", 140);
            favoriteInputView.Columns.Add("Options", 140);
            favoriteInputView.Columns.Add("Input", 320);
            favoriteInputView.SelectedIndexChanged += (_, __) => LoadSelectedFavoriteInputToInputs();
            favoriteInputView.DoubleClick += async (_, __) => await PlaySelectedFavoriteInputAsync();
            root.Controls.Add(favoriteInputView, 0, 1);

            return root;
        }

        private void BuildPlayoutWindow()
        {
            playoutForm = new Form
            {
                Text = "OmniVCam Playout",
                Size = new Size(1120, 720),
                MinimumSize = new Size(980, 600),
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                Icon = Icon
            };
            playoutForm.FormClosing += (_, e) =>
            {
                if (closingMainForm || e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                playoutForm.Hide();
            };
            BuildPlaylistPanel(playoutForm);
        }

        private void OpenPlayoutWindow()
        {
            if (playoutForm == null || playoutForm.IsDisposed) BuildPlayoutWindow();
            if (!playoutForm.Visible) playoutForm.Show(this);
            if (playoutForm.WindowState == FormWindowState.Minimized) playoutForm.WindowState = FormWindowState.Normal;
            playoutForm.Activate();
        }

        private void BuildPlaylistPanel(Control parent)
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            parent.Controls.Add(root);

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            header.Controls.Add(new Label { Text = "Playout", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(2, 5, 12, 0) });
            header.Controls.Add(playoutStatusLabel);
            header.Controls.Add(MakeButton("Start playout", async (_, __) => await StartPlayoutAsync()));
            header.Controls.Add(MakeButton("Next", async (_, __) => await PlayNextAsync(true)));
            header.Controls.Add(MakeButton("Stop playout", async (_, __) => await StopPlayoutAsync()));
            header.Controls.Add(MakeButton("Save XML", (_, __) => SaveAutoConfig()));
            header.Controls.Add(writeNowPlayingXmlBox);
            root.Controls.Add(header, 0, 0);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 50
            };
            root.Controls.Add(split, 0, 1);

            split.Panel1.Controls.Add(CreateScheduledPlaylistPanel());
            split.Panel2.Controls.Add(CreateNormalPlaylistPanel());
        }

        private Control CreateScheduledPlaylistPanel()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 3) };
            actions.Controls.Add(new Label { Text = "Scheduled playlist", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 8, 0) });
            actions.Controls.Add(MakeButton("Add current", async (_, __) => await AddScheduledFromCurrentAsync()));
            actions.Controls.Add(MakeButton("Add files", async (_, __) => await AddScheduledFilesAsync()));
            actions.Controls.Add(MakeButton("Add folder", async (_, __) => await AddScheduledFolderAsync()));
            actions.Controls.Add(MakeButton("Apply", (_, __) => ApplyScheduledControlsToSelected()));
            actions.Controls.Add(MakeButton("Remove", (_, __) => RemoveSelectedScheduled()));
            actions.Controls.Add(MakeButton("Up", (_, __) => MoveSelectedScheduled(-1)));
            actions.Controls.Add(MakeButton("Down", (_, __) => MoveSelectedScheduled(1)));
            actions.Controls.Add(MakeButton("Refresh durations", async (_, __) => await RefreshScheduledDurationsAsync()));
            root.Controls.Add(actions, 0, 0);

            var startSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 3) };
            startSettings.Controls.Add(MakeInlineLabel("Schedule type"));
            startSettings.Controls.Add(scheduleTypeBox);
            startSettings.Controls.Add(MakeInlineLabel("One-time start"));
            startSettings.Controls.Add(scheduleDateTimePicker);
            startSettings.Controls.Add(MakeInlineLabel("Weekly time"));
            startSettings.Controls.Add(scheduleTimePicker);
            startSettings.Controls.Add(MakeInlineLabel("Days"));
            startSettings.Controls.Add(mondayBox);
            startSettings.Controls.Add(tuesdayBox);
            startSettings.Controls.Add(wednesdayBox);
            startSettings.Controls.Add(thursdayBox);
            startSettings.Controls.Add(fridayBox);
            startSettings.Controls.Add(saturdayBox);
            startSettings.Controls.Add(sundayBox);
            startSettings.Controls.Add(MakeInlineLabel("Input title"));
            startSettings.Controls.Add(scheduledTitleBox);
            startSettings.Controls.Add(MakeInlineLabel("Input options"));
            startSettings.Controls.Add(scheduledOptionsBox);
            root.Controls.Add(startSettings, 0, 1);

            var endSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            endSettings.Controls.Add(scheduleEndBox);
            endSettings.Controls.Add(MakeInlineLabel("End time"));
            endSettings.Controls.Add(scheduleEndPicker);
            endSettings.Controls.Add(MakeInlineLabel("If media ends early"));
            endSettings.Controls.Add(scheduleEndActionBox);
            endSettings.Controls.Add(MakeInlineLabel("At scheduled time"));
            endSettings.Controls.Add(scheduleStartActionBox);
            root.Controls.Add(endSettings, 0, 2);

            scheduledPlaylistView.Columns.Add("#", 38);
            scheduledPlaylistView.Columns.Add("Status", 78);
            scheduledPlaylistView.Columns.Add("Title", 180);
            scheduledPlaylistView.Columns.Add("Duration", 76);
            scheduledPlaylistView.Columns.Add("Schedule", 210);
            scheduledPlaylistView.Columns.Add("End", 150);
            scheduledPlaylistView.Columns.Add("Start", 120);
            scheduledPlaylistView.Columns.Add("End action", 150);
            scheduledPlaylistView.Columns.Add("Last triggered", 150);
            scheduledPlaylistView.Columns.Add("Path", 360);
            scheduledPlaylistView.SelectedIndexChanged += (_, __) => LoadSelectedScheduledItemToInputs();
            scheduledPlaylistView.DoubleClick += async (_, __) => await PlaySelectedScheduledAsync();
            root.Controls.Add(scheduledPlaylistView, 0, 3);

            return root;
        }

        private static Label MakeInlineLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(8, 5, 2, 0) };
        }

        private Control CreateNormalPlaylistPanel()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            toolbar.Controls.Add(new Label { Text = "Normal playlist", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 8, 0) });
            toolbar.Controls.Add(MakeButton("Add current", async (_, __) => await AddCurrentToPlaylistAsync(false)));
            toolbar.Controls.Add(MakeButton("Add files", async (_, __) => await AddFilesAsync()));
            toolbar.Controls.Add(MakeButton("Add folder", async (_, __) => await AddFolderAsync()));
            toolbar.Controls.Add(MakeButton("Add bumper", async (_, __) => await AddBumperAsync()));
            toolbar.Controls.Add(MakeButton("Remove", (_, __) => RemoveSelected()));
            toolbar.Controls.Add(MakeButton("Up", (_, __) => MoveSelected(-1)));
            toolbar.Controls.Add(MakeButton("Down", (_, __) => MoveSelected(1)));
            toolbar.Controls.Add(MakeButton("Set title", (_, __) => SetSelectedTitle()));
            toolbar.Controls.Add(MakeButton("Set options", (_, __) => SetSelectedOptions()));
            toolbar.Controls.Add(MakeButton("Load", async (_, __) => await LoadPlaylistAsync()));
            toolbar.Controls.Add(MakeButton("Save file", (_, __) => SavePlaylist()));
            toolbar.Controls.Add(MakeButton("Refresh durations", async (_, __) => await RefreshDurationsAsync()));
            toolbar.Controls.Add(new Label { Text = "Mode", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            toolbar.Controls.Add(playoutModeBox);
            toolbar.Controls.Add(autoAdvanceBox);
            toolbar.Controls.Add(scheduledStartBox);
            toolbar.Controls.Add(scheduledStartPicker);
            root.Controls.Add(toolbar, 0, 0);

            playlistView.Columns.Add("#", 40);
            playlistView.Columns.Add("Status", 80);
            playlistView.Columns.Add("Type", 70);
            playlistView.Columns.Add("Title", 220);
            playlistView.Columns.Add("Duration", 80);
            playlistView.Columns.Add("Options", 220);
            playlistView.Columns.Add("Path", 520);
            playlistView.SelectedIndexChanged += (_, __) => LoadSelectedPlaylistItemToInputs();
            playlistView.DoubleClick += async (_, __) => await PlaySelectedAsync();
            root.Controls.Add(playlistView, 0, 1);

            return root;
        }

        private static void AddRow(TableLayoutPanel grid, string label1, Control control1, string label2, Control control2)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label1, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            grid.Controls.Add(control1, 1, row);
            grid.Controls.Add(new Label { Text = label2, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 4, 0, 0) }, 2, row);
            grid.Controls.Add(control2, 3, row);
        }

        private static void AddWideRow(TableLayoutPanel grid, string label, Control control)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            grid.Controls.Add(control, 1, row);
            grid.SetColumnSpan(control, 3);
        }

        private void AddProgressRow(TableLayoutPanel grid)
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            panel.Controls.Add(CreateProgressControl(), 0, 0);
            panel.Controls.Add(new Label { Text = "Seek mode", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 4, 0, 0) }, 1, 0);
            panel.Controls.Add(byteSeekBox, 2, 0);

            AddWideRow(grid, "Progress", panel);
        }

        private static Button MakeButton(string text, EventHandler handler)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 0), Padding = new Padding(3, 1, 3, 1) };
            button.Click += handler;
            return button;
        }

        private Control CreateShiftControl()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));

            shiftBox.Dock = DockStyle.Fill;
            var minusButton = MakeStepButton("-", () => AdjustShift(-shiftBox.Increment));
            var plusButton = MakeStepButton("+", () => AdjustShift(shiftBox.Increment));

            panel.Controls.Add(shiftBox, 0, 0);
            panel.Controls.Add(minusButton, 1, 0);
            panel.Controls.Add(plusButton, 2, 0);

            return panel;
        }

        private Control CreateHwDecodeControl()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

            hwDecodeBox.Dock = DockStyle.Fill;
            var setButton = MakeButton("Set", async (_, __) => await SendHwDecodeAsync());
            setButton.Margin = new Padding(4, 0, 0, 0);

            panel.Controls.Add(hwDecodeBox, 0, 0);
            panel.Controls.Add(setButton, 1, 0);

            return panel;
        }

        private Control CreateScaleModeControl()
        {
            scaleModeBox.Dock = DockStyle.Fill;
            return scaleModeBox;
        }

        private Control CreateDisplayAspectControl()
        {
            displayAspectBox.Dock = DockStyle.Fill;
            return displayAspectBox;
        }
        private Control CreateVideoFilterControl()
        {
            return CreateFilterControl(videoFilterBox, async () => await SendVideoFilterAsync(), async () => await CancelVideoFilterAsync());
        }

        private Control CreateAudioFilterControl()
        {
            return CreateFilterControl(audioFilterBox, async () => await SendAudioFilterAsync(), async () => await CancelAudioFilterAsync());
        }

        private static Control CreateFilterControl(TextBox textBox, Func<Task> setAction, Func<Task> cancelAction)
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

            textBox.Dock = DockStyle.Fill;
            var setButton = MakeButton("Set", async (_, __) => await setAction());
            var cancelButton = MakeButton("Cancel", async (_, __) => await cancelAction());
            setButton.Margin = new Padding(4, 0, 0, 0);
            cancelButton.Margin = new Padding(4, 0, 0, 0);

            panel.Controls.Add(textBox, 0, 0);
            panel.Controls.Add(setButton, 1, 0);
            panel.Controls.Add(cancelButton, 2, 0);

            return panel;
        }

        private static Button MakeStepButton(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 0, 0),
                Width = 34,
                Height = 26
            };
            button.Click += (_, __) => action();
            return button;
        }

        private void AdjustShift(decimal delta)
        {
            shiftBox.Value = Math.Max(shiftBox.Minimum, Math.Min(shiftBox.Maximum, shiftBox.Value + delta));
        }

        private Control CreateInputPicker()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };

            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            inputBox.Dock = DockStyle.Fill;
            var quickBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 4, 0)
            };
            quickBox.Items.AddRange(new object[] { "<TESTCARD>", "<TESTCARD2>", "<OBSVCAM>" });
            quickBox.SelectedIndexChanged += (_, __) =>
            {
                if (quickBox.SelectedItem != null) inputBox.Text = quickBox.SelectedItem.ToString();
            };
            var button = MakeButton("Browse", (_, __) => BrowseInputFile());
            button.Dock = DockStyle.None;

            panel.Controls.Add(inputBox, 0, 0);
            panel.Controls.Add(quickBox, 1, 0);
            panel.Controls.Add(button, 2, 0);

            return panel;
        }

        private Control CreateProgressControl()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 3,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));

            var backButton = MakeButton("-5s", async (_, __) => await SeekRelativeAsync(-5));
            var forwardButton = MakeButton("+5s", async (_, __) => await SeekRelativeAsync(5));

            progressBar.Dock = DockStyle.Fill;
            panel.Controls.Add(progressBar, 0, 0);
            panel.Controls.Add(backButton, 1, 0);
            panel.Controls.Add(forwardButton, 2, 0);

            return panel;
        }

        private void BrowseInputFile()
        {
            using (var dialog = new OpenFileDialog { Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                inputBox.Text = dialog.FileName;
            }
        }

        private async Task PlayManualAsync()
        {
            await PlayInputAsync(inputBox.Text.Trim(), optionsBox.Text.Trim());
        }

        private async Task ToggleManualPlayPauseAsync()
        {
            if (string.Equals(currentStatusState, "playing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentStatusState, "opening", StringComparison.OrdinalIgnoreCase))
            {
                await SendCommandAsync("PAUSE");
                currentStatusState = "paused";
                UpdateManualPlayButtonText();
                return;
            }

            if (string.Equals(currentStatusState, "paused", StringComparison.OrdinalIgnoreCase))
            {
                await SendCommandAsync("RESUME");
                currentStatusState = "playing";
                UpdateManualPlayButtonText();
                return;
            }

            await PlayManualAsync();
            currentStatusState = "playing";
            UpdateManualPlayButtonText();
        }

        private async Task SendVideoFilterAsync()
        {
            await SendCommandAsync("SET_FILTER " + videoFilterBox.Text.Trim());
        }

        private async Task SendAudioFilterAsync()
        {
            await SendCommandAsync("SET_AUDIO_FILTER " + audioFilterBox.Text.Trim());
        }

        private async Task SendHwDecodeAsync()
        {
            await SendCommandAsync($"SET_HW_DECODE {hwDecodeBox.Text.Trim()}");
        }

        private async Task SendScaleModeAsync()
        {
            await SendCommandAsync($"SET_SCALE_MODE {scaleModeBox.Text.Trim()}");
        }

        private async Task SendDisplayAspectAsync()
        {
            await SendCommandAsync($"SET_DISPLAY_ASPECT {displayAspectBox.Text.Trim()}");
        }
        private async Task CancelVideoFilterAsync()
        {
            await SendCommandAsync("SET_FILTER ");
        }

        private async Task CancelAudioFilterAsync()
        {
            await SendCommandAsync("SET_AUDIO_FILTER ");
        }

        private async Task SendIndexesAsync()
        {
            await SendCommandAsync($"SET_INDEX video={(int)videoIndexBox.Value} audio={(int)audioIndexBox.Value}");
        }

        private Task SendInitialCameraSettingsAsync()
        {
            return SendCommandsAsync(new[]
            {
                "SET_FILTER " + videoFilterBox.Text.Trim(),
                "SET_AUDIO_FILTER " + audioFilterBox.Text.Trim(),
                $"SET_HW_DECODE {hwDecodeBox.Text.Trim()}",
                $"SET_SCALE_MODE {scaleModeBox.Text.Trim()}",
                $"SET_DISPLAY_ASPECT {displayAspectBox.Text.Trim()}",
                $"SET_INDEX video={(int)videoIndexBox.Value} audio={(int)audioIndexBox.Value}",
                $"SET_SHIFT {(long)shiftBox.Value}"
            });
        }

        private async Task PlayInputAsync(string input, string options)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("Input is empty.");
                return;
            }
            await SendCommandAsync(string.IsNullOrWhiteSpace(options) ? "PLAY " + input : "PLAY " + input + "\t" + options);
        }

        private static void ConfigureFileDrop(Control control, Func<string[], Task> dropAction)
        {
            control.AllowDrop = true;
            control.DragEnter += (_, e) =>
            {
                e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            control.DragDrop += async (_, e) =>
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                await dropAction(files);
            };
        }

        private Task DropFilesToInputAsync(string[] files)
        {
            string file = files.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(file)) return Task.CompletedTask;
            inputBox.Text = file;
            titleBox.Text = GetInputTitle(file);
            scheduledTitleBox.Text = titleBox.Text;
            return Task.CompletedTask;
        }

        private Task DropFilesToPlaylistAsync(string[] files)
        {
            foreach (string path in ExpandDroppedMediaFiles(files))
            {
                AddPlaylistItem(path, false, optionsBox.Text.Trim(), false);
            }
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private static IEnumerable<string> ExpandDroppedMediaFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    if (IsMediaFile(path)) yield return path;
                }
                else if (Directory.Exists(path))
                {
                    foreach (string file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Where(IsMediaFile))
                        yield return file;
                }
            }
        }
        private void AddCurrentFavoriteInput()
        {
            string input = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("Input is empty.");
                return;
            }

            string options = optionsBox.Text.Trim();
            FavoriteInputItem existing = favoriteInputs.FirstOrDefault(item => PathsEqual(item.Input, input) && string.Equals(item.Options, options, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.Title = GetInputTitle(input);
            }
            else
            {
                favoriteInputs.Add(new FavoriteInputItem
                {
                    Input = input,
                    Title = GetInputTitle(input),
                    Options = options
                });
            }
            RefreshFavoriteInputView();
        }

        private void RemoveSelectedFavoriteInput()
        {
            foreach (int index in favoriteInputView.SelectedIndices.Cast<int>().OrderByDescending(i => i)) favoriteInputs.RemoveAt(index);
            RefreshFavoriteInputView();
        }

        private void MoveSelectedFavoriteInput(int direction)
        {
            if (favoriteInputView.SelectedIndices.Count != 1) return;
            int index = favoriteInputView.SelectedIndices[0];
            int target = index + direction;
            if (target < 0 || target >= favoriteInputs.Count) return;
            FavoriteInputItem item = favoriteInputs[index];
            favoriteInputs.RemoveAt(index);
            favoriteInputs.Insert(target, item);
            RefreshFavoriteInputView();
            favoriteInputView.Items[target].Selected = true;
        }

        private void LoadSelectedFavoriteInputToInputs()
        {
            if (switchingConnectionTab) return;
            if (favoriteInputView.SelectedIndices.Count != 1) return;
            int index = favoriteInputView.SelectedIndices[0];
            if (index < 0 || index >= favoriteInputs.Count) return;
            inputBox.Text = favoriteInputs[index].Input;
            titleBox.Text = favoriteInputs[index].Title;
            scheduledTitleBox.Text = favoriteInputs[index].Title;
            optionsBox.Text = favoriteInputs[index].Options;
            scheduledOptionsBox.Text = favoriteInputs[index].Options;
        }

        private async Task PlaySelectedFavoriteInputAsync()
        {
            if (favoriteInputView.SelectedIndices.Count == 0) return;
            int index = favoriteInputView.SelectedIndices[0];
            if (index < 0 || index >= favoriteInputs.Count) return;
            FavoriteInputItem item = favoriteInputs[index];
            inputBox.Text = item.Input;
            titleBox.Text = item.Title;
            scheduledTitleBox.Text = item.Title;
            optionsBox.Text = item.Options;
            scheduledOptionsBox.Text = item.Options;
            await PlayInputAsync(item.Input, item.Options);
        }

        private void RefreshFavoriteInputView()
        {
            favoriteInputView.BeginUpdate();
            favoriteInputView.Items.Clear();
            foreach (FavoriteInputItem item in favoriteInputs)
            {
                var row = new ListViewItem(item.Title);
                row.SubItems.Add(item.Options);
                row.SubItems.Add(item.Input);
                favoriteInputView.Items.Add(row);
            }
            favoriteInputView.EndUpdate();
        }

        private static string GetInputTitle(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            if (input.StartsWith("<", StringComparison.Ordinal) && input.EndsWith(">", StringComparison.Ordinal)) return input;
            string fileName = Path.GetFileNameWithoutExtension(input);
            return string.IsNullOrWhiteSpace(fileName) ? input : fileName;
        }

        private string GetTitleInputOrDefault(string input)
        {
            string title = titleBox.Text.Trim();
            return string.IsNullOrWhiteSpace(title) ? GetInputTitle(input) : title;
        }

        private Task AddFilesAsync()
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                foreach (string file in dialog.FileNames) AddPlaylistItem(file, false, optionsBox.Text.Trim(), false);
            }
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddFolderAsync()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                foreach (string file in Directory.GetFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories).Where(IsMediaFile))
                {
                    AddPlaylistItem(file, false, optionsBox.Text.Trim(), false);
                }
            }
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddBumperAsync()
        {
            using (var dialog = new OpenFileDialog { Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                AddPlaylistItem(dialog.FileName, true, optionsBox.Text.Trim(), false);
            }
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddCurrentToPlaylistAsync(bool bumper)
        {
            string input = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return Task.CompletedTask;
            AddPlaylistItem(input, bumper, optionsBox.Text.Trim(), true);
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private void AddPlaylistItem(string file, bool bumper, string options, bool useTitleInput)
        {
            playlist.Add(new PlaylistItem
            {
                Path = file,
                Title = useTitleInput ? GetTitleInputOrDefault(file) : GetInputTitle(file),
                DurationSeconds = 0,
                Options = options,
                IsBumper = bumper
            });
        }

        private Task AddScheduledFromCurrentAsync()
        {
            string input = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) return Task.CompletedTask;

            scheduledPlaylist.Add(CreateScheduledItem(input, true));
            RefreshScheduledPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddScheduledFilesAsync()
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                foreach (string file in dialog.FileNames) scheduledPlaylist.Add(CreateScheduledItem(file, false));
            }
            RefreshScheduledPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddScheduledFolderAsync()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                foreach (string file in Directory.GetFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories).Where(IsMediaFile))
                {
                    scheduledPlaylist.Add(CreateScheduledItem(file, false));
                }
            }
            RefreshScheduledPlaylistView();
            return Task.CompletedTask;
        }

        private ScheduledPlaylistItem CreateScheduledItem(string input, bool useTitleInput)
        {
            var item = new ScheduledPlaylistItem
            {
                Path = input,
                Title = useTitleInput ? GetScheduledTitleInputOrDefault(input) : GetInputTitle(input),
                DurationSeconds = 0,
                Options = GetScheduledOptionsInput(),
                ScheduleKind = scheduleTypeBox.Text == "Weekly" ? ScheduleKind.Weekly : ScheduleKind.OneTime,
                StartAt = scheduleDateTimePicker.Value,
                WeeklyTime = scheduleTimePicker.Value.TimeOfDay,
                WeekDays = GetSelectedWeekDays(),
                HasEndTime = scheduleEndBox.Checked,
                EndAt = scheduleEndPicker.Value,
                EndAction = GetSelectedEndAction(),
                StartAction = GetSelectedStartAction()
            };
            if (item.ScheduleKind == ScheduleKind.Weekly && item.WeekDays == 0) item.WeekDays = WeekDayMaskFromDay(DateTime.Now.DayOfWeek);
            return item;
        }

        private void RemoveSelectedScheduled()
        {
            foreach (int index in scheduledPlaylistView.SelectedIndices.Cast<int>().OrderByDescending(i => i))
            {
                scheduledPlaylist.RemoveAt(index);
                if (currentScheduledIndex == index) currentScheduledIndex = -1;
                else if (currentScheduledIndex > index) currentScheduledIndex--;
            }
            RefreshScheduledPlaylistView();
        }

        private void MoveSelectedScheduled(int direction)
        {
            if (scheduledPlaylistView.SelectedIndices.Count != 1) return;
            int index = scheduledPlaylistView.SelectedIndices[0];
            int target = index + direction;
            if (target < 0 || target >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[index];
            scheduledPlaylist.RemoveAt(index);
            scheduledPlaylist.Insert(target, item);
            if (currentScheduledIndex == index) currentScheduledIndex = target;
            RefreshScheduledPlaylistView();
            scheduledPlaylistView.Items[target].Selected = true;
        }

        private void LoadSelectedScheduledItemToInputs()
        {
            if (switchingConnectionTab) return;
            if (scheduledPlaylistView.SelectedIndices.Count != 1) return;
            int index = scheduledPlaylistView.SelectedIndices[0];
            if (index < 0 || index >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[index];
            inputBox.Text = item.Path;
            titleBox.Text = item.Title;
            scheduledTitleBox.Text = item.Title;
            optionsBox.Text = item.Options;
            scheduledOptionsBox.Text = item.Options;
            scheduleTypeBox.Text = item.ScheduleKind == ScheduleKind.Weekly ? "Weekly" : "One-time";
            scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, item.StartAt);
            scheduleTimePicker.Value = DateTime.Today.Add(item.WeeklyTime);
            SetSelectedWeekDays(item.WeekDays);
            scheduleEndBox.Checked = item.HasEndTime;
            scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, item.EndAt);
            scheduleEndActionBox.Text = EndActionText(item.EndAction);
            scheduleStartActionBox.Text = StartActionText(item.StartAction);
            UpdateScheduleEndControls();
        }

        private void ApplyScheduledControlsToSelected()
        {
            if (scheduledPlaylistView.SelectedIndices.Count != 1) return;
            int index = scheduledPlaylistView.SelectedIndices[0];
            if (index < 0 || index >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[index];
            item.Path = inputBox.Text.Trim();
            item.Title = GetScheduledTitleInputOrDefault(item.Path);
            titleBox.Text = item.Title;
            item.Options = scheduledOptionsBox.Text.Trim();
            optionsBox.Text = item.Options;
            item.ScheduleKind = scheduleTypeBox.Text == "Weekly" ? ScheduleKind.Weekly : ScheduleKind.OneTime;
            item.StartAt = scheduleDateTimePicker.Value;
            item.WeeklyTime = scheduleTimePicker.Value.TimeOfDay;
            item.WeekDays = GetSelectedWeekDays();
            if (item.ScheduleKind == ScheduleKind.Weekly && item.WeekDays == 0) item.WeekDays = WeekDayMaskFromDay(DateTime.Now.DayOfWeek);
            item.HasEndTime = scheduleEndBox.Checked;
            item.EndAt = scheduleEndPicker.Value;
            item.EndAction = GetSelectedEndAction();
            item.StartAction = GetSelectedStartAction();
            if (playingScheduled && index == currentScheduledIndex)
            {
                scheduledSlotEndAt = GetScheduleWindow(index, DateTime.Now).End;
            }
            RefreshScheduledPlaylistView();
            scheduledPlaylistView.Items[index].Selected = true;
        }

        private void UpdateScheduleEndControls()
        {
            bool enabled = scheduleEndBox.Checked;
            scheduleEndPicker.Enabled = enabled;
            scheduleEndActionBox.Enabled = enabled;
        }

        private string GetScheduledOptionsInput()
        {
            string scheduledOptions = scheduledOptionsBox.Text.Trim();
            return string.IsNullOrWhiteSpace(scheduledOptions) ? optionsBox.Text.Trim() : scheduledOptions;
        }

        private string GetScheduledTitleInputOrDefault(string input)
        {
            string title = scheduledTitleBox.Text.Trim();
            return string.IsNullOrWhiteSpace(title) ? GetTitleInputOrDefault(input) : title;
        }

        private async Task PlaySelectedScheduledAsync()
        {
            if (scheduledPlaylistView.SelectedIndices.Count == 0) return;
            currentScheduledIndex = scheduledPlaylistView.SelectedIndices[0];
            await PlayScheduledCurrentAsync();
        }

        private async Task<int> GetDurationOrDefaultAsync(string input, string options)
        {
            string command = string.IsNullOrWhiteSpace(options) ? "DURATION " + input : "DURATION " + input + "\t" + options;
            string reply = await SendRawCommandAsync(command);
            long duration = ParseStatusLong(reply ?? string.Empty, "duration=");
            if (duration > 0 && duration <= int.MaxValue) return (int)duration;
            return 0;
        }

        private static bool IsMediaFile(string file)
        {
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            return new[] { ".mp4", ".mov", ".mkv", ".ts", ".m2ts", ".avi", ".flv", ".wmv", ".mxf", ".vob", ".dat", ".mpg", ".mpeg", ".asf", ".rm", ".rmvb", ".mp3", ".wav", ".aac" }.Contains(ext);
        }

        private void RemoveSelected()
        {
            foreach (int index in playlistView.SelectedIndices.Cast<int>().OrderByDescending(i => i)) playlist.RemoveAt(index);
            RefreshPlaylistView();
        }

        private void MoveSelected(int direction)
        {
            if (playlistView.SelectedIndices.Count != 1) return;
            int index = playlistView.SelectedIndices[0];
            int target = index + direction;
            if (target < 0 || target >= playlist.Count) return;
            PlaylistItem item = playlist[index];
            playlist.RemoveAt(index);
            playlist.Insert(target, item);
            RefreshPlaylistView();
            playlistView.Items[target].Selected = true;
        }

        private void LoadSelectedPlaylistItemToInputs()
        {
            if (switchingConnectionTab) return;
            if (playlistView.SelectedIndices.Count != 1) return;
            int index = playlistView.SelectedIndices[0];
            if (index < 0 || index >= playlist.Count) return;
            inputBox.Text = playlist[index].Path;
            titleBox.Text = playlist[index].Title;
            scheduledTitleBox.Text = playlist[index].Title;
            optionsBox.Text = playlist[index].Options;
            scheduledOptionsBox.Text = playlist[index].Options;
        }

        private void SetSelectedOptions()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            string options = scheduledOptionsBox.Text.Trim();
            optionsBox.Text = options;
            foreach (int index in playlistView.SelectedIndices.Cast<int>())
            {
                if (index < 0 || index >= playlist.Count) continue;
                playlist[index].Options = options;
            }
            RefreshPlaylistView();
        }

        private void SetSelectedTitle()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            foreach (int index in playlistView.SelectedIndices.Cast<int>())
            {
                if (index < 0 || index >= playlist.Count) continue;
                playlist[index].Title = GetTitleInputOrDefault(playlist[index].Path);
            }
            RefreshPlaylistView();
        }

        private async Task PlaySelectedAsync()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            currentIndex = playlistView.SelectedIndices[0];
            await PlayCurrentAsync();
        }

        private async Task StartPlayoutAsync()
        {
            if (playlist.Count == 0 && scheduledPlaylist.Count == 0) return;
            playoutRunning = true;
            SetPlayoutTimerRunning(true);
            waitingForScheduledStart = scheduledStartBox.Checked;
            if (!waitingForScheduledStart)
            {
                bool startedScheduled = await StartDueScheduledItemAsync();
                if (!startedScheduled && playlist.Count > 0)
                {
                    currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
                    await PlayCurrentAsync();
                }
            }
            UpdatePlayoutStatusLabel();
            AppendLog("Playout started.");
        }

        private async Task StopPlayoutAsync()
        {
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            ResetScheduledTriggers();
            await SendCommandAsync("STOP");
            SetPlayoutTimerRunning(false);
            UpdatePlayoutStatusLabel();
            WriteNowPlayingXml();
            AppendLog("Playout stopped.");
        }

        private async Task StopAllAsync()
        {
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            ResetScheduledTriggers();
            await SendCommandAsync("STOP");
            SetPlayoutTimerRunning(false);
            UpdatePlayoutStatusLabel();
            WriteNowPlayingXml();
        }

        private void ResetScheduledTriggers()
        {
            foreach (ScheduledPlaylistItem item in scheduledPlaylist)
            {
                item.LastTriggeredAt = DateTime.MinValue;
            }
            RefreshScheduledPlaylistView();
        }

        private void SetPlayoutTimerRunning(bool running)
        {
            if (activeState != null) activeState.PlayoutRunning = running;
            if (running)
            {
                if (!playoutTimer.Enabled) playoutTimer.Start();
            }
            else
            {
                UpdatePlayoutTimerState();
            }
        }

        private async Task PlayoutTickAsync()
        {
            if (!playoutRunning || (playlist.Count == 0 && scheduledPlaylist.Count == 0)) return;
            if (advancingPlayout) return;

            if (waitingForScheduledStart)
            {
                TimeSpan now = DateTime.Now.TimeOfDay;
                TimeSpan scheduled = scheduledStartPicker.Value.TimeOfDay;
                if (now.Hours == scheduled.Hours && now.Minutes == scheduled.Minutes && now.Seconds == scheduled.Seconds)
                {
                    waitingForScheduledStart = false;
                    if (playlist.Count > 0)
                    {
                        currentIndex = 0;
                        await PlayCurrentAsync();
                    }
                }
                return;
            }

            await StartDueScheduledItemAsync();
            await HandleScheduledEndAsync();
            await AutoAdvanceIfNeededAsync();
        }

        private async Task AutoAdvanceIfNeededAsync()
        {
            if (advancingPlayout) return;
            if (playingScheduled)
            {
                await AutoAdvanceScheduledIfNeededAsync();
                return;
            }
            if (!autoAdvanceBox.Checked || currentIndex < 0 || currentIndex >= playlist.Count) return;
            if ((DateTime.Now - lastAutoAdvanceAt).TotalSeconds < 2) return;

            PlaylistItem current = playlist[currentIndex];
            bool statusMatchesCurrent = PathsEqual(current.Path, GetStatusInputPath(currentStatusInput));
            bool reachedPlaylistDuration = current.DurationSeconds > 0 && (DateTime.Now - currentStartedAt).TotalSeconds >= current.DurationSeconds;
            bool playbackFailed = statusMatchesCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool playbackEnded = statusMatchesCurrent && string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase);

            if (reachedPlaylistDuration || playbackFailed || playbackEnded)
            {
                advancingPlayout = true;
                lastAutoAdvanceAt = DateTime.Now;
                try
                {
                    await PlayNextAsync(false);
                }
                finally
                {
                    advancingPlayout = false;
                }
            }
        }

        private async Task PlayNextAsync(bool manual)
        {
            if (playingScheduled)
            {
                await ReturnToNormalPlaylistAsync();
                if (manual && playlist.Count > 0) await PlayNextAsync(false);
                return;
            }
            if (playlist.Count == 0) return;
            if (playoutModeBox.Text == "Random")
            {
                int nextValue = random.Next(playlist.Count);
                if(playlist.Count > 1)
                {
                    while(nextValue == currentIndex)
                    {
                        nextValue = random.Next(playlist.Count);
                    }
                }
                currentIndex = nextValue;
            }
            else
            {
                currentIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % playlist.Count;
            }
            if (manual)
            {
                playoutRunning = true;
                SetPlayoutTimerRunning(true);
            }
            await PlayCurrentAsync();
        }

        private async Task PlayCurrentAsync()
        {
            if (currentIndex < 0 || currentIndex >= playlist.Count) return;
            PlaylistItem item = playlist[currentIndex];
            playingScheduled = false;
            currentScheduledIndex = -1;
            scheduledSlotEndAt = DateTime.MinValue;
            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            currentStatusInput = item.Path;
            SelectCurrentRow();
            inputBox.Text = item.Path;
            titleBox.Text = item.Title;
            scheduledTitleBox.Text = item.Title;
            optionsBox.Text = item.Options;
            scheduledOptionsBox.Text = item.Options;
            await PlayInputAsync(item.Path, item.Options);
            UpdatePlayoutStatusLabel();
        }

        private async Task PlayScheduledCurrentAsync()
        {
            if (currentScheduledIndex < 0 || currentScheduledIndex >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[currentScheduledIndex];
            playingScheduled = true;
            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            currentStatusInput = item.Path;
            ScheduleWindow window = GetScheduleWindow(currentScheduledIndex, DateTime.Now);
            scheduledSlotEndAt = window.End;
            SelectCurrentRow();
            SelectCurrentScheduledRow();
            inputBox.Text = item.Path;
            titleBox.Text = item.Title;
            scheduledTitleBox.Text = item.Title;
            optionsBox.Text = item.Options;
            scheduledOptionsBox.Text = item.Options;
            item.LastTriggeredAt = window.Start;
            await PlayInputAsync(item.Path, item.Options);
            UpdatePlayoutStatusLabel();
        }

        private async Task<bool> StartDueScheduledItemAsync()
        {
            DateTime now = DateTime.Now;
            int selectedIndex = -1;
            ScheduleWindow selectedWindow = ScheduleWindow.Empty;

            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                ScheduledPlaylistItem item = scheduledPlaylist[i];
                ScheduleWindow window = GetScheduleWindow(i, now);
                if (!window.IsActive) continue;
                if (item.LastTriggeredAt >= window.Start && item.LastTriggeredAt < window.End) continue;
                if (item.StartAction == ScheduleStartAction.WaitCurrentItem && IsCurrentPlaybackActive()) continue;

                if (selectedIndex < 0 || window.Start > selectedWindow.Start)
                {
                    selectedIndex = i;
                    selectedWindow = window;
                }
            }

            if (selectedIndex < 0) return false;
            if (playingScheduled && IsCurrentPlaybackActive())
            {
                if (currentScheduledIndex >= 0 && currentScheduledIndex < scheduledPlaylist.Count)
                {
                    ScheduleWindow currentWindow = GetScheduleWindow(currentScheduledIndex, now);
                    if (selectedWindow.Start <= currentWindow.Start) return false;
                }
                if (scheduledPlaylist[selectedIndex].StartAction != ScheduleStartAction.StartImmediately) return false;
            }

            currentScheduledIndex = selectedIndex;
            await PlayScheduledCurrentAsync();
            return true;
        }

        private async Task HandleScheduledEndAsync()
        {
            if (!playingScheduled || currentScheduledIndex < 0 || currentScheduledIndex >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[currentScheduledIndex];
            DateTime now = DateTime.Now;

            if (scheduledSlotEndAt != DateTime.MinValue && now >= scheduledSlotEndAt)
            {
                await FinishScheduledSlotAsync();
                return;
            }

            if (item.HasEndTime && item.EndAction == ScheduleEndAction.WaitUntilEnd) return;
            bool ended = string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase) || string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool durationReached = item.DurationSeconds > 0 && (now - currentStartedAt).TotalSeconds >= item.DurationSeconds;
            if (!ended && !durationReached) return;

            if (item.HasEndTime && item.EndAction == ScheduleEndAction.ReplayUntilEnd)
            {
                await PlayScheduledCurrentAsync();
                return;
            }

            await ReturnToNormalPlaylistAsync();
        }

        private async Task AutoAdvanceScheduledIfNeededAsync()
        {
            if (currentScheduledIndex < 0 || currentScheduledIndex >= scheduledPlaylist.Count) return;
            if ((DateTime.Now - lastAutoAdvanceAt).TotalSeconds < 2) return;
            ScheduledPlaylistItem item = scheduledPlaylist[currentScheduledIndex];
            if (scheduledSlotEndAt != DateTime.MinValue) return;
            bool ended = string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase) || string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool durationReached = item.DurationSeconds > 0 && (DateTime.Now - currentStartedAt).TotalSeconds >= item.DurationSeconds;
            if (!ended && !durationReached) return;
            lastAutoAdvanceAt = DateTime.Now;
            await ReturnToNormalPlaylistAsync();
        }

        private async Task FinishScheduledSlotAsync()
        {
            await SendCommandAsync("STOP");
            playingScheduled = false;
            currentScheduledIndex = -1;
            scheduledSlotEndAt = DateTime.MinValue;
            currentStatusState = "stopped";
            currentStatusInput = string.Empty;
            RefreshScheduledPlaylistView();
            UpdatePlayoutStatusLabel();

            if (playlist.Count == 0) return;
            currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
            await PlayCurrentAsync();
        }

        private async Task ReturnToNormalPlaylistAsync()
        {
            playingScheduled = false;
            currentScheduledIndex = -1;
            scheduledSlotEndAt = DateTime.MinValue;
            RefreshScheduledPlaylistView();
            if (playlist.Count == 0)
            {
                UpdatePlayoutStatusLabel();
                return;
            }
            currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
            await PlayCurrentAsync();
        }

        private void SelectCurrentRow()
        {
            foreach (ListViewItem row in playlistView.Items) row.Selected = false;
            if (!playingScheduled && currentIndex >= 0 && currentIndex < playlistView.Items.Count)
            {
                playlistView.Items[currentIndex].Selected = true;
                playlistView.Items[currentIndex].EnsureVisible();
            }
        }

        private void SelectCurrentScheduledRow()
        {
            foreach (ListViewItem row in scheduledPlaylistView.Items) row.Selected = false;
            if (playingScheduled && currentScheduledIndex >= 0 && currentScheduledIndex < scheduledPlaylistView.Items.Count)
            {
                scheduledPlaylistView.Items[currentScheduledIndex].Selected = true;
                scheduledPlaylistView.Items[currentScheduledIndex].EnsureVisible();
            }
        }

        private void SavePlaylist()
        {
            using (var dialog = new SaveFileDialog { Filter = "OmniVCam playlist|*.ovcpl|Text files|*.txt", FileName = "playlist.ovcpl" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllLines(dialog.FileName, playlist.Select(item => string.Join("\t", Escape(item.Path), Escape(item.Title), item.DurationSeconds, Escape(item.Options), item.IsBumper ? "1" : "0")), Encoding.UTF8);
            }
        }

        private void SaveAutoConfig()
        {
            try
            {
                SaveActiveConnectionState();
                var document = new XDocument(
                    new XElement("OmniVCamController",
                        new XAttribute("selectedTab", Math.Max(0, connectionTabs.SelectedIndex)),
                        new XElement("Tabs", connectionStates.Select(SaveConnectionTabElement))));

                document.Save(GetAutoConfigPath());
            }
            catch (Exception ex)
            {
                AppendLog("Save config failed: " + ex.Message);
            }
        }

        private XElement SaveConnectionTabElement(ConnectionTabState state)
        {
            return new XElement("Tab",
                new XAttribute("name", GetConnectionTabTitle(state)),
                new XElement("Settings",
                    new XAttribute("host", state.Host),
                    new XAttribute("port", state.Port),
                    new XAttribute("input", state.Input),
                    new XAttribute("title", state.Title),
                    new XAttribute("options", state.Options),
                    new XAttribute("hwDecode", state.HwDecode),
                    new XAttribute("scaleMode", state.ScaleMode),
                    new XAttribute("displayAspect", state.DisplayAspect),
                    new XAttribute("videoFilter", state.VideoFilter),
                    new XAttribute("audioFilter", state.AudioFilter),
                    new XAttribute("videoIndex", state.VideoIndex),
                    new XAttribute("audioIndex", state.AudioIndex),
                    new XAttribute("shift", state.Shift),
                    new XAttribute("seek", state.Seek),
                    new XAttribute("byteSeek", state.ByteSeek ? "1" : "0"),
                    new XAttribute("writeNowPlayingXml", state.WriteNowPlayingXml ? "1" : "0"),
                    new XAttribute("playoutMode", state.PlayoutMode),
                    new XAttribute("autoAdvance", state.AutoAdvance ? "1" : "0"),
                    new XAttribute("scheduledStartEnabled", state.ScheduledStartEnabled ? "1" : "0"),
                    new XAttribute("scheduledStart", state.ScheduledStart.ToString("HH:mm:ss")),
                    new XAttribute("scheduleType", state.ScheduleType),
                    new XAttribute("scheduleDateTime", state.ScheduleDateTime.ToString("o")),
                    new XAttribute("scheduleTime", state.ScheduleTime.ToString("HH:mm:ss")),
                    new XAttribute("scheduleEndEnabled", state.ScheduleEndEnabled ? "1" : "0"),
                    new XAttribute("scheduleEnd", state.ScheduleEnd.ToString("o")),
                    new XAttribute("scheduledTitle", state.ScheduledTitle),
                    new XAttribute("scheduledOptions", state.ScheduledOptions),
                    new XAttribute("scheduleEndAction", state.ScheduleEndAction),
                    new XAttribute("scheduleStartAction", state.ScheduleStartAction),
                    new XAttribute("weekDays", state.WeekDays)),
                new XElement("Playlist",
                    state.Playlist.Select(item => new XElement("Item",
                        new XAttribute("path", item.Path),
                        new XAttribute("title", item.Title),
                        new XAttribute("durationSeconds", item.DurationSeconds),
                        new XAttribute("options", item.Options),
                        new XAttribute("isBumper", item.IsBumper ? "1" : "0")))),
                new XElement("ScheduledPlaylist",
                    state.ScheduledPlaylist.Select(item => new XElement("Item",
                        new XAttribute("path", item.Path),
                        new XAttribute("title", item.Title),
                        new XAttribute("durationSeconds", item.DurationSeconds),
                        new XAttribute("options", item.Options),
                        new XAttribute("scheduleKind", item.ScheduleKind),
                        new XAttribute("startAt", item.StartAt.ToString("o")),
                        new XAttribute("weeklyTime", item.WeeklyTime.ToString()),
                        new XAttribute("weekDays", item.WeekDays),
                        new XAttribute("hasEndTime", item.HasEndTime ? "1" : "0"),
                        new XAttribute("endAt", item.EndAt.ToString("o")),
                        new XAttribute("endAction", item.EndAction),
                        new XAttribute("startAction", item.StartAction)))),
                new XElement("FavoriteInputs",
                    state.FavoriteInputs.Select(item => new XElement("Item",
                        new XAttribute("input", item.Input),
                        new XAttribute("title", item.Title),
                        new XAttribute("options", item.Options)))));
        }

        private void LoadAutoConfig()
        {
            string path = GetAutoConfigPath();
            if (!File.Exists(path)) return;

            try
            {
                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null) return;
                connectionStates.Clear();
                connectionTabs.TabPages.Clear();

                XElement tabsElement = root.Element("Tabs");
                if (tabsElement != null)
                {
                    foreach (XElement tabElement in tabsElement.Elements("Tab")) AddConnectionTab(LoadConnectionTabElement(tabElement), false);
                }
                else
                {
                    AddConnectionTab(LoadLegacyConnectionState(root), false);
                }

                if (connectionStates.Count == 0) AddConnectionTab(CreateDefaultConnectionState(), false);

                int selectedIndex = 0;
                int.TryParse(GetAttribute(root, "selectedTab", "0"), out selectedIndex);
                connectionTabs.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, connectionTabs.TabPages.Count - 1));
                activeState = connectionTabs.SelectedTab.Tag as ConnectionTabState;
                if (activeState != null) LoadConnectionStateToControls(activeState);
            }
            catch (Exception ex)
            {
                AppendLog("Load config failed: " + ex.Message);
            }
        }

        private ConnectionTabState LoadLegacyConnectionState(XElement root)
        {
            var state = CreateDefaultConnectionState();
            playlist = state.Playlist;
            scheduledPlaylist = state.ScheduledPlaylist;
            favoriteInputs = state.FavoriteInputs;

            XElement settings = root.Element("Settings");
            if (settings != null) LoadSettingsElement(settings);

            XElement playlistElement = root.Element("Playlist");
            if (playlistElement != null)
            {
                playlist.Clear();
                foreach (XElement itemElement in playlistElement.Elements("Item")) LoadPlaylistElement(itemElement);
            }

            XElement scheduledPlaylistElement = root.Element("ScheduledPlaylist");
            if (scheduledPlaylistElement != null)
            {
                scheduledPlaylist.Clear();
                foreach (XElement itemElement in scheduledPlaylistElement.Elements("Item")) LoadScheduledPlaylistElement(itemElement);
            }

            XElement favoriteInputsElement = root.Element("FavoriteInputs");
            if (favoriteInputsElement != null)
            {
                favoriteInputs.Clear();
                foreach (XElement itemElement in favoriteInputsElement.Elements("Item")) LoadFavoriteInputElement(itemElement);
            }

            SaveControlsToConnectionState(state);
            state.Name = "OmniVCam";
            return state;
        }

        private ConnectionTabState LoadConnectionTabElement(XElement tabElement)
        {
            var state = CreateDefaultConnectionState();
            state.Name = GetAttribute(tabElement, "name", state.Name);
            playlist = state.Playlist;
            scheduledPlaylist = state.ScheduledPlaylist;
            favoriteInputs = state.FavoriteInputs;

            XElement settings = tabElement.Element("Settings");
            if (settings != null) LoadSettingsElement(settings);

            XElement playlistElement = tabElement.Element("Playlist");
            playlist.Clear();
            if (playlistElement != null)
            {
                foreach (XElement itemElement in playlistElement.Elements("Item")) LoadPlaylistElement(itemElement);
            }

            XElement scheduledPlaylistElement = tabElement.Element("ScheduledPlaylist");
            scheduledPlaylist.Clear();
            if (scheduledPlaylistElement != null)
            {
                foreach (XElement itemElement in scheduledPlaylistElement.Elements("Item")) LoadScheduledPlaylistElement(itemElement);
            }

            XElement favoriteInputsElement = tabElement.Element("FavoriteInputs");
            favoriteInputs.Clear();
            if (favoriteInputsElement != null)
            {
                foreach (XElement itemElement in favoriteInputsElement.Elements("Item")) LoadFavoriteInputElement(itemElement);
            }

            SaveControlsToConnectionState(state);
            state.Name = GetAttribute(tabElement, "name", state.Name);
            return state;
        }

        private void SaveControlsToConnectionState(ConnectionTabState state)
        {
            ConnectionTabState previous = activeState;
            activeState = state;
            SaveActiveConnectionState();
            activeState = previous;
        }

        private void LoadSettingsElement(XElement settings)
        {
            decimal decimalValue;

            hostBox.Text = GetAttribute(settings, "host", hostBox.Text);
            inputBox.Text = GetAttribute(settings, "input", inputBox.Text);
            titleBox.Text = GetAttribute(settings, "title", titleBox.Text);
            optionsBox.Text = GetAttribute(settings, "options", optionsBox.Text);
            hwDecodeBox.Text = GetAttribute(settings, "hwDecode", hwDecodeBox.Text);
            string scaleMode = GetAttribute(settings, "scaleMode", scaleModeBox.Text);
            if (scaleModeBox.Items.Contains(scaleMode)) scaleModeBox.Text = scaleMode;
            displayAspectBox.Text = GetAttribute(settings, "displayAspect", displayAspectBox.Text);
            videoFilterBox.Text = GetAttribute(settings, "videoFilter", videoFilterBox.Text);
            audioFilterBox.Text = GetAttribute(settings, "audioFilter", audioFilterBox.Text);

            if (decimal.TryParse(GetAttribute(settings, "port", null), out decimalValue)) portBox.Value = Clamp(decimalValue, portBox.Minimum, portBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "videoIndex", null), out decimalValue)) videoIndexBox.Value = Clamp(decimalValue, videoIndexBox.Minimum, videoIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "audioIndex", null), out decimalValue)) audioIndexBox.Value = Clamp(decimalValue, audioIndexBox.Minimum, audioIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "shift", null), out decimalValue)) shiftBox.Value = Clamp(decimalValue, shiftBox.Minimum, shiftBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "seek", null), out decimalValue)) seekBox.Value = Clamp(decimalValue, seekBox.Minimum, seekBox.Maximum);

            byteSeekBox.Checked = GetAttribute(settings, "byteSeek", byteSeekBox.Checked ? "1" : "0") == "1";
            writeNowPlayingXmlBox.Checked = GetAttribute(settings, "writeNowPlayingXml", writeNowPlayingXmlBox.Checked ? "1" : "0") == "1";

            string playoutMode = GetAttribute(settings, "playoutMode", playoutModeBox.Text);
            if (playoutModeBox.Items.Contains(playoutMode)) playoutModeBox.Text = playoutMode;

            DateTime scheduled;
            if (DateTime.TryParse(GetAttribute(settings, "scheduledStart", null), out scheduled)) scheduledStartPicker.Value = scheduled;
            autoAdvanceBox.Checked = GetAttribute(settings, "autoAdvance", autoAdvanceBox.Checked ? "1" : "0") == "1";
            scheduledStartBox.Checked = GetAttribute(settings, "scheduledStartEnabled", scheduledStartBox.Checked ? "1" : "0") == "1";
            string scheduleType = GetAttribute(settings, "scheduleType", scheduleTypeBox.Text);
            if (scheduleTypeBox.Items.Contains(scheduleType)) scheduleTypeBox.Text = scheduleType;
            DateTime dateValue;
            if (DateTime.TryParse(GetAttribute(settings, "scheduleDateTime", null), out dateValue)) scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, dateValue);
            if (DateTime.TryParse(GetAttribute(settings, "scheduleTime", null), out dateValue)) scheduleTimePicker.Value = ClampDateTimePicker(scheduleTimePicker, dateValue);
            scheduleEndBox.Checked = GetAttribute(settings, "scheduleEndEnabled", scheduleEndBox.Checked ? "1" : "0") == "1";
            if (DateTime.TryParse(GetAttribute(settings, "scheduleEnd", null), out dateValue)) scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, dateValue);
            scheduledTitleBox.Text = GetAttribute(settings, "scheduledTitle", scheduledTitleBox.Text);
            scheduledOptionsBox.Text = GetAttribute(settings, "scheduledOptions", scheduledOptionsBox.Text);
            string endAction = GetAttribute(settings, "scheduleEndAction", scheduleEndActionBox.Text);
            if (scheduleEndActionBox.Items.Contains(endAction)) scheduleEndActionBox.Text = endAction;
            string startAction = GetAttribute(settings, "scheduleStartAction", scheduleStartActionBox.Text);
            if (scheduleStartActionBox.Items.Contains(startAction)) scheduleStartActionBox.Text = startAction;
            int weekDays;
            if (int.TryParse(GetAttribute(settings, "weekDays", null), out weekDays)) SetSelectedWeekDays(weekDays);
        }

        private void LoadPlaylistElement(XElement itemElement)
        {
            string itemPath = GetAttribute(itemElement, "path", string.Empty);
            if (string.IsNullOrWhiteSpace(itemPath)) return;

            int duration;
            playlist.Add(new PlaylistItem
            {
                Path = itemPath,
                Title = GetAttribute(itemElement, "title", System.IO.Path.GetFileNameWithoutExtension(itemPath)),
                DurationSeconds = int.TryParse(GetAttribute(itemElement, "durationSeconds", null), out duration) ? duration : 0,
                Options = GetAttribute(itemElement, "options", string.Empty),
                IsBumper = GetAttribute(itemElement, "isBumper", "0") == "1"
            });
        }

        private void LoadScheduledPlaylistElement(XElement itemElement)
        {
            string itemPath = GetAttribute(itemElement, "path", string.Empty);
            if (string.IsNullOrWhiteSpace(itemPath)) return;

            int duration;
            DateTime startAt;
            DateTime endAt;
            TimeSpan weeklyTime;
            int weekDays;
            ScheduleKind scheduleKind;
            ScheduleEndAction endAction;
            ScheduleStartAction startAction;

            if (!Enum.TryParse(GetAttribute(itemElement, "scheduleKind", "OneTime"), out scheduleKind)) scheduleKind = ScheduleKind.OneTime;
            if (!DateTime.TryParse(GetAttribute(itemElement, "startAt", null), out startAt)) startAt = DateTime.Now.AddMinutes(1);
            if (!DateTime.TryParse(GetAttribute(itemElement, "endAt", null), out endAt)) endAt = startAt.AddMinutes(30);
            if (!TimeSpan.TryParse(GetAttribute(itemElement, "weeklyTime", null), out weeklyTime)) weeklyTime = startAt.TimeOfDay;
            if (!int.TryParse(GetAttribute(itemElement, "weekDays", null), out weekDays)) weekDays = WeekDayMaskFromDay(startAt.DayOfWeek);
            if (!Enum.TryParse(GetAttribute(itemElement, "endAction", "WaitUntilEnd"), out endAction)) endAction = ScheduleEndAction.WaitUntilEnd;
            if (!Enum.TryParse(GetAttribute(itemElement, "startAction", "StartImmediately"), out startAction)) startAction = ScheduleStartAction.StartImmediately;

            scheduledPlaylist.Add(new ScheduledPlaylistItem
            {
                Path = itemPath,
                Title = GetAttribute(itemElement, "title", GetInputTitle(itemPath)),
                DurationSeconds = int.TryParse(GetAttribute(itemElement, "durationSeconds", null), out duration) ? duration : 0,
                Options = GetAttribute(itemElement, "options", string.Empty),
                ScheduleKind = scheduleKind,
                StartAt = startAt,
                WeeklyTime = weeklyTime,
                WeekDays = weekDays,
                HasEndTime = GetAttribute(itemElement, "hasEndTime", "0") == "1",
                EndAt = endAt,
                EndAction = endAction,
                StartAction = startAction
            });
        }

        private void LoadFavoriteInputElement(XElement itemElement)
        {
            string input = GetAttribute(itemElement, "input", string.Empty);
            if (string.IsNullOrWhiteSpace(input)) return;

            favoriteInputs.Add(new FavoriteInputItem
            {
                Input = input,
                Title = GetAttribute(itemElement, "title", GetInputTitle(input)),
                Options = GetAttribute(itemElement, "options", string.Empty)
            });
        }

        private static string GetAttribute(XElement element, string name, string defaultValue)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? defaultValue : attribute.Value;
        }

        private void LoadPlaylistLine(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) return;
            string itemPath = Unescape(parts[0]);
            playlist.Add(new PlaylistItem
            {
                Path = itemPath,
                Title = parts.Length > 1 ? Unescape(parts[1]) : System.IO.Path.GetFileNameWithoutExtension(itemPath),
                DurationSeconds = parts.Length > 2 && int.TryParse(parts[2], out int duration) ? duration : 0,
                Options = parts.Length > 3 ? Unescape(parts[3]) : string.Empty,
                IsBumper = parts.Length > 4 && parts[4] == "1"
            });
        }

        private static decimal Clamp(decimal value, decimal minimum, decimal maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static string GetAutoConfigPath()
        {
            return Path.Combine(Application.StartupPath, AutoConfigFileName);
        }

        private string GetNowPlayingPath()
        {
            if (activeState == null || connectionStates.IndexOf(activeState) <= 0) return Path.Combine(Application.StartupPath, NowPlayingFileName);
            string fileName = "OmniVCamNowPlaying-" + SanitizeFileName(activeState.Host + "-" + activeState.Port.ToString("0")) + ".xml";
            return Path.Combine(Application.StartupPath, fileName);
        }

        private static string SanitizeFileName(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
            }
            return builder.ToString();
        }

        private void WriteNowPlayingXml()
        {
            if (!writeNowPlayingXmlBox.Checked) return;
            try
            {
                string path = GetStatusInputPath(currentStatusInput);
                long position = Math.Max(0, currentPositionSeconds);
                long duration = Math.Max(0, currentDurationSeconds);
                var document = new XDocument(
                    new XElement("NowPlaying",
                        new XElement("Title", GetCurrentPlaybackTitle(path)),
                        new XElement("Path", path),
                        new XElement("PositionSeconds", position),
                        new XElement("Position", FormatSeconds(position)),
                        new XElement("DurationSeconds", duration),
                        new XElement("Duration", duration > 0 ? FormatSeconds(duration) : string.Empty),
                        new XElement("Status", FormatState(currentStatusState))));
                document.Save(GetNowPlayingPath());
            }
            catch (Exception ex)
            {
                AppendLog("Save now playing XML failed: " + ex.Message);
            }
        }

        private string GetCurrentPlaybackTitle(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (playingScheduled && currentScheduledIndex >= 0 && currentScheduledIndex < scheduledPlaylist.Count && PathsEqual(scheduledPlaylist[currentScheduledIndex].Path, path)) return scheduledPlaylist[currentScheduledIndex].Title;
            if (!playingScheduled && currentIndex >= 0 && currentIndex < playlist.Count && PathsEqual(playlist[currentIndex].Path, path)) return playlist[currentIndex].Title;
            if (PathsEqual(inputBox.Text.Trim(), path) && !string.IsNullOrWhiteSpace(titleBox.Text)) return titleBox.Text.Trim();
            ScheduledPlaylistItem scheduled = scheduledPlaylist.FirstOrDefault(item => PathsEqual(item.Path, path));
            if (scheduled != null) return scheduled.Title;
            PlaylistItem normal = playlist.FirstOrDefault(item => PathsEqual(item.Path, path));
            return normal != null ? normal.Title : GetInputTitle(path);
        }

        private static string FormatSeconds(long seconds)
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
        }

        private Task LoadPlaylistAsync()
        {
            using (var dialog = new OpenFileDialog { Filter = "OmniVCam playlist|*.ovcpl;*.txt|All files|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                playlist.Clear();
                foreach (string line in File.ReadAllLines(dialog.FileName, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) continue;
                    playlist.Add(new PlaylistItem
                    {
                        Path = Unescape(parts[0]),
                        Title = parts.Length > 1 ? Unescape(parts[1]) : System.IO.Path.GetFileNameWithoutExtension(Unescape(parts[0])),
                        DurationSeconds = parts.Length > 2 && int.TryParse(parts[2], out int duration) ? duration : 0,
                        Options = parts.Length > 3 ? Unescape(parts[3]) : string.Empty,
                        IsBumper = parts.Length > 4 && parts[4] == "1"
                    });
                }
            }
            currentIndex = -1;
            RefreshPlaylistView();
            return Task.CompletedTask;
        }

        private async Task RefreshDurationsAsync()
        {
            foreach (PlaylistItem item in playlist)
            {
                item.DurationSeconds = await GetDurationOrDefaultAsync(item.Path, item.Options);
            }
            RefreshPlaylistView();
        }

        private async Task RefreshScheduledDurationsAsync()
        {
            foreach (ScheduledPlaylistItem item in scheduledPlaylist)
            {
                item.DurationSeconds = await GetDurationOrDefaultAsync(item.Path, item.Options);
            }
            RefreshScheduledPlaylistView();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        private void RefreshPlaylistView()
        {
            playlistView.BeginUpdate();
            playlistView.Items.Clear();
            for (int i = 0; i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                bool isCurrent = !playingScheduled && PathsEqual(item.Path, GetStatusInputPath(currentStatusInput));
                var row = new ListViewItem((i + 1).ToString());
                row.SubItems.Add(isCurrent ? FormatState(currentStatusState) : string.Empty);
                row.SubItems.Add(item.IsBumper ? "Bumper" : "Program");
                row.SubItems.Add(item.Title);
                row.SubItems.Add(FormatDuration(item.DurationSeconds));
                row.SubItems.Add(item.Options);
                row.SubItems.Add(item.Path);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(225, 245, 225);
                else if (item.IsBumper) row.BackColor = Color.FromArgb(245, 245, 230);
                playlistView.Items.Add(row);
            }
            playlistView.EndUpdate();
            SelectCurrentRow();
        }

        private void RefreshScheduledPlaylistView()
        {
            scheduledPlaylistView.BeginUpdate();
            scheduledPlaylistView.Items.Clear();
            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                ScheduledPlaylistItem item = scheduledPlaylist[i];
                bool isCurrent = playingScheduled && i == currentScheduledIndex;
                ScheduleWindow window = GetScheduleWindow(i, DateTime.Now);
                var row = new ListViewItem((i + 1).ToString());
                row.SubItems.Add(isCurrent ? FormatState(currentStatusState) : FormatScheduledIdleStatus(item, window));
                row.SubItems.Add(item.Title);
                row.SubItems.Add(FormatDuration(item.DurationSeconds));
                row.SubItems.Add(FormatSchedule(item));
                row.SubItems.Add(FormatScheduleEnd(item));
                row.SubItems.Add(StartActionText(item.StartAction));
                row.SubItems.Add(EndActionText(item.EndAction));
                row.SubItems.Add(FormatLastTriggered(item.LastTriggeredAt));
                row.SubItems.Add(item.Path);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(220, 238, 255);
                else if (FormatScheduledIdleStatus(item, window) == "Blocked") row.BackColor = Color.FromArgb(255, 248, 220);
                scheduledPlaylistView.Items.Add(row);
            }
            scheduledPlaylistView.EndUpdate();
            SelectCurrentScheduledRow();
        }

        private void UpdatePlaylistStatusView()
        {
            string statusPath = GetStatusInputPath(currentStatusInput);
            playlistView.BeginUpdate();
            for (int i = 0; i < playlistView.Items.Count && i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                bool isCurrent = !playingScheduled && PathsEqual(item.Path, statusPath);
                ListViewItem row = playlistView.Items[i];
                row.SubItems[1].Text = isCurrent ? FormatState(currentStatusState) : string.Empty;
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(225, 245, 225);
                else row.BackColor = item.IsBumper ? Color.FromArgb(245, 245, 230) : SystemColors.Window;
            }
            playlistView.EndUpdate();
            scheduledPlaylistView.BeginUpdate();
            for (int i = 0; i < scheduledPlaylistView.Items.Count && i < scheduledPlaylist.Count; i++)
            {
                bool isCurrent = playingScheduled && i == currentScheduledIndex;
                ListViewItem row = scheduledPlaylistView.Items[i];
                ScheduleWindow window = GetScheduleWindow(i, DateTime.Now);
                string idleStatus = FormatScheduledIdleStatus(scheduledPlaylist[i], window);
                row.SubItems[1].Text = isCurrent ? FormatState(currentStatusState) : idleStatus;
                row.SubItems[8].Text = FormatLastTriggered(scheduledPlaylist[i].LastTriggeredAt);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(220, 238, 255);
                else if (idleStatus == "Blocked") row.BackColor = Color.FromArgb(255, 248, 220);
                else row.BackColor = SystemColors.Window;
            }
            scheduledPlaylistView.EndUpdate();
        }

        private async Task SendCommandAsync(string command)
        {
            string reply = await SendRawCommandAsync(command);
            AppendLog($"> {command}\r\n< {reply}");
        }

        private async Task SendCommandsAsync(IEnumerable<string> commands)
        {
            var commandList = commands.Where(command => command != null).ToList();
            if (commandList.Count == 0) return;

            var log = new StringBuilder();
            foreach (string command in commandList)
            {
                string reply = await SendRawCommandAsync(command);
                log.Append("> ").Append(command).Append("\r\n< ").Append(reply ?? "ERR no reply").Append("\r\n");
                if (reply == null || !reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) break;
            }

            AppendLog(log.ToString().TrimEnd());
        }

        private Task<string> SendRawCommandAsync(string command)
        {
            return commandClient.SendAsync(hostBox.Text.Trim(), (int)portBox.Value, command);
        }

        private async Task RefreshStatusAsync()
        {
            string reply = await SendRawCommandAsync("STATUS");
            if (reply == null || !reply.StartsWith("OK seconds=", StringComparison.OrdinalIgnoreCase)) return;
            string value = reply.Substring("OK seconds=".Length);
            int space = value.IndexOf(' ');
            if (space >= 0) value = value.Substring(0, space);
            if (long.TryParse(value, out long seconds))
            {
                bool ignoreStaleSeekStatus = pendingSeekSeconds >= 0 && DateTime.Now < pendingSeekUntil && Math.Abs(seconds - pendingSeekSeconds) > 1;
                if (!ignoreStaleSeekStatus && pendingSeekSeconds >= 0)
                {
                    pendingSeekSeconds = -1;
                    pendingSeekUntil = DateTime.MinValue;
                }
                if (pendingSeekSeconds >= 0 && DateTime.Now >= pendingSeekUntil)
                {
                    pendingSeekSeconds = -1;
                    pendingSeekUntil = DateTime.MinValue;
                }

                currentPositionSeconds = seconds;
                currentDurationSeconds = ParseStatusLong(reply, "duration=");
                currentSizeBytes = ParseStatusLong(reply, "size=");
                currentStatusState = ParseStatusString(reply, "state=");
                currentStatusInput = ParseStatusString(reply, "input=");
                UpdateManualPlayButtonText();
                if (!syncingInitialCameraSettings && ParseStatusLong(reply, "controller_connected=") == 0)
                {
                    syncingInitialCameraSettings = true;
                    try
                    {
                        await SendInitialCameraSettingsAsync();
                    }
                    finally
                    {
                        syncingInitialCameraSettings = false;
                    }
                }
                UpdatePlayoutStatusLabel();
                UpdatePlaylistStatusView();
                string durationText = currentDurationSeconds > 0 ? TimeSpan.FromSeconds(currentDurationSeconds).ToString(@"hh\:mm\:ss") : "--:--:--";
                long displaySeconds = ignoreStaleSeekStatus ? pendingSeekSeconds : seconds;
                positionLabel.Text = TimeSpan.FromSeconds(displaySeconds).ToString(@"hh\:mm\:ss") + " / " + durationText;
                if (!ignoreStaleSeekStatus && !updatingProgress && !draggingProgress && currentDurationSeconds > 0)
                {
                    int progress = (int)Math.Max(0, Math.Min(progressBar.Maximum, seconds * progressBar.Maximum / currentDurationSeconds));
                    updatingProgress = true;
                    progressBar.Value = progress;
                    updatingProgress = false;
                }
                WriteNowPlayingXml();
            }
        }
        private static long ParseStatusLong(string reply, string key)
        {
            int start = reply.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return 0;
            start += key.Length;
            int end = reply.IndexOf(' ', start);
            string value = end >= 0 ? reply.Substring(start, end - start) : reply.Substring(start);
            return long.TryParse(value, out long parsed) ? parsed : 0;
        }

        private static string ParseStatusString(string reply, string key)
        {
            int start = reply.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += key.Length;
            if (key.Equals("input=", StringComparison.OrdinalIgnoreCase)) return reply.Substring(start).Trim();
            int end = reply.IndexOf(' ', start);
            return (end >= 0 ? reply.Substring(start, end - start) : reply.Substring(start)).Trim();
        }

        private static string GetStatusInputPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            int tab = input.IndexOf('\t');
            return (tab >= 0 ? input.Substring(0, tab) : input).Trim();
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string FormatState(string state)
        {
            if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase)) return "Playing";
            if (string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase)) return "Paused";
            if (string.Equals(state, "opening", StringComparison.OrdinalIgnoreCase)) return "Opening";
            if (string.Equals(state, "ended", StringComparison.OrdinalIgnoreCase)) return "Ended";
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)) return "Error";
            return "Stopped";
        }

        private static string FormatDuration(int seconds)
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : "--:--:--";
        }

        private void UpdatePlayoutStatusLabel()
        {
            string running = playoutRunning ? "running" : "stopped";
            string source = playingScheduled ? "scheduled" : "normal";
            playoutStatusLabel.Text = "Playout: " + running + " / " + source + " / " + FormatState(currentStatusState);
            playoutStatusLabel.ForeColor = playoutRunning ? Color.DarkGreen : SystemColors.ControlText;
            playoutStatusLabel.Font = new Font(playoutStatusLabel.Font, playoutRunning ? FontStyle.Bold : FontStyle.Regular);
        }

        private bool IsCurrentPlaybackActive()
        {
            return string.Equals(currentStatusState, "playing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currentStatusState, "paused", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currentStatusState, "opening", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateManualPlayButtonText()
        {
            if (manualPlayButton == null) return;
            manualPlayButton.Text = string.Equals(currentStatusState, "playing", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(currentStatusState, "opening", StringComparison.OrdinalIgnoreCase)
                ? "Pause"
                : "Play";
        }

        private string FormatScheduledIdleStatus(ScheduledPlaylistItem item, ScheduleWindow window)
        {
            if (!window.IsActive) return "Waiting";
            if (item.StartAction == ScheduleStartAction.WaitCurrentItem && IsCurrentPlaybackActive()) return "Blocked";
            return "In window";
        }

        private ScheduleWindow GetScheduleWindow(int index, DateTime now)
        {
            if (index < 0 || index >= scheduledPlaylist.Count) return ScheduleWindow.Empty;
            ScheduledPlaylistItem item = scheduledPlaylist[index];
            DateTime start = GetCurrentScheduleStart(item, now);
            if (start == DateTime.MinValue) return ScheduleWindow.Empty;

            DateTime end = item.HasEndTime ? GetExplicitScheduleEnd(item, start) : GetNextScheduleStart(start);
            if (end <= start) end = DateTime.MaxValue;
            return new ScheduleWindow(start, end, now >= start && now < end);
        }

        private DateTime GetCurrentScheduleStart(ScheduledPlaylistItem item, DateTime now)
        {
            if (item.ScheduleKind == ScheduleKind.OneTime) return item.StartAt;

            DateTime best = DateTime.MinValue;
            for (int offset = -7; offset <= 0; offset++)
            {
                DateTime candidateDate = now.Date.AddDays(offset);
                if ((item.WeekDays & WeekDayMaskFromDay(candidateDate.DayOfWeek)) == 0) continue;
                DateTime candidate = candidateDate.Add(item.WeeklyTime);
                if (candidate <= now && candidate > best) best = candidate;
            }
            return best;
        }

        private DateTime GetExplicitScheduleEnd(ScheduledPlaylistItem item, DateTime start)
        {
            if (item.ScheduleKind == ScheduleKind.OneTime) return item.EndAt;
            DateTime end = start.Date.Add(item.EndAt.TimeOfDay);
            if (end <= start) end = end.AddDays(1);
            return end;
        }

        private DateTime GetNextScheduleStart(DateTime start)
        {
            DateTime best = DateTime.MaxValue;
            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                DateTime candidate = GetNextScheduleStartAfter(scheduledPlaylist[i], start);
                if (candidate > start && candidate < best) best = candidate;
            }
            return best;
        }

        private static DateTime GetNextScheduleStartAfter(ScheduledPlaylistItem item, DateTime after)
        {
            if (item.ScheduleKind == ScheduleKind.OneTime) return item.StartAt > after ? item.StartAt : DateTime.MaxValue;

            DateTime best = DateTime.MaxValue;
            for (int offset = 0; offset <= 7; offset++)
            {
                DateTime candidateDate = after.Date.AddDays(offset);
                if ((item.WeekDays & WeekDayMaskFromDay(candidateDate.DayOfWeek)) == 0) continue;
                DateTime candidate = candidateDate.Add(item.WeeklyTime);
                if (candidate > after && candidate < best) best = candidate;
            }
            return best;
        }

        private ScheduleEndAction GetSelectedEndAction()
        {
            if (scheduleEndActionBox.Text == "Replay until end") return ScheduleEndAction.ReplayUntilEnd;
            if (scheduleEndActionBox.Text == "Continue immediately") return ScheduleEndAction.ContinueImmediately;
            return ScheduleEndAction.WaitUntilEnd;
        }

        private ScheduleStartAction GetSelectedStartAction()
        {
            return scheduleStartActionBox.Text == "Wait current item" ? ScheduleStartAction.WaitCurrentItem : ScheduleStartAction.StartImmediately;
        }

        private static string EndActionText(ScheduleEndAction action)
        {
            if (action == ScheduleEndAction.ReplayUntilEnd) return "Replay until end";
            if (action == ScheduleEndAction.ContinueImmediately) return "Continue immediately";
            return "Wait until end";
        }

        private static string StartActionText(ScheduleStartAction action)
        {
            return action == ScheduleStartAction.WaitCurrentItem ? "Wait current item" : "Start immediately";
        }

        private int GetSelectedWeekDays()
        {
            int mask = 0;
            if (mondayBox.Checked) mask |= (int)WeekDayMask.Monday;
            if (tuesdayBox.Checked) mask |= (int)WeekDayMask.Tuesday;
            if (wednesdayBox.Checked) mask |= (int)WeekDayMask.Wednesday;
            if (thursdayBox.Checked) mask |= (int)WeekDayMask.Thursday;
            if (fridayBox.Checked) mask |= (int)WeekDayMask.Friday;
            if (saturdayBox.Checked) mask |= (int)WeekDayMask.Saturday;
            if (sundayBox.Checked) mask |= (int)WeekDayMask.Sunday;
            return mask;
        }

        private void SetSelectedWeekDays(int mask)
        {
            mondayBox.Checked = (mask & (int)WeekDayMask.Monday) != 0;
            tuesdayBox.Checked = (mask & (int)WeekDayMask.Tuesday) != 0;
            wednesdayBox.Checked = (mask & (int)WeekDayMask.Wednesday) != 0;
            thursdayBox.Checked = (mask & (int)WeekDayMask.Thursday) != 0;
            fridayBox.Checked = (mask & (int)WeekDayMask.Friday) != 0;
            saturdayBox.Checked = (mask & (int)WeekDayMask.Saturday) != 0;
            sundayBox.Checked = (mask & (int)WeekDayMask.Sunday) != 0;
        }

        private static int WeekDayMaskFromDay(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return (int)WeekDayMask.Monday;
                case DayOfWeek.Tuesday: return (int)WeekDayMask.Tuesday;
                case DayOfWeek.Wednesday: return (int)WeekDayMask.Wednesday;
                case DayOfWeek.Thursday: return (int)WeekDayMask.Thursday;
                case DayOfWeek.Friday: return (int)WeekDayMask.Friday;
                case DayOfWeek.Saturday: return (int)WeekDayMask.Saturday;
                default: return (int)WeekDayMask.Sunday;
            }
        }

        private static string FormatSchedule(ScheduledPlaylistItem item)
        {
            if (item.ScheduleKind == ScheduleKind.OneTime) return item.StartAt.ToString("yyyy-MM-dd HH:mm:ss");
            return FormatWeekDays(item.WeekDays) + " " + item.WeeklyTime.ToString(@"hh\:mm\:ss");
        }

        private static string FormatScheduleEnd(ScheduledPlaylistItem item)
        {
            if (!item.HasEndTime) return "Next schedule";
            if (item.ScheduleKind == ScheduleKind.Weekly) return item.EndAt.TimeOfDay.ToString(@"hh\:mm\:ss");
            return item.EndAt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static string FormatLastTriggered(DateTime value)
        {
            return value == DateTime.MinValue ? string.Empty : value.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static string FormatWeekDays(int mask)
        {
            var names = new List<string>();
            if ((mask & (int)WeekDayMask.Monday) != 0) names.Add("Mon");
            if ((mask & (int)WeekDayMask.Tuesday) != 0) names.Add("Tue");
            if ((mask & (int)WeekDayMask.Wednesday) != 0) names.Add("Wed");
            if ((mask & (int)WeekDayMask.Thursday) != 0) names.Add("Thu");
            if ((mask & (int)WeekDayMask.Friday) != 0) names.Add("Fri");
            if ((mask & (int)WeekDayMask.Saturday) != 0) names.Add("Sat");
            if ((mask & (int)WeekDayMask.Sunday) != 0) names.Add("Sun");
            return names.Count == 0 ? "No days" : string.Join(",", names);
        }

        private static DateTime ClampDateTimePicker(DateTimePicker picker, DateTime value)
        {
            if (value < picker.MinDate) return picker.MinDate;
            if (value > picker.MaxDate) return picker.MaxDate;
            return value;
        }

        private void ProgressBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            draggingProgress = true;
            progressBar.Capture = true;
            SetProgressFromMouse(e.X);
        }

        private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!draggingProgress || e.Button != MouseButtons.Left) return;
            SetProgressFromMouse(e.X);
        }

        private async Task ProgressBar_MouseUpAsync(MouseEventArgs e)
        {
            if (!draggingProgress || e.Button != MouseButtons.Left) return;
            SetProgressFromMouse(e.X);
            draggingProgress = false;
            progressBar.Capture = false;
            await SeekByProgressAsync(progressBar.Value);
        }

        private void SetProgressFromMouse(int mouseX)
        {
            if (updatingProgress) return;
            int progress = ProgressFromMouseX(mouseX);

            updatingProgress = true;
            progressBar.Value = progress;
            updatingProgress = false;
        }

        private int ProgressFromMouseX(int mouseX)
        {
            Rectangle channel = GetTrackBarChannelRectangle(progressBar);
            int left = channel.Left;
            int right = Math.Max(channel.Left + 1, channel.Right);
            double ratio = (double)(Math.Max(left, Math.Min(right, mouseX)) - left) / (right - left);
            return progressBar.Minimum + (int)Math.Round(ratio * (progressBar.Maximum - progressBar.Minimum));
        }

        private static Rectangle GetTrackBarChannelRectangle(TrackBar trackBar)
        {
            if (trackBar.IsHandleCreated)
            {
                RECT rect = new RECT();
                SendMessage(trackBar.Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref rect);
                if (rect.Right > rect.Left) return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
            return trackBar.ClientRectangle;
        }

        private async Task SeekByProgressAsync(int progress)
        {
            if (updatingProgress) return;

            if (byteSeekBox.Checked)
            {
                await SendCommandAsync($"SEEK_BYTE_PERCENT {progress}");
                return;
            }
            if (currentDurationSeconds <= 0)
            {
                AppendLog("Duration is unknown. Enable Byte seek to seek by file position.");
                return;
            }
            long seconds = currentDurationSeconds * progress / progressBar.Maximum;
            suppressSeekValueEvent = true;
            seekBox.Value = Math.Max(seekBox.Minimum, Math.Min(seekBox.Maximum, seconds));
            suppressSeekValueEvent = false;
            await SeekBySecondsAsync(seconds);
        }

        private async Task SeekBySecondsAsync(long seconds)
        {
            currentPositionSeconds = seconds;
            pendingSeekSeconds = seconds;
            pendingSeekUntil = DateTime.Now.AddSeconds(5);
            await SendCommandAsync($"SEEK {seconds}");
        }

        private async Task SeekRelativeAsync(long deltaSeconds)
        {
            long targetSeconds = Math.Max(0, currentPositionSeconds + deltaSeconds);
            if (currentDurationSeconds > 0) targetSeconds = Math.Min(currentDurationSeconds, targetSeconds);

            suppressSeekValueEvent = true;
            seekBox.Value = Math.Max(seekBox.Minimum, Math.Min(seekBox.Maximum, targetSeconds));
            suppressSeekValueEvent = false;
            await SeekBySecondsAsync(targetSeconds);
        }

        private const int TBM_GETCHANNELRECT = 0x041A;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void AppendRemoteLog(string text, int generation)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, int>(AppendRemoteLog), text, generation);
                return;
            }
            if (generation == commandClient.Generation) AppendLog("[vcam] " + text);
        }

        private void AppendLog(string text)
        {
            pendingLogText.Append(DateTime.Now.ToString("HH:mm:ss")).Append(" ").Append(text).Append(Environment.NewLine);
            if ((DateTime.UtcNow - lastLogFlushAt).TotalMilliseconds >= 500) FlushPendingLog();
        }

        private void FlushPendingLog()
        {
            if (pendingLogText.Length == 0) return;
            logBox.AppendText(pendingLogText.ToString());
            pendingLogText.Clear();
            lastLogFlushAt = DateTime.UtcNow;
            if (logBox.Lines.Length > LOG_MAX_LINES)
            {
                int linesToRemove = logBox.Lines.Length - LOG_MAX_LINES;
                var allLines = logBox.Lines;
                var keepLines = allLines.Skip(linesToRemove).ToArray();

                logBox.Lines = keepLines;

                logBox.SelectionStart = logBox.Text.Length;
                logBox.ScrollToCaret();
            }
        }

        private sealed class TcpCommandClient : IDisposable
        {
            private const int CommandTimeoutMs = 5000;
            private readonly Action<string, int> logCallback;
            private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
            private readonly ConcurrentQueue<TaskCompletionSource<string>> pendingReplies = new ConcurrentQueue<TaskCompletionSource<string>>();
            private readonly object sync = new object();
            private TcpClient client;
            private NetworkStream stream;
            private CancellationTokenSource cts = new CancellationTokenSource();
            private Task readTask;
            private string host;
            private int port;
            private bool disposed;
            private int generation;

            public TcpCommandClient(Action<string, int> logCallback)
            {
                this.logCallback = logCallback;
            }

            public int Generation { get { return generation; } }

            public void Reset()
            {
                generation++;
                Disconnect();
            }

            public async Task<string> SendAsync(string host, int port, string command)
            {
                if (disposed) return "ERR client disposed";
                try
                {
                    await EnsureConnectedAsync(host, port);
                    var tcs = new TaskCompletionSource<string>();
                    pendingReplies.Enqueue(tcs);
                    await writeLock.WaitAsync();
                    try
                    {
                        byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                        await stream.WriteAsync(data, 0, data.Length, cts.Token);
                        await stream.FlushAsync(cts.Token);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                    Task finished = await Task.WhenAny(tcs.Task, Task.Delay(CommandTimeoutMs));
                    if (finished != tcs.Task)
                    {
                        tcs.TrySetResult("ERR command timeout");
                        Disconnect();
                    }
                    return await tcs.Task;
                }
                catch (Exception ex)
                {
                    Disconnect();
                    return "ERR " + ex.Message;
                }
            }

            private async Task EnsureConnectedAsync(string host, int port)
            {
                lock (sync)
                {
                    if (client != null && client.Connected && string.Equals(this.host, host, StringComparison.OrdinalIgnoreCase) && this.port == port) return;
                }

                await writeLock.WaitAsync();
                try
                {
                    lock (sync)
                    {
                        if (client != null && client.Connected && string.Equals(this.host, host, StringComparison.OrdinalIgnoreCase) && this.port == port) return;
                    }
                    Disconnect();
                    cts = new CancellationTokenSource();
                    var newClient = new TcpClient();
                    await newClient.ConnectAsync(host, port);
                    lock (sync)
                    {
                        client = newClient;
                        stream = client.GetStream();
                        this.host = host;
                        this.port = port;
                        int readGeneration = generation;
                        readTask = Task.Run(() => ReadLoopAsync(cts.Token, readGeneration));
                    }
                }
                finally
                {
                    writeLock.Release();
                }
            }

            private async Task ReadLoopAsync(CancellationToken token, int readGeneration)
            {
                try
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        while (!token.IsCancellationRequested)
                        {
                            string line = await reader.ReadLineAsync();
                            if (line == null) break;
                            if (line.StartsWith("LOG ", StringComparison.OrdinalIgnoreCase))
                            {
                                logCallback(ParseLogLine(line), readGeneration);
                                continue;
                            }
                            if (pendingReplies.TryDequeue(out TaskCompletionSource<string> tcs)) tcs.TrySetResult(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) logCallback("connection closed: " + ex.Message, readGeneration);
                }
                finally
                {
                    while (pendingReplies.TryDequeue(out TaskCompletionSource<string> tcs)) tcs.TrySetResult("ERR connection closed");
                    if (readGeneration == generation) Disconnect();
                }
            }

            private static string ParseLogLine(string line)
            {
                const string marker = " message=";
                int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                return index >= 0 ? line.Substring(index + marker.Length) : line.Substring(4).TrimStart();
            }

            private void Disconnect()
            {
                TcpClient oldClient = null;
                lock (sync)
                {
                    if (cts != null && !cts.IsCancellationRequested) cts.Cancel();
                    oldClient = client;
                    client = null;
                    stream = null;
                    host = null;
                    port = 0;
                }
                try { oldClient?.Close(); } catch { }
            }

            public void Dispose()
            {
                disposed = true;
                Disconnect();
                cts.Dispose();
                writeLock.Dispose();
            }
        }
        private sealed class PlaylistItem
        {
            public string Path { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int DurationSeconds { get; set; }
            public string Options { get; set; } = string.Empty;
            public bool IsBumper { get; set; }
        }

        private sealed class ScheduledPlaylistItem
        {
            public string Path { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int DurationSeconds { get; set; }
            public string Options { get; set; } = string.Empty;
            public ScheduleKind ScheduleKind { get; set; }
            public DateTime StartAt { get; set; }
            public TimeSpan WeeklyTime { get; set; }
            public int WeekDays { get; set; }
            public bool HasEndTime { get; set; }
            public DateTime EndAt { get; set; }
            public ScheduleEndAction EndAction { get; set; }
            public ScheduleStartAction StartAction { get; set; }
            public DateTime LastTriggeredAt { get; set; } = DateTime.MinValue;
        }

        private struct ScheduleWindow
        {
            public static readonly ScheduleWindow Empty = new ScheduleWindow(DateTime.MinValue, DateTime.MinValue, false);

            public ScheduleWindow(DateTime start, DateTime end, bool isActive)
            {
                Start = start;
                End = end;
                IsActive = isActive;
            }

            public DateTime Start { get; }
            public DateTime End { get; }
            public bool IsActive { get; }
        }

        private enum ScheduleKind
        {
            OneTime,
            Weekly
        }

        private enum ScheduleEndAction
        {
            ReplayUntilEnd,
            WaitUntilEnd,
            ContinueImmediately
        }

        private enum ScheduleStartAction
        {
            StartImmediately,
            WaitCurrentItem
        }

        [Flags]
        private enum WeekDayMask
        {
            Monday = 1,
            Tuesday = 2,
            Wednesday = 4,
            Thursday = 8,
            Friday = 16,
            Saturday = 32,
            Sunday = 64
        }

        private sealed class FavoriteInputItem
        {
            public string Input { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
        }

        private sealed class ConnectionTabState
        {
            public string Name { get; set; } = string.Empty;
            public string Host { get; set; } = "127.0.0.1";
            public decimal Port { get; set; } = 16999;
            public string Input { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
            public string HwDecode { get; set; } = "none";
            public string ScaleMode { get; set; } = "letterbox";
            public string DisplayAspect { get; set; } = "auto";
            public string VideoFilter { get; set; } = string.Empty;
            public string AudioFilter { get; set; } = string.Empty;
            public decimal VideoIndex { get; set; } = -1;
            public decimal AudioIndex { get; set; } = -1;
            public decimal Shift { get; set; }
            public decimal Seek { get; set; }
            public bool ByteSeek { get; set; }
            public bool WriteNowPlayingXml { get; set; } = true;
            public string PlayoutMode { get; set; } = "Sequential";
            public bool AutoAdvance { get; set; } = true;
            public bool ScheduledStartEnabled { get; set; }
            public DateTime ScheduledStart { get; set; } = DateTime.Now;
            public string ScheduleType { get; set; } = "One-time";
            public DateTime ScheduleDateTime { get; set; } = DateTime.Now;
            public DateTime ScheduleTime { get; set; } = DateTime.Now;
            public bool ScheduleEndEnabled { get; set; }
            public DateTime ScheduleEnd { get; set; } = DateTime.Now.AddMinutes(30);
            public string ScheduledTitle { get; set; } = string.Empty;
            public string ScheduledOptions { get; set; } = string.Empty;
            public string ScheduleEndAction { get; set; } = "Wait until end";
            public string ScheduleStartAction { get; set; } = "Start immediately";
            public int WeekDays { get; set; }
            public List<PlaylistItem> Playlist { get; set; } = new List<PlaylistItem>();
            public List<ScheduledPlaylistItem> ScheduledPlaylist { get; set; } = new List<ScheduledPlaylistItem>();
            public List<FavoriteInputItem> FavoriteInputs { get; set; } = new List<FavoriteInputItem>();
            public int CurrentIndex { get; set; } = -1;
            public int CurrentScheduledIndex { get; set; } = -1;
            public bool PlayoutRunning { get; set; }
            public bool PlayingScheduled { get; set; }
            public bool WaitingForScheduledStart { get; set; }
            public DateTime ScheduledSlotEndAt { get; set; } = DateTime.MinValue;
            public DateTime CurrentStartedAt { get; set; }
            public long CurrentPositionSeconds { get; set; }
            public long CurrentDurationSeconds { get; set; }
            public long CurrentSizeBytes { get; set; }
            public string CurrentStatusState { get; set; } = "stopped";
            public string CurrentStatusInput { get; set; } = string.Empty;
            public bool AdvancingPlayout { get; set; }
            public DateTime LastAutoAdvanceAt { get; set; } = DateTime.MinValue;
            public long PendingSeekSeconds { get; set; } = -1;
            public DateTime PendingSeekUntil { get; set; } = DateTime.MinValue;
            public int ProgressValue { get; set; }
            public string LogText { get; set; } = string.Empty;
            public List<int> SelectedPlaylistIndices { get; set; } = new List<int>();
            public List<int> SelectedScheduledPlaylistIndices { get; set; } = new List<int>();
            public List<int> SelectedFavoriteInputIndices { get; set; } = new List<int>();
        }
    }
}
