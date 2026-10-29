using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private sealed partial class InteractivePlaybackForm
        {
            private const int DefaultSwitchHotkeyId = 0x6F00;
            private const int DefaultHotkeyBaseId = 0x7000;
            private bool defaultSwitchHotkeyRegistered;
            private readonly Dictionary<int, InteractiveDefaultPlaybackItem> registeredDefaults = new Dictionary<int, InteractiveDefaultPlaybackItem>();
            private readonly FlowLayoutPanel defaultPlaybackButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            private Button switchDefaultButton;

            private Control BuildDefaultPlaybackPanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveDefaultPlaybacks"), AutoSize = true, Font = activeGroupFont, Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("InteractiveDefaultSettings"), (_, __) => EditDefaultPlaybacks()));
                switchDefaultButton = MakeButton(T("InteractiveDefaultSwitch"), (_, __) => owner.SwitchNextInteractiveDefault(T("InteractiveDefaultSwitch")));
                bar.Controls.Add(switchDefaultButton);
                panel.Controls.Add(bar, 0, 0); panel.Controls.Add(defaultPlaybackButtons, 0, 1);
                EnableDoubleBuffering(panel); EnableDoubleBuffering(defaultPlaybackButtons);
                return panel;
            }

            public void RefreshDefaultPlaybackButtons()
            {
                if (IsDisposed) return;
                var group = owner.CurrentInteractiveProjectGroup;
                var selected = SelectedInteractiveDefault(group);
                switchDefaultButton.Text = T("InteractiveDefaultSwitch") + " · " + T(group.DefaultSwitchRandom ? "InteractiveRandom" : "InteractiveSequential") +
                    (group.DefaultSwitchHotkey == null ? string.Empty : "  [" + FormatHotkey(group.DefaultSwitchHotkey) + "]");
                switchDefaultButton.Enabled = group.DefaultPlaybacks.Count > 1;
                defaultPlaybackButtons.SuspendLayout();
                try
                {
                    while (defaultPlaybackButtons.Controls.Count > group.DefaultPlaybacks.Count)
                        defaultPlaybackButtons.Controls[defaultPlaybackButtons.Controls.Count - 1].Dispose();
                    for (int index = 0; index < group.DefaultPlaybacks.Count; index++)
                    {
                        var item = group.DefaultPlaybacks[index]; Button button;
                        if (index < defaultPlaybackButtons.Controls.Count) button = (Button)defaultPlaybackButtons.Controls[index];
                        else
                        {
                            button = MakeButton(string.Empty, (sender, _) => owner.SwitchInteractiveDefault((InteractiveDefaultPlaybackItem)((Button)sender).Tag, T("InteractiveDefaultSwitch")));
                            defaultPlaybackButtons.Controls.Add(button);
                        }
                        button.Tag = item;
                        button.Text = DefaultPlaybackButtonText(item);
                        bool active = selected?.Id == item.Id;
                        button.BackColor = active ? Color.LightSkyBlue : SystemColors.Control;
                        button.UseVisualStyleBackColor = !active; button.Font = active ? activeGroupFont : Font;
                    }
                }
                finally { defaultPlaybackButtons.ResumeLayout(true); }
            }

            private string DefaultPlaybackButtonText(InteractiveDefaultPlaybackItem item)
            {
                string countdown = owner.InteractiveDefaultCountdown(item);
                return owner.InteractiveDefaultName(item) + (item.Hotkey == null ? string.Empty : "  [" + FormatHotkey(item.Hotkey) + "]") +
                    (countdown.Length == 0 ? string.Empty : "  · " + countdown);
            }

            public void RefreshDefaultPlaybackCountdown()
            {
                if (IsDisposed) return;
                foreach (Button button in defaultPlaybackButtons.Controls)
                {
                    var item = button.Tag as InteractiveDefaultPlaybackItem;
                    if (item == null) continue;
                    string text = DefaultPlaybackButtonText(item);
                    if (button.Text != text) button.Text = text;
                }
            }

            private void EditDefaultPlaybacks()
            {
                var group = owner.CurrentInteractiveProjectGroup;
                Guid before = SelectedInteractiveDefault(group)?.Id ?? Guid.Empty;
                using (var dialog = new DefaultPlaybackSettingsDialog(owner.interactiveMedia, group))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var edited = new InteractiveProjectGroup { Hotkey = group.Hotkey }; dialog.Apply(edited);
                    if (owner.InteractiveDefaultHotkeysConflict(edited, group)) { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    CopyInteractiveDefaults(edited, group);
                }
                RefreshDefaultPlaybackButtons(); RegisterRouteHotkeys(); owner.SaveActiveConnectionState();
                owner.RefreshInteractiveDefaultAfterSettings(before);
            }

            private void RegisterDefaultPlaybackHotkeys()
            {
                var group = owner.CurrentInteractiveProjectGroup;
                var hotkey = group.DefaultSwitchHotkey;
                if (hotkey != null && hotkey.KeyCode != Keys.None)
                {
                    defaultSwitchHotkeyRegistered = RegisterHotKey(Handle, DefaultSwitchHotkeyId, (uint)hotkey.Modifiers | 0x4000, (uint)hotkey.KeyCode);
                    if (!defaultSwitchHotkeyRegistered) AppendOperation(T("InteractiveHotkeyRegisterFailed") + FormatHotkey(hotkey) + " - " + T("InteractiveDefaultSwitch"));
                }
                int index = 0;
                foreach (var item in group.DefaultPlaybacks)
                {
                    if (item.Hotkey == null || item.Hotkey.KeyCode == Keys.None) continue;
                    int id = DefaultHotkeyBaseId + index++;
                    if (RegisterHotKey(Handle, id, (uint)item.Hotkey.Modifiers, (uint)item.Hotkey.KeyCode)) registeredDefaults[id] = item;
                    else AppendOperation(T("InteractiveHotkeyRegisterFailed") + FormatHotkey(item.Hotkey) + " - " + owner.InteractiveDefaultName(item));
                }
            }

            private void UnregisterDefaultPlaybackHotkeys()
            {
                if (IsHandleCreated)
                {
                    if (defaultSwitchHotkeyRegistered) UnregisterHotKey(Handle, DefaultSwitchHotkeyId);
                    foreach (int id in registeredDefaults.Keys.ToList()) UnregisterHotKey(Handle, id);
                }
                defaultSwitchHotkeyRegistered = false; registeredDefaults.Clear();
            }

            private bool HandleDefaultPlaybackHotkey(Message message)
            {
                if (message.Msg != 0x0312 || !Visible || !owner.interactiveWindowOpen || owner.closingMainForm || owner.interactiveHotkeysDisabled) return false;
                int id = message.WParam.ToInt32();
                if (defaultSwitchHotkeyRegistered && id == DefaultSwitchHotkeyId)
                {
                    owner.SwitchNextInteractiveDefault(T("InteractiveHotkeySource") + FormatHotkey(owner.CurrentInteractiveProjectGroup.DefaultSwitchHotkey));
                    return true;
                }
                InteractiveDefaultPlaybackItem item;
                if (!registeredDefaults.TryGetValue(id, out item)) return false;
                owner.SwitchInteractiveDefault(item, T("InteractiveHotkeySource") + FormatHotkey(item.Hotkey));
                return true;
            }
        }

        private sealed class DefaultPlaybackSettingsDialog : Form
        {
            private readonly List<InteractiveMediaItem> media;
            private readonly InteractiveProjectGroup settings = new InteractiveProjectGroup();
            private readonly ListView itemsView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            private readonly ComboBox returnDefaultBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, DropDownWidth = 500 };
            private readonly ComboBox modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            private readonly HotkeyTextBox switchHotkeyBox = new HotkeyTextBox { Width = 180 };
            public void Apply(InteractiveProjectGroup group)
            {
                settings.DefaultSwitchRandom = modeBox.SelectedIndex == 1; settings.DefaultSwitchHotkey = switchHotkeyBox.Value?.Clone();
                CopyInteractiveDefaults(settings, group);
            }

            public DefaultPlaybackSettingsDialog(List<InteractiveMediaItem> media, InteractiveProjectGroup group)
            {
                this.media = media; CopyInteractiveDefaults(group, settings);
                Text = T("InteractiveDefaultSettings"); ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                Size = new Size(1000, 650); MinimumSize = new Size(800, 600);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(MakeButton(T("InteractiveDefaultAdd"), (_, __) => EditItem(true)));
                bar.Controls.Add(MakeButton(T("InteractiveEdit"), (_, __) => EditItem(false)));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveItem()));
                bar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveItem(-1))); bar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveItem(1)));
                bar.Controls.Add(MakeButton(T("InteractiveDefaultSelect"), (_, __) =>
                {
                    var item = Selected(); if (item == null) return; settings.ActiveDefaultPlaybackId = item.Id; RefreshItems(item);
                }));
                root.Controls.Add(bar, 0, 0);
                itemsView.Columns.Add(T("InteractiveName"), 150); itemsView.Columns.Add(T("InteractiveMedia"), 180);
                itemsView.Columns.Add(T("InteractiveHotkey"), 120); itemsView.Columns.Add(T("InteractiveDefaultLoopMiddle"), 165);
                itemsView.Columns.Add(T("InteractiveDefaultSwitchInterruption"), 230);
                itemsView.Columns.Add(T("InteractiveDefaultAutoSwitchTime"), 160);
                itemsView.DoubleClick += (_, __) => EditItem(false); root.Controls.Add(itemsView, 0, 1);
                var switching = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                switching.Controls.Add(new Label { Text = T("InteractiveDefaultSwitchMode"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                modeBox.Items.AddRange(new object[] { T("InteractiveSequential"), T("InteractiveRandom") });
                modeBox.SelectedIndex = settings.DefaultSwitchRandom ? 1 : 0; switching.Controls.Add(modeBox);
                switching.Controls.Add(new Label { Text = T("InteractiveHotkey"), AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
                switchHotkeyBox.Value = settings.DefaultSwitchHotkey?.Clone(); switching.Controls.Add(switchHotkeyBox); root.Controls.Add(switching, 0, 2);
                var returning = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                returning.Controls.Add(new Label { Text = T("InteractiveDefaultReturnAfterProject"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                returning.Controls.Add(returnDefaultBox);
                returnDefaultBox.SelectedIndexChanged += (_, __) => settings.ReturnDefaultPlaybackId =
                    (returnDefaultBox.SelectedItem as DefaultPlaybackChoice)?.Item.Id ?? Guid.Empty;
                root.Controls.Add(returning, 0, 3);
                var hint = new Label { Text = T("InteractiveDefaultListHint"), AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(900, 0), Padding = new Padding(0, 8, 0, 8) };
                root.Controls.Add(hint, 0, 4);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 5);
                Controls.Add(root); AcceptButton = ok; CancelButton = cancel; RefreshItems(null);
            }

            private InteractiveDefaultPlaybackItem Selected() => itemsView.SelectedItems.Count == 1 ? (InteractiveDefaultPlaybackItem)itemsView.SelectedItems[0].Tag : null;
            private void RefreshItems(InteractiveDefaultPlaybackItem selected)
            {
                itemsView.BeginUpdate(); itemsView.Items.Clear();
                foreach (var item in settings.DefaultPlaybacks)
                {
                    var library = media.FirstOrDefault(entry => entry.RuntimeId == item.MediaId);
                    var row = new ListViewItem((SelectedInteractiveDefault(settings)?.Id == item.Id ? "● " : string.Empty) +
                        (string.IsNullOrWhiteSpace(item.Name) ? library?.Title ?? T("InteractiveNoDefaultMedia") : item.Name)) { Tag = item };
                    row.SubItems.Add(library?.Title ?? T("InteractiveNoDefaultMedia")); row.SubItems.Add(FormatHotkey(item.Hotkey));
                    row.SubItems.Add(item.LoopMiddleFiles ? T("InteractiveDefaultEnabled") : T("InteractiveDefaultDisabled"));
                    row.SubItems.Add(T(item.InterruptionMode.HasValue ? InteractiveGroupInterruptionKey(item.InterruptionMode.Value) : "InteractiveDefaultInheritInterruption"));
                    row.SubItems.Add(item.AutoSwitchEnabled ? item.AutoSwitchMinimumSeconds == item.AutoSwitchMaximumSeconds ?
                        string.Format(T("InteractiveDefaultSeconds"), item.AutoSwitchMinimumSeconds) :
                        string.Format(T("InteractiveDefaultSecondsRange"), item.AutoSwitchMinimumSeconds, item.AutoSwitchMaximumSeconds) : T("InteractiveDefaultDisabled"));
                    itemsView.Items.Add(row); if (selected == item) row.Selected = true;
                }
                itemsView.EndUpdate();
                Guid returnId = settings.ReturnDefaultPlaybackId;
                returnDefaultBox.BeginUpdate(); returnDefaultBox.Items.Clear();
                returnDefaultBox.Items.Add(T("InteractiveDefaultReturnKeepSelection"));
                foreach (var item in settings.DefaultPlaybacks)
                {
                    var library = media.FirstOrDefault(entry => entry.RuntimeId == item.MediaId);
                    returnDefaultBox.Items.Add(new DefaultPlaybackChoice(item, string.IsNullOrWhiteSpace(item.Name) ? library?.Title ?? T("InteractiveNoDefaultMedia") : item.Name));
                }
                int returnIndex = settings.DefaultPlaybacks.FindIndex(item => item.Id == returnId);
                returnDefaultBox.SelectedIndex = returnIndex + 1;
                returnDefaultBox.EndUpdate();
            }
            private sealed class DefaultPlaybackChoice
            {
                public readonly InteractiveDefaultPlaybackItem Item;
                private readonly string text;
                public DefaultPlaybackChoice(InteractiveDefaultPlaybackItem item, string text) { Item = item; this.text = text; }
                public override string ToString() => text;
            }
            private void EditItem(bool create)
            {
                var item = create ? new InteractiveDefaultPlaybackItem() : Selected(); if (item == null) return;
                using (var dialog = new DefaultPlaybackEditDialog(media, item))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    dialog.Apply(item);
                }
                if (create) settings.DefaultPlaybacks.Add(item);
                if (settings.ActiveDefaultPlaybackId == Guid.Empty) settings.ActiveDefaultPlaybackId = item.Id;
                RefreshItems(item);
            }
            private void RemoveItem()
            {
                var item = Selected(); if (item == null) return;
                settings.DefaultPlaybacks.Remove(item); settings.ActiveDefaultPlaybackId = SelectedInteractiveDefault(settings)?.Id ?? Guid.Empty; RefreshItems(null);
            }
            private void MoveItem(int direction)
            {
                var item = Selected(); if (item == null) return;
                int before = settings.DefaultPlaybacks.IndexOf(item), after = before + direction;
                if (after < 0 || after >= settings.DefaultPlaybacks.Count) return;
                settings.DefaultPlaybacks.RemoveAt(before); settings.DefaultPlaybacks.Insert(after, item); RefreshItems(item);
            }
        }

        private sealed class DefaultPlaybackEditDialog : Form
        {
            private readonly TextBox nameBox = new TextBox();
            private readonly ComboBox mediaBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            private readonly HotkeyTextBox hotkeyBox = new HotkeyTextBox();
            private readonly CheckBox loopMiddleBox = new CheckBox { AutoSize = true };
            private readonly CheckBox autoSwitchBox = new CheckBox { AutoSize = true };
            private readonly NumericUpDown minimumSecondsBox = new NumericUpDown { Minimum = 1, Maximum = MaximumInteractiveDefaultSeconds, Width = 100 };
            private readonly NumericUpDown maximumSecondsBox = new NumericUpDown { Minimum = 1, Maximum = MaximumInteractiveDefaultSeconds, Width = 100 };
            private readonly ComboBox interruptionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            public void Apply(InteractiveDefaultPlaybackItem item)
            {
                item.Name = nameBox.Text.Trim(); item.MediaId = ((MediaChoice)mediaBox.SelectedItem).Item.RuntimeId;
                item.Hotkey = hotkeyBox.Value?.Clone(); item.LoopMiddleFiles = loopMiddleBox.Checked;
                item.AutoSwitchEnabled = autoSwitchBox.Checked;
                item.AutoSwitchMinimumSeconds = (int)minimumSecondsBox.Value;
                item.AutoSwitchMaximumSeconds = (int)maximumSecondsBox.Value;
                item.InterruptionMode = interruptionBox.SelectedIndex == 0 ? (InteractiveGroupInterruptionMode?)null : (InteractiveGroupInterruptionMode)(interruptionBox.SelectedIndex - 1);
            }
            public DefaultPlaybackEditDialog(List<InteractiveMediaItem> media, InteractiveDefaultPlaybackItem item)
            {
                Text = T("InteractiveDefaultEdit"); ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                Size = new Size(860, 600); MinimumSize = new Size(780, 560);
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 9 };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int i = 0; i < 7; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                nameBox.Text = item.Name; hotkeyBox.Value = item.Hotkey?.Clone();
                foreach (var library in media) mediaBox.Items.Add(new MediaChoice(library));
                for (int index = 0; index < mediaBox.Items.Count; index++)
                    if (((MediaChoice)mediaBox.Items[index]).Item.RuntimeId == item.MediaId) mediaBox.SelectedIndex = index;
                if (mediaBox.SelectedIndex < 0 && mediaBox.Items.Count > 0) mediaBox.SelectedIndex = 0;
                loopMiddleBox.Text = T("InteractiveDefaultLoopMiddle"); loopMiddleBox.Checked = item.LoopMiddleFiles;
                mediaBox.SelectedIndexChanged += (_, __) => loopMiddleBox.Enabled = (mediaBox.SelectedItem as MediaChoice)?.Item.IsFileGroup == true;
                loopMiddleBox.Enabled = (mediaBox.SelectedItem as MediaChoice)?.Item.IsFileGroup == true;
                interruptionBox.Items.Add(T("InteractiveDefaultInheritInterruption"));
                foreach (InteractiveGroupInterruptionMode mode in Enum.GetValues(typeof(InteractiveGroupInterruptionMode))) interruptionBox.Items.Add(T(InteractiveGroupInterruptionKey(mode)));
                interruptionBox.SelectedIndex = item.InterruptionMode.HasValue ? (int)item.InterruptionMode.Value + 1 : 0;
                AddDialogRow(grid, T("InteractiveName"), nameBox, 0); AddDialogRow(grid, T("InteractiveMedia"), mediaBox, 1);
                AddDialogRow(grid, T("InteractiveHotkey"), hotkeyBox, 2); grid.Controls.Add(loopMiddleBox, 1, 3);
                AddDialogRow(grid, T("InteractiveDefaultSwitchInterruption"), interruptionBox, 4);
                autoSwitchBox.Text = T("InteractiveDefaultAutoSwitchEnabled"); autoSwitchBox.Checked = item.AutoSwitchEnabled;
                grid.Controls.Add(autoSwitchBox, 1, 5);
                minimumSecondsBox.Value = Math.Max(1, Math.Min(MaximumInteractiveDefaultSeconds, item.AutoSwitchMinimumSeconds));
                maximumSecondsBox.Value = Math.Max(minimumSecondsBox.Value, Math.Min(MaximumInteractiveDefaultSeconds, item.AutoSwitchMaximumSeconds));
                var duration = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                duration.Controls.Add(new Label { Text = T("InteractiveDefaultMinimumSeconds"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                duration.Controls.Add(minimumSecondsBox);
                duration.Controls.Add(new Label { Text = T("InteractiveDefaultMaximumSeconds"), AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
                duration.Controls.Add(maximumSecondsBox); grid.Controls.Add(duration, 1, 6);
                minimumSecondsBox.ValueChanged += (_, __) => { if (maximumSecondsBox.Value < minimumSecondsBox.Value) maximumSecondsBox.Value = minimumSecondsBox.Value; };
                maximumSecondsBox.ValueChanged += (_, __) => { if (minimumSecondsBox.Value > maximumSecondsBox.Value) minimumSecondsBox.Value = maximumSecondsBox.Value; };
                autoSwitchBox.CheckedChanged += (_, __) => duration.Enabled = autoSwitchBox.Checked;
                duration.Enabled = autoSwitchBox.Checked;
                var hint = new Label { Text = T("InteractiveDefaultItemHint") + Environment.NewLine + T("InteractiveDefaultAutoSwitchHint"), AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(730, 0), Padding = new Padding(0, 8, 0, 0) };
                grid.Controls.Add(hint, 0, 7); grid.SetColumnSpan(hint, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => { if (mediaBox.SelectedItem is MediaChoice) DialogResult = DialogResult.OK; });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 1, 8);
                Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
            }
        }
    }
}
