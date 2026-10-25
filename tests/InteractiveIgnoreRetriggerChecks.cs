using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

internal static partial class InteractivePlaybackChecks
{
    private static int WaitingTriggerCount()
    {
        return ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Count();
    }

    private static void CheckInteractiveIgnoreRetriggers()
    {
        var culture = Thread.CurrentThread.CurrentUICulture;
        object oldWindow = Field(owner, "interactiveWindowOpen"), oldProcessing = Field(owner, "processingInteractiveTriggers");
        var group = PhasedGroup(); Set(group, "FileGroupPlayAll", false); media.Add(group);
        try
        {
            var route = ComboRoute(group);
            Check(!(bool)Get(route, "IgnoreRetriggerWhilePlaying"), "Existing projects accept repeat triggers by default");
            Set(route, "IgnoreRetriggerWhilePlaying", true);
            var batch = ComboBatch(route);
            SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            foreach (bool immediate in new[] { false, true })
                foreach (var current in batch)
                {
                    QueueState(current, null); SetField("interactiveActive", true);
                    Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
                    int selection = (int)Get(route, "NextCombinationIndex");
                    int middleSelection = (int)Get(Get(group, "Selection"), "NextFileIndex");
                    for (int i = 0; i < 3; i++)
                    {
                        Set(route, "Immediate", immediate);
                        Invoke(owner, "RequestInteractiveRoute", route, "Repeat trigger");
                    }
                    Check(WaitingTriggerCount() == 0 && Field(owner, "interactiveCurrent") == current &&
                        Field(owner, "interactivePending") == null && QueuedEntries().Length == 0,
                        "PLAY and PLAY_NEXT repeats are ignored in every phase, including with combos enabled");
                    Check((int)Get(route, "NextCombinationIndex") == selection &&
                        (int)Get(Get(group, "Selection"), "NextFileIndex") == middleSelection,
                        "Ignored repeats do not consume project combinations or combo media selection");
                }
            QueueState(batch[1], batch[2], batch[0]);
            Invoke(owner, "RequestInteractiveRoute", route, "Preserve queue");
            Check(WaitingTriggerCount() == 0 && Field(owner, "interactivePending") == batch[2] &&
                QueuedEntries().SequenceEqual(new[] { batch[0] }), "Ignored repeats preserve already accepted lookahead and playback queue");
            Set(batch[1], "Loop", true);
            Invoke(owner, "RequestInteractiveRoute", route, "Loop repeat");
            Check(WaitingTriggerCount() == 0, "A looping project remains protected from repeat triggers");
            Set(batch[1], "Loop", false);
            Set(route, "Name", "Renamed while playing");
            Invoke(owner, "RequestInteractiveRoute", route, "Renamed repeat");
            Check(WaitingTriggerCount() == 0, "Renaming the playing project preserves repeat detection");

            var other = ComboRoute(group); Set(other, "Name", Get(route, "Name")); Set(other, "IgnoreRetriggerWhilePlaying", true);
            Invoke(owner, "RequestInteractiveRoute", other, "Different project");
            Check(WaitingTriggerCount() == 1, "A different project sharing name and media is still accepted");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            QueueState(ComboBatch(other)[1], batch[1]);
            Invoke(owner, "RequestInteractiveRoute", route, "Only queued");
            Invoke(owner, "RequestInteractiveRoute", route, "Only queued again");
            Check(WaitingTriggerCount() == 0, "A project already prefetched by the backend rejects repeated triggers");
            var otherEntry = ComboBatch(other)[1];
            foreach (bool immediate in new[] { false, true })
            {
                Set(route, "Immediate", immediate);
                QueueState(otherEntry, null, batch[0]);
                int selection = (int)Get(route, "NextCombinationIndex");
                Invoke(owner, "RequestInteractiveRoute", route, "Playback queue repeat");
                Check(WaitingTriggerCount() == 0 && QueuedEntries().Single() == batch[0] &&
                    (int)Get(route, "NextCombinationIndex") == selection, "PLAY and PLAY_NEXT repeats preserve queued media and selection progress");
                QueueState(otherEntry, null);
                Invoke(owner, "RequestInteractiveRoute", route, "First queued request");
                Check(WaitingTriggerCount() == 1, "A new project is accepted once");
                selection = (int)Get(route, "NextCombinationIndex");
                Invoke(owner, "RequestInteractiveRoute", route, "Repeated queued request");
                Check(WaitingTriggerCount() == 1 && (int)Get(route, "NextCombinationIndex") == selection,
                    "Trigger requests reject duplicates before media selection");
                Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            }
            foreach (string field in new[] { "interactiveStartingEntry", "interactiveSendingNext" })
            {
                QueueState(otherEntry, null); SetField(field, batch[0]);
                Invoke(owner, "RequestInteractiveRoute", route, "Awaiting command confirmation");
                Check(WaitingTriggerCount() == 0, "A project awaiting a command acknowledgement also rejects duplicates");
                SetField(field, null);
            }
            var inFlight = New("InteractiveTriggerRequest"); ((IList)Get(inFlight, "Entries")).Add(batch[0]);
            SetField("interactiveProcessingTrigger", inFlight);
            Invoke(owner, "RequestInteractiveRoute", route, "Dequeued request repeat");
            Check(WaitingTriggerCount() == 0, "Dequeuing a request during STATUS verification does not leave a duplicate-trigger gap");
            SetField("interactiveProcessingTrigger", null);
            var handoff = New("InteractiveProtectedHandoff");
            handoff.GetType().GetField("Request").SetValue(handoff, inFlight); SetField("interactiveProtectedHandoff", handoff);
            Invoke(owner, "RequestInteractiveRoute", route, "Protected handoff repeat");
            Check(WaitingTriggerCount() == 0, "A request waiting for protected media cannot be queued twice");
            Invoke(owner, "CancelInteractiveProtectedHandoff");
            Set(inFlight, "ProjectGroupId", Guid.NewGuid());
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", inFlight);
            Invoke(owner, "RequestInteractiveRoute", route, "Ignore stale group request");
            Check(WaitingTriggerCount() == 2, "An obsolete group request does not prevent a fresh invocation");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            QueueState(otherEntry, null, batch[0]); Set(route, "IgnoreRetriggerWhilePlaying", false);
            Invoke(owner, "RequestInteractiveRoute", route, "Queue repeats enabled");
            Invoke(owner, "RequestInteractiveRoute", route, "Queue repeats enabled again");
            Check(WaitingTriggerCount() == 2, "Disabling the option preserves repeated queued invocations");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");

            QueueState(batch[1], null); Set(route, "IgnoreRetriggerWhilePlaying", false);
            Invoke(owner, "RequestInteractiveRoute", route, "Combo remains available");
            var accepted = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single();
            Check(Get(accepted, "ComboRun") != null, "Disabling ignore repeats restores existing combo behavior");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            Set(route, "IgnoreRetriggerWhilePlaying", true);
            QueueState(null, null);
            Invoke(owner, "RequestInteractiveRoute", route, "Completed project trigger");
            Check(WaitingTriggerCount() == 1, "A completed project can be started again");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            foreach (string flag in new[] { "IsGroupDefault", "IsGroupTransition" })
            {
                QueueState(batch[1], null); SetField("interactiveActive", true); Set(batch[1], flag, true);
                Invoke(owner, "RequestInteractiveRoute", route, flag);
                Check(WaitingTriggerCount() == 1, "Default and transition media never suppress a real project trigger");
                Set(batch[1], flag, false); Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
            }
            var xml = Save(route);
            Check((bool)Get(Load(xml), "IgnoreRetriggerWhilePlaying") &&
                (bool)Get(StaticCall("CopyInteractiveProject", route), "IgnoreRetriggerWhilePlaying"),
                "Ignore-repeat settings survive save/load and project copies");
            xml.Attribute("ignoreRetriggerWhilePlaying").Remove();
            Check(!(bool)Get(Load(xml), "IgnoreRetriggerWhilePlaying"), "Legacy configurations leave ignore repeats disabled");
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using (var dialog = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new[] { media, route }))
                {
                    var toggle = (CheckBox)Field(dialog, "ignoreRetriggerBox");
                    Check(toggle.Checked && toggle.Text != "InteractiveIgnoreRetriggerWhilePlaying", "The translated editor loads the saved repeat-trigger setting");
                    dialog.Size = dialog.MinimumSize;
                    IntPtr handle = dialog.Handle; dialog.PerformLayout();
                    foreach (var control in toggle.Parent.Controls.Cast<Control>())
                    {
                        var bounds = control.Bounds;
                        for (var parent = control.Parent; parent != dialog; parent = parent.Parent) bounds.Offset(parent.Location);
                        Check(dialog.ClientRectangle.Contains(bounds), "Trigger controls fit in the minimum-size translated editor");
                    }
                    toggle.Checked = false;
                    Check((bool)Get(route, "IgnoreRetriggerWhilePlaying"), "Canceling edits leaves the live repeat-trigger setting unchanged");
                    var id = Get(route, "RuntimeId"); Invoke(dialog, "Apply", route);
                    Check(!(bool)Get(route, "IgnoreRetriggerWhilePlaying") && Equals(id, Get(route, "RuntimeId")), "Applying repeat-trigger settings preserves project identity");
                    Set(route, "IgnoreRetriggerWhilePlaying", true);
                }
                foreach (string key in new[] { "InteractiveIgnoreRetriggerWhilePlaying", "InteractiveIgnoreRetriggerHint", "InteractiveRepeatedTriggerIgnored" })
                {
                    string value = (string)StaticCall("T", key);
                    Check(value != key && !string.IsNullOrWhiteSpace(value) &&
                        (language == "en" || value.Any(c => c >= '\u4e00' && c <= '\u9fff')), "Repeat-trigger controls, tips and logs use existing language resources");
                }
            }
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group);
            SetField("interactiveWindowOpen", oldWindow); SetField("processingInteractiveTriggers", oldProcessing);
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        CheckQueuedRepeatDuringStatus();
        Console.WriteLine("PASS: repeat-trigger suppression across playback, request/media queues, prefetched and in-flight commands, protected handoffs, real delayed STATUS, cancellation, phases, looping, combo precedence, identity, opt-out, persistence and translated editors.");
    }
    private static async Task CheckQueuedRepeatDuringStatusAsync(TcpListener listener, int port)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        object oldWindow = Field(owner, "interactiveWindowOpen"), oldProcessing = Field(owner, "processingInteractiveTriggers");
        TcpClient peer = null; var group = PhasedGroup(); media.Add(group);
        SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Duplicate-trigger fixture connects");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Initial duplicate-trigger STATUS") == "STATUS", "Duplicate-trigger regression uses a real TCP connection");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Establish duplicate-trigger connection");
            var route = ComboRoute(group); var other = ComboRoute(group);
            Set(route, "IgnoreRetriggerWhilePlaying", true);
            var current = ComboBatch(other)[1]; QueueState(current, null); SetField("interactiveActive", true);
            SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            Invoke(owner, "RequestInteractiveRoute", route, "Initial queued project");
            SetField("processingInteractiveTriggers", false);
            var processing = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "Delayed duplicate-trigger STATUS") == "STATUS", "Processing dequeues the request before awaiting STATUS");
            Check(WaitingTriggerCount() == 0 && Field(owner, "interactiveProcessingTrigger") != null, "The in-flight invocation is retained during STATUS verification");
            int selection = (int)Get(route, "NextCombinationIndex");
            Invoke(owner, "RequestInteractiveRoute", route, "Duplicate during STATUS");
            Check(WaitingTriggerCount() == 0 && (int)Get(route, "NextCombinationIndex") == selection,
                "A repeat during a real delayed STATUS response never enters the queue or consumes selection");
            SetField("interactiveWindowOpen", false); Invoke(owner, "ClearInteractiveRuntime", true);
            await writer.WriteLineAsync(BackendStatus(current, "playing", current));
            await CompleteSoon(processing, "Cancelled in-flight invocation finishes");
            Check(Field(owner, "interactiveProcessingTrigger") == null && WaitingTriggerCount() == 0,
                "Cancellation and late STATUS replies release duplicate-trigger reservations");
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group);
            if (peer != null) peer.Close(); ((IDisposable)client).Dispose();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
            SetField("interactiveWindowOpen", oldWindow); SetField("processingInteractiveTriggers", oldProcessing);
        }
    }

    private static void CheckQueuedRepeatDuringStatus()
    {
        var context = SynchronizationContext.Current;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try { CheckQueuedRepeatDuringStatusAsync(listener, port).GetAwaiter().GetResult(); }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
    }

}
