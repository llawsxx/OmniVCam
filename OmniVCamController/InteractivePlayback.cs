using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private List<InteractiveMediaItem> interactiveMedia = new List<InteractiveMediaItem>();
        private List<InteractiveRouteItem> interactiveRoutes = new List<InteractiveRouteItem>();
        private readonly Queue<InteractivePlaybackEntry> interactiveQueue = new Queue<InteractivePlaybackEntry>();
        private InteractivePlaybackEntry interactiveCurrent;
        private InteractivePlaybackEntry interactivePending;
        private string interactivePendingSource = string.Empty;
        private bool interactiveActive;
        private Task interactiveNextQueueTask;
        private readonly Queue<InteractiveBackendEvent> interactiveBackendEvents = new Queue<InteractiveBackendEvent>();
        private Task interactiveBackendEventTask;
        private long interactiveLastEndedFrameId;
        private long interactiveObservedBackendFrameId;
        private InteractiveProtectedHandoff interactiveProtectedHandoff;
        private int interactiveRuntimeVersion;
        private int interactiveTriggerCancellationVersion;
        private int interactiveStatusRevision;
        private int observedBackendConnectionLoss;
        private bool interactiveNeedsStatusCheck;
        private InteractivePlaybackEntry interactiveStartingEntry;
        private InteractivePlaybackEntry interactiveSendingNext;
        private bool processingInteractiveTriggers;
        private InteractiveTriggerRequest interactiveProcessingTrigger;
        private readonly Queue<InteractiveTriggerRequest> interactiveTriggerRequests = new Queue<InteractiveTriggerRequest>();
        private InteractivePlaybackForm interactiveForm;
        private bool interactiveWindowOpen;
        private bool interactiveHotkeysDisabled;
        private int interactiveWindowVersion;
        private Task interactiveCleanupTask;

        private void BuildInteractiveWindow()
        {
            interactiveForm = new InteractivePlaybackForm(this);
            interactiveForm.FormClosing += (_, e) =>
            {
                if (!closingMainForm)
                {
                    e.Cancel = true;
                    interactiveForm.Hide();
                }
            };
        }

        private void OpenInteractiveWindow()
        {
            if (interactiveForm == null || interactiveForm.IsDisposed) BuildInteractiveWindow();
            interactiveForm.ReloadAll();
            interactiveForm.Show();
            interactiveForm.BringToFront();
        }

        private void SetInteractiveWindowOpen(bool open)
        {
            if (interactiveWindowOpen == open) return;
            interactiveWindowOpen = open;
            interactiveWindowVersion++;
            if (open)
            {
                QueueInteractiveObsGroupScenes(string.Empty, CurrentInteractiveProjectGroup.ObsEnterScene);
                RequestInteractiveGroupDefaultIfIdle();
                return;
            }

            bool hasPlayback = interactiveActive || interactiveCurrent != null || interactivePending != null ||
                interactiveQueue.Count > 0 || interactiveTriggerRequests.Count > 0 ||
                (interactiveNextQueueTask != null && !interactiveNextQueueTask.IsCompleted);
            if (hasPlayback && !closingMainForm) _ = StopInteractiveAsync();
            else ClearInteractiveRuntime();
        }

        private void ClearInteractiveRuntime(bool clearTriggerRequests = true)
        {
            ResetInteractiveDefaultAutoSwitch();
            SetInteractiveObsPlayback(null);
            interactiveActive = false;
            ResetInteractivePlaybackWatchdog();
            interactiveStatusRevision++;
            interactiveNeedsStatusCheck = false;
            interactiveStartingEntry = null;
            interactiveSendingNext = null;
            CancelInteractiveProtectedHandoff();
            interactiveRuntimeVersion++;
            interactiveBackendEvents.Clear();
            interactiveLastEndedFrameId = 0;
            interactiveObservedBackendFrameId = 0;
            interactiveCurrent = null;
            interactivePending = null;
            interactivePendingSource = string.Empty;
            interactiveQueue.Clear();
            interactiveNextQueueTask = null;
            if (clearTriggerRequests)
            {
                interactiveTriggerCancellationVersion++;
                interactiveProcessingTrigger = null;
                interactiveTriggerRequests.Clear();
            }
            interactiveForm?.RefreshRuntime();
        }

        private sealed class InteractiveBackendEvent
        {
            public BackendInputEvent InputEvent;
            public int RuntimeVersion;
        }

        private bool HandleInteractiveBackendEvent(string eventLine)
        {
            if (!interactiveActive) return false;
            if (interactiveNeedsStatusCheck && !eventLine.StartsWith("INPUT_STARTED", StringComparison.OrdinalIgnoreCase)) return true;
            BackendInputEventType type;
            if (eventLine.StartsWith("INPUT_STARTED", StringComparison.OrdinalIgnoreCase)) type = BackendInputEventType.Started;
            else if (eventLine.StartsWith("INPUT_EOF", StringComparison.OrdinalIgnoreCase)) type = BackendInputEventType.Eof;
            else if (eventLine.StartsWith("INPUT_ENDED", StringComparison.OrdinalIgnoreCase)) type = BackendInputEventType.Ended;
            else return false;
            interactiveBackendEvents.Enqueue(new InteractiveBackendEvent
            {
                InputEvent = new BackendInputEvent(type, ParseStatusLong(eventLine, "frame_id="), ParseStatusString(eventLine, "input=")),
                RuntimeVersion = interactiveRuntimeVersion
            });
            if (interactiveBackendEventTask == null || interactiveBackendEventTask.IsCompleted)
                interactiveBackendEventTask = ProcessInteractiveBackendEventsAsync();
            return true;
        }

        private async Task ProcessInteractiveBackendEventsAsync()
        {
            while (interactiveBackendEvents.Count > 0)
            {
                var queued = interactiveBackendEvents.Dequeue();
                await WaitForInteractiveNextQueueAsync();
                if (!interactiveActive || queued.RuntimeVersion != interactiveRuntimeVersion) continue;
                var inputEvent = queued.InputEvent;
                if (inputEvent.Type == BackendInputEventType.Started)
                {
                    if (ApplyInteractiveOutputStarted(inputEvent)) await EnsureInteractiveNextQueuedAsync();
                    continue;
                }
                if (interactiveNeedsStatusCheck) continue;
                if (IsObsoleteInteractiveTransitionFrame(inputEvent.FrameId)) continue;
                if (inputEvent.FrameId > 0 && inputEvent.FrameId <= interactiveLastEndedFrameId) continue;
                if (inputEvent.Type == BackendInputEventType.Eof)
                {
                    // Decoding the prefetched input may finish before the current input leaves the output queue.
                    if (interactiveCurrent != null && InputSummariesEqual(inputEvent.Input, interactiveCurrent.InputSummary) &&
                        (interactiveCurrent.BackendFrameId == 0 || inputEvent.FrameId == 0 || interactiveCurrent.BackendFrameId == inputEvent.FrameId))
                    {
                        interactiveCurrent.BackendFrameId = inputEvent.FrameId;
                        await HandleInteractiveEofAsync(inputEvent.Input);
                    }
                    else if (interactivePending != null && InputSummariesEqual(inputEvent.Input, interactivePending.InputSummary) &&
                        interactivePending.BackendFrameId == 0)
                    {
                        interactivePending.BackendFrameId = inputEvent.FrameId;
                    }
                    continue;
                }
                string source = interactivePending != null ? interactivePendingSource : interactiveCurrent?.InputSummary;
                if (!string.IsNullOrEmpty(source) && !InputSummariesEqual(inputEvent.Input, source)) continue;
                if (interactiveCurrent != null && interactiveCurrent.BackendFrameId > 0 && inputEvent.FrameId > 0 &&
                    interactiveCurrent.BackendFrameId != inputEvent.FrameId) continue;
                // The summary identifies a file; frame_id identifies this particular invocation of that file.
                if (inputEvent.FrameId > 0) interactiveLastEndedFrameId = inputEvent.FrameId;
                await HandleInteractiveEndedAsync(inputEvent.Input);
            }
        }


        private async Task HandleInteractiveEofAsync(string inputSummary)
        {
            if (!interactiveActive) return;
            if (interactiveCurrent != null && !InputSummariesEqual(inputSummary, interactiveCurrent.InputSummary)) return;

            if (interactivePending == null)
                await EnsureInteractiveNextQueuedAsync();
        }

        private async Task HandleInteractiveEndedAsync(string inputSummary)
        {
            int version = interactiveRuntimeVersion;
            await WaitForInteractiveNextQueueAsync();
            if (!interactiveActive || version != interactiveRuntimeVersion) return;
            if (interactivePending != null &&
                (string.IsNullOrEmpty(interactivePendingSource) || InputSummariesEqual(inputSummary, interactivePendingSource)))
            {
                if (interactivePending.RequiresOutputConfirmation)
                {
                    // Retain the old lifecycle until the queued destination actually wins the output race.
                    if (interactivePending.ObsPlayback != interactiveCurrent?.ObsPlayback &&
                        !CanReplaceInteractiveObsEnd(interactiveObsCurrent, interactivePending.ObsPlayback)) SetInteractiveObsPlayback(null);
                    // Ending the source cannot identify which pending batch actually won the race.
                    interactiveStatusRevision++;
                    interactiveAwaitingPendingConfirmation = true;
                    MarkInteractivePlaybackMismatch();
                    return;
                }
                interactiveStatusRevision++;
                ResetInteractivePlaybackWatchdog();
                CountCompletedInteractiveMiddle(interactivePending);
                interactiveCurrent = interactivePending;
                ObserveInteractiveDefaultOutput(interactiveCurrent, System.Diagnostics.Stopwatch.GetTimestamp());
                SetInteractiveObsPlayback(interactiveCurrent);
                currentStatusInput = interactiveCurrent.InputSummary;
                currentActualStatusInput = interactiveCurrent.InputSummary;
                interactivePending = null;
                interactivePendingSource = string.Empty;
                CompleteInteractiveProtectedHandoff();
                AppendInteractiveLog(T("InteractiveNowPlaying") + interactiveCurrent.Title);
                interactiveForm?.RefreshRuntime();
                await EnsureInteractiveNextQueuedAsync();
                return;
            }

            if (interactiveCurrent != null && InputSummariesEqual(inputSummary, interactiveCurrent.InputSummary) &&
                interactivePending == null && interactiveQueue.Count == 0 && !interactiveCurrent.Loop &&
                (interactiveCurrent.GroupLoop == null || interactiveCurrent.GroupLoop.Cancelled))
            {
                SetInteractiveObsPlayback(null);
                AppendInteractiveLog(T("InteractiveSequenceCompleted"));
                interactiveActive = false;
                interactiveCurrent = null;
                interactiveForm?.RefreshRuntime();
            }
        }

        private static bool IsInteractiveProjectEntry(InteractivePlaybackEntry entry, Guid routeId)
        {
            return entry != null && entry.RouteId == routeId && !entry.IsGroupDefault && !entry.IsGroupTransition;
        }

        private bool IsInteractiveProjectRequest(InteractiveTriggerRequest request, Guid routeId)
        {
            return request != null && !request.IsGroupTransition && !IsStaleInteractiveGroupRequest(request) &&
                request.Entries.Any(entry => IsInteractiveProjectEntry(entry, routeId));
        }

        private bool HasInteractiveProjectInvocation(InteractiveRouteItem route)
        {
            Guid id = route.RuntimeId;
            return interactiveActive && IsInteractiveProjectEntry(interactiveCurrent, id) ||
                IsInteractiveProjectEntry(interactivePending, id) ||
                IsInteractiveProjectEntry(interactiveStartingEntry, id) ||
                IsInteractiveProjectEntry(interactiveSendingNext, id) ||
                interactiveQueue.Any(entry => IsInteractiveProjectEntry(entry, id)) ||
                interactiveTriggerRequests.Any(request => IsInteractiveProjectRequest(request, id)) ||
                IsInteractiveProjectRequest(interactiveProcessingTrigger, id) ||
                IsInteractiveProjectRequest(interactiveProtectedHandoff?.Request, id);
        }

        private void RequestInteractiveRoute(InteractiveRouteItem route, string source)
        {
            if (!interactiveWindowOpen || closingMainForm || route == null) return;
            ObserveBackendConnectionLoss();
            if (route.IgnoreRetriggerWhilePlaying && HasInteractiveProjectInvocation(route))
            {
                AppendInteractiveLog(source + " - " + route.Name + ": " + T("InteractiveRepeatedTriggerIgnored"));
                return;
            }
            var comboRequest = BuildInteractiveComboRequest(route);
            List<InteractivePlaybackEntry> entries = comboRequest?.Entries ?? BuildInteractiveEntries(route);
            if (entries.Count == 0)
            {
                AppendInteractiveLog(source + " - " + T(route.RandomSelectionUnavailable ? "InteractiveCombinationUnavailable" : "InteractiveNoPlayableMedia"));
                return;
            }

            // A waiting idle video yields to a real trigger, including a default waiting behind protected media.
            var waiting = interactiveTriggerRequests.Where(request => request.IsGroupTransition ||
                !request.Entries.All(entry => entry.IsGroupDefault)).ToList();
            interactiveTriggerRequests.Clear();
            foreach (var request in waiting) interactiveTriggerRequests.Enqueue(request);
            if (!interactiveActive && interactiveTriggerRequests.Count == 0) BeginInteractiveMode();

            var triggerRequest = comboRequest ?? new InteractiveTriggerRequest { Entries = entries };
            triggerRequest.RouteName = route.Name;
            triggerRequest.ProjectGroupVersion = interactiveProjectGroupVersion;
            triggerRequest.ProjectGroupId = activeInteractiveProjectGroupId;
            triggerRequest.Immediate = route.Immediate;
            triggerRequest.Source = source;
            interactiveTriggerRequests.Enqueue(triggerRequest);
            if (interactiveProtectedHandoff != null && !interactiveProtectedHandoff.Request.IsGroupTransition &&
                interactiveProtectedHandoff.Request.Entries.All(entry => entry.IsGroupDefault))
                CancelInteractiveProtectedHandoff();
            AppendInteractiveLog(source + " - " + route.Name + " [" +
                (comboRequest != null ? T("InteractiveCombo") : route.Immediate ? "PLAY" : "PLAY_NEXT") + "] " +
                string.Join(" → ", entries.Select(FormatInteractiveEntry)));
            interactiveForm?.RefreshRuntime();
            if (!processingInteractiveTriggers) _ = ProcessInteractiveTriggerRequestsAsync();
        }

        private void BeginInteractiveMode()
        {
            ClearQueuedPlaybackState(true);
            playoutRunning = false;
            waitingForScheduledStart = false;
            playingScheduled = false;
            currentScheduledIndex = -1;
            currentScheduledItem = null;
            SetPlayoutTimerRunning(false);
            UpdatePlayoutStatusLabel();
            interactiveActive = true;
        }

        private async Task ProcessInteractiveTriggerRequestsAsync()
        {
            if (processingInteractiveTriggers || !interactiveWindowOpen || closingMainForm) return;
            int windowVersion = interactiveWindowVersion;
            int cancellationVersion = interactiveTriggerCancellationVersion;
            processingInteractiveTriggers = true;
            try
            {
                if (interactiveCleanupTask != null) await interactiveCleanupTask;
                while (interactiveWindowOpen && !closingMainForm && windowVersion == interactiveWindowVersion &&
                    cancellationVersion == interactiveTriggerCancellationVersion && interactiveTriggerRequests.Count > 0)
                {
                    InteractiveTriggerRequest request = interactiveTriggerRequests.Dequeue();
                    interactiveProcessingTrigger = request;
                    try
                    {
                        if (IsStaleInteractiveGroupRequest(request)) continue;
                        bool verified = await VerifyInteractivePlaybackAsync();
                        if (!interactiveWindowOpen || closingMainForm || windowVersion != interactiveWindowVersion ||
                            cancellationVersion != interactiveTriggerCancellationVersion) return;
                        if (IsStaleInteractiveGroupRequest(request)) continue;
                        if (!verified)
                        {
                            RetainInteractiveTriggerAfterRuntimeChange(request, cancellationVersion);
                            break;
                        }
                        if (request.Immediate)
                            await StartInteractiveImmediatelyAsync(request);
                        else
                            await AppendInteractiveRouteAsync(request);
                        interactiveForm?.RefreshRuntime();
                    }
                    finally
                    {
                        if (interactiveProcessingTrigger == request) interactiveProcessingTrigger = null;
                    }
                }
            }
            finally
            {
                processingInteractiveTriggers = false;
                // A reopened window or a new trigger after an explicit stop may
                // be waiting for this older dispatch to release the processor.
                if (interactiveWindowOpen && !closingMainForm &&
                    (windowVersion != interactiveWindowVersion || cancellationVersion != interactiveTriggerCancellationVersion) &&
                    interactiveTriggerRequests.Count > 0) _ = ProcessInteractiveTriggerRequestsAsync();
            }
        }

        private async Task StartInteractiveImmediatelyAsync(InteractiveTriggerRequest request)
        {
            int version = interactiveRuntimeVersion;
            int cancellationVersion = interactiveTriggerCancellationVersion;
            await WaitForInteractiveNextQueueAsync();
            if (version != interactiveRuntimeVersion)
            {
                RetainInteractiveTriggerAfterRuntimeChange(request, cancellationVersion);
                return;
            }
            if (DeferInteractiveDefaultSwitch(request)) return;
            if (await TryPlayInteractiveComboAsync(request)) return;
            if (await TryWaitForInteractivePhasedHandoffAsync(request)) return;
            if (interactiveActive && interactiveCurrent != null && PreventsInteractiveInterruption(interactiveCurrent, request))
            {
                await WaitForProtectedInteractiveMediaAsync(request);
                return;
            }
            await PlayInteractiveEntriesImmediatelyAsync(request.Entries);
        }

        private async Task PlayInteractiveEntriesImmediatelyAsync(List<InteractivePlaybackEntry> entries)
        {
            entries[0].MinimumBackendFrameId = Math.Max(entries[0].MinimumBackendFrameId,
                Math.Max(interactiveObservedBackendFrameId,
                    Math.Max(Math.Max(interactiveCurrent?.BackendFrameId ?? 0, interactiveCurrent?.MinimumBackendFrameId ?? 0),
                        interactivePending?.BackendFrameId ?? 0)));
            entries[0].MinimumBackendFrameId = Math.Max(entries[0].MinimumBackendFrameId, entries[0].BackendFrameId);
            entries[0].BackendFrameId = 0;
            entries[0].ReplaceAfterFrameId = 0;
            entries[0].RequiresOutputConfirmation = false;
            ResetInteractivePlaybackWatchdog();
            int playVersion = ++interactiveRuntimeVersion;
            interactiveBackendEvents.Clear();
            ClearQueuedPlaybackState(true);
            interactiveQueue.Clear();
            interactivePending = null;
            interactivePendingSource = string.Empty;
            interactiveActive = true;
            interactiveStatusRevision++;
            interactiveCurrent = entries[0];
            interactiveStartingEntry = interactiveCurrent;
            foreach (InteractivePlaybackEntry entry in entries.Skip(1)) interactiveQueue.Enqueue(entry);

            string command = BuildInteractivePlayCommand("PLAY", interactiveCurrent);
            string reply = await SendRawCommandAsync(command);
            if (playVersion != interactiveRuntimeVersion || !interactiveActive) return;
            interactiveStartingEntry = null;
            AppendInteractiveLog("> " + command + Environment.NewLine + "< " + reply);
            if (reply == null || !reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                AppendInteractiveLog(T("InteractiveImmediateFailed"));
                interactiveRecoveryEntry = interactiveCurrent;
                interactiveRecoveryRetryAt = DateTime.UtcNow.Add(InteractivePlaybackRetryInterval);
                MarkInteractivePlaybackMismatch();
                return;
            }
            currentStatusInput = interactiveCurrent.InputSummary;
            currentActualStatusInput = interactiveCurrent.InputSummary;
            await EnsureInteractiveNextQueuedAsync();
        }

        private async Task AppendInteractiveRouteAsync(InteractiveTriggerRequest request)
        {
            int version = interactiveRuntimeVersion;
            int cancellationVersion = interactiveTriggerCancellationVersion;
            await WaitForInteractiveNextQueueAsync();
            if (version != interactiveRuntimeVersion)
            {
                RetainInteractiveTriggerAfterRuntimeChange(request, cancellationVersion);
                return;
            }
            if (DeferInteractiveDefaultSwitch(request)) return;
            if (await TryPlayInteractiveComboAsync(request)) return;
            if (await TryWaitForInteractivePhasedHandoffAsync(request)) return;
            if (interactiveActive && interactiveCurrent != null && PreventsInteractiveInterruption(interactiveCurrent, request))
            {
                await WaitForProtectedInteractiveMediaAsync(request);
                return;
            }
            interactiveActive = true;

            interactiveStatusRevision++;
            var anyRun = interactiveCurrent?.PhaseRun;
            bool arbitraryGroup = anyRun != null && InteractiveInterruptionMode(interactiveCurrent, request) == InteractiveGroupInterruptionMode.Any;
            InteractivePlaybackEntry replaced = PrepareInteractiveRouteAppend();
            if (arbitraryGroup)
            {
                anyRun.Ending = true;
                var retained = interactiveQueue.Where(entry => entry.PhaseRun != anyRun).ToList();
                interactiveQueue.Clear();
                foreach (var entry in retained) interactiveQueue.Enqueue(entry);
                if (interactivePending?.PhaseRun == anyRun)
                {
                    replaced = interactivePending;
                    interactivePending = null;
                    interactivePendingSource = string.Empty;
                }
            }
            if (replaced != null && !arbitraryGroup)
            {
                request.Entries[0].RequiresOutputConfirmation = true;
                request.Entries[0].MinimumBackendFrameId = Math.Max(interactiveCurrent?.BackendFrameId ?? 0, replaced.BackendFrameId);
                AppendInteractiveLog(T("InteractiveReplacedLoop") + replaced.Title);
            }

            foreach (InteractivePlaybackEntry entry in request.Entries) interactiveQueue.Enqueue(entry);
            if (arbitraryGroup && replaced != null) MarkInteractivePhasedReplacement(interactiveQueue.Peek(), replaced);
            await EnsureInteractiveNextQueuedAsync();
        }

        private async Task WaitForInteractiveNextQueueAsync()
        {
            while (interactiveNextQueueTask != null && !interactiveNextQueueTask.IsCompleted) await interactiveNextQueueTask;
        }

        private Task EnsureInteractiveNextQueuedAsync()
        {
            if (interactiveNextQueueTask != null && !interactiveNextQueueTask.IsCompleted) return interactiveNextQueueTask;
            interactiveNextQueueTask = QueueInteractiveNextCoreAsync(SendRawCommandAsync);
            return interactiveNextQueueTask;
        }

        private async Task QueueInteractiveNextCoreAsync(Func<string, Task<string>> send)
        {
            int version = interactiveRuntimeVersion;
            if (!interactiveActive || interactiveNeedsStatusCheck || interactivePending != null) return;
            QueueInteractivePhasedContinuation();
            QueueInteractiveGroupContinuation();
            QueueInteractiveGroupDefaultIfNeeded();
            if (interactiveQueue.Count == 0) return;
            InteractivePlaybackEntry next = interactiveQueue.Dequeue();
            if (next.PhaseRun != null && next.Phase == InteractiveGroupPhase.End)
            {
                next.PhaseRun.Ending = true;
                if (next.GroupLoop != null) next.GroupLoop.Cancelled = true;
            }
            interactiveSendingNext = next;
            string sourceSummary = interactiveCurrent != null
                ? interactiveCurrent.InputSummary
                : (!string.IsNullOrEmpty(currentActualStatusInput) ? currentActualStatusInput : currentStatusInput);
            next.MinimumBackendFrameId = Math.Max(next.MinimumBackendFrameId,
                Math.Max(interactiveCurrent?.BackendFrameId ?? 0, interactiveCurrent?.MinimumBackendFrameId ?? 0));
            if (next.RequiresOutputConfirmation)
            {
                next.ReplaceAfterFrameId = interactiveCurrent?.BackendFrameId ?? 0;
                next.MinimumBackendFrameId = Math.Max(next.MinimumBackendFrameId, next.ReplaceAfterFrameId);
            }
            string command = BuildInteractivePlayCommand("PLAY_NEXT", next);
            string reply = await send(command);
            if (version != interactiveRuntimeVersion || !interactiveActive) return;
            interactiveSendingNext = null;
            AppendInteractiveLog("> " + command + Environment.NewLine + "< " + reply);
            if (reply != null && reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                interactiveStatusRevision++;
                interactivePending = next;
                interactivePendingSource = sourceSummary;
            }
            else
            {
                var retained = interactiveQueue.ToList();
                interactiveQueue.Clear();
                interactiveQueue.Enqueue(next);
                foreach (InteractivePlaybackEntry item in retained) interactiveQueue.Enqueue(item);
                AppendInteractiveLog(T("InteractivePlayNextFailed"));
            }
            interactiveForm?.RefreshRuntime();
        }

        private Task StopInteractiveAsync()
        {
            Task sendingNext = interactiveNextQueueTask;
            Task previousCleanup = interactiveCleanupTask;
            ClearInteractiveRuntime();
            interactiveCleanupTask = ClearInteractiveBackendNextAsync(previousCleanup, sendingNext, interactiveRuntimeVersion);
            return interactiveCleanupTask;
        }

        private async Task ClearInteractiveBackendNextAsync(Task previousCleanup, Task sendingNext, int version)
        {
            if (previousCleanup != null) await previousCleanup;
            if (sendingNext != null) await sendingNext;
            // Manual playback or a connection-tab change supersedes this cleanup.
            if (closingMainForm || version != interactiveRuntimeVersion) return;
            string reply = await SendRawCommandAsync("PLAY_NEXT");
            AppendInteractiveLog("> PLAY_NEXT" + Environment.NewLine + "< " + reply);
            AppendInteractiveLog(T("InteractiveQueueCleared"));
        }

        private List<InteractivePlaybackEntry> BuildInteractiveEntries(InteractiveRouteItem route)
        {
            return BuildInteractiveEntriesAt(route, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private List<InteractivePlaybackEntry> BuildInteractiveCombinationEntries(InteractiveMediaCombination combination)
        {
            var result = new List<InteractivePlaybackEntry>();
            foreach (Guid mediaId in combination.MediaIds)
            {
                InteractiveMediaItem media = interactiveMedia.FirstOrDefault(item => item.RuntimeId == mediaId);
                if (!CanPlayInteractiveMedia(media)) return new List<InteractivePlaybackEntry>();
                result.AddRange(BuildInteractiveMediaEntries(media, null));
            }
            return result;
        }

        private List<InteractivePlaybackEntry> BuildInteractiveMediaEntries(InteractiveMediaItem media, InteractiveFileGroupLoop groupLoop)
        {
            if (HasInteractiveGroupBoundaries(media)) return BuildInteractivePhasedGroupEntries(media, groupLoop);
            var paths = SelectInteractiveMediaPaths(media, random);
            if (media.IsFileGroup && media.Loop && groupLoop == null)
                groupLoop = new InteractiveFileGroupLoop { Media = SnapshotInteractiveFileGroup(media) };
            bool loop = media.Loop && !media.IsFileGroup;
            Guid playbackUnitId = Guid.NewGuid();
            return paths.Select((path, index) =>
            {
                string options = NormalizeInteractiveLoopOption(NormalizeQueueOptions(path, media.Options, false), loop);
                return new InteractivePlaybackEntry
                {
                    IsGroupDefault = groupLoop?.IsGroupDefault == true,
                    DefaultPlaybackId = groupLoop?.DefaultPlaybackId ?? Guid.Empty,
                    ObsPlayback = groupLoop?.ObsPlayback,
                    MediaId = media.RuntimeId,
                    PlaybackUnitId = playbackUnitId,
                    PreventInterrupt = media.PreventInterrupt,
                    DefaultSwitchInterruptionMode = media.DefaultSwitchInterruptionMode,
                    Title = media.IsFileGroup ? media.Title + " / " + Path.GetFileName(path) :
                        (string.IsNullOrWhiteSpace(media.Title) ? GetInputTitle(path) : media.Title),
                    Path = path,
                    Options = options,
                    Loop = loop,
                    InputSummary = BuildInputSummary(path, options),
                    GroupLoop = groupLoop,
                    GroupIteration = groupLoop == null ? 0 : groupLoop.Iteration,
                    GroupEnd = groupLoop != null && index == paths.Count - 1
                };
            }).ToList();
        }

        private static string NormalizeInteractiveLoopOption(string options, bool loop)
        {
            var entries = (options ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            entries.RemoveAll(value => value.Equals("loop", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("loop=", StringComparison.OrdinalIgnoreCase));
            if (loop) entries.Add("loop=1");
            return string.Join(",", entries);
        }

        private static string BuildInteractivePlayCommand(string verb, InteractivePlaybackEntry entry)
        {
            string options = entry.Options;
            if (verb == "PLAY_NEXT" && entry.RequiresOutputConfirmation && entry.ReplaceAfterFrameId > 0)
                options = "after_frame_id=" + entry.ReplaceAfterFrameId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    (string.IsNullOrWhiteSpace(options) ? string.Empty : "," + options);
            return string.IsNullOrWhiteSpace(options) ? verb + " " + entry.Path : verb + " " + entry.Path + "\t" + options;
        }

        private static string FormatInteractiveEntry(InteractivePlaybackEntry entry)
        {
            return entry.Title + (entry.Loop || (entry.GroupLoop != null && !entry.GroupLoop.Cancelled) ? " ↻" : string.Empty);
        }

        private void AppendInteractiveLog(string text)
        {
            interactiveForm?.AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            AppendLog("[interactive] " + text);
        }

        private string DescribeInteractiveQueue()
        {
            var parts = new List<string>();
            if (interactiveCurrent != null) parts.Add(T(interactiveNeedsStatusCheck ? "InteractiveVerifyingPlayback" : "InteractivePlaying") + FormatInteractiveEntry(interactiveCurrent));
            if (interactivePending != null) parts.Add(T("InteractiveNext") + FormatInteractiveEntry(interactivePending));
            parts.AddRange(interactiveQueue.Select((entry, index) => (index + 1) + ". " + FormatInteractiveEntry(entry)));
            parts.AddRange(interactiveTriggerRequests.Select(request => T("InteractivePendingAction") + request.RouteName));
            return parts.Count == 0 ? T("InteractiveQueueEmpty") : string.Join(Environment.NewLine, parts);
        }

        private sealed partial class InteractivePlaybackForm : Form
        {
            private const int HotkeyBaseId = 0x5100;
            private readonly MainForm owner;
            private readonly ListView mediaView = new ListView();
            private readonly ListView routeView = new ListView();
            private readonly FlowLayoutPanel routeButtons = new FlowLayoutPanel();
            private TableLayoutPanel rootPanel;
            private readonly Font activeGroupFont;
            private readonly TextBox queueBox = new TextBox();
            private readonly TextBox operationBox = new TextBox();
            private readonly Label connectionLabel = new Label();
            private readonly CheckBox hotkeysEnabledBox = new CheckBox { AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
            private readonly Dictionary<int, InteractiveRouteItem> registeredRoutes = new Dictionary<int, InteractiveRouteItem>();

            public InteractivePlaybackForm(MainForm owner)
            {
                this.owner = owner;
                activeGroupFont = new Font(Font, FontStyle.Bold);
                Disposed += (_, __) => activeGroupFont.Dispose();
                EnableDoubleBuffering(this);
                Text = T("InteractivePlayback");
                Size = new Size(1180, 780);
                MinimumSize = new Size(900, 620);
                StartPosition = FormStartPosition.CenterParent;
                ShowIcon = false;
                BuildUi();
            }

            private void BuildUi()
            {
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
                rootPanel = root;
                EnableDoubleBuffering(root);
                EnableDoubleBuffering(routeButtons);
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
                Controls.Add(root);

                var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
                connectionLabel.AutoSize = true;
                connectionLabel.Font = new Font(Font, FontStyle.Bold);
                connectionLabel.Padding = new Padding(0, 6, 12, 0);
                header.Controls.Add(connectionLabel);
                header.Controls.Add(MakeButton(T("SaveXml"), (_, __) => owner.SaveAutoConfig()));
                header.Controls.Add(MakeButton(T("InteractiveClearQueue"), async (_, __) => await owner.StopInteractiveAsync()));
                hotkeysEnabledBox.Text = T("InteractiveHotkeysEnabled");
                hotkeysEnabledBox.Checked = !owner.interactiveHotkeysDisabled;
                hotkeysEnabledBox.CheckedChanged += (_, __) =>
                {
                    owner.interactiveHotkeysDisabled = !hotkeysEnabledBox.Checked;
                    RegisterRouteHotkeys();
                };
                header.Controls.Add(hotkeysEnabledBox);
                root.Controls.Add(header, 0, 0);

                routeButtons.Dock = DockStyle.Fill;
                routeButtons.AutoSize = true;
                routeButtons.WrapContents = true;
                routeButtons.Padding = new Padding(0, 6, 0, 6);
                root.Controls.Add(BuildProjectGroupPanel(), 0, 1);
                root.Controls.Add(BuildDefaultPlaybackPanel(), 0, 2);
                root.Controls.Add(routeButtons, 0, 3);

                var lists = new SplitContainer { Size = new Size(1100, 400), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 550 };
                lists.Panel1.Controls.Add(BuildMediaPanel());
                lists.Panel2.Controls.Add(BuildRoutePanel());
                root.Controls.Add(lists, 0, 4);

                var bottom = new SplitContainer { Size = new Size(1100, 400), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 550 };
                bottom.Panel1.Controls.Add(BuildTextPanel(T("InteractiveQueuePanel"), queueBox, true));
                bottom.Panel2.Controls.Add(BuildTextPanel(T("InteractiveOperations"), operationBox, true));
                root.Controls.Add(bottom, 0, 5);

                mediaView.Columns.Add(T("ColumnTitle"), 150);
                mediaView.Columns.Add(T("InteractiveLoop"), 60);
                mediaView.Columns.Add(T("InteractivePreventInterrupt"), 130);
                mediaView.Columns.Add(T("ColumnOptions"), 150);
                mediaView.Columns.Add(T("ColumnPath"), 420);
                routeView.Columns.Add(T("InteractiveProject"), 160);
                routeView.Columns.Add(T("InteractiveHotkey"), 120);
                routeView.Columns.Add(T("InteractiveCommand"), 90);
                routeView.Columns.Add(T("InteractiveCombinationSelection"), 100);
                routeView.Columns.Add(T("InteractiveCombinations"), 430);
                routeView.Columns.Add(T("InteractiveObsStartSceneColumn"), 180);
                routeView.Columns.Add(T("InteractiveObsEndSceneColumn"), 180);
                routeView.DoubleClick += (_, __) => TriggerSelected(T("InteractiveDoubleClickedProject"));
            }

            private Control BuildMediaPanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveMedia"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("AddFiles"), (_, __) => AddMediaFiles()));
                bar.Controls.Add(MakeButton(T("InteractiveAddFileGroup"), (_, __) => AddFileGroup()));
                bar.Controls.Add(MakeButton(T("InteractiveEdit"), (_, __) => EditSelectedMedia()));
                bar.Controls.Add(MakeButton(T("InteractiveToggleLoop"), (_, __) => ToggleSelectedMediaLoop()));
                bar.Controls.Add(MakeButton(T("InteractiveToggleProtection"), (_, __) => ToggleSelectedMediaProtection()));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedMedia()));
                bar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSelectedMedia(-1)));
                bar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSelectedMedia(1)));
                panel.Controls.Add(bar, 0, 0);
                ConfigureList(mediaView);
                panel.Controls.Add(mediaView, 0, 1);
                return panel;
            }

            private Control BuildRoutePanel()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                bar.Controls.Add(new Label { Text = T("InteractiveProjects"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
                bar.Controls.Add(MakeButton(T("InteractiveAdd"), (_, __) => EditRoute(null)));
                bar.Controls.Add(MakeButton(T("InteractiveEdit"), (_, __) => EditSelectedRoute()));
                bar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSelectedRoute()));
                bar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSelectedRoute(-1)));
                bar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSelectedRoute(1)));
                bar.Controls.Add(MakeButton(T("InteractiveCopyProject"), (_, __) => CopySelectedProject()));
                bar.Controls.Add(MakeButton(T("InteractiveTrigger"), (_, __) => TriggerSelected(T("InteractiveClickedProject"))));
                panel.Controls.Add(bar, 0, 0);
                ConfigureList(routeView);
                panel.Controls.Add(routeView, 0, 1);
                return panel;
            }

            private static Control BuildTextPanel(string title, TextBox box, bool readOnly)
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(0, 5, 0, 4) }, 0, 0);
                box.Dock = DockStyle.Fill;
                box.Multiline = true;
                box.ReadOnly = readOnly;
                box.ScrollBars = ScrollBars.Vertical;
                panel.Controls.Add(box, 0, 1);
                return panel;
            }

            private static void ConfigureList(ListView view)
            {
                view.Dock = DockStyle.Fill;
                view.View = View.Details;
                view.FullRowSelect = true;
                view.GridLines = true;
                view.HideSelection = false;
                EnableDoubleBuffering(view);
            }

            public void ReloadAll()
            {
                connectionLabel.Text = T("InteractiveConnection") +
                    (owner.activeState == null ? "-" : owner.GetConnectionTabTitle(owner.activeState));
                RefreshMedia();
                ReloadProjectGroup();
            }

            public void ReloadProjectGroup()
            {
                rootPanel.SuspendLayout();
                try
                {
                    RefreshProjectGroups();
                    RefreshDefaultPlaybackButtons();
                    RefreshRoutes();
                    RefreshRuntime();
                    RegisterRouteHotkeys();
                }
                finally { rootPanel.ResumeLayout(true); }
            }

            private void RefreshMedia()
            {
                mediaView.BeginUpdate();
                mediaView.Items.Clear();
                foreach (InteractiveMediaItem media in owner.interactiveMedia)
                {
                    var row = new ListViewItem(FormatInteractiveMediaTitle(media));
                    row.SubItems.Add(media.Loop ? T("InteractiveYes") : string.Empty);
                    row.SubItems.Add(HasInteractiveGroupBoundaries(media) ? T(InteractiveGroupInterruptionKey(media.FileGroupInterruptionMode)) :
                        media.PreventInterrupt ? T("InteractiveYes") : string.Empty);
                    row.SubItems.Add(media.Options);
                    row.SubItems.Add(media.IsFileGroup ? DescribeInteractiveFileGroup(media) : media.Path);
                    row.Tag = media;
                    if (media.Loop) row.BackColor = Color.FromArgb(225, 245, 225);
                    mediaView.Items.Add(row);
                }
                mediaView.EndUpdate();
            }

            private void RefreshRoutes()
            {
                routeView.BeginUpdate();
                routeButtons.SuspendLayout();
                try
                {
                    routeView.Items.Clear();
                    // Keep native button handles alive across group switches. Clicks use the
                    // current Tag so a reused button never triggers the previous group's route.
                    while (routeButtons.Controls.Count > owner.interactiveRoutes.Count)
                        routeButtons.Controls[routeButtons.Controls.Count - 1].Dispose();
                    for (int index = 0; index < owner.interactiveRoutes.Count; index++)
                    {
                        InteractiveRouteItem route = owner.interactiveRoutes[index];
                        var row = new ListViewItem(route.Name);
                        row.SubItems.Add(FormatHotkey(route.Hotkey));
                        row.SubItems.Add(route.Immediate ? "PLAY" : "PLAY_NEXT");
                        row.SubItems.Add(T(route.RandomOrder ? "InteractiveRandom" : "InteractiveSequential"));
                        row.SubItems.Add(owner.DescribeRoute(route));
                        row.SubItems.Add(owner.DescribeInteractiveObsScene(route, true));
                        row.SubItems.Add(owner.DescribeInteractiveObsScene(route, false));
                        row.Tag = route;
                        routeView.Items.Add(row);

                        Button button;
                        if (index < routeButtons.Controls.Count) button = (Button)routeButtons.Controls[index];
                        else
                        {
                            button = MakeButton(string.Empty, (sender, _) =>
                                owner.RequestInteractiveRoute((InteractiveRouteItem)((Button)sender).Tag, T("InteractiveClickedProject")));
                            button.Padding = new Padding(10, 5, 10, 5);
                            routeButtons.Controls.Add(button);
                        }
                        button.Tag = route;
                        string hotkey = FormatHotkey(route.Hotkey);
                        button.Text = route.Name + (hotkey.Length == 0 ? string.Empty : "  [" + hotkey + "]");
                    }
                }
                finally
                {
                    routeButtons.ResumeLayout(true);
                    routeView.EndUpdate();
                }
            }

            public void RefreshRuntime()
            {
                if (IsDisposed) return;
                queueBox.Text = owner.DescribeInteractiveQueue();
            }

            public void AppendOperation(string text)
            {
                if (IsDisposed) return;
                bool followTail = operationBox.SelectionStart + operationBox.SelectionLength >= operationBox.TextLength;
                int selectionStart = operationBox.SelectionStart;
                int selectionLength = operationBox.SelectionLength;
                operationBox.AppendText(text + Environment.NewLine);
                if (operationBox.Lines.Length > 1000)
                    operationBox.Lines = operationBox.Lines.Skip(operationBox.Lines.Length - 1000).ToArray();
                if (followTail)
                {
                    operationBox.SelectionStart = operationBox.TextLength;
                    operationBox.SelectionLength = 0;
                    operationBox.ScrollToCaret();
                }
                else
                {
                    operationBox.SelectionStart = Math.Min(selectionStart, operationBox.TextLength);
                    operationBox.SelectionLength = Math.Min(selectionLength,
                        operationBox.TextLength - operationBox.SelectionStart);
                }
            }

            private void AddMediaFiles()
            {
                using (var dialog = new OpenFileDialog { Multiselect = true, Filter = T("InteractiveMediaFilter") })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    foreach (string path in dialog.FileNames)
                    {
                        owner.interactiveMedia.Add(new InteractiveMediaItem
                        {
                            Path = path,
                            Title = Path.GetFileNameWithoutExtension(path)
                        });
                    }
                }
                RefreshMedia();
                owner.SaveActiveConnectionState();
            }

            private void AddFileGroup()
            {
                var media = new InteractiveMediaItem { IsFileGroup = true };
                using (var dialog = new FileGroupEditDialog(media))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    dialog.Apply(media);
                }
                owner.interactiveMedia.Add(media);
                RefreshMedia();
                owner.SaveActiveConnectionState();
            }

            private InteractiveMediaItem SelectedMedia()
            {
                return mediaView.SelectedItems.Count == 1 ? mediaView.SelectedItems[0].Tag as InteractiveMediaItem : null;
            }

            private void MoveSelectedMedia(int direction)
            {
                MoveSelectedDisplayItem(mediaView, owner.interactiveMedia, direction, RefreshMedia);
            }

            private void MoveSelectedRoute(int direction)
            {
                // Existing hotkey registrations reference these same route objects.
                MoveSelectedDisplayItem(routeView, owner.interactiveRoutes, direction, RefreshRoutes);
            }

            private void MoveSelectedDisplayItem<T>(ListView view, List<T> items, int direction, Action refresh) where T : class
            {
                if (view.SelectedItems.Count != 1) return;
                T item = view.SelectedItems[0].Tag as T;
                int index = items.IndexOf(item), target = index + direction;
                if (index < 0 || target < 0 || target >= items.Count) return;
                items.RemoveAt(index);
                items.Insert(target, item);
                refresh();
                ListViewItem row = view.Items.Cast<ListViewItem>().First(value => ReferenceEquals(value.Tag, item));
                row.Selected = true;
                row.Focused = true;
                row.EnsureVisible();
                view.Select();
                owner.SaveActiveConnectionState();
            }

            private void EditSelectedMedia()
            {
                InteractiveMediaItem media = SelectedMedia();
                if (media == null) return;
                if (media.IsFileGroup)
                {
                    using (var dialog = new FileGroupEditDialog(media))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) return;
                        dialog.Apply(media);
                    }
                }
                else
                {
                    using (var dialog = new MediaEditDialog(media))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) return;
                        dialog.Apply(media);
                    }
                }
                RefreshMedia();
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void ToggleSelectedMediaLoop()
            {
                foreach (ListViewItem row in mediaView.SelectedItems)
                {
                    var media = row.Tag as InteractiveMediaItem;
                    if (media != null) media.Loop = !media.Loop;
                }
                RefreshMedia();
                RefreshRoutes();
                owner.SaveActiveConnectionState();
            }

            private void ToggleSelectedMediaProtection()
            {
                foreach (ListViewItem row in mediaView.SelectedItems)
                {
                    var media = row.Tag as InteractiveMediaItem;
                    if (media == null) continue;
                    if (HasInteractiveGroupBoundaries(media))
                    {
                        using (var dialog = new FileGroupBoundaryEditDialog(media))
                            if (dialog.ShowDialog(this) == DialogResult.OK)
                            {
                                dialog.Apply(media);
                                media.Selection = new InteractiveFileGroupSelection();
                            }
                    }
                    else media.PreventInterrupt = !media.PreventInterrupt;
                }
                RefreshMedia();
                owner.SaveActiveConnectionState();
            }

            private void RemoveSelectedMedia()
            {
                var ids = mediaView.SelectedItems.Cast<ListViewItem>().Select(row => row.Tag as InteractiveMediaItem)
                    .Where(item => item != null).Select(item => item.RuntimeId).ToList();
                if (ids.Count == 0) return;
                owner.interactiveMedia.RemoveAll(item => ids.Contains(item.RuntimeId));
                foreach (var group in owner.interactiveProjectGroups)
                {
                    group.DefaultPlaybacks.RemoveAll(item => ids.Contains(item.MediaId));
                    group.ActiveDefaultPlaybackId = SelectedInteractiveDefault(group)?.Id ?? Guid.Empty;
                    if (!group.DefaultPlaybacks.Any(item => item.Id == group.ReturnDefaultPlaybackId)) group.ReturnDefaultPlaybackId = Guid.Empty;
                }
                owner.interactiveGroupTransitions.RemoveAll(item => ids.Contains(item.MediaId));
                foreach (InteractiveRouteItem route in owner.AllInteractiveProjectRoutes())
                {
                    foreach (var combination in route.Combinations) combination.MediaIds.RemoveAll(ids.Contains);
                    route.Combinations.RemoveAll(combination => combination.MediaIds.Count == 0);
                    ResetInteractiveCombinationSelection(route);
                }
                RefreshMedia();
                RefreshDefaultPlaybackButtons();
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private InteractiveRouteItem SelectedRoute()
            {
                return routeView.SelectedItems.Count == 1 ? routeView.SelectedItems[0].Tag as InteractiveRouteItem : null;
            }

            private void EditSelectedRoute()
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route != null) EditRoute(route);
            }

            private void EditRoute(InteractiveRouteItem route)
            {
                using (var dialog = new RouteEditDialog(owner.interactiveMedia, route))
                {
                    owner.ConfigureInteractiveObsEditor(dialog);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var edited = new InteractiveRouteItem();
                    dialog.Apply(edited);
                    if (owner.IsInteractiveGroupSwitchHotkey(edited.Hotkey) || owner.HasInteractiveDefaultHotkey(edited.Hotkey) || owner.interactiveProjectGroups.Any(group => SameInteractiveHotkey(group.Hotkey, edited.Hotkey)))
                    { MessageBox.Show(this, T("InteractiveGroupHotkeyConflict")); return; }
                    if (route == null)
                    {
                        route = new InteractiveRouteItem();
                        owner.interactiveRoutes.Add(route);
                    }
                    dialog.Apply(route);
                }
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void RemoveSelectedRoute()
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route == null) return;
                owner.interactiveRoutes.Remove(route);
                RefreshRoutes();
                RegisterRouteHotkeys();
                owner.SaveActiveConnectionState();
            }

            private void TriggerSelected(string source)
            {
                InteractiveRouteItem route = SelectedRoute();
                if (route != null) owner.RequestInteractiveRoute(route, source);
            }

            private void RegisterRouteHotkeys()
            {
                UnregisterRouteHotkeys();
                if (!IsHandleCreated || !Visible || !owner.interactiveWindowOpen || owner.closingMainForm || owner.interactiveHotkeysDisabled) return;
                RegisterProjectGroupHotkeys();
                RegisterDefaultPlaybackHotkeys();
                int index = 0;
                foreach (InteractiveRouteItem route in owner.interactiveRoutes)
                {
                    if (route.Hotkey == null || route.Hotkey.KeyCode == Keys.None) continue;
                    int id = HotkeyBaseId + index++;
                    if (RegisterHotKey(Handle, id, (uint)route.Hotkey.Modifiers, (uint)route.Hotkey.KeyCode))
                        registeredRoutes[id] = route;
                    else
                        AppendOperation(DateTime.Now.ToString("HH:mm:ss") + " " + T("InteractiveHotkeyRegisterFailed") + FormatHotkey(route.Hotkey) + " - " + route.Name);
                }
            }

            private void UnregisterRouteHotkeys()
            {
                if (IsHandleCreated)
                    foreach (int id in registeredRoutes.Keys.ToList()) UnregisterHotKey(Handle, id);
                registeredRoutes.Clear();
                UnregisterProjectGroupHotkeys();
                UnregisterDefaultPlaybackHotkeys();
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                owner.SetInteractiveWindowOpen(Visible);
                RegisterRouteHotkeys();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                RegisterRouteHotkeys();
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                UnregisterRouteHotkeys();
                base.OnHandleDestroyed(e);
            }

            protected override void WndProc(ref Message m)
            {
                if (HandleDefaultPlaybackHotkey(m)) return;
                const int WM_HOTKEY = 0x0312;
                if (m.Msg == WM_HOTKEY && Visible && owner.interactiveWindowOpen && !owner.closingMainForm && !owner.interactiveHotkeysDisabled &&
                    groupSwitchHotkeyRegistered && m.WParam.ToInt32() == GroupSwitchHotkeyId)
                {
                    owner.SwitchNextInteractiveProjectGroup(T("InteractiveHotkeySource") + FormatHotkey(owner.interactiveGroupSwitchHotkey));
                    return;
                }
                if (m.Msg == WM_HOTKEY && Visible && owner.interactiveWindowOpen && !owner.closingMainForm && !owner.interactiveHotkeysDisabled &&
                    registeredProjectGroups.TryGetValue(m.WParam.ToInt32(), out InteractiveProjectGroup group))
                {
                    owner.SwitchInteractiveProjectGroup(group, T("InteractiveHotkeySource") + FormatHotkey(group.Hotkey));
                    return;
                }
                if (m.Msg == WM_HOTKEY && Visible && owner.interactiveWindowOpen && !owner.closingMainForm && !owner.interactiveHotkeysDisabled && registeredRoutes.TryGetValue(m.WParam.ToInt32(), out InteractiveRouteItem route))
                {
                    owner.RequestInteractiveRoute(route, T("InteractiveHotkeySource") + FormatHotkey(route.Hotkey));
                    return;
                }
                base.WndProc(ref m);
            }

            [DllImport("user32.dll")]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll")]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }

        private string DescribeRoute(InteractiveRouteItem route)
        {
            return string.Join(" | ", route.Combinations.Select((combination, index) =>
                (index + 1) + ". " + DescribeCombination(interactiveMedia, combination)));
        }

        private static string DescribeCombination(List<InteractiveMediaItem> media, InteractiveMediaCombination combination)
        {
            return string.Join(" → ", combination.MediaIds.Select(id => media.FirstOrDefault(item => item.RuntimeId == id))
                .Where(item => item != null).Select(FormatInteractiveMediaTitle));
        }

        private static XElement SaveInteractiveRouteElement(InteractiveRouteItem route)
        {
            return new XElement("Route", new XAttribute("name", route.Name),
                new XAttribute("immediate", route.Immediate ? "1" : "0"),
                new XAttribute("comboEnabled", route.ComboEnabled ? "1" : "0"),
                new XAttribute("ignoreRetriggerWhilePlaying", route.IgnoreRetriggerWhilePlaying ? "1" : "0"),
                new XAttribute("hotkey", route.Hotkey == null ? string.Empty : route.Hotkey.KeyCode.ToString()),
                new XAttribute("modifiers", route.Hotkey == null ? string.Empty : route.Hotkey.Modifiers.ToString()),
                new XAttribute("selection", route.RandomOrder ? "random" : "sequential"),
                new XAttribute("recentCount", route.RecentCombinationCount),
                new XAttribute("obsStartScene", route.ObsStartScene), new XAttribute("obsEndScene", route.ObsEndScene),
                new XAttribute("overlapEnabled", InteractiveOverlapOverrideText(route.OverlapEnabled)), new XAttribute("overlapSeconds", route.OverlapSeconds),
                new XElement("Combinations", route.Combinations.Select(combination => new XElement("Combination",
                    new XAttribute("weight", combination.Weight), new XAttribute("cooldownSeconds", combination.CooldownSeconds),
                    new XAttribute("obsStartScene", combination.ObsStartScene), new XAttribute("obsEndScene", combination.ObsEndScene),
                    new XAttribute("overlapEnabled", InteractiveOverlapOverrideText(combination.OverlapEnabled)), new XAttribute("overlapSeconds", combination.OverlapSeconds),
                    combination.MediaIds.Select(id => new XElement("MediaRef", new XAttribute("id", id)))))));
        }

        private static InteractiveRouteItem LoadInteractiveRouteElement(XElement element)
        {
            var route = new InteractiveRouteItem
            {
                Name = GetAttribute(element, "name", string.Empty),
                ObsStartScene = GetAttribute(element, "obsStartScene", string.Empty),
                ObsEndScene = GetAttribute(element, "obsEndScene", string.Empty),
                OverlapEnabled = ReadInteractiveOverlapOverride(element), OverlapSeconds = ReadInteractiveOverlapSeconds(element),
                Immediate = GetAttribute(element, "immediate", "0") == "1",
                ComboEnabled = GetAttribute(element, "comboEnabled", "0") == "1",
                IgnoreRetriggerWhilePlaying = GetAttribute(element, "ignoreRetriggerWhilePlaying", "0") == "1",
                Hotkey = ParseInteractiveHotkey(GetAttribute(element, "hotkey", string.Empty),
                    GetAttribute(element, "modifiers", string.Empty)),
                RandomOrder = !string.Equals(GetAttribute(element, "selection", "random"), "sequential", StringComparison.OrdinalIgnoreCase),
                RecentCombinationCount = GetInteractiveSelectionInt(element, "recentCount", 0, 0, MaxRecentCombinations)
            };
            XElement combinations = element.Element("Combinations");
            // Previous versions saved one sequence directly under Route.
            var sequences = combinations == null ? new[] { element } : combinations.Elements("Combination");
            foreach (XElement sequence in sequences)
            {
                var combination = new InteractiveMediaCombination
                {
                    ObsStartScene = combinations == null ? string.Empty : GetAttribute(sequence, "obsStartScene", string.Empty),
                    ObsEndScene = combinations == null ? string.Empty : GetAttribute(sequence, "obsEndScene", string.Empty),
                    OverlapEnabled = combinations == null ? null : ReadInteractiveOverlapOverride(sequence),
                    OverlapSeconds = combinations == null ? 0.1m : ReadInteractiveOverlapSeconds(sequence),
                    Weight = GetInteractiveSelectionInt(sequence, "weight", 1, 1, MaxCombinationWeight),
                    CooldownSeconds = GetInteractiveSelectionInt(sequence, "cooldownSeconds", 0, 0, MaxCombinationCooldownSeconds)
                };
                foreach (XElement reference in sequence.Elements("MediaRef"))
                {
                    Guid id;
                    if (Guid.TryParse(GetAttribute(reference, "id", null), out id)) combination.MediaIds.Add(id);
                }
                if (combination.MediaIds.Count > 0) route.Combinations.Add(combination);
            }
            return route;
        }

        private sealed class MediaEditDialog : Form
        {
            private readonly TextBox titleBox = new TextBox();
            private readonly TextBox pathBox = new TextBox();
            private readonly TextBox optionsBox = new TextBox();
            private readonly CheckBox loopBox = new CheckBox();
            private readonly CheckBox preventInterruptBox = new CheckBox { AutoSize = true };

            public MediaEditDialog(InteractiveMediaItem item)
            {
                Text = T("InteractiveEditMedia");
                ShowIcon = false;
                Size = new Size(700, 280);
                StartPosition = FormStartPosition.CenterParent;
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(10) };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(grid);
                AddDialogRow(grid, T("Title"), titleBox, 0);
                AddDialogRow(grid, T("ColumnPath"), pathBox, 1);
                AddDialogRow(grid, T("Options"), optionsBox, 2);
                loopBox.Text = T("InteractiveLoopPlayback");
                loopBox.AutoSize = true;
                loopBox.Anchor = AnchorStyles.Left;
                loopBox.Margin = new Padding(3, 6, 3, 6);
                var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                flags.Controls.Add(loopBox);
                preventInterruptBox.Text = T("InteractivePreventInterrupt");
                preventInterruptBox.Checked = item.PreventInterrupt;
                flags.Controls.Add(preventInterruptBox);
                grid.Controls.Add(flags, 1, 3);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 1, 4);
                AcceptButton = ok;
                CancelButton = cancel;
                titleBox.Text = item.Title;
                pathBox.Text = item.Path;
                optionsBox.Text = item.Options;
                loopBox.Checked = item.Loop;
            }

            public void Apply(InteractiveMediaItem item)
            {
                item.Title = titleBox.Text.Trim();
                item.Path = pathBox.Text.Trim();
                item.Options = optionsBox.Text.Trim();
                item.Loop = loopBox.Checked;
                item.PreventInterrupt = preventInterruptBox.Checked;
            }
        }

        private sealed partial class RouteEditDialog : Form
        {
            private readonly List<InteractiveMediaItem> media;
            private readonly TextBox nameBox = new TextBox();
            private readonly ComboBox mediaBox = new ComboBox();
            private readonly ListBox sequenceBox = new ListBox { HorizontalScrollbar = true };
            private readonly ListBox combinationsBox = new ListBox { HorizontalScrollbar = true };
            private readonly HotkeyTextBox hotkeyBox = new HotkeyTextBox();
            private readonly ComboBox commandBox = new ComboBox();
            private readonly CheckBox comboEnabledBox = new CheckBox { AutoSize = true };
            private readonly CheckBox ignoreRetriggerBox = new CheckBox { AutoSize = true };
            private readonly ComboBox selectionBox = new ComboBox();
            private readonly List<InteractiveMediaCombination> combinations = new List<InteractiveMediaCombination>();
            private bool refreshingCombinations;
            private List<Guid> Sequence => combinationsBox.SelectedIndex < 0 ? null : combinations[combinationsBox.SelectedIndex].MediaIds;

            public RouteEditDialog(List<InteractiveMediaItem> media, InteractiveRouteItem route)
            {
                this.media = media;
                Text = T("InteractiveEditProject");
                ShowIcon = false;
                Size = new Size(1040, 740);
                MinimumSize = new Size(900, 640);
                StartPosition = FormStartPosition.CenterParent;
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 2, Padding = new Padding(10) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);
                AddDialogRow(root, T("InteractiveName"), nameBox, 0);
                AddDialogRow(root, T("InteractiveHotkey"), hotkeyBox, 1);
                commandBox.DropDownStyle = ComboBoxStyle.DropDownList;
                commandBox.Items.AddRange(new object[] { "PLAY_NEXT", "PLAY" });
                commandBox.SelectedIndex = 0;
                var commandBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                commandBox.Width = 160;
                commandBar.Controls.Add(commandBox);
                comboEnabledBox.Text = T("InteractiveComboEnabled");
                comboEnabledBox.Checked = route?.ComboEnabled == true;
                var comboTip = new ToolTip(); comboTip.SetToolTip(comboEnabledBox, T("InteractiveComboHint"));
                Disposed += (_, __) => comboTip.Dispose();
                commandBar.Controls.Add(comboEnabledBox);
                ignoreRetriggerBox.Text = T("InteractiveIgnoreRetriggerWhilePlaying");
                ignoreRetriggerBox.Checked = route?.IgnoreRetriggerWhilePlaying == true;
                comboTip.SetToolTip(ignoreRetriggerBox, T("InteractiveIgnoreRetriggerHint"));
                commandBar.Controls.Add(ignoreRetriggerBox);
                commandBar.Controls.Add(MakeButton(T("InteractiveProjectObsScenes"), (_, __) => EditObsScenes(false)));
                commandBar.Controls.Add(MakeButton(T("InteractiveProjectOverlapSettings"), (_, __) => EditOverlapSettings(false)));
                AddDialogRow(root, T("InteractiveTriggerMode"), commandBar, 2);
                selectionBox.DropDownStyle = ComboBoxStyle.DropDownList;
                selectionBox.Items.AddRange(new object[] { T("InteractiveRandom"), T("InteractiveSequential") });
                selectionBox.SelectedIndex = 0;
                AddDialogRow(root, T("InteractiveCombinationSelection"), BuildRandomSelectionBar(), 3);
                var randomHint = new Label { Text = T("InteractiveCombinationRandomHint"), AutoSize = true, Dock = DockStyle.Fill };
                root.Controls.Add(randomHint, 1, 4);

                var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
                editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                var combinationPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
                combinationPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                combinationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                combinationPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var combinationBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                combinationBar.Controls.Add(MakeButton(T("InteractiveAddCombination"), (_, __) => AddCombination(false)));
                combinationBar.Controls.Add(MakeButton(T("InteractiveDuplicateCombination"), (_, __) => AddCombination(true)));
                combinationBar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveCombination()));
                combinationBar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveCombination(-1)));
                combinationBar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveCombination(1)));
                combinationPanel.Controls.Add(combinationBar, 0, 0);
                combinationsBox.Dock = DockStyle.Fill;
                combinationsBox.SelectedIndexChanged += (_, __) => { if (!refreshingCombinations) { RefreshSequence(); RefreshCombinationRandomSettings(); } };
                combinationPanel.Controls.Add(combinationsBox, 0, 1);
                combinationPanel.Controls.Add(BuildCombinationRandomSettings(), 0, 2);
                editor.Controls.Add(combinationPanel, 0, 0);

                var sequencePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                sequencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                sequencePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var addBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                mediaBox.DropDownStyle = ComboBoxStyle.DropDownList;
                mediaBox.Width = 250;
                foreach (InteractiveMediaItem item in media) mediaBox.Items.Add(new MediaChoice(item));
                if (mediaBox.Items.Count > 0) mediaBox.SelectedIndex = 0;
                addBar.Controls.Add(mediaBox);
                addBar.Controls.Add(MakeButton(T("InteractiveAddToSequence"), (_, __) => AddSelectedMedia()));
                addBar.Controls.Add(MakeButton(T("Remove"), (_, __) => RemoveSequenceItem()));
                addBar.Controls.Add(MakeButton(T("Up"), (_, __) => MoveSequenceItem(-1)));
                addBar.Controls.Add(MakeButton(T("Down"), (_, __) => MoveSequenceItem(1)));
                sequencePanel.Controls.Add(addBar, 0, 0);
                sequenceBox.Dock = DockStyle.Fill;
                sequencePanel.Controls.Add(sequenceBox, 0, 1);
                editor.Controls.Add(sequencePanel, 1, 0);
                root.Controls.Add(new Label { Text = T("InteractiveCombinations"), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 5, 0, 0) }, 0, 5);
                root.Controls.Add(editor, 1, 5);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
                var ok = MakeButton(T("InteractiveOk"), (_, __) =>
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
                    if (combinations.Count == 0 || combinations.Any(combination => combination.MediaIds.Count == 0))
                    {
                        MessageBox.Show(this, T("InteractiveEmptyCombination"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                });
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                root.Controls.Add(buttons, 1, 6);
                AcceptButton = ok;
                CancelButton = cancel;

                if (route != null)
                {
                    nameBox.Text = route.Name;
                    hotkeyBox.Value = route.Hotkey == null ? null : route.Hotkey.Clone();
                    commandBox.SelectedItem = route.Immediate ? "PLAY" : "PLAY_NEXT";
                    selectionBox.SelectedIndex = route.RandomOrder ? 0 : 1;
                    recentCountBox.Value = route.RecentCombinationCount;
                    routeObsStartScene = route.ObsStartScene; routeObsEndScene = route.ObsEndScene;
                    routeOverlapEnabled = route.OverlapEnabled; routeOverlapSeconds = route.OverlapSeconds;
                    combinations.AddRange(route.Combinations.Select(combination => new InteractiveMediaCombination
                    {
                        MediaIds = combination.MediaIds.Where(id => media.Any(item => item.RuntimeId == id)).ToList(),
                        Weight = combination.Weight, CooldownSeconds = combination.CooldownSeconds,
                        ObsStartScene = combination.ObsStartScene, ObsEndScene = combination.ObsEndScene,
                        OverlapEnabled = combination.OverlapEnabled, OverlapSeconds = combination.OverlapSeconds
                    }));
                }
                if (combinations.Count == 0) combinations.Add(new InteractiveMediaCombination());
                RefreshCombinations(0);
            }

            private void AddCombination(bool duplicate)
            {
                var combination = new InteractiveMediaCombination();
                if (duplicate && Sequence != null) combination = CopyInteractiveCombination(combinations[combinationsBox.SelectedIndex]);
                combinations.Add(combination);
                RefreshCombinations(combinations.Count - 1);
            }

            private void RemoveCombination()
            {
                int index = combinationsBox.SelectedIndex;
                if (index < 0) return;
                combinations.RemoveAt(index);
                RefreshCombinations(Math.Min(index, combinations.Count - 1));
            }

            private void MoveCombination(int delta)
            {
                int index = combinationsBox.SelectedIndex, target = index + delta;
                if (index < 0 || target < 0 || target >= combinations.Count) return;
                var combination = combinations[index];
                combinations.RemoveAt(index);
                combinations.Insert(target, combination);
                RefreshCombinations(target);
            }

            private void RefreshCombinations(int selectedIndex)
            {
                refreshingCombinations = true;
                combinationsBox.Items.Clear();
                for (int i = 0; i < combinations.Count; i++)
                    combinationsBox.Items.Add((i + 1) + ". " + DescribeCombination(media, combinations[i]));
                combinationsBox.SelectedIndex = selectedIndex;
                refreshingCombinations = false;
                RefreshSequence();
                RefreshCombinationRandomSettings();
            }

            private void AddSelectedMedia()
            {
                var choice = mediaBox.SelectedItem as MediaChoice;
                if (choice == null || Sequence == null) return;
                Sequence.Add(choice.Item.RuntimeId);
                RefreshCombinations(combinationsBox.SelectedIndex);
                sequenceBox.SelectedIndex = sequenceBox.Items.Count - 1;
            }

            private void RemoveSequenceItem()
            {
                int index = sequenceBox.SelectedIndex;
                if (index < 0 || Sequence == null) return;
                Sequence.RemoveAt(index);
                RefreshCombinations(combinationsBox.SelectedIndex);
                if (sequenceBox.Items.Count > 0) sequenceBox.SelectedIndex = Math.Min(index, sequenceBox.Items.Count - 1);
            }

            private void MoveSequenceItem(int delta)
            {
                int index = sequenceBox.SelectedIndex, target = index + delta;
                if (Sequence == null || index < 0 || target < 0 || target >= Sequence.Count) return;
                Guid id = Sequence[index];
                Sequence.RemoveAt(index);
                Sequence.Insert(target, id);
                RefreshCombinations(combinationsBox.SelectedIndex);
                sequenceBox.SelectedIndex = target;
            }

            private void RefreshSequence()
            {
                sequenceBox.Items.Clear();
                if (Sequence == null) return;
                foreach (Guid id in Sequence)
                {
                    InteractiveMediaItem item = media.FirstOrDefault(value => value.RuntimeId == id);
                    sequenceBox.Items.Add(item == null ? "?" : FormatInteractiveMediaTitle(item));
                }
            }

            public void Apply(InteractiveRouteItem route)
            {
                route.Name = nameBox.Text.Trim();
                route.ObsStartScene = routeObsStartScene; route.ObsEndScene = routeObsEndScene;
                route.OverlapEnabled = routeOverlapEnabled; route.OverlapSeconds = routeOverlapSeconds;
                route.Hotkey = hotkeyBox.Value == null ? null : hotkeyBox.Value.Clone();
                route.Immediate = string.Equals(commandBox.SelectedItem as string, "PLAY", StringComparison.Ordinal);
                route.ComboEnabled = comboEnabledBox.Checked;
                route.IgnoreRetriggerWhilePlaying = ignoreRetriggerBox.Checked;
                route.RandomOrder = selectionBox.SelectedIndex == 0;
                route.RecentCombinationCount = (int)recentCountBox.Value;
                route.Combinations = combinations.Select(CopyInteractiveCombination).ToList();
                ResetInteractiveCombinationSelection(route);
            }
        }

        private sealed class HotkeyTextBox : TextBox
        {
            private InteractiveHotkey value;

            public HotkeyTextBox()
            {
                ReadOnly = true;
                ShortcutsEnabled = false;
                KeyDown += CaptureKey;
                PreviewKeyDown += (_, e) => e.IsInputKey = true;
            }

            public InteractiveHotkey Value
            {
                get { return value; }
                set { this.value = value; Text = FormatHotkey(value); }
            }

            private void CaptureKey(object sender, KeyEventArgs e)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Escape)
                {
                    Value = null;
                    return;
                }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
                HotkeyModifiers modifiers = HotkeyModifiers.None;
                if (e.Control) modifiers |= HotkeyModifiers.Control;
                if (e.Shift) modifiers |= HotkeyModifiers.Shift;
                if (e.Alt) modifiers |= HotkeyModifiers.Alt;
                Value = new InteractiveHotkey { KeyCode = e.KeyCode, Modifiers = modifiers };
            }
        }

        private static void AddDialogRow(TableLayoutPanel grid, string label, Control control, int row)
        {
            control.Dock = DockStyle.Fill;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 0, 0) }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        private static string FormatHotkey(InteractiveHotkey hotkey)
        {
            if (hotkey == null || hotkey.KeyCode == Keys.None) return string.Empty;
            var parts = new List<string>();
            if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((hotkey.Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
            parts.Add(hotkey.KeyCode.ToString());
            return string.Join("+", parts);
        }

        private static InteractiveHotkey ParseInteractiveHotkey(string keyText, string modifierText)
        {
            if (!Enum.TryParse(keyText, true, out Keys key) || key == Keys.None) return null;
            if (!Enum.TryParse(modifierText, true, out HotkeyModifiers modifiers)) modifiers = HotkeyModifiers.None;
            return new InteractiveHotkey { KeyCode = key, Modifiers = modifiers };
        }

        private sealed class MediaChoice
        {
            public MediaChoice(InteractiveMediaItem item) { Item = item; }
            public InteractiveMediaItem Item { get; }
            public override string ToString() { return FormatInteractiveMediaTitle(Item); }
        }

        private sealed class InteractiveMediaItem
        {
            public Guid RuntimeId { get; set; } = Guid.NewGuid();
            public string Title { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
            public bool Loop { get; set; }
            public bool PreventInterrupt { get; set; }
            public bool IsFileGroup { get; set; }
            public bool ForcePhasedPlayback { get; set; }
            public InteractiveGroupInterruptionMode? DefaultSwitchInterruptionMode { get; set; }
            public List<string> FilePaths { get; set; } = new List<string>();
            public bool FileGroupRandom { get; set; }
            public bool FileGroupPlayAll { get; set; }
            public bool FileGroupRandomCount { get; set; }
            public int FileGroupMinimumFiles { get; set; } = 1;
            public int FileGroupMaximumFiles { get; set; } = 3;
            public int FileGroupMinimumMiddleFiles { get; set; }
            public bool FileGroupNoRepeat { get; set; } = true;
            public List<string> StartFilePaths { get; set; } = new List<string>();
            public List<string> EndFilePaths { get; set; } = new List<string>();
            public InteractiveFileChoiceRules FileRules { get; set; } = new InteractiveFileChoiceRules();
            public InteractiveFileChoiceRules StartFileRules { get; set; } = new InteractiveFileChoiceRules();
            public InteractiveFileChoiceRules EndFileRules { get; set; } = new InteractiveFileChoiceRules();
            public bool StartFileRandom { get; set; }
            public bool EndFileRandom { get; set; }
            public InteractiveGroupInterruptionMode FileGroupInterruptionMode { get; set; }
            // Active loops share selection progress with new triggers until the group is edited.
            public InteractiveFileGroupSelection Selection { get; set; } = new InteractiveFileGroupSelection();
            public int NextFileIndex { get { return Selection.NextFileIndex; } set { Selection.NextFileIndex = value; } }
            public List<string> RemainingFiles { get { return Selection.RemainingFiles; } }
        }

        private sealed class InteractiveRouteItem
        {
            public Guid RuntimeId { get; } = Guid.NewGuid();
            public bool ComboEnabled { get; set; }
            public bool IgnoreRetriggerWhilePlaying { get; set; }
            public string Name { get; set; } = string.Empty;
            public bool Immediate { get; set; }
            public InteractiveHotkey Hotkey { get; set; }
            public bool RandomOrder { get; set; } = true;
            public bool? OverlapEnabled { get; set; }
            public decimal OverlapSeconds { get; set; } = 0.1m;
            public string ObsStartScene { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
            public List<InteractiveMediaCombination> Combinations { get; set; } = new List<InteractiveMediaCombination>();
            public int NextCombinationIndex { get; set; }
            public int RecentCombinationCount { get; set; }
            public List<InteractiveMediaCombination> RecentCombinations { get; } = new List<InteractiveMediaCombination>();
            public bool RandomSelectionUnavailable { get; set; }
        }

        private sealed class InteractiveMediaCombination
        {
            public List<Guid> MediaIds { get; set; } = new List<Guid>();
            public int Weight { get; set; } = 1;
            public int CooldownSeconds { get; set; }
            public long? LastSelectedTimestamp { get; set; }
            public bool? OverlapEnabled { get; set; }
            public decimal OverlapSeconds { get; set; } = 0.1m;
            public string ObsStartScene { get; set; } = string.Empty;
            public string ObsEndScene { get; set; } = string.Empty;
        }

        private sealed class InteractiveHotkey
        {
            public Keys KeyCode { get; set; }
            public HotkeyModifiers Modifiers { get; set; }
            public InteractiveHotkey Clone() { return new InteractiveHotkey { KeyCode = KeyCode, Modifiers = Modifiers }; }
        }

        [Flags]
        private enum HotkeyModifiers : uint
        {
            None = 0,
            Alt = 1,
            Control = 2,
            Shift = 4,
            Win = 8
        }

        private sealed class InteractivePlaybackEntry
        {
            public Guid RouteId { get; set; }
            public bool IsComboMiddle { get; set; }
            public InteractiveObsPlayback ObsPlayback { get; set; }
            public InteractiveFileGroupRun PhaseRun { get; set; }
            public InteractiveGroupPhase Phase { get; set; }
            public bool PhaseMiddleCompleted { get; set; }
            public bool IsGroupTransition { get; set; }
            public bool IsGroupDefault { get; set; }
            public Guid DefaultPlaybackId { get; set; }
            public InteractiveGroupInterruptionMode? DefaultSwitchInterruptionMode { get; set; }
            public Guid MediaId { get; set; }
            public Guid PlaybackUnitId { get; set; }
            public long BackendFrameId { get; set; }
            public long MinimumBackendFrameId { get; set; }
            public long ReplaceAfterFrameId { get; set; }
            public bool RequiresOutputConfirmation { get; set; }
            public bool PreventInterrupt { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string Options { get; set; } = string.Empty;
            public bool Loop { get; set; }
            public string InputSummary { get; set; } = string.Empty;
            public InteractiveFileGroupLoop GroupLoop { get; set; }
            public int GroupIteration { get; set; }
            public bool GroupEnd { get; set; }
            public InteractivePlaybackEntry CopyForQueue() => (InteractivePlaybackEntry)MemberwiseClone();
        }

        private sealed class InteractiveTriggerRequest
        {
            public bool IsDefaultPlaybackSwitch { get; set; }
            public InteractiveFileGroupRun ComboRun { get; set; }
            public InteractiveRouteItem ComboRoute { get; set; }
            public bool IsGroupTransition { get; set; }
            public Guid ProjectGroupId { get; set; }
            public Guid DefaultPlaybackId { get; set; }
            public int ProjectGroupVersion { get; set; }
            public string RouteName { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public bool Immediate { get; set; }
            public List<InteractivePlaybackEntry> Entries { get; set; } = new List<InteractivePlaybackEntry>();
        }
    }
}
