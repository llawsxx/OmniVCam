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
    private static object ComboRoute(object group)
    {
        var route = New("InteractiveRouteItem"); Set(route, "Name", "Same project name"); Set(route, "ComboEnabled", true); Set(route, "RandomOrder", false);
        for (int i = 0; i < 2; i++)
        {
            var combination = New("InteractiveMediaCombination"); ((IList)Get(combination, "MediaIds")).Add(Get(group, "RuntimeId"));
            ((IList)Get(route, "Combinations")).Add(combination);
        }
        return route;
    }
    private static object[] ComboBatch(object route)
    {
        return ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", route)).Cast<object>().ToArray();
    }
    private static void CheckInteractiveCombos()
    {
        var culture = Thread.CurrentThread.CurrentUICulture;
        object oldWindow = Field(owner, "interactiveWindowOpen"), oldProcessing = Field(owner, "processingInteractiveTriggers");
        var group = PhasedGroup(); Set(group, "FileGroupPlayAll", false); media.Add(group);
        try
        {
            var route = ComboRoute(group); var batch = ComboBatch(route);
            QueueState(batch[1], batch[2]); SetField("interactiveActive", true);
            Check(Equals(Get(batch[1], "RouteId"), Get(route, "RuntimeId")), "Actual project identity is stamped independently of media identity and project name");
            SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            int combinationIndex = (int)Get(route, "NextCombinationIndex");
            Invoke(owner, "RequestInteractiveRoute", route, "First hit"); Invoke(owner, "RequestInteractiveRoute", route, "Second hit");
            var queued = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
            Check(queued.Length == 2 && queued.All(request => Get(request, "ComboRun") == Get(batch[1], "PhaseRun")), "Repeated project triggers reserve combo requests without a second full project");
            Check(queued.All(request => ((IList)Get(request, "Entries")).Count == 1) &&
                EntryPaths(queued.Select(request => ((IList)Get(request, "Entries"))[0])).SequenceEqual(new[] { "g2.mp4", "g3.mp4" }), "Each combo reserves one next middle using the file group's selection progress");
            Check((int)Get(route, "NextCombinationIndex") == combinationIndex &&
                (int)Get(Get(group, "Selection"), "NextStartFileIndex") == 1 && (int)Get(Get(group, "Selection"), "NextEndFileIndex") == 1,
                "Combos preserve combination selection and do not advance intro/outro pools");
            Check(Invoke(owner, "BuildInteractiveComboRequest", ComboRoute(group)) == null, "A different project with the same name and media cannot combo into the active project");
            Set(route, "Name", "Renamed while playing");
            Check(Invoke(owner, "BuildInteractiveComboRequest", route) != null, "Renaming a project preserves runtime identity for combos");
            Set(route, "ComboEnabled", false); Check(Invoke(owner, "BuildInteractiveComboRequest", route) == null, "The project combo switch can disable retriggering behavior"); Set(route, "ComboEnabled", true);
            QueueState(batch[0], null); Check(Invoke(owner, "BuildInteractiveComboRequest", route) != null, "Intro playback accepts a combo without rebuilding the project");
            QueueState(batch[2], null); Check(Invoke(owner, "BuildInteractiveComboRequest", route) == null, "Outro playback follows the original project trigger behavior");
            QueueState(batch[1], null); Set(batch[1], "IsGroupDefault", true); Check(Invoke(owner, "BuildInteractiveComboRequest", route) == null, "Group defaults do not become project combos"); Set(batch[1], "IsGroupDefault", false);
            Set(batch[1], "IsGroupTransition", true); Check(Invoke(owner, "BuildInteractiveComboRequest", route) == null, "Group transitions do not become project combos"); Set(batch[1], "IsGroupTransition", false);
            var expired = Invoke(owner, "BuildInteractiveComboRequest", route); QueueState(batch[2], null);
            Check(!((Task<bool>)Invoke(owner, "TryPlayInteractiveComboAsync", expired)).GetAwaiter().GetResult() && Get(expired, "ComboRun") == null &&
                Get(((IList)Get(expired, "Entries"))[0], "Phase").ToString() == "Start", "A combo whose source ended during verification falls back to a normal project trigger");
            var xml = Save(route); var loaded = Load(xml); var copied = StaticCall("CopyInteractiveProject", route);
            Check((bool)Get(loaded, "ComboEnabled") && (bool)Get(copied, "ComboEnabled") && !Equals(Get(copied, "RuntimeId"), Get(route, "RuntimeId")), "Combo settings persist and copied projects receive independent identity");
            xml.Attribute("comboEnabled").Remove(); Check(!(bool)Get(Load(xml), "ComboEnabled"), "Existing projects retain ordinary triggering until combo mode is enabled");
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using (var dialog = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new[] { media, route }))
                {
                    var check = (CheckBox)Field(dialog, "comboEnabledBox"); Check(check.Checked && check.Text != "InteractiveComboEnabled", "The project editor loads its localized combo switch");
                    check.Checked = false; Check((bool)Get(route, "ComboEnabled"), "Unsaved combo edits leave the active project unchanged");
                    var id = Get(route, "RuntimeId"); Invoke(dialog, "Apply", route);
                    Check(!(bool)Get(route, "ComboEnabled") && Equals(id, Get(route, "RuntimeId")), "Editing the combo switch preserves project identity"); Set(route, "ComboEnabled", true);
                }
                foreach (string key in new[] { "InteractiveComboEnabled", "InteractiveComboHint", "InteractiveCombo", "InteractiveComboWaiting", "InteractiveComboPlaying" })
                {
                    string text = (string)StaticCall("T", key);
                    Check(text != key && !string.IsNullOrWhiteSpace(text) && (language == "en" || text.Any(c => c >= '\u4e00' && c <= '\u9fff')), "Combo UI and logs use the existing resx languages");
                }
            }
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); SetField("interactiveWindowOpen", oldWindow); SetField("processingInteractiveTriggers", oldProcessing);
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        CheckInteractiveComboTcp();
        Console.WriteLine("PASS: project combo switch, stable identity, no new combination/intro/outro or OBS lifecycle, protected-start FIFO hits, arbitrary start interruption, default/whole-group middle FIFO hits, immediate middle hits in other modes, repeated filenames, stale lookahead, fallback, persistence and localized editing.");
    }
    private static async Task CheckInteractiveComboTcpAsync(TcpListener listener, int port)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null; SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Combo fixture connects"); var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Combo fixture receives STATUS") == "STATUS", "Combo checks establish an isolated connection");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Combo fixture establishes its generation");
            foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
            foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
            foreach (bool sameFile in new[] { false, true })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var group = PhasedGroup(mode); Set(group, "FileGroupPlayAll", false);
                if (sameFile) { ((IList)Get(group, "FilePaths")).Clear(); ((IList)Get(group, "FilePaths")).Add("same.mp4"); }
                media.Add(group);
                try
                {
                    var route = ComboRoute(group); var batch = ComboBatch(route); var source = batch[1]; var oldOutro = batch[2];
                    var obs = New("InteractiveObsPlayback"); Set(obs, "Started", true); foreach (var entry in batch) Set(entry, "ObsPlayback", obs); SetField("interactiveObsCurrent", obs);
                    Set(source, "BackendFrameId", 100L); Set(oldOutro, "BackendFrameId", 101L);
                    QueueState(source, oldOutro); SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 101L);
                    var request = Invoke(owner, "BuildInteractiveComboRequest", route); var next = ((IList)Get(request, "Entries"))[0];
                    Check(Get(next, "ObsPlayback") == obs && Get(next, "PhaseRun") == Get(source, "PhaseRun"), "Combo middleware keeps the same media and OBS playback invocation");
                    var playing = (Task)Invoke(owner, method, request);
                    string command = await CompleteSoon(reader.ReadLineAsync(), "A combo sends its media command");
                    bool wait = mode == "AfterMiddle" || mode == "WholeGroup";
                    Check(command.StartsWith((wait ? "PLAY_NEXT " : "PLAY ") + Get(next, "Path")), "Default and whole-group combos wait for EOF; other modes switch immediately: " + mode + "/" + method);
                    if (wait) Check(command.Contains("after_frame_id=100"), "Default combos target the output invocation while replacing a prefetched outro");
                    await writer.WriteLineAsync("OK combo");
                    if (!wait)
                    {
                        command = await CompleteSoon(reader.ReadLineAsync(), "Immediate combos restore the reserved outro");
                        Check(command.StartsWith("PLAY_NEXT " + Get(oldOutro, "Path")), "Immediate hits keep exactly one outro after the selected middle"); await writer.WriteLineAsync("OK restored outro");
                    }
                    await CompleteSoon(playing, "The combo is acknowledged without a protected-handoff wait");
                    Check(!(bool)Get(obs, "Ended") && Field(owner, "interactiveObsCurrent") == obs, "Combo hits do not execute OBS end/start actions");
                    Check(!(bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(oldOutro, 101)) && Field(owner, "interactiveCurrent") == (wait ? source : next), "The old prefetched outro cannot falsely complete a combo");
                    if (wait)
                    {
                        var later = new List<object>();
                        for (int i = 0; i < 2; i++)
                        {
                            request = Invoke(owner, "BuildInteractiveComboRequest", route); var extra = ((IList)Get(request, "Entries"))[0]; later.Add(extra);
                            await CompleteSoon((Task)Invoke(owner, method, request), "Consecutive default hits append without replacing the first hit");
                        }
                        Check(Field(owner, "interactivePending") == next && QueuedEntries().Take(2).SequenceEqual(later), "Three hits during one source middle retain FIFO playback order");
                        await CompleteSoon((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(source, "InputSummary")), "Source end awaits actual combo confirmation");
                        Check(Field(owner, "interactiveCurrent") == source, "Source end cannot identify a replacement until output is confirmed");
                        var middles = new[] { next }.Concat(later).ToArray();
                        for (int i = 0; i < middles.Length; i++)
                        {
                            Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(middles[i], 102 + i)), "Each combo occurrence confirms with its own backend frame ID");
                            var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                            command = await CompleteSoon(reader.ReadLineAsync(), "Default combo playback advances its preserved queue");
                            string expected = i + 1 < middles.Length ? (string)Get(middles[i + 1], "Path") : (string)Get(oldOutro, "Path");
                            Check(command.StartsWith("PLAY_NEXT " + expected), "Combos preserve trigger order and restore the outro after the final hit");
                            await writer.WriteLineAsync("OK next hit"); await CompleteSoon(queueNext, "The next combo or outro is acknowledged");
                        }
                        var outro = Field(owner, "interactivePending");
                        Check(outro != oldOutro && Get(Get(source, "PhaseRun"), "Outro") == outro, "Discarded lookahead receives a fresh invocation while preserving the chosen outro");
                        Check(!(bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(oldOutro, 101)), "Late old-outro starts remain rejected after every hit");
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 105));
                        Check((int)Get(Get(source, "PhaseRun"), "CompletedMiddleFiles") == 4, "The source and three completed combo middles each count exactly once");
                    }
                    else
                    {
                        Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(next, 102)), "An immediate hit confirms its actual output");
                        request = Invoke(owner, "BuildInteractiveComboRequest", route); var second = ((IList)Get(request, "Entries"))[0]; playing = (Task)Invoke(owner, method, request);
                        Check((await CompleteSoon(reader.ReadLineAsync(), "A second immediate hit interrupts the first")).StartsWith("PLAY " + Get(second, "Path")), "Consecutive immediate hits follow the latest middle");
                        await writer.WriteLineAsync("OK second hit");
                        Check((await CompleteSoon(reader.ReadLineAsync(), "The second hit retains its outro")).StartsWith("PLAY_NEXT " + Get(oldOutro, "Path")), "Repeated immediate hits do not duplicate the intro or outro");
                        await writer.WriteLineAsync("OK second outro"); await CompleteSoon(playing, "The second immediate hit completes");
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(second, 104));
                        var outro = Field(owner, "interactivePending"); Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 105));
                        Check((int)Get(Get(source, "PhaseRun"), "CompletedMiddleFiles") == 1, "Interrupted middles do not count as completed playback occurrences");
                    }
                    Check(Get(Field(owner, "interactiveCurrent"), "Phase").ToString() == "End" && !(bool)Get(obs, "Ended"), "A combo keeps its original OBS context through the final outro");
                }
                finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); }
            }
            await CheckInteractiveStartCombosAsync(reader, writer);
            await CheckInteractiveComboLoopsAsync(reader, writer);
            await CheckInteractiveDefaultComboHandoffsAsync(reader, writer);
            await CheckInteractiveComboPhaseChangesAsync(reader, writer);
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); ((IDisposable)client).Dispose(); if (peer != null) peer.Close();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
        }
    }
    private static async Task CheckInteractiveStartCombosAsync(StreamReader reader, StreamWriter writer)
    {
        foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
        foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
        foreach (bool sameFile in new[] { false, true })
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var group = PhasedGroup(mode);
            if (sameFile)
            {
                foreach (string pool in new[] { "StartFilePaths", "FilePaths", "EndFilePaths" })
                { ((IList)Get(group, pool)).Clear(); ((IList)Get(group, pool)).Add("same.mp4"); }
            }
            media.Add(group);
            try
            {
                var route = ComboRoute(group); var batch = ComboBatch(route);
                var intro = batch[0]; var oldMiddle = batch[1]; var run = Get(intro, "PhaseRun");
                var obs = New("InteractiveObsPlayback"); Set(obs, "Started", true);
                foreach (var entry in batch) Set(entry, "ObsPlayback", obs);
                SetField("interactiveObsCurrent", obs);
                Set(intro, "BackendFrameId", 100L); Set(oldMiddle, "BackendFrameId", 101L);
                QueueState(intro, oldMiddle, batch.Skip(2).ToArray());
                SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 101L);
                int selection = (int)Get(route, "NextCombinationIndex");
                var request = Invoke(owner, "BuildInteractiveComboRequest", route);
                var first = ((IList)Get(request, "Entries"))[0];
                bool wait = mode != "Any";
                var playing = (Task)Invoke(owner, method, request);
                string command = await CompleteSoon(reader.ReadLineAsync(), "An intro combo sends its media command");
                Check(command.StartsWith((wait ? "PLAY_NEXT " : "PLAY ") + Get(first, "Path")), "Only arbitrary interruption can skip the intro: " + mode + "/" + method);
                if (wait) Check(command.Contains("after_frame_id=100"), "An intro combo targets the current intro invocation");
                await writer.WriteLineAsync("OK intro combo");
                if (!wait)
                {
                    command = await CompleteSoon(reader.ReadLineAsync(), "An arbitrary intro hit restores the original middle");
                    Check(command.StartsWith("PLAY_NEXT " + Get(oldMiddle, "Path")), "An arbitrary intro hit preserves original middles before the outro");
                    await writer.WriteLineAsync("OK original middle");
                }
                await CompleteSoon(playing, "An intro hit is acknowledged without waiting for the whole project");
                var hits = new List<object> { first };
                if (wait)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        request = Invoke(owner, "BuildInteractiveComboRequest", route);
                        hits.Add(((IList)Get(request, "Entries"))[0]);
                        await CompleteSoon((Task)Invoke(owner, method, request), "Repeated intro hits queue behind the first");
                    }
                    Check(Field(owner, "interactiveCurrent") == intro && Field(owner, "interactivePending") == first &&
                        QueuedEntries().Take(2).SequenceEqual(hits.Skip(1)), "Protected intro hits retain FIFO order while the intro finishes");
                    await CompleteSoon((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(intro, "InputSummary")), "Intro completion awaits actual combo output");
                    Check(Field(owner, "interactiveCurrent") == intro, "Intro EOF alone cannot confirm a same-file combo");
                }
                Check((int)Get(route, "NextCombinationIndex") == selection && !(bool)Get(run, "Ending"), "Intro hits retain the original project selection and do not request an outro handoff");
                Check(!(bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(oldMiddle, 101)), "Old prefetched middle output cannot confirm an intro hit");
                var remaining = new[] { Field(owner, "interactivePending") }.Concat(QueuedEntries()).ToArray();
                var expected = wait ? hits.Concat(batch.Skip(1)).ToArray() : batch.Skip(1).ToArray();
                Check(EntryPaths(remaining).SequenceEqual(EntryPaths(expected)), "Intro hits preserve every original middle and exactly one reserved outro");
                if (!wait)
                {
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(first, 102)), "An arbitrary intro hit confirms its selected middle");
                }
                for (int i = 0; i < remaining.Length; i++)
                {
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(remaining[i], 103 + i)), "Every intro-combo successor confirms a fresh output invocation: " + mode + "/" + method + "/same=" + sameFile + "/index=" + i);
                    if (i + 1 == remaining.Length) break;
                    var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    command = await CompleteSoon(reader.ReadLineAsync(), "Intro-combo playback advances its retained queue");
                    Check(command.StartsWith("PLAY_NEXT " + Get(remaining[i + 1], "Path")), "Intro hits play in order before the original remaining middles and outro");
                    await writer.WriteLineAsync("OK intro successor"); await CompleteSoon(queueNext, "The intro-combo successor is acknowledged");
                }
                Check((int)Get(run, "CompletedMiddleFiles") == batch.Length - 2 + hits.Count, "Intro hits add one completed middle each without counting the intro");
                Check(Get(Field(owner, "interactiveCurrent"), "Phase").ToString() == "End" &&
                    Field(owner, "interactiveObsCurrent") == obs && !(bool)Get(obs, "Ended"), "Intro combos keep the same OBS lifecycle through the reserved outro");
            }
            finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); }
        }
    }
    private static async Task CheckInteractiveComboLoopsAsync(StreamReader reader, StreamWriter writer)
    {
        int cases = 0;
        foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
        foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
        foreach (string boundaries in new[] { "Both", "StartOnly", "EndOnly" })
        foreach (bool prefetched in new[] { false, true })
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var group = PhasedGroup(mode); Set(group, "Loop", true);
            Set(group, "FileGroupRandomCount", true); Set(group, "FileGroupMinimumFiles", 4); Set(group, "FileGroupMaximumFiles", 5);
            Set(group, "FileGroupRandom", true); Set(group, "FileGroupNoRepeat", true);
            if (mode == "AfterMiddle") Set(group, "FileGroupMinimumMiddleFiles", 7);
            if (boundaries == "StartOnly") ((IList)Get(group, "EndFilePaths")).Clear();
            if (boundaries == "EndOnly") ((IList)Get(group, "StartFilePaths")).Clear();
            media.Add(group);
            string label = mode + "/" + method + "/" + boundaries + "/prefetched=" + prefetched;
            try
            {
                var route = ComboRoute(group); var batch = ComboBatch(route);
                var source = batch.Last(e => Get(e, "Phase").ToString() == "Middle");
                var run = Get(source, "PhaseRun"); var loop = Get(source, "GroupLoop");
                var tail = batch.SkipWhile(e => e != source).Skip(1).ToArray();
                Set(source, "BackendFrameId", 100L); QueueState(source, null, tail);
                SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 100L);
                if (prefetched)
                {
                    var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Loop fixture prefetches the next pass")).StartsWith("PLAY_NEXT "), "Loop prefetch uses PLAY_NEXT");
                    await writer.WriteLineAsync("OK prefetched pass"); await CompleteSoon(queueNext, "Loop fixture acknowledges next-pass prefetch");
                    var pending = Field(owner, "interactivePending");
                    Check(Get(pending, "Phase").ToString() == "Middle" && (int)Get(pending, "GroupIteration") == 1, "The loop reserves the next middle pass without replaying its start");
                    Set(pending, "BackendFrameId", 101L); SetField("interactiveObservedBackendFrameId", 101L);
                }
                var request = Invoke(owner, "BuildInteractiveComboRequest", route); var hit = ((IList)Get(request, "Entries"))[0];
                Check((int)Get(hit, "GroupIteration") == (int)Get(source, "GroupIteration"), "A combo belongs to the playing pass even when the next pass was prefetched: " + label);
                bool wait = mode == "AfterMiddle" || mode == "WholeGroup";
                var playing = (Task)Invoke(owner, method, request);
                Check((await CompleteSoon(reader.ReadLineAsync(), "A last-middle combo sends its command")).StartsWith((wait ? "PLAY_NEXT " : "PLAY ") + Get(hit, "Path")), "Last-middle combos follow the interruption policy: " + label);
                await writer.WriteLineAsync("OK last-middle hit");
                if (!wait)
                {
                    Check((await CompleteSoon(reader.ReadLineAsync(), "An immediate last-middle hit restores loop prefetch")).StartsWith("PLAY_NEXT "), "An immediate loop hit retains lookahead");
                    await writer.WriteLineAsync("OK restored loop");
                }
                await CompleteSoon(playing, "The last-middle hit is acknowledged");
                long frame = 102;
                if (!wait) Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(hit, frame++)), "The immediate last-middle hit confirms its own output");
                int maxPass = 0;
                for (int i = 0; i < 18; i++)
                {
                    var pending = Field(owner, "interactivePending");
                    Check(pending != null && Get(pending, "Phase").ToString() == "Middle", "A combo at the final middle must keep looping instead of playing the outro or stopping: " + label);
                    Check(Get(pending, "PhaseRun") == run && Equals(Get(pending, "RouteId"), Get(route, "RuntimeId")), "Loop continuations retain the same run and project identity after combos");
                    maxPass = Math.Max(maxPass, (int)Get(pending, "GroupIteration"));
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(pending, frame++)), "Looped middle occurrences confirm distinct output invocations after combos");
                    var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Combo loop prefetch continues")).StartsWith("PLAY_NEXT "), "Loop continuation never restarts the project");
                    await writer.WriteLineAsync("OK continued pass"); await CompleteSoon(queueNext, "The next combo-loop middle is acknowledged");
                }
                Check(maxPass >= 2 && !(bool)Get(run, "Ending") && !(bool)Get(loop, "Cancelled"), "Last-middle combos preserve ongoing looping across multiple passes: " + label);
                cases++;
            }
            finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); }
        }
        Console.WriteLine("PASS: " + cases + " loop/combo boundary cases across all policies, both trigger modes, optional start/end pools, next-pass lookahead, variable counts and minimum-count padding.");
    }
    private static async Task CheckInteractiveDefaultComboHandoffsAsync(StreamReader reader, StreamWriter writer)
    {
        int cases = 0;
        foreach (string phase in new[] { "Start", "Middle" })
        foreach (int minimum in new[] { 0, 3, 7 })
        foreach (bool looping in new[] { false, true })
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var group = PhasedGroup(); Set(group, "FileGroupPlayAll", false);
            Set(group, "FileGroupMinimumMiddleFiles", minimum); Set(group, "Loop", looping); media.Add(group);
            string label = phase + "/minimum=" + minimum + "/loop=" + looping;
            try
            {
                var route = ComboRoute(group); var batch = ComboBatch(route); int index = phase == "Start" ? 0 : 1;
                var current = batch[index]; var run = Get(current, "PhaseRun");
                int completed = phase == "Middle" ? 2 : 0; Set(run, "CompletedMiddleFiles", completed);
                Set(current, "BackendFrameId", 100L); Set(batch[index + 1], "BackendFrameId", 101L);
                QueueState(current, batch[index + 1], batch.Skip(index + 2).ToArray());
                SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 101L);
                for (int hit = 0; hit < 2; hit++)
                {
                    var combo = Invoke(owner, "BuildInteractiveComboRequest", route);
                    var playing = (Task)Invoke(owner, "AppendInteractiveRouteAsync", combo);
                    if (hit == 0)
                    {
                        Check((await CompleteSoon(reader.ReadLineAsync(), "A default PLAY_NEXT combo reserves one middle")).StartsWith("PLAY_NEXT "), "A default PLAY_NEXT combo waits for the current file");
                        await writer.WriteLineAsync("OK reserved hit");
                    }
                    await CompleteSoon(playing, "Default PLAY_NEXT hits queue without a protected project handoff");
                }
                int needed = Math.Max(0, minimum - completed - (phase == "Middle" ? 1 : 0));
                var available = new[] { Field(owner, "interactivePending") }.Concat(QueuedEntries()).ToArray();
                var expected = available.Where(e => Get(e, "Phase").ToString() == "Middle").Take(needed).ToList();
                Check(expected.Count == needed, "The initial selection plus combo hits can meet the requested middle minimum");
                expected.Add(Get(run, "Outro"));
                var target = GroupBatch(media[2])[0]; expected.Add(target);
                var request = ProtectedRequest(target); Set(request, "Immediate", false);
                var switching = (Task)Invoke(owner, "AppendInteractiveRouteAsync", request);
                if (needed == 0)
                {
                    string command = await CompleteSoon(reader.ReadLineAsync(), "Default PLAY_NEXT skips surplus middles and queues the end");
                    Check(command.StartsWith("PLAY_NEXT " + Get(Get(run, "Outro"), "Path")), "Default PLAY_NEXT cannot skip the reserved end: " + label);
                    await writer.WriteLineAsync("OK reserved end"); await WaitForPhasePendingAsync();
                }
                Check(!switching.IsCompleted && Field(owner, "interactiveCurrent") == current, "A new PLAY_NEXT project cannot interrupt the current protected file: " + label);
                var remaining = new[] { Field(owner, "interactivePending") }.Concat(QueuedEntries()).ToArray();
                Check(remaining.SequenceEqual(expected), "Default PLAY_NEXT retains only required middles, then one end, then the new project: " + label);
                long frame = 102;
                for (int i = 0; i < remaining.Length; i++)
                {
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(remaining[i], frame++)), "The default combo handoff confirms its next actual output");
                    if (i == remaining.Length - 1) break;
                    Check(!switching.IsCompleted, "The target remains waiting until its own output starts");
                    var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Default combo handoff queues its next boundary")).StartsWith("PLAY_NEXT " + Get(remaining[i + 1], "Path")), "Default combo handoff follows required middles, end and target order");
                    await writer.WriteLineAsync("OK handoff boundary"); await CompleteSoon(queueNext, "The next handoff entry is acknowledged");
                }
                await CompleteSoon(switching, "The PLAY_NEXT handoff completes on actual target output");
                Check(Field(owner, "interactiveCurrent") == target && (int)Get(run, "CompletedMiddleFiles") == completed + (phase == "Middle" ? 1 : 0) + needed,
                    "Default PLAY_NEXT counts completed original/combo middles once, honors the minimum, and starts the destination: " + label);
                Check(QueuedEntries().Length == 0 && (Get(run, "Loop") == null || (bool)Get(Get(run, "Loop"), "Cancelled")), "Default PLAY_NEXT discards old surplus middles and stops old looping");
                cases++;
            }
            finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); }
        }
        Console.WriteLine("PASS: " + cases + " default-mode PLAY_NEXT handoffs after consecutive combo hits, across start/middle, zero/partial/large guarantees, completed-file counts and loop yielding.");
    }
    private static async Task CheckInteractiveComboPhaseChangesAsync(StreamReader reader, StreamWriter writer)
    {
        int cases = 0;
        foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
        foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
        foreach (bool nextPass in new[] { false, true })
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var group = PhasedGroup(mode); Set(group, "Loop", true); Set(group, "FileGroupPlayAll", false); media.Add(group);
            string label = mode + "/" + method + "/nextPass=" + nextPass;
            try
            {
                var route = ComboRoute(group); var batch = ComboBatch(route);
                var original = nextPass ? batch[1] : batch[0]; var run = Get(original, "PhaseRun"); var loop = Get(original, "GroupLoop");
                QueueState(original, null); SetField("interactiveActive", true);
                var request = Invoke(owner, "BuildInteractiveComboRequest", route); var hit = ((IList)Get(request, "Entries"))[0];
                if (nextPass)
                {
                    Set(loop, "Iteration", 1);
                    batch = ((IEnumerable)Invoke(owner, "BuildInteractiveMediaEntries", Get(loop, "Media"), loop)).Cast<object>().ToArray();
                }
                var current = batch.First(e => Get(e, "Phase").ToString() == "Middle");
                Set(current, "BackendFrameId", 200L); QueueState(current, null, batch.SkipWhile(e => e != current).Skip(1).ToArray());
                SetField("interactiveObservedBackendFrameId", 200L);
                bool wait = mode == "AfterMiddle" || mode == "WholeGroup";
                var playing = (Task)Invoke(owner, method, request);
                Check((await CompleteSoon(reader.ReadLineAsync(), "A reserved combo dispatches after playback advanced")).StartsWith((wait ? "PLAY_NEXT " : "PLAY ") + Get(hit, "Path")), "Combo dispatch applies the actual current phase's interruption rule: " + label);
                await writer.WriteLineAsync("OK phase-change hit");
                if (!wait)
                {
                    Check((await CompleteSoon(reader.ReadLineAsync(), "An immediate phase-change hit reserves the following middle")).StartsWith("PLAY_NEXT "), "The loop continues after a phase-change hit");
                    await writer.WriteLineAsync("OK phase-change lookahead");
                }
                await CompleteSoon(playing, "A combo survives a start-to-middle or pass change");
                Check((int)Get(hit, "GroupIteration") == (int)Get(current, "GroupIteration"), "A delayed combo must join the actual current pass, rather than its stale reservation pass: " + label);
                long frame = 202;
                if (!wait) Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(hit, frame++)), "A delayed immediate combo confirms its own invocation");
                for (int i = 0; i < 3; i++)
                {
                    var pending = Field(owner, "interactivePending");
                    Check(pending != null && Get(pending, "Phase").ToString() == "Middle", "A delayed combo cannot cause an early outro or stall the loop: " + label);
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(pending, frame++)), "A delayed combo and subsequent loop middles verify fresh frame IDs");
                    var queueNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                    Check((await CompleteSoon(reader.ReadLineAsync(), "Delayed combo looping continues")).StartsWith("PLAY_NEXT "), "A delayed combo keeps loop continuation");
                    await writer.WriteLineAsync("OK delayed loop"); await CompleteSoon(queueNext, "Delayed combo loop command is acknowledged");
                }
                Check(!(bool)Get(run, "Ending") && !(bool)Get(loop, "Cancelled"), "Phase changes preserve the same active looping run");
                cases++;
            }
            finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(group); }
        }
        Console.WriteLine("PASS: " + cases + " accepted combo requests survive start-to-middle and loop-pass changes using actual-phase policies, current-pass identity and continued looping.");
    }
    private static void CheckInteractiveComboTcp()
    {
        var context = SynchronizationContext.Current; var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try { CheckInteractiveComboTcpAsync(listener, port).GetAwaiter().GetResult(); }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
    }
}
