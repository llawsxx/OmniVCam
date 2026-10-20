using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private List<InteractiveProjectGroup> interactiveProjectGroups = new List<InteractiveProjectGroup>();
        private List<InteractiveGroupTransition> interactiveGroupTransitions = new List<InteractiveGroupTransition>();
        private Guid activeInteractiveProjectGroupId;
        private int interactiveProjectGroupVersion;
        private bool interactiveGroupSwitchRandom;
        private InteractiveHotkey interactiveGroupSwitchHotkey;

        private InteractiveProjectGroup SelectNextInteractiveProjectGroup()
        {
            EnsureInteractiveProjectGroups();
            if (interactiveProjectGroups.Count < 2) return null;
            var current = CurrentInteractiveProjectGroup;
            if (!interactiveGroupSwitchRandom)
                return interactiveProjectGroups[(interactiveProjectGroups.IndexOf(current) + 1) % interactiveProjectGroups.Count];
            var candidates = interactiveProjectGroups.Where(group => group.Id != current.Id).ToList();
            return candidates[random.Next(candidates.Count)];
        }

        private void SwitchNextInteractiveProjectGroup(string source)
        {
            if (!interactiveWindowOpen || closingMainForm) return;
            var target = SelectNextInteractiveProjectGroup();
            if (target != null) SwitchInteractiveProjectGroup(target, source);
        }

        private bool IsInteractiveGroupSwitchHotkey(InteractiveHotkey hotkey)
        {
            return SameInteractiveHotkey(interactiveGroupSwitchHotkey, hotkey);
        }

        private sealed class InteractiveProjectGroup
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public string Name { get; set; } = string.Empty;
            public InteractiveHotkey Hotkey { get; set; }
            public Guid DefaultMediaId { get; set; }
            public string ObsStartScene { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
            public List<InteractiveRouteItem> Routes { get; set; } = new List<InteractiveRouteItem>();
            public override string ToString() { return Name; }
        }

        private sealed class InteractiveGroupTransition
        {
            public Guid FromId { get; set; }
            public Guid ToId { get; set; }
            public Guid MediaId { get; set; }
        }

        private void EnsureInteractiveProjectGroups()
        {
            if (interactiveProjectGroups == null) interactiveProjectGroups = new List<InteractiveProjectGroup>();
            if (interactiveGroupTransitions == null) interactiveGroupTransitions = new List<InteractiveGroupTransition>();
            if (interactiveProjectGroups.Count == 0)
            {
                var group = new InteractiveProjectGroup { Name = T("InteractiveDefaultGroup"), Routes = interactiveRoutes };
                interactiveProjectGroups.Add(group);
                activeInteractiveProjectGroupId = group.Id;
            }
        }

        private InteractiveProjectGroup CurrentInteractiveProjectGroup
        {
            get
            {
                EnsureInteractiveProjectGroups();
                return interactiveProjectGroups.FirstOrDefault(group => group.Id == activeInteractiveProjectGroupId)
                    ?? interactiveProjectGroups[0];
            }
        }

        private IEnumerable<InteractiveRouteItem> AllInteractiveProjectRoutes()
        {
            EnsureInteractiveProjectGroups();
            return interactiveProjectGroups.SelectMany(group => group.Routes);
        }

        private void BindInteractiveProjectGroups(ConnectionTabState state)
        {
            interactiveProjectGroups = state.InteractiveProjectGroups;
            interactiveGroupTransitions = state.InteractiveGroupTransitions;
            interactiveGroupSwitchRandom = state.InteractiveGroupSwitchRandom;
            interactiveGroupSwitchHotkey = state.InteractiveGroupSwitchHotkey;
            activeInteractiveProjectGroupId = state.ActiveInteractiveProjectGroupId;
            EnsureInteractiveProjectGroups();
            activeInteractiveProjectGroupId = CurrentInteractiveProjectGroup.Id;
            interactiveRoutes = CurrentInteractiveProjectGroup.Routes;
        }

        private bool IsObsoleteInteractiveTransitionFrame(long frameId)
        {
            return interactiveCurrent?.IsGroupTransition == true && frameId > 0 &&
                frameId <= interactiveCurrent.MinimumBackendFrameId;
        }

        private bool IsStaleInteractiveGroupRequest(InteractiveTriggerRequest request)
        {
            return !request.IsGroupTransition && request.ProjectGroupId != Guid.Empty &&
                (request.ProjectGroupId != activeInteractiveProjectGroupId || request.ProjectGroupVersion != interactiveProjectGroupVersion);
        }

        private static InteractiveRouteItem CopyInteractiveProject(InteractiveRouteItem route)
        {
            // XML holds only configuration. Selection progress and playback snapshots are deliberately independent.
            return LoadInteractiveRouteElement(SaveInteractiveRouteElement(route));
        }

        private List<InteractiveGroupTransition> BuildInteractiveTransitionPairs()
        {
            EnsureInteractiveProjectGroups();
            return interactiveProjectGroups.SelectMany(from => interactiveProjectGroups.Where(to => to.Id != from.Id)
                .Select(to => new InteractiveGroupTransition
                {
                    FromId = from.Id, ToId = to.Id,
                    MediaId = interactiveGroupTransitions.FirstOrDefault(item => item.FromId == from.Id && item.ToId == to.Id)?.MediaId ?? Guid.Empty
                })).ToList();
        }

        private InteractiveTriggerRequest BuildInteractiveGroupTransition(InteractiveProjectGroup from, InteractiveProjectGroup to)
        {
            var transition = interactiveGroupTransitions.FirstOrDefault(item => item.FromId == from.Id && item.ToId == to.Id);
            if (transition == null || transition.MediaId == Guid.Empty) return null;
            var media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == transition.MediaId);
            if (!CanPlayInteractiveMedia(media)) return null;
            string title = string.Format(T("InteractiveGroupTransitionTitle"), from.Name, to.Name);
            var snapshot = media.IsFileGroup ? SnapshotInteractiveFileGroup(media) : LoadInteractiveMediaElement(SaveInteractiveMediaElement(media));
            snapshot.Title = title + " / " + media.Title;
            snapshot.Loop = false;
            var entries = BuildInteractiveMediaEntries(snapshot, null);
            // The library interruption policy applies to the whole selected transition sequence.
            foreach (var entry in entries) entry.IsGroupTransition = true;
            return new InteractiveTriggerRequest
            {
                RouteName = title, Source = T("InteractiveGroupSwitch"), Immediate = true,
                IsGroupTransition = true, Entries = entries
            };
        }

        private List<InteractivePlaybackEntry> BuildInteractiveGroupDefault(InteractiveProjectGroup group, bool reportMissing = false)
        {
            var media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == group.DefaultMediaId);
            if (media == null || !CanPlayInteractiveMedia(media) ||
                (media.IsFileGroup ? media.FilePaths.Any(path => !File.Exists(path)) : !File.Exists(media.Path)))
            {
                if (reportMissing && group.DefaultMediaId != Guid.Empty)
                    AppendInteractiveLog(T("InteractiveDefaultVideoMissing") + (media?.Title ?? group.Name));
                return new List<InteractivePlaybackEntry>();
            }
            // Defaults repeat while preserving the library item's interruption policy and selection progress.
            var snapshot = media.IsFileGroup ? SnapshotInteractiveFileGroup(media) : LoadInteractiveMediaElement(SaveInteractiveMediaElement(media));
            snapshot.Title = string.Format(T("InteractiveGroupDefaultTitle"), group.Name) +
                (string.IsNullOrWhiteSpace(media.Title) ? string.Empty : " / " + media.Title);
            snapshot.Loop = true;
            var entries = BuildInteractiveMediaEntries(snapshot, null);
            foreach (var entry in entries)
            {
                entry.IsGroupDefault = true;
                if (entry.GroupLoop != null) entry.GroupLoop.IsGroupDefault = true;
            }
            return entries;
        }

        private void QueueInteractiveGroupDefaultIfNeeded()
        {
            if (!interactiveWindowOpen || closingMainForm || interactivePending != null || interactiveQueue.Count > 0 ||
                interactiveTriggerRequests.Count > 0 || interactiveCurrent == null || interactiveCurrent.Loop ||
                (interactiveCurrent.GroupLoop != null && !interactiveCurrent.GroupLoop.Cancelled)) return;
            foreach (var entry in BuildInteractiveGroupDefault(CurrentInteractiveProjectGroup)) interactiveQueue.Enqueue(entry);
        }

        private void RequestInteractiveGroupDefaultIfIdle()
        {
            if (!interactiveWindowOpen || closingMainForm || interactiveActive || interactiveCurrent != null || interactivePending != null ||
                interactiveQueue.Count > 0 || interactiveTriggerRequests.Count > 0) return;
            var entries = BuildInteractiveGroupDefault(CurrentInteractiveProjectGroup, true);
            if (entries.Count == 0) return;
            BeginInteractiveMode();
            interactiveTriggerRequests.Enqueue(new InteractiveTriggerRequest
            {
                RouteName = entries[0].Title, Source = T("InteractiveGroupDefaultVideo"), Immediate = true,
                ProjectGroupId = activeInteractiveProjectGroupId, ProjectGroupVersion = interactiveProjectGroupVersion,
                Entries = entries
            });
            interactiveForm?.RefreshRuntime();
            if (!processingInteractiveTriggers) _ = ProcessInteractiveTriggerRequestsAsync();
        }

        private void SwitchInteractiveProjectGroup(InteractiveProjectGroup target, string source)
        {
            if (!interactiveWindowOpen || closingMainForm || target == null) return;
            EnsureInteractiveProjectGroups();
            if (!interactiveProjectGroups.Contains(target)) return;
            if (target.Id == CurrentInteractiveProjectGroup.Id)
            {
                RequestInteractiveGroupDefaultIfIdle();
                return;
            }
            var from = CurrentInteractiveProjectGroup;
            var setting = interactiveGroupTransitions.FirstOrDefault(item => item.FromId == from.Id && item.ToId == target.Id);
            if (setting != null && setting.MediaId != Guid.Empty)
            {
                var media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == setting.MediaId);
                if (!CanPlayInteractiveMedia(media) || (media.IsFileGroup ? media.FilePaths.Any(path => !File.Exists(path)) : !File.Exists(media.Path)))
                {
                    AppendInteractiveLog(T("InteractiveTransitionFileMissing") + (media?.Title ?? from.Name + " -> " + target.Name));
                    return;
                }
            }
            var transition = BuildInteractiveGroupTransition(from, target);
            var defaultEntries = BuildInteractiveGroupDefault(target, true);
            if (defaultEntries.Count > 0)
            {
                if (transition == null) transition = new InteractiveTriggerRequest
                    { RouteName = defaultEntries[0].Title, Source = T("InteractiveGroupSwitch"), Immediate = true };
                transition.Entries.AddRange(defaultEntries);
            }
            // Keep already requested transitions in order during rapid A -> B -> C switches.
            // Waiting project triggers from the previous group must not start after switching groups.
            var retained = interactiveTriggerRequests.Where(request => request.IsGroupTransition).ToList();
            interactiveTriggerRequests.Clear();
            foreach (var request in retained) interactiveTriggerRequests.Enqueue(request);
            interactiveProjectGroupVersion++;
            if (transition != null)
            {
                transition.ProjectGroupId = target.Id;
                transition.ProjectGroupVersion = interactiveProjectGroupVersion;
            }
            activeInteractiveProjectGroupId = target.Id;
            interactiveRoutes = target.Routes;
            SaveActiveConnectionState();
            interactiveForm?.ReloadAll();
            AppendInteractiveLog(source + " - " + string.Format(T("InteractiveGroupSwitched"), from.Name, target.Name));
            if (transition == null) return;
            ObserveBackendConnectionLoss();
            if (!interactiveActive && interactiveTriggerRequests.Count == 0) BeginInteractiveMode();
            interactiveTriggerRequests.Enqueue(transition);
            if (interactiveProtectedHandoff != null && !interactiveProtectedHandoff.Request.IsGroupTransition)
                CancelInteractiveProtectedHandoff();
            interactiveForm?.RefreshRuntime();
            if (!processingInteractiveTriggers) _ = ProcessInteractiveTriggerRequestsAsync();
        }

        private static XElement SaveInteractiveProjectGroupsElement(ConnectionTabState state)
        {
            var groups = state.InteractiveProjectGroups;
            if (groups.Count == 0)
                groups = new List<InteractiveProjectGroup> { new InteractiveProjectGroup
                    { Name = T("InteractiveDefaultGroup"), Routes = state.InteractiveRoutes } };
            Guid activeId = groups.Any(group => group.Id == state.ActiveInteractiveProjectGroupId)
                ? state.ActiveInteractiveProjectGroupId : groups[0].Id;
            return new XElement("InteractivePlayback", new XAttribute("activeGroup", activeId),
                new XElement("GroupSwitch", new XAttribute("random", state.InteractiveGroupSwitchRandom),
                    new XAttribute("hotkey", state.InteractiveGroupSwitchHotkey == null ? string.Empty : state.InteractiveGroupSwitchHotkey.KeyCode.ToString()),
                    new XAttribute("modifiers", state.InteractiveGroupSwitchHotkey == null ? string.Empty : state.InteractiveGroupSwitchHotkey.Modifiers.ToString())),
                new XElement("Media", state.InteractiveMedia.Select(SaveInteractiveMediaElement)),
                new XElement("Groups", groups.Select(group => new XElement("Group",
                    new XAttribute("id", group.Id), new XAttribute("name", group.Name), new XAttribute("defaultMedia", group.DefaultMediaId),
                    new XAttribute("obsStartScene", group.ObsStartScene), new XAttribute("obsEndScene", group.ObsEndScene),
                    new XAttribute("hotkey", group.Hotkey == null ? string.Empty : group.Hotkey.KeyCode.ToString()),
                    new XAttribute("modifiers", group.Hotkey == null ? string.Empty : group.Hotkey.Modifiers.ToString()),
                    new XElement("Routes", group.Routes.Select(SaveInteractiveRouteElement))))),
                new XElement("Transitions", state.InteractiveGroupTransitions.Where(item => item.MediaId != Guid.Empty &&
                    item.FromId != item.ToId && groups.Any(group => group.Id == item.FromId) && groups.Any(group => group.Id == item.ToId))
                    .Select(item => new XElement("Transition", new XAttribute("from", item.FromId),
                        new XAttribute("to", item.ToId), new XAttribute("media", item.MediaId)))));
        }

        private void LoadInteractiveProjectGroups(XElement element)
        {
            interactiveProjectGroups = new List<InteractiveProjectGroup>();
            interactiveGroupTransitions = new List<InteractiveGroupTransition>();
            activeInteractiveProjectGroupId = Guid.Empty;
            var switchSettings = element?.Element("GroupSwitch") ?? new XElement("GroupSwitch");
            bool randomOrder;
            interactiveGroupSwitchRandom = bool.TryParse(GetAttribute(switchSettings, "random", "false"), out randomOrder) && randomOrder;
            interactiveGroupSwitchHotkey = ParseInteractiveHotkey(GetAttribute(switchSettings, "hotkey", string.Empty),
                GetAttribute(switchSettings, "modifiers", string.Empty));
            var groups = element?.Element("Groups");
            if (groups != null)
                foreach (var node in groups.Elements("Group"))
                {
                    Guid id, defaultMediaId;
                    var group = new InteractiveProjectGroup
                    {
                        Name = GetAttribute(node, "name", T("InteractiveDefaultGroup")),
                        ObsStartScene = GetAttribute(node, "obsStartScene", string.Empty),
                        ObsEndScene = GetAttribute(node, "obsEndScene", string.Empty),
                        DefaultMediaId = Guid.TryParse(GetAttribute(node, "defaultMedia", null), out defaultMediaId) ? defaultMediaId : Guid.Empty,
                        Hotkey = ParseInteractiveHotkey(GetAttribute(node, "hotkey", string.Empty), GetAttribute(node, "modifiers", string.Empty))
                    };
                    if (Guid.TryParse(GetAttribute(node, "id", null), out id) && id != Guid.Empty &&
                        !interactiveProjectGroups.Any(item => item.Id == id)) group.Id = id;
                    if (node.Element("Routes") != null)
                        group.Routes.AddRange(node.Element("Routes").Elements("Route").Select(LoadInteractiveRouteElement));
                    interactiveProjectGroups.Add(group);
                }
            if (interactiveProjectGroups.Count == 0)
            {
                // Older configurations stored one Routes list directly under InteractivePlayback.
                var routes = element?.Element("Routes");
                if (routes != null) interactiveRoutes.AddRange(routes.Elements("Route").Select(LoadInteractiveRouteElement));
            }
            EnsureInteractiveProjectGroups();
            Guid selected;
            activeInteractiveProjectGroupId = Guid.TryParse(element?.Attribute("activeGroup")?.Value, out selected) &&
                interactiveProjectGroups.Any(group => group.Id == selected) ? selected : interactiveProjectGroups[0].Id;
            interactiveRoutes = CurrentInteractiveProjectGroup.Routes;
            var transitions = element?.Element("Transitions");
            if (transitions != null)
                foreach (var node in transitions.Elements("Transition"))
                {
                    Guid from, to, mediaId;
                    if (!Guid.TryParse(GetAttribute(node, "media", null), out mediaId) || mediaId == Guid.Empty ||
                        !Guid.TryParse(GetAttribute(node, "from", null), out from) || !Guid.TryParse(GetAttribute(node, "to", null), out to) ||
                        from == to || !interactiveProjectGroups.Any(group => group.Id == from) || !interactiveProjectGroups.Any(group => group.Id == to) ||
                        interactiveGroupTransitions.Any(item => item.FromId == from && item.ToId == to)) continue;
                    interactiveGroupTransitions.Add(new InteractiveGroupTransition { FromId = from, ToId = to, MediaId = mediaId });
                }
        }

        private sealed partial class InteractivePlaybackForm
        {
            private const int ProjectGroupHotkeyBaseId = 0x6100;
            private const int GroupSwitchHotkeyId = 0x6000;
            private bool groupSwitchHotkeyRegistered;
            private Button switchProjectGroupButton;
            private readonly FlowLayoutPanel projectGroupButtons = new FlowLayoutPanel();
            private readonly Dictionary<int, InteractiveProjectGroup> registeredProjectGroups = new Dictionary<int, InteractiveProjectGroup>();

            private Control BuildProjectGroupPanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveProjectGroups"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("InteractiveAddGroup"), (_, __) => EditProjectGroup(true)));
                bar.Controls.Add(MakeButton(T("InteractiveEditGroup"), (_, __) => EditProjectGroup(false)));
                bar.Controls.Add(MakeButton(T("InteractiveRemoveGroup"), (_, __) => RemoveProjectGroup()));
                bar.Controls.Add(MakeButton(T("InteractiveGroupTransitions"), (_, __) => EditGroupTransitions()));
                bar.Controls.Add(MakeButton(T("InteractiveGroupObsScenes"), (_, __) => EditGroupObsScenes()));
                panel.Controls.Add(bar, 0, 0);
                projectGroupButtons.Dock = DockStyle.Fill;
                projectGroupButtons.AutoSize = true;
                panel.Controls.Add(projectGroupButtons, 0, 1);
                var switchBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                switchProjectGroupButton = MakeButton(T("InteractiveGroupSwitch"),
                    (_, __) => owner.SwitchNextInteractiveProjectGroup(T("InteractiveGroupSwitch")));
                switchBar.Controls.Add(switchProjectGroupButton);
                switchBar.Controls.Add(MakeButton(T("InteractiveGroupSwitchSettings"), (_, __) => EditGroupSwitchSettings()));
                panel.Controls.Add(switchBar, 0, 2);
                return panel;
            }

            private void RefreshProjectGroups()
            {
                owner.EnsureInteractiveProjectGroups();
                string switchHotkey = FormatHotkey(owner.interactiveGroupSwitchHotkey);
                switchProjectGroupButton.Text = T("InteractiveGroupSwitch") + " · " +
                    T(owner.interactiveGroupSwitchRandom ? "InteractiveRandom" : "InteractiveSequential") +
                    (switchHotkey.Length > 0 ? "  [" + switchHotkey + "]" : string.Empty);
                switchProjectGroupButton.Enabled = owner.interactiveProjectGroups.Count > 1;
                foreach (Control button in projectGroupButtons.Controls.Cast<Control>().ToList()) button.Dispose();
                projectGroupButtons.Controls.Clear();
                foreach (var group in owner.interactiveProjectGroups)
                {
                    var captured = group;
                    string hotkey = FormatHotkey(group.Hotkey);
                    var button = MakeButton(group.Name + (hotkey.Length > 0 ? "  [" + hotkey + "]" : string.Empty),
                        (_, __) => owner.SwitchInteractiveProjectGroup(captured, T("InteractiveGroupSwitch")));
                    if (group.Id == owner.CurrentInteractiveProjectGroup.Id)
                    {
                        button.BackColor = Color.LightSkyBlue;
                        button.UseVisualStyleBackColor = false;
                        button.Font = new Font(button.Font, FontStyle.Bold);
                    }
                    projectGroupButtons.Controls.Add(button);
                }
            }

            private void EditGroupSwitchSettings()
            {
                using (var dialog = new GroupSwitchSettingsDialog(owner.interactiveGroupSwitchRandom, owner.interactiveGroupSwitchHotkey))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (dialog.Hotkey != null && (owner.interactiveProjectGroups.Any(group => SameInteractiveHotkey(group.Hotkey, dialog.Hotkey)) ||
                        owner.AllInteractiveProjectRoutes().Any(route => SameInteractiveHotkey(route.Hotkey, dialog.Hotkey))))
                    { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    owner.interactiveGroupSwitchRandom = dialog.RandomOrder;
                    owner.interactiveGroupSwitchHotkey = dialog.Hotkey;
                }
                RefreshProjectGroups();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void EditProjectGroup(bool create)
            {
                var current = owner.CurrentInteractiveProjectGroup;
                using (var dialog = new ProjectGroupEditDialog(owner.interactiveMedia, create ? null : current))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string name = dialog.GroupName;
                    if (owner.interactiveProjectGroups.Any(group => (create || group != current) && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                    { MessageBox.Show(this, T("InteractiveGroupNameExists")); return; }
                    if (dialog.Hotkey != null && (owner.IsInteractiveGroupSwitchHotkey(dialog.Hotkey) || owner.interactiveProjectGroups.Any(group => (create || group != current) && SameInteractiveHotkey(group.Hotkey, dialog.Hotkey)) ||
                        owner.AllInteractiveProjectRoutes().Any(route => SameInteractiveHotkey(route.Hotkey, dialog.Hotkey))))
                    { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    if (create)
                    {
                        var group = new InteractiveProjectGroup { Name = name, Hotkey = dialog.Hotkey, DefaultMediaId = dialog.DefaultMediaId };
                        if (dialog.CopyProjects) group.Routes.AddRange(current.Routes.Select(CopyInteractiveProject));
                        owner.interactiveProjectGroups.Add(group);
                    }
                    else { current.Name = name; current.Hotkey = dialog.Hotkey; current.DefaultMediaId = dialog.DefaultMediaId; }
                }
                ReloadAll();
                owner.SaveActiveConnectionState();
                owner.RequestInteractiveGroupDefaultIfIdle();
            }

            private void RemoveProjectGroup()
            {
                if (owner.interactiveProjectGroups.Count <= 1) return;
                var group = owner.CurrentInteractiveProjectGroup;
                if (MessageBox.Show(this, string.Format(T("InteractiveConfirmRemoveGroup"), group.Name), T("InteractiveRemoveGroup"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                owner.interactiveProjectGroups.Remove(group);
                owner.interactiveGroupTransitions.RemoveAll(item => item.FromId == group.Id || item.ToId == group.Id);
                owner.activeInteractiveProjectGroupId = owner.interactiveProjectGroups[0].Id;
                owner.interactiveRoutes = owner.interactiveProjectGroups[0].Routes;
                ReloadAll();
                owner.SaveActiveConnectionState();
            }

            private void CopySelectedProject()
            {
                var route = SelectedRoute();
                if (route == null) return;
                using (var dialog = new ProjectCopyDialog(owner.interactiveProjectGroups.Where(group => group != owner.CurrentInteractiveProjectGroup).ToList()))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var target = owner.interactiveProjectGroups.FirstOrDefault(group => string.Equals(group.Name, dialog.TargetName, StringComparison.OrdinalIgnoreCase));
                    if (target == owner.CurrentInteractiveProjectGroup) { MessageBox.Show(this, T("InteractiveGroupNameExists")); return; }
                    if (target == null)
                    {
                        target = new InteractiveProjectGroup { Name = dialog.TargetName };
                        owner.interactiveProjectGroups.Add(target);
                    }
                    var copy = CopyInteractiveProject(route);
                    if (target.Routes.Any(item => SameInteractiveHotkey(item.Hotkey, copy.Hotkey))) copy.Hotkey = null;
                    target.Routes.Add(copy);
                }
                ReloadAll();
                owner.SaveActiveConnectionState();
            }

            private void EditGroupTransitions()
            {
                using (var dialog = new GroupTransitionsDialog(owner.interactiveProjectGroups, owner.BuildInteractiveTransitionPairs(), owner.interactiveMedia))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    owner.interactiveGroupTransitions.Clear();
                    owner.interactiveGroupTransitions.AddRange(dialog.Transitions.Where(item => item.MediaId != Guid.Empty));
                }
                owner.SaveActiveConnectionState();
            }

            private void RegisterProjectGroupHotkeys()
            {
                owner.EnsureInteractiveProjectGroups();
                var switchHotkey = owner.interactiveGroupSwitchHotkey;
                if (switchHotkey != null && switchHotkey.KeyCode != Keys.None)
                {
                    groupSwitchHotkeyRegistered = RegisterHotKey(Handle, GroupSwitchHotkeyId,
                        (uint)switchHotkey.Modifiers | 0x4000, (uint)switchHotkey.KeyCode);
                    if (!groupSwitchHotkeyRegistered)
                        AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + T("InteractiveHotkeyRegisterFailed") +
                            FormatHotkey(switchHotkey) + " - " + T("InteractiveGroupSwitch"));
                }
                int index = 0;
                foreach (var group in owner.interactiveProjectGroups)
                {
                    if (group.Hotkey == null || group.Hotkey.KeyCode == Keys.None) continue;
                    int id = ProjectGroupHotkeyBaseId + index++;
                    if (RegisterHotKey(Handle, id, (uint)group.Hotkey.Modifiers, (uint)group.Hotkey.KeyCode)) registeredProjectGroups[id] = group;
                    else AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + T("InteractiveHotkeyRegisterFailed") + FormatHotkey(group.Hotkey) + " - " + group.Name);
                }
            }

            private void UnregisterProjectGroupHotkeys()
            {
                if (IsHandleCreated && groupSwitchHotkeyRegistered) UnregisterHotKey(Handle, GroupSwitchHotkeyId);
                groupSwitchHotkeyRegistered = false;
                if (IsHandleCreated)
                    foreach (int id in registeredProjectGroups.Keys.ToList()) UnregisterHotKey(Handle, id);
                registeredProjectGroups.Clear();
            }
        }

        private static bool SameInteractiveHotkey(InteractiveHotkey left, InteractiveHotkey right)
        {
            return left != null && right != null && left.KeyCode != Keys.None && left.KeyCode == right.KeyCode && left.Modifiers == right.Modifiers;
        }

        private sealed class ProjectGroupEditDialog : Form
        {
            private readonly TextBox nameBox = new TextBox();
            private readonly HotkeyTextBox hotkeyBox = new HotkeyTextBox();
            private readonly ComboBox defaultMediaBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            private readonly CheckBox copyBox = new CheckBox { AutoSize = true };
            public string GroupName => nameBox.Text.Trim();
            public InteractiveHotkey Hotkey => hotkeyBox.Value?.Clone();
            public bool CopyProjects => copyBox.Checked;
            public Guid DefaultMediaId => (defaultMediaBox.SelectedItem as MediaChoice)?.Item.RuntimeId ?? Guid.Empty;
            public ProjectGroupEditDialog(List<InteractiveMediaItem> media, InteractiveProjectGroup group)
            {
                Text = T(group == null ? "InteractiveAddGroup" : "InteractiveEditGroup");
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent; Size = new Size(760, 300); MinimumSize = Size;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoSize = true };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                AddDialogRow(grid, T("InteractiveGroupName"), nameBox, 0);
                AddDialogRow(grid, T("InteractiveHotkey"), hotkeyBox, 1);
                nameBox.Text = group?.Name ?? string.Empty;
                hotkeyBox.Value = group?.Hotkey?.Clone();
                defaultMediaBox.Items.Add(T("InteractiveNoDefaultMedia"));
                foreach (var item in media) defaultMediaBox.Items.Add(new MediaChoice(item));
                defaultMediaBox.SelectedIndex = 0;
                for (int index = 1; index < defaultMediaBox.Items.Count; index++)
                    if (((MediaChoice)defaultMediaBox.Items[index]).Item.RuntimeId == group?.DefaultMediaId)
                        defaultMediaBox.SelectedIndex = index;
                AddDialogRow(grid, T("InteractiveGroupDefaultVideo"), defaultMediaBox, 2);
                var hint = new Label { Text = T("InteractiveGroupDefaultHint"), AutoSize = true, Dock = DockStyle.Fill };
                grid.Controls.Add(hint, 1, 3);
                copyBox.Text = T("InteractiveCopyCurrentGroup"); copyBox.Visible = group == null;
                grid.Controls.Add(copyBox, 1, 4);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => { if (GroupName.Length > 0) DialogResult = DialogResult.OK; });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 1, 5);
                Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
            }
        }

        private sealed class GroupSwitchSettingsDialog : Form
        {
            private readonly ComboBox modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            private readonly HotkeyTextBox hotkeyBox = new HotkeyTextBox();
            public bool RandomOrder => modeBox.SelectedIndex == 1;
            public InteractiveHotkey Hotkey => hotkeyBox.Value?.Clone();

            public GroupSwitchSettingsDialog(bool randomOrder, InteractiveHotkey hotkey)
            {
                Text = T("InteractiveGroupSwitchSettings"); Size = new Size(660, 290); MinimumSize = new Size(580, 290);
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                modeBox.Items.AddRange(new object[] { T("InteractiveSequential"), T("InteractiveRandom") });
                modeBox.SelectedIndex = randomOrder ? 1 : 0; hotkeyBox.Value = hotkey?.Clone();
                AddDialogRow(grid, T("InteractiveGroupSwitchMode"), modeBox, 0);
                AddDialogRow(grid, T("InteractiveHotkey"), hotkeyBox, 1);
                var hint = new Label { Text = T("InteractiveGroupSwitchHint"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
                grid.Controls.Add(hint, 0, 2); grid.SetColumnSpan(hint, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 1, 3);
                Controls.Add(grid); AcceptButton = ok; CancelButton = cancel;
            }
        }

        private sealed class ProjectCopyDialog : Form
        {
            private readonly ComboBox targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            public string TargetName => targetBox.Text.Trim();
            public ProjectCopyDialog(List<InteractiveProjectGroup> groups)
            {
                Text = T("InteractiveCopyProject"); Size = new Size(620, 210); ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown };
                panel.Controls.Add(new Label { Text = T("InteractiveCopyProjectHint"), AutoSize = true, MaximumSize = new Size(570, 0) });
                targetBox.Width = 560; targetBox.Items.AddRange(groups.Cast<object>().ToArray()); panel.Controls.Add(targetBox);
                var buttons = new FlowLayoutPanel { AutoSize = true };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => { if (TargetName.Length > 0) DialogResult = DialogResult.OK; });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); panel.Controls.Add(buttons);
                Controls.Add(panel); AcceptButton = ok; CancelButton = cancel;
            }
        }

        private sealed class GroupTransitionsDialog : Form
        {
            private readonly ListView pairs = new ListView();
            private readonly ComboBox mediaBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            private readonly List<InteractiveMediaItem> media;
            private bool refreshingSelection;
            public List<InteractiveGroupTransition> Transitions { get; }
            public GroupTransitionsDialog(List<InteractiveProjectGroup> groups, List<InteractiveGroupTransition> transitions, List<InteractiveMediaItem> library)
            {
                Transitions = transitions; media = library;
                Text = T("InteractiveGroupTransitions"); Size = new Size(1000, 550); MinimumSize = new Size(720, 350);
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(8) };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.Controls.Add(new Label { Text = T("InteractiveTransitionHint"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
                pairs.Dock = DockStyle.Fill; pairs.View = View.Details; pairs.FullRowSelect = true; pairs.HideSelection = false; pairs.MultiSelect = false;
                pairs.Columns.Add(T("InteractiveTransitionFrom"), 170); pairs.Columns.Add(T("InteractiveTransitionTo"), 170); pairs.Columns.Add(T("InteractiveChooseTransition"), 570);
                foreach (var transition in transitions)
                {
                    var row = new ListViewItem(groups.First(group => group.Id == transition.FromId).Name);
                    row.SubItems.Add(groups.First(group => group.Id == transition.ToId).Name);
                    row.SubItems.Add(MediaTitle(transition.MediaId)); row.Tag = transition; pairs.Items.Add(row);
                }
                mediaBox.Items.Add(T("InteractiveNoTransitionMedia"));
                foreach (var item in library) mediaBox.Items.Add(new MediaChoice(item));
                mediaBox.SelectedIndex = 0; mediaBox.Enabled = false;
                mediaBox.SelectedIndexChanged += (_, __) =>
                {
                    if (!refreshingSelection) SetMedia((mediaBox.SelectedItem as MediaChoice)?.Item.RuntimeId ?? Guid.Empty);
                };
                pairs.SelectedIndexChanged += (_, __) => RefreshSelection();
                pairs.DoubleClick += (_, __) => { if (mediaBox.Enabled) { mediaBox.Focus(); mediaBox.DroppedDown = true; } };
                panel.Controls.Add(pairs, 0, 1);
                var selector = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
                selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                selector.Controls.Add(new Label { Text = T("InteractiveChooseTransition"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                selector.Controls.Add(mediaBox, 1, 0); panel.Controls.Add(selector, 0, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                buttons.Controls.Add(MakeButton(T("InteractiveClearTransition"), (_, __) => { SetMedia(Guid.Empty); RefreshSelection(); }));
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); panel.Controls.Add(buttons, 0, 3);
                Controls.Add(panel); AcceptButton = ok; CancelButton = cancel;
            }
            private string MediaTitle(Guid id)
            {
                var item = media.FirstOrDefault(entry => entry.RuntimeId == id);
                return item == null ? T(id == Guid.Empty ? "InteractiveNoTransitionMedia" : "InteractiveTransitionMediaMissing") : FormatInteractiveMediaTitle(item);
            }
            private void RefreshSelection()
            {
                refreshingSelection = true;
                try
                {
                    mediaBox.Enabled = pairs.SelectedItems.Count == 1;
                    Guid id = mediaBox.Enabled ? ((InteractiveGroupTransition)pairs.SelectedItems[0].Tag).MediaId : Guid.Empty;
                    mediaBox.SelectedIndex = 0;
                    for (int index = 1; index < mediaBox.Items.Count; index++)
                        if (((MediaChoice)mediaBox.Items[index]).Item.RuntimeId == id) mediaBox.SelectedIndex = index;
                }
                finally { refreshingSelection = false; }
            }
            private void SetMedia(Guid id)
            {
                if (pairs.SelectedItems.Count != 1) return;
                var row = pairs.SelectedItems[0]; ((InteractiveGroupTransition)row.Tag).MediaId = id; row.SubItems[2].Text = MediaTitle(id);
            }
        }
    }
}
