using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

internal static partial class InteractivePlaybackChecks
{
    // A real local WebSocket peer verifies the existing OBS client and command payloads.
    private sealed class ObsPeer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly List<string> scenes = new List<string>();
        private readonly Task acceptTask;
        private readonly List<Task> connectionTasks = new List<Task>();
        private int sourceChanges;
        private int connections;
        public int ConnectionCount { get { return connections; } }
        public int SourceChanges { get { return sourceChanges; } }
        public string Url { get; private set; }
        public string[] Applied { get { lock (scenes) return scenes.ToArray(); } }
        public ObsPeer()
        {
            listener.Start(); Url = "ws://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref connections);
                    connectionTasks.Add(Task.Run(() => Serve(client)));
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        private static byte[] ReadExact(Stream stream, int count)
        {
            var bytes = new byte[count]; int read = 0;
            while (read < count)
            {
                int amount = stream.Read(bytes, read, count - read);
                if (amount == 0) throw new EndOfStreamException();
                read += amount;
            }
            return bytes;
        }
        private static Dictionary<string, object> Receive(Stream stream)
        {
            var header = ReadExact(stream, 2); int count = header[1] & 127;
            if ((header[0] & 15) == 8) throw new EndOfStreamException();
            if (count == 126) { var length = ReadExact(stream, 2); count = length[0] * 256 + length[1]; }
            if (count == 127) throw new InvalidDataException("Unexpected large OBS test packet");
            var mask = (header[1] & 128) != 0 ? ReadExact(stream, 4) : null;
            var payload = ReadExact(stream, count);
            if (mask != null) for (int i = 0; i < count; i++) payload[i] ^= mask[i % 4];
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(payload));
        }
        private static void Send(Stream stream, object message)
        {
            byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message));
            stream.WriteByte(129);
            if (data.Length < 126) stream.WriteByte((byte)data.Length);
            else { stream.WriteByte(126); stream.WriteByte((byte)(data.Length >> 8)); stream.WriteByte((byte)data.Length); }
            stream.Write(data, 0, data.Length); stream.Flush();
        }
        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    var stream = client.GetStream(); var request = new StringBuilder();
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) request.Append((char)ReadExact(stream, 1)[0]);
                    string key = request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)
                        .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                    string accept;
                    using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                    stream.Write(response, 0, response.Length);
                    Send(stream, new { op = 0, d = new { rpcVersion = 1 } });
                    if (Convert.ToInt32(Receive(stream)["op"]) != 1) throw new InvalidDataException("Expected OBS Identify");
                    Send(stream, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                    while (true)
                    {
                        var packet = Receive(stream); var data = (Dictionary<string, object>)packet["d"];
                        string type = (string)data["requestType"]; var body = new Dictionary<string, object>(); bool ok = true;
                        if (type == "GetSceneList") body["scenes"] = new[] { new { sceneName = "Day" }, new { sceneName = "Night" } };
                        else if (type == "GetCurrentProgramScene") body["currentProgramSceneName"] = "Day";
                        else if (type == "GetSceneItemId") body["sceneItemId"] = 1;
                        else if (type == "SetSceneItemEnabled") Interlocked.Increment(ref sourceChanges);
                        else if (type == "SetCurrentProgramScene")
                        {
                            string scene = (string)((Dictionary<string, object>)data["requestData"])["sceneName"];
                            ok = scene != "Rejected"; if (ok) lock (scenes) scenes.Add(scene);
                        }
                        else ok = false; // Manual switching must never issue source visibility commands.
                        Send(stream, new { op = 7, d = new { requestType = type, requestId = data["requestId"],
                            requestStatus = new { result = ok, code = ok ? 100 : 600, comment = ok ? "" : "test rejection" }, responseData = body } });
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }
        public void Dispose()
        {
            listener.Stop(); acceptTask.GetAwaiter().GetResult();
            Check(Task.WaitAll(connectionTasks.ToArray(), 10000), "Local OBS connections close cleanly");
        }
    }

    private static void WaitObs()
    {
        var task = Field(owner, "interactiveObsActionTask") as Task;
        if (task != null) { Check(task.Wait(10000), "OBS actions complete within the local test deadline"); task.GetAwaiter().GetResult(); }
    }
    private static object[] ObsEntries(object route)
    {
        return ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", route)).Cast<object>().ToArray();
    }
    private static void CheckInteractiveGroupObs(ObsPeer peer)
    {
        string[] fields = { "interactiveProjectGroups", "interactiveRoutes", "activeInteractiveProjectGroupId",
            "interactiveGroupTransitions", "interactiveGroupSwitchRandom", "interactiveGroupSwitchHotkey" };
        var old = fields.ToDictionary(name => name, name => Field(owner, name));
        try
        {
            var state = New("ConnectionTabState"); Set(state, "InteractiveMedia", media);
            var groups = (IList)Get(state, "InteractiveProjectGroups");
            var a = New("InteractiveProjectGroup"); Set(a, "Name", "Day");
            Set(a, "ObsStartScene", "Group A start"); Set(a, "ObsEndScene", "Group A end");
            var b = New("InteractiveProjectGroup"); Set(b, "Name", "Night");
            Set(b, "ObsStartScene", "Group B start"); Set(b, "ObsEndScene", "Group B end");
            var route = Route("sequential"); Set(route, "ObsStartScene", "Project start"); Set(route, "ObsEndScene", "  ");
            var combinations = (IList)Get(route, "Combinations");
            Set(combinations[0], "ObsStartScene", "Combination start");
            Set(combinations[1], "ObsStartScene", "  "); Set(combinations[1], "ObsEndScene", "Combination end");
            var otherRoute = Route("sequential");
            ((IList)Get(a, "Routes")).Add(route); ((IList)Get(b, "Routes")).Add(otherRoute);
            groups.Add(a); groups.Add(b); Set(state, "ActiveInteractiveProjectGroupId", Get(a, "Id"));
            SetField("interactiveProjectGroups", groups); SetField("interactiveRoutes", Get(a, "Routes"));
            SetField("activeInteractiveProjectGroupId", Get(a, "Id"));
            var first = ObsEntries(route); var second = ObsEntries(route); var third = ObsEntries(route);
            var firstSession = Get(first[0], "ObsPlayback");
            Check((string)Get(firstSession, "StartScene") == "Combination start" && (string)Get(firstSession, "EndScene") == "Group A end" &&
                (string)Get(Get(second[0], "ObsPlayback"), "StartScene") == "Project start" &&
                (string)Get(Get(second[0], "ObsPlayback"), "EndScene") == "Combination end" &&
                (string)Get(Get(third[0], "ObsPlayback"), "EndScene") == "Group A end",
                "Start and end separately resolve combination > project > owning group, including whitespace inheritance");
            Check((string)Invoke(owner, "DescribeInteractiveObsScene", route, true) ==
                "1. Combination start | 2. Project start | 3. Project start" &&
                (string)Invoke(owner, "DescribeInteractiveObsScene", otherRoute, false) == "Group B end",
                "OBS list columns show the same inherited settings as playback");
            SetField("activeInteractiveProjectGroupId", Get(b, "Id")); SetField("interactiveRoutes", Get(b, "Routes"));
            var other = ObsEntries(otherRoute);
            Check((string)Get(Get(other[0], "ObsPlayback"), "StartScene") == "Group B start" &&
                (string)Get(Get(ObsEntries(route)[0], "ObsPlayback"), "EndScene") == "Group A end",
                "Group fallback follows the project's owning group even after the active group changes");
            Set(a, "ObsEndScene", "Edited group end");
            Check((string)Get(firstSession, "EndScene") == "Group A end", "Accepted triggers snapshot inherited group scenes");
            Invoke(owner, "SetInteractiveObsPlayback", first[0]); WaitObs();
            Invoke(owner, "SetInteractiveObsPlayback", (object)null); WaitObs();
            Check(peer.Applied.Reverse().Take(2).Reverse().SequenceEqual(new[] { "Combination start", "Group A end" }),
                "Inherited scene snapshots reach real OBS requests after settings and group changes");

            var saveMethod = main.GetMethod("SaveInteractiveProjectGroupsElement", BindingFlags.Static | BindingFlags.NonPublic);
            var xml = (XElement)saveMethod.Invoke(null, new[] { state });
            Invoke(owner, "LoadInteractiveProjectGroups", xml);
            var loaded = (IList)Field(owner, "interactiveProjectGroups");
            Check((string)Get(loaded[0], "ObsStartScene") == "Group A start" && (string)Get(loaded[0], "ObsEndScene") == "Edited group end" &&
                (string)Get(loaded[1], "ObsStartScene") == "Group B start", "Group scenes persist independently through XML save/load");
            int connections = peer.ConnectionCount;
            using (var dialog = (Form)Invoke(owner, "CreateInteractiveGroupObsScenesDialog", loaded[0]))
            {
                Check(dialog.Text.Contains("Day") && (string)Get(dialog, "StartScene") == "Group A start" &&
                    ((ComboBox)Field(dialog, "startSceneBox")).Items.Count == 2, "Group editor uses the shared catalog and identifies the edited group");
                ((ComboBox)Field(dialog, "startSceneBox")).Text = "Canceled group edit";
            }
            Check((string)Get(loaded[0], "ObsStartScene") == "Group A start" && peer.ConnectionCount == connections,
                "Opening and canceling group settings neither connects to OBS nor changes saved scenes");
            foreach (var node in xml.Element("Groups").Elements("Group"))
            { node.Attribute("obsStartScene").Remove(); node.Attribute("obsEndScene").Remove(); }
            Invoke(owner, "LoadInteractiveProjectGroups", xml);
            Check(((IList)Field(owner, "interactiveProjectGroups")).Cast<object>().All(item =>
                (string)Get(item, "ObsStartScene") == "" && (string)Get(item, "ObsEndScene") == ""),
                "Older groups without OBS scene attributes retain blank defaults");
        }
        finally
        {
            Invoke(owner, "SetInteractiveObsPlayback", (object)null); WaitObs();
            foreach (var pair in old) SetField(pair.Key, pair.Value);
        }
    }

    private static void CheckInteractiveObsScenes(Assembly assembly)
    {
        var context = SynchronizationContext.Current; var culture = Thread.CurrentThread.CurrentUICulture;
        string[] fields = { "mainObsUrlBox", "mainObsPasswordBox", "mainObsSceneBox", "playoutObsSceneBox", "pendingLogText", "lastLogFlushAt", "obsOperationsEnabledBox" };
        var old = fields.ToDictionary(name => name, name => Field(owner, name));
        var group = main.GetProperty("CurrentInteractiveProjectGroup", PrivateInstance).GetValue(owner, null);
        object defaultMedia = Get(group, "DefaultMediaId");
        using (var peer = new ObsPeer())
        using (var url = new TextBox { Text = peer.Url })
        using (var enabled = new CheckBox { Checked = true })
        using (var password = new TextBox { Text = "saved-password" })
        using (var mainScene = new ComboBox { Text = "Main manual" })
        using (var playoutScene = new ComboBox { Text = "Playout manual" })
        {
            try
            {
                Invoke(owner, "ClearInteractiveRuntime", true); Set(group, "DefaultMediaId", Guid.Empty);
                SetField("obsOperationsEnabledBox", enabled);
                enabled.CheckedChanged += (_, e) => Invoke(owner, "ObsOperationsEnabledChanged", enabled, e);
                SetField("mainObsUrlBox", url); SetField("mainObsPasswordBox", password);
                SetField("mainObsSceneBox", mainScene); SetField("playoutObsSceneBox", playoutScene);
                SetField("pendingLogText", new StringBuilder()); SetField("lastLogFlushAt", DateTime.UtcNow.AddDays(1));
                SynchronizationContext.SetSynchronizationContext(null);
                ((Task)Invoke(owner, "SwitchObsSceneAsync", true)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "SwitchObsSceneAsync", false)).GetAwaiter().GetResult();
                Check(peer.Applied.SequenceEqual(new[] { "Main manual", "Playout manual" }),
                    "Main and playout manual switching both send scene-only commands through the main OBS connection");
                Check(peer.SourceChanges == 0, "Manual scene buttons do not apply source visibility actions");

                ((Task)Invoke(owner, "RefreshObsCatalogAsync", true)).GetAwaiter().GetResult();
                Check(mainScene.Items.Count == 2 && playoutScene.Items.Count == 2, "Main refresh publishes the scene catalog to other windows");
                int cachedConnections = peer.ConnectionCount;
                using (var editor = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, null }))
                {
                    Invoke(owner, "ConfigureInteractiveObsEditor", editor);
                    foreach (bool combination in new[] { false, true })
                    using (var dialog = (Form)Invoke(editor, "CreateObsScenesDialog", combination))
                    {
                        Check(((ComboBox)Field(dialog, "startSceneBox")).Items.Cast<string>().SequenceEqual(new[] { "Day", "Night" }) &&
                            ((ComboBox)Field(dialog, "endSceneBox")).Items.Count == 2 &&
                            (string)Get(dialog, "StartScene") == "" && (string)Get(dialog, "EndScene") == "",
                            "Project and combination dialogs reuse main scene choices while preserving blank inheritance");
                        Check(peer.ConnectionCount == cachedConnections, "Opening OBS settings with a shared catalog makes no connection");
                    }
                    using (var dialog = (Form)Invoke(editor, "CreateObsScenesDialog", true))
                    {
                        ((ComboBox)Field(dialog, "startSceneBox")).Text = "Custom start";
                        mainScene.Items.Clear(); mainScene.Items.Add("Outdated");
                        SynchronizationContext.SetSynchronizationContext(null);
                        ((Task)Invoke(dialog, "RefreshScenesAsync")).GetAwaiter().GetResult();
                        Check(mainScene.Items.Cast<string>().SequenceEqual(new[] { "Day", "Night" }) && playoutScene.Items.Count == 2 &&
                            (string)Get(dialog, "StartScene") == "Custom start",
                            "Refreshing an interactive dialog republishes the catalog and preserves configured scene names");
                    }
                }
                SynchronizationContext.SetSynchronizationContext(null);

                var route = Route("sequential"); Set(route, "ObsStartScene", "Project start"); Set(route, "ObsEndScene", "Project end");
                var combinations = (IList)Get(route, "Combinations");
                Set(combinations[0], "ObsStartScene", "Variant start"); Set(combinations[1], "ObsEndScene", "Variant end");
                var first = ObsEntries(route); var second = ObsEntries(route);
                var firstSession = Get(first[0], "ObsPlayback"); var secondSession = Get(second[0], "ObsPlayback");
                Check(first.All(entry => Get(entry, "ObsPlayback") == firstSession) && firstSession != secondSession &&
                    (string)Get(firstSession, "StartScene") == "Variant start" && (string)Get(firstSession, "EndScene") == "Project end" &&
                    (string)Get(secondSession, "StartScene") == "Project start" && (string)Get(secondSession, "EndScene") == "Variant end",
                    "Every trigger shares one lifecycle and resolves combination overrides independently for start and end");
                Set(route, "ObsEndScene", "Edited after trigger"); password.Text = "new-password";
                Check((string)Get(firstSession, "EndScene") == "Project end" && (string)Get(firstSession, "Password") == "saved-password",
                    "Queued triggers retain immutable scene and connection snapshots");
                var saved = Save(route); var loaded = Load(saved);
                Check(Save(loaded).ToString() == saved.ToString() && Save(CopyProject(route)).ToString() == saved.ToString(),
                    "Project and combination OBS scenes round-trip and copy independently");
                Check(peer.Applied.Length == 2, "Selecting and queuing combinations sends no OBS action");
                QueueState(second[0], second[1]); SetField("interactiveActive", true);
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Check(peer.Applied.Length == 2, "Clearing a trigger before actual output sends neither start nor end actions");
                QueueState(first[0], first[1]); SetField("interactiveActive", true);
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(first[0], "playing", first[1])); WaitObs();
                Check(peer.Applied.Last() == "Variant start", "Actual output starts the scene action before decoder lookahead");
                ((Task)Invoke(owner, "HandleInteractiveEofAsync", Get(first[0], "InputSummary"))).GetAwaiter().GetResult(); WaitObs();
                Check(peer.Applied.Length == 3, "Decoder EOF and prefetch do not finish an OBS lifecycle");
                ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(first[0], "InputSummary"))).GetAwaiter().GetResult(); WaitObs();
                Check(peer.Applied.Length == 3, "Moving between files in a combination does not repeat scene actions");
                QueueState(first[1], second[0]); SetField("interactivePendingSource", Get(first[1], "InputSummary"));
                ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(first[1], "InputSummary"))).GetAwaiter().GetResult(); WaitObs();
                Check(peer.Applied.Skip(2).SequenceEqual(new[] { "Variant start", "Project start" }) && (bool)Get(firstSession, "Ended"),
                    "A queued handoff skips the intermediate end scene and directly applies the next start scene");
                QueueState(second[1], null); SetField("interactiveActive", true);
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(second[1], "ended", second[1])); WaitObs();
                Check(peer.Applied.Last() == "Variant end" && !(bool)Field(owner, "interactiveActive"),
                    "Terminal actual output finishes the entire combination once without a default video");
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Check(peer.Applied.Count(scene => scene == "Variant end") == 1, "Duplicate completion and clearing do not send duplicate end scenes");

                var queuedRoute = Route("sequential"); Set(queuedRoute, "ObsStartScene", "Queued A"); Set(queuedRoute, "ObsEndScene", "Intermediate B");
                var queuedOld = ObsEntries(queuedRoute).Last();
                Set(queuedRoute, "ObsStartScene", "Queued C"); Set(queuedRoute, "ObsEndScene", "Final D");
                var queuedNext = ObsEntries(queuedRoute)[0];
                Invoke(owner, "ActivateVerifiedInteractiveEntry", queuedOld); WaitObs();
                Set(queuedOld, "BackendFrameId", 300L); Set(queuedNext, "MinimumBackendFrameId", 300L);
                Set(queuedNext, "RequiresOutputConfirmation", true);
                QueueState(queuedOld, queuedNext); SetField("interactiveActive", true);
                SetField("interactivePendingSource", Get(queuedOld, "InputSummary"));
                ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(queuedOld, "InputSummary"))).GetAwaiter().GetResult(); WaitObs();
                Check(peer.Applied.Last() == "Queued A" && !peer.Applied.Contains("Intermediate B"),
                    "Ambiguous source end events do not flash an end scene while awaiting destination confirmation");
                Set(queuedNext, "BackendFrameId", 301L);
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(queuedNext, "playing", queuedNext)); WaitObs();
                Check(peer.Applied.Last() == "Queued C" && !peer.Applied.Contains("Intermediate B"),
                    "Confirmed output replaces the previous end action with the destination start action");
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Check(peer.Applied.Last() == "Final D", "The last trigger still applies its final end scene when cleared");

                var fallbackRoute = Route("sequential"); Set(fallbackRoute, "ObsStartScene", "Fallback start"); Set(fallbackRoute, "ObsEndScene", "Required end");
                var fallbackOld = ObsEntries(fallbackRoute).Last();
                Set(fallbackRoute, "ObsStartScene", ""); Set(fallbackRoute, "ObsEndScene", "End only");
                var endOnly = ObsEntries(fallbackRoute)[0];
                Invoke(owner, "ActivateVerifiedInteractiveEntry", fallbackOld); WaitObs();
                QueueState(fallbackOld, endOnly); SetField("interactiveActive", true);
                SetField("interactivePendingSource", Get(fallbackOld, "InputSummary"));
                ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(fallbackOld, "InputSummary"))).GetAwaiter().GetResult(); WaitObs();
                Check(peer.Applied.Last() == "Required end", "A destination without a start scene retains the previous end action");
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Check(peer.Applied.Last() == "End only", "An end-only trigger retains its own final action");

                var fileGroup = FileGroup(); Set(fileGroup, "Loop", true); Set(fileGroup, "FileGroupPlayAll", true); media.Add(fileGroup);
                try
                {
                    var loopRoute = New("InteractiveRouteItem"); Set(loopRoute, "ObsStartScene", "Loop start"); Set(loopRoute, "ObsEndScene", "Loop end");
                    var combination = New("InteractiveMediaCombination"); ((IList)Get(combination, "MediaIds")).Add(Get(fileGroup, "RuntimeId"));
                    ((IList)Get(loopRoute, "Combinations")).Add(combination);
                    var pass = ObsEntries(loopRoute); QueueState(pass[pass.Length - 1], null); SetField("interactiveActive", true);
                    Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(pass.Last(), "playing", pass.Last())); WaitObs();
                    Invoke(owner, "QueueInteractiveGroupContinuation"); var repeat = QueuedEntries();
                    Check(repeat.All(entry => Get(entry, "ObsPlayback") == Get(pass[0], "ObsPlayback")), "Looped group passes retain the trigger's OBS lifecycle");
                    Invoke(owner, "ActivateVerifiedInteractiveEntry", repeat[0]); WaitObs();
                    Check(peer.Applied.Count(scene => scene == "Loop start") == 1 && !peer.Applied.Contains("Loop end"), "Group repeats do not end or restart scene actions");
                    Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                    Check(peer.Applied.Last() == "Loop end", "Clearing a looping trigger runs its end action exactly once");
                }
                finally { media.Remove(fileGroup); }
                var failedRoute = Route("sequential"); Set(failedRoute, "ObsStartScene", "Rejected"); Set(failedRoute, "ObsEndScene", "After rejected");
                var failed = ObsEntries(failedRoute); Invoke(owner, "ActivateVerifiedInteractiveEntry", failed[0]);
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Check(peer.Applied.Last() == "After rejected" && Field(owner, "pendingLogText").ToString().Contains("test rejection"),
                    "OBS errors are logged and do not prevent later end actions");

                var sharedOperation = main.GetMethod("ApplyObsActionsAsync", PrivateInstance, null,
                    new[] { typeof(string), typeof(string), typeof(string) }, null);
                ((Task)sharedOperation.Invoke(owner, new object[] { "Scheduled", "Logo", "Overlay" })).GetAwaiter().GetResult();
                Check(peer.Applied.Last() == "Scheduled" && peer.SourceChanges == 2,
                    "Scheduled scene and source visibility operations also use the main connection");
                var scheduled = New("ScheduledPlaylistItem"); Set(scheduled, "ObsEndScene", "Scheduled end");
                Invoke(owner, "EnqueueScheduledObsEnd", scheduled, 0L);
                ((Task)Invoke(owner, "ApplyPendingScheduledObsEndsAsync", true, 0L)).GetAwaiter().GetResult();
                Check(peer.Applied.Last() == "Scheduled end", "Queued scheduled end actions snapshot the main OBS connection");
                var held = new TaskCompletionSource<bool>();
                SetField("interactiveObsActionTask", held.Task);
                Invoke(owner, "QueueInteractiveObsScene", "Must not replay", firstSession);
                Invoke(owner, "EnqueueScheduledObsEnd", scheduled, 0L);
                int connections = peer.ConnectionCount;
                enabled.Checked = false;
                Check(((ICollection)Field(owner, "interactiveObsActions")).Count == 0 &&
                    ((ICollection)Field(owner, "pendingScheduledObsEnds")).Count == 0, "Disabling OBS discards waiting interactive and scheduled actions");
                ((Task)Invoke(owner, "SwitchObsSceneAsync", true)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "SwitchObsSceneAsync", false)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "ApplyObsManualActionsAsync")).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "RefreshObsCatalogAsync", true)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "RefreshObsCatalogAsync", false)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "SelectObsSourcesAsync", true, true)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "SelectObsSourcesAsync", false, false)).GetAwaiter().GetResult();
                ((Task)Invoke(owner, "SelectObsEndSourcesAsync", false)).GetAwaiter().GetResult();
                ((Task)sharedOperation.Invoke(owner, new object[] { "Disabled schedule", "Logo", "Overlay" })).GetAwaiter().GetResult();
                Invoke(owner, "EnqueueScheduledObsEnd", scheduled, 0L);
                Check(ObsEntries(route).All(entry => Get(entry, "ObsPlayback") == null), "Disabled interactive triggers do not create OBS actions");
                using (var dialog = (Form)Activator.CreateInstance(Nested("InteractiveObsScenesDialog"), new object[] { true, "", "", peer.Url, "" }))
                {
                    Set(dialog, "IsObsEnabled", new Func<bool>(() => enabled.Checked));
                    ((Task)Invoke(dialog, "RefreshScenesAsync")).GetAwaiter().GetResult();
                }
                Check(peer.ConnectionCount == connections && ((ICollection)Field(owner, "pendingScheduledObsEnds")).Count == 0,
                    "Disabled OBS operations open zero connections across manual, playout, source pickers, refresh and interactive editors");
                var state = New("ConnectionTabState"); Check((bool)Get(state, "ObsEnabled"), "Legacy settings default to OBS enabled");
                Set(state, "ObsEnabled", false);
                Check((string)((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("Settings").Attribute("obsEnabled") == "0",
                    "The OBS enable switch is persisted in connection XML");
                enabled.Checked = true; SynchronizationContext.SetSynchronizationContext(null);
                held.SetResult(true); SetField("interactiveObsActionTask", null);
                Invoke(owner, "QueueInteractiveObsScene", "After re-enable", firstSession); WaitObs();
                Check(peer.Applied.Last() == "After re-enable" && !peer.Applied.Contains("Must not replay"),
                    "Re-enabling OBS permits new actions without replaying discarded requests");
                CheckInteractiveGroupObs(peer);
                var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
                foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
                foreach (string key in new[] { "ObsOperations", "ObsEnabled", "ObsSwitchScene", "ObsSceneRequired", "InteractiveProjectObsScenes", "InteractiveCombinationObsScenes",
                    "InteractiveObsStartScene", "InteractiveObsEndScene", "InteractiveProjectObsHint", "InteractiveCombinationObsHint",
                    "InteractiveGroupObsScenes", "InteractiveGroupObsHint", "InteractiveObsStartSceneColumn", "InteractiveObsEndSceneColumn" })
                {
                    string text = resources.GetString(key, CultureInfo.GetCultureInfo(language));
                    Check(!string.IsNullOrWhiteSpace(text), "OBS resources are present: " + language + "/" + key);
                    if (language != "en") Check(text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'), "OBS UI uses translated resx strings");
                }

                foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
                {
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                    using (var editor = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, route }))
                    {
                        SetField("lastLogFlushAt", DateTime.UtcNow.AddDays(1));
                        var copy = New("InteractiveRouteItem"); Invoke(editor, "Apply", copy);
                        Check((string)Get(copy, "ObsStartScene") == "Project start" && (string)Get(((IList)Get(copy, "Combinations"))[0], "ObsStartScene") == "Variant start",
                            "The project editor retains OBS settings in " + language);
                        Set(((IList)Field(editor, "combinations"))[0], "ObsStartScene", "Detached edit");
                        Check((string)Get(combinations[0], "ObsStartScene") == "Variant start", "Canceling the editor leaves original scene settings intact");
                    }
                    using (var dialog = (Form)Activator.CreateInstance(Nested("InteractiveObsScenesDialog"), new object[] { true, "Custom start", "Custom end", peer.Url, "" }))
                    {
                        IntPtr handle = dialog.Handle; dialog.Size = dialog.MinimumSize; dialog.PerformLayout();
                        SynchronizationContext.SetSynchronizationContext(null);
                        ((Task)Invoke(dialog, "RefreshScenesAsync")).GetAwaiter().GetResult();
                        Check((string)Get(dialog, "StartScene") == "Custom start" && (string)Get(dialog, "EndScene") == "Custom end" &&
                            ((ComboBox)Field(dialog, "startSceneBox")).Items.Count == 2 && ((ComboBox)Field(dialog, "startSceneBox")).Width > 150,
                            "Scene refresh preserves custom names and supports the minimum dialog size in " + language);
                    }
                }
            }
            finally
            {
                Invoke(owner, "ClearInteractiveRuntime", true); WaitObs();
                Set(group, "DefaultMediaId", defaultMedia);
                foreach (var pair in old) SetField(pair.Key, pair.Value);
                Thread.CurrentThread.CurrentUICulture = culture; SynchronizationContext.SetSynchronizationContext(context);
            }
        }
        Console.WriteLine("PASS: real OBS WebSocket scene commands, shared scene catalogs without duplicate refresh, per-phase combination/project/group priority, group persistence and catalog sharing, snapshot/copy persistence, actual playback boundaries, direct scene handoffs without intermediate flashes, loops, clearing, errors, zero connections while disabled, re-enable behavior and translated editors.");
    }
}
