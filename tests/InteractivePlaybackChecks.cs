using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class InteractivePlaybackChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Type main;
    private static object owner;
    private static IList media;
    private static Type Nested(string name) { return main.GetNestedType(name, BindingFlags.NonPublic); }
    private static object New(string name) { return Activator.CreateInstance(Nested(name), true); }
    private static object Get(object item, string name) { return item.GetType().GetProperty(name).GetValue(item, null); }
    private static void Set(object item, string name, object value) { item.GetType().GetProperty(name).SetValue(item, value, null); }
    private static object Field(object item, string name) { return item.GetType().GetField(name, PrivateInstance).GetValue(item); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Invoke(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, PrivateInstance | BindingFlags.Public).Invoke(target, args);
    }
    private static object Load(XElement element)
    {
        return main.GetMethod("LoadInteractiveRouteElement", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { element });
    }
    private static XElement Save(object route)
    {
        return (XElement)main.GetMethod("SaveInteractiveRouteElement", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { route });
    }
    private static XElement Combination(params int[] indices)
    {
        return new XElement("Combination", indices.Select(i => new XElement("MediaRef", new XAttribute("id", Get(media[i], "RuntimeId")))));
    }
    private static object Route(string selection)
    {
        return Load(new XElement("Route", new XAttribute("name", "F1 project"), new XAttribute("hotkey", "F1"),
            new XAttribute("modifiers", "Control"), new XAttribute("immediate", "1"), new XAttribute("selection", selection),
            new XElement("Combinations", Combination(0, 1), Combination(2, 1), Combination(2, 3))));
    }
    private static string Entries(object route)
    {
        return string.Join(",", ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", route)).Cast<object>().Select(e => (string)Get(e, "Title")));
    }
    private static void CheckSelection()
    {
        var route = Route("sequential");
        string[] expected = { "A,B", "A2,B", "A2,B2" };
        for (int i = 0; i < 9; i++) Check(Entries(route) == expected[i % 3], "Sequential selection wraps in order");
        var other = Route("sequential");
        Check(Entries(route) == "A,B" && Entries(other) == "A,B", "Projects have independent sequence positions");
        route = Route("random");
        var seen = new HashSet<string>();
        for (int i = 0; i < 120; i++)
        {
            string entries = Entries(route);
            Check(expected.Contains(entries), "Random selects a complete configured combination");
            seen.Add(entries);
        }
        Check(seen.Count == 3, "Random mode reaches every combination with a seeded random source");
        Check((int)Get(route, "NextCombinationIndex") == 0, "Random mode does not advance sequential position");
        route = Route("sequential");
        var combinations = (IList)Get(route, "Combinations");
        combinations.Insert(0, New("InteractiveMediaCombination"));
        Check(Entries(route) == "A,B", "Empty combinations are ignored");
        var missing = New("InteractiveMediaCombination");
        ((IList)Get(missing, "MediaIds")).Add((Guid)Get(media[0], "RuntimeId"));
        ((IList)Get(missing, "MediaIds")).Add(Guid.NewGuid());
        combinations.Insert(0, missing);
        Set(route, "NextCombinationIndex", 0);
        Check(Entries(route) == "A,B", "Missing media does not trigger a partial combination");
        combinations.Clear(); combinations.Add(missing);
        Check(Entries(route) == "" && (int)Get(route, "NextCombinationIndex") == 1, "No playable combination does not consume a selection");
        route = Route("sequential");
        Set(media[1], "Loop", true);
        Set(media[1], "Options", "loop=0,custom=1");
        var snapshot = (IList)Invoke(owner, "BuildInteractiveEntries", route);
        Check((bool)Get(snapshot[1], "Loop") && ((string)Get(snapshot[1], "Options")).Contains("loop=1"), "Combination preserves media options and loop settings");
        Set(media[0], "Path", "changed.mp4");
        Check((string)Get(snapshot[0], "Path") == "A.mp4", "Already selected trigger keeps a playback snapshot");
        Set(media[0], "Path", "A.mp4"); Set(media[1], "Loop", false); Set(media[1], "Options", "");
        Console.WriteLine("PASS: sequential wrap, independent projects, random whole combinations, unavailable media and playback snapshots.");
    }
    private static void CheckPersistence()
    {
        var old = new XElement("Route", new XAttribute("name", "Legacy"), new XAttribute("hotkey", "F1"),
            new XAttribute("modifiers", "None"), new XAttribute("immediate", "1"), Combination(0, 1).Elements());
        var migrated = Load(old);
        Check(((IList)Get(migrated, "Combinations")).Count == 1 && Entries(migrated) == "A,B", "Legacy sequence migrates to a single combination");
        foreach (string mode in new[] { "random", "sequential" })
        {
            var route = Route(mode);
            Entries(route);
            XElement saved = Save(route);
            var loaded = Load(XElement.Parse(saved.ToString()));
            Check(saved.Attribute("selection").Value == mode && Save(loaded).ToString() == saved.ToString(), "All combinations, hotkey, command and mode round trip");
            Check((int)Get(loaded, "NextCombinationIndex") == 0, "Sequential playback restarts from the first combination after loading");
        }
        var state = New("ConnectionTabState");
        ((IList)Get(state, "InteractiveRoutes")).Add(Route("sequential"));
        foreach (var item in media) ((IList)Get(state, "InteractiveMedia")).Add(item);
        var tab = (XElement)Invoke(owner, "SaveConnectionTabElement", state);
        Invoke(owner, "LoadInteractivePlaybackElement", tab.Element("InteractivePlayback"));
        var routes = (IList)Field(owner, "interactiveRoutes");
        Check(routes.Count == 1 && ((IList)Get(routes[0], "Combinations")).Count == 3 && !(bool)Get(routes[0], "RandomOrder"), "Real connection XML save and load includes combinations");
        Console.WriteLine("PASS: legacy XML migration, random/sequential round trips and connection configuration persistence.");
    }
    private static object FileGroup(bool random = false, bool noRepeat = true)
    {
        var group = New("InteractiveMediaItem");
        Set(group, "IsFileGroup", true);
        Set(group, "Title", "Group");
        Set(group, "FileGroupRandom", random);
        Set(group, "FileGroupNoRepeat", noRepeat);
        foreach (string path in new[] { "g1.mp4", "g2.mp4", "g3.mp4" }) ((IList)Get(group, "FilePaths")).Add(path);
        return group;
    }
    private static string SelectFile(object group, Random random)
    {
        return (string)main.GetMethod("SelectInteractiveMediaPath", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { group, random });
    }
    private sealed class FirstRandom : Random
    {
        public override int Next(int maxValue) { return 0; }
    }
    private static void CheckFileGroups(Assembly assembly)
    {
        var group = FileGroup();
        var random = new Random(73);
        for (int i = 0; i < 9; i++) Check(SelectFile(group, random) == "g" + (i % 3 + 1) + ".mp4", "Sequential file selection wraps");
        group = FileGroup(true);
        for (int round = 0; round < 12; round++)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 3; i++) Check(seen.Add(SelectFile(group, random)), "A random round does not repeat a file");
            Check(seen.SetEquals(new[] { "g1.mp4", "g2.mp4", "g3.mp4" }), "A round covers every file exactly once");
        }
        group = FileGroup(true, false);
        Check(SelectFile(group, new FirstRandom()) == "g1.mp4" && SelectFile(group, new FirstRandom()) == "g1.mp4", "Random repeats can be enabled");
        var single = FileGroup(true);
        ((IList)Get(single, "FilePaths")).Clear(); ((IList)Get(single, "FilePaths")).Add("single.mp4");
        Check(SelectFile(single, random) == "single.mp4" && SelectFile(single, random) == "single.mp4", "Single-file groups complete repeated rounds");
        ((IList)Get(single, "FilePaths")).Clear();
        Check(SelectFile(single, random) == null, "Empty groups cannot select a file");

        group = FileGroup();
        media.Add(group);
        try
        {
            Guid groupId = (Guid)Get(group, "RuntimeId");
            var route = Load(new XElement("Route", new XAttribute("selection", "sequential"), new XElement("Combinations",
                Combination(1), new XElement("Combination", new XElement("MediaRef", new XAttribute("id", groupId))))));
            Check(Entries(route) == "B" && (int)Get(group, "NextFileIndex") == 0, "Unselected project alternatives do not advance a group");
            Check(Entries(route) == "Group / g1.mp4" && (int)Get(group, "NextFileIndex") == 1, "Selected alternative consumes exactly one file");
            Check(Entries(route) == "B" && Entries(route) == "Group / g2.mp4", "Group progress survives other project selections");
            var other = FileGroup();
            Check(SelectFile(other, random) == "g1.mp4", "Groups keep independent progress");
            Set(group, "Options", "custom=1,loop=0"); Set(group, "Loop", true);
            var groupOnly = Load(new XElement("Route", new XElement("Combinations", new XElement("Combination",
                new XElement("MediaRef", new XAttribute("id", groupId))))));
            var entries = (IList)Invoke(owner, "BuildInteractiveEntries", groupOnly);
            Check(entries.Count == 1 && (string)Get(entries[0], "Path") == "g3.mp4", "Group occupies a single media position");
            string options = (string)Get(entries[0], "Options");
            Check(options.Contains("custom=1") && !options.Contains("loop="), "Group options apply without pinning a single file in a backend loop");
            Check(Get(entries[0], "GroupLoop") != null, "The outer loop setting creates a group repeat state");
            ((IList)Get(group, "FilePaths"))[2] = "changed.mp4";
            Check((string)Get(entries[0], "Path") == "g3.mp4", "Queued selections keep a stable file snapshot");
            ((IList)Get(group, "FilePaths"))[2] = "g3.mp4";
            Set(group, "FileGroupPlayAll", true);
            Set(group, "FileGroupRandom", false);
            var allEntries = (IList)Invoke(owner, "BuildInteractiveEntries", groupOnly);
            Check(string.Join(",", allEntries.Cast<object>().Select(e => (string)Get(e, "Path"))) == "g1.mp4,g2.mp4,g3.mp4", "Play-all expands the complete group in order");
            Check(allEntries.Cast<object>().All(entry => !(bool)Get(entry, "Loop") && Get(entry, "GroupLoop") != null), "Every grouped file participates in a whole-group loop");
            Check(allEntries.Cast<object>().All(entry => !((string)Get(entry, "Options")).Contains("loop=")), "No grouped file is given a backend loop flag");
            Set(group, "FileGroupRandom", true);
            Set(group, "FileGroupNoRepeat", false);
            var orders = new HashSet<string>();
            for (int i = 0; i < 12; i++)
            {
                var all = ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", groupOnly)).Cast<object>().Select(e => (string)Get(e, "Path")).ToArray();
                Check(all.Length == 3 && all.Distinct().Count() == 3, "Random play-all includes every file once regardless of single-file repeat setting");
                orders.Add(string.Join(",", all));
            }
            Check(orders.Count > 1, "Random play-all shuffles group order");
            var withTail = Load(new XElement("Route", new XElement("Combinations", new XElement("Combination",
                new XElement("MediaRef", new XAttribute("id", groupId)), new XElement("MediaRef", new XAttribute("id", Get(media[1], "RuntimeId")))))));
            var extended = (IList)Invoke(owner, "BuildInteractiveEntries", withTail);
            Check(extended.Count == 4 && (string)Get(extended[3], "Title") == "B", "Expanded group preserves the following project media");
            ((IList)Get(group, "FilePaths")).Clear();
            Check(Entries(groupOnly) == "", "Empty file group makes the whole project alternative unavailable");
        }
        finally { media.Remove(group); }

        var saveMedia = main.GetMethod("SaveInteractiveMediaElement", BindingFlags.Static | BindingFlags.NonPublic);
        var loadMedia = main.GetMethod("LoadInteractiveMediaElement", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (bool randomMode in new[] { false, true })
        foreach (bool noRepeat in new[] { false, true })
        foreach (bool playAll in new[] { false, true })
        {
            group = FileGroup(randomMode, noRepeat);
            Set(group, "FileGroupPlayAll", playAll);
            SelectFile(group, random);
            var xml = (XElement)saveMedia.Invoke(null, new[] { group });
            var loaded = loadMedia.Invoke(null, new object[] { XElement.Parse(xml.ToString()) });
            Check(((XElement)saveMedia.Invoke(null, new[] { loaded })).ToString() == xml.ToString(), "File paths and playback settings round trip");
            Check((int)Get(loaded, "NextFileIndex") == 0 && ((IList)Get(loaded, "RemainingFiles")).Count == 0, "Reload resets file-group progress");
        }
        var legacy = loadMedia.Invoke(null, new object[] { new XElement("Item", new XAttribute("path", "legacy.mp4")) });
        Check(!(bool)Get(legacy, "IsFileGroup") && (string)Get(legacy, "Path") == "legacy.mp4", "Old single media remains a single file");
        var duplicateXml = (XElement)saveMedia.Invoke(null, new[] { FileGroup() });
        duplicateXml.Element("Files").Add(new XElement("File", new XAttribute("path", " G1.MP4 ")), new XElement("File", new XAttribute("path", "")));
        var deduplicated = loadMedia.Invoke(null, new object[] { duplicateXml });
        Check(((IList)Get(deduplicated, "FilePaths")).Count == 3, "Repeated and blank file paths cannot break no-repeat rounds");

        foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            group = FileGroup();
            Set(group, "Loop", true);
            using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupEditDialog"), new object[] { group }))
            {
                Check(dialog.GetType().GetField("loopBox", PrivateInstance) == null, "Group editor has no separate loop checkbox");
                ((CheckBox)Field(dialog, "preventInterruptBox")).Checked = true;
                var mode = (ComboBox)Field(dialog, "selectionBox");
                var noRepeat = (CheckBox)Field(dialog, "noRepeatBox");
                Check(!noRepeat.Enabled, "No-repeat is only enabled in random mode");
                mode.SelectedIndex = 1;
                Check(noRepeat.Enabled, "Switching to random enables no-repeat");
                var scope = (ComboBox)Field(dialog, "scopeBox");
                scope.SelectedIndex = 1;
                Check(!noRepeat.Enabled, "Play-all already includes every file once, so single-file repeat control is disabled");
                scope.SelectedIndex = 0;
                Check(noRepeat.Enabled, "Returning to one-file mode restores the no-repeat control");
                Invoke(dialog, "AddPaths", (object)new[] { "g4.mp4", " G1.MP4 ", "" });
                Check(((IList)Field(dialog, "files")).Count == 4, "Editor adds multiple files and rejects duplicates");
                Check(((IList)Get(group, "FilePaths")).Count == 3 && !(bool)Get(group, "FileGroupRandom"), "Unsaved group edits cannot mutate library settings");
                var filesBox = (ListBox)Field(dialog, "filesBox");
                filesBox.SelectedIndex = 0;
                Invoke(dialog, "MoveFile", 1);
                Check((string)((IList)Field(dialog, "files"))[1] == "g1.mp4", "Editor can reorder files");
                Invoke(dialog, "RemoveFile");
                Check(((IList)Field(dialog, "files")).Count == 3, "Editor can remove a file");
                Set(group, "NextFileIndex", 2); ((IList)Get(group, "RemainingFiles")).Add("old.mp4");
                scope.SelectedIndex = 1;
                Invoke(dialog, "Apply", group);
                Check((bool)Get(group, "FileGroupPlayAll"), "Editor saves one-file/all-files scope");
                Check((bool)Get(group, "Loop"), "Saving group edits preserves the outer loop setting");
                Check((bool)Get(group, "PreventInterrupt"), "Group editor saves interruption protection");
                Check((bool)Get(group, "IsFileGroup") && (bool)Get(group, "FileGroupRandom") && (bool)Get(group, "FileGroupNoRepeat"), "Editor saves file-group playback options");
                Check((int)Get(group, "NextFileIndex") == 0 && ((IList)Get(group, "RemainingFiles")).Count == 0, "Editing clears stale selection progress");
                Invoke(dialog, "AddPaths", (object)new[] { "extra.mp4" });
                Check(((IList)Get(group, "FilePaths")).Count == 3, "Saved paths do not share the editor's mutable list");
                dialog.Size = dialog.MinimumSize; dialog.PerformLayout();
                Check(filesBox.Height > 100, "File editor fits minimum window size in each language");
            }
        }
        var state = New("ConnectionTabState");
        ((IList)Get(state, "InteractiveMedia")).Add(FileGroup(true));
        var savedTab = (XElement)Invoke(owner, "SaveConnectionTabElement", state);
        var savedGroup = savedTab.Element("InteractivePlayback").Element("Media").Element("Item");
        Check(savedGroup.Attribute("type").Value == "fileGroup" && savedGroup.Element("Files").Elements().Count() == 3, "Real connection XML saves file groups");
        var originalMedia = media.Cast<object>().ToArray();
        try
        {
            Invoke(owner, "LoadInteractivePlaybackElement", savedTab.Element("InteractivePlayback"));
            Check(media.Count == 1 && (bool)Get(media[0], "IsFileGroup") && ((IList)Get(media[0], "FilePaths")).Count == 3, "Real connection XML loads file groups");
        }
        finally { media.Clear(); foreach (var item in originalMedia) media.Add(item); }
        Console.WriteLine("PASS: file-group single/all selection, sequential/random/no-repeat rounds, loop placement, per-group state, XML migration and three-language editing.");
    }
    private static void SetField(string name, object value) { main.GetField(name, PrivateInstance).SetValue(owner, value); }
    private static object[] GroupBatch(object group)
    {
        return ((IEnumerable)Invoke(owner, "BuildInteractiveMediaEntries", group, null)).Cast<object>().ToArray();
    }
    private static object[] QueuedEntries()
    {
        return ((IEnumerable)Field(owner, "interactiveQueue")).Cast<object>().ToArray();
    }
    private static void QueueState(object current, object pending, params object[] queued)
    {
        SetField("interactiveCurrent", current); SetField("interactivePending", pending);
        object queue = Field(owner, "interactiveQueue"); Invoke(queue, "Clear");
        foreach (var entry in queued) Invoke(queue, "Enqueue", entry);
    }
    private static void CheckGroupLooping()
    {
        var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true);
        Set(group, "Options", "loop=1,custom=1");
        var batch = GroupBatch(group);
        Check(batch.Length == 3 && batch.All(entry => !(bool)Get(entry, "Loop") && !((string)Get(entry, "Options")).Contains("loop=")), "Group cycles advance through EOF instead of looping one file");
        for (int pass = 0; pass < 20; pass++)
        {
            QueueState(batch[batch.Length - 1], null);
            Invoke(owner, "QueueInteractiveGroupContinuation");
            batch = QueuedEntries();
            Check(string.Join(",", batch.Select(entry => (string)Get(entry, "Path"))) == "g1.mp4,g2.mp4,g3.mp4", "Sequential looping repeats the whole group");
            Check(batch.All(entry => (int)Get(entry, "GroupIteration") == pass + 1), "Each continuation is a new pass");
            Invoke(owner, "QueueInteractiveGroupContinuation");
            Check(QueuedEntries().Length == 3, "Loop lookahead is bounded to one pass");
        }
        ((IList)Get(group, "FilePaths")).Clear(); ((IList)Get(group, "FilePaths")).Add("edited.mp4");
        QueueState(batch[2], null); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check((string)Get(QueuedEntries()[0], "Path") == "g1.mp4", "Active group loops retain their playback snapshot");

        group = FileGroup(true); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true);
        batch = GroupBatch(group);
        var orders = new HashSet<string>();
        for (int pass = 0; pass < 12; pass++)
        {
            string[] paths = batch.Select(entry => (string)Get(entry, "Path")).ToArray();
            Check(paths.Length == 3 && paths.Distinct().Count() == 3, "Random whole-group loops visit every file once per pass");
            orders.Add(string.Join(",", paths));
            QueueState(batch[2], null); Invoke(owner, "QueueInteractiveGroupContinuation"); batch = QueuedEntries();
        }
        Check(orders.Count > 1, "Whole-group random loops reshuffle each pass");
        group = FileGroup(true); Set(group, "Loop", true); batch = GroupBatch(group);
        for (int round = 0; round < 8; round++)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 3; i++)
            {
                Check(seen.Add((string)Get(batch[0], "Path")), "One-file loop mode carries no-repeat state through the complete group");
                QueueState(batch[0], null); Invoke(owner, "QueueInteractiveGroupContinuation"); batch = QueuedEntries();
            }
        }
        group = FileGroup(true); Set(group, "Loop", true); batch = GroupBatch(group);
        var acrossTriggers = new HashSet<string> { (string)Get(batch[0], "Path") };
        QueueState(batch[0], null); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check(acrossTriggers.Add((string)Get(QueuedEntries()[0], "Path")), "Group-loop prefetch consumes the shared no-repeat round");
        Check(acrossTriggers.Add((string)Get(GroupBatch(group)[0], "Path")), "Retriggering the same looping group continues the shared no-repeat round");
        group = FileGroup(); Set(group, "Loop", true); batch = GroupBatch(group);
        for (int i = 0; i < 6; i++)
        {
            Check((string)Get(batch[0], "Path") == "g" + (i % 3 + 1) + ".mp4", "One-file sequential looping rotates the whole group");
            QueueState(batch[0], null); Invoke(owner, "QueueInteractiveGroupContinuation"); batch = QueuedEntries();
        }
        group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true); batch = GroupBatch(group);
        QueueState(batch[0], batch[1], batch[2]);
        Check(Invoke(owner, "PrepareInteractiveRouteAppend") == null && QueuedEntries().Length == 1 && Field(owner, "interactivePending") == batch[1], "A new trigger preserves the remainder of the pass already playing");
        Check((bool)Get(Get(batch[0], "GroupLoop"), "Cancelled"), "A new trigger stops further group repeats");
        QueueState(batch[2], null); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check(QueuedEntries().Length == 0, "Cancelled group cannot generate another pass");

        batch = GroupBatch(group); QueueState(batch[2], null); Invoke(owner, "QueueInteractiveGroupContinuation");
        var prefetched = QueuedEntries(); QueueState(batch[2], prefetched[0], prefetched[1], prefetched[2]);
        Check(Invoke(owner, "PrepareInteractiveRouteAppend") == prefetched[0] && QueuedEntries().Length == 0 && Field(owner, "interactivePending") == null,
            "A new trigger replaces a prefetched repeat pass, including the backend's pending first file");
        var ordinary = ((IList)Invoke(owner, "BuildInteractiveEntries", Route("sequential")))[0];
        batch = GroupBatch(group); QueueState(ordinary, batch[0], batch[1], batch[2]);
        Check(Invoke(owner, "PrepareInteractiveRouteAppend") == batch[0] && QueuedEntries().Length == 0, "An unstarted loop group is replaced as a whole");
        batch = GroupBatch(group); QueueState(ordinary, batch[0], batch[1], batch[2], ordinary);
        Check(Invoke(owner, "PrepareInteractiveRouteAppend") == null && QueuedEntries().Length == 3, "A group with following media still plays one pass before the follow-up");
        QueueState(batch[2], null, ordinary); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check(QueuedEntries().Length == 1 && QueuedEntries()[0] == ordinary, "Group looping cannot block already queued following media");
        group = FileGroup(); Set(group, "FileGroupPlayAll", true); batch = GroupBatch(group);
        QueueState(batch[2], null); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check(QueuedEntries().Length == 0 && Get(batch[2], "GroupLoop") == null, "Outer loop off plays the group once");
        var file = New("InteractiveMediaItem"); Set(file, "Path", "single.mp4"); Set(file, "Loop", true);
        var single = GroupBatch(file)[0];
        Check((bool)Get(single, "Loop") && ((string)Get(single, "Options")).Contains("loop=1"), "Ordinary media retains backend file looping");
        QueueState(ordinary, single, single); Check(Invoke(owner, "PrepareInteractiveRouteAppend") == single && QueuedEntries().Length == 0, "Ordinary loop destinations remain replaceable");
        QueueState(batch[2], null); Invoke(owner, "ClearInteractiveRuntime", true); Invoke(owner, "QueueInteractiveGroupContinuation");
        Check(Field(owner, "interactiveCurrent") == null && QueuedEntries().Length == 0, "Clearing playback releases the loop and its queued pass");
        Console.WriteLine("PASS: complete group repeats, shuffled/no-repeat cycles, bounded lookahead, outer-loop control and new-trigger replacement.");
    }
    private static void CheckGroupQueueCommands()
    {
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        SetField("pendingLogText", new System.Text.StringBuilder());
        SetField("lastLogFlushAt", DateTime.UtcNow.AddYears(1));
        try
        {
            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true);
            var batch = GroupBatch(group);
            QueueState(batch[2], null); SetField("interactiveActive", true);
            var response = new TaskCompletionSource<string>();
            var commands = new List<string>();
            Func<string, Task<string>> send = command => { commands.Add(command); return response.Task; };
            var task = (Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send);
            SetField("interactiveNextQueueTask", task);
            Check(!task.IsCompleted && commands.Count == 1 && commands[0] == "PLAY_NEXT g1.mp4", "Whole-group continuation queues the first file through PLAY_NEXT");
            Check(ReferenceEquals(Invoke(owner, "EnsureInteractiveNextQueuedAsync"), task), "Concurrent EOF scheduling shares the in-flight send");
            var waiting = (Task)Invoke(owner, "WaitForInteractiveNextQueueAsync");
            Check(!waiting.IsCompleted, "Replacement waits for the in-flight next-file command");
            response.SetResult("OK"); task.GetAwaiter().GetResult(); waiting.GetAwaiter().GetResult();
            Check((string)Get(Field(owner, "interactivePending"), "Path") == "g1.mp4" && QueuedEntries().Length == 2, "A successful send retains the rest of the loop pass");
            ((Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync")).GetAwaiter().GetResult();
            Check(commands.Count == 1 && QueuedEntries().Length == 2, "An acknowledged pending file prevents duplicate next-file sends");

            batch = GroupBatch(group); QueueState(batch[2], null); SetField("interactiveActive", true);
            Func<string, Task<string>> fail = command => Task.FromResult("ERR busy");
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", fail)).GetAwaiter().GetResult();
            Check(string.Join(",", QueuedEntries().Select(entry => (string)Get(entry, "Path"))) == "g1.mp4,g2.mp4,g3.mp4", "A failed send keeps the complete repeat pass queued for retry");

            batch = GroupBatch(group); QueueState(batch[2], null); SetField("interactiveActive", true);
            response = new TaskCompletionSource<string>();
            task = (Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send);
            SetField("interactiveNextQueueTask", task);
            Invoke(owner, "ClearInteractiveRuntime", true);
            response.SetResult("OK"); task.GetAwaiter().GetResult();
            Check(Field(owner, "interactivePending") == null && QueuedEntries().Length == 0 && Field(owner, "interactiveCurrent") == null,
                "A late command reply cannot restart a cleared group loop");
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
        Console.WriteLine("PASS: PLAY_NEXT group handoff, concurrent-send serialization, failed-send retry and late-reply cancellation.");
    }
    private static void CheckPlaybackDuringConfigurationEdits()
    {
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        var routes = (IList)Field(owner, "interactiveRoutes");
        int routeCount = routes.Count;
        try
        {
            var first = GroupBatch(media[0])[0];
            var second = GroupBatch(media[0])[0];
            var third = GroupBatch(media[0])[0];
            QueueState(first, second); SetField("interactiveActive", true);
            string summary = (string)Get(first, "InputSummary");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == second, "The first natural end activates the second invocation of the same file");
            using (var editor = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, null }))
            {
                ((TextBox)Field(editor, "nameBox")).Text = "Added during playback";
                ((ComboBox)Field(editor, "mediaBox")).SelectedIndex = 2;
                Invoke(editor, "AddSelectedMedia");
                var route = New("InteractiveRouteItem");
                Invoke(editor, "Apply", route); routes.Add(route);
                using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
                {
                    Invoke(window, "RefreshRoutes"); Invoke(window, "RefreshMedia"); Invoke(window, "RefreshRuntime");
                }
            }
            Check(Field(owner, "interactiveCurrent") == second && QueuedEntries().Length == 0, "Adding and refreshing a project cannot change the active playback snapshot");
            QueueState(second, third);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == second && Field(owner, "interactivePending") == third,
                "A delayed or duplicate end from the previous invocation cannot skip the current same-file invocation after editing");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=200 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == third, "A new playback frame ID still advances the repeated file normally");
        }
        finally
        {
            while (routes.Count > routeCount) routes.RemoveAt(routes.Count - 1);
            Invoke(owner, "ClearInteractiveRuntime", true);
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
        }
        Console.WriteLine("PASS: project creation and list refresh during playback preserve snapshots; repeated files reject delayed duplicate end events.");
    }
    private static void CheckInteractiveEventOrdering()
    {
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var first = GroupBatch(media[0])[0];
            var second = GroupBatch(media[0])[0];
            var third = GroupBatch(media[0])[0];
            string summary = (string)Get(first, "InputSummary");
            QueueState(first, null, second); SetField("interactiveActive", true);
            var response = new TaskCompletionSource<string>();
            Func<string, Task<string>> send = command => response.Task;
            var sending = (Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send);
            SetField("interactiveNextQueueTask", sending);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_EOF frame_id=100 input=" + summary);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + summary);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + summary);
            var processing = (Task)Field(owner, "interactiveBackendEventTask");
            Check(!processing.IsCompleted && Field(owner, "interactiveCurrent") == first, "Backend events wait for the in-flight PLAY_NEXT acknowledgement");
            response.SetResult("OK"); processing.GetAwaiter().GetResult();
            Check(Field(owner, "interactiveCurrent") == second && Field(owner, "interactivePending") == null,
                "Queued EOF/end events advance exactly once after acknowledgement, even for repeated file summaries");
            Check((string)Field(owner, "currentActualStatusInput") == summary, "The confirmed transition updates the currently displayed output identity");
            QueueState(second, third);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_EOF frame_id=200 input=" + summary);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_EOF frame_id=300 input=" + summary);
            Check((long)Get(second, "BackendFrameId") == 200 && (long)Get(third, "BackendFrameId") == 300,
                "Early decoding EOF distinguishes current and prefetched invocations of the same file");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=250 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == second, "A different invocation ID cannot end the current file even when its hash matches");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=200 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == third, "The correct current invocation can activate the already decoded next file");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_EOF frame_id=200 input=" + summary);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + summary);
            Check(Field(owner, "interactiveCurrent") == third && (long)Get(third, "BackendFrameId") == 300, "Late earlier EOF/end events cannot change the current invocation");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=300 input=" + summary);
            Check(!(bool)Field(owner, "interactiveActive"), "The final invocation ends normally");

            Invoke(owner, "ClearInteractiveRuntime", true);
            first = GroupBatch(media[0])[0]; second = GroupBatch(media[1])[0];
            QueueState(first, null, second); SetField("interactiveActive", true);
            response = new TaskCompletionSource<string>();
            sending = (Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send); SetField("interactiveNextQueueTask", sending);
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=400 input=" + summary);
            processing = (Task)Field(owner, "interactiveBackendEventTask");
            Invoke(owner, "ClearInteractiveRuntime", true);
            response.SetResult("OK"); processing.GetAwaiter().GetResult();
            Check(Field(owner, "interactiveCurrent") == null && Field(owner, "interactivePending") == null && !(bool)Field(owner, "interactiveActive"),
                "Stopping while an event waits for acknowledgement cancels its runtime generation");
            Check(!((IEnumerable)Field(owner, "interactiveBackendEvents")).Cast<object>().Any(), "Stopping also clears queued backend notifications");

            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true); Set(group, "PreventInterrupt", true);
            var batch = GroupBatch(group);
            QueueState(batch[0], batch[1], batch[2]); SetField("interactiveActive", true);
            using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupEditDialog"), new object[] { group }))
            {
                ((CheckBox)Field(dialog, "preventInterruptBox")).Checked = false;
                ((ComboBox)Field(dialog, "scopeBox")).SelectedIndex = 0;
                ((TextBox)Field(dialog, "titleBox")).Text = "Edited group";
                Invoke(dialog, "Apply", group);
            }
            Check((bool)Get(batch[0], "PreventInterrupt") && QueuedEntries().SequenceEqual(new[] { batch[2] }) && Field(owner, "interactivePending") == batch[1],
                "Saving group edits preserves the running group's original protection, pending member and complete pass");
            Check((bool)Get(Get(Get(batch[0], "GroupLoop"), "Media"), "PreventInterrupt"), "Future repeats of the running snapshot keep their own settings");
            var future = GroupBatch(group);
            Check(future.Length == 1 && !(bool)Get(future[0], "PreventInterrupt") && ((string)Get(future[0], "Title")).StartsWith("Edited group"),
                "New triggers pick up the edited media settings independently of the playing snapshot");
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
        Console.WriteLine("PASS: ordered backend events during delayed replies, per-invocation frame IDs, prefetched EOF, cancellation and independent settings snapshots.");
    }
    private static object ProtectedRequest(params object[] entries)
    {
        var request = New("InteractiveTriggerRequest");
        foreach (var entry in entries) ((IList)Get(request, "Entries")).Add(entry);
        return request;
    }
    private static Task HandoffTask(object handoff)
    {
        var completion = handoff.GetType().GetField("Completion").GetValue(handoff);
        return (Task)Get(completion, "Task");
    }
    private static void CheckInterruptProtection()
    {
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var a = New("InteractiveMediaItem"); Set(a, "Title", "Protected A"); Set(a, "Path", "protected.mp4");
            Set(a, "PreventInterrupt", true);
            var current = GroupBatch(a)[0];
            var b = GroupBatch(media[1])[0];
            var next = GroupBatch(media[2])[0];
            Check((bool)Get(current, "PreventInterrupt") && !(bool)Get(b, "PreventInterrupt"), "Protection belongs to individual media, not the entire project");
            var request = ProtectedRequest(next);
            QueueState(current, b); SetField("interactiveActive", true);
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", request);
            var waiting = HandoffTask(handoff);
            Check(Field(owner, "interactivePending") == null && QueuedEntries().SequenceEqual(new[] { next }), "A protected first file replaces the interruptible second file with the new project");
            var commands = new List<string>();
            Func<string, Task<string>> send = command => { commands.Add(command); return Task.FromResult("OK"); };
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(commands.SequenceEqual(new[] { "PLAY_NEXT A2.mp4" }) && !waiting.IsCompleted, "Protected files defer through PLAY_NEXT instead of issuing immediate PLAY");
            ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(current, "InputSummary"))).GetAwaiter().GetResult();
            Check(!waiting.IsCompleted && Field(owner, "interactivePending") == next, "Source end alone cannot confirm a replaced pending input");
            Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                Enum.Parse(Nested("BackendInputEventType"), "Started"), 100L, Get(next, "InputSummary") }));
            Check(waiting.IsCompleted && Field(owner, "interactiveCurrent") == next && Field(owner, "interactiveProtectedHandoff") == null, "Actual output start releases the waiting trigger and skips old B");

            Set(a, "Loop", true); current = GroupBatch(a)[0];
            next = GroupBatch(media[2])[0]; request = ProtectedRequest(next);
            QueueState(current, b); SetField("interactiveActive", true);
            handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", request);
            commands.Clear();
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(commands.SequenceEqual(new[] { "PLAY_NEXT A2.mp4" }), "Protected looping files yield to a next-file command at their natural EOF");
            ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(current, "InputSummary"))).GetAwaiter().GetResult();
            Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                Enum.Parse(Nested("BackendInputEventType"), "Started"), 200L, Get(next, "InputSummary") }));
            Check(HandoffTask(handoff).IsCompleted && Field(owner, "interactiveCurrent") == next, "Protected single-file loops do not make triggers wait forever");
            Set(a, "Loop", false);
            var one = FileGroup(); Set(one, "PreventInterrupt", true);
            var selected = GroupBatch(one);
            Check(selected.Length == 1 && (bool)Get(selected[0], "PreventInterrupt"), "One-file group mode protects only the file selected for this invocation");

            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "Loop", true); Set(group, "PreventInterrupt", true);
            var batch = GroupBatch(group);
            Check(batch.All(entry => (bool)Get(entry, "PreventInterrupt") && Get(entry, "PlaybackUnitId").Equals(Get(batch[0], "PlaybackUnitId"))), "All-files groups protect every member with a shared invocation identity");
            var repeated = GroupBatch(group);
            Check(!Get(batch[0], "PlaybackUnitId").Equals(Get(repeated[0], "PlaybackUnitId")), "Repeated invocations have separate protection boundaries");
            QueueState(batch[0], batch[1], batch[2], b); SetField("interactiveActive", true);
            handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", request); waiting = HandoffTask(handoff);
            Check(Field(owner, "interactivePending") == batch[1] && QueuedEntries().SequenceEqual(new[] { batch[2], next }), "A protected group keeps its pending and queued members, removes the old project tail and appends the new project");
            Check((bool)Get(Get(batch[0], "GroupLoop"), "Cancelled"), "A protected looping group yields at the current pass boundary");
            SetField("interactiveCurrent", batch[1]); Invoke(owner, "CompleteInteractiveProtectedHandoff");
            Check(!waiting.IsCompleted, "A transition inside a protected group cannot release subsequent triggers");
            QueueState(batch[2], next);
            ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(batch[2], "InputSummary"))).GetAwaiter().GetResult();
            Check(waiting.IsCompleted && Field(owner, "interactiveCurrent") == next && QueuedEntries().Length == 0, "The final group member releases the new project without another loop pass");

            batch = GroupBatch(group); repeated = GroupBatch(group);
            QueueState(batch[2], repeated[0], repeated[1], repeated[2]); SetField("interactiveActive", true);
            handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", request);
            Check(Field(owner, "interactivePending") == null && QueuedEntries().SequenceEqual(new[] { next }), "Prefetched future passes cannot extend the protected current pass");
            Invoke(owner, "ClearInteractiveRuntime", true);
            Check(HandoffTask(handoff).IsCompleted, "Clearing playback releases protected waits");

            foreach (string method in new[] { "StartInteractiveImmediatelyAsync", "AppendInteractiveRouteAsync" })
            {
                batch = GroupBatch(group); QueueState(batch[0], batch[1], batch[2], b); SetField("interactiveActive", true);
                SetField("interactiveNextQueueTask", null);
                var task = (Task)Invoke(owner, method, request);
                Check(!task.IsCompleted && Field(owner, "interactiveCurrent") == batch[0] && Field(owner, "interactivePending") == batch[1], method + " respects the protected group without interrupting it");
                var queuedRequest = ProtectedRequest(b);
                Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", queuedRequest);
                Check(((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Count() == 1, "Later triggers remain queued while protection holds the dispatcher");
                Invoke(owner, "ClearInteractiveRuntime", true); task.GetAwaiter().GetResult();
                Check(Field(owner, "interactiveCurrent") == null && !((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Any(), "Stop cancels protected waits and later triggers without restarting playback");
            }

            var save = main.GetMethod("SaveInteractiveMediaElement", BindingFlags.Static | BindingFlags.NonPublic);
            var load = main.GetMethod("LoadInteractiveMediaElement", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var item in new[] { a, group })
            {
                var xml = (XElement)save.Invoke(null, new[] { item });
                Check(xml.Attribute("preventInterrupt").Value == "1" && (bool)Get(load.Invoke(null, new object[] { xml }), "PreventInterrupt"), "Single-file and group protection survive XML save and reload");
                xml.Attribute("preventInterrupt").Remove();
                Check(!(bool)Get(load.Invoke(null, new object[] { xml }), "PreventInterrupt"), "Legacy media default to interruptible");
            }
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using (var dialog = (Form)Activator.CreateInstance(Nested("MediaEditDialog"), new object[] { a }))
                {
                    var check = (CheckBox)Field(dialog, "preventInterruptBox");
                    Check(check.Checked && !string.IsNullOrWhiteSpace(check.Text), "Single-file editor loads translated protection setting");
                    check.Checked = false; Invoke(dialog, "Apply", a);
                    Check(!(bool)Get(a, "PreventInterrupt"), "Single-file editor can disable protection");
                    Set(a, "PreventInterrupt", true);
                }
                using (var dialog = (Form)Activator.CreateInstance(Nested("FileGroupEditDialog"), new object[] { group }))
                {
                    var check = (CheckBox)Field(dialog, "preventInterruptBox");
                    Check(check.Checked, "Group editor loads protection setting");
                    check.Checked = false; Invoke(dialog, "Apply", group);
                    Check(!(bool)Get(group, "PreventInterrupt") && (bool)Get(group, "Loop"), "Group editor saves protection without changing outer looping");
                    Set(group, "PreventInterrupt", true);
                }
            }
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
        Console.WriteLine("PASS: protected file/group boundaries, skipped interruptible tails, loop yielding, PLAY/PLAY_NEXT deferral, stop cancellation, persistence and translated editors.");
    }
    private static void CheckActualOutputVerification()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "PreventInterrupt", true);
            var batch = GroupBatch(group);
            QueueState(batch[0], batch[1], batch[2]); SetField("interactiveActive", true); SetField("interactiveNeedsStatusCheck", true);
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", "ERR unavailable"), "A failed query is inconclusive");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(null, "opening", batch[1])), "Opening a prefetched input is inconclusive");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(null, "playing", batch[1])), "Decoder input without actual output cannot establish current playback");
            Check(Field(owner, "interactiveCurrent") == batch[0] && Field(owner, "interactivePending") == batch[1] && QueuedEntries().SequenceEqual(new[] { batch[2] }), "Inconclusive status preserves the entire running group and planned queue");
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[0], "paused", batch[1])) && Field(owner, "interactiveCurrent") == batch[0],
                "Actual output identifies the current file even when decoding has advanced, and paused playback is retained");
            Check(Field(owner, "interactivePending") == batch[1] && !(bool)Field(owner, "interactiveNeedsStatusCheck"), "A prefetched decoder input is not promoted to playing prematurely");

            SetField("interactiveNeedsStatusCheck", true);
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[0], "playing", batch[0])), "The current output can be verified after reconnect");
            Check(Field(owner, "interactivePending") == null && QueuedEntries().SequenceEqual(new[] { batch[1], batch[2] }), "An unconfirmed pending command is restored to the queue rather than discarded");
            var commands = new List<string>();
            Func<string, Task<string>> send = command => { commands.Add(command); return Task.FromResult("OK"); };
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(commands.SequenceEqual(new[] { "PLAY_NEXT g2.mp4" }) && Field(owner, "interactivePending") == batch[1], "Reconnection can re-submit the missing next input without restarting the current file");

            var next = GroupBatch(media[2])[0];
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(next));
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[1], "playing", batch[1])) && Field(owner, "interactiveCurrent") == batch[1] && !HandoffTask(handoff).IsCompleted,
                "Actual output can recover a missed group transition while retaining interrupt protection");
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next)) && Field(owner, "interactiveCurrent") == next && HandoffTask(handoff).IsCompleted,
                "An already playing queued trigger is activated and releases its protected handoff without replaying it");

            var oldCurrent = GroupBatch(media[0])[0]; var advanced = GroupBatch(media[1])[0];
            QueueState(oldCurrent, advanced); SetField("interactiveActive", true); SetField("interactivePendingSource", Get(oldCurrent, "InputSummary"));
            int statusVersion = (int)Field(owner, "interactiveRuntimeVersion"), statusRevision = (int)Field(owner, "interactiveStatusRevision");
            string delayedStatus = BackendStatus(oldCurrent, "playing", advanced);
            ((Task)Invoke(owner, "HandleInteractiveEndedAsync", Get(oldCurrent, "InputSummary"))).GetAwaiter().GetResult();
            Check(!((Task<bool>)Invoke(owner, "ReconcileInteractiveBackendStatusAsync", delayedStatus, statusVersion, statusRevision, true)).GetAwaiter().GetResult() && Field(owner, "interactiveCurrent") == advanced,
                "A STATUS response captured before a confirmed transition cannot roll back or invalidate the newer playing state");

            var same = GroupBatch(media[0]); var repeat = GroupBatch(media[0]);
            QueueState(same[0], repeat[0]); SetField("interactiveActive", true); SetField("interactiveNeedsStatusCheck", true);
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(same[0], "playing", repeat[0])) && Field(owner, "interactiveCurrent") == same[0],
                "A file-summary match alone cannot advance between identical consecutive files");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(same[0], "ended", same[0])) && Field(owner, "interactiveCurrent") == same[0],
                "An ended state starts the mismatch timeout even if its stale actual_input matches");
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); SynchronizationContext.SetSynchronizationContext(context); }
        Console.WriteLine("PASS: actual-output verification preserves paused/current playback, distinguishes decoder lookahead, restores missing next commands, recovers handoffs and handles inconclusive/terminal status.");
    }
    private static void CheckPlaybackWatchdog()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "PreventInterrupt", true);
            var batch = GroupBatch(group);
            QueueState(batch[0], batch[1], batch[2]); SetField("interactiveActive", true);
            SetField("interactivePendingSource", Get(batch[0], "InputSummary"));
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(GroupBatch(media[2])[0]));
            var commands = new List<string>();
            Func<string, Task<string>> failedSend = command => { commands.Add(command); return Task.FromResult("ERR unavailable"); };
            Func<string, Task<string>> acceptedSend = command => { commands.Add(command); return Task.FromResult("OK queued"); };
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(GroupBatch(media[3])[0], "playing", null));
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(commands.Count == 0 && Field(owner, "interactiveCurrent") == batch[0], "A brief mismatch does not interrupt protected playback");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[0], "paused", batch[1]));
            Check((DateTime)Field(owner, "interactiveMismatchSince") == DateTime.MinValue, "Verified paused output resets the timeout");
            Invoke(owner, "ApplyInteractiveBackendStatus", "ERR timeout");
            Check(DateTime.UtcNow - (DateTime)Field(owner, "interactiveMismatchSince") < TimeSpan.FromSeconds(1), "A later query failure starts a fresh continuous mismatch interval");
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(commands.Count == 0, "Nonconsecutive mismatches cannot accumulate into a timeout");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-9));
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(commands.Count == 0 && Field(owner, "interactiveCurrent") == batch[0], "A continuous nine-second mismatch cannot advance before the ten-second timeout");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(commands.SequenceEqual(new[] { "PLAY g2.mp4" }) && Field(owner, "interactiveCurrent") == batch[1] && Field(owner, "interactiveRecoveryEntry") == batch[1],
                "Persistent mismatch advances to the pending file even without disconnect notification and bypasses stale protection");
            Check(QueuedEntries().Length == 2 && QueuedEntries()[0] == batch[2] && !HandoffTask(handoff).IsCompleted, "A failed recovery preserves the protected group tail and waiting trigger");
            SetField("interactiveRecoveryRetryAt", DateTime.MinValue);
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(commands.Count == 2 && commands.All(c => c == "PLAY g2.mp4") && QueuedEntries()[0] == batch[2], "Offline retries always target the selected next file without consuming the queue");
            SetField("interactiveRecoveryRetryAt", DateTime.MinValue);
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", acceptedSend)).GetAwaiter().GetResult();
            Check((bool)Field(owner, "interactiveNeedsStatusCheck") && Field(owner, "interactiveRecoveryEntry") == batch[1], "PLAY acknowledgement alone does not release the recovery target");
            Invoke(owner, "ApplyInteractiveBackendStatus", "OK seconds=0 state=opening actual_input= input=");
            SetField("interactiveRecoveryRetryAt", DateTime.MinValue);
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", acceptedSend)).GetAwaiter().GetResult();
            Check(commands.Count == 4 && commands.All(c => c == "PLAY g2.mp4"), "Even an acknowledged command with no matching output retries the same target");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[1], "playing", batch[1]));
            Check(Field(owner, "interactiveRecoveryEntry") == null && !(bool)Field(owner, "interactiveNeedsStatusCheck"), "Actual output confirmation resumes normal queue processing");
            Invoke(owner, "ClearInteractiveRuntime", true);

            // A replacement selected from a queued trigger retains the remaining files of that trigger.
            var first = GroupBatch(media[0])[0]; var next = GroupBatch(media[2])[0]; var tail = GroupBatch(media[3])[0];
            QueueState(first, null); SetField("interactiveActive", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", ProtectedRequest(next, tail));
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(null, "stopped", null));
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            var pendingReply = new TaskCompletionSource<string>();
            Func<string, Task<string>> delayedSend = command => pendingReply.Task;
            var recovery = (Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", delayedSend);
            Check(Field(owner, "interactiveCurrent") == next && QueuedEntries().SequenceEqual(new[] { tail }), "A timeout can start the next queued trigger and preserve its sequence");
            Invoke(owner, "ClearInteractiveRuntime", true);
            pendingReply.SetResult("OK late"); recovery.GetAwaiter().GetResult();
            Check(!(bool)Field(owner, "interactiveActive") && Field(owner, "interactiveCurrent") == null && QueuedEntries().Length == 0, "A late recovery reply cannot restart playback after explicit stop");

            QueueState(first, null); SetField("interactiveActive", true);
            Invoke(owner, "ApplyInteractiveBackendStatus", "ERR unavailable");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            int before = commands.Count;
            ((Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", failedSend)).GetAwaiter().GetResult();
            Check(!(bool)Field(owner, "interactiveActive") && Field(owner, "interactiveCurrent") == null && commands.Count == before, "With no next item, timeout releases stale playback without issuing a phantom PLAY");
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); SynchronizationContext.SetSynchronizationContext(context); }
        Console.WriteLine("PASS: continuous playback watchdog resets on matches, advances after timeout, retains failed recovery targets and queue order, waits for actual output, and rejects late replies.");
    }
    private static string BackendStatus(object actual, string state, object decoded)
    {
        return "OK seconds=1 state=" + state + " actual_input=" + (actual == null ? "" : (string)Get(actual, "InputSummary")) +
            " input=" + (decoded == null ? "" : (string)Get(decoded, "InputSummary"));
    }
    private static async Task<T> CompleteSoon<T>(Task<T> task, string message)
    {
        Check(await Task.WhenAny(task, Task.Delay(3000)) == task, message);
        return await task;
    }
    private static async Task CompleteSoon(Task task, string message)
    {
        Check(await Task.WhenAny(task, Task.Delay(3000)) == task, message);
        await task;
    }
    private static Task<string> ClientSend(object client, int port, string command)
    {
        return (Task<string>)Invoke(client, "SendAsync", "127.0.0.1", port, command);
    }
    private static async Task CheckBackendRestartRecoveryAsync(TcpListener listener, int port)
    {
        int notifications = 0;
        var lost = new TaskCompletionSource<bool>();
        var eventGeneration = new TaskCompletionSource<int>();
        Action closed = () => { Interlocked.Increment(ref notifications); lost.TrySetResult(true); };
        Action<string, int> log = (text, generation) => { };
        Action<string, int> events = (text, generation) => eventGeneration.TrySetResult(generation);
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { log, events, closed });
        SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        TcpClient peer = null;
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var initial = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "The first local backend connection is established");
            var reader = new StreamReader(peer.GetStream());
            var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Backend receives STATUS") == "STATUS", "The first connection carries the requested command");
            await writer.WriteLineAsync("OK initial");
            Check(await CompleteSoon(initial, "Initial reply completes") == "OK initial", "Initial command receives its own reply");
            int oldGeneration = (int)Get(client, "Generation");
            var oldStream = Field(client, "stream");
            var oldReplies = Field(client, "pendingReplies");
            var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "PreventInterrupt", true);
            var batch = GroupBatch(group);
            QueueState(batch[0], batch[1], batch[2]); SetField("interactiveActive", true);
            SetField("interactiveLastEndedFrameId", 99999L);
            var next = GroupBatch(media[2])[0];
            var protectedWait = (Task)Invoke(owner, "AppendInteractiveRouteAsync", ProtectedRequest(next));
            Check(!protectedWait.IsCompleted, "A protected group has a waiting project before the backend restart");
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", ProtectedRequest(next));
            int mediaCount = media.Count, routeCount = ((IList)Field(owner, "interactiveRoutes")).Count;
            var pending = ClientSend(client, port, "STATUS");
            await CompleteSoon(reader.ReadLineAsync(), "The backend receives a command before closing");
            peer.Close(); peer = null;
            Check((await CompleteSoon(pending, "Disconnect promptly releases pending commands")).StartsWith("ERR"), "A closed backend fails the old in-flight command");
            await CompleteSoon(lost.Task, "Disconnect notification is delivered even with no further command");
            Check((int)Get(client, "ConnectionLossVersion") == 1 && (int)Get(client, "Generation") != oldGeneration, "A real disconnect advances both loss and event generations");
            Invoke(owner, "ObserveBackendConnectionLoss");
            Check(!protectedWait.IsCompleted && (bool)Field(owner, "interactiveNeedsStatusCheck") && Field(owner, "interactiveCurrent") == batch[0] && Field(owner, "interactivePending") == batch[1],
                "Disconnect alone preserves the current item, pending item and protected handoff");
            Check(QueuedEntries().SequenceEqual(new[] { batch[2], next }) && ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Count() == 1,
                "Disconnect alone preserves playback and trigger queues");
            Check((long)Field(owner, "interactiveLastEndedFrameId") == 0, "A new backend session will not inherit old frame-ID comparisons");
            Check(media.Count == mediaCount && ((IList)Field(owner, "interactiveRoutes")).Count == routeCount, "Disconnect preserves media and project configuration");

            accept = listener.AcceptTcpClientAsync();
            var reconnectStatus = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "The backend accepts a fresh camera session");
            reader = new StreamReader(peer.GetStream()); writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Reconnected verification reaches the backend") == "STATUS", "Reconnection queries actual playback before deciding whether the old project stopped");
            string retainedStatus = BackendStatus(batch[0], "paused", batch[1]);
            await writer.WriteLineAsync(retainedStatus);
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", await CompleteSoon(reconnectStatus, "Reconnected status query completes")) && Field(owner, "interactiveCurrent") == batch[0] && !protectedWait.IsCompleted,
                "Actual output still on the protected item preserves the project and its waiting trigger");
            Check(!(bool)Field(owner, "interactiveNeedsStatusCheck") && Field(owner, "interactivePending") == batch[1], "Paused output is valid playback and decoder lookahead is preserved");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", "ERR unavailable") && Field(owner, "interactiveCurrent") == batch[0], "A failed query cannot erase the playing item");
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[1], "playing", batch[1])) && Field(owner, "interactiveCurrent") == batch[1], "STATUS can recover a missed transition to the prefetched file");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", "OK seconds=0 state=stopped actual_input= input="), "A stopped status starts continuous mismatch monitoring");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            Func<string, Task<string>> recoveredSend = command => Task.FromResult("OK recovered");
            await (Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", recoveredSend);
            Check(Field(owner, "interactiveCurrent") == batch[2] && !protectedWait.IsCompleted, "A timeout advances to the next member without discarding the protected group or its waiting trigger");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(batch[2], "playing", batch[2]));
            Invoke(owner, "ApplyInteractiveBackendStatus", "OK seconds=0 state=stopped actual_input= input=");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            await (Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", recoveredSend);
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));
            await CompleteSoon(protectedWait, "Verified replacement releases the protected wait");
            var retainedRequests = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
            Check(retainedRequests.Length == 1, "Later triggers remain queued while the original waiting request is recovered");
            Invoke(owner, "ClearInteractiveRuntime", true);
            var playing = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(next));
            Check((await CompleteSoon(reader.ReadLineAsync(), "A trigger reaches the verified backend")).StartsWith("PLAY A2.mp4"), "A fresh trigger sends PLAY after confirmed stop");
            await writer.WriteLineAsync("OK playing"); await CompleteSoon(playing, "A project starts after verification");
            int newGeneration = (int)Get(client, "Generation");
            Check(newGeneration > oldGeneration && Field(owner, "interactiveCurrent") == next, "Reconnection establishes a fresh generation and a new current item");
            Invoke(owner, "ObserveBackendConnectionLoss");
            Check(Field(owner, "interactiveCurrent") == next, "A late duplicate disconnect notification cannot clear the new playback");
            Invoke(owner, "HandleBackendEvent", "INPUT_ENDED frame_id=99999 input=" + Get(next, "InputSummary"), oldGeneration);
            Check(Field(owner, "interactiveCurrent") == next, "Old-connection events are ignored after restart");

            var freshReply = ClientSend(client, port, "STATUS");
            await CompleteSoon(reader.ReadLineAsync(), "A new-session reply is pending");
            await CompleteSoon((Task)Invoke(client, "ReadLoopAsync", oldStream, new CancellationToken(true), oldGeneration, oldReplies), "Old reader cleanup finishes");
            Check(!freshReply.IsCompleted && (int)Get(client, "Generation") == newGeneration, "An old reader cannot drain the new session's pending replies or close its connection");
            await writer.WriteLineAsync("EVENT INPUT_ENDED frame_id=1 input=new");
            Check(await CompleteSoon(eventGeneration.Task, "New backend events arrive") == newGeneration, "Events carry their TCP session generation");
            await writer.WriteLineAsync("OK resumed");
            Check(await CompleteSoon(freshReply, "The new session receives its reply") == "OK resumed", "New replies are isolated from old reader cleanup");
            Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=1 input=" + Get(next, "InputSummary"));
            Check(!(bool)Field(owner, "interactiveActive"), "Small frame IDs from a restarted backend are accepted after reset");

            lost = new TaskCompletionSource<bool>();
            var interruptedPlay = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(GroupBatch(media[0])[0]));
            Check((await CompleteSoon(reader.ReadLineAsync(), "A PLAY command is in flight before another restart")).StartsWith("PLAY A.mp4"), "The restart test includes an immediate PLAY awaiting acknowledgement");
            peer.Close(); peer = null;
            await CompleteSoon(interruptedPlay, "An interrupted PLAY releases without a null-state exception or stale update");
            await CompleteSoon(lost.Task, "The second restart is detected");
            Check((bool)Field(owner, "interactiveNeedsStatusCheck") && Field(owner, "interactiveCurrent") != null && (int)Get(client, "ConnectionLossVersion") == 2,
                "Disconnect while PLAY awaits its reply leaves playback unverified instead of claiming it stopped");

            accept = listener.AcceptTcpClientAsync();
            reconnectStatus = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Playback can connect again after repeated restarts");
            reader = new StreamReader(peer.GetStream()); writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            await CompleteSoon(reader.ReadLineAsync(), "Repeated restart queries actual status again");
            await writer.WriteLineAsync("OK seconds=0 state=stopped actual_input= input=");
            Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", await CompleteSoon(reconnectStatus, "Repeated restart status is received")) && Field(owner, "interactiveCurrent") != null,
                "Stopped output preserves the interrupted PLAY snapshot until the mismatch timeout");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            await (Task)Invoke(owner, "RecoverInteractivePlaybackCoreAsync", recoveredSend);
            Check(Field(owner, "interactiveCurrent") == null, "A timed-out current item with no successor releases stale playback state");
            playing = (Task)Invoke(owner, "StartInteractiveImmediatelyAsync", ProtectedRequest(next));
            await CompleteSoon(reader.ReadLineAsync(), "Another fresh PLAY reaches the verified backend");
            await writer.WriteLineAsync("OK playing again"); await CompleteSoon(playing, "Playback starts after repeated restarts");
            Check(Field(owner, "interactiveCurrent") == next && notifications == 2, "Repeated restart recovery leaves the controller able to play new projects");

            var protectedFile = GroupBatch(media[0])[0]; Set(protectedFile, "PreventInterrupt", true);
            QueueState(protectedFile, null); SetField("interactiveActive", true);
            var request = ProtectedRequest(next); Set(request, "Immediate", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
            var dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "Trigger first queries actual playback") == "STATUS", "Trigger decisions query backend output before applying an old interrupt-protection flag");
            await writer.WriteLineAsync(BackendStatus(next, "playing", next));
            await CompleteSoon(dispatch, "An unmatched output defers the trigger until timeout");
            Check(Field(owner, "interactiveCurrent") == protectedFile, "A transient unmatched output does not immediately bypass protection");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            var recovery = (Task)Invoke(owner, "RecoverInteractivePlaybackAsync");
            Check((await CompleteSoon(reader.ReadLineAsync(), "A persistent mismatch releases stale protection")).StartsWith("PLAY A2.mp4"), "A project that is no longer playing cannot block the queued trigger after timeout");
            await writer.WriteLineAsync("OK started after verification");
            await CompleteSoon(recovery, "The queued trigger starts after timeout");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));

            QueueState(protectedFile, null); SetField("interactiveActive", true);
            request = ProtectedRequest(next); Set(request, "Immediate", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
            dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "A protected trigger queries actual playback") == "STATUS", "The check also runs when protection should remain in force");
            await writer.WriteLineAsync(BackendStatus(protectedFile, "paused", protectedFile));
            Check((await CompleteSoon(reader.ReadLineAsync(), "Verified protected playback defers the trigger")).StartsWith("PLAY_NEXT A2.mp4"), "A still-playing or paused protected input correctly uses PLAY_NEXT");
            await writer.WriteLineAsync("OK next queued");
            Check(!dispatch.IsCompleted, "The verified protected trigger waits for actual completion");
            Invoke(owner, "ApplyInteractiveBackendStatus", "OK seconds=0 state=stopped actual_input= input=");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            recovery = (Task)Invoke(owner, "RecoverInteractivePlaybackAsync");
            Check((await CompleteSoon(reader.ReadLineAsync(), "Timed-out stop releases the waiting trigger")).StartsWith("PLAY A2.mp4"), "Persistent stopped output starts the already requested project without an end event");
            await writer.WriteLineAsync("OK recovered waiting request");
            await CompleteSoon(recovery, "The replacement PLAY is acknowledged");
            Check(!dispatch.IsCompleted, "PLAY acknowledgement waits for actual output before releasing protection");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));
            await CompleteSoon(dispatch, "The waiting request survives verification and starts");
            Check(Field(owner, "interactiveCurrent") == next, "Actual-state reconciliation preserves and completes the queued user request");

            // Replacing at a boundary must keep a replay pending until its exact output batch starts.
            foreach (int targetIndex in new[] { 0, 1, 2 })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var boundaryCurrent = GroupBatch(media[0])[0]; Set(boundaryCurrent, "PreventInterrupt", true); Set(boundaryCurrent, "BackendFrameId", 100L);
                var oldStandby = GroupBatch(media[1])[0]; Set(oldStandby, "BackendFrameId", 200L);
                var replay = GroupBatch(media[targetIndex])[0];
                var newStandby = GroupBatch(media[3])[0];
                QueueState(boundaryCurrent, oldStandby); SetField("interactiveActive", true);
                SetField("interactivePendingSource", Get(boundaryCurrent, "InputSummary"));
                var boundaryHandoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(replay, newStandby));
                var queueReplay = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                string replaceCommand = await CompleteSoon(reader.ReadLineAsync(), "Boundary replacement reaches backend");
                Check(replaceCommand.StartsWith("PLAY_NEXT " + Get(replay, "Path") + "\tafter_frame_id=100"),
                    "Single PLAY_NEXT identifies the protected batch so already-prefetched standby can be replaced atomically");
                await writer.WriteLineAsync("OK"); await CompleteSoon(queueReplay, "Boundary replacement acknowledged");
                Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + Get(boundaryCurrent, "InputSummary"));
                await CompleteSoon((Task)Field(owner, "interactiveBackendEventTask"), "Old source end is processed");
                Check(Field(owner, "interactiveCurrent") == boundaryCurrent && Field(owner, "interactivePending") == replay && !HandoffTask(boundaryHandoff).IsCompleted,
                    "Source end alone cannot announce the replay or prefetch its successor");
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(oldStandby, "playing", replay) + " actual_frame_id=200 input_frame_id=300"),
                    "The superseded standby cannot advance past the pending replay");
                Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_STARTED frame_id=300 input=" + Get(replay, "InputSummary"));
                var startEvent = (Task)Field(owner, "interactiveBackendEventTask");
                Check((await CompleteSoon(reader.ReadLineAsync(), "Successor follows actual replay output")).StartsWith("PLAY_NEXT " + Get(newStandby, "Path")),
                    "The existing single-next command queues the successor only after the replay actually starts");
                await writer.WriteLineAsync("OK"); await CompleteSoon(startEvent, "Actual-start event completes");
                Check(Field(owner, "interactiveCurrent") == replay && (long)Get(replay, "BackendFrameId") == 300L && HandoffTask(boundaryHandoff).IsCompleted,
                    "Actual-start notification confirms repeated or different-file replacements without a ten-second recovery wait");
                Invoke(owner, "HandleInteractiveBackendEvent", "INPUT_ENDED frame_id=100 input=" + Get(boundaryCurrent, "InputSummary"));
                await CompleteSoon((Task)Field(owner, "interactiveBackendEventTask"), "Delayed old end processed");
                Check(Field(owner, "interactiveCurrent") == replay && Field(owner, "interactivePending") == newStandby,
                    "A delayed duplicate end cannot skip the confirmed replay");
            }

            Invoke(owner, "ClearInteractiveRuntime", true);
            protectedFile = GroupBatch(media[0])[0]; Set(protectedFile, "PreventInterrupt", true); Set(protectedFile, "BackendFrameId", 100L);
            var actualStandby = GroupBatch(media[1])[0]; Set(actualStandby, "BackendFrameId", 200L);
            next = GroupBatch(media[2])[0];
            QueueState(protectedFile, actualStandby); SetField("interactiveActive", true);
            request = ProtectedRequest(next); Set(request, "Immediate", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
            dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "Boundary trigger verifies output") == "STATUS", "Trigger still reuses backend STATUS protocol");
            await writer.WriteLineAsync(BackendStatus(actualStandby, "playing", actualStandby) + " actual_frame_id=200 input_frame_id=200");
            Check((await CompleteSoon(reader.ReadLineAsync(), "Finished protection switches to PLAY")).StartsWith("PLAY " + Get(next, "Path")),
                "When standby is already actually playing, stale protected state cannot force PLAY_NEXT");
            await writer.WriteLineAsync("OK"); await CompleteSoon(dispatch, "Trigger starts immediately after status synchronization");

            // Exercise the real polling path, including a generation change during the STATUS query.
            var retainedTail = GroupBatch(media[3])[0];
            QueueState(protectedFile, next, retainedTail); SetField("interactiveActive", true);
            Invoke(owner, "ApplyInteractiveBackendStatus", "ERR unavailable");
            SetField("interactiveMismatchSince", DateTime.UtcNow.AddSeconds(-11));
            var poll = (Task)Invoke(owner, "RefreshStatusAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "The periodic monitor requests STATUS") == "STATUS", "Continuous monitoring uses actual backend status without a trigger");
            accept = listener.AcceptTcpClientAsync();
            peer.Close(); peer = null;
            peer = await CompleteSoon(accept, "A failed status query can reconnect for recovery");
            reader = new StreamReader(peer.GetStream()); writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check((await CompleteSoon(reader.ReadLineAsync(), "The status watchdog starts the selected next file")).StartsWith("PLAY A2.mp4"), "A connection generation change cannot disable the timeout watchdog");
            await writer.WriteLineAsync("OK recovered by periodic monitor");
            await CompleteSoon(poll, "Periodic recovery completes without a trigger");
            Check(Field(owner, "interactiveCurrent") == next && Field(owner, "interactiveRecoveryEntry") == next && QueuedEntries().SequenceEqual(new[] { retainedTail }),
                "Periodic recovery retains its selected target and subsequent queue until output is verified");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));

            // Closing must invalidate late replies and clear the backend slot after an in-flight queue write.
            Invoke(owner, "ClearInteractiveRuntime", true);
            QueueState(protectedFile, null, next, retainedTail); SetField("interactiveActive", true);
            var sendingNext = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
            Check((await CompleteSoon(reader.ReadLineAsync(), "A next command is in flight before closing")).StartsWith("PLAY_NEXT "), "Close test includes an unacknowledged next command");
            Invoke(owner, "SetInteractiveWindowOpen", false);
            Check(!(bool)Field(owner, "interactiveActive") && Field(owner, "interactiveCurrent") == null && Field(owner, "interactivePending") == null && QueuedEntries().Length == 0,
                "Closing immediately clears runtime and queued interactive items");
            var cleanup = (Task)Field(owner, "interactiveCleanupTask");
            var clearCommand = reader.ReadLineAsync();
            await Task.Delay(30);
            Check(!clearCommand.IsCompleted, "Cleanup waits for the older queue write before clearing its destination");
            await writer.WriteLineAsync("OK delayed next");
            await CompleteSoon(sendingNext, "The old next acknowledgement finishes");
            Check(await CompleteSoon(clearCommand, "Closing clears the backend next slot") == "PLAY_NEXT", "Close clears the single pending slot without stopping current output");
            Check(Field(owner, "interactivePending") == null, "A late next acknowledgement cannot restore closed playback");

            Invoke(owner, "SetInteractiveWindowOpen", true);
            request = ProtectedRequest(next); Set(request, "Immediate", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
            dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            var reopenedPlay = reader.ReadLineAsync();
            await Task.Delay(30);
            Check(!reopenedPlay.IsCompleted, "A reopened session waits for the previous close cleanup");
            await writer.WriteLineAsync("OK cleared");
            await CompleteSoon(cleanup, "Close cleanup completes");
            Check((await CompleteSoon(reopenedPlay, "The reopened session can start a project")).StartsWith("PLAY " + Get(next, "Path")), "Reopened playback starts after cleanup and cannot be erased by an old clear command");
            await writer.WriteLineAsync("OK reopened"); await CompleteSoon(dispatch, "Reopened trigger completes");

            QueueState(protectedFile, null); SetField("interactiveActive", true);
            request = ProtectedRequest(next); Set(request, "Immediate", true);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", request);
            dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "An old session is awaiting STATUS") == "STATUS", "Close test includes pending trigger verification");
            Invoke(owner, "SetInteractiveWindowOpen", false);
            cleanup = (Task)Field(owner, "interactiveCleanupTask");
            Check(await CompleteSoon(reader.ReadLineAsync(), "Closing during verification clears next") == "PLAY_NEXT", "Closing invalidates pending verification");
            await writer.WriteLineAsync(BackendStatus(protectedFile, "playing", protectedFile));
            await writer.WriteLineAsync("OK cleared after verification");
            await CompleteSoon(dispatch, "Late verification finishes without a trigger");
            await CompleteSoon(cleanup, "Final close cleanup completes");
            Check(!(bool)Field(owner, "interactiveActive") && Field(owner, "interactiveCurrent") == null &&
                !((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Any(), "Late STATUS cannot restore or requeue a closed session's trigger");
            Invoke(owner, "SetInteractiveWindowOpen", true);
            Console.WriteLine("PASS: closing cancels runtime and late STATUS/next acknowledgements; backend queue cleanup precedes reopened triggers.");
        }
        finally
        {
            ((IDisposable)client).Dispose(); if (peer != null) peer.Close();
            SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
            Invoke(owner, "ClearInteractiveRuntime", true);
        }
    }
    private static void CheckBackendRestartRecovery()
    {
        var context = SynchronizationContext.Current;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox);
            SynchronizationContext.SetSynchronizationContext(null);
            try { CheckBackendRestartRecoveryAsync(listener, port).GetAwaiter().GetResult(); }
            finally
            {
                listener.Stop(); SetField("hostBox", null); SetField("portBox", null);
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }
        Console.WriteLine("PASS: real TCP reconnection preserves queues until actual-output verification, recovers missed transitions, retains waiting triggers, isolates old readers and allows replay.");
    }
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private static void CheckInteractiveDisplayOrder()
    {
        var routes = (IList)Field(owner, "interactiveRoutes");
        object[] originalMedia = media.Cast<object>().ToArray(), originalRoutes = routes.Cast<object>().ToArray();
        try
        {
            routes.Clear();
            for (int i = 0; i < 3; i++)
            {
                var route = Route("sequential"); Set(route, "Name", "Display route " + i); routes.Add(route);
            }
            object firstRoute = routes[0];
            Set(firstRoute, "NextCombinationIndex", 1);
            string routeContents = Save(firstRoute).ToString();
            var current = GroupBatch(media[0])[0]; var pending = GroupBatch(media[1])[0]; var queued = GroupBatch(media[2])[0];
            QueueState(current, pending, queued); SetField("interactiveActive", true);
            int runtimeVersion = (int)Field(owner, "interactiveRuntimeVersion");
            using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
            {
                Invoke(window, "RefreshMedia"); Invoke(window, "RefreshRoutes");
                var mediaView = (ListView)Field(window, "mediaView"); var routeView = (ListView)Field(window, "routeView");
                IntPtr unused = mediaView.Handle; unused = routeView.Handle;
                mediaView.Items[0].Selected = true;
                Invoke(window, "MoveSelectedMedia", 1);
                Check(media[1] == originalMedia[0] && mediaView.SelectedItems[0].Tag == originalMedia[0], "Moving media keeps its identity and selection");
                Invoke(window, "MoveSelectedMedia", -1); Invoke(window, "MoveSelectedMedia", -1);
                Check(media.Cast<object>().SequenceEqual(originalMedia), "Moving back and moving above the first item preserve media order");
                Invoke(window, "MoveSelectedMedia", 1);

                var hotkeys = (IDictionary)Field(window, "registeredRoutes");
                hotkeys.Add(0x5100, firstRoute); hotkeys.Add(0x5101, routes[1]);
                routeView.Items[0].Selected = true;
                Invoke(window, "MoveSelectedRoute", 1); Invoke(window, "MoveSelectedRoute", 1);
                Invoke(window, "MoveSelectedRoute", 1);
                Check(routes[2] == firstRoute && routeView.SelectedItems[0].Tag == firstRoute, "Moving a project to the bottom keeps selection and ignores a move beyond the list");
                Invoke(window, "MoveSelectedRoute", -1);
                Check(routes[1] == firstRoute && hotkeys.Count == 2 && hotkeys[0x5100] == firstRoute,
                    "Display reordering retains the existing shortcut-to-project mapping");
                var buttons = (FlowLayoutPanel)Field(window, "routeButtons");
                Check(buttons.Controls.Cast<Control>().Select(button => button.Text.Split('[')[0].Trim()).SequenceEqual(routes.Cast<object>().Select(route => (string)Get(route, "Name"))),
                    "Project trigger buttons follow the reordered list");
                Check(Save(firstRoute).ToString() == routeContents && (int)Get(firstRoute, "NextCombinationIndex") == 1,
                    "Sorting cannot modify combinations, media references or sequential-selection progress");
                Check(Field(owner, "interactiveCurrent") == current && Field(owner, "interactivePending") == pending && QueuedEntries().SequenceEqual(new[] { queued }) &&
                    (bool)Field(owner, "interactiveActive") && (int)Field(owner, "interactiveRuntimeVersion") == runtimeVersion,
                    "Sorting during playback preserves current, pending and queued playback snapshots");
            }

            var state = New("ConnectionTabState");
            foreach (object item in media) ((IList)Get(state, "InteractiveMedia")).Add(item);
            foreach (object route in routes) ((IList)Get(state, "InteractiveRoutes")).Add(route);
            XElement saved = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            Guid[] mediaOrder = media.Cast<object>().Select(item => (Guid)Get(item, "RuntimeId")).ToArray();
            string[] routeOrder = routes.Cast<object>().Select(route => Save(route).ToString()).ToArray();
            Invoke(owner, "LoadInteractivePlaybackElement", XElement.Parse(saved.ToString()));
            Check(media.Cast<object>().Select(item => (Guid)Get(item, "RuntimeId")).SequenceEqual(mediaOrder) &&
                routes.Cast<object>().Select(route => Save(route).ToString()).SequenceEqual(routeOrder),
                "Connection XML round trip retains both display orders and original media references");
        }
        finally
        {
            media.Clear(); foreach (object item in originalMedia) media.Add(item);
            routes.Clear(); foreach (object route in originalRoutes) routes.Add(route);
            Invoke(owner, "ClearInteractiveRuntime", true);
        }
        Console.WriteLine("PASS: media/project display sorting preserves shortcuts, combinations, selection progress, active playback and saved XML order.");
    }

    private static void CheckInteractiveWindowHotkeys()
    {
        var routes = (IList)Field(owner, "interactiveRoutes");
        var route = Route("sequential");
        var hotkey = New("InteractiveHotkey");
        Set(hotkey, "KeyCode", Keys.F24);
        Set(hotkey, "Modifiers", Enum.ToObject(Nested("HotkeyModifiers"), 7));
        Set(route, "Hotkey", hotkey);
        routes.Add(route);
        SetField("interactiveWindowOpen", false);
        Form window = null;
        using (var probe = new Form())
        {
            IntPtr probeHandle = probe.Handle;
            const int probeId = 0x6201;
            Check(RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24), "Lifecycle test hotkey is available");
            UnregisterHotKey(probeHandle, probeId);
            try
            {
                Invoke(owner, "BuildInteractiveWindow");
                window = (Form)Field(owner, "interactiveForm");
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new System.Drawing.Point(-32000, -32000);
                window.ShowInTaskbar = false;
                IntPtr unused = window.Handle;
                Invoke(window, "ReloadAll");
                Check(!window.Visible && !(bool)Field(owner, "interactiveWindowOpen") &&
                    ((IDictionary)Field(window, "registeredRoutes")).Count == 0, "Creating/reloading a hidden interactive window does not activate hotkeys");
                Check(RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24), "Hidden window leaves the global shortcut available");
                UnregisterHotKey(probeHandle, probeId);
                int selection = (int)Get(route, "NextCombinationIndex");
                Invoke(owner, "RequestInteractiveRoute", route, "hidden test");
                Check((int)Get(route, "NextCombinationIndex") == selection && !(bool)Field(owner, "interactiveActive"), "Hidden-window triggers cannot select or start a project");

                window.Show();
                Check((bool)Field(owner, "interactiveWindowOpen") && ((IDictionary)Field(window, "registeredRoutes")).Count > 0,
                    "Showing the interactive window enables route shortcuts");
                Check(!RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24), "Visible interactive window actually owns its global shortcut");
                window.WindowState = FormWindowState.Minimized;
                Check((bool)Field(owner, "interactiveWindowOpen") && !RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24),
                    "Minimized interactive window remains enabled");
                window.Close();
                Check(!window.Visible && !window.IsDisposed && !(bool)Field(owner, "interactiveWindowOpen") &&
                    ((IDictionary)Field(window, "registeredRoutes")).Count == 0, "Closing the reusable window disables interaction and unregisters all shortcuts");
                Check(RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24), "Closing releases the OS shortcut for other applications");
                UnregisterHotKey(probeHandle, probeId);
                Invoke(window, "ReloadAll");
                Check(((IDictionary)Field(window, "registeredRoutes")).Count == 0, "Configuration reload cannot reactivate shortcuts while closed");
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x5100, IntPtr.Zero));
                Check(!(bool)Field(owner, "interactiveActive"), "A late queued hotkey message cannot restart closed interaction");

                window.WindowState = FormWindowState.Normal;
                Invoke(owner, "OpenInteractiveWindow");
                Check(window.Visible && !RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24), "Reopening registers shortcuts again");
                window.Hide();
                Check(!(bool)Field(owner, "interactiveWindowOpen") && RegisterHotKey(probeHandle, probeId, 7, (uint)Keys.F24),
                    "Hiding also releases shortcuts and disables interaction");
                UnregisterHotKey(probeHandle, probeId);
            }
            finally
            {
                UnregisterHotKey(probeHandle, probeId);
                if (window != null) window.Dispose();
                SetField("interactiveForm", null);
                routes.Remove(route);
                SetField("interactiveWindowOpen", true); // Remaining playback checks simulate an open interactive window.
            }
        }
        Console.WriteLine("PASS: real Windows hotkeys follow show/close/hide/reopen, remain active while minimized, and ignore hidden reloads and late hotkey messages.");
    }

    private static void CheckEditor(Assembly assembly)
    {
        var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
        foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var original = Route("sequential");
            string before = Save(original).ToString();
            using (var dialog = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, original }))
            {
                Check(((ComboBox)Field(dialog, "selectionBox")).SelectedIndex == 1, "Editor loads selection mode");
                dialog.Size = dialog.MinimumSize;
                dialog.PerformLayout();
                Check(((ListBox)Field(dialog, "sequenceBox")).Height > 100 && ((ListBox)Field(dialog, "combinationsBox")).Width > 150,
                    "Combination and media editors remain usable at minimum window size");
                Invoke(dialog, "AddCombination", true);
                Check(((IList)Field(dialog, "combinations")).Count == 4, "Duplicate creates a combination");
                ((ComboBox)Field(dialog, "mediaBox")).SelectedIndex = 3;
                Invoke(dialog, "AddSelectedMedia");
                Check(Save(original).ToString() == before, "Editing/cancelling cannot mutate the saved project");
                Invoke(dialog, "MoveCombination", -1);
                Check(((ListBox)Field(dialog, "combinationsBox")).SelectedIndex == 2, "Combinations can be reordered");
                Invoke(dialog, "RemoveSequenceItem"); // No selection after changing combinations: safely ignored.
                Invoke(dialog, "RemoveCombination");
                Invoke(dialog, "AddCombination", false);
                Invoke(dialog, "AddSelectedMedia");
                Invoke(dialog, "Apply", original);
                Check(((IList)Get(original, "Combinations")).Count == 4, "Editor saves all combinations");
                Check(((IList)Get(((IList)Get(original, "Combinations"))[3], "MediaIds")).Count == 1, "Media editing applies to the selected combination");
                Check((int)Get(original, "NextCombinationIndex") == 0, "Saving changes resets the sequence position");
                Invoke(dialog, "AddSelectedMedia");
                Check(((IList)Get(((IList)Get(original, "Combinations"))[3], "MediaIds")).Count == 1, "Applied project does not share mutable editor lists");
                foreach (string key in new[] { "Up", "Down", "InteractiveCombinationSelection", "InteractiveRandom", "InteractiveSequential", "InteractiveCombinations", "InteractiveAddCombination", "InteractiveDuplicateCombination", "InteractiveEmptyCombination",
                    "InteractiveAddFileGroup", "InteractiveEditFileGroup", "InteractiveFileGroupTag", "InteractiveFileGroupPlayback", "InteractiveFileGroupNoRepeat",
                    "InteractiveFileGroupHint", "InteractiveFileGroupFiles", "InteractiveFileGroupRequired", "InteractiveFileGroupSummary",
                    "InteractiveFileGroupScope", "InteractiveFileGroupOne", "InteractiveFileGroupAll",
                    "InteractivePreventInterrupt", "InteractiveToggleProtection", "InteractiveWaitingProtected", "InteractiveBackendDisconnected", "InteractiveVerifyingPlayback", "InteractivePlaybackVerified", "InteractivePlaybackAutoNext", "InteractivePlaybackRetry", "InteractivePlaybackNoNext" })
                {
                    string value = resources.GetString(key, CultureInfo.CurrentUICulture);
                    Check(!string.IsNullOrWhiteSpace(value) && !value.Contains("?"), "Translated text exists: " + language + " " + key);
                    if (language != "en") Check(value.Any(c => c >= '\u4e00' && c <= '\u9fff'), "Chinese resources contain translated text");
                }
            }
        }
        Console.WriteLine("PASS: editor add/duplicate/reorder/remove, cancellation isolation, independent saved lists and three-language resources.");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            main = assembly.GetType("OmniVCamController.MainForm", true);
            // Avoid loading user configuration, contacting a camera or starting application timers.
            // Network recovery checks use only a temporary local TCP fixture.
            owner = FormatterServices.GetUninitializedObject(main);
            media = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveMediaItem")));
            foreach (string title in new[] { "A", "B", "A2", "B2" })
            {
                var item = New("InteractiveMediaItem"); Set(item, "Title", title); Set(item, "Path", title + ".mp4"); media.Add(item);
            }
            main.GetField("interactiveMedia", PrivateInstance).SetValue(owner, media);
            main.GetField("interactiveRoutes", PrivateInstance).SetValue(owner, Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveRouteItem"))));
            main.GetField("random", PrivateInstance).SetValue(owner, new Random(17));
            main.GetField("interactiveQueue", PrivateInstance).SetValue(owner, Activator.CreateInstance(typeof(Queue<>).MakeGenericType(Nested("InteractivePlaybackEntry"))));
            main.GetField("interactiveTriggerRequests", PrivateInstance).SetValue(owner, Activator.CreateInstance(typeof(Queue<>).MakeGenericType(Nested("InteractiveTriggerRequest"))));
            SetField("interactiveBackendEvents", Activator.CreateInstance(typeof(Queue<>).MakeGenericType(Nested("InteractiveBackendEvent"))));
            SetField("backendInputEvents", Activator.CreateInstance(typeof(Queue<>).MakeGenericType(Nested("BackendInputEvent"))));
            SetField("pendingScheduledObsEnds", Activator.CreateInstance(typeof(Queue<>).MakeGenericType(Nested("ScheduledObsAction"))));
            CheckInteractiveWindowHotkeys();
            CheckSelection();
            CheckEditor(assembly);
            CheckFileGroups(assembly);
            CheckGroupLooping();
            CheckGroupQueueCommands();
            CheckPlaybackDuringConfigurationEdits();
            CheckInteractiveDisplayOrder();
            CheckInteractiveEventOrdering();
            CheckInterruptProtection();
            CheckActualOutputVerification();
            CheckPlaybackWatchdog();
            CheckBackendRestartRecovery();
            CheckPersistence();
            Console.WriteLine("All interactive combination checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
