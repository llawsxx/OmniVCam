using System;
using System.Linq;
using System.Threading.Tasks;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private static readonly TimeSpan InteractivePlaybackMismatchTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InteractivePlaybackRetryInterval = TimeSpan.FromSeconds(3);
        private bool interactiveAwaitingPendingConfirmation;
        private DateTime interactiveMismatchSince;
        private InteractivePlaybackEntry interactiveRecoveryEntry;
        private DateTime interactiveRecoveryRetryAt;
        private Task interactiveRecoveryTask;

        private void ResetInteractivePlaybackWatchdog()
        {
            interactiveAwaitingPendingConfirmation = false;
            interactiveMismatchSince = DateTime.MinValue;
            interactiveRecoveryEntry = null;
            interactiveRecoveryRetryAt = DateTime.MinValue;
        }

        private bool MarkInteractivePlaybackMismatch()
        {
            if (!interactiveActive) return false;
            if (interactiveMismatchSince == DateTime.MinValue) interactiveMismatchSince = DateTime.UtcNow;
            interactiveNeedsStatusCheck = true;
            interactiveForm?.RefreshRuntime();
            return false;
        }

        private void HandleBackendConnectionClosed()
        {
            if (IsDisposed || closingMainForm) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(HandleBackendConnectionClosed)); }
                catch (InvalidOperationException) { }
                return;
            }
            ObserveBackendConnectionLoss();
        }

        private void ObserveBackendConnectionLoss()
        {
            if (commandClient == null) return;
            int lossVersion = commandClient.ConnectionLossVersion;
            if (lossVersion == observedBackendConnectionLoss) return;
            observedBackendConnectionLoss = lossVersion;
            if (!interactiveActive && interactiveCurrent == null && interactivePending == null && interactiveQueue.Count == 0) return;
            // A lost control connection says nothing about whether output playback stopped.
            // Invalidate old callbacks, but retain the planned playback and protected handoff until STATUS confirms output.
            interactiveRuntimeVersion++;
            MarkInteractivePlaybackMismatch();
            interactiveBackendEvents.Clear();
            interactiveLastEndedFrameId = 0;
            interactiveStartingEntry = null;
            if (interactiveSendingNext != null)
            {
                PrependInteractiveEntry(interactiveSendingNext);
                interactiveSendingNext = null;
            }
            interactiveNextQueueTask = null;
            if (interactiveCurrent != null) { interactiveCurrent.BackendFrameId = 0; interactiveCurrent.MinimumBackendFrameId = 0; }
            if (interactivePending != null) { interactivePending.BackendFrameId = 0; interactivePending.MinimumBackendFrameId = 0; }
            foreach (var entry in interactiveQueue) { entry.BackendFrameId = 0; entry.MinimumBackendFrameId = 0; }
            AppendInteractiveLog(T("InteractiveBackendDisconnected"));
            interactiveForm?.RefreshRuntime();
        }

        private void PrependInteractiveEntry(InteractivePlaybackEntry entry)
        {
            var retained = interactiveQueue.ToList();
            interactiveQueue.Clear();
            interactiveQueue.Enqueue(entry);
            foreach (var item in retained) interactiveQueue.Enqueue(item);
        }

        private void PrependInteractiveTrigger(InteractiveTriggerRequest request)
        {
            var retained = interactiveTriggerRequests.ToList();
            interactiveTriggerRequests.Clear();
            interactiveTriggerRequests.Enqueue(request);
            foreach (var item in retained) interactiveTriggerRequests.Enqueue(item);
        }

        private async Task<bool> VerifyInteractivePlaybackAsync()
        {
            if (interactiveCurrent == null && !interactiveNeedsStatusCheck) return true;
            int version = interactiveRuntimeVersion;
            int revision = interactiveStatusRevision;
            int generation = commandClient.Generation;
            string reply = await SendRawCommandAsync("STATUS");
            if (generation != commandClient.Generation) return false;
            return await ReconcileInteractiveBackendStatusAsync(reply, version, revision, false);
        }

        private async Task<bool> ReconcileInteractiveBackendStatusAsync(string reply, int version, int revision, bool queueNext)
        {
            await WaitForInteractiveNextQueueAsync();
            // A status response requested before an acknowledged transition cannot override that newer state.
            if (version != interactiveRuntimeVersion || revision != interactiveStatusRevision) return false;
            if (!ApplyInteractiveBackendStatus(reply))
            {
                if (queueNext) await RecoverInteractivePlaybackAsync();
                return false;
            }
            if (queueNext && interactiveActive && !interactiveNeedsStatusCheck) await EnsureInteractiveNextQueuedAsync();
            if (!interactiveNeedsStatusCheck && interactiveTriggerRequests.Count > 0 && !processingInteractiveTriggers)
                _ = ProcessInteractiveTriggerRequestsAsync();
            return true;
        }

        private bool ApplyInteractiveOutputStarted(BackendInputEvent inputEvent)
        {
            if (inputEvent.FrameId <= 0 || inputEvent.FrameId <= interactiveLastEndedFrameId || IsObsoleteInteractiveTransitionFrame(inputEvent.FrameId)) return false;
            // After PLAY, the first matching output confirms the current invocation. A same-file
            // lookahead needs its own known frame ID before it can take precedence over that event.
            bool confirmsImmediateCurrent = interactiveCurrent != null && interactiveCurrent.BackendFrameId == 0 &&
                !interactiveCurrent.RequiresOutputConfirmation && inputEvent.FrameId > interactiveCurrent.MinimumBackendFrameId &&
                InputSummariesEqual(inputEvent.Input, interactiveCurrent.InputSummary) &&
                interactivePending?.BackendFrameId != inputEvent.FrameId;
            bool pendingMatches = !confirmsImmediateCurrent && interactivePending != null && inputEvent.FrameId > interactivePending.MinimumBackendFrameId &&
                InputSummariesEqual(inputEvent.Input, interactivePending.InputSummary) &&
                (interactivePending.BackendFrameId == 0 || interactivePending.BackendFrameId == inputEvent.FrameId || interactivePending.Loop) &&
                (interactiveCurrent == null || !InputSummariesEqual(inputEvent.Input, interactiveCurrent.InputSummary) ||
                    interactiveCurrent.BackendFrameId > 0 && interactiveCurrent.BackendFrameId != inputEvent.FrameId ||
                    interactivePending.RequiresOutputConfirmation && interactivePending.MinimumBackendFrameId > 0);
            if (pendingMatches)
            {
                interactivePending.BackendFrameId = inputEvent.FrameId;
                ActivateVerifiedInteractiveEntry(interactivePending);
            }
            else if (!interactiveAwaitingPendingConfirmation && interactiveCurrent != null &&
                InputSummariesEqual(inputEvent.Input, interactiveCurrent.InputSummary) && inputEvent.FrameId > interactiveCurrent.MinimumBackendFrameId &&
                (interactiveCurrent.BackendFrameId == 0 || interactiveCurrent.BackendFrameId == inputEvent.FrameId || interactiveCurrent.Loop))
            {
                interactiveCurrent.BackendFrameId = inputEvent.FrameId;
                SetInteractiveObsPlayback(interactiveCurrent);
                interactiveStatusRevision++;
                interactiveNeedsStatusCheck = false;
                ResetInteractivePlaybackWatchdog();
                CompleteInteractiveProtectedHandoff();
            }
            else return false;
            currentActualStatusInput = inputEvent.Input;
            currentStatusState = "playing";
            interactiveForm?.RefreshRuntime();
            return true;
        }

        private bool ApplyInteractiveBackendStatus(string reply)
        {
            if (reply == null || !reply.StartsWith("OK seconds=", StringComparison.OrdinalIgnoreCase)) return MarkInteractivePlaybackMismatch();
            if (interactiveStartingEntry != null) return false;
            string state = ParseStatusString(reply, "state=");
            bool running = state.Equals("playing", StringComparison.OrdinalIgnoreCase) || state.Equals("paused", StringComparison.OrdinalIgnoreCase);
            bool terminal = state.Equals("stopped", StringComparison.OrdinalIgnoreCase) || state.Equals("ended", StringComparison.OrdinalIgnoreCase) || state.Equals("error", StringComparison.OrdinalIgnoreCase);
            if (!running && !terminal) return MarkInteractivePlaybackMismatch();
            string actual = ParseStatusString(reply, "actual_input=");
            if (running && string.IsNullOrEmpty(actual)) return MarkInteractivePlaybackMismatch();
            string decoded = ParseStatusString(reply, "input=");
            long actualFrameId = ParseStatusLong(reply, "actual_frame_id=");
            long decodedFrameId = ParseStatusLong(reply, "input_frame_id=");
            interactiveObservedBackendFrameId = Math.Max(actualFrameId, decodedFrameId);
            if (terminal && state.Equals("ended", StringComparison.OrdinalIgnoreCase) &&
                interactiveCurrent != null && interactivePending == null && interactiveQueue.Count == 0 &&
                (interactiveCurrent.IsGroupTransition || (!interactiveCurrent.Loop &&
                    (interactiveCurrent.GroupLoop == null || interactiveCurrent.GroupLoop.Cancelled) &&
                    (CurrentInteractiveProjectGroup.DefaultMediaId != Guid.Empty || interactiveCurrent.ObsPlayback != null))) &&
                InputSummariesEqual(actual, interactiveCurrent.InputSummary) && !IsObsoleteInteractiveTransitionFrame(actualFrameId) &&
                (actualFrameId == 0 || actualFrameId > interactiveCurrent.MinimumBackendFrameId) &&
                (actualFrameId == 0 || interactiveCurrent.BackendFrameId == 0 || actualFrameId == interactiveCurrent.BackendFrameId))
            {
                ResetInteractivePlaybackWatchdog();
                interactiveNeedsStatusCheck = false;
                if (actualFrameId > 0) interactiveCurrent.BackendFrameId = actualFrameId;
                SetInteractiveObsPlayback(null);
                QueueInteractiveGroupDefaultIfNeeded();
                if (interactiveQueue.Count > 0) return true;
                interactiveActive = false;
                interactiveCurrent = null;
                interactiveStatusRevision++;
                AppendInteractiveLog(T("InteractiveSequenceCompleted"));
                interactiveForm?.RefreshRuntime();
                return true;
            }
            if (interactivePending != null && InputSummariesEqual(decoded, interactivePending.InputSummary) &&
                decodedFrameId > interactivePending.MinimumBackendFrameId && decodedFrameId != (interactiveCurrent?.BackendFrameId ?? 0))
                interactivePending.BackendFrameId = decodedFrameId;
            bool pendingMatches = running && interactivePending != null && InputSummariesEqual(actual, interactivePending.InputSummary) &&
                (actualFrameId > 0 ? actualFrameId > interactivePending.MinimumBackendFrameId &&
                    (interactivePending.BackendFrameId == 0 || actualFrameId == interactivePending.BackendFrameId || interactivePending.Loop) :
                    !interactivePending.RequiresOutputConfirmation || !InputSummariesEqual(actual, interactiveCurrent?.InputSummary));
            bool wasUnverified = interactiveNeedsStatusCheck;
            if (running && !interactiveAwaitingPendingConfirmation && interactiveCurrent != null && InputSummariesEqual(actual, interactiveCurrent.InputSummary) && !IsObsoleteInteractiveTransitionFrame(actualFrameId) &&
                (actualFrameId == 0 || actualFrameId > interactiveCurrent.MinimumBackendFrameId) &&
                (actualFrameId == 0 || interactiveCurrent.BackendFrameId == 0 || actualFrameId == interactiveCurrent.BackendFrameId ||
                    (interactiveCurrent.Loop && !pendingMatches)))
            {
                if (actualFrameId > 0) interactiveCurrent.BackendFrameId = actualFrameId;
                SetInteractiveObsPlayback(interactiveCurrent);
                interactiveNeedsStatusCheck = false;
                ResetInteractivePlaybackWatchdog();
                // If the next item was not opened, re-submit it once after reconnection. This replaces the backend's
                // pending command if it survived; it does not replay an input already decoding into output queues.
                if (wasUnverified && interactivePending != null && !InputSummariesEqual(decoded, interactivePending.InputSummary))
                {
                    interactiveStatusRevision++;
                    PrependInteractiveEntry(interactivePending);
                    interactivePending = null;
                    interactivePendingSource = string.Empty;
                }
                CompleteInteractiveProtectedHandoff();
                if (wasUnverified) AppendInteractiveLog(T("InteractivePlaybackVerified") + interactiveCurrent.Title);
            }
            else if (pendingMatches)
            {
                if (actualFrameId > 0) interactivePending.BackendFrameId = actualFrameId;
                ActivateVerifiedInteractiveEntry(interactivePending);
            }
            else if (running && !interactiveAwaitingPendingConfirmation && interactivePending?.RequiresOutputConfirmation != true && interactiveRecoveryEntry == null && interactiveQueue.Any(entry => InputSummariesEqual(actual, entry.InputSummary)))
            {
                InteractivePlaybackEntry entry;
                do { entry = interactiveQueue.Dequeue(); } while (!InputSummariesEqual(actual, entry.InputSummary));
                ActivateVerifiedInteractiveEntry(entry);
            }
            else if (running && interactiveCurrent == null && interactivePending != null && InputSummariesEqual(actual, interactivePendingSource))
            {
                // PLAY_NEXT may intentionally be waiting for a normal (non-interactive) input to finish.
                interactiveNeedsStatusCheck = false;
                ResetInteractivePlaybackWatchdog();
                if (wasUnverified && !InputSummariesEqual(decoded, interactivePending.InputSummary))
                {
                    interactiveStatusRevision++;
                    PrependInteractiveEntry(interactivePending);
                    interactivePending = null;
                    interactivePendingSource = string.Empty;
                }
            }
            else if (interactiveCurrent != null || interactivePending != null || interactiveNeedsStatusCheck)
            {
                return MarkInteractivePlaybackMismatch();
            }
            else
            {
                interactiveNeedsStatusCheck = false;
            }
            currentStatusInput = decoded;
            currentActualStatusInput = actual;
            currentStatusState = state;
            interactiveForm?.RefreshRuntime();
            return true;
        }

        private Task RecoverInteractivePlaybackAsync()
        {
            if (interactiveRecoveryTask == null || interactiveRecoveryTask.IsCompleted)
                interactiveRecoveryTask = RecoverInteractivePlaybackCoreAsync(SendRawCommandAsync);
            return interactiveRecoveryTask;
        }

        private async Task RecoverInteractivePlaybackCoreAsync(Func<string, Task<string>> send)
        {
            int version = interactiveRuntimeVersion;
            await WaitForInteractiveNextQueueAsync();
            if (version != interactiveRuntimeVersion || !interactiveActive || !interactiveNeedsStatusCheck) return;
            if (interactiveRecoveryEntry == null)
            {
                if (interactiveMismatchSince == DateTime.MinValue || DateTime.UtcNow - interactiveMismatchSince < InteractivePlaybackMismatchTimeout) return;
                QueueInteractiveGroupContinuation();
                InteractivePlaybackEntry next = interactivePending;
                if (next == null && interactiveQueue.Count == 0 && interactiveTriggerRequests.Count > 0)
                {
                    var request = interactiveTriggerRequests.Dequeue();
                    foreach (var entry in request.Entries) interactiveQueue.Enqueue(entry);
                }
                if (next == null && interactiveQueue.Count > 0) next = interactiveQueue.Dequeue();
                if (next == null)
                {
                    ClearInteractiveRuntime(false);
                    AppendInteractiveLog(T("InteractivePlaybackNoNext"));
                    return;
                }
                // Select exactly one replacement. Keep it selected through failed sends and reconnects.
                // Protection applies only while the protected input is actually playing.
                version = ++interactiveRuntimeVersion;
                interactiveStatusRevision++;
                interactiveBackendEvents.Clear();
                interactiveLastEndedFrameId = 0;
                next.BackendFrameId = 0;
                next.MinimumBackendFrameId = 0;
                next.ReplaceAfterFrameId = 0;
                next.RequiresOutputConfirmation = false;
                interactiveCurrent = next;
                interactivePending = null;
                interactivePendingSource = string.Empty;
                interactiveStartingEntry = null;
                interactiveRecoveryEntry = next;
                interactiveRecoveryRetryAt = DateTime.MinValue;
                AppendInteractiveLog(T("InteractivePlaybackAutoNext") + next.Title);
                interactiveForm?.RefreshRuntime();
            }
            if (DateTime.UtcNow < interactiveRecoveryRetryAt) return;
            var target = interactiveRecoveryEntry;
            interactiveRecoveryRetryAt = DateTime.UtcNow.Add(InteractivePlaybackRetryInterval);
            string command = BuildInteractivePlayCommand("PLAY", target);
            string reply = await send(command);
            if (version != interactiveRuntimeVersion || !interactiveActive || interactiveRecoveryEntry != target) return;
            AppendInteractiveLog("> " + command + Environment.NewLine + "< " + reply);
            // An acknowledgement alone does not prove actual output. STATUS must verify this target
            // before prefetching the following item or allowing another timeout to advance the queue.
            if (reply == null || !reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                AppendInteractiveLog(T("InteractivePlaybackRetry") + target.Title);
        }

        private void ActivateVerifiedInteractiveEntry(InteractivePlaybackEntry entry)
        {
            interactiveStatusRevision++;
            CountCompletedInteractiveMiddle(entry);
            interactiveCurrent = entry;
            SetInteractiveObsPlayback(entry);
            entry.RequiresOutputConfirmation = false;
            entry.ReplaceAfterFrameId = 0;
            interactivePending = null;
            interactivePendingSource = string.Empty;
            interactiveNeedsStatusCheck = false;
            ResetInteractivePlaybackWatchdog();
            CompleteInteractiveProtectedHandoff();
            AppendInteractiveLog(T("InteractiveNowPlaying") + entry.Title);
        }
    }
}
