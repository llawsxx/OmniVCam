using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private sealed class InteractiveDefaultPlaybackItem
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public string Name { get; set; } = string.Empty;
            public Guid MediaId { get; set; }
            public InteractiveHotkey Hotkey { get; set; }
            public bool LoopMiddleFiles { get; set; } = true;
            public InteractiveGroupInterruptionMode? InterruptionMode { get; set; }
        }

        private static InteractiveDefaultPlaybackItem SelectedInteractiveDefault(InteractiveProjectGroup group)
        {
            return group.DefaultPlaybacks.FirstOrDefault(item => item.Id == group.ActiveDefaultPlaybackId)
                ?? group.DefaultPlaybacks.FirstOrDefault();
        }

        private string InteractiveDefaultName(InteractiveDefaultPlaybackItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Name)) return item.Name;
            return interactiveMedia.FirstOrDefault(media => media.RuntimeId == item.MediaId)?.Title ?? T("InteractiveNoDefaultMedia");
        }

        private static InteractiveDefaultPlaybackItem CopyInteractiveDefault(InteractiveDefaultPlaybackItem item)
        {
            return new InteractiveDefaultPlaybackItem { Id = item.Id, Name = item.Name, MediaId = item.MediaId,
                Hotkey = item.Hotkey?.Clone(), LoopMiddleFiles = item.LoopMiddleFiles, InterruptionMode = item.InterruptionMode };
        }

        private static void CopyInteractiveDefaults(InteractiveProjectGroup source, InteractiveProjectGroup target)
        {
            target.DefaultPlaybacks = source.DefaultPlaybacks.Select(CopyInteractiveDefault).ToList();
            target.ActiveDefaultPlaybackId = SelectedInteractiveDefault(source)?.Id ?? Guid.Empty;
            target.DefaultSwitchRandom = source.DefaultSwitchRandom;
            target.DefaultSwitchHotkey = source.DefaultSwitchHotkey?.Clone();
        }

        private static XElement SaveInteractiveDefaults(InteractiveProjectGroup group)
        {
            return new XElement("DefaultPlaybacks", new XAttribute("active", SelectedInteractiveDefault(group)?.Id ?? Guid.Empty),
                new XAttribute("random", group.DefaultSwitchRandom),
                new XAttribute("hotkey", group.DefaultSwitchHotkey?.KeyCode.ToString() ?? string.Empty),
                new XAttribute("modifiers", group.DefaultSwitchHotkey?.Modifiers.ToString() ?? string.Empty),
                group.DefaultPlaybacks.Select(item => new XElement("Default",
                    new XAttribute("id", item.Id), new XAttribute("name", item.Name), new XAttribute("media", item.MediaId),
                    new XAttribute("loopMiddle", item.LoopMiddleFiles),
                    new XAttribute("interruption", item.InterruptionMode?.ToString() ?? "inherit"),
                    new XAttribute("hotkey", item.Hotkey?.KeyCode.ToString() ?? string.Empty),
                    new XAttribute("modifiers", item.Hotkey?.Modifiers.ToString() ?? string.Empty))));
        }

        private static void LoadInteractiveDefaults(XElement node, InteractiveProjectGroup group)
        {
            var settings = node.Element("DefaultPlaybacks");
            if (settings == null) return; // The legacy defaultMedia attribute was loaded as one default item.
            group.DefaultPlaybacks.Clear();
            Guid active;
            group.ActiveDefaultPlaybackId = Guid.TryParse(GetAttribute(settings, "active", null), out active) ? active : Guid.Empty;
            group.DefaultSwitchRandom = GetAttribute(settings, "random", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            group.DefaultSwitchHotkey = ParseInteractiveHotkey(GetAttribute(settings, "hotkey", string.Empty), GetAttribute(settings, "modifiers", string.Empty));
            foreach (var element in settings.Elements("Default"))
            {
                Guid id, media;
                if (!Guid.TryParse(GetAttribute(element, "media", null), out media) || media == Guid.Empty) continue;
                var item = new InteractiveDefaultPlaybackItem { MediaId = media,
                    Name = GetAttribute(element, "name", string.Empty),
                    LoopMiddleFiles = !GetAttribute(element, "loopMiddle", "true").Equals("false", StringComparison.OrdinalIgnoreCase),
                    Hotkey = ParseInteractiveHotkey(GetAttribute(element, "hotkey", string.Empty), GetAttribute(element, "modifiers", string.Empty)) };
                if (Guid.TryParse(GetAttribute(element, "id", null), out id) && id != Guid.Empty && !group.DefaultPlaybacks.Any(entry => entry.Id == id)) item.Id = id;
                InteractiveGroupInterruptionMode mode;
                if (Enum.TryParse(GetAttribute(element, "interruption", "inherit"), out mode) && Enum.IsDefined(typeof(InteractiveGroupInterruptionMode), mode)) item.InterruptionMode = mode;
                group.DefaultPlaybacks.Add(item);
            }
            group.ActiveDefaultPlaybackId = SelectedInteractiveDefault(group)?.Id ?? Guid.Empty;
        }

        private InteractiveDefaultPlaybackItem SelectNextInteractiveDefault()
        {
            var group = CurrentInteractiveProjectGroup;
            var current = SelectedInteractiveDefault(group);
            if (group.DefaultPlaybacks.Count < 2) return null;
            if (!group.DefaultSwitchRandom) return group.DefaultPlaybacks[(group.DefaultPlaybacks.IndexOf(current) + 1) % group.DefaultPlaybacks.Count];
            var candidates = group.DefaultPlaybacks.Where(item => item.Id != current.Id).ToList();
            return candidates[random.Next(candidates.Count)];
        }

        private void SwitchNextInteractiveDefault(string source)
        {
            if (!interactiveWindowOpen || closingMainForm) return;
            var next = SelectNextInteractiveDefault();
            if (next != null) SwitchInteractiveDefault(next, source);
        }

        private void SwitchInteractiveDefault(InteractiveDefaultPlaybackItem target, string source)
        {
            if (!interactiveWindowOpen || closingMainForm || target == null) return;
            var group = CurrentInteractiveProjectGroup;
            if (!group.DefaultPlaybacks.Contains(target)) return;
            bool selected = SelectedInteractiveDefault(group)?.Id == target.Id;
            if (selected && (interactiveActive && interactiveCurrent?.IsGroupDefault == true && interactiveCurrent.DefaultPlaybackId == target.Id ||
                interactiveTriggerRequests.Any(request => request.IsDefaultPlaybackSwitch && request.DefaultPlaybackId == target.Id) ||
                interactiveProcessingTrigger?.IsDefaultPlaybackSwitch == true && interactiveProcessingTrigger.DefaultPlaybackId == target.Id ||
                interactiveProtectedHandoff?.Request.IsDefaultPlaybackSwitch == true && interactiveProtectedHandoff.Request.DefaultPlaybackId == target.Id)) return;
            if (ResolveInteractiveDefaultMedia(group, target, true) == null) return;
            bool defer = interactiveCurrent != null && !interactiveCurrent.IsGroupDefault;
            var entries = defer ? null : BuildInteractiveDefaultPlayback(group, target, false);
            group.ActiveDefaultPlaybackId = target.Id;
            SaveActiveConnectionState();
            ObserveBackendConnectionLoss();
            // Latest default selection replaces waiting idle requests. Real project and transition requests retain their order.
            var waiting = interactiveTriggerRequests.Where(request => request.IsGroupTransition || !request.Entries.All(entry => entry.IsGroupDefault)).ToList();
            interactiveTriggerRequests.Clear();
            foreach (var request in waiting) interactiveTriggerRequests.Enqueue(request);
            if (defer) _ = RefreshInteractiveDefaultDestinationAsync(group, target);
            else
            {
                if (!interactiveActive && interactiveTriggerRequests.Count == 0) BeginInteractiveMode();
                interactiveTriggerRequests.Enqueue(new InteractiveTriggerRequest
                {
                    RouteName = InteractiveDefaultName(target), Source = source, Immediate = true, Entries = entries, IsDefaultPlaybackSwitch = true,
                    ProjectGroupId = group.Id, ProjectGroupVersion = interactiveProjectGroupVersion, DefaultPlaybackId = target.Id
                });
                if (interactiveProtectedHandoff != null && !interactiveProtectedHandoff.Request.IsGroupTransition &&
                    interactiveProtectedHandoff.Request.Entries.All(entry => entry.IsGroupDefault)) CancelInteractiveProtectedHandoff();
            }
            AppendInteractiveLog(source + " - " + string.Format(T("InteractiveDefaultSwitched"), InteractiveDefaultName(target)));
            interactiveForm?.RefreshDefaultPlaybackButtons();
            interactiveForm?.RefreshRuntime();
            if (!defer && !processingInteractiveTriggers) _ = ProcessInteractiveTriggerRequestsAsync();
        }

        private bool DeferInteractiveDefaultSwitch(InteractiveTriggerRequest request)
        {
            if (!request.IsDefaultPlaybackSwitch || interactiveCurrent == null || interactiveCurrent.IsGroupDefault) return false;
            var group = CurrentInteractiveProjectGroup;
            var target = SelectedInteractiveDefault(group);
            if (target != null) _ = RefreshInteractiveDefaultDestinationAsync(group, target);
            return true;
        }

        private async Task RefreshInteractiveDefaultDestinationAsync(InteractiveProjectGroup group, InteractiveDefaultPlaybackItem target)
        {
            int version = interactiveRuntimeVersion;
            int groupVersion = interactiveProjectGroupVersion;
            await WaitForInteractiveNextQueueAsync();
            if (!interactiveWindowOpen || closingMainForm || version != interactiveRuntimeVersion ||
                groupVersion != interactiveProjectGroupVersion || CurrentInteractiveProjectGroup != group ||
                SelectedInteractiveDefault(group)?.Id != target.Id) return;
            // The project may have finished while its lookahead command was being acknowledged.
            if (interactiveCurrent?.IsGroupDefault == true) { SwitchInteractiveDefault(target, T("InteractiveDefaultSwitch")); return; }
            var retained = interactiveQueue.Where(entry => !entry.IsGroupDefault).ToList();
            bool hasDefaults = retained.Count != interactiveQueue.Count;
            var discarded = interactivePending?.IsGroupDefault == true ? interactivePending : null;
            if (!hasDefaults && discarded == null) return; // Normal completion will resolve the selected default later.
            interactiveQueue.Clear();
            foreach (var entry in retained) interactiveQueue.Enqueue(entry);
            if (discarded == null) return;
            interactivePending = null;
            interactivePendingSource = string.Empty;
            // Replace only the idle lookahead. The project's remaining media and loops stay intact.
            QueueInteractiveGroupDefaultIfNeeded();
            if (interactiveQueue.Count > 0) MarkInteractivePhasedReplacement(interactiveQueue.Peek(), discarded);
            await EnsureInteractiveNextQueuedAsync();
            interactiveForm?.RefreshRuntime();
        }

        // Only an explicit default-to-default selection uses this override. Projects,
        // transitions and automatic idle playback retain the library snapshot's policy.
        private static InteractiveGroupInterruptionMode InteractiveInterruptionMode(InteractivePlaybackEntry entry, InteractiveTriggerRequest request)
        {
            if (entry?.IsGroupDefault == true && request.IsDefaultPlaybackSwitch && entry.DefaultSwitchInterruptionMode.HasValue)
                return entry.DefaultSwitchInterruptionMode.Value;
            return entry?.PhaseRun?.Media.FileGroupInterruptionMode ?? InteractiveGroupInterruptionMode.Any;
        }

        private static bool PreventsInteractiveInterruption(InteractivePlaybackEntry entry, InteractiveTriggerRequest request)
        {
            if (entry.PhaseRun != null) return InteractiveInterruptionMode(entry, request) != InteractiveGroupInterruptionMode.Any;
            if (entry.IsGroupDefault && request.IsDefaultPlaybackSwitch && entry.DefaultSwitchInterruptionMode.HasValue)
                return entry.DefaultSwitchInterruptionMode != InteractiveGroupInterruptionMode.Any &&
                    entry.DefaultSwitchInterruptionMode != InteractiveGroupInterruptionMode.ImmediateMiddle;
            return entry.PreventInterrupt;
        }

        private bool HasInteractiveDefaultHotkey(InteractiveHotkey hotkey)
        {
            return hotkey != null && interactiveProjectGroups.Any(group => SameInteractiveHotkey(group.DefaultSwitchHotkey, hotkey) ||
                group.DefaultPlaybacks.Any(item => SameInteractiveHotkey(item.Hotkey, hotkey)));
        }

        private bool InteractiveDefaultHotkeysConflict(InteractiveProjectGroup edited, InteractiveProjectGroup original)
        {
            var keys = edited.DefaultPlaybacks.Select(item => item.Hotkey).Concat(new[] { edited.DefaultSwitchHotkey })
                .Where(key => key != null && key.KeyCode != Keys.None).ToList();
            return keys.Select((key, index) => keys.Skip(index + 1).Any(other => SameInteractiveHotkey(key, other))).Any(match => match) ||
                keys.Any(key => SameInteractiveHotkey(edited.Hotkey, key) || IsInteractiveGroupSwitchHotkey(key) || AllInteractiveProjectRoutes().Any(route => SameInteractiveHotkey(route.Hotkey, key)) ||
                    interactiveProjectGroups.Any(group => group != original && (SameInteractiveHotkey(group.Hotkey, key) ||
                        SameInteractiveHotkey(group.DefaultSwitchHotkey, key) || group.DefaultPlaybacks.Any(item => SameInteractiveHotkey(item.Hotkey, key)))));
        }
    }
}
