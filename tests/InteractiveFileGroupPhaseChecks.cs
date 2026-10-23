using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

internal static partial class InteractivePlaybackChecks
{
    private static object PhasedGroup(string mode = "AfterMiddle")
    {
        var group = FileGroup(); Set(group, "FileGroupPlayAll", true);
        ((IList)Get(group, "StartFilePaths")).Add("intro1.mp4");
        ((IList)Get(group, "StartFilePaths")).Add("intro2.mp4");
        ((IList)Get(group, "EndFilePaths")).Add("outro1.mp4");
        ((IList)Get(group, "EndFilePaths")).Add("outro2.mp4");
        Set(group, "FileGroupInterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), mode));
        return group;
    }
    private static string[] EntryPaths(IEnumerable<object> entries) { return entries.Select(e => (string)Get(e, "Path")).ToArray(); }
    private static object StaticCall(string name, params object[] args)
    {
        return main.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    }
    private static void CheckFileGroupPhases(Assembly assembly)
    {
        object oldRandom = Field(owner, "random"); var culture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            SetField("random", new Random(987));
            var group = PhasedGroup(); var batch = GroupBatch(group);
            Check(EntryPaths(batch).SequenceEqual(new[] { "intro1.mp4", "g1.mp4", "g2.mp4", "g3.mp4", "outro1.mp4" }), "A group selects one intro, all selected middle files and one outro in order");
            Check(EntryPaths(GroupBatch(group)).SequenceEqual(new[] { "intro2.mp4", "g1.mp4", "g2.mp4", "g3.mp4", "outro2.mp4" }), "Intro and outro rotate independently of middle order");
            Check(batch.Select(e => Get(e, "PlaybackUnitId")).Distinct().Count() == 1 && batch.All(e => (bool)Get(e, "PreventInterrupt")), "One phased invocation shares a protected identity");
            Set(group, "FileGroupPlayAll", false);
            Check(EntryPaths(GroupBatch(group)).SequenceEqual(new[] { "intro1.mp4", "g1.mp4", "outro1.mp4" }), "One-file scope still wraps the selected middle with one intro and one outro");
            Set(group, "Loop", true); batch = GroupBatch(group);
            var run = Get(batch[0], "PhaseRun"); var loop = Get(batch[0], "GroupLoop");
            QueueState(batch[1], null, batch[2]); Invoke(owner, "QueueInteractivePhasedContinuation");
            var next = QueuedEntries();
            Check(next.Length == 2 && (string)Get(next[0], "Path") == "g3.mp4" && next[1] == batch[2] && Equals(Get(next[0], "PhaseRun"), run), "Loops repeat only the middle and reserve the selected outro");
            Check((int)Get(loop, "Iteration") == 1 && !(bool)Get(run, "Ending"), "A middle loop remains in the same invocation");
            var destination = GroupBatch(media[0])[0];
            QueueState(next[0], null, next[1], destination); Invoke(owner, "QueueInteractivePhasedContinuation");
            Check(QueuedEntries().SequenceEqual(new[] { next[1], destination }), "A queued successor causes the outro to run once instead of another middle pass");
            foreach (string pool in new[] { "FileRules", "StartFileRules", "EndFileRules" })
            {
                group = PhasedGroup(); Set(group, "FileGroupRandom", true); Set(group, "FileGroupNoRepeat", false); Set(group, "FileGroupPlayAll", false);
                Set(group, "StartFileRandom", true); Set(group, "EndFileRandom", true);
                string pathsName = pool == "FileRules" ? "FilePaths" : pool == "StartFileRules" ? "StartFilePaths" : "EndFilePaths";
                var paths = (IList)Get(group, pathsName); string heavy = (string)paths[0];
                var rules = Get(group, pool); ((IDictionary)Get(rules, "Weights"))[heavy] = 9;
                int hits = 0;
                for (int i = 0; i < 4000; i++)
                {
                    batch = GroupBatch(group); int index = pool == "FileRules" ? 1 : pool == "StartFileRules" ? 0 : 2;
                    if ((string)Get(batch[index], "Path") == heavy) hits++;
                }
                Check(hits > 3150 && hits < 3740, "Relative file weights apply to " + pool);
                Set(rules, "RecentCount", 10000); var history = new Queue<string>(); int limit = paths.Count - 1;
                for (int i = 0; i < 90; i++)
                {
                    batch = GroupBatch(group); int index = pool == "FileRules" ? 1 : pool == "StartFileRules" ? 0 : 2;
                    string selected = (string)Get(batch[index], "Path");
                    Check(!history.Contains(selected), "Excessive recent-N is capped without repeating recent selections for " + pool);
                    history.Enqueue(selected); if (history.Count > limit) history.Dequeue();
                }
            }
            group = FileGroup(true); var middleRules = Get(group, "FileRules"); Set(middleRules, "RecentCount", 2);
            var recent = new Queue<string>(); var rng = new Random(23);
            for (int i = 0; i < 90; i++)
            {
                string selected = SelectFile(group, rng); Check(!recent.Contains(selected), "Recent-N and no-repeat rounds compose across round boundaries");
                recent.Enqueue(selected); if (recent.Count > 2) recent.Dequeue();
            }
            Set(group, "FileGroupPlayAll", true); recent.Clear();
            for (int i = 0; i < 20; i++)
            {
                batch = GroupBatch(group); Check(batch.Length == 3 && EntryPaths(batch).Distinct().Count() == 3, "Weighted play-all always includes every middle exactly once");
                foreach (string selected in EntryPaths(batch)) { Check(!recent.Contains(selected), "Play-all preserves recent history across shuffled rounds"); recent.Enqueue(selected); if (recent.Count > 2) recent.Dequeue(); }
            }
            ((IList)Get(group, "FilePaths")).Clear(); ((IList)Get(group, "FilePaths")).Add("only.mp4");
            Check(GroupBatch(group).Length == 1 && GroupBatch(group).Length == 1, "One-file pools remain playable with excessive exclusions");
            group = PhasedGroup(); Set(group, "FileGroupPlayAll", false); Set(group, "FileGroupRandomCount", true);
            Set(group, "FileGroupRandom", true); Set(group, "FileGroupMinimumFiles", 3); Set(group, "FileGroupMaximumFiles", 5);
            for (int i = 4; i <= 8; i++) ((IList)Get(group, "FilePaths")).Add("g" + i + ".mp4");
            var sizes = new HashSet<int>(); var stream = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                batch = GroupBatch(group); int count = batch.Length - 2; sizes.Add(count);
                var chosen = EntryPaths(batch.Skip(1).Take(count)); stream.AddRange(chosen);
                Check(count >= 3 && count <= 5, "Random-count groups draw a fresh bounded playback count per invocation");
                Check(Get(batch[0], "Phase").ToString() == "Start" && Get(batch[batch.Length - 1], "Phase").ToString() == "End", "Variable middle counts preserve exactly one intro and outro");
            }
            Check(sizes.SetEquals(new[] { 3, 4, 5 }), "Random-count invocations can choose different lengths within the configured range");
            for (int i = 0; i + 8 <= stream.Count; i += 8) Check(stream.Skip(i).Take(8).Distinct().Count() == 8, "Random counts preserve no-repeat rounds even when a batch crosses the round boundary");
            var countCopy = StaticCall("LoadInteractiveMediaElement", StaticCall("SaveInteractiveMediaElement", group));
            Check((bool)Get(countCopy, "FileGroupRandomCount") && (int)Get(countCopy, "FileGroupMinimumFiles") == 3 && (int)Get(countCopy, "FileGroupMaximumFiles") == 5, "Random count mode and its inclusive range survive XML persistence");
            var countSnapshot = StaticCall("SnapshotInteractiveFileGroup", group); Set(group, "FileGroupMaximumFiles", 8);
            Check((int)Get(countSnapshot, "FileGroupMaximumFiles") == 5, "Queued playback snapshots preserve their selected count configuration");
            Set(group, "FileGroupMinimumFiles", 100); Set(group, "FileGroupMaximumFiles", 200);
            batch = GroupBatch(group);
            Check(batch.Length >= 102 && batch.Length <= 202, "Random playback counts can exceed the number of source files");
            Check(EntryPaths(batch.Skip(1).Take(batch.Length - 2)).Distinct().Count() == 8, "Long no-repeat playback starts new selection rounds until the count is filled");
            countCopy = StaticCall("LoadInteractiveMediaElement", StaticCall("SaveInteractiveMediaElement", group));
            Check((int)Get(countCopy, "FileGroupMinimumFiles") == 100 && (int)Get(countCopy, "FileGroupMaximumFiles") == 200, "Long playback ranges survive save and reload without being capped to pool size");
            var weightedLong = FileGroup(true, false); Set(weightedLong, "FileGroupRandomCount", true);
            Set(weightedLong, "FileGroupMinimumFiles", 3000); Set(weightedLong, "FileGroupMaximumFiles", 3000);
            ((IDictionary)Get(Get(weightedLong, "FileRules"), "Weights"))["g1.mp4"] = 8;
            var longDraws = ((IEnumerable)StaticCall("SelectInteractiveMediaPaths", weightedLong, new Random(71))).Cast<string>().ToArray();
            int frequent = longDraws.Count(path => path == "g1.mp4");
            Check(frequent > 2250 && frequent < 2550, "Long playback without no-repeat rounds retains weighted selection with replacement");
            Set(Get(weightedLong, "FileRules"), "RecentCount", 2);
            longDraws = ((IEnumerable)StaticCall("SelectInteractiveMediaPaths", weightedLong, new Random(72))).Cast<string>().ToArray();
            for (int i = 2; i < longDraws.Length; i++) Check(longDraws[i] != longDraws[i - 1] && longDraws[i] != longDraws[i - 2], "Recent-N exclusions remain active throughout an oversized playback selection");
            group = PhasedGroup(); Set(group, "FileGroupRandomCount", true); Set(group, "FileGroupMinimumFiles", 2); Set(group, "FileGroupMaximumFiles", 2);
            Check(EntryPaths(GroupBatch(group)).Skip(1).Take(2).SequenceEqual(new[] { "g1.mp4", "g2.mp4" }) &&
                EntryPaths(GroupBatch(group)).Skip(1).Take(2).SequenceEqual(new[] { "g3.mp4", "g1.mp4" }), "Fixed count ranges work with sequential file rotation");
            group = PhasedGroup(); Set(group, "FileGroupPlayAll", false); Set(group, "FileGroupMinimumMiddleFiles", 7);
            Check(GroupBatch(group).Length == 9, "Default minimum-middle protection expands a one-file selection to the required middle count");
            var minimumCopy = StaticCall("LoadInteractiveMediaElement", StaticCall("SaveInteractiveMediaElement", group));
            Check((int)Get(minimumCopy, "FileGroupMinimumMiddleFiles") == 7 && (int)Get(StaticCall("SnapshotInteractiveFileGroup", group), "FileGroupMinimumMiddleFiles") == 7, "Minimum-middle settings survive persistence and queued snapshots");
            Set(group, "FileGroupInterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), "Any"));
            Check(GroupBatch(group).Length == 3, "The minimum-middle guarantee is specific to default interruption mode");
            foreach (string policy in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
            {
                var policyGroup = PhasedGroup(policy);
                Check(Get(StaticCall("LoadInteractiveMediaElement", StaticCall("SaveInteractiveMediaElement", policyGroup)), "FileGroupInterruptionMode").ToString() == policy,
                    "Every interruption policy persists independently: " + policy);
            }
            group = PhasedGroup("ImmediateMiddle"); Set(group, "StartFileRandom", true);
            Set(Get(group, "StartFileRules"), "RecentCount", 1);
            ((IDictionary)Get(Get(group, "EndFileRules"), "Weights"))["outro1.mp4"] = 7;
            var xml = (XElement)StaticCall("SaveInteractiveMediaElement", group);
            var loaded = StaticCall("LoadInteractiveMediaElement", xml);
            Check(((IList)Get(loaded, "StartFilePaths")).Count == 2 && (bool)Get(loaded, "StartFileRandom") && Get(loaded, "FileGroupInterruptionMode").ToString() == "ImmediateMiddle", "Boundary lists, independent selection and interruption policy survive persistence");
            Check((int)Get(Get(loaded, "StartFileRules"), "RecentCount") == 1 && (int)((IDictionary)Get(Get(loaded, "EndFileRules"), "Weights"))["outro1.mp4"] == 7, "Per-pool history limits and per-file weights survive persistence");
            var snapshot = StaticCall("SnapshotInteractiveFileGroup", group);
            ((IList)Get(group, "StartFilePaths")).Clear(); ((IDictionary)Get(Get(group, "EndFileRules"), "Weights"))["outro1.mp4"] = 1;
            Check(((IList)Get(snapshot, "StartFilePaths")).Count == 2 && (int)((IDictionary)Get(Get(snapshot, "EndFileRules"), "Weights"))["outro1.mp4"] == 7, "Playback snapshots isolate boundary lists and file probabilities from live edits");
            Check(Equals(Get(snapshot, "Selection"), Get(group, "Selection")), "Accepted invocations share only the established selection progress");
            xml.Element("StartFiles").Remove(); xml.Element("EndFiles").Remove(); xml.Attribute("interruptionMode").Remove();
            foreach (var file in xml.Element("Files").Elements()) file.Attribute("weight").Remove();
            loaded = StaticCall("LoadInteractiveMediaElement", xml);
            Check(!(bool)StaticCall("HasInteractiveGroupBoundaries", loaded) && (int)Invoke(Get(loaded, "FileRules"), "WeightFor", "g1.mp4") == 1, "Legacy file groups retain equal probabilities and their original protection behavior");
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                group = PhasedGroup();
                using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupBoundaryEditDialog"), new[] { group }))
                {
                    dialog.Size = dialog.MinimumSize; dialog.CreateControl(); dialog.PerformLayout();
                    Invoke(dialog, "AddPaths", true, new[] { "intro3.mp4", "INTRO1.MP4" });
                    Check(((IList)Get(group, "StartFilePaths")).Count == 2, "Unsaved phase edits leave the library intact");
                    var list = (ListBox)Field(dialog, "startFilesBox"); list.SelectedIndex = 2; Invoke(dialog, "MoveFile", true, -1);
                    var policy = (ComboBox)Field(dialog, "interruptionBox");
                    ((NumericUpDown)Field(dialog, "minimumMiddleBox")).Value = 20;
                    policy.SelectedIndex = 2;
                    Check(!((NumericUpDown)Field(dialog, "minimumMiddleBox")).Enabled, "Middle-count protection is editable only for default mode");
                    Invoke(dialog, "Apply", group);
                    Check((int)Get(group, "FileGroupMinimumMiddleFiles") == 20, "The boundary editor preserves a playback minimum larger than the source pool");
                    Check(((IList)Get(group, "StartFilePaths")).Count == 3 && (string)((IList)Get(group, "StartFilePaths"))[1] == "intro3.mp4" && Get(group, "FileGroupInterruptionMode").ToString() == "ImmediateMiddle", "The translated phase editor adds, reorders and saves its policy");
                    Check(list.Height > 100, "Boundary files remain visible at minimum dialog size in " + language);
                }
                using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupFileRulesDialog"), new[] { Get(group, "FilePaths"), Get(group, "FileRules") }))
                {
                    dialog.Size = dialog.MinimumSize; dialog.CreateControl(); dialog.PerformLayout();
                    var view = (ListView)Field(dialog, "filesView"); var viewHandle = view.Handle; view.Items[0].Selected = true;
                    Invoke(dialog, "UpdateSelectedWeight");
                    ((NumericUpDown)Field(dialog, "weightBox")).Value = 5; ((NumericUpDown)Field(dialog, "recentBox")).Value = 2;
                    Check((int)Invoke(Get(group, "FileRules"), "WeightFor", "g1.mp4") == 1, "Probability edits are isolated until the parent confirms");
                    var result = Get(dialog, "Result"); Check((int)Get(result, "RecentCount") == 2 && (int)Invoke(result, "WeightFor", "g1.mp4") == 5, "The file probability editor saves weight and recent-N settings");
                    Check(view.Height > 100, "Probability file rows remain visible at minimum size in " + language);
                }
                using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupEditDialog"), new[] { group }))
                {
                    ((ComboBox)Field(dialog, "scopeBox")).SelectedIndex = 2;
                    ((NumericUpDown)Field(dialog, "minimumFilesBox")).Value = 30;
                    ((NumericUpDown)Field(dialog, "maximumFilesBox")).Value = 50;
                    Invoke(dialog, "Apply", group);
                    Check((bool)Get(group, "FileGroupRandomCount") && !(bool)Get(group, "FileGroupPlayAll") &&
                        (int)Get(group, "FileGroupMinimumFiles") == 30 && (int)Get(group, "FileGroupMaximumFiles") == 50 &&
                        (int)Get(group, "FileGroupMinimumMiddleFiles") == 20, "The translated main editor saves oversized random ranges and middle guarantees");
                    dialog.Size = dialog.MinimumSize; dialog.PerformLayout();
                    Check(((ListBox)Field(dialog, "filesBox")).Height > 100, "Count controls and phase buttons fit the minimum group editor size in " + language);
                }
                foreach (string key in new[] { "FileGroupBoundarySettings", "FileGroupBoundaryHint", "FileGroupFileRules", "FileGroupFileRulesHint", "FileGroupFileName", "FileGroupInterruptDefault", "FileGroupInterruptMiddle", "FileGroupInterruptAny", "FileGroupInterruptNone", "FileGroupRandomCount", "FileGroupMinimumFiles", "FileGroupMaximumFiles", "FileGroupMinimumMiddleFiles", "FileGroupMinimumMiddleHint" })
                {
                    string text = (string)StaticCall("T", key);
                    Check(!string.IsNullOrWhiteSpace(text) && text != key && (language == "en" || text.Any(c => c >= '\u4e00' && c <= '\u9fff')), "New UI strings use the existing localized resources: " + language + "/" + key);
                }
            }
        }
        finally { Thread.CurrentThread.CurrentUICulture = culture; SetField("random", oldRandom); Invoke(owner, "ClearInteractiveRuntime", true); }
        Console.WriteLine("PASS: start/middle/end sequences, variable middle counts, minimum-middle guarantees, weighted recent-N pools, middle-only loops, persistence and isolated translated editors.");
        CheckPhaseHandoffs();
    }
    private static object Started(object entry, long frame)
    {
        return Activator.CreateInstance(Nested("BackendInputEvent"), new object[] { Enum.Parse(Nested("BackendInputEventType"), "Started"), frame, Get(entry, "InputSummary") });
    }
    private static async Task WaitForPhasePendingAsync()
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (Field(owner, "interactivePending") == null || Field(owner, "interactiveNextQueueTask") == null ||
            !((Task)Field(owner, "interactiveNextQueueTask")).IsCompleted)
        {
            Check(timeout.ElapsedMilliseconds < 3000, "The phase next command receives its acknowledgement");
            await Task.Delay(1);
        }
    }
    private static async Task CheckPhaseHandoffsAsync(TcpListener listener, int port)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null; SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Phase handoff fixture connects");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "The fixture receives STATUS") == "STATUS", "Phase handoff tests use an isolated backend");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Phase fixture establishes its generation");
            foreach (string mode in new[] { "AfterMiddle", "ImmediateMiddle", "Any" })
            foreach (string phase in new[] { "Start", "Middle", "End" })
            foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
            {
                Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveNextQueueTask", null);
                var group = PhasedGroup(mode); Set(group, "Loop", true); var batch = GroupBatch(group);
                int index = phase == "Start" ? 0 : phase == "Middle" ? (mode == "ImmediateMiddle" ? 3 : 1) : 4;
                object current = batch[index], oldPending = index < 4 ? batch[index + 1] : GroupBatch(media[1])[0];
                Set(current, "BackendFrameId", 100L); Set(oldPending, "BackendFrameId", 101L);
                if (phase == "End") Set(oldPending, "Loop", true);
                QueueState(current, oldPending, batch.Skip(index + 2).ToArray()); SetField("interactiveActive", true);
                SetField("interactiveObservedBackendFrameId", 101L);
                var target = GroupBatch(media[2])[0];
                var task = (Task)Invoke(owner, method, ProtectedRequest(target));
                bool immediate = mode == "Any" && method == "StartInteractiveImmediatelyAsync" || mode == "ImmediateMiddle" && phase == "Middle";
                string path = mode == "Any" || phase == "End" ? (string)Get(target, "Path") : (string)Get(batch[4], "Path");
                string command = await CompleteSoon(reader.ReadLineAsync(), mode + "/" + phase + "/" + method + " issues a playback command");
                Check(command.StartsWith((immediate ? "PLAY " : "PLAY_NEXT ") + path), mode + "/" + phase + "/" + method + " uses the correct command and boundary");
                if (!immediate) Check(command.Contains("after_frame_id=100"), "Replacement commands identify the actual invocation despite old lookahead");
                await writer.WriteLineAsync("OK phase boundary");
                if (mode == "ImmediateMiddle" && phase == "Middle")
                {
                    command = await CompleteSoon(reader.ReadLineAsync(), "Immediate middle interruption prequeues the target after the outro");
                    Check(command.StartsWith("PLAY_NEXT " + Get(target, "Path")), "Immediate middle interruption plays the outro before its target");
                    await writer.WriteLineAsync("OK target");
                }
                if (mode == "Any") await CompleteSoon(task, "The arbitrary-interruption command completes");
                else await WaitForPhasePendingAsync();
                if (mode == "Any")
                {
                    await CompleteSoon(task, "Any-time interruption releases dispatch without a protected wait");
                    Check(QueuedEntries().Length == 0 && (immediate ? Field(owner, "interactiveCurrent") : Field(owner, "interactivePending")) == target, "Any-time mode discards the group's remaining middle and outro");
                    continue;
                }
                Check(!task.IsCompleted && (bool)Get(Get(current, "GroupLoop"), "Cancelled"), "Protected handoffs stop middle looping and wait for actual destination output");
                if (phase != "End")
                {
                    var outro = mode == "ImmediateMiddle" && phase == "Middle" ? Field(owner, "interactiveCurrent") : Field(owner, "interactivePending");
                    Check((string)Get(outro, "Path") == "outro1.mp4", "Protected interruptions omit remaining middles and select the reserved outro");
                    if (immediate)
                    {
                        Check(!(bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 101)), "A prefetched old outro cannot confirm a fresh PLAY");
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 102));
                        Check((long)Get(outro, "BackendFrameId") == 102L, "A restarted prefetched outro accepts its new output invocation");
                    }
                    else
                    {
                        Check(!(bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(oldPending, 101)), "Stale middle/lookahead events cannot promote an outro replacement");
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 102));
                        Check(Field(owner, "interactiveCurrent") == outro && !task.IsCompleted, "The target still waits while its protected outro plays");
                        var queuing = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                        command = await CompleteSoon(reader.ReadLineAsync(), "The outro prequeues the target");
                        Check(command.StartsWith("PLAY_NEXT " + Get(target, "Path")), "No middle is inserted between interrupted start and end");
                        await writer.WriteLineAsync("OK target"); await CompleteSoon(queuing, "The target command is acknowledged");
                    }
                }
                Invoke(owner, "ApplyInteractiveOutputStarted", Started(target, 103)); await CompleteSoon(task, "Actual target output releases the protected dispatcher");
                Check(Field(owner, "interactiveCurrent") == target && Field(owner, "interactiveProtectedHandoff") == null, "The destination wins exactly once after the required end boundary");
            }
            foreach (int repeatCase in new[] { 0, 1, 2 })
            foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var group = PhasedGroup(repeatCase == 2 ? "AfterMiddle" : "WholeGroup");
                if (repeatCase > 0)
                {
                    ((IList)Get(group, "FilePaths")).Clear(); ((IList)Get(group, "FilePaths")).Add("same.mp4");
                    if (repeatCase == 1)
                    {
                        Set(group, "FileGroupRandomCount", true); Set(group, "FileGroupMinimumFiles", 7); Set(group, "FileGroupMaximumFiles", 7);
                    }
                    else Set(group, "FileGroupMinimumMiddleFiles", 7);
                }
                Set(group, "Loop", true); var batch = GroupBatch(group);
                Check(batch.Length == (repeatCase == 0 ? 5 : 9), "Repeated media expands into separate playback occurrences rather than a file-count cap");
                Set(batch[0], "BackendFrameId", 100L); Set(batch[1], "BackendFrameId", 101L);
                var target = GroupBatch(media[2])[0];
                QueueState(batch[0], batch[1], batch.Skip(2).Concat(new[] { GroupBatch(media[1])[0] }).ToArray()); SetField("interactiveActive", true);
                var task = (Task)Invoke(owner, method, ProtectedRequest(target));
                Check(!task.IsCompleted && Field(owner, "interactivePending") == batch[1] && QueuedEntries().SequenceEqual(batch.Skip(2).Concat(new[] { target })),
                    "Whole-group protection retains all current middle files and its outro, skipping the old project's later media");
                long frame = 100;
                var remaining = batch.Skip(1).Concat(new[] { target }).ToArray();
                for (int i = 0; i < remaining.Length; i++)
                {
                    Invoke(owner, "ApplyInteractiveOutputStarted", Started(remaining[i], ++frame));
                    Check(Field(owner, "interactiveCurrent") == remaining[i], "Whole-group protection follows each actual file output in order");
                    if (i + 1 == remaining.Length) break;
                    Check(!task.IsCompleted, "Start, every middle and end must finish before releasing a fully protected group");
                    var queuing = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    string command = await CompleteSoon(reader.ReadLineAsync(), "The full protected sequence prequeues its next file");
                    Check(command.StartsWith("PLAY_NEXT " + Get(remaining[i + 1], "Path")), "Whole-group mode never skips a remaining file in the current pass");
                    await writer.WriteLineAsync("OK whole group"); await CompleteSoon(queuing, "Full-group next command completes");
                }
                await CompleteSoon(task, "The full protected group yields only after its outro");
                Check((int)Get(Get(batch[0], "PhaseRun"), "CompletedMiddleFiles") == (repeatCase == 0 ? 3 : 7), "Each repeated occurrence counts exactly once toward middle playback completion");
                Check((bool)Get(Get(batch[0], "GroupLoop"), "Cancelled"), "Full-group loop interruption finishes this pass rather than looping indefinitely");
            }
            foreach (int completed in new[] { 0, 1, 2 })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var group = PhasedGroup(); Set(group, "FileGroupMinimumMiddleFiles", 2); var batch = GroupBatch(group);
                Set(batch[0], "BackendFrameId", 100L); QueueState(batch[0], batch[1]); SetField("interactiveActive", true);
                if (completed > 0)
                {
                    Invoke(owner, "ApplyInteractiveOutputStarted", Started(batch[1], 101));
                    for (int i = 1; i <= completed; i++)
                    {
                        QueueState(batch[i], batch[i + 1]);
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(batch[i + 1], 101 + i));
                    }
                }
                int index = completed == 0 ? 0 : completed + 1;
                var run = Get(batch[0], "PhaseRun");
                Check((int)Get(run, "CompletedMiddleFiles") == completed, "Only completed middle files count toward the default guarantee");
                var oldPending = batch[index + 1]; Set(oldPending, "BackendFrameId", 110L);
                QueueState(batch[index], oldPending, batch.Skip(index + 2).ToArray());
                var target = GroupBatch(media[2])[0];
                var expected = completed == 0 ? new[] { batch[1], batch[2], batch[4], target } : new[] { batch[4], target };
                var task = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(target));
                if (oldPending != expected[0])
                {
                    Check((await CompleteSoon(reader.ReadLineAsync(), "The minimum guarantee replaces unneeded middles")).StartsWith("PLAY_NEXT " + Get(expected[0], "Path")), "Already completed middles are counted rather than replayed");
                    await writer.WriteLineAsync("OK minimum boundary"); await WaitForPhasePendingAsync();
                }
                for (int i = 0; i < expected.Length; i++)
                {
                    Check(Field(owner, "interactivePending") == expected[i], "The protected minimum retains only enough future middles and the outro");
                    long frameId = (long)Get(expected[i], "BackendFrameId");
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(expected[i], frameId > 0 ? frameId : 200 + i)), "Minimum guarantees advance only on the matching output invocation");
                    if (i + 1 == expected.Length) break;
                    Check(!task.IsCompleted, "The default guarantee and outro must finish before the target starts");
                    var queuing = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Minimum protection prequeues its next file (completed=" + completed + ", step=" + i + ")")).StartsWith("PLAY_NEXT " + Get(expected[i + 1], "Path")), "Minimum-middle playback skips only files beyond the required count");
                    await writer.WriteLineAsync("OK minimum next"); await CompleteSoon(queuing, "Minimum next command completes");
                }
                await CompleteSoon(task, "The minimum-middle sequence yields after its outro");
                Check((int)Get(run, "CompletedMiddleFiles") >= 2, "The outro never starts before the configured completed-middle minimum");
            }
            // An outro already queued at the last middle's EOF must be preserved without a second PLAY_NEXT.
            Invoke(owner, "ClearInteractiveRuntime", true);
            var preserved = GroupBatch(PhasedGroup()); var preservedTarget = GroupBatch(media[2])[0];
            Set(preserved[3], "BackendFrameId", 100L); Set(preserved[4], "BackendFrameId", 101L);
            QueueState(preserved[3], preserved[4]); SetField("interactiveActive", true);
            var preserving = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(preservedTarget));
            Check(!preserving.IsCompleted && Field(owner, "interactivePending") == preserved[4] && QueuedEntries().SequenceEqual(new[] { preservedTarget }), "A prefetched outro is preserved while the new project waits");
            Invoke(owner, "ApplyInteractiveOutputStarted", Started(preserved[4], 101));
            var queueTarget = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
            Check((await CompleteSoon(reader.ReadLineAsync(), "The preserved outro prequeues its destination")).StartsWith("PLAY_NEXT " + Get(preservedTarget, "Path")), "Preserving an outro does not restart it");
            await writer.WriteLineAsync("OK preserved target"); await CompleteSoon(queueTarget, "Preserved target is acknowledged");
            Invoke(owner, "ApplyInteractiveOutputStarted", Started(preservedTarget, 102)); await CompleteSoon(preserving, "Preserved outro completes its handoff");

            // A full-group loop may have already prefetched the next pass; only the current pass is protected.
            Invoke(owner, "ClearInteractiveRuntime", true);
            var looping = PhasedGroup("WholeGroup"); Set(looping, "Loop", true); var currentPass = GroupBatch(looping);
            var loop = Get(currentPass[0], "GroupLoop"); Set(loop, "Iteration", 1);
            var futurePass = ((IEnumerable)Invoke(owner, "BuildInteractiveMediaEntries", Get(loop, "Media"), loop)).Cast<object>().ToArray();
            var loopTarget = GroupBatch(media[2])[0]; Set(currentPass[3], "BackendFrameId", 100L); Set(futurePass[0], "BackendFrameId", 101L);
            QueueState(currentPass[3], futurePass[0], futurePass.Skip(1).ToArray()); SetField("interactiveActive", true);
            var yielding = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(loopTarget));
            Check((await CompleteSoon(reader.ReadLineAsync(), "A fully protected loop drops its future pass")).StartsWith("PLAY_NEXT outro1.mp4"), "A prefetched future pass cannot extend full-group protection");
            await writer.WriteLineAsync("OK loop outro"); await WaitForPhasePendingAsync();
            var loopOutro = Field(owner, "interactivePending"); Invoke(owner, "ApplyInteractiveOutputStarted", Started(loopOutro, 102));
            queueTarget = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
            Check((await CompleteSoon(reader.ReadLineAsync(), "The full loop outro queues the destination")).StartsWith("PLAY_NEXT " + Get(loopTarget, "Path")), "The outro hands off directly without any next-pass middle");
            await writer.WriteLineAsync("OK loop target"); await CompleteSoon(queueTarget, "The loop destination is acknowledged");
            Invoke(owner, "ApplyInteractiveOutputStarted", Started(loopTarget, 103)); await CompleteSoon(yielding, "The full loop yields after its current pass and outro");
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); ((IDisposable)client).Dispose(); if (peer != null) peer.Close();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
        }
    }
    private static void CheckPhaseHandoffs()
    {
        var context = SynchronizationContext.Current; var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try { CheckPhaseHandoffsAsync(listener, port).GetAwaiter().GetResult(); }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
        Console.WriteLine("PASS: actual TCP PLAY/PLAY_NEXT for four interruption modes, whole-pass protection, start/middle/end handoffs, skipped middles, preserved outros, prefetched next-pass rejection and actual-output dispatch completion.");
    }
}
