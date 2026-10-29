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
    private static object DefaultItem(object group, object library, string mode, bool loop = true)
    {
        var item = New("InteractiveDefaultPlaybackItem");
        Set(item, "MediaId", Get(library, "RuntimeId")); Set(item, "Name", "Default " + mode);
        Set(item, "LoopMiddleFiles", loop);
        if (mode != null) Set(item, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), mode));
        ((IList)Get(group, "DefaultPlaybacks")).Add(item);
        return item;
    }

    private static object DefaultHotkey(Keys key)
    {
        var hotkey = New("InteractiveHotkey"); Set(hotkey, "KeyCode", key);
        Set(hotkey, "Modifiers", Enum.ToObject(Nested("HotkeyModifiers"), 7)); return hotkey;
    }

    private static void CheckInteractiveDefaultPlaybacks(Assembly assembly)
    {
        string[] savedFields = { "interactiveProjectGroups", "activeInteractiveProjectGroupId", "interactiveRoutes",
            "interactiveGroupSwitchHotkey", "interactiveWindowOpen", "interactiveHotkeysDisabled", "processingInteractiveTriggers",
            "interactiveForm", "connectionStates", "hostBox", "portBox", "currentStatusState" };
        var saved = savedFields.ToDictionary(name => name, name => Field(owner, name));
        var culture = Thread.CurrentThread.CurrentUICulture;
        var files = Enumerable.Range(0, 7).Select(i => Path.GetTempFileName()).ToArray();
        var library = PhasedGroup("WholeGroup"); Set(library, "Title", "Multi-default source");
        foreach (string name in new[] { "FilePaths", "StartFilePaths", "EndFilePaths" }) ((IList)Get(library, name)).Clear();
        for (int i = 0; i < 3; i++) ((IList)Get(library, "FilePaths")).Add(files[i]);
        ((IList)Get(library, "StartFilePaths")).Add(files[3]); ((IList)Get(library, "EndFilePaths")).Add(files[4]);
        var plain = FileGroup(); Set(plain, "PreventInterrupt", true); Set(plain, "FileGroupPlayAll", true);
        ((IList)Get(plain, "FilePaths")).Clear(); for (int i = 0; i < 3; i++) ((IList)Get(plain, "FilePaths")).Add(files[i]);
        var single = New("InteractiveMediaItem"); Set(single, "Path", files[5]); Set(single, "PreventInterrupt", true);
        media.Add(library); media.Add(plain); media.Add(single);
        var group = New("InteractiveProjectGroup"); Set(group, "Name", "Defaults"); Set(group, "OverlapEnabled", false);
        var groups = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveProjectGroup"))); groups.Add(group);
        SetField("interactiveProjectGroups", groups); SetField("activeInteractiveProjectGroupId", Get(group, "Id"));
        SetField("interactiveRoutes", Get(group, "Routes")); SetField("interactiveGroupSwitchHotkey", null);
        SetField("interactiveForm", null); SetField("interactiveHotkeysDisabled", false);
        SetField("connectionStates", Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("ConnectionTabState"))));
        try
        {
            var source = DefaultItem(group, library, "Any"); var target = DefaultItem(group, single, "WholeGroup", false);
            var third = DefaultItem(group, plain, "AfterMiddle");
            Set(source, "Hotkey", DefaultHotkey(Keys.F21)); Set(target, "Hotkey", DefaultHotkey(Keys.F22));
            Set(group, "DefaultSwitchHotkey", DefaultHotkey(Keys.F24)); Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var project = ProtectedRequest(GroupBatch(single)[0]);
            var change = ProtectedRequest(GroupBatch(single)[0]); Set(change, "IsDefaultPlaybackSwitch", true);
            foreach (object item in new[] { source, third, target })
            foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
            {
                Set(item, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), mode));
                Set(group, "ActiveDefaultPlaybackId", Get(item, "Id")); var batch = DefaultBatch(group);
                Check(batch.Length > 0 && batch.All(entry => (Guid)Get(entry, "DefaultPlaybackId") == (Guid)Get(item, "Id")), "Defaults preserve their identities across every phase");
                foreach (var entry in batch)
                {
                    bool protectedSwitch = mode != "Any" && (Get(entry, "PhaseRun") != null || mode != "ImmediateMiddle");
                    Check((bool)StaticCall("PreventsInteractiveInterruption", entry, change) == protectedSwitch, "Switch overrides apply to phased, plain and single-file defaults: " + mode);
                    Check((bool)StaticCall("PreventsInteractiveInterruption", entry, project), "Interactive projects retain the protected library policy regardless of default override");
                }
                Check(Get(library, "FileGroupInterruptionMode").ToString() == "WholeGroup" && (bool)Get(plain, "PreventInterrupt"), "Default overrides do not mutate library settings");
            }
            Set(source, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), "Any"));
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var initial = DefaultBatch(group); var loop = Get(initial[0], "GroupLoop");
            QueueState(initial[3], null, initial[4]); Invoke(owner, "QueueInteractivePhasedContinuation");
            var continuation = QueuedEntries();
            Check(continuation[0] != initial[0] && Get(continuation[0], "Phase").ToString() == "Middle" && continuation.Last() == initial[4], "Continuous middle looping retains one start and the reserved end");
            Check(continuation.All(entry => Equals(Get(entry, "DefaultPlaybackId"), Get(source, "Id"))), "Loop continuation retains default identity");
            Set(source, "LoopMiddleFiles", false); var finite = DefaultBatch(group);
            Check(Get(finite[0], "GroupLoop") == null && finite.Length == 5 && Get(finite.Last(), "Phase").ToString() == "End", "Disabling middle loops yields a finite start-middle-end pass for automatic replay");
            var singleBatch = ((IEnumerable)Invoke(owner, "BuildInteractiveDefaultPlayback", group, target, false)).Cast<object>().ToArray();
            Check((bool)Get(singleBatch[0], "Loop"), "Single-file defaults always loop");
            Set(source, "LoopMiddleFiles", true);
            for (int i = 0; i < 6; i++)
            {
                var next = Invoke(owner, "SelectNextInteractiveDefault");
                Check(next == new[] { target, third, source }[i % 3], "Sequential default switching wraps in display order");
                Set(group, "ActiveDefaultPlaybackId", Get(next, "Id"));
            }
            Set(group, "DefaultSwitchRandom", true); var seen = new HashSet<Guid>();
            for (int i = 0; i < 100; i++)
            {
                var next = Invoke(owner, "SelectNextInteractiveDefault");
                Check(!Equals(Get(next, "Id"), Get(group, "ActiveDefaultPlaybackId")), "Random default switching excludes current selection");
                seen.Add((Guid)Get(next, "Id")); Set(group, "ActiveDefaultPlaybackId", Get(next, "Id"));
            }
            Check(seen.Count == 3, "Random switching reaches all configured defaults");
            var xml = (XElement)StaticCall("SaveInteractiveDefaults", group); var restored = New("InteractiveProjectGroup");
            StaticCall("LoadInteractiveDefaults", new XElement("Group", xml), restored);
            Check(((XElement)StaticCall("SaveInteractiveDefaults", restored)).ToString() == xml.ToString(), "All default identities, selection, loop, policies and shortcuts survive XML");
            var cloned = New("InteractiveProjectGroup"); StaticCall("CopyInteractiveDefaults", group, cloned);
            Set(((IList)Get(cloned, "DefaultPlaybacks"))[0], "Name", "Detached edit");
            Set(Get(((IList)Get(cloned, "DefaultPlaybacks"))[0], "Hotkey"), "KeyCode", Keys.F20);
            Check((string)Get(source, "Name") != "Detached edit" && (Keys)Get(Get(source, "Hotkey"), "KeyCode") == Keys.F21, "Default item and shortcut edits are deeply isolated");
            var malformed = new XElement(xml); malformed.SetAttributeValue("active", Guid.NewGuid());
            malformed.Elements("Default").First().SetAttributeValue("interruption", "999");
            malformed.Elements("Default").Last().SetAttributeValue("id", malformed.Elements("Default").First().Attribute("id").Value);
            StaticCall("LoadInteractiveDefaults", new XElement("Group", malformed), restored);
            var loaded = ((IList)Get(restored, "DefaultPlaybacks")).Cast<object>().ToArray();
            Check(loaded.Select(item => Get(item, "Id")).Distinct().Count() == 3 && Get(loaded[0], "InterruptionMode") == null && Equals(Get(restored, "ActiveDefaultPlaybackId"), Get(loaded[0], "Id")), "Malformed policies and duplicate/absent active identities fall back safely");
            var legacy = New("InteractiveProjectGroup"); Set(legacy, "DefaultMediaId", Get(single, "RuntimeId"));
            StaticCall("LoadInteractiveDefaults", new XElement("Group"), legacy);
            Check(((IList)Get(legacy, "DefaultPlaybacks")).Count == 1 && (bool)Get(((IList)Get(legacy, "DefaultPlaybacks"))[0], "LoopMiddleFiles"), "Legacy single defaults migrate to one looping item");
            CheckDefaultPlaybackEditors(assembly, group, source, target);
            CheckDefaultPlaybackDispatch(group, source, target, third);
            CheckDefaultPlaybackTcp(group, source, target, library, plain, single);
            CheckDefaultPlaybackHotkeys(group, source, target);
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            foreach (var value in saved) SetField(value.Key, value.Value);
            media.Remove(library); media.Remove(plain); media.Remove(single);
            foreach (string file in files) File.Delete(file);
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        Console.WriteLine("PASS: multiple defaults, persisted selection/shortcuts, independent switch-only interruption overrides, media snapshots, finite/full versus middle loops, editor cancellation, default buttons, random/ordered cycling and hotkey lifecycle.");
    }

    private static void CheckDefaultPlaybackEditors(Assembly assembly, object group, object source, object target)
    {
        var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
        foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            foreach (string key in new[] { "InteractiveDefaultPlaybacks", "InteractiveDefaultSettings", "InteractiveDefaultSwitch", "InteractiveDefaultSwitchInterruption", "InteractiveDefaultItemHint", "InteractiveDefaultListHint" })
                Check(!string.IsNullOrWhiteSpace(resources.GetString(key, CultureInfo.GetCultureInfo(language))), "Default UI is localized: " + language + "/" + key);
            using (var dialog = (Form)Activator.CreateInstance(Nested("DefaultPlaybackEditDialog"), new object[] { media, source }))
            {
                ((TextBox)Field(dialog, "nameBox")).Text = "Changed";
                ((CheckBox)Field(dialog, "loopMiddleBox")).Checked = false;
                ((ComboBox)Field(dialog, "interruptionBox")).SelectedIndex = 2;
                var clone = StaticCall("CopyInteractiveDefault", source); Invoke(dialog, "Apply", clone);
                Check((string)Get(clone, "Name") == "Changed" && !(bool)Get(clone, "LoopMiddleFiles") && Get(clone, "InterruptionMode").ToString() == "Any" && (string)Get(source, "Name") != "Changed", "Item editor applies only to its detached target");
                dialog.Size = dialog.MinimumSize; var handle = dialog.Handle; dialog.PerformLayout();
                Check(((ComboBox)Field(dialog, "mediaBox")).Width > 200, "Default media picker remains usable at minimum size: " + language);
            }
        }
        using (var dialog = (Form)Activator.CreateInstance(Nested("DefaultPlaybackSettingsDialog"), new object[] { media, group }))
        {
            var shadow = Field(dialog, "settings"); ((IList)Get(shadow, "DefaultPlaybacks")).RemoveAt(0);
            Check(((IList)Get(group, "DefaultPlaybacks")).Count == 3, "Cancelling default list edits preserves live configuration");
            var edited = New("InteractiveProjectGroup"); Invoke(dialog, "Apply", edited);
            Check(((IList)Get(edited, "DefaultPlaybacks")).Count == 2, "Default list edits can be applied as a unit");
            Set(target, "Hotkey", DefaultHotkey(Keys.F21));
            Check((bool)Invoke(owner, "InteractiveDefaultHotkeysConflict", group, group), "Duplicate default shortcuts are rejected");
            Set(target, "Hotkey", DefaultHotkey(Keys.F22));
        }
    }

    private static void CheckDefaultPlaybackDispatch(object group, object source, object target, object third)
    {
        Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
        Set(group, "ActiveDefaultPlaybackId", Get(source, "Id")); var current = DefaultBatch(group)[1];
        QueueState(current, null); SetField("interactiveActive", true);
        Invoke(owner, "SwitchInteractiveDefault", target, "test");
        Check(WaitingTriggerCount() == 1 && Equals(Get(group, "ActiveDefaultPlaybackId"), Get(target, "Id")), "A direct default selection queues a handoff and updates the idle destination");
        Invoke(owner, "SwitchInteractiveDefault", target, "duplicate"); Check(WaitingTriggerCount() == 1, "Repeated waiting default clicks are coalesced");
        Invoke(owner, "SwitchInteractiveDefault", third, "latest");
        Check(WaitingTriggerCount() == 1 && Equals(Get(group, "ActiveDefaultPlaybackId"), Get(third, "Id")), "The latest waiting default selection replaces the previous default request");
        var requests = (IEnumerable)Field(owner, "interactiveTriggerRequests"); var request = requests.Cast<object>().Single();
        Check((bool)Get(request, "IsDefaultPlaybackSwitch") && !(bool)Invoke(owner, "IsStaleInteractiveGroupRequest", request), "Explicit switches carry a separate handoff-policy scope");
        SetField("interactiveProcessingTrigger", request); Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
        Invoke(owner, "SwitchInteractiveDefault", third, "duplicate while verifying");
        Check(WaitingTriggerCount() == 0, "A repeated selection cannot duplicate a default request already verifying output");
        SetField("interactiveProcessingTrigger", null);
        Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
        Check((bool)Invoke(owner, "IsStaleInteractiveGroupRequest", request), "Obsolete selected-default requests cannot start after a newer selection");
        Invoke(owner, "ClearInteractiveRuntime", true);
        var interactive = GroupBatch(media[0])[0]; Set(interactive, "Loop", false);
        var nextProjectFile = GroupBatch(media[1])[0];
        QueueState(interactive, nextProjectFile); SetField("interactiveActive", true);
        Invoke(owner, "SwitchInteractiveDefault", target, "during project");
        Check(WaitingTriggerCount() == 0 && Field(owner, "interactiveCurrent") == interactive && Field(owner, "interactivePending") == nextProjectFile,
            "Selecting a default during a project leaves every project file intact and queues no interrupting trigger");
        var deferred = ProtectedRequest(DefaultBatch(group)[0]); Set(deferred, "IsDefaultPlaybackSwitch", true);
        ((Task)Invoke(owner, "StartInteractiveImmediatelyAsync", deferred)).GetAwaiter().GetResult();
        Check(Field(owner, "interactiveCurrent") == interactive && Field(owner, "interactivePending") == nextProjectFile,
            "A default request rechecks for an interactive project when it reaches dispatch");
        QueueState(interactive, null); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
        Check(QueuedEntries().All(entry => Equals(Get(entry, "DefaultPlaybackId"), Get(target, "Id"))), "Project completion resolves the newly selected default");
        QueueState(interactive, null); Set(interactive, "Loop", true);
        Invoke(owner, "SwitchInteractiveDefault", third, "during looping project"); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
        Check(WaitingTriggerCount() == 0 && QueuedEntries().Length == 0 && (bool)Get(interactive, "Loop"), "A default selection cannot terminate an interactive loop");
        Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false);
    }

    private static void CheckDefaultPlaybackTcp(object group, object source, object target, object library, object plain, object single)
    {
        var context = SynchronizationContext.Current; var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try { CheckDefaultPlaybackTcpAsync(listener, port, group, source, target, library, plain, single).GetAwaiter().GetResult(); }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
    }

    private static async Task CheckDefaultPlaybackTcpAsync(TcpListener listener, int port, object group, object source, object target, object library, object plain, object single)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null; SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Default fixture connects");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Default initial STATUS") == "STATUS", "Default fixture uses isolated TCP");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Default fixture generation initialized");
            foreach (object lib in new[] { library, plain, single })
            foreach (string mode in new[] { "AfterMiddle", "Any", "ImmediateMiddle", "WholeGroup" })
            foreach (bool switching in new[] { true, false })
            foreach (string phase in lib == library ? new[] { "Start", "Middle", "End" } : new[] { "Middle" })
            {
                Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveNextQueueTask", null);
                string baseMode = mode == "WholeGroup" ? "Any" : "WholeGroup";
                Set(lib, "PreventInterrupt", baseMode != "Any");
                Set(lib, "FileGroupInterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), baseMode));
                Set(source, "MediaId", Get(lib, "RuntimeId")); Set(source, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), mode));
                Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
                var batch = DefaultBatch(group); int index = phase == "Start" ? 0 : phase == "End" ? batch.Length - 1 : lib == library ? 1 : 0;
                var current = batch[index]; object pending = index + 1 < batch.Length ? batch[index + 1] : null;
                Set(current, "BackendFrameId", 100L); if (pending != null) Set(pending, "BackendFrameId", 101L);
                QueueState(current, pending, batch.Skip(index + 2).ToArray()); SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 101L);
                var destination = ((IEnumerable)Invoke(owner, "BuildInteractiveDefaultPlayback", group, target, false)).Cast<object>().Single();
                var request = ProtectedRequest(destination); Set(request, "IsDefaultPlaybackSwitch", switching);
                string effective = switching ? mode : baseMode;
                if (lib == single && effective == "ImmediateMiddle") effective = "Any";
                var remaining = new List<object>();
                if (effective == "WholeGroup") remaining.AddRange(batch.Skip(index + 1));
                else if (effective != "Any" && lib == library && phase != "End") remaining.Add(batch.Last());
                remaining.Add(destination);
                bool immediate = effective == "Any" || effective == "ImmediateMiddle" && phase == "Middle";
                string label = (lib == library ? "phased" : lib == plain ? "plain" : "single") + "/" + mode + "/" + phase + "/" + (switching ? "default" : "project");
                var task = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", request);
                if (immediate)
                {
                    string command = await CompleteSoon(reader.ReadLineAsync(), label + " immediate command");
                    Check(command.StartsWith("PLAY " + Get(remaining[0], "Path")), label + " uses immediate replacement"); await writer.WriteLineAsync("OK immediate");
                    if (remaining.Count == 1)
                    {
                        if (effective != "Any") Invoke(owner, "ApplyInteractiveOutputStarted", Started(destination, 102L));
                        await CompleteSoon(task, label + " immediate completion");
                        Check(Field(owner, "interactiveCurrent") == destination, label + " starts destination immediately"); continue;
                    }
                    string next = await CompleteSoon(reader.ReadLineAsync(), label + " outro successor");
                    Check(next.StartsWith("PLAY_NEXT " + Get(destination, "Path")), label + " preserves the outro before destination"); await writer.WriteLineAsync("OK next");
                    await WaitForPhasePendingAsync(); Invoke(owner, "ApplyInteractiveOutputStarted", Started(remaining[0], 102L));
                    Invoke(owner, "ApplyInteractiveOutputStarted", Started(destination, 103L)); await CompleteSoon(task, label + " protected completion"); continue;
                }
                long frame = 102;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var entry = remaining[i];
                    Task queuing = i > 0 ? (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync") : null;
                    if (!(i == 0 && pending == entry))
                    {
                        string command = await CompleteSoon(reader.ReadLineAsync(), label + " queued boundary " + i);
                        Check(command.StartsWith("PLAY_NEXT " + Get(entry, "Path")), label + " waits for the correct library/default boundary " + i);
                        await writer.WriteLineAsync("OK queued");
                    }
                    if (queuing != null) await CompleteSoon(queuing, label + " queue acknowledged");
                    if (i == 0 && pending == entry) await Task.Delay(1); else await WaitForPhasePendingAsync();
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(entry, (long)Get(entry, "BackendFrameId") > 0 ? (long)Get(entry, "BackendFrameId") : frame++)), label + " accepts the expected output invocation " + i);
                }
                await CompleteSoon(task, label + " actual output completes handoff");
                Check(Field(owner, "interactiveCurrent") == destination && Field(owner, "interactiveProtectedHandoff") == null, label + " starts once after the required tail");
            }

            // A project superseding a waiting default switch restores the library's full protected tail.
            Invoke(owner, "ClearInteractiveRuntime", true);
            Set(source, "MediaId", Get(library, "RuntimeId")); Set(source, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), "AfterMiddle"));
            Set(library, "FileGroupInterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), "WholeGroup"));
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var protectedBatch = DefaultBatch(group); Set(protectedBatch[0], "BackendFrameId", 100L); Set(protectedBatch[1], "BackendFrameId", 101L);
            QueueState(protectedBatch[0], protectedBatch[1], protectedBatch.Skip(2).ToArray()); SetField("interactiveActive", true);
            var nextDefault = ((IEnumerable)Invoke(owner, "BuildInteractiveDefaultPlayback", group, target, false)).Cast<object>().Single();
            var defaultRequest = ProtectedRequest(nextDefault); Set(defaultRequest, "IsDefaultPlaybackSwitch", true);
            var firstSwitch = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", defaultRequest);
            Check((await CompleteSoon(reader.ReadLineAsync(), "Waiting default reserves the outro")).StartsWith("PLAY_NEXT " + Get(protectedBatch.Last(), "Path")), "A default switch can omit the remaining middles");
            await writer.WriteLineAsync("OK default outro"); await WaitForPhasePendingAsync();
            Invoke(owner, "CancelInteractiveProtectedHandoff"); await CompleteSoon(firstSwitch, "A real project supersedes the waiting default");
            var projectRequest = ProtectedRequest(nextDefault);
            var realTrigger = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", projectRequest);
            var expectedTail = protectedBatch.Skip(1).Concat(new[] { nextDefault }).ToArray();
            long restoredFrame = 200;
            for (int i = 0; i < expectedTail.Length; i++)
            {
                Task queuing = i == 0 ? null : (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                string command = await CompleteSoon(reader.ReadLineAsync(), "Restored library tail " + i);
                Check(command.StartsWith("PLAY_NEXT " + Get(expectedTail[i], "Path")), "A real project restores every library-protected middle and outro");
                await writer.WriteLineAsync("OK restored");
                if (queuing != null) await CompleteSoon(queuing, "Restored command acknowledged");
                await WaitForPhasePendingAsync(); var pending = Field(owner, "interactivePending");
                Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(pending, restoredFrame++)), "A discarded old lookahead accepts its fresh invocation");
            }
            await CompleteSoon(realTrigger, "The real project starts after the restored full tail");

            // A switch-only minimum must not expand ordinary playback selection.
            Invoke(owner, "ClearInteractiveRuntime", true);
            Set(library, "FileGroupInterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), "Any"));
            Set(library, "FileGroupPlayAll", false); Set(library, "FileGroupMinimumMiddleFiles", 4);
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id")); var minimumBatch = DefaultBatch(group);
            Check(minimumBatch.Length == 3, "A default-switch minimum does not alter the library's normal selection count");
            Set(minimumBatch[0], "BackendFrameId", 300L); Set(minimumBatch[1], "BackendFrameId", 301L);
            QueueState(minimumBatch[0], minimumBatch[1], minimumBatch.Last()); SetField("interactiveActive", true);
            var minimumRequest = ProtectedRequest(nextDefault); Set(minimumRequest, "IsDefaultPlaybackSwitch", true);
            var minimumTask = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", minimumRequest);
            Check(Field(owner, "interactivePending") == minimumBatch[1] && QueuedEntries().Length == 5, "The switch expands its protected middle tail to the required minimum");
            var middlePending = Field(owner, "interactivePending");
            Invoke(owner, "ApplyInteractiveOutputStarted", Started(middlePending, 301L));
            for (int i = 0; i < 5; i++)
            {
                var queuing = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                string command = await CompleteSoon(reader.ReadLineAsync(), "Minimum-middle switch tail " + i);
                Check(command.StartsWith("PLAY_NEXT "), "Minimum-middle switch uses natural file boundaries");
                await writer.WriteLineAsync("OK minimum"); await CompleteSoon(queuing, "Minimum next acknowledged");
                var pending = Field(owner, "interactivePending");
                Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(pending, 302L + i)), "Minimum-middle tail progresses through each fresh invocation");
            }
            await CompleteSoon(minimumTask, "The minimum-middle switch reaches its new default");
            Set(library, "FileGroupPlayAll", true); Set(library, "FileGroupMinimumMiddleFiles", 0);

            // Update an already prefetched default while the last project file is still playing.
            Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true);
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id")); var oldDefault = DefaultBatch(group)[0];
            var projectEntry = GroupBatch(single)[0]; Set(projectEntry, "Loop", false); Set(projectEntry, "BackendFrameId", 500L); Set(oldDefault, "BackendFrameId", 501L);
            QueueState(projectEntry, oldDefault); SetField("interactiveActive", true);
            Invoke(owner, "SwitchInteractiveDefault", target, "prefetched idle replacement");
            string replacement = await CompleteSoon(reader.ReadLineAsync(), "Updated idle lookahead command");
            Check(replacement.StartsWith("PLAY_NEXT " + Get(single, "Path")) && replacement.Contains("after_frame_id=500"), "Replacing an idle destination waits for the current project invocation to finish");
            await writer.WriteLineAsync("OK selected default"); await WaitForPhasePendingAsync();
            Check(Field(owner, "interactiveCurrent") == projectEntry && WaitingTriggerCount() == 0 && Equals(Get(Field(owner, "interactivePending"), "DefaultPlaybackId"), Get(target, "Id")), "An idle selection cannot interrupt a project, even after old idle media was prefetched");
            Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(Field(owner, "interactivePending"), 502L)), "Updated idle lookahead starts with its fresh identity");

            // Full-cycle defaults prequeue their next start instead of repeating only middles.
            Invoke(owner, "ClearInteractiveRuntime", true); Set(source, "LoopMiddleFiles", false);
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id")); var fullPass = DefaultBatch(group);
            QueueState(fullPass.Last(), null); SetField("interactiveActive", true);
            Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(Get(QueuedEntries()[0], "Phase").ToString() == "Start" && Equals(Get(QueuedEntries()[0], "DefaultPlaybackId"), Get(source, "Id")), "A finite default pass naturally returns to its start after the outro");
            Set(source, "LoopMiddleFiles", true);
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); if (peer != null) peer.Close(); ((IDisposable)client).Dispose();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
            Set(source, "MediaId", Get(library, "RuntimeId"));
        }
        Console.WriteLine("PASS: 40 actual TCP handoffs across four overrides, start/middle/end phases and boundary-free/single-file defaults; project handoffs use opposing library policies.");
    }

    private static void CheckDefaultPlaybackHotkeys(object group, object source, object target)
    {
        Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true);
        Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
        // Avoid starting idle playback while testing Windows registration lifecycle.
        SetField("processingInteractiveTriggers", true);
        using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
        using (var probe = new Form())
        {
            SetField("interactiveForm", window);
            var active = DefaultBatch(group)[0]; QueueState(active, null); SetField("interactiveActive", true);
            Invoke(window, "ReloadAll"); window.Show(); var panel = (FlowLayoutPanel)Field(window, "defaultPlaybackButtons");
            Check(panel.Controls.Count == 3 && ((IDictionary)Field(window, "registeredDefaults")).Count == 2 && (bool)Field(window, "defaultSwitchHotkeyRegistered"), "Default buttons and both hotkey families are installed");
            var first = panel.Controls[0]; Invoke(window, "RefreshDefaultPlaybackButtons");
            Check(panel.Controls[0] == first && first.Tag == source, "Default buttons refresh without recreation");
            ((Button)panel.Controls[1]).PerformClick();
            Check(Equals(Get(group, "ActiveDefaultPlaybackId"), Get(target, "Id")), "Default button selects its tagged item");
            Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x7000, IntPtr.Zero));
            Check(Equals(Get(group, "ActiveDefaultPlaybackId"), Get(source, "Id")), "Direct default shortcut uses the same selection path");
            Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x6F00, IntPtr.Zero));
            Check(!Equals(Get(group, "ActiveDefaultPlaybackId"), Get(source, "Id")), "General default shortcut selects another item");
            var toggle = (CheckBox)Field(window, "hotkeysEnabledBox"); toggle.Checked = false;
            Check(((IDictionary)Field(window, "registeredDefaults")).Count == 0 && !(bool)Field(window, "defaultSwitchHotkeyRegistered"), "Master toggle unregisters all default shortcuts");
            foreach (Keys key in new[] { Keys.F21, Keys.F22, Keys.F24 })
            {
                Check(RegisterHotKey(probe.Handle, 0x7390, 7, (uint)key), "Released default hotkey is available to other applications: " + key);
                UnregisterHotKey(probe.Handle, 0x7390);
            }
            toggle.Checked = true;
            Invoke(owner, "ClearInteractiveRuntime", true); window.Hide();
            Check(((IDictionary)Field(window, "registeredDefaults")).Count == 0 && !(bool)Field(window, "defaultSwitchHotkeyRegistered"), "Hiding releases default shortcut registrations");
            SetField("activeInteractiveProjectGroupId", Get(group, "Id")); Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var view = (ListView)Field(window, "mediaView");
            var removedMediaId = Get(((IList)Get(group, "DefaultPlaybacks"))[2], "MediaId");
            foreach (ListViewItem row in view.Items) row.Selected = Equals(Get(row.Tag, "RuntimeId"), removedMediaId);
            Invoke(window, "RemoveSelectedMedia");
            Check(((IList)Get(group, "DefaultPlaybacks")).Count == 2 && panel.Controls.Count == 2 && Equals(Get(group, "ActiveDefaultPlaybackId"), Get(source, "Id")), "Deleting one library item removes only its default references and preserves other defaults");
            SetField("interactiveForm", null);
        }
        SetField("processingInteractiveTriggers", false);
    }
}
