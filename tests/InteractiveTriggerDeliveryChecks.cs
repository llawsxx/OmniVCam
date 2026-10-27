using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class InteractivePlaybackChecks
{
    private static async Task CheckTriggerDeliveryAcrossRecoveryAsync(TcpListener listener, int port)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null;
        SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        object oldWindow = Field(owner, "interactiveWindowOpen"), oldProcessing = Field(owner, "processingInteractiveTriggers");
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Trigger delivery fixture connects");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Initial trigger delivery STATUS") == "STATUS", "Trigger delivery regression uses a real TCP backend");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Trigger delivery backend connected");
            foreach (bool hasNext in new[] { true, false })
            foreach (bool immediate in new[] { true, false })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var current = GroupBatch(media[0])[0]; var next = GroupBatch(media[1])[0]; var target = GroupBatch(media[2])[0];
                QueueState(current, hasNext ? next : null); SetField("interactiveActive", true);
                SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", false);
                var request = ProtectedRequest(target); Set(request, "Immediate", immediate);
                Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
                var dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
                Check(await CompleteSoon(reader.ReadLineAsync(), "A trigger waits for STATUS") == "STATUS", "An accepted request is in flight during verification");
                Invoke(owner, "ApplyInteractiveBackendStatus", "ERR unavailable");
                SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
                Func<string, Task<string>> accepted = command => Task.FromResult("OK recovered");
                await CompleteSoon((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", accepted), "A concurrent watchdog recovers while trigger STATUS is delayed");
                if (hasNext) Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));
                Check(!(bool)Field(owner, "interactiveNeedsStatusCheck"), "The recovery has settled before the older STATUS reply arrives");
                await writer.WriteLineAsync(BackendStatus(current, "playing", current));
                await CompleteSoon(dispatch, "The older verification finishes");
                var waiting = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
                Check(waiting.Length == 1 && waiting[0] == request,
                    "A runtime change during STATUS must retain the accepted trigger even when recovery already cleared the mismatch");
                var retry = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
                if (hasNext)
                {
                    Check(await CompleteSoon(reader.ReadLineAsync(), "Retained request verifies recovered output") == "STATUS", "A retained request verifies the new playback state");
                    await writer.WriteLineAsync(BackendStatus(next, "playing", next));
                }
                string expected = immediate ? "PLAY " : "PLAY_NEXT ";
                Check((await CompleteSoon(reader.ReadLineAsync(), "Retained trigger reaches the backend")).StartsWith(expected + Get(target, "Path")),
                    "A retained request keeps its trigger mode and original media snapshot");
                await writer.WriteLineAsync("OK delivered"); await CompleteSoon(retry, "Retained trigger is delivered once");
                Check(WaitingTriggerCount() == 0, "The retained trigger is removed after dispatch");
            }
            foreach (bool startAnother in new[] { false, true })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var current = GroupBatch(media[0])[0]; var target = GroupBatch(media[2])[0]; var replacement = GroupBatch(media[3])[0];
                QueueState(current, null); SetField("interactiveActive", true); SetField("interactiveWindowOpen", true);
                var cancelled = ProtectedRequest(target); Set(cancelled, "Immediate", true);
                Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", cancelled);
                var dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
                Check(await CompleteSoon(reader.ReadLineAsync(), "Stop overlaps trigger verification") == "STATUS", "Explicit stop is tested while a trigger reply is delayed");
                Invoke(owner, "ClearInteractiveRuntime", true);
                if (startAnother)
                {
                    var fresh = ProtectedRequest(replacement); Set(fresh, "Immediate", true);
                    Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", fresh);
                }
                await writer.WriteLineAsync(BackendStatus(current, "playing", current));
                await CompleteSoon(dispatch, "Stopped trigger verification finishes");
                if (startAnother)
                {
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Fresh trigger resumes after explicit stop")).StartsWith("PLAY " + Get(replacement, "Path")),
                        "A fresh trigger starts after the cancelled processor releases its slot");
                    await writer.WriteLineAsync("OK new invocation");
                    for (int i = 0; i < 200 && (bool)Field(owner, "processingInteractiveTriggers"); i++) await Task.Delay(5);
                    Check(!(bool)Field(owner, "processingInteractiveTriggers") && Field(owner, "interactiveCurrent") == replacement,
                        "Late replies cannot restore the cancelled target or block a new trigger");
                }
                else Check(Field(owner, "interactiveCurrent") == null, "Explicit stop does not resurrect an unsent target");
                Check(WaitingTriggerCount() == 0, "Explicit cancellation never requeues the stopped trigger");
            }
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            if (peer != null) peer.Close(); ((IDisposable)client).Dispose();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
            SetField("interactiveWindowOpen", oldWindow); SetField("processingInteractiveTriggers", oldProcessing);
        }
    }

    private static void CheckTriggerDeliveryAfterQueueWait()
    {
        object oldWindow = Field(owner, "interactiveWindowOpen");
        try
        {
            foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
            foreach (bool cancel in new[] { false, true })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                QueueState(GroupBatch(media[0])[0], null); SetField("interactiveActive", true); SetField("interactiveWindowOpen", true);
                var gate = new TaskCompletionSource<bool>(); SetField("interactiveNextQueueTask", gate.Task);
                var request = ProtectedRequest(GroupBatch(media[2])[0]);
                var dispatch = (Task)Invoke(owner, method, request);
                Check(!dispatch.IsCompleted, "A trigger waits behind an in-flight next command");
                Invoke(owner, "ClearInteractiveRuntime", cancel);
                gate.SetResult(true); dispatch.GetAwaiter().GetResult();
                var waiting = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
                Check(cancel ? waiting.Length == 0 : waiting.Length == 1 && waiting[0] == request,
                    "Queue-wait runtime invalidation retains triggers after recovery and cancels them after explicit stop");
            }
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", oldWindow); }
    }

    private static void CheckInteractiveTriggerDelivery()
    {
        var context = SynchronizationContext.Current;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                CheckTriggerDeliveryAcrossRecoveryAsync(listener, port).GetAwaiter().GetResult();
                CheckTriggerDeliveryAfterQueueWait();
            }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
        Console.WriteLine("PASS: accepted PLAY/PLAY_NEXT triggers survive concurrent recovery and delayed STATUS or next-command waits, preserving snapshots while explicit cancellation still discards old requests and permits fresh triggers.");
    }
}
