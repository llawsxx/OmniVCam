using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm : Form
    {
        private const string MediaFileFilter = "Media files|*.mp4;*.mov;*.mkv;*.ts;*.m2ts;*.avi;*.flv;*.wmv;*.mp3;*.wav;*.aac|All files|*.*";
        private const string AutoConfigFileName = "OmniVCamController.xml";

        private Timer statusTimer;
        private Timer playoutTimer;

        private readonly List<PlaylistItem> playlist = new List<PlaylistItem>();
        private readonly List<FavoriteInputItem> favoriteInputs = new List<FavoriteInputItem>();
        private readonly Random random = new Random();
        private int currentIndex = -1;
        private bool playoutRunning;
        private bool waitingForScheduledStart;
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


        public MainForm()
        {
            InitializeComponent();
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildManualPanel(rootSplitContainer.Panel1);
            BuildPlaylistPanel(rootSplitContainer.Panel2);
            hwDecodeBox.Items.AddRange(new object[] { "none", "dxva2", "d3d11va", "cuda", "qsv" });
            hwDecodeBox.Text = "none";
            playoutModeBox.Items.AddRange(new object[] { "Sequential", "Random" });
            playoutModeBox.SelectedIndex = 0;
            scheduledStartPicker.Value = DateTime.Today.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute).AddMinutes(1);
            LoadAutoConfig();
            controlsReady = true;

            statusTimer.Tick += async (_, __) => await RefreshStatusAsync();
            statusTimer.Start();
            playoutTimer.Tick += async (_, __) => await PlayoutTickAsync();
            playoutTimer.Start();
            FormClosing += (_, __) => SaveAutoConfig();
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
            AddRow(grid, "Input", CreateInputPicker(), "Options", optionsBox);
            AddRow(grid, "HW decode", CreateHwDecodeControl(), "Shift us", CreateShiftControl());
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

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            buttons.Controls.Add(MakeButton("Ping", async (_, __) => await SendCommandAsync("PING")));
            buttons.Controls.Add(MakeButton("Play", async (_, __) => await PlayManualAsync()));
            buttons.Controls.Add(MakeButton("Set video index", async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton("Set audio index", async (_, __) => await SendIndexesAsync()));
            buttons.Controls.Add(MakeButton("Reopen", async (_, __) => await SendCommandAsync("REOPEN")));
            buttons.Controls.Add(MakeButton("Stop", async (_, __) => await StopAllAsync()));
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

        private void BuildPlaylistPanel(Control parent)
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8, 0, 8, 8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            parent.Controls.Add(root);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 5) };
            toolbar.Controls.Add(MakeButton("Add files", async (_, __) => await AddFilesAsync()));
            toolbar.Controls.Add(MakeButton("Add folder", async (_, __) => await AddFolderAsync()));
            toolbar.Controls.Add(MakeButton("Add bumper", async (_, __) => await AddBumperAsync()));
            toolbar.Controls.Add(MakeButton("Remove", (_, __) => RemoveSelected()));
            toolbar.Controls.Add(MakeButton("Up", (_, __) => MoveSelected(-1)));
            toolbar.Controls.Add(MakeButton("Down", (_, __) => MoveSelected(1)));
            toolbar.Controls.Add(MakeButton("Set options", async (_, __) => await SetSelectedOptionsAsync()));
            toolbar.Controls.Add(MakeButton("Load", async (_, __) => await LoadPlaylistAsync()));
            toolbar.Controls.Add(MakeButton("Save", (_, __) => SavePlaylist()));
            toolbar.Controls.Add(MakeButton("Refresh durations", async (_, __) => await RefreshDurationsAsync()));
            toolbar.Controls.Add(new Label { Text = "Mode", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            toolbar.Controls.Add(playoutModeBox);
            toolbar.Controls.Add(playoutStatusLabel);
            toolbar.Controls.Add(scheduledStartBox);
            toolbar.Controls.Add(scheduledStartPicker);
            toolbar.Controls.Add(MakeButton("Start playout", async (_, __) => await StartPlayoutAsync()));
            toolbar.Controls.Add(MakeButton("Next", async (_, __) => await PlayNextAsync(true)));
            toolbar.Controls.Add(MakeButton("Stop playout", async (_, __) => await StopPlayoutAsync()));
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

        private async Task PlayInputAsync(string input, string options)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("Input is empty.");
                return;
            }
            await SendCommandAsync(string.IsNullOrWhiteSpace(options) ? "PLAY " + input : "PLAY " + input + "\t" + options);
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
            if (favoriteInputView.SelectedIndices.Count != 1) return;
            int index = favoriteInputView.SelectedIndices[0];
            if (index < 0 || index >= favoriteInputs.Count) return;
            inputBox.Text = favoriteInputs[index].Input;
            optionsBox.Text = favoriteInputs[index].Options;
        }

        private async Task PlaySelectedFavoriteInputAsync()
        {
            if (favoriteInputView.SelectedIndices.Count == 0) return;
            int index = favoriteInputView.SelectedIndices[0];
            if (index < 0 || index >= favoriteInputs.Count) return;
            FavoriteInputItem item = favoriteInputs[index];
            inputBox.Text = item.Input;
            optionsBox.Text = item.Options;
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

        private async Task AddFilesAsync()
        {
            using (var dialog = new OpenFileDialog { Multiselect = true, Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in dialog.FileNames) await AddPlaylistItemAsync(file, false, optionsBox.Text.Trim());
            }
            RefreshPlaylistView();
        }

        private async Task AddFolderAsync()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in Directory.GetFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories).Where(IsMediaFile))
                {
                    await AddPlaylistItemAsync(file, false, optionsBox.Text.Trim());
                }
            }
            RefreshPlaylistView();
        }

        private async Task AddBumperAsync()
        {
            using (var dialog = new OpenFileDialog { Filter = MediaFileFilter })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await AddPlaylistItemAsync(dialog.FileName, true, optionsBox.Text.Trim());
            }
            RefreshPlaylistView();
        }

        private async Task AddPlaylistItemAsync(string file, bool bumper, string options)
        {
            int duration = await GetDurationOrDefaultAsync(file, options);
            playlist.Add(new PlaylistItem
            {
                Path = file,
                Title = System.IO.Path.GetFileNameWithoutExtension(file),
                DurationSeconds = duration,
                Options = options,
                IsBumper = bumper
            });
        }

        private async Task<int> GetDurationOrDefaultAsync(string input, string options)
        {
            string command = string.IsNullOrWhiteSpace(options) ? "DURATION " + input : "DURATION " + input + "\t" + options;
            string reply = await SendRawCommandAsync(command);
            long duration = ParseStatusLong(reply ?? string.Empty, "duration=");
            if (duration > 0 && duration <= (long)defaultDurationBox.Maximum) return (int)duration;
            return (int)defaultDurationBox.Value;
        }

        private static bool IsMediaFile(string file)
        {
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            return new[] { ".mp4", ".mov", ".mkv", ".ts", ".m2ts", ".avi", ".flv", ".wmv", ".mp3", ".wav", ".aac" }.Contains(ext);
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
            if (playlistView.SelectedIndices.Count != 1) return;
            int index = playlistView.SelectedIndices[0];
            if (index < 0 || index >= playlist.Count) return;
            inputBox.Text = playlist[index].Path;
            optionsBox.Text = playlist[index].Options;
        }

        private async Task SetSelectedOptionsAsync()
        {
            if (playlistView.SelectedIndices.Count == 0) return;
            string options = optionsBox.Text.Trim();
            foreach (int index in playlistView.SelectedIndices.Cast<int>())
            {
                if (index < 0 || index >= playlist.Count) continue;
                playlist[index].Options = options;
                playlist[index].DurationSeconds = await GetDurationOrDefaultAsync(playlist[index].Path, options);
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
            if (playlist.Count == 0) return;
            playoutRunning = true;
            waitingForScheduledStart = scheduledStartBox.Checked;
            if (!waitingForScheduledStart)
            {
                currentIndex = currentIndex >= 0 && currentIndex < playlist.Count ? currentIndex : 0;
                await PlayCurrentAsync();
            }
            AppendLog("Playout started.");
        }

        private async Task StopPlayoutAsync()
        {
            playoutRunning = false;
            waitingForScheduledStart = false;
            await SendCommandAsync("STOP");
            AppendLog("Playout stopped.");
        }

        private async Task StopAllAsync()
        {
            playoutRunning = false;
            waitingForScheduledStart = false;
            await SendCommandAsync("STOP");
        }

        private async Task PlayoutTickAsync()
        {
            if (!playoutRunning || playlist.Count == 0) return;
            if (advancingPlayout) return;

            if (waitingForScheduledStart)
            {
                TimeSpan now = DateTime.Now.TimeOfDay;
                TimeSpan scheduled = scheduledStartPicker.Value.TimeOfDay;
                if (now.Hours == scheduled.Hours && now.Minutes == scheduled.Minutes && now.Seconds == scheduled.Seconds)
                {
                    waitingForScheduledStart = false;
                    currentIndex = 0;
                    await PlayCurrentAsync();
                }
                return;
            }

            await AutoAdvanceIfNeededAsync();
        }

        private async Task AutoAdvanceIfNeededAsync()
        {
            if (advancingPlayout) return;
            if (!autoAdvanceBox.Checked || currentIndex < 0 || currentIndex >= playlist.Count) return;
            if ((DateTime.Now - lastAutoAdvanceAt).TotalSeconds < 2) return;

            PlaylistItem current = playlist[currentIndex];
            bool statusMatchesCurrent = PathsEqual(current.Path, GetStatusInputPath(currentStatusInput));
            bool reachedPlaylistDuration = (DateTime.Now - currentStartedAt).TotalSeconds >= Math.Max(1, current.DurationSeconds);
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
            if (manual) playoutRunning = true;
            await PlayCurrentAsync();
        }

        private async Task PlayCurrentAsync()
        {
            if (currentIndex < 0 || currentIndex >= playlist.Count) return;
            PlaylistItem item = playlist[currentIndex];
            currentStartedAt = DateTime.Now;
            currentPositionSeconds = 0;
            currentDurationSeconds = 0;
            currentStatusState = "playing";
            currentStatusInput = item.Path;
            SelectCurrentRow();
            inputBox.Text = item.Path;
            optionsBox.Text = item.Options;
            await PlayInputAsync(item.Path, item.Options);
        }

        private void SelectCurrentRow()
        {
            foreach (ListViewItem row in playlistView.Items) row.Selected = false;
            if (currentIndex >= 0 && currentIndex < playlistView.Items.Count)
            {
                playlistView.Items[currentIndex].Selected = true;
                playlistView.Items[currentIndex].EnsureVisible();
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
                var document = new XDocument(
                    new XElement("OmniVCamController",
                        new XElement("Settings",
                            new XAttribute("host", hostBox.Text),
                            new XAttribute("port", portBox.Value),
                            new XAttribute("input", inputBox.Text),
                            new XAttribute("options", optionsBox.Text),
                            new XAttribute("hwDecode", hwDecodeBox.Text),
                            new XAttribute("videoFilter", videoFilterBox.Text),
                            new XAttribute("audioFilter", audioFilterBox.Text),
                            new XAttribute("videoIndex", videoIndexBox.Value),
                            new XAttribute("audioIndex", audioIndexBox.Value),
                            new XAttribute("shift", shiftBox.Value),
                            new XAttribute("seek", seekBox.Value),
                            new XAttribute("byteSeek", byteSeekBox.Checked ? "1" : "0"),
                            new XAttribute("playoutMode", playoutModeBox.Text),
                            new XAttribute("scheduledStart", scheduledStartPicker.Value.ToString("HH:mm:ss"))),
                        new XElement("Playlist",
                            playlist.Select(item => new XElement("Item",
                                new XAttribute("path", item.Path),
                                new XAttribute("title", item.Title),
                                new XAttribute("durationSeconds", item.DurationSeconds),
                                new XAttribute("options", item.Options),
                                new XAttribute("isBumper", item.IsBumper ? "1" : "0")))),
                        new XElement("FavoriteInputs",
                            favoriteInputs.Select(item => new XElement("Item",
                                new XAttribute("input", item.Input),
                                new XAttribute("title", item.Title),
                                new XAttribute("options", item.Options))))));

                document.Save(GetAutoConfigPath());
            }
            catch (Exception ex)
            {
                AppendLog("Save config failed: " + ex.Message);
            }
        }

        private void LoadAutoConfig()
        {
            string path = GetAutoConfigPath();
            if (!File.Exists(path)) return;

            try
            {
                playlist.Clear();
                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null) return;

                XElement settings = root.Element("Settings");
                if (settings != null) LoadSettingsElement(settings);

                XElement playlistElement = root.Element("Playlist");
                if (playlistElement != null)
                {
                    foreach (XElement itemElement in playlistElement.Elements("Item")) LoadPlaylistElement(itemElement);
                }

                XElement favoriteInputsElement = root.Element("FavoriteInputs");
                if (favoriteInputsElement != null)
                {
                    favoriteInputs.Clear();
                    foreach (XElement itemElement in favoriteInputsElement.Elements("Item")) LoadFavoriteInputElement(itemElement);
                }

                RefreshPlaylistView();
                RefreshFavoriteInputView();
            }
            catch (Exception ex)
            {
                AppendLog("Load config failed: " + ex.Message);
            }
        }

        private void LoadSettingsElement(XElement settings)
        {
            decimal decimalValue;

            hostBox.Text = GetAttribute(settings, "host", hostBox.Text);
            inputBox.Text = GetAttribute(settings, "input", inputBox.Text);
            optionsBox.Text = GetAttribute(settings, "options", optionsBox.Text);
            hwDecodeBox.Text = GetAttribute(settings, "hwDecode", hwDecodeBox.Text);
            videoFilterBox.Text = GetAttribute(settings, "videoFilter", videoFilterBox.Text);
            audioFilterBox.Text = GetAttribute(settings, "audioFilter", audioFilterBox.Text);

            if (decimal.TryParse(GetAttribute(settings, "port", null), out decimalValue)) portBox.Value = Clamp(decimalValue, portBox.Minimum, portBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "videoIndex", null), out decimalValue)) videoIndexBox.Value = Clamp(decimalValue, videoIndexBox.Minimum, videoIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "audioIndex", null), out decimalValue)) audioIndexBox.Value = Clamp(decimalValue, audioIndexBox.Minimum, audioIndexBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "shift", null), out decimalValue)) shiftBox.Value = Clamp(decimalValue, shiftBox.Minimum, shiftBox.Maximum);
            if (decimal.TryParse(GetAttribute(settings, "seek", null), out decimalValue)) seekBox.Value = Clamp(decimalValue, seekBox.Minimum, seekBox.Maximum);

            byteSeekBox.Checked = GetAttribute(settings, "byteSeek", byteSeekBox.Checked ? "1" : "0") == "1";

            string playoutMode = GetAttribute(settings, "playoutMode", playoutModeBox.Text);
            if (playoutModeBox.Items.Contains(playoutMode)) playoutModeBox.Text = playoutMode;

            DateTime scheduled;
            if (DateTime.TryParse(GetAttribute(settings, "scheduledStart", null), out scheduled)) scheduledStartPicker.Value = scheduled;
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
                DurationSeconds = int.TryParse(GetAttribute(itemElement, "durationSeconds", null), out duration) ? duration : (int)defaultDurationBox.Value,
                Options = GetAttribute(itemElement, "options", string.Empty),
                IsBumper = GetAttribute(itemElement, "isBumper", "0") == "1"
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
                DurationSeconds = parts.Length > 2 && int.TryParse(parts[2], out int duration) ? duration : (int)defaultDurationBox.Value,
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

        private async Task LoadPlaylistAsync()
        {
            using (var dialog = new OpenFileDialog { Filter = "OmniVCam playlist|*.ovcpl;*.txt|All files|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                playlist.Clear();
                foreach (string line in File.ReadAllLines(dialog.FileName, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) continue;
                    playlist.Add(new PlaylistItem
                    {
                        Path = Unescape(parts[0]),
                        Title = parts.Length > 1 ? Unescape(parts[1]) : System.IO.Path.GetFileNameWithoutExtension(Unescape(parts[0])),
                        DurationSeconds = parts.Length > 2 && int.TryParse(parts[2], out int duration) ? duration : (int)defaultDurationBox.Value,
                        Options = parts.Length > 3 ? Unescape(parts[3]) : string.Empty,
                        IsBumper = parts.Length > 4 && parts[4] == "1"
                    });
                }
            }
            currentIndex = -1;
            await RefreshDurationsAsync();
            RefreshPlaylistView();
        }

        private async Task RefreshDurationsAsync()
        {
            foreach (PlaylistItem item in playlist)
            {
                item.DurationSeconds = await GetDurationOrDefaultAsync(item.Path, item.Options);
            }
            RefreshPlaylistView();
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
                bool isCurrent = PathsEqual(item.Path, GetStatusInputPath(currentStatusInput));
                var row = new ListViewItem((i + 1).ToString());
                row.SubItems.Add(isCurrent ? FormatState(currentStatusState) : string.Empty);
                row.SubItems.Add(item.IsBumper ? "Bumper" : "Program");
                row.SubItems.Add(item.Title);
                row.SubItems.Add(TimeSpan.FromSeconds(item.DurationSeconds).ToString(@"hh\:mm\:ss"));
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

        private void UpdatePlaylistStatusView()
        {
            string statusPath = GetStatusInputPath(currentStatusInput);
            playlistView.BeginUpdate();
            for (int i = 0; i < playlistView.Items.Count && i < playlist.Count; i++)
            {
                PlaylistItem item = playlist[i];
                bool isCurrent = PathsEqual(item.Path, statusPath);
                ListViewItem row = playlistView.Items[i];
                row.SubItems[1].Text = isCurrent ? FormatState(currentStatusState) : string.Empty;
                if (isCurrent && string.Equals(currentStatusState, "error", StringComparison.OrdinalIgnoreCase)) row.BackColor = Color.FromArgb(255, 230, 230);
                else if (isCurrent) row.BackColor = Color.FromArgb(225, 245, 225);
                else row.BackColor = item.IsBumper ? Color.FromArgb(245, 245, 230) : SystemColors.Window;
            }
            playlistView.EndUpdate();
        }

        private async Task SendCommandAsync(string command)
        {
            string reply = await SendRawCommandAsync(command);
            AppendLog($"> {command}\r\n< {reply}");
        }

        private async Task<string> SendRawCommandAsync(string command)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(hostBox.Text.Trim(), (int)portBox.Value);
                    using (NetworkStream stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                        await stream.WriteAsync(data, 0, data.Length);
                        return await reader.ReadLineAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                return "ERR " + ex.Message;
            }
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
                playoutStatusLabel.Text = "Status: " + FormatState(currentStatusState);
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
                if (!ignoreStaleSeekStatus) await AutoAdvanceIfNeededAsync();
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
            if (string.Equals(state, "opening", StringComparison.OrdinalIgnoreCase)) return "Opening";
            if (string.Equals(state, "ended", StringComparison.OrdinalIgnoreCase)) return "Ended";
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)) return "Error";
            return "Stopped";
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

        private void AppendLog(string text)
        {
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + text + Environment.NewLine);
        }

        private sealed class PlaylistItem
        {
            public string Path { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int DurationSeconds { get; set; }
            public string Options { get; set; } = string.Empty;
            public bool IsBumper { get; set; }
        }

        private sealed class FavoriteInputItem
        {
            public string Input { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
        }
    }
}
