using System.Drawing;
using System.Windows.Forms;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private TextBox hostBox;
        private NumericUpDown portBox;
        private TextBox inputBox;
        private TextBox optionsBox;
        private ComboBox hwDecodeBox;
        private ComboBox scaleModeBox;
        private ComboBox displayAspectBox;
        private TextBox videoFilterBox;
        private TextBox audioFilterBox;
        private NumericUpDown videoIndexBox;
        private NumericUpDown audioIndexBox;
        private NumericUpDown shiftBox;
        private NumericUpDown seekBox;
        private Label positionLabel;
        private TrackBar progressBar;
        private CheckBox byteSeekBox;
        private TextBox logBox;
        private ListView favoriteInputView;
        private ListView playlistView;
        private Label playoutStatusLabel;
        private ComboBox playoutModeBox;
        private CheckBox autoAdvanceBox;
        private CheckBox scheduledStartBox;
        private DateTimePicker scheduledStartPicker;
        private NumericUpDown defaultDurationBox;
        private SplitContainer rootSplitContainer;

        private void InitializeComponent()
        {
            hostBox = new TextBox();
            portBox = new NumericUpDown();
            inputBox = new TextBox();
            optionsBox = new TextBox();
            hwDecodeBox = new ComboBox();
            scaleModeBox = new ComboBox();
            displayAspectBox = new ComboBox();
            videoFilterBox = new TextBox();
            audioFilterBox = new TextBox();
            videoIndexBox = new NumericUpDown();
            audioIndexBox = new NumericUpDown();
            shiftBox = new NumericUpDown();
            seekBox = new NumericUpDown();
            positionLabel = new Label();
            progressBar = new TrackBar();
            byteSeekBox = new CheckBox();
            logBox = new TextBox();
            favoriteInputView = new ListView();

            playlistView = new ListView();
            playoutStatusLabel = new Label();
            playoutModeBox = new ComboBox();
            autoAdvanceBox = new CheckBox();
            scheduledStartBox = new CheckBox();
            scheduledStartPicker = new DateTimePicker();
            defaultDurationBox = new NumericUpDown();
            statusTimer = new Timer();
            playoutTimer = new Timer();

            rootSplitContainer = new SplitContainer();

            ((System.ComponentModel.ISupportInitialize)(portBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(videoIndexBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(audioIndexBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(shiftBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(seekBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(progressBar)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(defaultDurationBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(rootSplitContainer)).BeginInit();
            rootSplitContainer.SuspendLayout();
            SuspendLayout();

            Text = "OmniVCam Controller";
            MinimumSize = new Size(820, 600);
            Size = new Size(980, 680);
            Font = new Font("Segoe UI", 9F);

            hostBox.Text = "127.0.0.1";
            portBox.Minimum = 1;
            portBox.Maximum = 65535;
            portBox.Value = 16999;
            inputBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            optionsBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            hwDecodeBox.DropDownStyle = ComboBoxStyle.DropDown;
            scaleModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            displayAspectBox.DropDownStyle = ComboBoxStyle.DropDown;
            videoFilterBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            audioFilterBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            videoIndexBox.Minimum = -1;
            videoIndexBox.Maximum = 128;
            videoIndexBox.Value = -1;
            audioIndexBox.Minimum = -1;
            audioIndexBox.Maximum = 128;
            audioIndexBox.Value = -1;
            shiftBox.Minimum = 0;
            shiftBox.Maximum = 41700;
            shiftBox.Increment = 1500;
            shiftBox.Width = 90;
            seekBox.Maximum = 86400;
            seekBox.Width = 90;
            positionLabel.Text = "00:00:00 / --:--:--";
            positionLabel.AutoSize = true;
            positionLabel.Anchor = AnchorStyles.Left;
            positionLabel.Padding = new Padding(0, 4, 0, 0);
            progressBar.Minimum = 0;
            progressBar.Maximum = 10000;
            progressBar.TickFrequency = 1000;
            progressBar.SmallChange = 10;
            progressBar.LargeChange = 500;
            progressBar.Dock = DockStyle.Fill;
            byteSeekBox.Text = "Byte seek";
            byteSeekBox.AutoSize = true;
            byteSeekBox.Anchor = AnchorStyles.Left;
            byteSeekBox.Padding = new Padding(0, 4, 0, 0);
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            favoriteInputView.Dock = DockStyle.Fill;
            favoriteInputView.View = View.Details;
            favoriteInputView.FullRowSelect = true;
            favoriteInputView.GridLines = true;
            favoriteInputView.HideSelection = false;
            playlistView.Dock = DockStyle.Fill;
            playlistView.View = View.Details;
            playlistView.FullRowSelect = true;
            playlistView.GridLines = true;
            playlistView.HideSelection = false;
            playoutStatusLabel.Text = "Status: stopped";
            playoutStatusLabel.AutoSize = true;
            playoutStatusLabel.Padding = new Padding(6, 5, 0, 0);
            playoutModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            autoAdvanceBox.Text = "Auto";
            autoAdvanceBox.Checked = true;
            autoAdvanceBox.AutoSize = true;
            scheduledStartBox.Text = "Start at";
            scheduledStartBox.AutoSize = true;
            scheduledStartPicker.Format = DateTimePickerFormat.Time;
            scheduledStartPicker.ShowUpDown = true;
            scheduledStartPicker.Width = 90;
            defaultDurationBox.Minimum = 1;
            defaultDurationBox.Maximum = 86400;
            defaultDurationBox.Value = 1800;
            defaultDurationBox.Width = 80;
            statusTimer.Interval = 1000;
            playoutTimer.Interval = 1000;

            rootSplitContainer.Dock = DockStyle.Fill;
            rootSplitContainer.Orientation = Orientation.Horizontal;
            rootSplitContainer.SplitterDistance = 178;
            rootSplitContainer.Name = "rootSplitContainer";
            Controls.Add(rootSplitContainer);

            ((System.ComponentModel.ISupportInitialize)(portBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(videoIndexBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(audioIndexBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(shiftBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(seekBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(progressBar)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(defaultDurationBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(rootSplitContainer)).EndInit();
            rootSplitContainer.ResumeLayout(false);
            ResumeLayout(false);
        }
    }
}
