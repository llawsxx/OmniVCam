using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private InteractiveTriggerRequest BuildInteractiveComboRequest(InteractiveRouteItem route)
        {
            var current = interactiveActive ? interactiveCurrent : null;
            var run = current?.PhaseRun;
            if (!route.ComboEnabled || run == null || (current.Phase != InteractiveGroupPhase.Middle && current.Phase != InteractiveGroupPhase.Start) ||
                run.RouteId != route.RuntimeId || current.IsGroupDefault || current.IsGroupTransition) return null;
            string path = SelectInteractiveMediaPath(run.Media, random);
            if (path == null) return null;
            var next = CreateInteractivePhasedEntry(run, path, InteractiveGroupPhase.Middle);
            next.IsComboMiddle = true;
            next.GroupIteration = current.GroupIteration;
            next.ObsPlayback = current.ObsPlayback;
            return new InteractiveTriggerRequest
            {
                ComboRun = run, ComboRoute = route,
                Entries = new List<InteractivePlaybackEntry> { next }
            };
        }

        private InteractivePlaybackEntry RetainInteractiveComboPending(InteractivePlaybackEntry entry)
        {
            // Reissuing a discarded lookahead starts another backend invocation of this entry.
            var copy = entry.CopyForQueue();
            copy.BackendFrameId = 0;
            copy.MinimumBackendFrameId = Math.Max(entry.MinimumBackendFrameId,
                Math.Max(entry.BackendFrameId, interactiveObservedBackendFrameId));
            copy.RequiresOutputConfirmation = true;
            copy.ReplaceAfterFrameId = 0;
            if (entry.PhaseRun?.Outro == entry) entry.PhaseRun.Outro = copy;
            return copy;
        }

        private void PreserveInteractiveComboLoopBoundary(InteractiveFileGroupRun run,
            InteractivePlaybackEntry current, IEnumerable<InteractivePlaybackEntry> future)
        {
            if (run.Loop == null || run.Loop.Cancelled || run.Ending) return;
            // Combo hits extend the playing pass, including when the backend has already
            // reserved the next pass. Its final remaining middle owns the loop boundary.
            var pass = new[] { current, interactivePending }.Concat(future).Where(entry => entry != null &&
                entry.PhaseRun == run && entry.Phase == InteractiveGroupPhase.Middle &&
                entry.GroupIteration == current.GroupIteration).Distinct().ToList();
            foreach (var entry in pass) entry.GroupEnd = false;
            if (pass.Count > 0) pass[pass.Count - 1].GroupEnd = true;
        }

        private async Task<bool> TryPlayInteractiveComboAsync(InteractiveTriggerRequest request)
        {
            var run = request.ComboRun;
            if (run == null) return false;
            var current = interactiveActive ? interactiveCurrent : null;
            if (current?.PhaseRun != run || (current.Phase != InteractiveGroupPhase.Middle && current.Phase != InteractiveGroupPhase.Start) ||
                current.RouteId != request.ComboRoute.RuntimeId || current.IsGroupDefault || current.IsGroupTransition)
            {
                // STATUS may reveal that the source finished while the trigger was waiting.
                // Preserve the trigger by treating it as a normal project invocation at its new boundary.
                request.ComboRun = null;
                request.Entries = BuildInteractiveEntries(request.ComboRoute);
                if (request.Entries.Count > 0) return false;
                AppendInteractiveLog(T("InteractiveNoPlayableMedia"));
                return true;
            }
            var next = request.Entries[0];
            // The same run may have advanced while STATUS or another command was in flight.
            next.GroupIteration = current.GroupIteration;
            var retained = interactiveQueue.ToList();
            bool wait = run.Media.FileGroupInterruptionMode == InteractiveGroupInterruptionMode.AfterMiddle ||
                run.Media.FileGroupInterruptionMode == InteractiveGroupInterruptionMode.WholeGroup ||
                current.Phase == InteractiveGroupPhase.Start && run.Media.FileGroupInterruptionMode != InteractiveGroupInterruptionMode.Any;
            AppendInteractiveLog(T(wait ? "InteractiveComboWaiting" : "InteractiveComboPlaying") + next.Title);
            if (wait)
            {
                // Several hits arriving during one protected start or middle keep their trigger order.
                int prefix = retained.TakeWhile(entry => entry.IsComboMiddle && entry.PhaseRun == run).Count();
                if (interactivePending?.IsComboMiddle == true && interactivePending.PhaseRun == run ||
                    interactivePending == null && prefix > 0)
                {
                    retained.Insert(prefix, next);
                    PreserveInteractiveComboLoopBoundary(run, current, retained);
                    interactiveQueue.Clear();
                    foreach (var entry in retained) interactiveQueue.Enqueue(entry);
                    interactiveStatusRevision++;
                    await EnsureInteractiveNextQueuedAsync();
                    return true;
                }
            }
            var discarded = interactivePending;
            if (discarded != null) retained.Insert(0, RetainInteractiveComboPending(discarded));
            retained.Insert(0, next);
            PreserveInteractiveComboLoopBoundary(run, current, retained);
            if (!wait)
            {
                // Only arbitrary interruption can skip the start; middle interruption also allows a direct switch here.
                await PlayInteractiveEntriesImmediatelyAsync(retained);
                return true;
            }
            interactiveQueue.Clear();
            foreach (var entry in retained) interactiveQueue.Enqueue(entry);
            interactivePending = null;
            interactivePendingSource = string.Empty;
            if (discarded != null) MarkInteractivePhasedReplacement(next, discarded);
            interactiveStatusRevision++;
            await EnsureInteractiveNextQueuedAsync();
            return true;
        }
    }
}
