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
            public List<InteractiveDefaultPlaybackItem> DefaultPlaybacks { get; set; } = new List<InteractiveDefaultPlaybackItem>();
            public Guid ActiveDefaultPlaybackId { get; set; }
            public bool DefaultSwitchRandom { get; set; }
            public InteractiveHotkey DefaultSwitchHotkey { get; set; }
            public Guid DefaultMediaId
            {
                get { return SelectedInteractiveDefault(this)?.MediaId ?? Guid.Empty; }
                set
                {
                    DefaultPlaybacks.Clear(); ActiveDefaultPlaybackId = Guid.Empty;
                    if (value == Guid.Empty) return;
                    var item = new InteractiveDefaultPlaybackItem { MediaId = value };
                    DefaultPlaybacks.Add(item); ActiveDefaultPlaybackId = item.Id;
                }
            }
            public bool OverlapEnabled { get; set; } = true;
            public decimal OverlapSeconds { get; set; } = 0.1m;
            public string ObsStartScene { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
            public string ObsEnterScene { get; set; } = string.Empty;
            public string ObsExitScene { get; set; } = string.Empty;
            public List<InteractiveRouteItem> Routes { get; set; } = new List<InteractiveRouteItem>();
            public override string ToString() { return Name; }
        }

        private sealed class InteractiveGroupTransition
        {
            public Guid FromId { get; set; }
            public Guid ToId { get; set; }
            public Guid MediaId { get; set; }
            public bool Immediate { get; set; } = true;
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
            if (request.IsGroupTransition) return false;
            if (request.ProjectGroupId != Guid.Empty &&
                (request.ProjectGroupId != activeInteractiveProjectGroupId || request.ProjectGroupVersion != interactiveProjectGroupVersion)) return true;
            return request.DefaultPlaybackId != Guid.Empty && SelectedInteractiveDefault(CurrentInteractiveProjectGroup)?.Id != request.DefaultPlaybackId;
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
                    MediaId = interactiveGroupTransitions.FirstOrDefault(item => item.FromId == from.Id && item.ToId == to.Id)?.MediaId ?? Guid.Empty,
                    Immediate = interactiveGroupTransitions.FirstOrDefault(item => item.FromId == from.Id && item.ToId == to.Id)?.Immediate ?? true
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
            ApplyInteractiveGroupOverlap(entries, to);
            return new InteractiveTriggerRequest
            {
                RouteName = title, Source = T("InteractiveGroupSwitch"), Immediate = transition.Immediate,
                IsGroupTransition = true, Entries = entries
            };
        }

        private List<InteractivePlaybackEntry> BuildInteractiveGroupDefault(InteractiveProjectGroup group, bool reportMissing = false)
        {
            var selected = SelectedInteractiveDefault(group);
            return BuildInteractiveDefaultPlayback(group, selected, reportMissing);
        }

        private InteractiveMediaItem ResolveInteractiveDefaultMedia(InteractiveProjectGroup group, InteractiveDefaultPlaybackItem selected, bool reportMissing)
        {
            var media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == selected?.MediaId);
            if (media == null || !CanPlayInteractiveMedia(media) ||
                (media.IsFileGroup ? EnumerateInteractiveMediaFiles(media).Any(path => !File.Exists(path)) : !File.Exists(media.Path)))
            {
                if (reportMissing && selected != null)
                    AppendInteractiveLog(T("InteractiveDefaultVideoMissing") + (media?.Title ?? group.Name));
                return null;
            }
            return media;
        }

        private List<InteractivePlaybackEntry> BuildInteractiveDefaultPlayback(InteractiveProjectGroup group, InteractiveDefaultPlaybackItem selected, bool reportMissing = false)
        {
            var media = ResolveInteractiveDefaultMedia(group, selected, reportMissing);
            if (media == null) return new List<InteractivePlaybackEntry>();
            // Defaults repeat while preserving the library item's interruption policy and selection progress.
            var snapshot = media.IsFileGroup ? SnapshotInteractiveFileGroup(media) : LoadInteractiveMediaElement(SaveInteractiveMediaElement(media));
            snapshot.Title = string.Format(T("InteractiveGroupDefaultTitle"), group.Name) +
                (string.IsNullOrWhiteSpace(media.Title) ? string.Empty : " / " + media.Title);
            snapshot.Loop = !snapshot.IsFileGroup || selected.LoopMiddleFiles;
            snapshot.DefaultSwitchInterruptionMode = selected.InterruptionMode;
            // A boundary-free group normally uses PreventInterrupt for the whole pass.
            // Represent it as phases when a default-switch override needs per-file handoff.
            if (snapshot.IsFileGroup && selected.InterruptionMode.HasValue && !HasInteractiveGroupBoundaries(snapshot))
            {
                snapshot.FileGroupInterruptionMode = media.PreventInterrupt ?
                    InteractiveGroupInterruptionMode.WholeGroup : InteractiveGroupInterruptionMode.Any;
                snapshot.ForcePhasedPlayback = true;
            }
            var entries = BuildInteractiveMediaEntries(snapshot, null);
            foreach (var entry in entries)
            {
                entry.IsGroupDefault = true;
                entry.DefaultPlaybackId = selected.Id;
                if (entry.GroupLoop != null)
                {
                    entry.GroupLoop.IsGroupDefault = true;
                    entry.GroupLoop.DefaultPlaybackId = selected.Id;
                }
            }
            ApplyInteractiveGroupOverlap(entries, group);
            return entries;
        }

        private static string InteractiveOverlapOptions(string options, decimal seconds)
        {
            var values = (options ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0 &&
                    !value.Split('=')[0].Equals("overlap", StringComparison.OrdinalIgnoreCase)).ToList();
            values.Add("overlap=" + seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(",", values);
        }

        private static void ApplyInteractiveGroupOverlap(IEnumerable<InteractivePlaybackEntry> entries, InteractiveProjectGroup group)
        {
            ApplyInteractiveOverlapSeconds(entries, group.OverlapEnabled ? group.OverlapSeconds : 0m);
        }

        private static void ApplyInteractiveOverlapSeconds(IEnumerable<InteractivePlaybackEntry> entries, decimal seconds)
        {
            foreach (var entry in entries)
            {
                entry.Options = InteractiveOverlapOptions(entry.Options, seconds);
                entry.InputSummary = BuildInputSummary(entry.Path, entry.Options);
                // Continuations and combo hits are created later from these private snapshots.
                if (entry.GroupLoop != null) entry.GroupLoop.Media.Options = InteractiveOverlapOptions(entry.GroupLoop.Media.Options, seconds);
                if (entry.PhaseRun != null)
                {
                    entry.PhaseRun.Media.Options = InteractiveOverlapOptions(entry.PhaseRun.Media.Options, seconds);
                    var outro = entry.PhaseRun.Outro;
                    if (outro != null)
                    {
                        outro.Options = InteractiveOverlapOptions(outro.Options, seconds);
                        outro.InputSummary = BuildInputSummary(outro.Path, outro.Options);
                    }
                }
            }
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
                Entries = entries, DefaultPlaybackId = entries[0].DefaultPlaybackId
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
                if (!CanPlayInteractiveMedia(media) || (media.IsFileGroup ? EnumerateInteractiveMediaFiles(media).Any(path => !File.Exists(path)) : !File.Exists(media.Path)))
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
            interactiveForm?.ReloadProjectGroup();
            AppendInteractiveLog(source + " - " + string.Format(T("InteractiveGroupSwitched"), from.Name, target.Name));
            QueueInteractiveObsGroupSwitch(from, target);
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
                    new XAttribute("id", group.Id), new XAttribute("name", group.Name), SaveInteractiveDefaults(group),
                    new XAttribute("overlapEnabled", group.OverlapEnabled), new XAttribute("overlapSeconds", group.OverlapSeconds),
                    new XAttribute("obsStartScene", group.ObsStartScene), new XAttribute("obsEndScene", group.ObsEndScene),
                    new XAttribute("obsEnterScene", group.ObsEnterScene), new XAttribute("obsExitScene", group.ObsExitScene),
                    new XAttribute("hotkey", group.Hotkey == null ? string.Empty : group.Hotkey.KeyCode.ToString()),
                    new XAttribute("modifiers", group.Hotkey == null ? string.Empty : group.Hotkey.Modifiers.ToString()),
                    new XElement("Routes", group.Routes.Select(SaveInteractiveRouteElement))))),
                new XElement("Transitions", state.InteractiveGroupTransitions.Where(item => item.MediaId != Guid.Empty &&
                    item.FromId != item.ToId && groups.Any(group => group.Id == item.FromId) && groups.Any(group => group.Id == item.ToId))
                    .Select(item => new XElement("Transition", new XAttribute("from", item.FromId),
                        new XAttribute("to", item.ToId), new XAttribute("media", item.MediaId),
                        new XAttribute("immediate", item.Immediate ? "1" : "0")))));
        }

        private static decimal ReadInteractiveOverlapSeconds(XElement node)
        {
            decimal seconds;
            return decimal.TryParse(GetAttribute(node, "overlapSeconds", "0.1"), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds >= 0 && seconds <= 5 ? seconds : 0.1m;
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
                        OverlapEnabled = !GetAttribute(node, "overlapEnabled", "true").Equals("false", StringComparison.OrdinalIgnoreCase),
                        OverlapSeconds = ReadInteractiveOverlapSeconds(node),
                        ObsStartScene = GetAttribute(node, "obsStartScene", string.Empty),
                        ObsEndScene = GetAttribute(node, "obsEndScene", string.Empty),
                        ObsEnterScene = GetAttribute(node, "obsEnterScene", string.Empty),
                        ObsExitScene = GetAttribute(node, "obsExitScene", string.Empty),
                        DefaultMediaId = Guid.TryParse(GetAttribute(node, "defaultMedia", null), out defaultMediaId) ? defaultMediaId : Guid.Empty,
                        Hotkey = ParseInteractiveHotkey(GetAttribute(node, "hotkey", string.Empty), GetAttribute(node, "modifiers", string.Empty))
                    };
                    if (Guid.TryParse(GetAttribute(node, "id", null), out id) && id != Guid.Empty &&
                        !interactiveProjectGroups.Any(item => item.Id == id)) group.Id = id;
                    LoadInteractiveDefaults(node, group);
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
                    interactiveGroupTransitions.Add(new InteractiveGroupTransition { FromId = from, ToId = to, MediaId = mediaId,
                        Immediate = GetAttribute(node, "immediate", "1") != "0" });
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
                EnableDoubleBuffering(panel);
                EnableDoubleBuffering(projectGroupButtons);
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
                projectGroupButtons.SuspendLayout();
                try
                {
                    while (projectGroupButtons.Controls.Count > owner.interactiveProjectGroups.Count)
                        projectGroupButtons.Controls[projectGroupButtons.Controls.Count - 1].Dispose();
                    for (int index = 0; index < owner.interactiveProjectGroups.Count; index++)
                    {
                        var group = owner.interactiveProjectGroups[index];
                        Button button;
                        if (index < projectGroupButtons.Controls.Count) button = (Button)projectGroupButtons.Controls[index];
                        else
                        {
                            button = MakeButton(string.Empty, (sender, _) =>
                                owner.SwitchInteractiveProjectGroup((InteractiveProjectGroup)((Button)sender).Tag, T("InteractiveGroupSwitch")));
                            projectGroupButtons.Controls.Add(button);
                        }
                        button.Tag = group;
                        string hotkey = FormatHotkey(group.Hotkey);
                        button.Text = group.Name + (hotkey.Length > 0 ? "  [" + hotkey + "]" : string.Empty);
                        bool active = group.Id == owner.CurrentInteractiveProjectGroup.Id;
                        button.BackColor = active ? Color.LightSkyBlue : SystemColors.Control;
                        button.UseVisualStyleBackColor = !active;
                        button.Font = active ? activeGroupFont : Font;
                    }
                }
                finally { projectGroupButtons.ResumeLayout(true); }
            }

            private void EditGroupSwitchSettings()
            {
                using (var dialog = new GroupSwitchSettingsDialog(owner.interactiveGroupSwitchRandom, owner.interactiveGroupSwitchHotkey))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (dialog.Hotkey != null && (owner.HasInteractiveDefaultHotkey(dialog.Hotkey) || owner.interactiveProjectGroups.Any(group => SameInteractiveHotkey(group.Hotkey, dialog.Hotkey)) ||
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
                    if (dialog.Hotkey != null && (owner.IsInteractiveGroupSwitchHotkey(dialog.Hotkey) || owner.HasInteractiveDefaultHotkey(dialog.Hotkey) || owner.interactiveProjectGroups.Any(group => (create || group != current) && SameInteractiveHotkey(group.Hotkey, dialog.Hotkey)) ||
                        owner.AllInteractiveProjectRoutes().Any(route => SameInteractiveHotkey(route.Hotkey, dialog.Hotkey))))
                    { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    var defaults = new InteractiveProjectGroup { Hotkey = dialog.Hotkey }; dialog.ApplyDefaults(defaults);
                    if (owner.InteractiveDefaultHotkeysConflict(defaults, create ? null : current) ||
                        SameInteractiveHotkey(dialog.Hotkey, defaults.DefaultSwitchHotkey) || defaults.DefaultPlaybacks.Any(item => SameInteractiveHotkey(dialog.Hotkey, item.Hotkey)))
                    { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    if (create)
                    {
                        var group = new InteractiveProjectGroup { Name = name, Hotkey = dialog.Hotkey,
                            OverlapEnabled = dialog.OverlapEnabled, OverlapSeconds = dialog.OverlapSeconds };
                        dialog.ApplyDefaults(group);
                        if (dialog.CopyProjects) group.Routes.AddRange(current.Routes.Select(CopyInteractiveProject));
                        owner.interactiveProjectGroups.Add(group);
                    }
                    else { current.Name = name; current.Hotkey = dialog.Hotkey; dialog.ApplyDefaults(current);
                        current.OverlapEnabled = dialog.OverlapEnabled; current.OverlapSeconds = dialog.OverlapSeconds; }
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
                if (!IsHandleCreated || !Visible || !owner.interactiveWindowOpen || owner.closingMainForm || owner.interactiveHotkeysDisabled) return;
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
            private readonly InteractiveProjectGroup defaultSettings = new InteractiveProjectGroup();
            private readonly CheckBox copyBox = new CheckBox { AutoSize = true };
            private readonly CheckBox overlapBox = new CheckBox { AutoSize = true };
            private readonly NumericUpDown overlapSecondsBox = new NumericUpDown { DecimalPlaces = 3, Minimum = 0, Maximum = 5, Increment = 0.01m, Width = 110 };
            public string GroupName => nameBox.Text.Trim();
            public InteractiveHotkey Hotkey => hotkeyBox.Value?.Clone();
            public bool CopyProjects => copyBox.Checked;
            public bool OverlapEnabled => overlapBox.Checked;
            public decimal OverlapSeconds => overlapSecondsBox.Value;
            public Guid DefaultMediaId => defaultSettings.DefaultMediaId;
            public void ApplyDefaults(InteractiveProjectGroup group) { CopyInteractiveDefaults(defaultSettings, group); }
            public ProjectGroupEditDialog(List<InteractiveMediaItem> media, InteractiveProjectGroup group)
            {
                Text = T(group == null ? "InteractiveAddGroup" : "InteractiveEditGroup");
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent; Size = new Size(800, 490); MinimumSize = Size;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoSize = true };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                AddDialogRow(grid, T("InteractiveGroupName"), nameBox, 0);
                AddDialogRow(grid, T("InteractiveHotkey"), hotkeyBox, 1);
                nameBox.Text = group?.Name ?? string.Empty;
                hotkeyBox.Value = group?.Hotkey?.Clone();
                if (group != null) CopyInteractiveDefaults(group, defaultSettings);
                var defaultsButton = MakeButton(T("InteractiveDefaultSettings"), (_, __) =>
                {
                    using (var editor = new DefaultPlaybackSettingsDialog(media, defaultSettings))
                        if (editor.ShowDialog(this) == DialogResult.OK) editor.Apply(defaultSettings);
                });
                AddDialogRow(grid, T("InteractiveGroupDefaultVideo"), defaultsButton, 2);
                var hint = new Label { Text = T("InteractiveDefaultListHint"), AutoSize = true, Dock = DockStyle.Fill };
                grid.Controls.Add(hint, 1, 3);
                overlapBox.Text = T("InteractiveGroupOverlapEnabled"); overlapBox.Checked = group?.OverlapEnabled ?? true;
                overlapSecondsBox.Value = group?.OverlapSeconds ?? 0.1m;
                overlapSecondsBox.Enabled = overlapBox.Checked;
                overlapBox.CheckedChanged += (_, __) => overlapSecondsBox.Enabled = overlapBox.Checked;
                grid.Controls.Add(overlapBox, 1, 4);
                AddDialogRow(grid, T("InteractiveGroupOverlapSeconds"), overlapSecondsBox, 5);
                var overlapHint = new Label { Text = T("InteractiveGroupOverlapHint"), AutoSize = true, Dock = DockStyle.Fill };
                grid.Controls.Add(overlapHint, 1, 6);
                copyBox.Text = T("InteractiveCopyCurrentGroup"); copyBox.Visible = group == null;
                grid.Controls.Add(copyBox, 1, 7);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => { if (GroupName.Length > 0) DialogResult = DialogResult.OK; });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 1, 8);
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
            private readonly ComboBox commandBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
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
                // Constrain the selector to the dialog width; an AutoSize column can
                // otherwise grow to the hint's preferred width and hide the command box.
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.Controls.Add(new Label { Text = T("InteractiveTransitionHint"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
                pairs.Dock = DockStyle.Fill; pairs.View = View.Details; pairs.FullRowSelect = true; pairs.HideSelection = false; pairs.MultiSelect = false;
                pairs.Columns.Add(T("InteractiveTransitionFrom"), 140); pairs.Columns.Add(T("InteractiveTransitionTo"), 140);
                pairs.Columns.Add(T("InteractiveChooseTransition"), 450); pairs.Columns.Add(T("InteractiveTriggerMode"), 110);
                foreach (var transition in transitions)
                {
                    var row = new ListViewItem(groups.First(group => group.Id == transition.FromId).Name);
                    row.SubItems.Add(groups.First(group => group.Id == transition.ToId).Name);
                    row.SubItems.Add(MediaTitle(transition.MediaId)); row.SubItems.Add(transition.Immediate ? "PLAY" : "PLAY_NEXT");
                    row.Tag = transition; pairs.Items.Add(row);
                }
                mediaBox.Items.Add(T("InteractiveNoTransitionMedia"));
                foreach (var item in library) mediaBox.Items.Add(new MediaChoice(item));
                mediaBox.SelectedIndex = 0; mediaBox.Enabled = false;
                mediaBox.SelectedIndexChanged += (_, __) =>
                {
                    if (!refreshingSelection) SetMedia((mediaBox.SelectedItem as MediaChoice)?.Item.RuntimeId ?? Guid.Empty);
                };
                commandBox.Items.AddRange(new object[] { "PLAY", "PLAY_NEXT" });
                commandBox.SelectedIndex = 0; commandBox.Enabled = false;
                commandBox.SelectedIndexChanged += (_, __) =>
                {
                    if (refreshingSelection || pairs.SelectedItems.Count != 1) return;
                    var row = pairs.SelectedItems[0];
                    ((InteractiveGroupTransition)row.Tag).Immediate = commandBox.SelectedIndex == 0;
                    row.SubItems[3].Text = commandBox.SelectedItem as string;
                };
                pairs.SelectedIndexChanged += (_, __) => RefreshSelection();
                pairs.DoubleClick += (_, __) => { if (mediaBox.Enabled) { mediaBox.Focus(); mediaBox.DroppedDown = true; } };
                panel.Controls.Add(pairs, 0, 1);
                var selector = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
                selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); selector.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                selector.Controls.Add(new Label { Text = T("InteractiveChooseTransition"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                selector.Controls.Add(mediaBox, 1, 0);
                selector.Controls.Add(new Label { Text = T("InteractiveTriggerMode"), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 0, 0, 0) }, 2, 0);
                selector.Controls.Add(commandBox, 3, 0); panel.Controls.Add(selector, 0, 2);
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
                    var transition = mediaBox.Enabled ? (InteractiveGroupTransition)pairs.SelectedItems[0].Tag : null;
                    Guid id = transition?.MediaId ?? Guid.Empty;
                    commandBox.Enabled = transition != null && id != Guid.Empty;
                    commandBox.SelectedIndex = transition?.Immediate != false ? 0 : 1;
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
                commandBox.Enabled = id != Guid.Empty;
            }
        }
    }
}
