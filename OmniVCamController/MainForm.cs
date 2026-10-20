using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using OmniVCamController.Resources;

namespace OmniVCamController
{
    public sealed partial class MainForm : Form
    {
        private const string AutoConfigFileName = "OmniVCamController.xml";
        private const string NowPlayingFileName = "OmniVCamNowPlaying.xml";
        private const string ObsSourceSeparator = "␟";
        private static string T(string name) => AppStrings.ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
        private static string TF(string name, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(name), args);

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
        private ScheduledPlaylistItem currentScheduledItem;
        private bool playoutRunning;
        private bool playingScheduled;
        private bool scheduledObsEndApplied;
        private bool waitingForScheduledStart;
        private DateTime scheduledSlotEndAt = DateTime.MinValue;
        private DateTime currentStartedAt;
        private long currentPositionSeconds;
        private long currentDurationSeconds;
        private long currentSizeBytes;
		private long currentDeliverNs;
		private long currentDeliverAvgNs;
        private string currentStatusState = "stopped";
        private string currentStatusInput = string.Empty;
        private string currentActualStatusInput = string.Empty;
        private bool advancingPlayout;
        private bool processingPlayoutTick;
        private DateTime lastAutoAdvanceAt = DateTime.MinValue;
        private DateTime terminalStatusSince = DateTime.MinValue;
        private static readonly TimeSpan PendingPlaybackTimeout = TimeSpan.FromSeconds(10);
        private Guid playNextOverrideId = Guid.Empty;
        private Guid activePlayNextOverrideId = Guid.Empty;
        private int playNextResumeIndex = -1;
        private PendingPlaybackTransition pendingPlaybackTransition;
        private readonly Queue<BackendInputEvent> backendInputEvents = new Queue<BackendInputEvent>();
        private bool handlingBackendInputEvents;
        private readonly Queue<ScheduledObsAction> pendingScheduledObsEnds = new Queue<ScheduledObsAction>();
        private int playbackStateGeneration;
        private long pendingSeekSeconds = -1;
        private DateTime pendingSeekUntil = DateTime.MinValue;
        private bool updatingProgress;
        private bool draggingProgress;
        private bool suppressSeekValueEvent;
        private bool suppressInputTitleUpdate;
        private bool controlsReady;
        private bool syncingInitialCameraSettings;
        private bool refreshingStatus;
        private Button manualPlayButton;
        private Button dshowConfigureButton;
        private CheckBox dshowDeviceTimestampBox;
        private CheckBox dshowAudioDeviceTimestampBox;
        private NumericUpDown dshowAudioBufferSizeBox;
        private NumericUpDown queueLeftBox;
        private NumericUpDown queueRightBox;
        private NumericUpDown queueCenterBox;
        private bool updatingQueueControls;
        private bool updatingDShowTimestamp;
        private ComboBox languageBox;
        private Form playoutForm;
        private bool closingMainForm;
        private readonly TcpCommandClient commandClient;
        private readonly StringBuilder pendingLogText = new StringBuilder();
        private readonly int LOG_MAX_LINES = 1000;
        private DateTime lastLogFlushAt = DateTime.UtcNow;

        public MainForm()
        {
            InitializeComponent();
            EnableDoubleBuffering(playlistView);
            EnableDoubleBuffering(scheduledPlaylistView);
            ApplyLocalizedDesignerText();
            commandClient = new TcpCommandClient(AppendRemoteLog, HandleBackendEvent, HandleBackendConnectionClosed);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildManualPanel(mainContentPanel);
            BuildPlayoutWindow();
            hwDecodeBox.Items.AddRange(new object[] { "none", "dxva2", "d3d11va", "cuda", "qsv" });
            hwDecodeBox.Text = "none";
            scaleModeBox.Items.AddRange(new object[] { "letterbox", "fill" });
            scaleModeBox.Text = "letterbox";
            displayAspectBox.Items.AddRange(new object[] { "auto", "16:9", "4:3", "1:1" });
            displayAspectBox.Text = "auto";
            autoDeinterlaceFilterBox.Items.AddRange(new object[] { "bwdif", "yadif", "w3fdif" });
            autoDeinterlaceFilterBox.Text = "bwdif";
            InitializeLocalizedComboItems();
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
                FlushPendingLog();
                SaveActiveConnectionState();
                SaveAutoConfig();
                if (playoutForm != null && !playoutForm.IsDisposed) playoutForm.Close();
                if (interactiveForm != null && !interactiveForm.IsDisposed) interactiveForm.Close();
                commandClient.Dispose();
            };
        }

        private static void EnableDoubleBuffering(Control control)
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

        private void ApplyLocalizedDesignerText()
        {
            deliverLabel.Text = T("FrameDeliverBlockTimeEmpty");
            byteSeekBox.Text = T("ByteSeek");
            playoutStatusLabel.Text = T("PlayoutStatusStopped");
            mondayBox.Text = T("WeekMonday");
            tuesdayBox.Text = T("WeekTuesday");
            wednesdayBox.Text = T("WeekWednesday");
            thursdayBox.Text = T("WeekThursday");
            fridayBox.Text = T("WeekFriday");
            saturdayBox.Text = T("WeekSaturday");
            sundayBox.Text = T("WeekSunday");
            scheduleEndBox.Text = T("UseEndTime");
            scheduledEnabledBox.Text = T("Enabled");
            writeNowPlayingXmlBox.Text = T("WriteNowPlayingXml");
            autoAdvanceBox.Text = T("NormalAutoNext");
            scheduledStartBox.Text = T("StartAt");
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

        private void InitializeLocalizedComboItems()
        {
            SetComboItems(playoutModeBox,
                new LocalizedItem("Sequential", T("Sequential")),
                new LocalizedItem("Random", T("Random")));
            playoutModeBox.SelectedIndex = 0;

            SetComboItems(scheduleTypeBox,
                new LocalizedItem("One-time", T("OneTime")),
                new LocalizedItem("Weekly", T("Weekly")));
            scheduleTypeBox.SelectedIndex = 0;

            SetComboItems(scheduleEndActionBox,
                new LocalizedItem("Replay until end", T("ReplayUntilEnd")),
                new LocalizedItem("Wait until end", T("WaitUntilEnd")),
                new LocalizedItem("Continue immediately", T("ContinueImmediately")));
            SetComboValue(scheduleEndActionBox, "Wait until end");

            SetComboItems(scheduleStartActionBox,
                new LocalizedItem("Start immediately", T("StartImmediately")),
                new LocalizedItem("Wait current item", T("WaitCurrentItem")));
            SetComboValue(scheduleStartActionBox, "Start immediately");
        }

        private static void SetComboItems(ComboBox comboBox, params LocalizedItem[] items)
        {
            comboBox.Items.Clear();
            comboBox.Items.AddRange(items);
        }

        private static string GetComboValue(ComboBox comboBox)
        {
            LocalizedItem item = comboBox.SelectedItem as LocalizedItem;
            return item != null ? item.Value : comboBox.Text;
        }

        private static void SetComboValue(ComboBox comboBox, string value)
        {
            foreach (object obj in comboBox.Items)
            {
                LocalizedItem item = obj as LocalizedItem;
                if (item != null && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
            comboBox.Text = value;
        }

        private static bool ComboContainsValue(ComboBox comboBox, string value)
        {
            return comboBox.Items.Cast<object>().OfType<LocalizedItem>().Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)) || comboBox.Items.Contains(value);
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
                AutoDeinterlace = autoDeinterlaceBox.Checked,
                AutoDeinterlaceFilter = autoDeinterlaceFilterBox.Text,
                VideoIndex = videoIndexBox.Value,
                AudioIndex = audioIndexBox.Value,
                Shift = shiftBox.Value,
                Seek = seekBox.Value,
                ByteSeek = byteSeekBox.Checked,
                WriteNowPlayingXml = writeNowPlayingXmlBox.Checked,
                PlayoutMode = GetComboValue(playoutModeBox),
                AutoAdvance = autoAdvanceBox.Checked,
                ScheduledStartEnabled = scheduledStartBox.Checked,
                ScheduledStart = scheduledStartPicker.Value,
                ScheduleType = GetComboValue(scheduleTypeBox),
                ScheduleDateTime = scheduleDateTimePicker.Value,
                ScheduleTime = scheduleTimePicker.Value,
                ScheduleEndEnabled = scheduleEndBox.Checked,
                ScheduleEnd = scheduleEndPicker.Value,
                ScheduledItemEnabled = scheduledEnabledBox.Checked,
                ScheduledTitle = scheduledTitleBox.Text,
                ScheduledOptions = scheduledOptionsBox.Text,
                ObsEnabled = ObsOperationsEnabled,
                ObsUrl = mainObsUrlBox.Text,
                ObsPassword = mainObsPasswordBox.Text,
                ObsScene = obsSceneBox.Text,
                ObsShowSources = obsShowSourcesBox.Text,
                ObsHideSources = obsHideSourcesBox.Text,
                ObsEndScene = obsEndSceneBox.Text,
                ObsEndShowSources = obsEndShowSourcesBox.Text,
                ObsEndHideSources = obsEndHideSourcesBox.Text,
                ScheduleEndAction = GetComboValue(scheduleEndActionBox),
                ScheduleStartAction = GetComboValue(scheduleStartActionBox),
                WeekDays = GetSelectedWeekDays(),
                CurrentStatusState = currentStatusState,
                CurrentStatusInput = currentStatusInput,
                CurrentActualStatusInput = currentActualStatusInput,
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
                ClearQueuedPlaybackState(true);
                ClearInteractiveRuntime();
                commandClient.Reset();
                activeState = connectionTabs.SelectedTab.Tag as ConnectionTabState;
                if (activeState == null) return;
                LoadConnectionStateToControls(activeState);
                interactiveForm?.ReloadAll();
                RequestInteractiveGroupDefaultIfIdle();
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
            activeState.AutoDeinterlace = autoDeinterlaceBox.Checked;
            activeState.AutoDeinterlaceFilter = autoDeinterlaceFilterBox.Text;
            activeState.VideoIndex = videoIndexBox.Value;
            activeState.AudioIndex = audioIndexBox.Value;
            activeState.Shift = shiftBox.Value;
            activeState.Seek = seekBox.Value;
            activeState.ByteSeek = byteSeekBox.Checked;
            activeState.WriteNowPlayingXml = writeNowPlayingXmlBox.Checked;
            activeState.PlayoutMode = GetComboValue(playoutModeBox);
            activeState.AutoAdvance = autoAdvanceBox.Checked;
            activeState.ScheduledStartEnabled = scheduledStartBox.Checked;
            activeState.ScheduledStart = scheduledStartPicker.Value;
            activeState.ScheduleType = GetComboValue(scheduleTypeBox);
            activeState.ScheduleDateTime = scheduleDateTimePicker.Value;
            activeState.ScheduleTime = scheduleTimePicker.Value;
            activeState.ScheduleEndEnabled = scheduleEndBox.Checked;
            activeState.ScheduleEnd = scheduleEndPicker.Value;
            activeState.ScheduledItemEnabled = scheduledEnabledBox.Checked;
            activeState.ScheduledTitle = scheduledTitleBox.Text;
            activeState.ScheduledOptions = scheduledOptionsBox.Text;
            activeState.ObsEnabled = ObsOperationsEnabled;
            activeState.ObsUrl = mainObsUrlBox.Text;
            activeState.ObsPassword = mainObsPasswordBox.Text;
            activeState.ObsScene = obsSceneBox.Text;
            activeState.ObsShowSources = obsShowSourcesBox.Text;
            activeState.ObsHideSources = obsHideSourcesBox.Text;
            activeState.ObsEndScene = obsEndSceneBox.Text;
            activeState.ObsEndShowSources = obsEndShowSourcesBox.Text;
            activeState.ObsEndHideSources = obsEndHideSourcesBox.Text;
            activeState.ScheduleEndAction = GetComboValue(scheduleEndActionBox);
            activeState.ScheduleStartAction = GetComboValue(scheduleStartActionBox);
            activeState.WeekDays = GetSelectedWeekDays();
            activeState.Playlist = playlist;
            activeState.ScheduledPlaylist = scheduledPlaylist;
            activeState.FavoriteInputs = favoriteInputs;
            activeState.InteractiveMedia = interactiveMedia;
            activeState.InteractiveRoutes = interactiveRoutes;
            activeState.InteractiveProjectGroups = interactiveProjectGroups;
            activeState.ActiveInteractiveProjectGroupId = activeInteractiveProjectGroupId;
            activeState.InteractiveGroupTransitions = interactiveGroupTransitions;
            activeState.InteractiveGroupSwitchRandom = interactiveGroupSwitchRandom;
            activeState.InteractiveGroupSwitchHotkey = interactiveGroupSwitchHotkey;
            activeState.CurrentIndex = currentIndex;
            activeState.CurrentScheduledIndex = currentScheduledIndex;
            activeState.CurrentScheduledItem = CloneScheduledItem(currentScheduledItem);
            activeState.PlayoutRunning = playoutRunning;
            activeState.PlayingScheduled = playingScheduled;
            activeState.ScheduledObsEndApplied = scheduledObsEndApplied;
            activeState.WaitingForScheduledStart = waitingForScheduledStart;
            activeState.ScheduledSlotEndAt = scheduledSlotEndAt;
            activeState.CurrentStartedAt = currentStartedAt;
            activeState.CurrentPositionSeconds = currentPositionSeconds;
            activeState.CurrentDurationSeconds = currentDurationSeconds;
            activeState.CurrentSizeBytes = currentSizeBytes;
            activeState.CurrentStatusState = currentStatusState;
            activeState.CurrentStatusInput = currentStatusInput;
            activeState.CurrentActualStatusInput = currentActualStatusInput;
            activeState.AdvancingPlayout = advancingPlayout;
            activeState.LastAutoAdvanceAt = lastAutoAdvanceAt;
            activeState.PendingSeekSeconds = pendingSeekSeconds;
            activeState.PendingSeekUntil = pendingSeekUntil;
            activeState.ProgressValue = GetProgressValue();
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
            interactiveMedia = state.InteractiveMedia;
            interactiveRoutes = state.InteractiveRoutes;
            BindInteractiveProjectGroups(state);
            currentIndex = state.CurrentIndex;
            currentScheduledIndex = state.CurrentScheduledIndex;
            currentScheduledItem = CloneScheduledItem(state.CurrentScheduledItem);
            playoutRunning = state.PlayoutRunning;
            playingScheduled = state.PlayingScheduled;
            scheduledObsEndApplied = state.ScheduledObsEndApplied;
            waitingForScheduledStart = state.WaitingForScheduledStart;
            scheduledSlotEndAt = state.ScheduledSlotEndAt;
            currentStartedAt = state.CurrentStartedAt;
            currentPositionSeconds = state.CurrentPositionSeconds;
            currentDurationSeconds = state.CurrentDurationSeconds;
            currentSizeBytes = state.CurrentSizeBytes;
            currentStatusState = state.CurrentStatusState;
            currentStatusInput = state.CurrentStatusInput;
            currentActualStatusInput = state.CurrentActualStatusInput;
            advancingPlayout = state.AdvancingPlayout;
            lastAutoAdvanceAt = state.LastAutoAdvanceAt;
            pendingSeekSeconds = state.PendingSeekSeconds;
            pendingSeekUntil = state.PendingSeekUntil;

            hostBox.Text = state.Host;
            portBox.Value = Clamp(state.Port, portBox.Minimum, portBox.Maximum);
            SetInputFields(state.Input, state.Title, state.Options);
            hwDecodeBox.Text = state.HwDecode;
            if (scaleModeBox.Items.Contains(state.ScaleMode)) scaleModeBox.Text = state.ScaleMode;
            displayAspectBox.Text = state.DisplayAspect;
            videoFilterBox.Text = state.VideoFilter;
            audioFilterBox.Text = state.AudioFilter;
            autoDeinterlaceBox.Checked = state.AutoDeinterlace;
            if (autoDeinterlaceFilterBox.Items.Contains(state.AutoDeinterlaceFilter)) autoDeinterlaceFilterBox.Text = state.AutoDeinterlaceFilter;
            videoIndexBox.Value = Clamp(state.VideoIndex, videoIndexBox.Minimum, videoIndexBox.Maximum);
            audioIndexBox.Value = Clamp(state.AudioIndex, audioIndexBox.Minimum, audioIndexBox.Maximum);
            shiftBox.Value = Clamp(state.Shift, shiftBox.Minimum, shiftBox.Maximum);
            seekBox.Value = Clamp(state.Seek, seekBox.Minimum, seekBox.Maximum);
            byteSeekBox.Checked = state.ByteSeek;
            writeNowPlayingXmlBox.Checked = state.WriteNowPlayingXml;
            if (ComboContainsValue(playoutModeBox, state.PlayoutMode)) SetComboValue(playoutModeBox, state.PlayoutMode);
            autoAdvanceBox.Checked = state.AutoAdvance;
            scheduledStartBox.Checked = state.ScheduledStartEnabled;
            scheduledStartPicker.Value = ClampDateTimePicker(scheduledStartPicker, state.ScheduledStart);
            if (ComboContainsValue(scheduleTypeBox, state.ScheduleType)) SetComboValue(scheduleTypeBox, state.ScheduleType);
            scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, state.ScheduleDateTime);
            scheduleTimePicker.Value = ClampDateTimePicker(scheduleTimePicker, state.ScheduleTime);
            scheduleEndBox.Checked = state.ScheduleEndEnabled;
            scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, state.ScheduleEnd);
            scheduledEnabledBox.Checked = state.ScheduledItemEnabled;
            SetScheduledInputFields(state.ScheduledTitle, state.ScheduledOptions);
            obsOperationsEnabledBox.Checked = state.ObsEnabled;
            mainObsUrlBox.Text = state.ObsUrl;
            mainObsPasswordBox.Text = state.ObsPassword;
            obsSceneBox.Text = state.ObsScene;
            obsShowSourcesBox.Text = state.ObsShowSources;
            obsHideSourcesBox.Text = state.ObsHideSources;
            obsEndSceneBox.Text = state.ObsEndScene;
            obsEndShowSourcesBox.Text = state.ObsEndShowSources;
            obsEndHideSourcesBox.Text = state.ObsEndHideSources;
            if (ComboContainsValue(scheduleEndActionBox, state.ScheduleEndAction)) SetComboValue(scheduleEndActionBox, state.ScheduleEndAction);
            if (ComboContainsValue(scheduleStartActionBox, state.ScheduleStartAction)) SetComboValue(scheduleStartActionBox, state.ScheduleStartAction);
            SetSelectedWeekDays(state.WeekDays);
            logBox.Text = state.LogText;
            positionLabel.Text = FormatSeconds(currentPositionSeconds) + " / " + (currentDurationSeconds > 0 ? FormatSeconds(currentDurationSeconds) : "--:--:--");
            SetProgressValue(state.ProgressValue);
            controlsReady = true;

            RefreshPlaylistView();
            RefreshScheduledPlaylistView();
            RefreshFavoriteInputView();
            RestoreSelectedIndices(playlistView, state.SelectedPlaylistIndices);
            RestoreSelectedIndices(scheduledPlaylistView, state.SelectedScheduledPlaylistIndices);
            RestoreSelectedIndices(favoriteInputView, state.SelectedFavoriteInputIndices);
            SetInputFields(state.Input, state.Title, state.Options);
            SetScheduledInputFields(state.ScheduledTitle, state.ScheduledOptions);
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
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            parent.Controls.Add(root);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.Controls.Add(grid, 0, 0);

            languageBox = CreateLanguageBox();
            AddWideRow(grid, T("Language"), languageBox);

            AddRow(grid, T("Host"), hostBox, T("Port"), portBox);
            hostBox.Leave += (_, __) =>
            {
                SaveActiveConnectionState();
            };
            portBox.ValueChanged += (_, __) =>
            {
                if (controlsReady) SaveActiveConnectionState();
            };

            AddWideRow(grid, T("Input"), CreateInputPicker());
            AddWideRow(grid, T("Title"), titleBox);
            AddWideRow(grid, T("Options"), optionsBox);
            AddWideRow(grid, T("FrameQueue"), CreateFrameQueueControl());
            AddWideRow(grid, T("DirectShow"), CreateDShowControl());
            AddRow(grid, T("HWDecode"), CreateHwDecodeControl(), T("ScaleMode"), CreateScaleModeControl());
            AddRow(grid, T("DisplayAR"), CreateDisplayAspectControl(), T("ShiftUs"), CreateShiftControl());
            AddRow(grid, T("VideoFilter"), CreateVideoFilterControl(), T("AudioFilter"), CreateAudioFilterControl());
            AddRow(grid, T("AutoDeinterlace"), CreateAutoDeinterlaceControl(), T("AutoDeinterlaceFilter"), autoDeinterlaceFilterBox);
            AddRow(grid, T("VideoIndex"), videoIndexBox, T("AudioIndex"), audioIndexBox);
            AddRow(grid, T("Position"), CreatePositionControl(), T("SeekSeconds"), seekBox);
            AddProgressRow(grid);
            progressBar.MouseDown += ProgressBar_MouseDown;
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
            autoDeinterlaceBox.CheckedChanged += async (_, __) =>
            {
                if (controlsReady) await SendAutoDeinterlaceAsync();
            };
            autoDeinterlaceFilterBox.SelectedIndexChanged += async (_, __) =>
            {
                if (controlsReady) await SendAutoDeinterlaceAsync();
            };
            seekBox.ValueChanged += async (_, __) =>
            {
                if (controlsReady && !suppressSeekValueEvent) await SeekBySecondsAsync((long)seekBox.Value);
            };
            inputBox.TextChanged += (_, __) =>
            {
                UpdateTitleFromManualInput();
                ApplyInputQueuePolicy(true);
            };
            optionsBox.Leave += (_, __) => ApplyInputQueuePolicy(false);
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

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top | DockStyle.Left, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            buttons.Controls.Add(MakeButton(T("Ping"), async (_, __) => await SendCommandAsync("PING")));
            buttons.Controls.Add(MakeButton(T("ButtonOpen"), async (_, __) => await PlayManualAsync()));
            manualPlayButton = MakeButton(T("ManualPlay"), async (_, __) => await ToggleManualPlayPauseAsync());
            buttons.Controls.Add(manualPlayButton);
            buttons.Controls.Add(MakeButton(T("Stop"), async (_, __) => await StopAllAsync()));
            buttons.Controls.Add(MakeButton(T("Reopen"), async (_, __) => await SendCommandAsync("REOPEN")));
            buttons.Controls.Add(MakeButton(T("SetVideoIndex"), async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton(T("SetAudioIndex"), async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton(T("FrameInfo"), async (_, __) => await ShowFrameInfoAsync()));
            buttons.Controls.Add(MakeButton(T("CameraDiagnostics"), (_, __) => OpenCameraCalibration()));
            buttons.Controls.Add(MakeButton(T("SaveXml"), (_, __) => SaveAutoConfig()));
            buttons.Controls.Add(MakeButton(T("OpenPlayout"), (_, __) => OpenPlayoutWindow()));
            buttons.Controls.Add(MakeButton(T("InteractivePlayback"), (_, __) => OpenInteractiveWindow()));
            root.Controls.Add(buttons, 0, 1);

            var obsOperations = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 0, 6) };
            var obsOperationsConnectionRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
            obsOperationsConnectionRow.Controls.Add(new Label { Text = T("ObsOperations"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 6, 0) });
            obsOperationsEnabledBox.Text = T("ObsEnabled");
            obsOperationsEnabledBox.CheckedChanged += ObsOperationsEnabledChanged;
            obsOperationsConnectionRow.Controls.Add(obsOperationsEnabledBox);
            obsOperationsConnectionRow.Controls.Add(MakeInlineLabel(T("ObsUrl")));
            obsOperationsConnectionRow.Controls.Add(mainObsUrlBox);
            obsOperationsConnectionRow.Controls.Add(MakeInlineLabel(T("ObsPassword")));
            obsOperationsConnectionRow.Controls.Add(mainObsPasswordBox);
            obsOperationsConnectionRow.Controls.Add(MakeInlineLabel(T("ObsScene")));
            obsOperationsConnectionRow.Controls.Add(mainObsSceneBox);
            obsOperationsConnectionRow.Controls.Add(MakeButton(T("ObsSwitchScene"), async (_, __) => await SwitchObsSceneAsync(true)));
            obsOperationsConnectionRow.Controls.Add(MakeButton(T("ObsRefresh"), async (_, __) => await RefreshObsCatalogAsync(true)));
            obsOperationsConnectionRow.Controls.Add(MakeButton(T("ObsTest"), async (_, __) => await TestObsAsync()));
            var obsOperationsSourceRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, Padding = new Padding(70, 0, 0, 0) };
            obsOperationsSourceRow.Controls.Add(MakeInlineLabel(T("ObsShowSources")));
            obsOperationsSourceRow.Controls.Add(mainObsShowSourcesBox);
            obsOperationsSourceRow.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsSourcesAsync(true, true)));
            obsOperationsSourceRow.Controls.Add(MakeInlineLabel(T("ObsHideSources")));
            obsOperationsSourceRow.Controls.Add(mainObsHideSourcesBox);
            obsOperationsSourceRow.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsSourcesAsync(true, false)));
            obsOperationsSourceRow.Controls.Add(MakeButton(T("ObsApply"), async (_, __) => await ApplyObsManualActionsAsync()));
            obsOperations.Controls.Add(obsOperationsConnectionRow, 0, 0);
            obsOperations.Controls.Add(obsOperationsSourceRow, 0, 1);
            root.Controls.Add(obsOperations, 0, 2);
            root.Controls.Add(CreateLogAndFavoriteInputsPanel(), 0, 3);
        }

        private async Task TestObsAsync()
        {
            await RefreshObsCatalogAsync(true);
        }

        private async Task RefreshObsCatalogAsync(bool mainPanel)
        {
            if (!ObsOperationsEnabled) return;
            ComboBox sceneBox = mainPanel ? mainObsSceneBox : obsSceneBox;
            TextBox urlBox = mainObsUrlBox;
            TextBox passwordBox = mainObsPasswordBox;
            string selectedScene = sceneBox.Text.Trim();
            try
            {
                using (var client = new ObsWebSocketClient(urlBox.Text.Trim(), passwordBox.Text))
                {
                    await client.ConnectAsync();
                    if (!ObsOperationsEnabled) return;
                    string currentScene = await client.GetCurrentProgramSceneAsync();
                    if (!ObsOperationsEnabled) return;
                    List<string> scenes = await client.GetSceneListAsync();
                    if (!ObsOperationsEnabled) return;
                    UpdateSharedObsSceneChoices(scenes, currentScene);
                    if (scenes.Contains(selectedScene)) sceneBox.Text = selectedScene;
                    else if (scenes.Contains(currentScene)) sceneBox.Text = currentScene;
                    else if (scenes.Count > 0) sceneBox.SelectedIndex = 0;
                    AppendLog(TF("ObsScenesLoaded", scenes.Count, currentScene));
                }
            }
            catch (Exception ex)
            {
                AppendLog(TF("ObsActionFailed", ex.Message));
            }
        }

        private async Task SelectObsSourcesAsync(bool mainPanel, bool showSources)
        {
            if (!ObsOperationsEnabled) return;
            ComboBox sceneBox = mainPanel ? mainObsSceneBox : obsSceneBox;
            TextBox targetBox = mainPanel
                ? (showSources ? mainObsShowSourcesBox : mainObsHideSourcesBox)
                : (showSources ? obsShowSourcesBox : obsHideSourcesBox);
            TextBox urlBox = mainObsUrlBox;
            TextBox passwordBox = mainObsPasswordBox;
            string sceneName = sceneBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                await RefreshObsCatalogAsync(mainPanel);
                sceneName = sceneBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(sceneName)) return;
            }

            if (!ObsOperationsEnabled) return;
            try
            {
                List<string> sources;
                using (var client = new ObsWebSocketClient(urlBox.Text.Trim(), passwordBox.Text))
                {
                    await client.ConnectAsync();
                    if (!ObsOperationsEnabled) return;
                    sources = await client.GetSceneItemListAsync(sceneName);
                }
                ShowObsSourcePicker(targetBox, sceneName, sources, showSources);
            }
            catch (Exception ex)
            {
                AppendLog(TF("ObsActionFailed", ex.Message));
            }
        }

        private async Task SelectObsEndSourcesAsync(bool showSources)
        {
            if (!ObsOperationsEnabled) return;
            string sceneName = obsEndSceneBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                await RefreshObsCatalogAsync(false);
                sceneName = obsEndSceneBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(sceneName)) return;
            }
            if (!ObsOperationsEnabled) return;
            try
            {
                List<string> sources;
                using (var client = new ObsWebSocketClient(mainObsUrlBox.Text.Trim(), mainObsPasswordBox.Text))
                {
                    await client.ConnectAsync();
                    if (!ObsOperationsEnabled) return;
                    sources = await client.GetSceneItemListAsync(sceneName);
                }
                ShowObsSourcePicker(showSources ? obsEndShowSourcesBox : obsEndHideSourcesBox, sceneName, sources, showSources);
            }
            catch (Exception ex)
            {
                AppendLog(TF("ObsActionFailed", ex.Message));
            }
        }

        private void ShowObsSourcePicker(TextBox targetBox, string sceneName, IList<string> sources, bool showSources)
        {
            var selected = new HashSet<string>(SplitObsSources(targetBox.Text), StringComparer.Ordinal);
            using (var dialog = new Form
            {
                Text = TF("ObsSourcesTitle", sceneName),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 430)
            })
            {
                var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
                foreach (string source in sources) list.Items.Add(source, selected.Contains(source));
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 42,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(6)
                };
                var cancel = MakeButton(T("Cancel"), (_, __) => dialog.DialogResult = DialogResult.Cancel);
                var apply = MakeButton(T("Apply"), (_, __) => dialog.DialogResult = DialogResult.OK);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(apply);
                dialog.Controls.Add(list);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = apply;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    targetBox.Text = FormatObsSources(list.CheckedItems.Cast<object>().Select(value => Convert.ToString(value)));
            }
        }

        private Task ApplyObsManualActionsAsync()
        {
            if (!ObsOperationsEnabled) return Task.CompletedTask;
            var item = new ScheduledPlaylistItem
            {
                ObsScene = mainObsSceneBox.Text.Trim(),
                ObsShowSources = mainObsShowSourcesBox.Text.Trim(),
                ObsHideSources = mainObsHideSourcesBox.Text.Trim()
            };
            return ApplyObsActionsAsync(item, mainObsUrlBox.Text.Trim(), mainObsPasswordBox.Text);
        }

        private Control CreatePositionControl()
		{
			var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
			panel.Controls.Add(positionLabel);
			panel.Controls.Add(deliverLabel);
			return panel;
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
            toolbar.Controls.Add(new Label { Text = T("FavoriteInputs"), AutoSize = true, Padding = new Padding(0, 5, 6, 0) });
            toolbar.Controls.Add(MakeButton(T("AddCurrent"), (_, __) => AddCurrentFavoriteInput()));
            toolbar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedFavoriteInput()));
            toolbar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSelectedFavoriteInput(-1)));
            toolbar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSelectedFavoriteInput(1)));
            root.Controls.Add(toolbar, 0, 0);

            favoriteInputView.Columns.Add(T("ColumnTitle"), 140);
            favoriteInputView.Columns.Add(T("ColumnOptions"), 140);
            favoriteInputView.Columns.Add(T("ColumnInput"), 320);
            favoriteInputView.SelectedIndexChanged += (_, __) => LoadSelectedFavoriteInputToInputs();
            favoriteInputView.DoubleClick += async (_, __) => await PlaySelectedFavoriteInputAsync();
            root.Controls.Add(favoriteInputView, 0, 1);

            return root;
        }

        private void BuildPlayoutWindow()
        {
            playoutForm = new Form
            {
                Text = T("Playout"),
                Size = new Size(1120, 720),
                MinimumSize = new Size(980, 600),
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                ShowIcon = false
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
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            parent.Controls.Add(root);

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            header.Controls.Add(new Label { Text = T("Playout"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(2, 5, 12, 0) });
            header.Controls.Add(playoutStatusLabel);
            header.Controls.Add(MakeButton(T("StartPlayout"), async (_, __) => await StartPlayoutAsync()));
            header.Controls.Add(MakeButton(T("Next"), async (_, __) => await PlayNextAsync(true)));
            header.Controls.Add(MakeButton(T("StopPlayout"), async (_, __) => await StopPlayoutAsync()));
            header.Controls.Add(MakeButton(T("SaveXml"), (_, __) => SaveAutoConfig()));
            header.Controls.Add(writeNowPlayingXmlBox);
            root.Controls.Add(header, 0, 0);

            var obsSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            obsSettings.Controls.Add(new Label { Text = T("ObsOperations"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 6, 0) });
            obsSettings.Controls.Add(MakeInlineLabel(T("ObsScene")));
            obsSettings.Controls.Add(playoutObsSceneBox);
            obsSettings.Controls.Add(MakeButton(T("ObsRefresh"), async (_, __) => await RefreshObsCatalogAsync(false)));
            obsSettings.Controls.Add(MakeButton(T("ObsSwitchScene"), async (_, __) => await SwitchObsSceneAsync(false)));
            root.Controls.Add(obsSettings, 0, 1);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 60
            };
            root.Controls.Add(split, 0, 2);

            split.Panel1.Controls.Add(CreateScheduledPlaylistPanel());
            split.Panel2.Controls.Add(CreateNormalPlaylistPanel());
        }

        private Control CreateScheduledPlaylistPanel()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 3) };
            actions.Controls.Add(new Label { Text = T("ScheduledPlaylist"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 8, 0) });
            actions.Controls.Add(MakeButton(T("AddCurrent"), async (_, __) => await AddScheduledFromCurrentAsync()));
            actions.Controls.Add(MakeButton(T("AddFiles"), async (_, __) => await AddScheduledFilesAsync()));
            actions.Controls.Add(MakeButton(T("AddFolder"), async (_, __) => await AddScheduledFolderAsync()));
            actions.Controls.Add(MakeButton(T("Apply"), (_, __) => ApplyScheduledControlsToSelected()));
            actions.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedScheduled()));
            actions.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSelectedScheduled(-1)));
            actions.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSelectedScheduled(1)));
            actions.Controls.Add(MakeButton(T("RefreshDurations"), async (_, __) => await RefreshScheduledDurationsAsync()));
            root.Controls.Add(actions, 0, 0);

            var startSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 3) };
            startSettings.Controls.Add(scheduledEnabledBox);
            startSettings.Controls.Add(MakeInlineLabel(T("ScheduleType")));
            startSettings.Controls.Add(scheduleTypeBox);
            startSettings.Controls.Add(MakeInlineLabel(T("OneTimeStart")));
            startSettings.Controls.Add(scheduleDateTimePicker);
            startSettings.Controls.Add(MakeInlineLabel(T("WeeklyTime")));
            startSettings.Controls.Add(scheduleTimePicker);
            startSettings.Controls.Add(MakeInlineLabel(T("Days")));
            startSettings.Controls.Add(mondayBox);
            startSettings.Controls.Add(tuesdayBox);
            startSettings.Controls.Add(wednesdayBox);
            startSettings.Controls.Add(thursdayBox);
            startSettings.Controls.Add(fridayBox);
            startSettings.Controls.Add(saturdayBox);
            startSettings.Controls.Add(sundayBox);
            startSettings.Controls.Add(MakeInlineLabel(T("InputTitle")));
            startSettings.Controls.Add(scheduledTitleBox);
            startSettings.Controls.Add(MakeInlineLabel(T("InputOptions")));
            startSettings.Controls.Add(scheduledOptionsBox);
            root.Controls.Add(startSettings, 0, 1);

            var obsStartSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 3) };
            obsStartSettings.Controls.Add(new Label { Text = T("ObsAtStart"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 5, 0) });
            obsStartSettings.Controls.Add(MakeInlineLabel(T("ObsScene")));
            obsStartSettings.Controls.Add(obsSceneBox);
            obsStartSettings.Controls.Add(MakeButton(T("ObsRefresh"), async (_, __) => await RefreshObsCatalogAsync(false)));
            obsStartSettings.Controls.Add(MakeInlineLabel(T("ObsShowSources")));
            obsStartSettings.Controls.Add(obsShowSourcesBox);
            obsStartSettings.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsSourcesAsync(false, true)));
            obsStartSettings.Controls.Add(MakeInlineLabel(T("ObsHideSources")));
            obsStartSettings.Controls.Add(obsHideSourcesBox);
            obsStartSettings.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsSourcesAsync(false, false)));
            root.Controls.Add(obsStartSettings, 0, 2);

            var obsEndSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 3) };
            obsEndSettings.Controls.Add(new Label { Text = T("ObsAtEnd"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 5, 0) });
            obsEndSettings.Controls.Add(MakeInlineLabel(T("ObsScene")));
            obsEndSettings.Controls.Add(obsEndSceneBox);
            obsEndSettings.Controls.Add(MakeButton(T("ObsRefresh"), async (_, __) => await RefreshObsCatalogAsync(false)));
            obsEndSettings.Controls.Add(MakeInlineLabel(T("ObsShowSources")));
            obsEndSettings.Controls.Add(obsEndShowSourcesBox);
            obsEndSettings.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsEndSourcesAsync(true)));
            obsEndSettings.Controls.Add(MakeInlineLabel(T("ObsHideSources")));
            obsEndSettings.Controls.Add(obsEndHideSourcesBox);
            obsEndSettings.Controls.Add(MakeButton(T("ObsSelect"), async (_, __) => await SelectObsEndSourcesAsync(false)));
            root.Controls.Add(obsEndSettings, 0, 3);

            var endSettings = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            endSettings.Controls.Add(scheduleEndBox);
            endSettings.Controls.Add(MakeInlineLabel(T("EndTime")));
            endSettings.Controls.Add(scheduleEndPicker);
            endSettings.Controls.Add(MakeInlineLabel(T("IfMediaEndsEarly")));
            endSettings.Controls.Add(scheduleEndActionBox);
            endSettings.Controls.Add(MakeInlineLabel(T("AtScheduledTime")));
            endSettings.Controls.Add(scheduleStartActionBox);
            root.Controls.Add(endSettings, 0, 4);

            scheduledPlaylistView.Columns.Add("#", 38);
            scheduledPlaylistView.Columns.Add(T("ColumnStatus"), 78);
            scheduledPlaylistView.Columns.Add(T("ColumnEnabled"), 70);
            scheduledPlaylistView.Columns.Add(T("ColumnTitle"), 180);
            scheduledPlaylistView.Columns.Add(T("ColumnDuration"), 76);
            scheduledPlaylistView.Columns.Add(T("ColumnSchedule"), 210);
            scheduledPlaylistView.Columns.Add(T("ColumnEnd"), 150);
            scheduledPlaylistView.Columns.Add(T("ColumnStart"), 120);
            scheduledPlaylistView.Columns.Add(T("ColumnEndAction"), 150);
            scheduledPlaylistView.Columns.Add(T("ColumnObsStart"), 260);
            scheduledPlaylistView.Columns.Add(T("ColumnObsEnd"), 260);
            scheduledPlaylistView.Columns.Add(T("ColumnLastTriggered"), 150);
            scheduledPlaylistView.Columns.Add(T("ColumnPath"), 360);
            scheduledPlaylistView.SelectedIndexChanged += (_, __) => LoadSelectedScheduledItemToInputs();
            scheduledPlaylistView.DoubleClick += async (_, __) => await PlaySelectedScheduledAsync();
            root.Controls.Add(scheduledPlaylistView, 0, 5);

            return root;
        }

        private static Label MakeInlineLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Padding = new Padding(8, 5, 2, 0) };
        }

        private ComboBox CreateLanguageBox()
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 150,
                Margin = new Padding(0, 0, 6, 6)
            };
            combo.Items.AddRange(new object[]
            {
                new LocalizedItem(string.Empty, T("LanguageAuto")),
                new LocalizedItem("en", T("LanguageEnglish")),
                new LocalizedItem("zh-CN", T("LanguageSimplifiedChinese")),
                new LocalizedItem("zh-TW", T("LanguageTraditionalChinese"))
            });
            SetComboValue(combo, ControllerLanguage.LoadSavedCultureName());
            combo.SelectedIndexChanged += (_, __) =>
            {
                if (!controlsReady) return;
                SaveAutoConfig();
                MessageBox.Show(this, T("LanguageRestartRequired"), T("Language"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            return combo;
        }

        private Control CreateNormalPlaylistPanel()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            toolbar.Controls.Add(new Label { Text = T("NormalPlaylist"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 8, 0) });
            toolbar.Controls.Add(MakeButton(T("AddCurrent"), async (_, __) => await AddCurrentToPlaylistAsync(false)));
            toolbar.Controls.Add(MakeButton(T("AddFiles"), async (_, __) => await AddFilesAsync()));
            toolbar.Controls.Add(MakeButton(T("AddFolder"), async (_, __) => await AddFolderAsync()));
            toolbar.Controls.Add(MakeButton(T("AddBumper"), async (_, __) => await AddBumperAsync()));
            toolbar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelected()));
            toolbar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSelected(-1)));
            toolbar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSelected(1)));
            toolbar.Controls.Add(MakeButton(T("SetTitle"), (_, __) => SetSelectedTitle()));
            toolbar.Controls.Add(MakeButton(T("SetOptions"), (_, __) => SetSelectedOptions()));
            toolbar.Controls.Add(MakeButton(T("PlayNext"), (_, __) => SetSelectedAsNextPlayback()));
            toolbar.Controls.Add(MakeButton(T("Load"), async (_, __) => await LoadPlaylistAsync()));
            toolbar.Controls.Add(MakeButton(T("SaveFile"), (_, __) => SavePlaylist()));
            toolbar.Controls.Add(MakeButton(T("RefreshDurations"), async (_, __) => await RefreshDurationsAsync()));
            toolbar.Controls.Add(new Label { Text = T("Mode"), AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            toolbar.Controls.Add(playoutModeBox);
            toolbar.Controls.Add(autoAdvanceBox);
            toolbar.Controls.Add(scheduledStartBox);
            toolbar.Controls.Add(scheduledStartPicker);
            root.Controls.Add(toolbar, 0, 0);

            playlistView.Columns.Add("#", 40);
            playlistView.Columns.Add(T("ColumnStatus"), 80);
            playlistView.Columns.Add(T("ColumnType"), 70);
            playlistView.Columns.Add(T("ColumnTitle"), 220);
            playlistView.Columns.Add(T("ColumnDuration"), 80);
            playlistView.Columns.Add(T("ColumnOptions"), 220);
            playlistView.Columns.Add(T("ColumnPath"), 520);
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
            grid.SetColumnSpan(control, grid.ColumnCount - 1);
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
            panel.Controls.Add(new Label { Text = T("SeekMode"), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0), Margin = Padding.Empty }, 1, 0);
            panel.Controls.Add(byteSeekBox, 2, 0);

            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = T("Progress"), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(6, 4, 0, 0), Margin = Padding.Empty }, 0, row);
            grid.Controls.Add(panel, 1, row);
            grid.SetColumnSpan(panel, grid.ColumnCount - 1);
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
                ColumnCount = 4,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                AutoSize = true
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            shiftBox.Dock = DockStyle.Fill;
            var minusButton = MakeStepButton("-", () => AdjustShift(-shiftBox.Increment));
            var plusButton = MakeStepButton("+", () => AdjustShift(shiftBox.Increment));

            panel.Controls.Add(shiftBox, 0, 0);
            panel.Controls.Add(minusButton, 1, 0);
            panel.Controls.Add(plusButton, 2, 0);
            panel.Controls.Add(new Label { Text = T("ShiftUsHint"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0) }, 3, 0);

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
            var setButton = MakeButton(T("Set"), async (_, __) => await SendHwDecodeAsync());
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

        private Control CreateAutoDeinterlaceControl()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            panel.Controls.Add(autoDeinterlaceBox);
            return panel;
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
            var setButton = MakeButton(T("Set"), async (_, __) => await setAction());
            var cancelButton = MakeButton(T("Cancel"), async (_, __) => await cancelAction());
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
            quickBox.Items.AddRange(new object[] { "<TESTCARD>", "<TESTCARD2>", "<TESTCARDCAL>", "<OBSVCAM>", "<DSHOW>" });
            quickBox.SelectedIndexChanged += (_, __) =>
            {
                if (quickBox.SelectedItem != null) inputBox.Text = quickBox.SelectedItem.ToString();
            };
            var button = MakeButton(T("Browse"), (_, __) => BrowseInputFile());
            button.Dock = DockStyle.None;

            panel.Controls.Add(inputBox, 0, 0);
            panel.Controls.Add(quickBox, 1, 0);
            panel.Controls.Add(button, 2, 0);

            return panel;
        }

        private Control CreateDShowControl()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
            dshowConfigureButton = MakeButton(T("CameraButton"), async (_, __) => await ConfigureDShowAsync());
            dshowConfigureButton.AutoSize = true;
            dshowDeviceTimestampBox = new CheckBox
            {
                Text = T("UseVideoDeviceTimestamp"),
                AutoSize = true,
                Padding = new Padding(8, 4, 0, 0)
            };
            dshowAudioDeviceTimestampBox = new CheckBox
            {
                Text = T("UseAudioDeviceTimestamp"),
                AutoSize = true,
                Padding = new Padding(8, 4, 0, 0)
            };
            dshowAudioBufferSizeBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 60000,
                Value = 50,
                Width = 72
            };
            dshowDeviceTimestampBox.CheckedChanged += (_, __) => UpdateDShowCaptureOptions();
            dshowAudioDeviceTimestampBox.CheckedChanged += (_, __) => UpdateDShowCaptureOptions();
            dshowAudioBufferSizeBox.ValueChanged += (_, __) => UpdateDShowCaptureOptions();
            panel.Controls.Add(dshowConfigureButton);
            panel.Controls.Add(dshowDeviceTimestampBox);
            panel.Controls.Add(dshowAudioDeviceTimestampBox);
            panel.Controls.Add(MakeInlineLabel(T("AudioBufferSizeMs")));
            panel.Controls.Add(dshowAudioBufferSizeBox);
            return panel;
        }

        private Control CreateFrameQueueControl()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
            queueLeftBox = CreateQueueNumber();
            queueRightBox = CreateQueueNumber();
            queueCenterBox = CreateQueueNumber();
            queueLeftBox.ValueChanged += (_, __) => UpdateQueueOptionsFromControls();
            queueRightBox.ValueChanged += (_, __) => UpdateQueueOptionsFromControls();
            queueCenterBox.ValueChanged += (_, __) => UpdateQueueOptionsFromControls();
            panel.Controls.Add(MakeInlineLabel(T("QueueLeft")));
            panel.Controls.Add(queueLeftBox);
            panel.Controls.Add(MakeInlineLabel(T("QueueRight")));
            panel.Controls.Add(queueRightBox);
            panel.Controls.Add(MakeInlineLabel(T("QueueCenter")));
            panel.Controls.Add(queueCenterBox);
            return panel;
        }

        private static NumericUpDown CreateQueueNumber()
        {
            return new NumericUpDown
            {
                Minimum = -1,
                Maximum = 1000000,
                Value = -1,
                Width = 72
            };
        }

        private async Task ConfigureDShowAsync()
        {
            string reply = await SendRawCommandAsync("DSHOW_DEVICES");
            List<DShowDeviceItem> allDevices = ParseDShowPayload(reply).Select(record =>
            {
                string[] parts = record.Split('|');
                return parts.Length == 3 && int.TryParse(parts[0], out int type)
                    ? new DShowDeviceItem(type, DecodeBase64Utf8(parts[1]), DecodeBase64Utf8(parts[2])) : null;
            }).Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name)).ToList();
            List<DShowDeviceItem> devices = allDevices.Where(item => item.Type == 0).ToList();
            if (devices.Count == 0 && allDevices.All(item => item.Type != 1))
            {
                MessageBox.Show(this, reply ?? T("NoDirectShowDevices"), T("DirectShow"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new Form
            {
                Text = T("DirectShowCamera"),
                Size = new Size(800, 650),
                MinimumSize = new Size(680, 480),
                StartPosition = FormStartPosition.CenterParent,
                ShowIcon = false
            })
            {
                var existingOptions = ParseOptionMap(optionsBox.Text);
                string savedVideoDevice = GetBase64Option(existingOptions, "dshow_device_b64");
                string savedVideoPin = GetBase64Option(existingOptions, "dshow_pin_b64");
                int savedVideoFormat = GetIntegerOption(existingOptions, "dshow_format", -1);
                string savedAudioDevice = GetBase64Option(existingOptions, "dshow_audio_device_b64");
                string savedAudioPin = GetBase64Option(existingOptions, "dshow_audio_pin_b64");
                int savedAudioType = GetIntegerOption(existingOptions, "dshow_audio_device_type", 1);
                int savedAudioFormat = GetIntegerOption(existingOptions, "dshow_audio_format", -1);
                int savedVideoInput = GetIntegerOption(existingOptions, "dshow_xbar_video_in", -1);
                int savedVideoOutput = GetIntegerOption(existingOptions, "dshow_xbar_video_out", -1);
                int savedAudioInput = GetIntegerOption(existingOptions, "dshow_xbar_audio_in", -1);
                int savedAudioOutput = GetIntegerOption(existingOptions, "dshow_xbar_audio_out", -1);
                bool restoringExistingOptions = true;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 14, Padding = new Padding(10) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var deviceBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                deviceBox.Items.Add(new DShowDeviceItem(-1, T("NoVideo"), string.Empty));
                deviceBox.Items.AddRange(devices.Cast<object>().ToArray());
                deviceBox.SelectedItem = deviceBox.Items.Cast<object>().OfType<DShowDeviceItem>().FirstOrDefault(item =>
                    item.Type == 0 && string.Equals(item.DisplayName, savedVideoDevice, StringComparison.Ordinal)) ??
                    deviceBox.Items[devices.Count > 0 ? 1 : 0];
                var formatBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                var audioSourceBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                audioSourceBox.Items.Add(new DShowDeviceItem(-1, T("NoAudio"), string.Empty));
                audioSourceBox.Items.AddRange(devices.Cast<object>().ToArray());
                audioSourceBox.Items.AddRange(allDevices.Where(item => item.Type == 1).Cast<object>().ToArray());
                audioSourceBox.SelectedItem = audioSourceBox.Items.Cast<object>().OfType<DShowDeviceItem>().FirstOrDefault(item =>
                    item.Type == savedAudioType && string.Equals(item.DisplayName, savedAudioDevice, StringComparison.Ordinal)) ??
                    audioSourceBox.Items[0];
                var audioFormatBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                var videoRouteBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                var audioRouteBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                var crossbarDialog = new CheckBox { Text = T("OpenCrossbarWhenPlaying"), AutoSize = true };
                var tunerDialog = new CheckBox { Text = T("OpenTVTunerWhenPlaying"), AutoSize = true };
                var audioDialog = new CheckBox { Text = T("OpenTVAudioWhenPlaying"), AutoSize = true };
                var deviceTimestamp = new CheckBox { Text = T("UseVideoDeviceTimestamp"), AutoSize = true, Checked = false };
                var audioDeviceTimestamp = new CheckBox { Text = T("UseAudioDeviceTimestamp"), AutoSize = true, Checked = false };
                var audioBufferSize = new NumericUpDown { Minimum = 0, Maximum = 60000, Value = 50, Width = 100 };
                if (existingOptions.TryGetValue("dshow_use_video_device_timestamp", out string timestampValue) ||
                    existingOptions.TryGetValue("dshow_use_video_device_timestamps", out timestampValue))
                    deviceTimestamp.Checked = timestampValue != "0";
                if (existingOptions.TryGetValue("dshow_use_audio_device_timestamp", out timestampValue) ||
                    existingOptions.TryGetValue("dshow_use_audio_device_timestamps", out timestampValue))
                    audioDeviceTimestamp.Checked = timestampValue != "0";
                if ((existingOptions.TryGetValue("dshow_audio_buffer_size", out string bufferValue) ||
                    existingOptions.TryGetValue("audio_buffer_size", out bufferValue)) &&
                    decimal.TryParse(bufferValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal bufferSize))
                    audioBufferSize.Value = Math.Max(audioBufferSize.Minimum, Math.Min(audioBufferSize.Maximum, bufferSize));
                crossbarDialog.Checked = GetIntegerOption(existingOptions, "dshow_crossbar_dialog", 0) != 0;
                tunerDialog.Checked = GetIntegerOption(existingOptions, "dshow_tv_tuner_dialog", 0) != 0;
                audioDialog.Checked = GetIntegerOption(existingOptions, "dshow_tv_audio_dialog", 0) != 0;
                var statusLabel = new Label { Dock = DockStyle.Fill, AutoSize = true };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
                var ok = MakeButton(T("OK"), null);
                var open = MakeButton(T("ButtonOpen"), null);
                var cancel = MakeButton(T("Cancel"), null);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(open);
                buttons.Controls.Add(cancel);

                grid.Controls.Add(new Label { Text = T("Device"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                grid.Controls.Add(deviceBox, 1, 0);
                grid.Controls.Add(new Label { Text = T("ResolutionFPSFormat"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                grid.Controls.Add(formatBox, 1, 1);
                grid.Controls.Add(new Label { Text = T("AudioSource"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
                grid.Controls.Add(audioSourceBox, 1, 2);
                grid.Controls.Add(new Label { Text = T("AudioPinFormat"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
                grid.Controls.Add(audioFormatBox, 1, 3);
                grid.Controls.Add(new Label { Text = T("VideoCrossbar"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
                grid.Controls.Add(videoRouteBox, 1, 4);
                grid.Controls.Add(new Label { Text = T("AudioCrossbar"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
                grid.Controls.Add(audioRouteBox, 1, 5);
                grid.Controls.Add(crossbarDialog, 1, 6);
                grid.Controls.Add(tunerDialog, 1, 7);
                grid.Controls.Add(audioDialog, 1, 8);
                grid.Controls.Add(deviceTimestamp, 1, 9);
                grid.Controls.Add(audioDeviceTimestamp, 1, 10);
                var audioBufferPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
                audioBufferPanel.Controls.Add(audioBufferSize);
                audioBufferPanel.Controls.Add(MakeInlineLabel(T("MillisecondsDeviceDefault")));
                grid.Controls.Add(new Label { Text = T("AudioBufferSize"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 11);
                grid.Controls.Add(audioBufferPanel, 1, 11);
                grid.Controls.Add(statusLabel, 1, 12);
                grid.Controls.Add(buttons, 1, 13);
                dialog.Controls.Add(grid);

                Func<Task> refresh = async () =>
                {
                    var videoDevice = (DShowDeviceItem)deviceBox.SelectedItem;
                    string device = videoDevice.DisplayName;
                    formatBox.Items.Clear();
                    videoRouteBox.Items.Clear();
                    audioRouteBox.Items.Clear();
                    videoRouteBox.Items.Add(new DShowRouteItem(T("Default"), -1, -1, false));
                    audioRouteBox.Items.Add(new DShowRouteItem(T("Default"), -1, -1, true));
                    if (videoDevice.Type < 0)
                    {
                        formatBox.Enabled = false;
                        videoRouteBox.Enabled = false;
                        audioRouteBox.Enabled = false;
                        crossbarDialog.Enabled = false;
                        tunerDialog.Enabled = false;
                        audioDialog.Enabled = false;
                        deviceTimestamp.Enabled = false;
                        statusLabel.Text = T("AudioOnlySource");
                        return;
                    }
                    formatBox.Enabled = true;
                    videoRouteBox.Enabled = true;
                    audioRouteBox.Enabled = true;
                    crossbarDialog.Enabled = true;
                    deviceTimestamp.Enabled = true;
                    string formatsReply = await SendRawCommandAsync("DSHOW_FORMATS 0 " + device);
                    foreach (string record in ParseDShowPayload(formatsReply))
                    {
                        string[] parts = record.Split('|');
                        if (parts.Length != 4 || parts[0] != "video" || !int.TryParse(parts[2], out int index)) continue;
                        formatBox.Items.Add(new DShowFormatItem(DecodeBase64Utf8(parts[1]), index, DecodeBase64Utf8(parts[3])));
                    }
                    if (formatBox.Items.Count > 0) {
                        var savedFormat = restoringExistingOptions && string.Equals(device, savedVideoDevice, StringComparison.Ordinal)
                            ? formatBox.Items.Cast<object>().OfType<DShowFormatItem>().FirstOrDefault(item =>
                                item.Index == savedVideoFormat && string.Equals(item.PinId, savedVideoPin, StringComparison.Ordinal))
                            : null;
                        formatBox.SelectedItem = savedFormat ?? formatBox.Items[0];
                    }

                    string crossbarReply = await SendRawCommandAsync("DSHOW_CROSSBAR " + device);
                    bool hasTuner = false, hasAudio = false;
                    foreach (string record in ParseDShowPayload(crossbarReply))
                    {
                        string[] parts = record.Split('|');
                        if (parts.Length == 6 && parts[0] == "route" && int.TryParse(parts[1], out int output) &&
                            int.TryParse(parts[2], out int input) && int.TryParse(parts[3], out int outputType) &&
                            int.TryParse(parts[4], out int inputType) && int.TryParse(parts[5], out int current))
                        {
                            bool audio = inputType >= 4096;
                            string name = $"In {input} ({PhysicalConnectorName(inputType)}) -> Out {output} ({PhysicalConnectorName(outputType)})" + (current == input ? " [current]" : "");
                            (audio ? audioRouteBox : videoRouteBox).Items.Add(new DShowRouteItem(name, input, output, audio));
                        }
                        else if (parts.Length == 3 && parts[0] == "features")
                        {
                            hasTuner = parts[1] == "1";
                            hasAudio = parts[2] == "1";
                        }
                    }
                    DShowRouteItem savedVideoRoute = null, savedAudioRoute = null;
                    if (restoringExistingOptions && string.Equals(device, savedVideoDevice, StringComparison.Ordinal))
                    {
                        savedVideoRoute = videoRouteBox.Items.Cast<object>().OfType<DShowRouteItem>().FirstOrDefault(item =>
                            item.Input == savedVideoInput && item.Output == savedVideoOutput);
                        savedAudioRoute = audioRouteBox.Items.Cast<object>().OfType<DShowRouteItem>().FirstOrDefault(item =>
                            item.Input == savedAudioInput && item.Output == savedAudioOutput);
                    }
                    videoRouteBox.SelectedItem = savedVideoRoute ?? videoRouteBox.Items[0];
                    audioRouteBox.SelectedItem = savedAudioRoute ?? audioRouteBox.Items[0];
                    tunerDialog.Enabled = hasTuner;
                    audioDialog.Enabled = hasAudio;
                    statusLabel.Text = TF("DShowFormatsRoutes", formatBox.Items.Count,
                        videoRouteBox.Items.Count - 1, audioRouteBox.Items.Count - 1);
                };

                Func<Task> refreshAudio = async () =>
                {
                    audioFormatBox.Items.Clear();
                    var audioDevice = audioSourceBox.SelectedItem as DShowDeviceItem;
                    if (audioDevice == null || audioDevice.Type < 0)
                    {
                        audioFormatBox.Enabled = false;
                        audioDeviceTimestamp.Enabled = false;
                        audioBufferSize.Enabled = false;
                        return;
                    }
                    audioFormatBox.Enabled = true;
                    audioDeviceTimestamp.Enabled = true;
                    audioBufferSize.Enabled = true;
                    string formatsReply = await SendRawCommandAsync("DSHOW_FORMATS " + audioDevice.Type + " " + audioDevice.DisplayName);
                    foreach (string record in ParseDShowPayload(formatsReply))
                    {
                        string[] parts = record.Split('|');
                        if (parts.Length != 4 || parts[0] != "audio" || !int.TryParse(parts[2], out int index)) continue;
                        audioFormatBox.Items.Add(new DShowFormatItem(DecodeBase64Utf8(parts[1]), index, DecodeBase64Utf8(parts[3])));
                    }
                    if (audioFormatBox.Items.Count > 0) {
                        var savedFormat = restoringExistingOptions && audioDevice.Type == savedAudioType &&
                            string.Equals(audioDevice.DisplayName, savedAudioDevice, StringComparison.Ordinal)
                            ? audioFormatBox.Items.Cast<object>().OfType<DShowFormatItem>().FirstOrDefault(item =>
                                item.Index == savedAudioFormat && string.Equals(item.PinId, savedAudioPin, StringComparison.Ordinal))
                            : null;
                        audioFormatBox.SelectedItem = savedFormat ?? audioFormatBox.Items[0];
                    }
                };

                deviceBox.SelectedIndexChanged += async (_, __) =>
                {
                    await refresh();
                    if ((audioSourceBox.SelectedItem as DShowDeviceItem)?.Type == 0) await refreshAudio();
                };
                audioSourceBox.SelectedIndexChanged += async (_, __) => await refreshAudio();
                cancel.Click += (_, __) => dialog.DialogResult = DialogResult.Cancel;
                ok.Click += (_, __) =>
                {
                    var format = formatBox.SelectedItem as DShowFormatItem;
                    var videoRoute = videoRouteBox.SelectedItem as DShowRouteItem;
                    var audioRoute = audioRouteBox.SelectedItem as DShowRouteItem;
                    var audioDevice = audioSourceBox.SelectedItem as DShowDeviceItem;
                    var audioFormat = audioFormatBox.SelectedItem as DShowFormatItem;
                    var videoDevice = deviceBox.SelectedItem as DShowDeviceItem;
                    if ((videoDevice == null || videoDevice.Type < 0) && (audioDevice == null || audioDevice.Type < 0 || audioFormat == null)) return;
                    if (videoDevice != null && videoDevice.Type >= 0 && format == null) return;
                    var options = new List<string>();
                    if (videoDevice != null && videoDevice.Type >= 0)
                    {
                        options.Add("dshow_device_b64=" + EncodeBase64Utf8(videoDevice.DisplayName));
                        options.Add("dshow_pin_b64=" + EncodeBase64Utf8(format.PinId));
                        options.Add("dshow_format=" + format.Index.ToString(CultureInfo.InvariantCulture));
                    }
                    if (audioDevice != null && audioDevice.Type >= 0 && audioFormat != null)
                    {
                        options.Add("dshow_audio_device_b64=" + EncodeBase64Utf8(audioDevice.DisplayName));
                        options.Add("dshow_audio_device_type=" + audioDevice.Type);
                        options.Add("dshow_audio_pin_b64=" + EncodeBase64Utf8(audioFormat.PinId));
                        options.Add("dshow_audio_format=" + audioFormat.Index);
                    }
                    if (videoRoute != null && videoRoute.Input >= 0)
                    {
                        options.Add("dshow_xbar_video_in=" + videoRoute.Input);
                        options.Add("dshow_xbar_video_out=" + videoRoute.Output);
                    }
                    if (audioRoute != null && audioRoute.Input >= 0)
                    {
                        options.Add("dshow_xbar_audio_in=" + audioRoute.Input);
                        options.Add("dshow_xbar_audio_out=" + audioRoute.Output);
                    }
                    if (crossbarDialog.Checked) options.Add("dshow_crossbar_dialog=1");
                    if (tunerDialog.Checked) options.Add("dshow_tv_tuner_dialog=1");
                    if (audioDialog.Checked) options.Add("dshow_tv_audio_dialog=1");
                    options.Add("dshow_use_video_device_timestamp=" + (deviceTimestamp.Checked ? "1" : "0"));
                    options.Add("dshow_use_audio_device_timestamp=" + (audioDeviceTimestamp.Checked ? "1" : "0"));
                    options.Add("dshow_audio_buffer_size=" + audioBufferSize.Value.ToString(CultureInfo.InvariantCulture));
                    string title = videoDevice != null && videoDevice.Type >= 0 ? videoDevice.Name : audioDevice.Name;
                    SetInputFields("<DSHOW>", title, string.Join(",", options));
                    dialog.DialogResult = DialogResult.OK;
                };
                open.Click += async (_, __) =>
                {
                    ok.PerformClick();
                    if (dialog.DialogResult == DialogResult.OK)
                        await PlayManualAsync();
                };

                await refresh();
                await refreshAudio();
                restoringExistingOptions = false;
                dialog.ShowDialog(this);
            }
        }

        private static IEnumerable<string> ParseDShowPayload(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply) || !reply.StartsWith("OK data=", StringComparison.OrdinalIgnoreCase)) return Enumerable.Empty<string>();
            string payload = reply.Substring(8);
            return payload.Length == 0 ? Enumerable.Empty<string>() : payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string EncodeBase64Utf8(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        private static string DecodeBase64Utf8(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
            catch { return string.Empty; }
        }

        private static string PhysicalConnectorName(int type)
        {
            switch (type)
            {
                case 1: return "Video Tuner";
                case 2: return "Composite";
                case 3: return "S-Video";
                case 4: return "RGB";
                case 12: return "Video Decoder";
                case 4096: return "Audio Tuner";
                case 4097: return "Audio Line";
                case 4098: return "Microphone";
                case 4106: return "Audio Decoder";
                default: return type.ToString(CultureInfo.InvariantCulture);
            }
        }

        private sealed class DShowFormatItem
        {
            public DShowFormatItem(string pinId, int index, string description) { PinId = pinId; Index = index; Description = description; }
            public string PinId { get; }
            public int Index { get; }
            public string Description { get; }
            public override string ToString() => Description;
        }

        private sealed class DShowDeviceItem
        {
            public DShowDeviceItem(int type, string name, string displayName) { Type = type; Name = name; DisplayName = displayName; }
            public int Type { get; }
            public string Name { get; }
            public string DisplayName { get; }
            public override string ToString() => Type < 0 ? Name : (Type == 0 ? "Video device: " : "Audio device: ") + Name;
        }

        private sealed class DShowRouteItem
        {
            public DShowRouteItem(string description, int input, int output, bool audio) { Description = description; Input = input; Output = output; Audio = audio; }
            public string Description { get; }
            public int Input { get; }
            public int Output { get; }
            public bool Audio { get; }
            public override string ToString() => Description;
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

            progressBar.Dock = DockStyle.None;
            progressBar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            backButton.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            forwardButton.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(progressBar, 0, 0);
            panel.Controls.Add(backButton, 1, 0);
            panel.Controls.Add(forwardButton, 2, 0);

            return panel;
        }

        private void BrowseInputFile()
        {
            using (var dialog = new OpenFileDialog { Filter = T("MediaFileFilter") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                inputBox.Text = dialog.FileName;
            }
        }

        private void UpdateTitleFromManualInput()
        {
            if (suppressInputTitleUpdate) return;
            string title = GetInputTitle(inputBox.Text.Trim());
            titleBox.Text = title;
        }

        private void SetInputFields(string input, string title, string options)
        {
            suppressInputTitleUpdate = true;
            updatingQueueControls = true;
            updatingDShowTimestamp = true;
            try
            {
                inputBox.Text = input;
                titleBox.Text = title;
                // This path restores an existing input (startup, tabs, playlists, and favorites).
                // Keep any saved queue tuning rather than replacing it with source defaults.
                optionsBox.Text = NormalizeQueueOptions(input, options, false);
                LoadQueueControlsFromOptions(input, optionsBox.Text);
                LoadDShowTimestampFromOptions(input, optionsBox.Text);
            }
            finally
            {
                updatingDShowTimestamp = false;
                updatingQueueControls = false;
                suppressInputTitleUpdate = false;
            }
        }

        private void LoadDShowTimestampFromOptions(string input, string options)
        {
            if (dshowDeviceTimestampBox == null || dshowAudioDeviceTimestampBox == null || dshowAudioBufferSizeBox == null) return;
            bool isDShow = string.Equals(input?.Trim(), "<DSHOW>", StringComparison.OrdinalIgnoreCase);
            var values = ParseOptionMap(options);
            bool hasVideo = isDShow && values.ContainsKey("dshow_device_b64");
            bool hasAudio = isDShow && values.ContainsKey("dshow_audio_device_b64");
            dshowDeviceTimestampBox.Enabled = hasVideo;
            dshowDeviceTimestampBox.Checked = hasVideo &&
                ((values.TryGetValue("dshow_use_video_device_timestamp", out string value) && value != "0") ||
                 (values.TryGetValue("dshow_use_video_device_timestamps", out value) && value != "0"));
            dshowAudioDeviceTimestampBox.Enabled = hasAudio;
            dshowAudioDeviceTimestampBox.Checked = hasAudio &&
                ((values.TryGetValue("dshow_use_audio_device_timestamp", out value) && value != "0") ||
                 (values.TryGetValue("dshow_use_audio_device_timestamps", out value) && value != "0"));
            dshowAudioBufferSizeBox.Enabled = hasAudio;
            dshowAudioBufferSizeBox.Value = GetDShowAudioBufferSize(values);
        }

        private static decimal GetDShowAudioBufferSize(Dictionary<string, string> values)
        {
            return (values.TryGetValue("dshow_audio_buffer_size", out string value) ||
                values.TryGetValue("audio_buffer_size", out value)) && decimal.TryParse(value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal size)
                ? Math.Max(0, Math.Min(60000, size)) : 50;
        }

        private void UpdateDShowCaptureOptions()
        {
            if (updatingDShowTimestamp || dshowDeviceTimestampBox == null || dshowAudioDeviceTimestampBox == null || dshowAudioBufferSizeBox == null ||
                !string.Equals(inputBox.Text?.Trim(), "<DSHOW>", StringComparison.OrdinalIgnoreCase)) return;
            var entries = optionsBox.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            entries.RemoveAll(value => value.StartsWith("dshow_use_video_device_timestamp=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("dshow_use_video_device_timestamps=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("dshow_use_audio_device_timestamp=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("dshow_use_audio_device_timestamps=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("dshow_audio_buffer_size=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("audio_buffer_size=", StringComparison.OrdinalIgnoreCase));
            entries.Add("dshow_use_video_device_timestamp=" + (dshowDeviceTimestampBox.Checked ? "1" : "0"));
            entries.Add("dshow_use_audio_device_timestamp=" + (dshowAudioDeviceTimestampBox.Checked ? "1" : "0"));
            entries.Add("dshow_audio_buffer_size=" + dshowAudioBufferSizeBox.Value.ToString(CultureInfo.InvariantCulture));
            optionsBox.Text = string.Join(",", entries);
            if (controlsReady) SaveActiveConnectionState();
        }

        private static bool IsBufferedSpecialSource(string input)
        {
            return string.Equals(input?.Trim(), "<DSHOW>", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(input?.Trim(), "<OBSVCAM>", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> ParseOptionMap(string options)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in (options ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = entry.IndexOf('=');
                if (separator > 0) values[entry.Substring(0, separator).Trim()] = entry.Substring(separator + 1).Trim();
            }
            return values;
        }

        private static string GetBase64Option(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out string value) ? DecodeBase64Utf8(value) : string.Empty;
        }

        private static int GetIntegerOption(Dictionary<string, string> values, string key, int fallback)
        {
            return values.TryGetValue(key, out string value) && int.TryParse(value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
        }

        private static string NormalizeQueueOptions(string input, string options, bool resetToDefaults)
        {
            var entries = (options ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            var values = ParseOptionMap(options);
            entries.RemoveAll(value => value.StartsWith("queue_left=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("queue_right=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("queue_center=", StringComparison.OrdinalIgnoreCase));
            bool bufferedSpecialSource = IsBufferedSpecialSource(input);
            string defaultLeft = bufferedSpecialSource ? "1" : "-1";
            string defaultRight = bufferedSpecialSource ? "5" : "-1";
            string left = resetToDefaults ? defaultLeft : (values.ContainsKey("queue_left") ? values["queue_left"] : defaultLeft);
            string right = resetToDefaults ? defaultRight : (values.ContainsKey("queue_right") ? values["queue_right"] : defaultRight);
            string center = resetToDefaults ? "-1" : (values.ContainsKey("queue_center") ? values["queue_center"] : "-1");
            bool hasQueueOptions = values.ContainsKey("queue_left") || values.ContainsKey("queue_right") || values.ContainsKey("queue_center");
            if (bufferedSpecialSource || (!resetToDefaults && hasQueueOptions))
            {
                entries.Add("queue_left=" + left);
                entries.Add("queue_right=" + right);
                entries.Add("queue_center=" + center);
            }
            return string.Join(",", entries);
        }

        private static decimal GetQueueOption(Dictionary<string, string> values, string key, decimal fallback)
        {
            return values.TryGetValue(key, out string value) && decimal.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out decimal parsed) ? Math.Max(-1, Math.Min(1000000, parsed)) : fallback;
        }

        private void LoadQueueControlsFromOptions(string input, string options)
        {
            if (queueLeftBox == null) return;
            var values = ParseOptionMap(options);
            decimal defaultLeft = IsBufferedSpecialSource(input) ? 1 : -1;
            decimal defaultRight = IsBufferedSpecialSource(input) ? 5 : -1;
            queueLeftBox.Value = GetQueueOption(values, "queue_left", defaultLeft);
            queueRightBox.Value = GetQueueOption(values, "queue_right", defaultRight);
            queueCenterBox.Value = GetQueueOption(values, "queue_center", -1);
        }

        private void UpdateQueueOptionsFromControls()
        {
            if (updatingQueueControls || queueLeftBox == null) return;
            optionsBox.Text = NormalizeQueueOptions(inputBox.Text, optionsBox.Text, false);
            var entries = optionsBox.Text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            entries.RemoveAll(value => value.StartsWith("queue_left=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("queue_right=", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("queue_center=", StringComparison.OrdinalIgnoreCase));
            entries.Add("queue_left=" + queueLeftBox.Value.ToString(CultureInfo.InvariantCulture));
            entries.Add("queue_right=" + queueRightBox.Value.ToString(CultureInfo.InvariantCulture));
            entries.Add("queue_center=" + queueCenterBox.Value.ToString(CultureInfo.InvariantCulture));
            optionsBox.Text = string.Join(",", entries);
            if (controlsReady) SaveActiveConnectionState();
        }

        private void ApplyInputQueuePolicy(bool resetQueueDefaults)
        {
            if (updatingQueueControls || queueLeftBox == null) return;
            updatingQueueControls = true;
            updatingDShowTimestamp = true;
            try
            {
                optionsBox.Text = NormalizeQueueOptions(inputBox.Text, optionsBox.Text, resetQueueDefaults);
                LoadQueueControlsFromOptions(inputBox.Text, optionsBox.Text);
                LoadDShowTimestampFromOptions(inputBox.Text, optionsBox.Text);
            }
            finally
            {
                updatingDShowTimestamp = false;
                updatingQueueControls = false;
            }
        }

        private void SetScheduledInputFields(string title, string options)
        {
            scheduledTitleBox.Text = title;
            scheduledOptionsBox.Text = options;
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

        private async Task SendAutoDeinterlaceAsync()
        {
            await SendCommandAsync($"SET_AUTO_DEINTERLACE {(autoDeinterlaceBox.Checked ? 1 : 0)} {autoDeinterlaceFilterBox.Text.Trim()}");
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
                $"SET_AUTO_DEINTERLACE {(autoDeinterlaceBox.Checked ? 1 : 0)} {autoDeinterlaceFilterBox.Text.Trim()}",
                $"SET_INDEX video={(int)videoIndexBox.Value} audio={(int)audioIndexBox.Value}",
                $"SET_SHIFT {(long)shiftBox.Value}"
            });
        }

        private async Task PlayInputAsync(string input, string options)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog(T("InputEmpty"));
                return;
            }
            options = NormalizeQueueOptions(input, options, false);
            ClearInteractiveRuntime();
            ClearQueuedPlaybackState();
            await SendCommandAsync(string.IsNullOrWhiteSpace(options) ? "PLAY " + input : "PLAY " + input + "\t" + options);
        }

        private async Task<bool> QueueNextInputAsync(string input, string options, bool logCommand = false)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            options = NormalizeQueueOptions(input, options, false);
            string command = string.IsNullOrWhiteSpace(options) ? "PLAY_NEXT " + input : "PLAY_NEXT " + input + "\t" + options;
            string reply = await SendRawCommandAsync(command);
            if (logCommand) AppendLog($"> {command}\r\n< {reply}");
            return reply != null && reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
        }

        private void ClearQueuedPlaybackState(bool clearEvents = false)
        {
            pendingPlaybackTransition = null;
            if (!clearEvents) return;
            playbackStateGeneration++;
            backendInputEvents.Clear();
            pendingScheduledObsEnds.Clear();
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
            SetInputFields(file, GetInputTitle(file), optionsBox.Text);
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
                AppendLog(T("InputEmpty"));
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
            SetInputFields(favoriteInputs[index].Input, favoriteInputs[index].Title, favoriteInputs[index].Options);
        }

        private async Task PlaySelectedFavoriteInputAsync()
        {
            if (favoriteInputView.SelectedIndices.Count == 0) return;
            int index = favoriteInputView.SelectedIndices[0];
            if (index < 0 || index >= favoriteInputs.Count) return;
            FavoriteInputItem item = favoriteInputs[index];
            SetInputFields(item.Input, item.Title, item.Options);
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
            using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("MediaFileFilter") })
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
            using (var dialog = new OpenFileDialog { Filter = T("MediaFileFilter") })
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

            ScheduledPlaylistItem item = CreateScheduledItem(input, true);
            if (!ValidateScheduledItemForUser(item)) return Task.CompletedTask;
            scheduledPlaylist.Add(item);
            RefreshScheduledPlaylistView();
            return Task.CompletedTask;
        }

        private Task AddScheduledFilesAsync()
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("MediaFileFilter") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                foreach (string file in dialog.FileNames)
                {
                    ScheduledPlaylistItem item = CreateScheduledItem(file, false);
                    if (!ValidateScheduledItemForUser(item)) return Task.CompletedTask;
                    scheduledPlaylist.Add(item);
                }
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
                    ScheduledPlaylistItem item = CreateScheduledItem(file, false);
                    if (!ValidateScheduledItemForUser(item)) return Task.CompletedTask;
                    scheduledPlaylist.Add(item);
                }
            }
            RefreshScheduledPlaylistView();
            return Task.CompletedTask;
        }

        private ScheduledPlaylistItem CreateScheduledItem(string input, bool useTitleInput)
        {
            var item = new ScheduledPlaylistItem
            {
                Enabled = scheduledEnabledBox.Checked,
                Path = input,
                Title = useTitleInput ? GetScheduledTitleInputOrDefault(input) : GetInputTitle(input),
                DurationSeconds = 0,
                Options = GetScheduledOptionsInput(),
                ScheduleKind = GetComboValue(scheduleTypeBox) == "Weekly" ? ScheduleKind.Weekly : ScheduleKind.OneTime,
                StartAt = scheduleDateTimePicker.Value,
                WeeklyTime = scheduleTimePicker.Value.TimeOfDay,
                WeekDays = GetSelectedWeekDays(),
                HasEndTime = scheduleEndBox.Checked,
                EndAt = scheduleEndPicker.Value,
                EndAction = GetSelectedEndAction(),
                StartAction = GetSelectedStartAction()
                ,ObsScene = obsSceneBox.Text.Trim()
                ,ObsShowSources = obsShowSourcesBox.Text.Trim()
                ,ObsHideSources = obsHideSourcesBox.Text.Trim()
                ,ObsEndScene = obsEndSceneBox.Text.Trim()
                ,ObsEndShowSources = obsEndShowSourcesBox.Text.Trim()
                ,ObsEndHideSources = obsEndHideSourcesBox.Text.Trim()
            };
            if (item.ScheduleKind == ScheduleKind.Weekly && item.WeekDays == 0) item.WeekDays = WeekDayMaskFromDay(DateTime.Now.DayOfWeek);
            return item;
        }

        private void RemoveSelectedScheduled()
        {
            foreach (int index in scheduledPlaylistView.SelectedIndices.Cast<int>().OrderByDescending(i => i))
            {
                scheduledPlaylist.RemoveAt(index);
            }
            RefreshCurrentScheduledIndex();
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
            RefreshCurrentScheduledIndex();
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
            scheduledEnabledBox.Checked = item.Enabled;
            SetInputFields(item.Path, item.Title, item.Options);
            SetScheduledInputFields(item.Title, item.Options);
            SetComboValue(scheduleTypeBox, item.ScheduleKind == ScheduleKind.Weekly ? "Weekly" : "One-time");
            scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, item.StartAt);
            scheduleTimePicker.Value = DateTime.Today.Add(item.WeeklyTime);
            SetSelectedWeekDays(item.WeekDays);
            scheduleEndBox.Checked = item.HasEndTime;
            scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, item.EndAt);
            SetComboValue(scheduleEndActionBox, EndActionValue(item.EndAction));
            SetComboValue(scheduleStartActionBox, StartActionValue(item.StartAction));
            obsSceneBox.Text = item.ObsScene;
            obsShowSourcesBox.Text = item.ObsShowSources;
            obsHideSourcesBox.Text = item.ObsHideSources;
            obsEndSceneBox.Text = item.ObsEndScene;
            obsEndShowSourcesBox.Text = item.ObsEndShowSources;
            obsEndHideSourcesBox.Text = item.ObsEndHideSources;
            UpdateScheduleEndControls();
        }

        private void ApplyScheduledControlsToSelected()
        {
            if (scheduledPlaylistView.SelectedIndices.Count != 1) return;
            int index = scheduledPlaylistView.SelectedIndices[0];
            if (index < 0 || index >= scheduledPlaylist.Count) return;
            ScheduledPlaylistItem item = scheduledPlaylist[index];
            ScheduledPlaylistItem updated = CreateScheduledItem(inputBox.Text.Trim(), true);
            updated.DurationSeconds = item.DurationSeconds;
            updated.LastTriggeredAt = item.LastTriggeredAt;
            if (!ValidateScheduledItemForUser(updated)) return;
            item.Path = inputBox.Text.Trim();
            item.Enabled = updated.Enabled;
            item.Title = updated.Title;
            item.Options = updated.Options;
            SetScheduledInputFields(item.Title, item.Options);
            item.ScheduleKind = updated.ScheduleKind;
            item.StartAt = updated.StartAt;
            item.WeeklyTime = updated.WeeklyTime;
            item.WeekDays = updated.WeekDays;
            item.HasEndTime = updated.HasEndTime;
            item.EndAt = updated.EndAt;
            item.EndAction = updated.EndAction;
            item.StartAction = updated.StartAction;
            item.ObsScene = updated.ObsScene;
            item.ObsShowSources = updated.ObsShowSources;
            item.ObsHideSources = updated.ObsHideSources;
            item.ObsEndScene = updated.ObsEndScene;
            item.ObsEndShowSources = updated.ObsEndShowSources;
            item.ObsEndHideSources = updated.ObsEndHideSources;
            RefreshScheduledPlaylistView();
            scheduledPlaylistView.Items[index].Selected = true;
        }

        private void UpdateScheduleEndControls()
        {
            bool enabled = scheduleEndBox.Checked;
            scheduleEndPicker.Enabled = enabled;
            scheduleEndActionBox.Enabled = enabled;
        }

        private bool ValidateScheduledItemForUser(ScheduledPlaylistItem item)
        {
            string error = GetScheduledItemValidationError(item);
            if (string.IsNullOrEmpty(error)) return true;

            AppendLog(T("InvalidScheduledItemTitle") + ": " + error);
            MessageBox.Show(this, error, T("InvalidScheduledItemTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private static string GetScheduledItemValidationError(ScheduledPlaylistItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Path)) return T("InputPathEmpty");
            if (item.ScheduleKind == ScheduleKind.Weekly && item.WeekDays == 0) return T("NoDays");
            if (!item.HasEndTime) return string.Empty;

            if (item.ScheduleKind == ScheduleKind.OneTime && item.EndAt <= item.StartAt)
            {
                return T("ScheduleEndBeforeStart");
            }

            return string.Empty;
        }

        private string GetScheduledOptionsInput()
        {
            return scheduledOptionsBox.Text.Trim();
        }

        private string GetScheduledTitleInputOrDefault(string input)
        {
            string title = scheduledTitleBox.Text.Trim();
            return string.IsNullOrWhiteSpace(title) ? GetInputTitle(input) : title;
        }

        private async Task PlaySelectedScheduledAsync()
        {
            if (scheduledPlaylistView.SelectedIndices.Count == 0) return;
            await ApplyScheduledObsEndAsync();
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
            SetInputFields(playlist[index].Path, playlist[index].Title, playlist[index].Options);
        }

        private void SetSelectedOptions()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            string options = scheduledOptionsBox.Text.Trim();
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
            await ApplyScheduledObsEndAsync();
            currentIndex = playlistView.SelectedIndices[0];
            await PlayCurrentAsync();
        }

        private void SetSelectedAsNextPlayback()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            int index = playlistView.SelectedIndices[0];
            if (index < 0 || index >= playlist.Count) return;
            playNextOverrideId = playlist[index].RuntimeId;
            playNextResumeIndex = playingScheduled
                ? (currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0)
                : GetNextPlaylistIndex(currentIndex);
            AppendLog(TF("LogNextPlayback", playlist[index].Title));
            RefreshPlaylistView();
        }

        private int TakePreferredNextPlaylistIndex(int sourceIndex, bool returningFromScheduled = false)
        {
            if (!returningFromScheduled && activePlayNextOverrideId != Guid.Empty &&
                sourceIndex >= 0 && sourceIndex < playlist.Count &&
                playlist[sourceIndex].RuntimeId == activePlayNextOverrideId)
            {
                activePlayNextOverrideId = Guid.Empty;
                int resume = playNextResumeIndex;
                playNextResumeIndex = -1;
                return resume >= 0 && resume < playlist.Count ? resume : GetNextPlaylistIndex(sourceIndex);
            }

            int overrideIndex = FindPlaylistItemIndex(playNextOverrideId);
            if (overrideIndex >= 0)
            {
                activePlayNextOverrideId = playNextOverrideId;
                playNextOverrideId = Guid.Empty;
                return overrideIndex;
            }
            playNextOverrideId = Guid.Empty;
            return returningFromScheduled
                ? (currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0)
                : GetNextPlaylistIndex(sourceIndex);
        }

        private void ActivatePlayNextOverrideIfNeeded(int index)
        {
            if (index < 0 || index >= playlist.Count || playNextOverrideId == Guid.Empty) return;
            if (playlist[index].RuntimeId != playNextOverrideId) return;
            activePlayNextOverrideId = playNextOverrideId;
            playNextOverrideId = Guid.Empty;
        }

        private async Task StartPlayoutAsync()
        {
            if (playlist.Count == 0 && scheduledPlaylist.Count == 0) return;
            if (playoutRunning) return;
            await RefreshStatusForPlayoutStartAsync();
            playoutRunning = true;
            SetPlayoutTimerRunning(true);
            bool adoptedCurrentPlayback = await TryAdoptCurrentPlaybackAsync();
            waitingForScheduledStart = !adoptedCurrentPlayback && scheduledStartBox.Checked;
            if (!adoptedCurrentPlayback && !waitingForScheduledStart)
            {
                bool startedScheduled = await StartDueScheduledItemAsync();
                if (!startedScheduled && !playingScheduled && playlist.Count > 0)
                {
                    currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
                    await PlayCurrentAsync();
                }
            }
            UpdatePlayoutStatusLabel();
            AppendLog(T("PlayoutStarted"));
        }

        private async Task RefreshStatusForPlayoutStartAsync()
        {
            while (refreshingStatus)
                await Task.Delay(25);
            await RefreshStatusAsync();
        }

        private async Task<bool> TryAdoptCurrentPlaybackAsync()
        {
            if (!IsCurrentPlaybackActive()) return false;
            string actualSummary = string.IsNullOrEmpty(currentActualStatusInput)
                ? currentStatusInput : currentActualStatusInput;
            if (string.IsNullOrEmpty(actualSummary)) return false;

            DateTime now = DateTime.Now;
            int scheduledIndex = FindMatchingActiveScheduledIndex(actualSummary, now);
            if (scheduledIndex >= 0)
            {
                ScheduledPlaylistItem liveItem = scheduledPlaylist[scheduledIndex];
                ScheduleWindow window = GetScheduleWindow(liveItem, now);
                liveItem.LastTriggeredAt = window.Start;
                currentScheduledIndex = scheduledIndex;
                currentScheduledItem = CloneScheduledItem(liveItem);
                playingScheduled = true;
                scheduledObsEndApplied = false;
                scheduledSlotEndAt = window.End;
                currentStartedAt = now.AddSeconds(-Math.Max(0, currentPositionSeconds));
                SetInputFields(liveItem.Path, liveItem.Title, liveItem.Options);
                SelectCurrentRow();
                SelectCurrentScheduledRow();
                await ApplyObsActionsAsync(liveItem.ObsScene, liveItem.ObsShowSources, liveItem.ObsHideSources);
                RefreshScheduledPlaylistView();
                AppendLog(TF("LogAdoptedScheduledPlayback", liveItem.Title));
                return true;
            }

            int normalIndex = FindMatchingPlaylistIndex(actualSummary);
            if (normalIndex < 0) return false;
            currentIndex = normalIndex;
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            scheduledSlotEndAt = DateTime.MinValue;
            currentStartedAt = now.AddSeconds(-Math.Max(0, currentPositionSeconds));
            ActivatePlayNextOverrideIfNeeded(currentIndex);
            SelectCurrentRow();
            RefreshScheduledPlaylistView();
            AppendLog(TF("LogAdoptedNormalPlayback", playlist[normalIndex].Title));
            return true;
        }

        private int FindMatchingActiveScheduledIndex(string summary, DateTime now)
        {
            int selectedIndex = -1;
            DateTime selectedStart = DateTime.MinValue;
            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                ScheduledPlaylistItem item = scheduledPlaylist[i];
                if (!item.Enabled) continue;
                ScheduleWindow window = GetScheduleWindow(item, now);
                if (!window.IsActive || !InputSummariesEqual(summary,
                    BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false)))) continue;
                if (selectedIndex < 0 || window.Start > selectedStart)
                {
                    selectedIndex = i;
                    selectedStart = window.Start;
                }
            }
            return selectedIndex;
        }

        private int FindMatchingPlaylistIndex(string summary)
        {
            for (int i = 0; i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                if (InputSummariesEqual(summary,
                    BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false)))) return i;
            }
            return -1;
        }

        private async Task StopPlayoutAsync()
        {
            await ApplyScheduledObsEndAsync();
            ClearQueuedPlaybackState();
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            ResetScheduledTriggers();
            SetPlayoutTimerRunning(false);
            UpdatePlayoutStatusLabel();
            WriteNowPlayingXml();
            AppendLog(T("PlayoutStopped"));
        }

        private async Task StopAllAsync()
        {
            await ApplyScheduledObsEndAsync();
            ClearQueuedPlaybackState();
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            ResetScheduledTriggers();
            ClearInteractiveRuntime();
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
            if (processingPlayoutTick) return;
            processingPlayoutTick = true;
            try
            {
                await PlayoutTickCoreAsync();
            }
            finally
            {
                processingPlayoutTick = false;
            }
        }

        private async Task PlayoutTickCoreAsync()
        {
            if (!playoutRunning) return;
            if (advancingPlayout) return;
            if (pendingPlaybackTransition != null)
            {
                await RecoverTimedOutPendingPlaybackAsync();
                return;
            }
            if (playlist.Count == 0 && scheduledPlaylist.Count == 0) return;

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

            if (IsWaitingScheduledSlotActive(DateTime.Now))
            {
                await HandleScheduledEndAsync();
                return;
            }

            await StartDueScheduledItemAsync();
            await HandleScheduledEndAsync();
            await AutoAdvanceIfNeededAsync();
        }

        private async Task AutoAdvanceIfNeededAsync()
        {
            if (advancingPlayout) return;
            bool terminalTimedOut = terminalStatusSince != DateTime.MinValue &&
                DateTime.Now - terminalStatusSince >= PendingPlaybackTimeout;
            if (playingScheduled)
            {
                if (terminalTimedOut) await HandleScheduledEndAsync();
                else await AutoAdvanceScheduledIfNeededAsync();
                return;
            }
            if (!autoAdvanceBox.Checked || currentIndex < 0 || currentIndex >= playlist.Count) return;
            if ((DateTime.Now - lastAutoAdvanceAt).TotalSeconds < 2) return;

            PlaylistItem current = playlist[currentIndex];
            string actualSummary = string.IsNullOrEmpty(currentActualStatusInput) ? currentStatusInput : currentActualStatusInput;
            bool statusMatchesCurrent = InputSummariesEqual(actualSummary,
                BuildInputSummary(current.Path, NormalizeQueueOptions(current.Path, current.Options, false)));
            bool playbackFailed = statusMatchesCurrent && terminalTimedOut && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool playbackEnded = statusMatchesCurrent && terminalTimedOut && string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase);

            // Normal playlist transitions are driven by INPUT_EOF. Duration is informational and
            // may be reached before the backend has actually finished reading the input; advancing
            // here would incorrectly skip a WaitCurrentItem scheduled task that is still blocked.
            if (playbackFailed || playbackEnded)
            {
                advancingPlayout = true;
                lastAutoAdvanceAt = DateTime.Now;
                try
                {
                    if (!await StartDueScheduledItemAsync()) await PlayNextAsync(false);
                }
                finally
                {
                    advancingPlayout = false;
                }
            }
        }

        private async Task PlayNextAsync(bool manual)
        {
            if (!manual && await StartDueScheduledItemAsync()) return;
            if (playingScheduled)
            {
                await ReturnToNormalPlaylistAsync();
                if (manual && playlist.Count > 0) await PlayNextAsync(false);
                return;
            }
            if (playlist.Count == 0) return;
            currentIndex = TakePreferredNextPlaylistIndex(currentIndex);
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
            currentScheduledItem = null;
            scheduledSlotEndAt = DateTime.MinValue;
            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
            ActivatePlayNextOverrideIfNeeded(currentIndex);
            SelectCurrentRow();
            await PlayInputAsync(item.Path, item.Options);
            UpdatePlayoutStatusLabel();
        }

        private int GetNextPlaylistIndex(int fromIndex)
        {
            if (playlist.Count == 0) return -1;
            if (GetComboValue(playoutModeBox) == "Random")
            {
                int nextIndex = random.Next(playlist.Count);
                if (playlist.Count > 1)
                    while (nextIndex == fromIndex) nextIndex = random.Next(playlist.Count);
                return nextIndex;
            }
            return fromIndex < 0 ? 0 : (fromIndex + 1) % playlist.Count;
        }

        private void HandleBackendEvent(string eventLine, int generation)
        {
            if (IsDisposed || generation != commandClient.Generation) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, int>(HandleBackendEvent), eventLine, generation);
                return;
            }
            AppendLog("[vcam event] " + eventLine);
            if (HandleInteractiveBackendEvent(eventLine)) return;
            string inputSummary = ParseStatusString(eventLine, "input=");
            if (eventLine.StartsWith("INPUT_EOF", StringComparison.OrdinalIgnoreCase))
                QueueBackendInputEvent(new BackendInputEvent(BackendInputEventType.Eof,
                    ParseStatusLong(eventLine, "frame_id="), inputSummary));
            else if (eventLine.StartsWith("INPUT_ENDED", StringComparison.OrdinalIgnoreCase))
                QueueBackendInputEvent(new BackendInputEvent(BackendInputEventType.Ended,
                    ParseStatusLong(eventLine, "frame_id="), inputSummary));
        }

        private void QueueBackendInputEvent(BackendInputEvent inputEvent)
        {
            backendInputEvents.Enqueue(inputEvent);
            if (!handlingBackendInputEvents) _ = HandleBackendInputEventsAsync();
        }

        private async Task HandleBackendInputEventsAsync()
        {
            if (handlingBackendInputEvents) return;
            handlingBackendInputEvents = true;
            int stateGeneration = playbackStateGeneration;
            try
            {
                while (stateGeneration == playbackStateGeneration && backendInputEvents.Count > 0)
                {
                    BackendInputEvent inputEvent = backendInputEvents.Dequeue();
                    if (inputEvent.Type == BackendInputEventType.Eof)
                        await AdvanceOnInputEofAsync(inputEvent);
                    else
                        await CompleteInputTransitionAsync(inputEvent);
                }
            }
            finally
            {
                handlingBackendInputEvents = false;
            }
        }

        private async Task AdvanceOnInputEofAsync(BackendInputEvent inputEvent)
        {
            if (!playoutRunning) return;
            // Do not discard an EOF merely because a timer/manual transition is finishing. This
            // event is the authoritative ordinary-item completion signal used to unblock schedules.
            while (advancingPlayout && playoutRunning)
                await Task.Delay(25);
            if (!playoutRunning) return;
            advancingPlayout = true;
            lastAutoAdvanceAt = DateTime.Now;
            try
            {
                if (pendingPlaybackTransition != null)
                {
                    if (inputEvent.FrameId != pendingPlaybackTransition.FrameId &&
                        InputSummariesEqual(inputEvent.Input, pendingPlaybackTransition.TargetInput))
                        pendingPlaybackTransition.DeferredTargetEof = inputEvent;
                    return;
                }
                string endedInput = inputEvent.Input;
                bool eventMatchesScheduled = currentScheduledItem != null && InputSummariesEqual(endedInput,
                    BuildInputSummary(currentScheduledItem.Path, NormalizeQueueOptions(
                        currentScheduledItem.Path, currentScheduledItem.Options, false)));
                bool eventMatchesNormal = currentIndex >= 0 && currentIndex < playlist.Count && InputSummariesEqual(endedInput,
                    BuildInputSummary(playlist[currentIndex].Path, NormalizeQueueOptions(
                        playlist[currentIndex].Path, playlist[currentIndex].Options, false)));
                bool sourceScheduled = !string.IsNullOrEmpty(endedInput)
                    ? eventMatchesScheduled && !eventMatchesNormal
                    : playingScheduled;
                int sourceIndex = sourceScheduled ? currentScheduledIndex : currentIndex;
                if (sourceScheduled)
                {
                    await HandleScheduledEndAsync(true, inputEvent.FrameId, sourceIndex, currentScheduledItem);
                    return;
                }
                if (await QueueWaitingScheduledActivationAsync(inputEvent.FrameId)) return;
                if (!autoAdvanceBox.Checked || playlist.Count == 0) return;
                int nextIndex = TakePreferredNextPlaylistIndex(sourceIndex);
                if (nextIndex < 0 || nextIndex >= playlist.Count) return;
                PlaylistItem next = playlist[nextIndex];
                if (!await QueueNextInputAsync(next.Path, next.Options, true)) return;
                pendingPlaybackTransition = PendingPlaybackTransition.ForPlaylist(nextIndex, next, inputEvent.FrameId);
            }
            finally
            {
                advancingPlayout = false;
            }
        }

        private async Task PlayScheduledCurrentAsync()
        {
            List<ScheduledPlaylistItem> active = scheduledPlaylist;
            if (currentScheduledIndex < 0 || currentScheduledIndex >= active.Count) return;
            ScheduledPlaylistItem liveItem = active[currentScheduledIndex];
            ScheduledPlaylistItem item = CloneScheduledItem(liveItem);
            currentScheduledItem = item;
            playingScheduled = true;
            scheduledObsEndApplied = false;
            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
            ScheduleWindow window = GetScheduleWindow(item, DateTime.Now);
            scheduledSlotEndAt = window.End;
            SelectCurrentRow();
            SelectCurrentScheduledRow();
            SetInputFields(item.Path, item.Title, item.Options);
            item.LastTriggeredAt = window.Start;
            liveItem.LastTriggeredAt = window.Start;
            await ApplyObsActionsAsync(item.ObsScene, item.ObsShowSources, item.ObsHideSources);
            await PlayInputAsync(item.Path, item.Options);
            UpdatePlayoutStatusLabel();
        }

        private async Task<bool> QueueWaitingScheduledActivationAsync(long endingFrameId = 0,
            ScheduledPlaylistItem endingScheduledItem = null)
        {
            DateTime now = DateTime.Now;
            int index = FindDueScheduledIndex(now);
            if (index < 0) return false;
            ScheduledPlaylistItem item = CloneScheduledItem(scheduledPlaylist[index]);
            ScheduleWindow window = GetScheduleWindow(item, now);
            if (item.StartAction == ScheduleStartAction.StartImmediately)
            {
                if (endingScheduledItem != null) await ApplyObsActionsAsync(endingScheduledItem.ObsEndScene,
                    endingScheduledItem.ObsEndShowSources, endingScheduledItem.ObsEndHideSources);
                currentScheduledIndex = index;
                currentScheduledItem = item;
                await PlayScheduledCurrentAsync();
                return true;
            }

            if (!await QueueNextInputAsync(item.Path, item.Options, true)) return false;
            if (endingScheduledItem != null) QueueScheduledObsEnd(endingScheduledItem, endingFrameId);
            pendingPlaybackTransition = PendingPlaybackTransition.ForScheduled(index, item, endingFrameId, window.Start, true);
            return true;
        }

        private int FindDueScheduledIndex(DateTime now)
        {
            int selectedIndex = -1;
            ScheduleWindow selectedWindow = ScheduleWindow.Empty;
            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                ScheduledPlaylistItem item = scheduledPlaylist[i];
                if (!item.Enabled) continue;
                ScheduleWindow window = GetScheduleWindow(i, now);
                if (!window.IsActive) continue;
                if (item.LastTriggeredAt >= window.Start && item.LastTriggeredAt < window.End) continue;
                if (selectedIndex < 0 || window.Start > selectedWindow.Start)
                {
                    selectedIndex = i;
                    selectedWindow = window;
                }
            }
            return selectedIndex;
        }

        private async Task CompleteInputTransitionAsync(BackendInputEvent inputEvent)
        {
            int stateGeneration = playbackStateGeneration;
            await ApplyPendingScheduledObsEndsAsync(false, inputEvent.FrameId);
            if (stateGeneration != playbackStateGeneration) return;
            if (pendingPlaybackTransition == null)
            {
                if (InputSummariesEqual(inputEvent.Input, currentStatusInput))
                    currentActualStatusInput = currentStatusInput;
                return;
            }
            BackendInputEvent? deferredEof = await ActivatePendingPlaybackAsync(inputEvent.FrameId);
            if (deferredEof.HasValue && stateGeneration == playbackStateGeneration)
                await AdvanceOnInputEofAsync(deferredEof.Value);
        }

        private async Task<BackendInputEvent?> ActivatePendingPlaybackAsync(long frameId)
        {
            PendingPlaybackTransition activation = pendingPlaybackTransition;
            if (activation == null) return null;
            if (activation.FrameId != 0 && frameId != 0 && activation.FrameId != frameId)
            {
                AppendLog(TF("LogIgnoredInputEnded", frameId, activation.FrameId));
                return null;
            }
            pendingPlaybackTransition = null;

            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            if (activation.Scheduled)
            {
                ScheduledPlaylistItem item = CloneScheduledItem(activation.ScheduledItem);
                if (item == null) return activation.DeferredTargetEof;
                int liveIndex = FindScheduledItemIndex(item.RuntimeId);
                if (activation.WindowStart != DateTime.MinValue)
                {
                    item.LastTriggeredAt = activation.WindowStart;
                    if (liveIndex >= 0) scheduledPlaylist[liveIndex].LastTriggeredAt = activation.WindowStart;
                }
                playingScheduled = true;
                currentScheduledIndex = liveIndex;
                currentScheduledItem = item;
                scheduledObsEndApplied = false;
                currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
                scheduledSlotEndAt = GetScheduleWindow(item, DateTime.Now).End;
                SelectCurrentRow();
                SelectCurrentScheduledRow();
                SetInputFields(item.Path, item.Title, item.Options);
                if (activation.ApplyObsStart)
                    await ApplyObsActionsAsync(item.ObsScene, item.ObsShowSources, item.ObsHideSources);
                RefreshScheduledPlaylistView();
            }
            else
            {
                PlaylistItem item = ClonePlaylistItem(activation.PlaylistItem);
                if (item == null) return activation.DeferredTargetEof;
                currentIndex = FindPlaylistItemIndex(item.RuntimeId);
                ActivatePlayNextOverrideIfNeeded(currentIndex);
                playingScheduled = false;
                currentScheduledIndex = -1;
                currentScheduledItem = null;
                scheduledSlotEndAt = DateTime.MinValue;
                currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
                SelectCurrentRow();
            }
            currentActualStatusInput = currentStatusInput;
            UpdatePlayoutStatusLabel();
            return activation.DeferredTargetEof;
        }

        private async Task RecoverTimedOutPendingPlaybackAsync()
        {
            if (pendingPlaybackTransition == null) return;
            bool sourceEnded = string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase);
            bool sourceFailed = string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            if (!sourceEnded && !sourceFailed) return;
            PendingPlaybackTransition activation = pendingPlaybackTransition;
            if (DateTime.UtcNow - activation.QueuedAtUtc < PendingPlaybackTimeout) return;

            pendingPlaybackTransition = null;
            AppendLog(T("LogPlayNextTransitionTimedOut"));
            await ApplyPendingScheduledObsEndsAsync(false, activation.FrameId);
            ClearQueuedPlaybackState();

            if (activation.Scheduled)
            {
                ScheduledPlaylistItem item = CloneScheduledItem(activation.ScheduledItem);
                if (item == null) return;
                int liveIndex = FindScheduledItemIndex(item.RuntimeId);
                if (activation.WindowStart != DateTime.MinValue)
                {
                    item.LastTriggeredAt = activation.WindowStart;
                    if (liveIndex >= 0) scheduledPlaylist[liveIndex].LastTriggeredAt = activation.WindowStart;
                }
                currentScheduledIndex = liveIndex;
                currentScheduledItem = item;
                playingScheduled = true;
                scheduledObsEndApplied = false;
                scheduledSlotEndAt = GetScheduleWindow(item, DateTime.Now).End;
                currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
                currentStatusState = "opening";
                if (activation.ApplyObsStart)
                    await ApplyObsActionsAsync(item.ObsScene, item.ObsShowSources, item.ObsHideSources);
                await PlayInputAsync(item.Path, item.Options);
            }
            else
            {
                PlaylistItem item = ClonePlaylistItem(activation.PlaylistItem);
                if (item == null) return;
                currentIndex = FindPlaylistItemIndex(item.RuntimeId);
                ActivatePlayNextOverrideIfNeeded(currentIndex);
                playingScheduled = false;
                currentScheduledIndex = -1;
                currentScheduledItem = null;
                scheduledSlotEndAt = DateTime.MinValue;
                currentStatusState = "opening";
                currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
                await PlayInputAsync(item.Path, item.Options);
            }
            RefreshScheduledPlaylistView();
            SelectCurrentRow();
            SelectCurrentScheduledRow();
            UpdatePlayoutStatusLabel();
        }

        private Task ApplyObsActionsAsync(ScheduledPlaylistItem item)
        {
            return ApplyObsActionsAsync(item.ObsScene, item.ObsShowSources, item.ObsHideSources, mainObsUrlBox.Text.Trim(), mainObsPasswordBox.Text);
        }

        private Task ApplyObsActionsAsync(ScheduledPlaylistItem item, string url, string password)
        {
            return ApplyObsActionsAsync(item.ObsScene, item.ObsShowSources, item.ObsHideSources, url, password);
        }

        private Task ApplyObsActionsAsync(string scene, string showSources, string hideSources)
        {
            return ApplyObsActionsAsync(scene, showSources, hideSources, mainObsUrlBox.Text.Trim(), mainObsPasswordBox.Text);
        }

        private async Task ApplyObsActionsAsync(string scene, string showSources, string hideSources, string url, string password)
        {
            if (!ObsOperationsEnabled) return;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (string.IsNullOrWhiteSpace(scene) && string.IsNullOrWhiteSpace(showSources) && string.IsNullOrWhiteSpace(hideSources)) return;
            try
            {
                using (var client = new ObsWebSocketClient(url, password))
                {
                    await client.ConnectAsync();
                    if (!ObsOperationsEnabled) return;
                    if (!string.IsNullOrWhiteSpace(scene))
                    {
                        AppendLog("> OBS SetCurrentProgramScene scene=" + FormatObsLogValue(scene));
                        await client.SendRequestAsync("SetCurrentProgramScene", new Dictionary<string, object> { ["sceneName"] = scene });
                        AppendLog("< OBS OK SetCurrentProgramScene");
                    }
                    foreach (string source in SplitObsSources(showSources))
                    {
                        if (!ObsOperationsEnabled) return;
                        AppendLog("> OBS SetSceneItemEnabled scene=" + FormatObsLogValue(scene) +
                            " source=" + FormatObsLogValue(source) + " enabled=true");
                        await client.SetSceneItemEnabledAsync(scene, source, true);
                        AppendLog("< OBS OK SetSceneItemEnabled");
                    }
                    foreach (string source in SplitObsSources(hideSources))
                    {
                        if (!ObsOperationsEnabled) return;
                        AppendLog("> OBS SetSceneItemEnabled scene=" + FormatObsLogValue(scene) +
                            " source=" + FormatObsLogValue(source) + " enabled=false");
                        await client.SetSceneItemEnabledAsync(scene, source, false);
                        AppendLog("< OBS OK SetSceneItemEnabled");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog(TF("ObsActionFailed", ex.Message));
            }
        }

        private static string FormatObsLogValue(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }

        private async Task ApplyScheduledObsEndAsync()
        {
            await ApplyPendingScheduledObsEndsAsync();
            if (!playingScheduled || scheduledObsEndApplied || currentScheduledItem == null) return;
            scheduledObsEndApplied = true;
            ScheduledPlaylistItem item = currentScheduledItem;
            await ApplyObsActionsAsync(item.ObsEndScene, item.ObsEndShowSources, item.ObsEndHideSources);
        }

        private void QueueScheduledObsEnd(ScheduledPlaylistItem item, long frameId)
        {
            if (item == null) return;
            if (playingScheduled && currentScheduledItem != null && item.RuntimeId == currentScheduledItem.RuntimeId)
                scheduledObsEndApplied = true;
            EnqueueScheduledObsEnd(item, frameId);
        }

        private void EnqueueScheduledObsEnd(ScheduledPlaylistItem item, long frameId)
        {
            if (!ObsOperationsEnabled) return;
            pendingScheduledObsEnds.Enqueue(new ScheduledObsAction
            {
                Scene = item.ObsEndScene,
                ShowSources = item.ObsEndShowSources,
                HideSources = item.ObsEndHideSources,
                Url = mainObsUrlBox.Text.Trim(),
                Password = mainObsPasswordBox.Text,
                FrameId = frameId
            });
        }

        private async Task ApplyPendingScheduledObsEndsAsync(bool applyAll = true, long frameId = 0)
        {
            int stateGeneration = playbackStateGeneration;
            if (!applyAll && pendingScheduledObsEnds.Count > 0)
            {
                ScheduledObsAction next = pendingScheduledObsEnds.Peek();
                if (next.FrameId != 0 && frameId != 0 && next.FrameId != frameId) return;
            }
            int count = applyAll ? pendingScheduledObsEnds.Count : Math.Min(1, pendingScheduledObsEnds.Count);
            while (count-- > 0 && pendingScheduledObsEnds.Count > 0)
            {
                if (stateGeneration != playbackStateGeneration) return;
                ScheduledObsAction action = pendingScheduledObsEnds.Dequeue();
                await ApplyObsActionsAsync(action.Scene, action.ShowSources, action.HideSources, action.Url, action.Password);
            }
        }

        private static IEnumerable<string> SplitObsSources(string text)
        {
            text = text ?? string.Empty;
            return text.Split(new[] { ObsSourceSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value.Length > 0);
        }

        private static string FormatObsSources(IEnumerable<string> sources)
        {
            string[] values = sources.Where(value => !string.IsNullOrEmpty(value)).ToArray();
            return values.Length == 0 ? string.Empty : ObsSourceSeparator + string.Join(ObsSourceSeparator, values);
        }

        private static string FormatObsAction(string scene, string showSources, string hideSources)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(scene)) parts.Add(T("ObsScene") + ": " + scene);
            string[] show = SplitObsSources(showSources).ToArray();
            if (show.Length > 0) parts.Add(T("ObsShowSources") + ": " + string.Join(", ", show));
            string[] hide = SplitObsSources(hideSources).ToArray();
            if (hide.Length > 0) parts.Add(T("ObsHideSources") + ": " + string.Join(", ", hide));
            return parts.Count == 0 ? "-" : string.Join("; ", parts);
        }

        private async Task<bool> StartDueScheduledItemAsync()
        {
            DateTime now = DateTime.Now;
            if (IsWaitingScheduledSlotActive(now)) return false;
            List<ScheduledPlaylistItem> active = scheduledPlaylist;
            int selectedIndex = FindDueScheduledIndex(now);

            if (selectedIndex < 0) return false;
            ScheduleWindow selectedWindow = GetScheduleWindow(selectedIndex, now);
            if (active[selectedIndex].StartAction == ScheduleStartAction.WaitCurrentItem && IsCurrentPlaybackActive()) return false;
            if (playingScheduled && IsCurrentPlaybackActive())
            {
                if (currentScheduledItem != null)
                {
                    ScheduleWindow currentWindow = GetScheduleWindow(currentScheduledItem, now);
                    if (selectedWindow.Start <= currentWindow.Start) return false;
                }
                if (active[selectedIndex].StartAction != ScheduleStartAction.StartImmediately) return false;
            }

            await ApplyScheduledObsEndAsync();
            currentScheduledIndex = selectedIndex;
            currentScheduledItem = CloneScheduledItem(active[selectedIndex]);
            await PlayScheduledCurrentAsync();
            return true;
        }

        private async Task HandleScheduledEndAsync(bool inputEof = false, long eofFrameId = 0,
            int scheduledIndex = -1, ScheduledPlaylistItem scheduledItem = null)
        {
            int index = scheduledIndex >= 0 ? scheduledIndex : currentScheduledIndex;
            ScheduledPlaylistItem item = scheduledItem ?? currentScheduledItem;
            if (item == null && index >= 0 && index < scheduledPlaylist.Count)
                item = CloneScheduledItem(scheduledPlaylist[index]);
            if (item == null) return;
            DateTime now = DateTime.Now;

            bool isCurrentSource = currentScheduledItem != null && item.RuntimeId == currentScheduledItem.RuntimeId;
            DateTime sourceSlotEndAt = isCurrentSource
                ? scheduledSlotEndAt : GetScheduleWindow(item, now).End;
            if (sourceSlotEndAt != DateTime.MinValue && now >= sourceSlotEndAt)
            {
                if (inputEof)
                    await QueueReturnToNormalPlaylistAsync(eofFrameId, item);
                else
                    await FinishScheduledSlotAsync();
                return;
            }

            if (item.HasEndTime && item.EndAction == ScheduleEndAction.WaitUntilEnd) return;
            bool ended = inputEof || string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase) || string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool durationReached = item.DurationSeconds > 0 && currentPositionSeconds >= item.DurationSeconds;
            if (!ended && !durationReached) return;

            if (item.HasEndTime && item.EndAction == ScheduleEndAction.ReplayUntilEnd)
            {
                if (inputEof)
                {
                    if (!await QueueNextInputAsync(item.Path, item.Options, true)) return;
                    pendingPlaybackTransition = PendingPlaybackTransition.ForScheduled(index, item, eofFrameId,
                        item.LastTriggeredAt, false);
                }
                else
                {
                    currentStartedAt = DateTime.Now;
                    currentPositionSeconds = 0;
                    currentDurationSeconds = 0;
                    currentStatusState = "playing";
                    currentStatusInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false));
                    await PlayInputAsync(item.Path, item.Options);
                }
                return;
            }

            if (inputEof)
            {
                if (await QueueWaitingScheduledActivationAsync(eofFrameId, item)) return;
            }

            if (inputEof)
                await QueueReturnToNormalPlaylistAsync(eofFrameId, item);
            else
                await ReturnToNormalPlaylistAsync();
        }

        private async Task AutoAdvanceScheduledIfNeededAsync()
        {
            if (currentScheduledItem == null) return;
            if ((DateTime.Now - lastAutoAdvanceAt).TotalSeconds < 2) return;
            ScheduledPlaylistItem item = currentScheduledItem;
            if (scheduledSlotEndAt != DateTime.MinValue) return;
            bool ended = string.Equals(currentStatusState, "ended", StringComparison.OrdinalIgnoreCase) || string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase);
            bool durationReached = item.DurationSeconds > 0 && currentPositionSeconds >= item.DurationSeconds;
            if (!ended && !durationReached) return;
            lastAutoAdvanceAt = DateTime.Now;
            await ReturnToNormalPlaylistAsync();
        }

        private async Task FinishScheduledSlotAsync()
        {
            await ApplyScheduledObsEndAsync();
            await SendCommandAsync("STOP");
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            scheduledSlotEndAt = DateTime.MinValue;
            currentStatusState = "stopped";
            currentStatusInput = string.Empty;
            currentActualStatusInput = string.Empty;
            RefreshScheduledPlaylistView();
            UpdatePlayoutStatusLabel();

            if (playlist.Count == 0) return;
            currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
            await PlayCurrentAsync();
        }

        private async Task QueueReturnToNormalPlaylistAsync(long endingFrameId, ScheduledPlaylistItem scheduledItem)
        {
            if (playlist.Count == 0) return;
            int returnIndex = TakePreferredNextPlaylistIndex(currentIndex, true);
            PlaylistItem item = playlist[returnIndex];
            ActivatePlayNextOverrideIfNeeded(returnIndex);
            if (!await QueueNextInputAsync(item.Path, item.Options, true)) return;
            QueueScheduledObsEnd(scheduledItem, endingFrameId);
            pendingPlaybackTransition = PendingPlaybackTransition.ForPlaylist(returnIndex, item, endingFrameId);
        }

        private async Task ReturnToNormalPlaylistAsync()
        {
            currentIndex = TakePreferredNextPlaylistIndex(currentIndex, true);
            ActivatePlayNextOverrideIfNeeded(currentIndex);
            Task obsEndTask = ApplyScheduledObsEndAsync();
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            scheduledSlotEndAt = DateTime.MinValue;
            RefreshScheduledPlaylistView();
            if (playlist.Count == 0)
            {
                await obsEndTask;
                UpdatePlayoutStatusLabel();
                return;
            }
            await PlayCurrentAsync();
            await obsEndTask;
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
            using (var dialog = new SaveFileDialog { Filter = T("PlaylistSaveFilter"), FileName = "playlist.ovcpl" })
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
                var root = new XElement("OmniVCamController",
                    new XAttribute("selectedTab", Math.Max(0, connectionTabs.SelectedIndex)),
                    new XElement("Tabs", connectionStates.Select(SaveConnectionTabElement)));
                ControllerLanguage.SetLanguageAttribute(root, languageBox == null ? ControllerLanguage.LoadSavedCultureName() : GetComboValue(languageBox));
                var document = new XDocument(
                    root);

                document.Save(GetAutoConfigPath());
            }
            catch (Exception ex)
            {
                AppendLog(TF("SaveConfigFailed", ex.Message));
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
                    new XAttribute("autoDeinterlace", state.AutoDeinterlace ? "1" : "0"),
                    new XAttribute("autoDeinterlaceFilter", state.AutoDeinterlaceFilter),
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
                    new XAttribute("scheduledItemEnabled", state.ScheduledItemEnabled ? "1" : "0"),
                    new XAttribute("scheduledTitle", state.ScheduledTitle),
                    new XAttribute("scheduledOptions", state.ScheduledOptions),
                    new XAttribute("obsEnabled", state.ObsEnabled ? "1" : "0"),
                    new XAttribute("obsUrl", state.ObsUrl),
                    new XAttribute("obsPassword", state.ObsPassword),
                    new XAttribute("obsScene", state.ObsScene),
                    new XAttribute("obsShowSources", state.ObsShowSources),
                    new XAttribute("obsHideSources", state.ObsHideSources),
                    new XAttribute("obsEndScene", state.ObsEndScene),
                    new XAttribute("obsEndShowSources", state.ObsEndShowSources),
                    new XAttribute("obsEndHideSources", state.ObsEndHideSources),
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
                        new XAttribute("enabled", item.Enabled ? "1" : "0"),
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
                        new XAttribute("startAction", item.StartAction),
                        new XAttribute("obsScene", item.ObsScene),
                        new XAttribute("obsShowSources", item.ObsShowSources),
                        new XAttribute("obsHideSources", item.ObsHideSources),
                        new XAttribute("obsEndScene", item.ObsEndScene),
                        new XAttribute("obsEndShowSources", item.ObsEndShowSources),
                        new XAttribute("obsEndHideSources", item.ObsEndHideSources)))),
                new XElement("FavoriteInputs",
                    state.FavoriteInputs.Select(item => new XElement("Item",
                        new XAttribute("input", item.Input),
                        new XAttribute("title", item.Title),
                        new XAttribute("options", item.Options)))),
                SaveInteractiveProjectGroupsElement(state));
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
                AppendLog(TF("LoadConfigFailed", ex.Message));
            }
        }

        private ConnectionTabState LoadLegacyConnectionState(XElement root)
        {
            var state = CreateDefaultConnectionState();
            playlist = state.Playlist;
            scheduledPlaylist = state.ScheduledPlaylist;
            favoriteInputs = state.FavoriteInputs;
            interactiveMedia = state.InteractiveMedia;
            interactiveRoutes = state.InteractiveRoutes;

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

            LoadInteractivePlaybackElement(root.Element("InteractivePlayback"));

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
            interactiveMedia = state.InteractiveMedia;
            interactiveRoutes = state.InteractiveRoutes;

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

            LoadInteractivePlaybackElement(tabElement.Element("InteractivePlayback"));

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

        private void LoadInteractivePlaybackElement(XElement element)
        {
            interactiveMedia.Clear();
            interactiveRoutes.Clear();
            if (element != null)
            {
                XElement mediaElement = element.Element("Media");
                if (mediaElement != null)
                    foreach (XElement item in mediaElement.Elements("Item"))
                        interactiveMedia.Add(LoadInteractiveMediaElement(item));
            }
            LoadInteractiveProjectGroups(element);
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
            autoDeinterlaceBox.Checked = GetAttribute(settings, "autoDeinterlace", autoDeinterlaceBox.Checked ? "1" : "0") == "1";
            string autoDeinterlaceFilter = GetAttribute(settings, "autoDeinterlaceFilter", autoDeinterlaceFilterBox.Text);
            if (autoDeinterlaceFilterBox.Items.Contains(autoDeinterlaceFilter)) autoDeinterlaceFilterBox.Text = autoDeinterlaceFilter;

            if (decimal.TryParse(GetAttribute(settings, "port", null), out decimalValue)) portBox.Value = Clamp(decimalValue, portBox.Minimum, portBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "videoIndex", null), out decimalValue)) videoIndexBox.Value = Clamp(decimalValue, videoIndexBox.Minimum, videoIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "audioIndex", null), out decimalValue)) audioIndexBox.Value = Clamp(decimalValue, audioIndexBox.Minimum, audioIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "shift", null), out decimalValue)) shiftBox.Value = Clamp(decimalValue, shiftBox.Minimum, shiftBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "seek", null), out decimalValue)) seekBox.Value = Clamp(decimalValue, seekBox.Minimum, seekBox.Maximum);

            byteSeekBox.Checked = GetAttribute(settings, "byteSeek", byteSeekBox.Checked ? "1" : "0") == "1";
            writeNowPlayingXmlBox.Checked = GetAttribute(settings, "writeNowPlayingXml", writeNowPlayingXmlBox.Checked ? "1" : "0") == "1";

            string playoutMode = GetAttribute(settings, "playoutMode", GetComboValue(playoutModeBox));
            if (ComboContainsValue(playoutModeBox, playoutMode)) SetComboValue(playoutModeBox, playoutMode);

            DateTime scheduled;
            if (DateTime.TryParse(GetAttribute(settings, "scheduledStart", null), out scheduled)) scheduledStartPicker.Value = scheduled;
            autoAdvanceBox.Checked = GetAttribute(settings, "autoAdvance", autoAdvanceBox.Checked ? "1" : "0") == "1";
            scheduledStartBox.Checked = GetAttribute(settings, "scheduledStartEnabled", scheduledStartBox.Checked ? "1" : "0") == "1";
            string scheduleType = GetAttribute(settings, "scheduleType", GetComboValue(scheduleTypeBox));
            if (ComboContainsValue(scheduleTypeBox, scheduleType)) SetComboValue(scheduleTypeBox, scheduleType);
            DateTime dateValue;
            if (DateTime.TryParse(GetAttribute(settings, "scheduleDateTime", null), out dateValue)) scheduleDateTimePicker.Value = ClampDateTimePicker(scheduleDateTimePicker, dateValue);
            if (DateTime.TryParse(GetAttribute(settings, "scheduleTime", null), out dateValue)) scheduleTimePicker.Value = ClampDateTimePicker(scheduleTimePicker, dateValue);
            scheduleEndBox.Checked = GetAttribute(settings, "scheduleEndEnabled", scheduleEndBox.Checked ? "1" : "0") == "1";
            if (DateTime.TryParse(GetAttribute(settings, "scheduleEnd", null), out dateValue)) scheduleEndPicker.Value = ClampDateTimePicker(scheduleEndPicker, dateValue);
            scheduledEnabledBox.Checked = GetAttribute(settings, "scheduledItemEnabled", "1") == "1";
            scheduledTitleBox.Text = GetAttribute(settings, "scheduledTitle", scheduledTitleBox.Text);
            scheduledOptionsBox.Text = GetAttribute(settings, "scheduledOptions", scheduledOptionsBox.Text);
            obsOperationsEnabledBox.Checked = GetAttribute(settings, "obsEnabled", "1") != "0";
            mainObsUrlBox.Text = GetAttribute(settings, "obsUrl", mainObsUrlBox.Text);
            mainObsPasswordBox.Text = GetAttribute(settings, "obsPassword", mainObsPasswordBox.Text);
            obsSceneBox.Text = GetAttribute(settings, "obsScene", obsSceneBox.Text);
            obsShowSourcesBox.Text = GetAttribute(settings, "obsShowSources", obsShowSourcesBox.Text);
            obsHideSourcesBox.Text = GetAttribute(settings, "obsHideSources", obsHideSourcesBox.Text);
            obsEndSceneBox.Text = GetAttribute(settings, "obsEndScene", obsEndSceneBox.Text);
            obsEndShowSourcesBox.Text = GetAttribute(settings, "obsEndShowSources", obsEndShowSourcesBox.Text);
            obsEndHideSourcesBox.Text = GetAttribute(settings, "obsEndHideSources", obsEndHideSourcesBox.Text);
            string endAction = GetAttribute(settings, "scheduleEndAction", GetComboValue(scheduleEndActionBox));
            if (ComboContainsValue(scheduleEndActionBox, endAction)) SetComboValue(scheduleEndActionBox, endAction);
            string startAction = GetAttribute(settings, "scheduleStartAction", GetComboValue(scheduleStartActionBox));
            if (ComboContainsValue(scheduleStartActionBox, startAction)) SetComboValue(scheduleStartActionBox, startAction);
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
                Title = GetAttribute(itemElement, "title", GetInputTitle(itemPath)),
                DurationSeconds = int.TryParse(GetAttribute(itemElement, "durationSeconds", null), out duration) ? duration : 0,
                Options = GetAttribute(itemElement, "options", string.Empty),
                IsBumper = GetAttribute(itemElement, "isBumper", "0") == "1"
            });
        }

        private void LoadScheduledPlaylistElement(XElement itemElement)
        {
            ScheduledPlaylistItem item = ParseScheduledPlaylistElement(itemElement);
            if (item != null) scheduledPlaylist.Add(item);
        }

        private ScheduledPlaylistItem ParseScheduledPlaylistElement(XElement itemElement)
        {
            string itemPath = GetAttribute(itemElement, "path", string.Empty);
            if (string.IsNullOrWhiteSpace(itemPath)) return null;

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

            return new ScheduledPlaylistItem
            {
                Enabled = GetAttribute(itemElement, "enabled", "1") == "1",
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
                ,ObsScene = GetAttribute(itemElement, "obsScene", string.Empty)
                ,ObsShowSources = GetAttribute(itemElement, "obsShowSources", string.Empty)
                ,ObsHideSources = GetAttribute(itemElement, "obsHideSources", string.Empty)
                ,ObsEndScene = GetAttribute(itemElement, "obsEndScene", string.Empty)
                ,ObsEndShowSources = GetAttribute(itemElement, "obsEndShowSources", string.Empty)
                ,ObsEndHideSources = GetAttribute(itemElement, "obsEndHideSources", string.Empty)
            };
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
                Title = parts.Length > 1 ? Unescape(parts[1]) : GetInputTitle(itemPath),
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
                string path = ResolveStatusInputPath(string.IsNullOrEmpty(currentActualStatusInput)
                    ? currentStatusInput : currentActualStatusInput);
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
                AppendLog(TF("SaveNowPlayingXmlFailed", ex.Message));
            }
        }

        private string GetCurrentPlaybackTitle(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (playingScheduled && currentScheduledItem != null && PathsEqual(currentScheduledItem.Path, path)) return currentScheduledItem.Title;
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
            using (var dialog = new OpenFileDialog { Filter = T("PlaylistFilter") })
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
                        Title = parts.Length > 1 ? Unescape(parts[1]) : GetInputTitle(Unescape(parts[0])),
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
            var rows = new List<ListViewItem>(playlist.Count);
            for (int i = 0; i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                bool isCurrent = !playingScheduled && InputSummariesEqual(
                    string.IsNullOrEmpty(currentActualStatusInput) ? currentStatusInput : currentActualStatusInput,
                    BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false)));
                var row = new ListViewItem((i + 1).ToString());
                row.SubItems.Add(isCurrent ? FormatState(currentStatusState) : string.Empty);
                row.SubItems.Add(item.IsBumper ? T("Bumper") : T("Program"));
                row.SubItems.Add(item.Title);
                row.SubItems.Add(FormatDuration(item.DurationSeconds));
                row.SubItems.Add(item.Options);
                row.SubItems.Add(item.Path);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(225, 245, 225);
                else if (item.IsBumper) row.BackColor = Color.FromArgb(245, 245, 230);
                rows.Add(row);
            }
            SynchronizeListViewRows(playlistView, rows);
            SelectCurrentRow();
        }

        private void RefreshScheduledPlaylistView()
        {
            var rows = new List<ListViewItem>(scheduledPlaylist.Count);
            for (int i = 0; i < scheduledPlaylist.Count; i++)
            {
                ScheduledPlaylistItem item = scheduledPlaylist[i];
                bool isCurrent = playingScheduled && i == currentScheduledIndex;
                ScheduleWindow window = GetScheduleWindow(i, DateTime.Now);
                var row = new ListViewItem((i + 1).ToString());
                row.SubItems.Add(isCurrent ? FormatState(currentStatusState) : FormatScheduledIdleStatus(item, window));
                row.SubItems.Add(item.Enabled ? T("Enabled") : T("Disabled"));
                row.SubItems.Add(item.Title);
                row.SubItems.Add(FormatDuration(item.DurationSeconds));
                row.SubItems.Add(FormatSchedule(item));
                row.SubItems.Add(FormatScheduleEnd(item));
                row.SubItems.Add(StartActionText(item.StartAction));
                row.SubItems.Add(EndActionText(item.EndAction));
                row.SubItems.Add(FormatObsAction(item.ObsScene, item.ObsShowSources, item.ObsHideSources));
                row.SubItems.Add(FormatObsAction(item.ObsEndScene, item.ObsEndShowSources, item.ObsEndHideSources));
                row.SubItems.Add(FormatLastTriggered(item.LastTriggeredAt));
                row.SubItems.Add(item.Path);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(220, 238, 255);
                else if (!item.Enabled)
                {
                    row.BackColor = Color.FromArgb(245, 245, 245);
                    row.ForeColor = SystemColors.GrayText;
                }
                else if (FormatScheduledIdleStatus(item, window) == T("Blocked")) row.BackColor = Color.FromArgb(255, 248, 220);
                rows.Add(row);
            }
            SynchronizeListViewRows(scheduledPlaylistView, rows);
            SelectCurrentScheduledRow();
            UpdatePlayoutStatusLabel();
        }

        private static void SynchronizeListViewRows(ListView view, IList<ListViewItem> desiredRows)
        {
            if (!ListViewRowsChanged(view, desiredRows)) return;
            view.BeginUpdate();
            try
            {
                int sharedCount = Math.Min(view.Items.Count, desiredRows.Count);
                for (int i = 0; i < sharedCount; i++)
                {
                    ListViewItem current = view.Items[i];
                    ListViewItem desired = desiredRows[i];
                    while (current.SubItems.Count < desired.SubItems.Count) current.SubItems.Add(string.Empty);
                    while (current.SubItems.Count > desired.SubItems.Count) current.SubItems.RemoveAt(current.SubItems.Count - 1);
                    for (int column = 0; column < desired.SubItems.Count; column++)
                    {
                        string text = desired.SubItems[column].Text;
                        if (!string.Equals(current.SubItems[column].Text, text, StringComparison.Ordinal))
                            current.SubItems[column].Text = text;
                    }
                    if (current.BackColor != desired.BackColor) current.BackColor = desired.BackColor;
                    if (current.ForeColor != desired.ForeColor) current.ForeColor = desired.ForeColor;
                }
                while (view.Items.Count > desiredRows.Count) view.Items.RemoveAt(view.Items.Count - 1);
                for (int i = view.Items.Count; i < desiredRows.Count; i++) view.Items.Add(desiredRows[i]);
            }
            finally
            {
                view.EndUpdate();
            }
        }

        private static bool ListViewRowsChanged(ListView view, IList<ListViewItem> desiredRows)
        {
            if (view.Items.Count != desiredRows.Count) return true;
            for (int i = 0; i < desiredRows.Count; i++)
            {
                ListViewItem current = view.Items[i];
                ListViewItem desired = desiredRows[i];
                if (current.SubItems.Count != desired.SubItems.Count || current.BackColor != desired.BackColor ||
                    current.ForeColor != desired.ForeColor) return true;
                for (int column = 0; column < desired.SubItems.Count; column++)
                {
                    if (!string.Equals(current.SubItems[column].Text, desired.SubItems[column].Text, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static void SetListViewText(ListViewItem row, int column, string value)
        {
            value = value ?? string.Empty;
            if (!string.Equals(row.SubItems[column].Text, value, StringComparison.Ordinal)) row.SubItems[column].Text = value;
        }

        private static void SetListViewBackColor(ListViewItem row, Color color)
        {
            if (row.BackColor != color) row.BackColor = color;
        }

        private void UpdatePlaylistStatusView()
        {
            string actualSummary = string.IsNullOrEmpty(currentActualStatusInput)
                ? currentStatusInput : currentActualStatusInput;
            for (int i = 0; i < playlistView.Items.Count && i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                bool isCurrent = !playingScheduled && InputSummariesEqual(actualSummary,
                    BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false)));
                ListViewItem row = playlistView.Items[i];
                SetListViewText(row, 1, isCurrent ? FormatState(currentStatusState) : string.Empty);
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) SetListViewBackColor(row, Color.FromArgb(255, 230, 230));
                else if (isCurrent) SetListViewBackColor(row, Color.FromArgb(225, 245, 225));
                else SetListViewBackColor(row, item.IsBumper ? Color.FromArgb(245, 245, 230) : SystemColors.Window);
            }
            for (int i = 0; i < scheduledPlaylistView.Items.Count && i < scheduledPlaylist.Count; i++)
            {
                bool isCurrent = playingScheduled && i == currentScheduledIndex;
                ListViewItem row = scheduledPlaylistView.Items[i];
                ScheduleWindow window = GetScheduleWindow(i, DateTime.Now);
                string idleStatus = FormatScheduledIdleStatus(scheduledPlaylist[i], window);
                SetListViewText(row, 1, isCurrent ? FormatState(currentStatusState) : idleStatus);
                SetListViewText(row, 2, scheduledPlaylist[i].Enabled ? T("Enabled") : T("Disabled"));
                SetListViewText(row, 11, FormatLastTriggered(scheduledPlaylist[i].LastTriggeredAt));
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) SetListViewBackColor(row, Color.FromArgb(255, 230, 230));
                else if (isCurrent) SetListViewBackColor(row, Color.FromArgb(220, 238, 255));
                else if (!scheduledPlaylist[i].Enabled) SetListViewBackColor(row, Color.FromArgb(245, 245, 245));
                else if (idleStatus == T("Blocked")) SetListViewBackColor(row, Color.FromArgb(255, 248, 220));
                else SetListViewBackColor(row, SystemColors.Window);
            }
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

        private async Task<string> SendRawCommandAsync(string command)
        {
            ObserveBackendConnectionLoss();
            string reply = await commandClient.SendAsync(hostBox.Text.Trim(), (int)portBox.Value, command);
            ObserveBackendConnectionLoss();
            return reply;
        }

        private async Task ShowFrameInfoAsync()
        {
            string reply = await SendRawCommandAsync("FRAME_INFO");
            AppendLog($"> FRAME_INFO\r\n< {reply}");
            if (reply == null || !reply.StartsWith("OK ", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, reply ?? "ERR no reply", T("FrameInfo"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new Form
            {
                Text = T("CurrentFrameInfoTitle"),
                Size = new Size(760, 560),
                MinimumSize = new Size(560, 360),
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                ShowIcon = false
            })
            using (var text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9.0f),
                Text = FormatFrameInfoReply(reply.Substring(3).Trim())
            })
            {
                dialog.Controls.Add(text);
                dialog.ShowDialog(this);
            }
        }

        private static string FormatFrameInfoReply(string payload)
        {
            var builder = new StringBuilder();
            foreach (string section in payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = section.Trim();
                if (trimmed.Length == 0) continue;
                int firstSpace = trimmed.IndexOf(' ');
                string title = firstSpace > 0 ? trimmed.Substring(0, firstSpace) : trimmed;
                string fields = firstSpace > 0 ? trimmed.Substring(firstSpace + 1) : string.Empty;
                builder.AppendLine(title);
                foreach (string part in fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    builder.Append("  ").AppendLine(part);
                }
                builder.AppendLine();
            }
            return builder.ToString().TrimEnd();
        }

        private async Task RefreshStatusAsync()
        {
            if (refreshingStatus) return;
            refreshingStatus = true;
            try
            {
                int statusGeneration = commandClient.Generation;
                int interactiveVersion = interactiveRuntimeVersion;
                int interactiveRevision = interactiveStatusRevision;
                ConnectionTabState statusState = activeState;
                string reply = await SendRawCommandAsync("STATUS");
                if (!ReferenceEquals(statusState, activeState)) return;
                if (statusGeneration != commandClient.Generation &&
                    (reply == null || !reply.StartsWith("OK seconds=", StringComparison.OrdinalIgnoreCase)))
                {
                    // Failed connection attempts also change the TCP generation. They still count toward
                    // the playback watchdog, which must be able to choose (and retain) the next item offline.
                    await ReconcileInteractiveBackendStatusAsync(null, interactiveRuntimeVersion, interactiveStatusRevision, true);
                    return;
                }
                await ReconcileInteractiveBackendStatusAsync(reply, interactiveVersion, interactiveRevision, true);
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
                currentDeliverNs = ParseStatusLong(reply, "deliver_ns=");
                currentDeliverAvgNs = ParseStatusLong(reply, "deliver_avg_ns=");
                string newStatusState = ParseStatusString(reply, "state=");
                bool terminalState = string.Equals(newStatusState, "ended", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(newStatusState, "error", StringComparison.OrdinalIgnoreCase);
                if (!terminalState) terminalStatusSince = DateTime.MinValue;
                else if (!string.Equals(currentStatusState, newStatusState, StringComparison.OrdinalIgnoreCase) || terminalStatusSince == DateTime.MinValue)
                    terminalStatusSince = DateTime.Now;
                currentStatusState = newStatusState;
                currentStatusInput = ParseStatusString(reply, "input=");
                currentActualStatusInput = ParseStatusString(reply, "actual_input=");
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
                deliverLabel.Text = TF("FrameDeliverBlockTime", currentDeliverNs / 1000000.0, currentDeliverAvgNs / 1000000.0);
                if (!ignoreStaleSeekStatus && !updatingProgress && !draggingProgress && currentDurationSeconds > 0)
                {
                    int progress = (int)Math.Max(0, Math.Min(ProgressMaximum, seconds * ProgressMaximum / currentDurationSeconds));
                    updatingProgress = true;
                    SetProgressValue(progress);
                    updatingProgress = false;
                }
                WriteNowPlayingXml();
            }
            }
            finally
            {
                refreshingStatus = false;
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
            int start = FindProtocolField(reply, key);
            if (start < 0) return string.Empty;
            start += key.Length;
            if (key.Equals("input=", StringComparison.OrdinalIgnoreCase)) return reply.Substring(start).Trim();
            int end = reply.IndexOf(' ', start);
            return (end >= 0 ? reply.Substring(start, end - start) : reply.Substring(start)).Trim();
        }

        private static int FindProtocolField(string text, string key)
        {
            int start = 0;
            while (start < text.Length)
            {
                int found = text.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return -1;
                if (found == 0 || char.IsWhiteSpace(text[found - 1])) return found;
                start = found + key.Length;
            }
            return -1;
        }

        private static string BuildInputSummary(string input, string options)
        {
            string value = string.IsNullOrWhiteSpace(options)
                ? (input ?? string.Empty)
                : (input ?? string.Empty) + "\t" + options;
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
            }
        }

        private static bool InputSummariesEqual(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveStatusInputPath(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return string.Empty;
            PlaylistItem normal = playlist.FirstOrDefault(item => InputSummariesEqual(summary,
                BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false))));
            if (normal != null) return normal.Path;
            ScheduledPlaylistItem scheduled = scheduledPlaylist.FirstOrDefault(item => InputSummariesEqual(summary,
                BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false))));
            return scheduled != null ? scheduled.Path : string.Empty;
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
            if (string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase)) return T("StatePlaying");
            if (string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase)) return T("StatePaused");
            if (string.Equals(state, "opening", StringComparison.OrdinalIgnoreCase)) return T("StateOpening");
            if (string.Equals(state, "ended", StringComparison.OrdinalIgnoreCase)) return T("StateEnded");
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)) return T("StateError");
            return T("StateStopped");
        }

        private static string FormatDuration(int seconds)
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : "--:--:--";
        }

        private void UpdatePlayoutStatusLabel()
        {
            string running = playoutRunning ? T("StatePlaying") : T("Stopped");
            string source = playingScheduled ? T("Scheduled") : T("NormalPlaylist");
            playoutStatusLabel.Text = TF("PlayoutStatus", running, source, FormatState(currentStatusState));
            playoutStatusLabel.ForeColor = playoutRunning ? Color.DarkGreen : SystemColors.ControlText;
            playoutStatusLabel.Font = new Font(playoutStatusLabel.Font,
                playoutRunning ? FontStyle.Bold : FontStyle.Regular);
        }

        private bool IsCurrentPlaybackActive()
        {
            return string.Equals(currentStatusState, "playing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currentStatusState, "paused", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(currentStatusState, "opening", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsWaitingScheduledSlotActive(DateTime now)
        {
            if (!playingScheduled || currentScheduledItem == null) return false;
            ScheduledPlaylistItem item = currentScheduledItem;
            return item.HasEndTime &&
                   item.EndAction == ScheduleEndAction.WaitUntilEnd &&
                   scheduledSlotEndAt != DateTime.MinValue &&
                   now < scheduledSlotEndAt;
        }

        private void UpdateManualPlayButtonText()
        {
            if (manualPlayButton == null) return;
            manualPlayButton.Text = string.Equals(currentStatusState, "playing", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(currentStatusState, "opening", StringComparison.OrdinalIgnoreCase)
                ? T("ManualPause")
                : T("ManualPlay");
        }

        private string FormatScheduledIdleStatus(ScheduledPlaylistItem item, ScheduleWindow window)
        {
            if (!item.Enabled) return T("Disabled");
            if (!string.IsNullOrEmpty(GetScheduledItemValidationError(item))) return T("Invalid");
            if (!window.IsActive) return T("Waiting");
            if (item.StartAction == ScheduleStartAction.WaitCurrentItem && IsCurrentPlaybackActive()) return T("Blocked");
            return T("InWindow");
        }

        private ScheduleWindow GetScheduleWindow(int index, DateTime now)
        {
            List<ScheduledPlaylistItem> active = scheduledPlaylist;
            if (index < 0 || index >= active.Count) return ScheduleWindow.Empty;
            return GetScheduleWindow(active[index], now);
        }

        private ScheduleWindow GetScheduleWindow(ScheduledPlaylistItem item, DateTime now)
        {
            if (item == null) return ScheduleWindow.Empty;
            DateTime start = GetCurrentScheduleStart(item, now);
            if (start == DateTime.MinValue) return ScheduleWindow.Empty;
            if (!string.IsNullOrEmpty(GetScheduledItemValidationError(item))) return ScheduleWindow.Empty;

            DateTime end = item.HasEndTime ? GetExplicitScheduleEnd(item, start) : GetNextScheduleStart(start);
            if (end <= start) end = DateTime.MaxValue;
            return new ScheduleWindow(start, end, now >= start && now < end);
        }

        private int FindScheduledItemIndex(Guid runtimeId)
        {
            if (runtimeId == Guid.Empty) return -1;
            for (int i = 0; i < scheduledPlaylist.Count; i++)
                if (scheduledPlaylist[i].RuntimeId == runtimeId) return i;
            return -1;
        }

        private void RefreshCurrentScheduledIndex()
        {
            currentScheduledIndex = currentScheduledItem == null
                ? -1 : FindScheduledItemIndex(currentScheduledItem.RuntimeId);
        }

        private int FindPlaylistItemIndex(Guid runtimeId)
        {
            if (runtimeId == Guid.Empty) return -1;
            for (int i = 0; i < playlist.Count; i++)
                if (playlist[i].RuntimeId == runtimeId) return i;
            return -1;
        }

        private static PlaylistItem ClonePlaylistItem(PlaylistItem item)
        {
            if (item == null) return null;
            return new PlaylistItem
            {
                RuntimeId = item.RuntimeId,
                Path = item.Path,
                Title = item.Title,
                DurationSeconds = item.DurationSeconds,
                Options = item.Options,
                IsBumper = item.IsBumper
            };
        }

        private static ScheduledPlaylistItem CloneScheduledItem(ScheduledPlaylistItem item)
        {
            if (item == null) return null;
            return new ScheduledPlaylistItem
            {
                RuntimeId = item.RuntimeId,
                Enabled = item.Enabled,
                Path = item.Path,
                Title = item.Title,
                DurationSeconds = item.DurationSeconds,
                Options = item.Options,
                ScheduleKind = item.ScheduleKind,
                StartAt = item.StartAt,
                WeeklyTime = item.WeeklyTime,
                WeekDays = item.WeekDays,
                HasEndTime = item.HasEndTime,
                EndAt = item.EndAt,
                EndAction = item.EndAction,
                StartAction = item.StartAction,
                LastTriggeredAt = item.LastTriggeredAt,
                ObsScene = item.ObsScene,
                ObsShowSources = item.ObsShowSources,
                ObsHideSources = item.ObsHideSources,
                ObsEndScene = item.ObsEndScene,
                ObsEndShowSources = item.ObsEndShowSources,
                ObsEndHideSources = item.ObsEndHideSources
            };
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
            List<ScheduledPlaylistItem> active = scheduledPlaylist;
            for (int i = 0; i < active.Count; i++)
            {
                if (!active[i].Enabled) continue;
                DateTime candidate = GetNextScheduleStartAfter(active[i], start);
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
            if (GetComboValue(scheduleEndActionBox) == "Replay until end") return ScheduleEndAction.ReplayUntilEnd;
            if (GetComboValue(scheduleEndActionBox) == "Continue immediately") return ScheduleEndAction.ContinueImmediately;
            return ScheduleEndAction.WaitUntilEnd;
        }

        private ScheduleStartAction GetSelectedStartAction()
        {
            return GetComboValue(scheduleStartActionBox) == "Wait current item" ? ScheduleStartAction.WaitCurrentItem : ScheduleStartAction.StartImmediately;
        }

        private static string EndActionText(ScheduleEndAction action)
        {
            if (action == ScheduleEndAction.ReplayUntilEnd) return T("ReplayUntilEnd");
            if (action == ScheduleEndAction.ContinueImmediately) return T("ContinueImmediately");
            return T("WaitUntilEnd");
        }

        private static string EndActionValue(ScheduleEndAction action)
        {
            if (action == ScheduleEndAction.ReplayUntilEnd) return "Replay until end";
            if (action == ScheduleEndAction.ContinueImmediately) return "Continue immediately";
            return "Wait until end";
        }

        private static string StartActionText(ScheduleStartAction action)
        {
            return action == ScheduleStartAction.WaitCurrentItem ? T("WaitCurrentItem") : T("StartImmediately");
        }

        private static string StartActionValue(ScheduleStartAction action)
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
            if (!item.HasEndTime) return T("NextSchedule");
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
            if ((mask & (int)WeekDayMask.Monday) != 0) names.Add(T("WeekMonday"));
            if ((mask & (int)WeekDayMask.Tuesday) != 0) names.Add(T("WeekTuesday"));
            if ((mask & (int)WeekDayMask.Wednesday) != 0) names.Add(T("WeekWednesday"));
            if ((mask & (int)WeekDayMask.Thursday) != 0) names.Add(T("WeekThursday"));
            if ((mask & (int)WeekDayMask.Friday) != 0) names.Add(T("WeekFriday"));
            if ((mask & (int)WeekDayMask.Saturday) != 0) names.Add(T("WeekSaturday"));
            if ((mask & (int)WeekDayMask.Sunday) != 0) names.Add(T("WeekSunday"));
            return names.Count == 0 ? T("NoDays") : string.Join(",", names);
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

        private async Task ProgressBar_MouseUpAsync(MouseEventArgs e)
        {
            if (!draggingProgress || e.Button != MouseButtons.Left) return;
            draggingProgress = false;
            progressBar.Capture = false;
            await SeekByProgressAsync(GetProgressValue());
        }

        private void SetProgressFromMouse(int mouseX)
        {
            if (updatingProgress) return;
            int progress = ProgressFromMouseX(mouseX);

            updatingProgress = true;
            SetProgressValue(progress);
            updatingProgress = false;
        }

        private int ProgressFromMouseX(int mouseX)
        {
            int width = Math.Max(1, progressBar.ClientSize.Width);
            double ratio = (double)Math.Max(0, Math.Min(width, mouseX)) / width;
            return (int)Math.Round(ratio * ProgressMaximum);
        }

        private const int ProgressMaximum = 10000;

        private int GetProgressValue()
        {
            return (int)Math.Round(progressBar.Value * ProgressMaximum);
        }

        private void SetProgressValue(int value)
        {
            progressBar.Value = Math.Max(0, Math.Min(ProgressMaximum, value)) / (float)ProgressMaximum;
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
                AppendLog(T("DurationUnknownByteSeek"));
                return;
            }
            long seconds = currentDurationSeconds * progress / ProgressMaximum;
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
            bool followTail = logBox.SelectionStart + logBox.SelectionLength >= logBox.TextLength;
            int selectionStart = logBox.SelectionStart;
            int selectionLength = logBox.SelectionLength;
            logBox.AppendText(pendingLogText.ToString());
            pendingLogText.Clear();
            lastLogFlushAt = DateTime.UtcNow;
            if (logBox.Lines.Length > LOG_MAX_LINES)
            {
                int linesToRemove = logBox.Lines.Length - LOG_MAX_LINES;
                var allLines = logBox.Lines;
                var keepLines = allLines.Skip(linesToRemove).ToArray();

                logBox.Lines = keepLines;
            }
            if (followTail)
            {
                logBox.SelectionStart = logBox.TextLength;
                logBox.SelectionLength = 0;
                logBox.ScrollToCaret();
            }
            else
            {
                logBox.SelectionStart = Math.Min(selectionStart, logBox.TextLength);
                logBox.SelectionLength = Math.Min(selectionLength, logBox.TextLength - logBox.SelectionStart);
            }
        }

        private sealed class TcpCommandClient : IDisposable
        {
            private const int CommandTimeoutMs = 5000;
            private const int ConnectTimeoutMs = 3000;
            private readonly Action<string, int> logCallback;
            private readonly Action<string, int> eventCallback;
            private readonly Action connectionClosedCallback;
            private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
            private ConcurrentQueue<TaskCompletionSource<string>> pendingReplies = new ConcurrentQueue<TaskCompletionSource<string>>();
            private readonly object sync = new object();
            private TcpClient client;
            private TcpClient connectingClient;
            private NetworkStream stream;
            private CancellationTokenSource cts;
            private Task readTask;
            private string host;
            private int port;
            private bool disposed;
            private int generation;
            private int connectionLossVersion;

            public TcpCommandClient(Action<string, int> logCallback, Action<string, int> eventCallback, Action connectionClosedCallback = null)
            {
                this.logCallback = logCallback;
                this.eventCallback = eventCallback;
                this.connectionClosedCallback = connectionClosedCallback;
            }

            public int Generation { get { return Volatile.Read(ref generation); } }
            public int ConnectionLossVersion { get { return Volatile.Read(ref connectionLossVersion); } }

            public void Reset()
            {
                Interlocked.Increment(ref generation);
                Disconnect();
            }

            public async Task<string> SendAsync(string host, int port, string command)
            {
                if (disposed) return "ERR client disposed";
                int requestGeneration = Generation;
                try
                {
                    requestGeneration = await EnsureConnectedAsync(host, port);
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await writeLock.WaitAsync();
                    try
                    {
                        NetworkStream requestStream;
                        CancellationToken requestToken;
                        lock (sync)
                        {
                            if (disposed || requestGeneration != generation || stream == null)
                                throw new OperationCanceledException("connection changed");
                            requestStream = stream;
                            requestToken = cts.Token;
                            pendingReplies.Enqueue(tcs);
                        }
                        byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                        await requestStream.WriteAsync(data, 0, data.Length, requestToken);
                        await requestStream.FlushAsync(requestToken);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                    Task finished = await Task.WhenAny(tcs.Task, Task.Delay(CommandTimeoutMs));
                    if (finished != tcs.Task)
                    {
                        tcs.TrySetResult("ERR command timeout");
                        DisconnectIfGeneration(requestGeneration);
                    }
                    return await tcs.Task;
                }
                catch (Exception ex)
                {
                    DisconnectIfGeneration(requestGeneration);
                    return "ERR " + ex.Message;
                }
            }

            private async Task<int> EnsureConnectedAsync(string host, int port)
            {
                lock (sync)
                {
                    if (client != null && client.Connected && string.Equals(this.host, host, StringComparison.OrdinalIgnoreCase) && this.port == port) return generation;
                }

                await writeLock.WaitAsync();
                TcpClient newClient = null;
                try
                {
                    lock (sync)
                    {
                        if (client != null && client.Connected && string.Equals(this.host, host, StringComparison.OrdinalIgnoreCase) && this.port == port) return generation;
                    }
                    Disconnect();
                    newClient = new TcpClient();
                    int connectGeneration;
                    lock (sync)
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(TcpCommandClient));
                        connectGeneration = generation;
                        connectingClient = newClient;
                    }
                    Task connectTask = newClient.ConnectAsync(host, port);
                    Task finished = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeoutMs));
                    if (finished != connectTask) throw new TimeoutException("connection timeout");
                    await connectTask;
                    lock (sync)
                    {
                        if (disposed || connectGeneration != generation)
                            throw new OperationCanceledException("connection was cancelled");
                        connectingClient = null;
                        client = newClient;
                        stream = client.GetStream();
                        cts = new CancellationTokenSource();
                        pendingReplies = new ConcurrentQueue<TaskCompletionSource<string>>();
                        this.host = host;
                        this.port = port;
                        int readGeneration = Interlocked.Increment(ref generation);
                        // Each reader owns its stream, token and replies. An old reader must never drain a new session's replies.
                        var readStream = stream;
                        var readToken = cts.Token;
                        var readReplies = pendingReplies;
                        readTask = Task.Run(() => ReadLoopAsync(readStream, readToken, readGeneration, readReplies));
                        return readGeneration;
                    }
                }
                finally
                {
                    lock (sync)
                    {
                        if (ReferenceEquals(connectingClient, newClient)) connectingClient = null;
                        if (newClient != null && !ReferenceEquals(client, newClient))
                        {
                            try { newClient.Close(); } catch { }
                        }
                    }
                    writeLock.Release();
                }
            }

            private async Task ReadLoopAsync(NetworkStream readStream, CancellationToken token, int readGeneration,
                ConcurrentQueue<TaskCompletionSource<string>> readReplies)
            {
                try
                {
                    using (var reader = new StreamReader(readStream, Encoding.UTF8))
                    {
                        while (!token.IsCancellationRequested)
                        {
                            string line = await reader.ReadLineAsync();
                            if (line == null || readGeneration != Generation) break;
                            if (line.StartsWith("LOG ", StringComparison.OrdinalIgnoreCase))
                            {
                                logCallback(ParseLogLine(line), readGeneration);
                                continue;
                            }
                            if (line.StartsWith("EVENT ", StringComparison.OrdinalIgnoreCase))
                            {
                                eventCallback?.Invoke(line.Substring(6).TrimStart(), readGeneration);
                                continue;
                            }
                            if (readReplies.TryDequeue(out TaskCompletionSource<string> tcs)) tcs.TrySetResult(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) logCallback("connection closed: " + ex.Message, readGeneration);
                }
                finally
                {
                    DisconnectIfGeneration(readGeneration);
                    while (readReplies.TryDequeue(out TaskCompletionSource<string> tcs)) tcs.TrySetResult("ERR connection closed");
                }
            }

            private static string ParseLogLine(string line)
            {
                const string marker = " message=";
                int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                return index >= 0 ? line.Substring(index + marker.Length) : line.Substring(4).TrimStart();
            }

            private void Disconnect(int? expectedGeneration = null)
            {
                TcpClient oldClient;
                TcpClient oldConnectingClient;
                CancellationTokenSource oldCts;
                ConcurrentQueue<TaskCompletionSource<string>> oldReplies;
                lock (sync)
                {
                    if (expectedGeneration.HasValue && expectedGeneration.Value != generation) return;
                    oldClient = client;
                    oldConnectingClient = connectingClient;
                    oldCts = cts;
                    oldReplies = pendingReplies;
                    if (oldClient != null || oldConnectingClient != null) Interlocked.Increment(ref generation);
                    client = null;
                    connectingClient = null;
                    stream = null;
                    cts = null;
                    pendingReplies = new ConcurrentQueue<TaskCompletionSource<string>>();
                    host = null;
                    port = 0;
                    if (oldClient != null) Interlocked.Increment(ref connectionLossVersion);
                }
                try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }
                try { oldConnectingClient?.Close(); } catch { }
                try { oldClient?.Close(); } catch { }
                while (oldReplies.TryDequeue(out TaskCompletionSource<string> tcs)) tcs.TrySetResult("ERR connection closed");
                oldCts?.Dispose();
                if (oldClient != null) connectionClosedCallback?.Invoke();
            }

            private void DisconnectIfGeneration(int expectedGeneration)
            {
                Disconnect(expectedGeneration);
            }

            public void Dispose()
            {
                disposed = true;
                Disconnect();
            }
        }
        private sealed class PlaylistItem
        {
            public Guid RuntimeId { get; set; } = Guid.NewGuid();
            public string Path { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int DurationSeconds { get; set; }
            public string Options { get; set; } = string.Empty;
            public bool IsBumper { get; set; }
        }

        private sealed class ScheduledPlaylistItem
        {
            public Guid RuntimeId { get; set; } = Guid.NewGuid();
            public bool Enabled { get; set; } = true;
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
            public string ObsScene { get; set; } = string.Empty;
            public string ObsShowSources { get; set; } = string.Empty;
            public string ObsHideSources { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
            public string ObsEndShowSources { get; set; } = string.Empty;
            public string ObsEndHideSources { get; set; } = string.Empty;
        }

        private sealed class ScheduledObsAction
        {
            public string Scene { get; set; } = string.Empty;
            public string ShowSources { get; set; } = string.Empty;
            public string HideSources { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public long FrameId { get; set; }
        }

        private enum BackendInputEventType
        {
            Started,
            Eof,
            Ended
        }

        private readonly struct BackendInputEvent
        {
            public BackendInputEvent(BackendInputEventType type, long frameId, string input)
            {
                Type = type;
                FrameId = frameId;
                Input = input ?? string.Empty;
            }

            public BackendInputEventType Type { get; }
            public long FrameId { get; }
            public string Input { get; }
        }

        private sealed class PendingPlaybackTransition
        {
            public bool Scheduled { get; private set; }
            public int Index { get; private set; }
            public long FrameId { get; private set; }
            public DateTime WindowStart { get; private set; }
            public bool ApplyObsStart { get; private set; }
            public PlaylistItem PlaylistItem { get; private set; }
            public ScheduledPlaylistItem ScheduledItem { get; private set; }
            public DateTime QueuedAtUtc { get; private set; }
            public string TargetInput { get; private set; } = string.Empty;
            public BackendInputEvent? DeferredTargetEof { get; set; }

            public static PendingPlaybackTransition ForPlaylist(int index, PlaylistItem item, long frameId)
            {
                return new PendingPlaybackTransition
                {
                    Index = index,
                    PlaylistItem = ClonePlaylistItem(item),
                    FrameId = frameId,
                    QueuedAtUtc = DateTime.UtcNow,
                    TargetInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false))
                };
            }

            public static PendingPlaybackTransition ForScheduled(int index, ScheduledPlaylistItem item, long frameId,
                DateTime windowStart, bool applyObsStart)
            {
                return new PendingPlaybackTransition
                {
                    Scheduled = true,
                    Index = index,
                    ScheduledItem = CloneScheduledItem(item),
                    FrameId = frameId,
                    WindowStart = windowStart,
                    ApplyObsStart = applyObsStart,
                    QueuedAtUtc = DateTime.UtcNow,
                    TargetInput = BuildInputSummary(item.Path, NormalizeQueueOptions(item.Path, item.Options, false))
                };
            }
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

        private sealed class LocalizedItem
        {
            public LocalizedItem(string value, string text)
            {
                Value = value;
                Text = text;
            }

            public string Value { get; private set; }
            public string Text { get; private set; }

            public override string ToString()
            {
                return Text;
            }
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
            public bool AutoDeinterlace { get; set; } = true;
            public string AutoDeinterlaceFilter { get; set; } = "bwdif";
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
            public bool ScheduledItemEnabled { get; set; } = true;
            public string ScheduledTitle { get; set; } = string.Empty;
            public string ScheduledOptions { get; set; } = string.Empty;
            public bool ObsEnabled { get; set; } = true;
            public string ObsUrl { get; set; } = "ws://127.0.0.1:4455";
            public string ObsPassword { get; set; } = string.Empty;
            public string ObsScene { get; set; } = string.Empty;
            public string ObsShowSources { get; set; } = string.Empty;
            public string ObsHideSources { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
            public string ObsEndShowSources { get; set; } = string.Empty;
            public string ObsEndHideSources { get; set; } = string.Empty;
            public string ScheduleEndAction { get; set; } = "Wait until end";
            public string ScheduleStartAction { get; set; } = "Start immediately";
            public int WeekDays { get; set; }
            public List<PlaylistItem> Playlist { get; set; } = new List<PlaylistItem>();
            public List<ScheduledPlaylistItem> ScheduledPlaylist { get; set; } = new List<ScheduledPlaylistItem>();
            public List<FavoriteInputItem> FavoriteInputs { get; set; } = new List<FavoriteInputItem>();
            public List<InteractiveMediaItem> InteractiveMedia { get; set; } = new List<InteractiveMediaItem>();
            public List<InteractiveRouteItem> InteractiveRoutes { get; set; } = new List<InteractiveRouteItem>();
            public List<InteractiveProjectGroup> InteractiveProjectGroups { get; set; } = new List<InteractiveProjectGroup>();
            public Guid ActiveInteractiveProjectGroupId { get; set; }
            public bool InteractiveGroupSwitchRandom { get; set; }
            public InteractiveHotkey InteractiveGroupSwitchHotkey { get; set; }
            public List<InteractiveGroupTransition> InteractiveGroupTransitions { get; set; } = new List<InteractiveGroupTransition>();
            public int CurrentIndex { get; set; } = -1;
            public int CurrentScheduledIndex { get; set; } = -1;
            public ScheduledPlaylistItem CurrentScheduledItem { get; set; }
            public bool PlayoutRunning { get; set; }
            public bool PlayingScheduled { get; set; }
            public bool ScheduledObsEndApplied { get; set; }
            public bool WaitingForScheduledStart { get; set; }
            public DateTime ScheduledSlotEndAt { get; set; } = DateTime.MinValue;
            public DateTime CurrentStartedAt { get; set; }
            public long CurrentPositionSeconds { get; set; }
            public long CurrentDurationSeconds { get; set; }
            public long CurrentSizeBytes { get; set; }
            public string CurrentStatusState { get; set; } = "stopped";
            public string CurrentStatusInput { get; set; } = string.Empty;
            public string CurrentActualStatusInput { get; set; } = string.Empty;
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
