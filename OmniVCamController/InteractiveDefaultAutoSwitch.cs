using System;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private const int MaximumInteractiveDefaultSeconds = 604800;
        private Guid interactiveTimedDefaultGroupId;
        private Guid interactiveTimedDefaultId;
        private int interactiveTimedDefaultMinimum;
        private int interactiveTimedDefaultMaximum;
        private long interactiveDefaultLastTick;
        private double interactiveDefaultElapsedSeconds;
        private int interactiveDefaultDurationSeconds;
        private bool interactiveDefaultClockRunning;
        private bool interactiveDefaultAutoRequested;

        private static int ReadInteractiveDefaultSeconds(XElement element, string attribute, int fallback)
        {
            int seconds;
            return int.TryParse(GetAttribute(element, attribute, null), out seconds) && seconds >= 1 &&
                seconds <= MaximumInteractiveDefaultSeconds ? seconds : fallback;
        }

        private void SelectInteractiveDefaultReturnTarget()
        {
            if (interactiveCurrent == null || interactiveCurrent.IsGroupDefault || interactiveCurrent.IsGroupTransition) return;
            var group = CurrentInteractiveProjectGroup;
            var target = group.DefaultPlaybacks.FirstOrDefault(item => item.Id == group.ReturnDefaultPlaybackId);
            if (target == null || target.Id == SelectedInteractiveDefault(group)?.Id || ResolveInteractiveDefaultMedia(group, target, true) == null) return;
            group.ActiveDefaultPlaybackId = target.Id;
            SaveActiveConnectionState();
            interactiveForm?.RefreshDefaultPlaybackButtons();
        }

        private void ResetInteractiveDefaultAutoSwitch()
        {
            interactiveTimedDefaultGroupId = Guid.Empty;
            interactiveTimedDefaultId = Guid.Empty;
            interactiveDefaultLastTick = 0;
            interactiveDefaultElapsedSeconds = 0;
            interactiveDefaultDurationSeconds = 0;
            interactiveDefaultClockRunning = false;
            interactiveDefaultAutoRequested = false;
            interactiveForm?.RefreshDefaultPlaybackCountdown();
        }

        private void ObserveInteractiveDefaultOutput(InteractivePlaybackEntry entry, long now)
        {
            var group = interactiveProjectGroups?.FirstOrDefault(candidateGroup => candidateGroup.Id == activeInteractiveProjectGroupId);
            var item = entry?.IsGroupDefault == true ? group?.DefaultPlaybacks.FirstOrDefault(candidate => candidate.Id == entry.DefaultPlaybackId) : null;
            if (item == null || !item.AutoSwitchEnabled)
            {
                ResetInteractiveDefaultAutoSwitch();
                return;
            }
            int minimum = Math.Max(1, Math.Min(MaximumInteractiveDefaultSeconds, item.AutoSwitchMinimumSeconds));
            int maximum = Math.Max(minimum, Math.Min(MaximumInteractiveDefaultSeconds, item.AutoSwitchMaximumSeconds));
            if (interactiveTimedDefaultGroupId == group.Id && interactiveTimedDefaultId == item.Id &&
                interactiveTimedDefaultMinimum == minimum && interactiveTimedDefaultMaximum == maximum) return;
            ResetInteractiveDefaultAutoSwitch();
            interactiveTimedDefaultGroupId = group.Id;
            interactiveTimedDefaultId = item.Id;
            interactiveTimedDefaultMinimum = minimum;
            interactiveTimedDefaultMaximum = maximum;
            interactiveDefaultDurationSeconds = minimum == maximum ? minimum : random.Next(minimum, maximum + 1);
            interactiveDefaultLastTick = now;
            interactiveDefaultClockRunning = true;
            interactiveForm?.RefreshDefaultPlaybackCountdown();
        }

        private string InteractiveDefaultCountdown(InteractiveDefaultPlaybackItem item)
        {
            if (!item.AutoSwitchEnabled || interactiveTimedDefaultId != item.Id ||
                interactiveTimedDefaultGroupId != activeInteractiveProjectGroupId) return string.Empty;
            int remaining = Math.Max(0, (int)Math.Ceiling(interactiveDefaultDurationSeconds - interactiveDefaultElapsedSeconds));
            if (remaining == 0) return T("InteractiveDefaultWaitingSwitch");
            var time = TimeSpan.FromSeconds(remaining);
            string text = string.Format("{0:00}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
            return string.Format(T("InteractiveDefaultCountdown"), text);
        }

        private void AdvanceInteractiveDefaultAutoSwitch(long now, bool playing)
        {
            try { AdvanceInteractiveDefaultAutoSwitchCore(now, playing); }
            finally { interactiveForm?.RefreshDefaultPlaybackCountdown(); }
        }

        private void AdvanceInteractiveDefaultAutoSwitchCore(long now, bool playing)
        {
            if (!interactiveWindowOpen || closingMainForm || !interactiveActive)
            {
                ResetInteractiveDefaultAutoSwitch();
                return;
            }
            if (interactiveTimedDefaultId == Guid.Empty) return;
            if (interactiveCurrent?.IsGroupDefault != true || interactiveCurrent.DefaultPlaybackId != interactiveTimedDefaultId ||
                interactiveTimedDefaultGroupId != activeInteractiveProjectGroupId)
            {
                ResetInteractiveDefaultAutoSwitch();
                return;
            }
            bool running = playing && !interactiveNeedsStatusCheck;
            if (running && interactiveDefaultClockRunning && now >= interactiveDefaultLastTick)
                interactiveDefaultElapsedSeconds += (now - interactiveDefaultLastTick) / (double)Stopwatch.Frequency;
            interactiveDefaultLastTick = now;
            interactiveDefaultClockRunning = running;
            if (!running || interactiveDefaultAutoRequested || interactiveDefaultElapsedSeconds < interactiveDefaultDurationSeconds ||
                processingInteractiveTriggers || interactiveTriggerRequests.Count > 0 || interactiveProtectedHandoff != null ||
                SelectedInteractiveDefault(CurrentInteractiveProjectGroup)?.Id != interactiveTimedDefaultId ||
                interactiveQueue.Any(entry => !entry.IsGroupDefault) || interactivePending != null && !interactivePending.IsGroupDefault) return;
            var next = SelectNextInteractiveDefault();
            if (next == null) return;
            // Submit once. A protected start/middle/end may take time to hand over;
            // its deadline cannot create further switches while that handoff waits.
            interactiveDefaultAutoRequested = true;
            SwitchInteractiveDefault(next, T("InteractiveDefaultAutoSwitchSource"));
        }
    }
}
