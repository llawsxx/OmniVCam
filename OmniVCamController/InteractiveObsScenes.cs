using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private readonly ComboBox playoutObsSceneBox = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckBox obsOperationsEnabledBox = new CheckBox { AutoSize = true, Checked = true };
        private int obsOperationsVersion;
        private bool ObsOperationsEnabled => obsOperationsEnabledBox == null || obsOperationsEnabledBox.Checked;

        private void ObsOperationsEnabledChanged(object sender, EventArgs e)
        {
            obsOperationsVersion++;
            if (!ObsOperationsEnabled)
            {
                interactiveObsActions?.Clear();
                pendingScheduledObsEnds?.Clear();
            }
        }

        private InteractiveObsPlayback interactiveObsCurrent;
        private Queue<InteractiveObsAction> interactiveObsActions = new Queue<InteractiveObsAction>();
        private Task interactiveObsActionTask;

        private sealed class InteractiveObsPlayback
        {
            public string StartScene { get; set; }
            public string EndScene { get; set; }
            public string Url { get; set; }
            public string Password { get; set; }
            public bool Started { get; set; }
            public bool Ended { get; set; }
        }

        private sealed class InteractiveObsAction
        {
            public int Version;
            public string Scene;
            public string Url;
            public string Password;
        }

        private InteractiveProjectGroup GetInteractiveObsGroup(InteractiveRouteItem route)
        {
            return interactiveProjectGroups?.FirstOrDefault(group => group.Routes.Contains(route))
                ?? CurrentInteractiveProjectGroup;
        }

        private static string ResolveInteractiveObsScene(InteractiveProjectGroup group, InteractiveRouteItem route,
            InteractiveMediaCombination combination, bool atStart)
        {
            string scene = combination == null ? null : atStart ? combination.ObsStartScene : combination.ObsEndScene;
            if (string.IsNullOrWhiteSpace(scene)) scene = atStart ? route.ObsStartScene : route.ObsEndScene;
            if (string.IsNullOrWhiteSpace(scene)) scene = atStart ? group.ObsStartScene : group.ObsEndScene;
            return (scene ?? string.Empty).Trim();
        }

        private string DescribeInteractiveObsScene(InteractiveRouteItem route, bool atStart)
        {
            var group = GetInteractiveObsGroup(route);
            var scenes = route.Combinations.Select(combination =>
                ResolveInteractiveObsScene(group, route, combination, atStart)).ToList();
            if (scenes.Count == 0) return ResolveInteractiveObsScene(group, route, null, atStart);
            if (scenes.All(scene => scene == scenes[0])) return scenes[0];
            return string.Join(" | ", scenes.Select((scene, index) =>
                (index + 1) + ". " + (scene.Length == 0 ? "-" : scene)));
        }

        private void AttachInteractiveObsPlayback(InteractiveRouteItem route, InteractiveMediaCombination combination,
            List<InteractivePlaybackEntry> entries)
        {
            if (!ObsOperationsEnabled) return;
            var group = GetInteractiveObsGroup(route);
            string start = ResolveInteractiveObsScene(group, route, combination, true);
            string end = ResolveInteractiveObsScene(group, route, combination, false);
            if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end)) return;
            var playback = new InteractiveObsPlayback
            {
                StartScene = (start ?? string.Empty).Trim(), EndScene = (end ?? string.Empty).Trim(),
                Url = mainObsUrlBox?.Text.Trim() ?? string.Empty, Password = mainObsPasswordBox?.Text ?? string.Empty
            };
            foreach (var entry in entries)
            {
                entry.ObsPlayback = playback;
                if (entry.GroupLoop != null) entry.GroupLoop.ObsPlayback = playback;
            }
        }

        private static bool CanReplaceInteractiveObsEnd(InteractiveObsPlayback previous, InteractiveObsPlayback next)
        {
            return previous != null && next != null && !next.Started && !next.Ended &&
                !string.IsNullOrWhiteSpace(next.StartScene) && !string.IsNullOrWhiteSpace(next.Url) &&
                string.Equals(previous.Url, next.Url, StringComparison.Ordinal);
        }

        private void SetInteractiveObsPlayback(InteractivePlaybackEntry entry)
        {
            var next = entry?.ObsPlayback;
            if (interactiveObsCurrent == next) return;
            var previous = interactiveObsCurrent;
            interactiveObsCurrent = next;
            if (previous != null && previous.Started && !previous.Ended)
            {
                previous.Ended = true;
                // A confirmed direct handoff needs only the destination scene; an intermediate end scene would flash.
                if (!CanReplaceInteractiveObsEnd(previous, next)) QueueInteractiveObsScene(previous.EndScene, previous);
            }
            if (next != null && !next.Started && !next.Ended)
            {
                next.Started = true;
                QueueInteractiveObsScene(next.StartScene, next);
            }
        }

        private void QueueInteractiveObsScene(string scene, InteractiveObsPlayback playback)
        {
            if (!ObsOperationsEnabled || string.IsNullOrWhiteSpace(scene) || string.IsNullOrWhiteSpace(playback.Url)) return;
            if (interactiveObsActions == null) interactiveObsActions = new Queue<InteractiveObsAction>();
            interactiveObsActions.Enqueue(new InteractiveObsAction { Version = obsOperationsVersion, Scene = scene, Url = playback.Url, Password = playback.Password });
            if (interactiveObsActionTask == null || interactiveObsActionTask.IsCompleted)
                interactiveObsActionTask = ProcessInteractiveObsActionsAsync();
        }

        private async Task ProcessInteractiveObsActionsAsync()
        {
            // Serial order guarantees that an old end action cannot overtake the next start action.
            // Playback and output-boundary processing never wait for an OBS network timeout.
            while (interactiveObsActions.Count > 0)
            {
                var action = interactiveObsActions.Dequeue();
                if (!ObsOperationsEnabled || action.Version != obsOperationsVersion) continue;
                await ApplyObsActionsAsync(action.Scene, string.Empty, string.Empty, action.Url, action.Password);
            }
        }

        private Task SwitchObsSceneAsync(bool mainPanel)
        {
            if (!ObsOperationsEnabled) return Task.CompletedTask;
            string scene = (mainPanel ? mainObsSceneBox : playoutObsSceneBox).Text.Trim();
            if (string.IsNullOrWhiteSpace(scene))
            {
                AppendLog(T("ObsSceneRequired"));
                return Task.CompletedTask;
            }
            return ApplyObsActionsAsync(scene, string.Empty, string.Empty,
                mainObsUrlBox.Text.Trim(),
                mainObsPasswordBox.Text);
        }

        private static void UpdateObsSceneChoices(ComboBox box, IList<string> scenes, string currentScene)
        {
            if (box == null || box.IsDisposed) return;
            string selected = box.Text;
            box.BeginUpdate();
            box.Items.Clear();
            box.Items.AddRange(scenes.Cast<object>().ToArray());
            box.EndUpdate();
            box.Text = !string.IsNullOrWhiteSpace(selected) ? selected : currentScene;
        }

        private List<string> GetSharedObsSceneNames()
        {
            return mainObsSceneBox == null ? new List<string>() : mainObsSceneBox.Items.Cast<string>().ToList();
        }

        private void UpdateSharedObsSceneChoices(IList<string> scenes, string currentScene)
        {
            UpdateObsSceneChoices(mainObsSceneBox, scenes, currentScene);
            UpdateObsSceneChoices(playoutObsSceneBox, scenes, currentScene);
            UpdateObsSceneChoices(obsSceneBox, scenes, string.Empty);
            UpdateObsSceneChoices(obsEndSceneBox, scenes, string.Empty);
        }

        private void ConfigureInteractiveObsEditor(RouteEditDialog dialog)
        {
            dialog.ObsUrl = mainObsUrlBox.Text.Trim();
            dialog.ObsPassword = mainObsPasswordBox.Text;
            dialog.IsObsEnabled = () => ObsOperationsEnabled;
            dialog.GetSceneNames = GetSharedObsSceneNames;
            dialog.PublishSceneNames = scenes => UpdateSharedObsSceneChoices(scenes, string.Empty);
        }

        private InteractiveObsScenesDialog CreateInteractiveGroupObsScenesDialog(InteractiveProjectGroup group)
        {
            var dialog = new InteractiveObsScenesDialog("InteractiveGroupObsScenes", "InteractiveGroupObsHint",
                group.ObsStartScene, group.ObsEndScene, mainObsUrlBox.Text.Trim(), mainObsPasswordBox.Text)
            {
                IsObsEnabled = () => ObsOperationsEnabled,
                PublishSceneNames = scenes => UpdateSharedObsSceneChoices(scenes, string.Empty)
            };
            dialog.Text += " · " + group.Name;
            dialog.SetSceneNames(GetSharedObsSceneNames());
            return dialog;
        }

        private sealed partial class InteractivePlaybackForm
        {
            private void EditGroupObsScenes()
            {
                var group = owner.CurrentInteractiveProjectGroup;
                using (var dialog = owner.CreateInteractiveGroupObsScenesDialog(group))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    group.ObsStartScene = dialog.StartScene;
                    group.ObsEndScene = dialog.EndScene;
                }
                RefreshRoutes();
                owner.SaveActiveConnectionState();
            }
        }

        private sealed partial class RouteEditDialog
        {
            public string ObsUrl { get; set; } = string.Empty;
            public string ObsPassword { get; set; } = string.Empty;
            public Func<bool> IsObsEnabled { get; set; } = () => true;
            public Func<IList<string>> GetSceneNames { get; set; } = () => new List<string>();
            public Action<IList<string>> PublishSceneNames { get; set; }
            private string routeObsStartScene = string.Empty;
            private string routeObsEndScene = string.Empty;

            private InteractiveObsScenesDialog CreateObsScenesDialog(bool combination)
            {
                var item = combination ? combinations[combinationsBox.SelectedIndex] : null;
                var dialog = new InteractiveObsScenesDialog(combination,
                    item == null ? routeObsStartScene : item.ObsStartScene,
                    item == null ? routeObsEndScene : item.ObsEndScene, ObsUrl, ObsPassword)
                    { IsObsEnabled = IsObsEnabled, PublishSceneNames = PublishSceneNames };
                dialog.SetSceneNames(GetSceneNames());
                return dialog;
            }

            private void EditObsScenes(bool combination)
            {
                if (combination && combinationsBox.SelectedIndex < 0) return;
                var item = combination ? combinations[combinationsBox.SelectedIndex] : null;
                using (var dialog = CreateObsScenesDialog(combination))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (item == null) { routeObsStartScene = dialog.StartScene; routeObsEndScene = dialog.EndScene; }
                    else { item.ObsStartScene = dialog.StartScene; item.ObsEndScene = dialog.EndScene; }
                }
            }
        }

        private sealed class InteractiveObsScenesDialog : Form
        {
            private readonly ComboBox startSceneBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            private readonly ComboBox endSceneBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            private readonly string url;
            private readonly string password;
            public Func<bool> IsObsEnabled { get; set; } = () => true;
            public Action<IList<string>> PublishSceneNames { get; set; }
            public void SetSceneNames(IList<string> scenes)
            {
                UpdateObsSceneChoices(startSceneBox, scenes, string.Empty);
                UpdateObsSceneChoices(endSceneBox, scenes, string.Empty);
            }
            public string StartScene => startSceneBox.Text.Trim();
            public string EndScene => endSceneBox.Text.Trim();

            public InteractiveObsScenesDialog(bool combination, string start, string end, string url, string password)
                : this(combination ? "InteractiveCombinationObsScenes" : "InteractiveProjectObsScenes",
                    combination ? "InteractiveCombinationObsHint" : "InteractiveProjectObsHint", start, end, url, password)
            {
            }

            public InteractiveObsScenesDialog(string titleKey, string hintKey, string start, string end, string url, string password)
            {
                this.url = url; this.password = password;
                Text = T(titleKey);
                Size = new Size(700, 330); MinimumSize = new Size(600, 330);
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                startSceneBox.Text = start ?? string.Empty; endSceneBox.Text = end ?? string.Empty;
                AddDialogRow(grid, T("InteractiveObsStartScene"), startSceneBox, 0);
                AddDialogRow(grid, T("InteractiveObsEndScene"), endSceneBox, 1);
                var refresh = MakeButton(T("ObsRefresh"), async (_, __) => await RefreshScenesAsync());
                grid.Controls.Add(refresh, 1, 2);
                var hint = new Label { Text = T(hintKey),
                    AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
                grid.Controls.Add(hint, 0, 3); grid.SetColumnSpan(hint, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 1, 4);
                Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
            }

            private async Task RefreshScenesAsync()
            {
                if (!IsObsEnabled()) return;
                try
                {
                    List<string> scenes;
                    using (var client = new ObsWebSocketClient(url, password))
                    {
                        await client.ConnectAsync();
                        if (!IsObsEnabled()) return;
                        scenes = await client.GetSceneListAsync();
                    }
                    if (IsDisposed || !IsObsEnabled()) return;
                    PublishSceneNames?.Invoke(scenes);
                    SetSceneNames(scenes);
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) MessageBox.Show(this, TF("ObsActionFailed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
    }
}
