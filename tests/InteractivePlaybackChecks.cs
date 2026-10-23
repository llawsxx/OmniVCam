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

internal static partial class InteractivePlaybackChecks
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
    private static string EntriesAt(object route, long timestamp)
    {
        return string.Join(",", ((IEnumerable)Invoke(owner, "BuildInteractiveEntriesAt", route, timestamp)).Cast<object>().Select(entry => (string)Get(entry, "Title")));
    }
    private static void CheckRandomSelectionPolicies()
    {
        object originalRandom = Field(owner, "random"), originalLog = Field(owner, "pendingLogText"), originalFlush = Field(owner, "lastLogFlushAt");
        SetField("random", new Random(12345)); SetField("pendingLogText", new System.Text.StringBuilder()); SetField("lastLogFlushAt", DateTime.MaxValue);
        try
        {
            var weighted = Route("random"); var combinations = (IList)Get(weighted, "Combinations");
            Set(combinations[0], "Weight", 8);
            var counts = new Dictionary<string, int> { { "A,B", 0 }, { "A2,B", 0 }, { "A2,B2", 0 } };
            for (int i = 0; i < 12000; i++) counts[EntriesAt(weighted, 0)]++;
            Check(counts["A,B"] > 9200 && counts["A,B"] < 10000 && counts["A2,B"] > 900 && counts["A2,B2"] > 900,
                "Weighted selection follows relative probabilities without excluding rare combinations");
            var recent = Route("random"); Set(recent, "RecentCombinationCount", 2);
            var history = new Queue<string>();
            for (int i = 0; i < 120; i++)
            {
                string selected = EntriesAt(recent, 0);
                Check(selected.Length > 0 && !history.Contains(selected), "Recent-N exclusions prevent repeats across random draws");
                history.Enqueue(selected); if (history.Count > 2) history.Dequeue();
                Check(((IList)Get(recent, "RecentCombinations")).Count <= 2, "Runtime selection history stays bounded");
            }
            Set(recent, "RecentCombinationCount", 1000);
            for (int i = 0; i < 60; i++) Check(EntriesAt(recent, 0).Length > 0, "Excessive recent-N settings cannot permanently exhaust a small project");
            var one = Route("random"); ((IList)Get(one, "Combinations")).RemoveAt(2); ((IList)Get(one, "Combinations")).RemoveAt(1);
            Set(one, "RecentCombinationCount", 1000);
            Check(EntriesAt(one, 0) == "A,B" && EntriesAt(one, 0) == "A,B", "A single available combination can repeat regardless of recent-N");
            Check(((IList)Get(Route("random"), "RecentCombinations")).Count == 0, "Other projects have independent selection history");
            var cooling = Route("random"); combinations = (IList)Get(cooling, "Combinations");
            foreach (var combination in combinations) Set(combination, "CooldownSeconds", 10);
            var selectedRound = new HashSet<string>();
            for (int i = 0; i < 3; i++) Check(selectedRound.Add(EntriesAt(cooling, 0)), "Each selected variant enters its own cooldown immediately");
            long frequency = System.Diagnostics.Stopwatch.Frequency;
            Check(EntriesAt(cooling, 9 * frequency) == "" && (bool)Get(cooling, "RandomSelectionUnavailable"), "All-cooling projects skip a trigger before expiry");
            Check(EntriesAt(cooling, 10 * frequency).Length > 0, "A variant becomes eligible exactly at the cooldown boundary");
            foreach (var combination in combinations) Set(combination, "LastSelectedTimestamp", 0L);
            Set(combinations[0], "CooldownSeconds", 2);
            Check(EntriesAt(cooling, 2 * frequency) == "A,B" && EntriesAt(cooling, 2 * frequency) == "", "Independent cooldowns filter candidates before weighted selection");
            Set(cooling, "RecentCombinationCount", 1);
            Check(EntriesAt(cooling, 4 * frequency) == "A,B", "An accepted random draw enters recent history");
            Check(EntriesAt(cooling, 6 * frequency) == "", "Cooldown and recent exclusions are both respected when no candidate qualifies");
            Check(EntriesAt(cooling, 10 * frequency).Length > 0, "Combined restrictions recover when another variant's cooldown ends");
            var sequential = Route("sequential"); Set(sequential, "RecentCombinationCount", 1000);
            foreach (var combination in (IList)Get(sequential, "Combinations"))
            { Set(combination, "CooldownSeconds", 86400); Set(combination, "LastSelectedTimestamp", 0L); Set(combination, "Weight", 1000); }
            for (int i = 0; i < 9; i++) Check(EntriesAt(sequential, 0) == new[] { "A,B", "A2,B", "A2,B2" }[i % 3], "Sequential order ignores random-only restrictions");

            var group = FileGroup(); media.Add(group);
            try
            {
                var route = New("InteractiveRouteItem"); var combination = New("InteractiveMediaCombination");
                ((IList)Get(combination, "MediaIds")).Add(Get(group, "RuntimeId")); Set(combination, "CooldownSeconds", 5);
                ((IList)Get(route, "Combinations")).Add(combination);
                Check(EntriesAt(route, 0).Length > 0, "A group combination can be selected");
                int position = (int)Get(group, "NextFileIndex");
                Check(EntriesAt(route, frequency) == "" && (int)Get(group, "NextFileIndex") == position,
                    "A rejected trigger cannot consume file-group progress");
                Check(EntriesAt(route, 5 * frequency).Length > 0 && (int)Get(group, "NextFileIndex") != position,
                    "Group progress advances again only after cooldown expires");
            }
            finally { media.Remove(group); }
            var savedRoute = Route("random"); Set(savedRoute, "RecentCombinationCount", 2);
            combinations = (IList)Get(savedRoute, "Combinations"); Set(combinations[0], "Weight", 9); Set(combinations[0], "CooldownSeconds", 17);
            EntriesAt(savedRoute, 0);
            var saved = Save(savedRoute); var loaded = Load(XElement.Parse(saved.ToString()));
            Check(Save(loaded).ToString() == saved.ToString() && (int)Get(loaded, "RecentCombinationCount") == 2 &&
                (int)Get(((IList)Get(loaded, "Combinations"))[0], "Weight") == 9 && (int)Get(((IList)Get(loaded, "Combinations"))[0], "CooldownSeconds") == 17,
                "Weights, recent-N and per-combination cooldowns round-trip through project XML");
            Check(((IList)Get(loaded, "RecentCombinations")).Count == 0 && ((IList)Get(loaded, "Combinations")).Cast<object>().All(combination => Get(combination, "LastSelectedTimestamp") == null),
                "Reloading does not retain runtime cooldown or history");
            var copied = CopyProject(savedRoute);
            Check(Save(copied).ToString() == saved.ToString() && ((IList)Get(copied, "RecentCombinations")).Count == 0 &&
                ((IList)Get(copied, "Combinations")).Cast<object>().All(combination => Get(combination, "LastSelectedTimestamp") == null),
                "Copied projects preserve selection settings with independent runtime state");
            var invalid = XElement.Parse(saved.ToString()); invalid.SetAttributeValue("recentCount", -1);
            var first = invalid.Element("Combinations").Elements().First(); first.SetAttributeValue("weight", 0); first.SetAttributeValue("cooldownSeconds", -1);
            loaded = Load(invalid);
            Check((int)Get(loaded, "RecentCombinationCount") == 0 && (int)Get(((IList)Get(loaded, "Combinations"))[0], "Weight") == 1 &&
                (int)Get(((IList)Get(loaded, "Combinations"))[0], "CooldownSeconds") == 0, "Invalid negative selection settings are clamped");
            invalid.SetAttributeValue("recentCount", int.MaxValue); first.SetAttributeValue("weight", int.MaxValue); first.SetAttributeValue("cooldownSeconds", int.MaxValue);
            loaded = Load(invalid);
            Check((int)Get(loaded, "RecentCombinationCount") == 1000 && (int)Get(((IList)Get(loaded, "Combinations"))[0], "Weight") == 1000 &&
                (int)Get(((IList)Get(loaded, "Combinations"))[0], "CooldownSeconds") == 86400, "Oversized persisted selection settings are bounded");
            invalid.Attribute("recentCount").Remove(); foreach (var combination in invalid.Element("Combinations").Elements())
            { combination.Attribute("weight").Remove(); combination.Attribute("cooldownSeconds").Remove(); }
            loaded = Load(invalid);
            Check((int)Get(loaded, "RecentCombinationCount") == 0 && ((IList)Get(loaded, "Combinations")).Cast<object>().All(combination =>
                (int)Get(combination, "Weight") == 1 && (int)Get(combination, "CooldownSeconds") == 0), "Old configurations retain uniform random defaults");

            Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            cooling = Route("random"); foreach (var combination in (IList)Get(cooling, "Combinations")) Set(combination, "CooldownSeconds", 86400);
            long now = System.Diagnostics.Stopwatch.GetTimestamp(); for (int i = 0; i < 3; i++) EntriesAt(cooling, now);
            var current = GroupBatch(media[0])[0]; var pending = GroupBatch(media[1])[0]; var followup = GroupBatch(media[2])[0];
            QueueState(current, pending, followup); SetField("interactiveActive", true);
            Invoke(owner, "RequestInteractiveRoute", cooling, "blocked cooldown trigger");
            Check(Field(owner, "interactiveCurrent") == current && Field(owner, "interactivePending") == pending && QueuedEntries().SequenceEqual(new[] { followup }) &&
                !((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Any(), "Skipped triggers preserve current playback, pending output and the entire queue");
        }
        finally { Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false); SetField("random", originalRandom); SetField("pendingLogText", originalLog); SetField("lastLogFlushAt", originalFlush); }
        Console.WriteLine("PASS: weighted random selection, bounded recent history, independent monotonic cooldowns, blocked-trigger preservation, sequential behavior and configuration/copy isolation.");
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
            await WaitForPhasePendingAsync();
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
            routes = (IList)Field(owner, "interactiveRoutes");
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

    private static void CheckInteractiveHotkeyMaster(Form window, Form probe, object a, object b)
    {
        var toggle = (CheckBox)Field(window, "hotkeysEnabledBox");
        IntPtr handle = probe.Handle;
        int[] ids = { 0x7301, 0x7302, 0x7303 };
        Keys[] keys = { Keys.F23, Keys.F21, Keys.F20 };
        var route = ((IList)Get(a, "Routes"))[0];
        string configuration = Save(route).ToString();
        int selection = (int)Get(route, "NextCombinationIndex");
        object current = Field(owner, "interactiveCurrent"), pending = Field(owner, "interactivePending");
        object active = Field(owner, "interactiveActive"), runtime = Field(owner, "interactiveRuntimeVersion");
        try
        {
            Check(toggle.Checked, "Interactive hotkeys default to enabled");
            toggle.Checked = false;
            Check((bool)Field(owner, "interactiveHotkeysDisabled") &&
                ((IDictionary)Field(window, "registeredRoutes")).Count == 0 &&
                ((IDictionary)Field(window, "registeredProjectGroups")).Count == 0 && !(bool)Field(window, "groupSwitchHotkeyRegistered"),
                "The master switch unregisters route, group and general switching hotkeys immediately");
            Check(Field(owner, "interactiveCurrent") == current && Field(owner, "interactivePending") == pending &&
                Equals(Field(owner, "interactiveActive"), active) && Equals(Field(owner, "interactiveRuntimeVersion"), runtime),
                "Disabling hotkeys preserves playback and its pending media");
            for (int i = 0; i < keys.Length; i++)
                Check(RegisterHotKey(handle, ids[i], 7, (uint)keys[i]), "Other applications can actually register a released shortcut: " + keys[i]);
            foreach (int id in new[] { 0x5100, 0x6101, 0x6000 })
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)id, IntPtr.Zero));
            Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")) &&
                (int)Get(route, "NextCombinationIndex") == selection,
                "Late disabled hotkey messages cannot trigger a project or switch groups");
            Invoke(window, "ReloadAll");
            Invoke(window, "RegisterProjectGroupHotkeys");
            ((Button)((FlowLayoutPanel)Field(window, "projectGroupButtons")).Controls[1]).PerformClick();
            Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(b, "Id")), "Mouse group selection still works with hotkeys disabled");
            window.Hide(); window.Show();
            Check(!toggle.Checked && ((IDictionary)Field(window, "registeredRoutes")).Count == 0 &&
                ((IDictionary)Field(window, "registeredProjectGroups")).Count == 0 && !(bool)Field(window, "groupSwitchHotkeyRegistered"),
                "Reloading, changing groups and reopening cannot re-enable a disabled master switch");
            using (var replacement = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
                Check(!((CheckBox)Field(replacement, "hotkeysEnabledBox")).Checked, "A recreated window retains the session-wide disabled state");
            ((Button)((FlowLayoutPanel)Field(window, "projectGroupButtons")).Controls[0]).PerformClick();
            toggle.Checked = true;
            Check(((IDictionary)Field(window, "registeredRoutes")).Count == 0 &&
                ((IDictionary)Field(window, "registeredProjectGroups")).Count == 2 && !(bool)Field(window, "groupSwitchHotkeyRegistered") &&
                ((TextBox)Field(window, "operationBox")).Text.Contains("F23"),
                "Occupied shortcuts fail to register and are logged while other group shortcuts can recover");
            toggle.Checked = false;
            foreach (int id in ids) UnregisterHotKey(handle, id);
            toggle.Checked = true;
            Check(((IDictionary)Field(window, "registeredRoutes")).Count == 1 &&
                ((IDictionary)Field(window, "registeredProjectGroups")).Count == 3 && (bool)Field(window, "groupSwitchHotkeyRegistered"),
                "Re-enabling restores all shortcut families once other applications release them");
            for (int i = 0; i < keys.Length; i++)
                Check(!RegisterHotKey(handle, ids[i], 7, (uint)keys[i]), "Restored shortcut is actually owned by the interactive window: " + keys[i]);
            Check(Save(route).ToString() == configuration, "The temporary master switch preserves shortcut configuration");
        }
        finally
        {
            foreach (int id in ids) UnregisterHotKey(handle, id);
            toggle.Checked = true;
        }
        Console.WriteLine("PASS: session-wide hotkey master switch releases all three shortcut families to other Windows applications, preserves playback/configuration, ignores disabled messages, survives reload/reopen, and restores available registrations.");
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
            Set(original, "RecentCombinationCount", 2);
            Set(((IList)Get(original, "Combinations"))[0], "Weight", 9); Set(((IList)Get(original, "Combinations"))[0], "CooldownSeconds", 17);
            string before = Save(original).ToString();
            using (var dialog = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, original }))
            {
                Check(((ComboBox)Field(dialog, "selectionBox")).SelectedIndex == 1, "Editor loads selection mode");
                dialog.Size = dialog.MinimumSize;
                dialog.PerformLayout();
                Check(((ListBox)Field(dialog, "sequenceBox")).Height > 100 && ((ListBox)Field(dialog, "combinationsBox")).Width > 150,
                    "Combination and media editors remain usable at minimum window size");
                var weightBox = (NumericUpDown)Field(dialog, "combinationWeightBox"); var cooldownBox = (NumericUpDown)Field(dialog, "combinationCooldownBox");
                var recentBox = (NumericUpDown)Field(dialog, "recentCountBox");
                Check(weightBox.Value == 9 && cooldownBox.Value == 17 && recentBox.Value == 2 && !weightBox.Enabled && !cooldownBox.Enabled && !recentBox.Enabled,
                    "Sequential mode loads but disables random policy controls");
                ((ComboBox)Field(dialog, "selectionBox")).SelectedIndex = 0;
                Check(weightBox.Enabled && cooldownBox.Enabled && recentBox.Enabled, "Random mode enables all selection settings");
                weightBox.Value = 7; cooldownBox.Value = 23;
                var comboList = (ListBox)Field(dialog, "combinationsBox"); comboList.SelectedIndex = 1;
                Check(weightBox.Value == 1 && cooldownBox.Value == 0, "Each combination has independent editor settings");
                comboList.SelectedIndex = 0;
                Check(weightBox.Value == 7 && cooldownBox.Value == 23, "Changing combination selection preserves edited settings");
                Invoke(dialog, "AddCombination", true);
                Check(weightBox.Value == 7 && cooldownBox.Value == 23 && Get(((IList)Field(dialog, "combinations"))[3], "LastSelectedTimestamp") == null,
                    "Duplicating a combination copies its settings without runtime cooldown");
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
                Check((int)Get(original, "RecentCombinationCount") == 2 && (int)Get(((IList)Get(original, "Combinations"))[0], "Weight") == 7 &&
                    (int)Get(((IList)Get(original, "Combinations"))[0], "CooldownSeconds") == 23, "The editor saves recent-N and independent weights/cooldowns");
                Check(((IList)Get(original, "RecentCombinations")).Count == 0 && ((IList)Get(original, "Combinations")).Cast<object>().All(combination => Get(combination, "LastSelectedTimestamp") == null),
                    "Saving project edits clears runtime selection history and cooldown");
                Invoke(dialog, "AddSelectedMedia");
                Check(((IList)Get(((IList)Get(original, "Combinations"))[3], "MediaIds")).Count == 1, "Applied project does not share mutable editor lists");
                foreach (string key in new[] { "Up", "Down", "InteractiveCombinationSelection", "InteractiveRandom", "InteractiveSequential", "InteractiveCombinations", "InteractiveAddCombination", "InteractiveDuplicateCombination", "InteractiveEmptyCombination",
                    "InteractiveRecentCombinationCount", "InteractiveCombinationWeight", "InteractiveCombinationCooldown", "InteractiveCombinationRandomHint", "InteractiveCombinationUnavailable",
                    "InteractiveAddFileGroup", "InteractiveEditFileGroup", "InteractiveFileGroupTag", "InteractiveFileGroupPlayback", "InteractiveFileGroupNoRepeat",
                    "InteractiveFileGroupHint", "InteractiveFileGroupFiles", "InteractiveFileGroupRequired", "InteractiveFileGroupSummary",
                    "InteractiveFileGroupScope", "InteractiveFileGroupOne", "InteractiveFileGroupAll",
                    "InteractiveHotkeysEnabled", "InteractivePreventInterrupt", "InteractiveToggleProtection", "InteractiveWaitingProtected", "InteractiveBackendDisconnected", "InteractiveVerifyingPlayback", "InteractivePlaybackVerified", "InteractivePlaybackAutoNext", "InteractivePlaybackRetry", "InteractivePlaybackNoNext" })
                {
                    string value = resources.GetString(key, CultureInfo.CurrentUICulture);
                    Check(!string.IsNullOrWhiteSpace(value) && !value.Contains("?"), "Translated text exists: " + language + " " + key);
                    if (language != "en") Check(value.Any(c => c >= '\u4e00' && c <= '\u9fff'), "Chinese resources contain translated text");
                }
            }
        }
        Console.WriteLine("PASS: editor add/duplicate/reorder/remove, cancellation isolation, independent saved lists and three-language resources.");
    }
    private static object CopyProject(object route)
    {
        return main.GetMethod("CopyInteractiveProject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { route });
    }
    private static object ProjectGroup(string name, Keys key, int firstMedia)
    {
        var group = New("InteractiveProjectGroup"); Set(group, "Name", name);
        var hotkey = New("InteractiveHotkey"); Set(hotkey, "KeyCode", key); Set(hotkey, "Modifiers", Enum.ToObject(Nested("HotkeyModifiers"), 7));
        Set(group, "Hotkey", hotkey);
        var route = Route("sequential"); Set(route, "Name", name + " project");
        var routeKey = New("InteractiveHotkey"); Set(routeKey, "KeyCode", Keys.F23); Set(routeKey, "Modifiers", Enum.ToObject(Nested("HotkeyModifiers"), 7)); Set(route, "Hotkey", routeKey);
        ((IList)Get(route, "Combinations")).Clear();
        var combination = New("InteractiveMediaCombination"); ((IList)Get(combination, "MediaIds")).Add(Get(media[firstMedia], "RuntimeId"));
        ((IList)Get(route, "Combinations")).Add(combination);
        ((IList)Get(group, "Routes")).Add(route); return group;
    }
    private static async Task CheckProjectGroupDispatchAsync(TcpListener listener, object a, object b, bool defaults, bool protectTransition)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null;
        var mapping = ((IList)Field(owner, "interactiveGroupTransitions")).Cast<object>().First(item =>
            Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
        var transitionMedia = media.Cast<object>().First(item => Equals(Get(item, "RuntimeId"), Get(mapping, "MediaId")));
        object originalProtection = Get(transitionMedia, "PreventInterrupt"); Set(transitionMedia, "PreventInterrupt", protectTransition);
        SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            var source = GroupBatch(media[0])[0]; Set(source, "PreventInterrupt", false); Set(source, "BackendFrameId", 100L);
            QueueState(source, null); SetField("interactiveActive", true);
            SetField("processingInteractiveTriggers", true); Invoke(owner, "SwitchInteractiveProjectGroup", b, "TCP switch"); SetField("processingInteractiveTriggers", false);
            var accept = listener.AcceptTcpClientAsync();
            var connecting = ClientSend(client, (int)((NumericUpDown)Field(owner, "portBox")).Value, "STATUS");
            peer = await CompleteSoon(accept, "Group switching connects to the backend");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "The fixture establishes its initial connection") == "STATUS", "The fixture connects before testing group dispatch");
            await writer.WriteLineAsync(BackendStatus(source, "playing", source) + " actual_frame_id=100 input_frame_id=100");
            await CompleteSoon(connecting, "The initial connection generation is established");
            var dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "Group switching verifies the current output") == "STATUS", "A configured group switch uses existing playback verification");
            await writer.WriteLineAsync(BackendStatus(source, "playing", source) + " actual_frame_id=100 input_frame_id=100");
            string command = await CompleteSoon(reader.ReadLineAsync(), "A group switch sends the transition");
            Check(command.StartsWith("PLAY ") && !command.StartsWith("PLAY_NEXT "), "An interruptible source switches immediately to the transition");
            await writer.WriteLineAsync("OK playing transition");
            if (defaults)
            {
                command = await CompleteSoon(reader.ReadLineAsync(), "The transition prequeues the group's idle video");
                Check(command.StartsWith("PLAY_NEXT " + DefaultPath(b)) && command.Contains("loop=1"),
                    "Switching groups prequeues the target group's looping default after the transition");
                await writer.WriteLineAsync("OK idle queued");
            }
            await CompleteSoon(dispatch, "The transition command is acknowledged");
            var transition = Field(owner, "interactiveCurrent");
            Check((bool)Get(transition, "IsGroupTransition") && (defaults || command.StartsWith("PLAY " + Get(transition, "Path"))), "The actual PLAY contains the configured direction's video");
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(transition, "playing", defaults ? Field(owner, "interactivePending") : transition) +
                " actual_frame_id=200 input_frame_id=" + (defaults ? "201" : "200"));
            SetField("processingInteractiveTriggers", true);
            Invoke(owner, "RequestInteractiveRoute", ((IList)Get(b, "Routes"))[0], "TCP target");
            SetField("processingInteractiveTriggers", false); dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "A target trigger verifies the transition") == "STATUS", "The target trigger verifies actual transition playback");
            await writer.WriteLineAsync(BackendStatus(transition, "playing", defaults ? Field(owner, "interactivePending") : transition) +
                " actual_frame_id=200 input_frame_id=" + (defaults ? "201" : "200"));
            command = await CompleteSoon(reader.ReadLineAsync(), "A target trigger waits at the transition boundary");
            if (!protectTransition)
            {
                Check(command.StartsWith("PLAY " + Get(media[2], "Path")), "An unprotected transition can be interrupted immediately by a PLAY project");
                await writer.WriteLineAsync("OK immediate project");
                if (defaults)
                {
                    Check((await CompleteSoon(reader.ReadLineAsync(), "The interrupted transition is replaced by the project/default sequence")).StartsWith("PLAY_NEXT " + DefaultPath(b)),
                        "An interrupted transition discards its old tail and returns to the target default after the project");
                    await writer.WriteLineAsync("OK return default");
                }
                await CompleteSoon(dispatch, "An interruptible transition does not hold the project dispatcher");
                Check(!(bool)Get(Field(owner, "interactiveCurrent"), "IsGroupTransition") && Field(owner, "interactiveProtectedHandoff") == null,
                    "An interruptible transition has no protected wait");
                return;
            }
            Check(command.StartsWith("PLAY_NEXT " + Get(media[2], "Path")), "The target group's actual media is sent as PLAY_NEXT during the protected transition");
            await writer.WriteLineAsync("OK target queued");
            await CompleteSoon((Task)Field(owner, "interactiveNextQueueTask"), "The target next command is acknowledged");
            var target = Field(owner, "interactivePending");
            Check(!dispatch.IsCompleted && Field(owner, "interactiveCurrent") == transition && target != null,
                "The trigger remains protected until the target actually reaches output");
            Set(target, "PreventInterrupt", false);
            Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(target, "playing", target) +
                " actual_frame_id=" + (defaults ? "202" : "201") + " input_frame_id=" + (defaults ? "202" : "201"));
            await CompleteSoon(dispatch, "Actual target output completes the group handoff");
            Check(Field(owner, "interactiveCurrent") == target, "The active playback item is the new group's project");
            if (defaults)
            {
                var returning = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                command = await CompleteSoon(reader.ReadLineAsync(), "Project playback queues the group's default video again");
                Check(command.StartsWith("PLAY_NEXT " + DefaultPath(b)), "A completed target project returns to its own group's default");
                await writer.WriteLineAsync("OK returning idle"); await CompleteSoon(returning, "The return default is acknowledged");
                var idle = Field(owner, "interactivePending");
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(idle, "playing", idle) + " actual_frame_id=201 input_frame_id=203") && Field(owner, "interactiveCurrent") == target,
                    "The default batch discarded before the project cannot be mistaken for its return default");
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(target, "playing", idle) + " actual_frame_id=202 input_frame_id=203");
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(idle, "playing", idle) + " actual_frame_id=203 input_frame_id=203");
                Check(Field(owner, "interactiveCurrent") == idle && (bool)Get(idle, "IsGroupDefault"), "The default resumes once its new batch actually reaches output");
                SetField("processingInteractiveTriggers", true); Invoke(owner, "RequestInteractiveRoute", ((IList)Get(b, "Routes"))[0], "idle trigger"); SetField("processingInteractiveTriggers", false);
                dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
                Check(await CompleteSoon(reader.ReadLineAsync(), "A trigger verifies default playback") == "STATUS", "Triggers verify actual default output");
                await writer.WriteLineAsync(BackendStatus(idle, "playing", idle) + " actual_frame_id=203 input_frame_id=204");
                Check((await CompleteSoon(reader.ReadLineAsync(), "An immediate project interrupts the default")).StartsWith("PLAY " + Get(media[2], "Path")),
                    "The default video permits an immediate interactive trigger");
                await writer.WriteLineAsync("OK project playing");
                Check((await CompleteSoon(reader.ReadLineAsync(), "The second project prequeues the same default")).StartsWith("PLAY_NEXT " + DefaultPath(b)),
                    "Repeated triggers keep a bounded return-to-default slot");
                await writer.WriteLineAsync("OK default queued again"); await CompleteSoon(dispatch, "The repeated project is dispatched");
                var repeated = Field(owner, "interactiveCurrent"); var repeatedIdle = Field(owner, "interactivePending");
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(idle, "playing", repeatedIdle) + " actual_frame_id=204 input_frame_id=206") && Field(owner, "interactiveCurrent") == repeated,
                    "A predecoded old default loop cannot complete the new project before its first output frame is observed");
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(repeated, "playing", repeatedIdle) + " actual_frame_id=205 input_frame_id=206");
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(repeatedIdle, "playing", repeatedIdle) + " actual_frame_id=206 input_frame_id=206");
                Check(Field(owner, "interactiveCurrent") == repeatedIdle, "Repeated project playback returns to a distinct new default batch");
                return;
            }

            SetField("processingInteractiveTriggers", true); Invoke(owner, "RequestInteractiveRoute", ((IList)Get(b, "Routes"))[0], "obsolete target"); SetField("processingInteractiveTriggers", false);
            dispatch = (Task)Invoke(owner, "ProcessInteractiveTriggerRequestsAsync");
            Check(await CompleteSoon(reader.ReadLineAsync(), "An old group trigger is awaiting verification") == "STATUS", "The race includes an in-flight old-group STATUS");
            Invoke(owner, "SwitchInteractiveProjectGroup", a, "TCP return");
            await writer.WriteLineAsync(BackendStatus(target, "playing", target) + " actual_frame_id=201 input_frame_id=201");
            Check(await CompleteSoon(reader.ReadLineAsync(), "The stale trigger is discarded before dispatch") == "STATUS",
                "A late old-group reply starts verification for the return transition without replaying the old project");
            await writer.WriteLineAsync(BackendStatus(target, "playing", target) + " actual_frame_id=201 input_frame_id=201");
            command = await CompleteSoon(reader.ReadLineAsync(), "The reverse transition plays after the race");
            Check(command.StartsWith("PLAY "), "The requested return transition wins over the obsolete project");
            await writer.WriteLineAsync("OK returning transition"); await CompleteSoon(dispatch, "The reverse transition finishes dispatch");
            Check((bool)Get(Field(owner, "interactiveCurrent"), "IsGroupTransition") && Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")),
                "The current group and playback remain synchronized after a delayed STATUS race");
        }
        finally
        {
            Set(transitionMedia, "PreventInterrupt", originalProtection);
            ((IDisposable)client).Dispose(); if (peer != null) peer.Close(); SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
            Invoke(owner, "ClearInteractiveRuntime", true);
        }
    }
    private static void CheckProjectGroupDispatch(object a, object b, bool defaults = false, bool protectTransition = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox);
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { CheckProjectGroupDispatchAsync(listener, a, b, defaults, protectTransition).GetAwaiter().GetResult(); }
            finally
            {
                listener.Stop(); SetField("hostBox", null); SetField("portBox", null);
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }
    }
    private static string DefaultPath(object group)
    {
        return (string)Get(media.Cast<object>().First(item => Equals(Get(item, "RuntimeId"), Get(group, "DefaultMediaId"))), "Path");
    }
    private static object[] DefaultBatch(object group)
    {
        return ((IEnumerable)Invoke(owner, "BuildInteractiveGroupDefault", group, false)).Cast<object>().ToArray();
    }
    private static void CheckGroupDefaults(object a, object b)
    {
        string day = Path.GetTempFileName(), night = Path.GetTempFileName();
        var dayMedia = New("InteractiveMediaItem"); Set(dayMedia, "Path", day); Set(dayMedia, "Title", "Day idle");
        var nightMedia = New("InteractiveMediaItem"); Set(nightMedia, "Path", night); Set(nightMedia, "Title", "Night idle");
        media.Add(dayMedia); media.Add(nightMedia);
        object oldConnections = Field(owner, "connectionStates"), oldTimer = Field(owner, "playoutTimer"), oldLabel = Field(owner, "playoutStatusLabel"), oldStatus = Field(owner, "currentStatusState");
        var context = SynchronizationContext.Current;
        using (var timer = new System.Windows.Forms.Timer())
        using (var label = new Label())
        try
        {
            Set(a, "DefaultMediaId", Get(dayMedia, "RuntimeId")); Set(b, "DefaultMediaId", Get(nightMedia, "RuntimeId"));
            SetField("connectionStates", Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("ConnectionTabState"))));
            SetField("playoutTimer", timer); SetField("playoutStatusLabel", label); SetField("currentStatusState", "stopped");
            SynchronizationContext.SetSynchronizationContext(null);
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            Invoke(owner, "ClearInteractiveRuntime", true);
            var idle = DefaultBatch(a).Single();
            Check((bool)Get(idle, "IsGroupDefault") && (bool)Get(idle, "Loop") && !(bool)Get(idle, "PreventInterrupt") && ((string)Get(idle, "Options")).Contains("loop=1"),
                "The per-group default loops without interrupt protection");
            using (var dialog = (Form)Activator.CreateInstance(Nested("ProjectGroupEditDialog"), new object[] { media, a }))
            {
                Check(Equals(Get(dialog, "DefaultMediaId"), Get(dayMedia, "RuntimeId")), "The group editor loads its library default");
                var box = (ComboBox)Field(dialog, "defaultMediaBox");
                box.SelectedIndex = media.IndexOf(nightMedia) + 1;
                dialog.PerformLayout();
                Check(Equals(Get(dialog, "DefaultMediaId"), Get(nightMedia, "RuntimeId")) && Equals(Get(a, "DefaultMediaId"), Get(dayMedia, "RuntimeId")),
                    "Default media editing supports independent cancellation");
                Check(box.Width > 100 && box.DropDownStyle == ComboBoxStyle.DropDownList, "Default selection uses a usable library-only picker");
                box.SelectedIndex = 0;
                Check((Guid)Get(dialog, "DefaultMediaId") == Guid.Empty, "The picker can disable default playback");
            }
            var state = New("ConnectionTabState"); Set(state, "InteractiveProjectGroups", Field(owner, "interactiveProjectGroups"));
            Set(state, "InteractiveMedia", media);
            Set(state, "ActiveInteractiveProjectGroupId", Get(b, "Id"));
            var xml = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            var isolated = FormatterServices.GetUninitializedObject(main);
            main.GetField("interactiveMedia", PrivateInstance).SetValue(isolated, Activator.CreateInstance(media.GetType()));
            main.GetField("interactiveRoutes", PrivateInstance).SetValue(isolated, Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveRouteItem"))));
            Invoke(isolated, "LoadInteractivePlaybackElement", XElement.Parse(xml.ToString()));
            var loaded = ((IList)Field(isolated, "interactiveProjectGroups")).Cast<object>().ToArray();
            Check(Equals(Get(loaded.First(group => Equals(Get(group, "Id"), Get(a, "Id"))), "DefaultMediaId"), Get(dayMedia, "RuntimeId")) &&
                Equals(Get(loaded.First(group => Equals(Get(group, "Id"), Get(b, "Id"))), "DefaultMediaId"), Get(nightMedia, "RuntimeId")),
                "Each group's independent default library reference survives connection XML round trips");
            Check(!xml.Descendants("Group").Any(group => group.Attribute("defaultVideo") != null), "No default video path is saved");
            foreach (var group in xml.Element("Groups").Elements("Group")) group.Attribute("defaultMedia").Remove();
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            Check(((IList)Field(isolated, "interactiveProjectGroups")).Cast<object>().All(group => (Guid)Get(group, "DefaultMediaId") == Guid.Empty),
                "Groups without a default library reference have no implicit default media");

            SetField("processingInteractiveTriggers", true); SetField("interactiveWindowOpen", false);
            Invoke(owner, "SetInteractiveWindowOpen", true);
            var requests = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
            Check(requests.Length == 1 && (bool)Get(((IList)Get(requests[0], "Entries"))[0], "IsGroupDefault") && (bool)Field(owner, "interactiveActive"),
                "Opening the interactive window starts its active group's default even before any project trigger");
            Invoke(owner, "RequestInteractiveGroupDefaultIfIdle");
            Check(((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Count() == 1, "Repeated idle activation cannot enqueue duplicate default requests");
            SetField("closingMainForm", true); Invoke(owner, "SetInteractiveWindowOpen", false); SetField("closingMainForm", false);
            Invoke(owner, "RequestInteractiveGroupDefaultIfIdle");
            Check(!(bool)Field(owner, "interactiveActive") && !((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Any(),
                "A hidden or closing window cannot restart its default video");
            SetField("interactiveWindowOpen", true);

            var source = GroupBatch(media[0])[0]; Set(source, "Loop", false); Set(source, "BackendFrameId", 500L);
            QueueState(source, null); SetField("interactiveActive", true);
            Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded"); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().Length == 1 && (string)Get(QueuedEntries()[0], "Path") == day, "A finite project prequeues exactly one default for its active group");
            string command = null; Func<string, Task<string>> send = value => { command = value; return Task.FromResult("OK queued"); };
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(command.StartsWith("PLAY_NEXT " + day) && Field(owner, "interactiveCurrent") == source && (bool)Get(Field(owner, "interactivePending"), "IsGroupDefault"),
                "Returning to default uses PLAY_NEXT without truncating the interactive project's tail");
            var followup = GroupBatch(media[2])[0];
            Set(source, "PreventInterrupt", true);
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(followup));
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(command.StartsWith("PLAY_NEXT " + Get(followup, "Path")) && command.Contains("after_frame_id=500") && Field(owner, "interactiveCurrent") == source,
                "A new protected trigger replaces the predecoded default while preserving the current media tail");
            Invoke(owner, "ClearInteractiveRuntime", true);
            QueueState(source, null); SetField("interactiveActive", true);
            var idleRequest = ProtectedRequest(idle);
            handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", idleRequest);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", ProtectedRequest(idle));
            Invoke(owner, "RequestInteractiveRoute", ((IList)Get(a, "Routes"))[0], "priority trigger");
            Check(HandoffTask(handoff).IsCompleted && ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().All(request =>
                    !((IList)Get(request, "Entries")).Cast<object>().All(entry => (bool)Get(entry, "IsGroupDefault"))),
                "A real trigger replaces queued idle requests and releases an idle-only protected wait");
            Invoke(owner, "ClearInteractiveRuntime", true);
            QueueState(idle, null); SetField("interactiveActive", true); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().Length == 0, "A looping default never queues another copy of itself");
            QueueState(source, null, followup); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().SequenceEqual(new[] { followup }), "The default cannot displace an explicit project successor");
            var groupLoop = FileGroup(); Set(groupLoop, "FileGroupPlayAll", true); Set(groupLoop, "Loop", true);
            var batch = GroupBatch(groupLoop); QueueState(batch[batch.Length - 1], null); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().Length == 0, "Explicit whole-group loops retain their configured playback behavior");
            Invoke(owner, "ClearInteractiveRuntime", true); QueueState(source, null); SetField("interactiveActive", true);
            Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(source, "ended", source) + " actual_frame_id=500") && QueuedEntries().Length == 1,
                "A short project's ended status can return to the default without a ten-second mismatch timeout");
            Invoke(owner, "ClearInteractiveRuntime", true);
            var transitions = (IList)Field(owner, "interactiveGroupTransitions");
            object[] savedTransitions = transitions.Cast<object>().ToArray();
            transitions.Clear();
            try
            {
                Invoke(owner, "SwitchInteractiveProjectGroup", b, "no transition");
                var noTransition = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single();
                Check(!(bool)Get(noTransition, "IsGroupTransition") && ((IList)Get(noTransition, "Entries")).Count == 1 &&
                    (string)Get(((IList)Get(noTransition, "Entries"))[0], "Path") == night,
                    "Without a transition, selecting a group immediately requests that group's default video");
                Invoke(owner, "SwitchInteractiveProjectGroup", a, "rapid idle switch");
                var latest = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single();
                Check((string)Get(((IList)Get(latest, "Entries"))[0], "Path") == day,
                    "Rapid switches discard obsolete default-only requests and keep the latest group's idle video");
            }
            finally { transitions.Clear(); foreach (var item in savedTransitions) transitions.Add(item); }
            Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("processingInteractiveTriggers", false);
            CheckProjectGroupDispatch(a, b, true);
            CheckProjectGroupDispatch(a, b, true, false);
            CheckLibraryGroupDefaults(a, b, day, night);
            CheckLibraryGroupTransitions(a, b, day, night);
            CheckProtectedGroupDefaults(a, b, dayMedia, day, night);
        }
        finally
        {
            Set(a, "DefaultMediaId", Guid.Empty); Set(b, "DefaultMediaId", Guid.Empty);
            media.Remove(dayMedia); media.Remove(nightMedia);
            SetField("closingMainForm", false); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", false);
            Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("connectionStates", oldConnections); SetField("playoutTimer", oldTimer); SetField("playoutStatusLabel", oldLabel); SetField("currentStatusState", oldStatus);
            SynchronizationContext.SetSynchronizationContext(context); File.Delete(day); File.Delete(night);
        }
        Console.WriteLine("PASS: per-group default configuration, idle activation, looping return, protected replacement, short projects and actual TCP transition/project/default cycles.");
    }
    private static void CheckLibraryGroupDefaults(object a, object b, string day, string night)
    {
        string third = Path.GetTempFileName();
        var group = FileGroup();
        var files = (IList)Get(group, "FilePaths"); files.Clear(); files.Add(day); files.Add(night); files.Add(third);
        Set(group, "FileGroupPlayAll", true); Set(group, "PreventInterrupt", true); Set(group, "Options", "loop=1,custom=1");
        media.Add(group);
        object originalDefault = Get(a, "DefaultMediaId");
        try
        {
            Set(a, "DefaultMediaId", Get(group, "RuntimeId"));
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            SetField("processingInteractiveTriggers", true); SetField("interactiveWindowOpen", true);
            Invoke(owner, "ClearInteractiveRuntime", true);
            var batch = DefaultBatch(a);
            Check(batch.Length == 3 && batch.Select(entry => (string)Get(entry, "Path")).SequenceEqual(new[] { day, night, third }),
                "A default file group selects all files in configured order");
            Check(batch.All(entry => (bool)Get(entry, "IsGroupDefault") && !(bool)Get(entry, "Loop") && (bool)Get(entry, "PreventInterrupt") &&
                !((string)Get(entry, "Options")).Contains("loop=") && ((string)Get(entry, "Options")).Contains("custom=1")),
                "Default groups repeat as a group and retain media options and interruption protection");
            Check(!(bool)Get(group, "Loop") && (bool)Get(group, "PreventInterrupt"), "Building defaults does not edit library loop or protection settings");
            for (int pass = 0; pass < 5; pass++)
            {
                QueueState(batch[batch.Length - 1], null);
                Invoke(owner, "QueueInteractiveGroupContinuation"); batch = QueuedEntries();
                Check(batch.Length == 3 && batch.All(entry => (bool)Get(entry, "IsGroupDefault") && (bool)Get(entry, "PreventInterrupt")) &&
                    (string)Get(batch[0], "Path") == day && (string)Get(batch[2], "Path") == third,
                    "Every default pass repeats the full group and retains its default identity");
            }
            using (var dialog = (Form)Activator.CreateInstance(Nested("ProjectGroupEditDialog"), new object[] { media, a }))
                Check(Equals(Get(dialog, "DefaultMediaId"), Get(group, "RuntimeId")), "The default picker selects a library file group");
            var state = New("ConnectionTabState"); Set(state, "InteractiveMedia", media);
            Set(state, "InteractiveProjectGroups", Field(owner, "interactiveProjectGroups")); Set(state, "ActiveInteractiveProjectGroupId", Get(a, "Id"));
            var xml = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            var isolated = FormatterServices.GetUninitializedObject(main);
            main.GetField("interactiveMedia", PrivateInstance).SetValue(isolated, Activator.CreateInstance(media.GetType()));
            main.GetField("interactiveRoutes", PrivateInstance).SetValue(isolated, Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveRouteItem"))));
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            var loadedGroup = ((IList)Field(isolated, "interactiveProjectGroups")).Cast<object>().First(item => Equals(Get(item, "Id"), Get(a, "Id")));
            var loadedBatch = ((IEnumerable)Invoke(isolated, "BuildInteractiveGroupDefault", loadedGroup, false)).Cast<object>().ToArray();
            Check(loadedBatch.Length == 3 && loadedBatch.Select(entry => (string)Get(entry, "Path")).SequenceEqual(new[] { day, night, third }),
                "A group's default library reference and file-group settings survive real connection XML");

            var source = GroupBatch(media[0])[0]; Set(source, "Loop", false); Set(source, "BackendFrameId", 710L);
            Invoke(owner, "ClearInteractiveRuntime", true); QueueState(source, null); SetField("interactiveActive", true);
            Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded"); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().Length == 3, "Returning from a project queues the entire default group exactly once");
            string command = null;
            Func<string, Task<string>> send = text => { command = text; return Task.FromResult("OK"); };
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(command.StartsWith("PLAY_NEXT " + day) && QueuedEntries().Length == 2 &&
                (bool)Get(Field(owner, "interactivePending"), "IsGroupDefault"), "The default group's first file is prequeued behind a finite project");
            Set(group, "PreventInterrupt", false);
            batch = DefaultBatch(a);
            QueueState(batch[0], batch[1], batch[2]);
            Check(Invoke(owner, "PrepareInteractiveRouteAppend") == batch[1] && Field(owner, "interactivePending") == null && QueuedEntries().Length == 0 &&
                (bool)Get(Get(batch[0], "GroupLoop"), "Cancelled"), "A real PLAY_NEXT trigger replaces the prefetched default and drops the rest of its group");
            Invoke(owner, "ClearInteractiveRuntime", true);
            Set(source, "PreventInterrupt", true); QueueState(source, null); SetField("interactiveActive", true);
            var idleRequest = ProtectedRequest(batch);
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", idleRequest);
            Invoke(Field(owner, "interactiveTriggerRequests"), "Enqueue", ProtectedRequest(batch));
            Invoke(owner, "RequestInteractiveRoute", ((IList)Get(a, "Routes"))[0], "priority over idle group");
            Check(HandoffTask(handoff).IsCompleted && ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().All(request =>
                !((IList)Get(request, "Entries")).Cast<object>().All(entry => (bool)Get(entry, "IsGroupDefault"))),
                "Real projects replace queued multi-file defaults and release their protected wait");
            Invoke(owner, "ClearInteractiveRuntime", true);
            Invoke(owner, "RequestInteractiveGroupDefaultIfIdle");
            var requestEntries = (IList)Get(((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single(), "Entries");
            Check(requestEntries.Count == 3, "Opening an idle group requests all selected default files");
            Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("activeInteractiveProjectGroupId", Get(b, "Id")); SetField("interactiveRoutes", Get(b, "Routes"));
            Invoke(owner, "SwitchInteractiveProjectGroup", a, "switch to default group");
            requestEntries = (IList)Get(((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single(), "Entries");
            Check(requestEntries.Count == 4 && (bool)Get(requestEntries[0], "IsGroupTransition") &&
                requestEntries.Cast<object>().Skip(1).All(entry => (bool)Get(entry, "IsGroupDefault")),
                "Switching groups queues the complete default file group after its transition");

            Set(group, "FileGroupPlayAll", false); Set(group, "FileGroupRandom", true); Set(group, "FileGroupNoRepeat", true);
            for (int round = 0; round < 4; round++)
            {
                var seen = new HashSet<string>(); batch = DefaultBatch(a);
                for (int draw = 0; draw < 3; draw++)
                {
                    Check(batch.Length == 1 && seen.Add((string)Get(batch[0], "Path")), "Default one-file random mode does not repeat within a round");
                    if (draw < 2) { QueueState(batch[0], null); Invoke(owner, "QueueInteractiveGroupContinuation"); batch = QueuedEntries(); }
                }
            }
            Set(group, "FileGroupPlayAll", true);
            var orders = new HashSet<string>();
            for (int pass = 0; pass < 12; pass++)
            {
                batch = DefaultBatch(a); orders.Add(string.Join("|", batch.Select(entry => (string)Get(entry, "Path"))));
                Check(batch.Length == 3 && batch.Select(entry => (string)Get(entry, "Path")).Distinct().Count() == 3,
                    "Default random all-files mode plays every file once per pass");
            }
            Check(orders.Count > 1, "Random default groups vary playback order");
            Set(group, "FileGroupPlayAll", false); Set(group, "FileGroupRandom", false); Set(group, "NextFileIndex", 0);
            batch = DefaultBatch(a); Check((string)Get(batch[0], "Path") == day, "Sequential one-file default starts at the configured position");
            QueueState(batch[0], null); Invoke(owner, "QueueInteractiveGroupContinuation");
            Check((string)Get(QueuedEntries().Single(), "Path") == night, "Sequential default loops advance to the next group file");
            files[1] = night + ".missing";
            Check(DefaultBatch(a).Length == 0, "An incomplete default file group cannot start");
            Set(a, "DefaultMediaId", Guid.NewGuid()); Check(DefaultBatch(a).Length == 0, "A missing library reference has no path fallback");
        }
        finally
        {
            Set(a, "DefaultMediaId", originalDefault); media.Remove(group); File.Delete(third);
            Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false);
        }
        Console.WriteLine("PASS: library-only defaults, full-group cycling, random/no-repeat and sequential selection, persistence, prequeue replacement and group transitions.");
    }
    private static void CheckLibraryGroupTransitions(object a, object b, string day, string night)
    {
        string third = Path.GetTempFileName();
        var transition = ((IList)Field(owner, "interactiveGroupTransitions")).Cast<object>().First(item =>
            Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
        object originalMedia = Get(transition, "MediaId");
        var group = FileGroup(); Set(group, "Loop", true); Set(group, "PreventInterrupt", false);
        Set(group, "Options", "loop=1,custom=1"); Set(group, "FileGroupPlayAll", true);
        var files = (IList)Get(group, "FilePaths"); files.Clear(); files.Add(day); files.Add(night); files.Add(third);
        media.Add(group); Set(transition, "MediaId", Get(group, "RuntimeId"));
        try
        {
            var interruptible = ((IList)Get(Invoke(owner, "BuildInteractiveGroupTransition", a, b), "Entries")).Cast<object>().ToArray();
            Check(interruptible.Length == 3 && interruptible.All(entry => !(bool)Get(entry, "PreventInterrupt") && !(bool)Get(entry, "Loop") && Get(entry, "GroupLoop") == null),
                "Unprotected library transition groups remain interruptible while looping is disabled");
            Set(group, "PreventInterrupt", true);
            var request = Invoke(owner, "BuildInteractiveGroupTransition", a, b);
            var entries = ((IList)Get(request, "Entries")).Cast<object>().ToArray();
            Check(entries.Length == 3 && entries.Select(entry => (string)Get(entry, "Path")).SequenceEqual(new[] { day, night, third }),
                "A transition selects the configured library group's complete sequence");
            Check(entries.All(entry => (bool)Get(entry, "IsGroupTransition") && (bool)Get(entry, "PreventInterrupt") && !(bool)Get(entry, "Loop") &&
                Get(entry, "GroupLoop") == null && Equals(Get(entry, "PlaybackUnitId"), Get(entries[0], "PlaybackUnitId")) &&
                !((string)Get(entry, "Options")).Contains("loop=") && ((string)Get(entry, "Options")).Contains("custom=1")),
                "All transition files share one protection boundary, play once and ignore both loop flags and loop options");
            Check((bool)Get(group, "Loop") && (bool)Get(group, "PreventInterrupt") && ((string)Get(group, "Options")).Contains("loop=1"),
                "Transition overrides do not alter the library item");
            var state = New("ConnectionTabState"); Set(state, "InteractiveMedia", media);
            Set(state, "InteractiveProjectGroups", Field(owner, "interactiveProjectGroups"));
            Set(state, "InteractiveGroupTransitions", Field(owner, "interactiveGroupTransitions"));
            var xml = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            var saved = xml.Element("Transitions").Elements("Transition").First(item => item.Attribute("from").Value == Get(a, "Id").ToString() &&
                item.Attribute("to").Value == Get(b, "Id").ToString());
            Check(saved.Attribute("media").Value == Get(group, "RuntimeId").ToString() && saved.Attribute("path") == null,
                "Transition configuration saves a library reference without a file path");
            var isolated = FormatterServices.GetUninitializedObject(main);
            main.GetField("interactiveMedia", PrivateInstance).SetValue(isolated, Activator.CreateInstance(media.GetType()));
            main.GetField("interactiveRoutes", PrivateInstance).SetValue(isolated, Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveRouteItem"))));
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            Check(((IList)Field(isolated, "interactiveGroupTransitions")).Cast<object>().Any(item => Equals(Get(item, "MediaId"), Get(group, "RuntimeId"))),
                "A transition's file-group reference survives connection XML loading");

            Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            Set(entries[0], "BackendFrameId", 800L);
            var project = GroupBatch(media[2])[0]; var idle = DefaultBatch(b).Single();
            QueueState(entries[0], entries[1], entries[2], idle); SetField("interactiveActive", true);
            var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(project));
            var waiting = HandoffTask(handoff);
            Check(Field(owner, "interactivePending") == entries[1] && QueuedEntries().SequenceEqual(new[] { entries[2], project }),
                "A project triggered during a multi-file transition retains every remaining transition member");
            var commands = new List<string>(); Func<string, Task<string>> send = text => { commands.Add(text); return Task.FromResult("OK"); };
            for (int index = 1; index < entries.Length; index++)
            {
                Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                    Enum.Parse(Nested("BackendInputEventType"), "Started"), 800L + index, Get(entries[index], "InputSummary") }));
                Check(Field(owner, "interactiveCurrent") == entries[index] && !waiting.IsCompleted,
                    "An internal transition boundary cannot release a waiting project");
                ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            }
            Check(commands.Count == 2 && commands[0].StartsWith("PLAY_NEXT " + third) && commands[1].StartsWith("PLAY_NEXT " + Get(project, "Path")),
                "Protected transitions queue their remaining files and then the waiting project without any immediate PLAY");
            Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                Enum.Parse(Nested("BackendInputEventType"), "Started"), 803L, Get(project, "InputSummary") }));
            Check(waiting.IsCompleted && Field(owner, "interactiveCurrent") == project, "A trigger is released only after the complete transition reaches the next project");
            QueueState(entries[entries.Length - 1], null); Invoke(owner, "QueueInteractiveGroupContinuation");
            Check(QueuedEntries().Length == 0, "A completed transition group never schedules another pass");

            Set(group, "PreventInterrupt", false);
            Check(((IList)Get(Invoke(owner, "BuildInteractiveGroupTransition", a, b), "Entries")).Cast<object>().All(entry => !(bool)Get(entry, "PreventInterrupt")) &&
                entries.All(entry => (bool)Get(entry, "PreventInterrupt")), "Library edits apply to new transition snapshots without changing the running protected sequence");
            Set(group, "FileGroupPlayAll", false); Set(group, "NextFileIndex", 0);
            for (int draw = 0; draw < 6; draw++)
            {
                entries = ((IList)Get(Invoke(owner, "BuildInteractiveGroupTransition", a, b), "Entries")).Cast<object>().ToArray();
                Check(entries.Length == 1 && (string)Get(entries[0], "Path") == new[] { day, night, third }[draw % 3],
                    "A one-file transition group advances sequentially once per switch");
            }
            Set(group, "FileGroupRandom", true); Set(group, "FileGroupNoRepeat", true);
            for (int round = 0; round < 3; round++)
            {
                var seen = new HashSet<string>();
                for (int draw = 0; draw < 3; draw++)
                {
                    entries = ((IList)Get(Invoke(owner, "BuildInteractiveGroupTransition", a, b), "Entries")).Cast<object>().ToArray();
                    Check(entries.Length == 1 && seen.Add((string)Get(entries[0], "Path")), "Random transition selection retains no-repeat rounds");
                }
            }
            Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            files[2] = third + ".missing";
            object selection = Get(group, "NextFileIndex");
            Invoke(owner, "SwitchInteractiveProjectGroup", b, "missing group member");
            Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")) &&
                !((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Any() && Equals(Get(group, "NextFileIndex"), selection),
                "An unavailable transition member prevents group switching before selecting or sending partial playback");
        }
        finally
        {
            Set(transition, "MediaId", originalMedia); media.Remove(group); File.Delete(third);
            Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false);
        }
        Console.WriteLine("PASS: library transition groups, one-shot loop overrides, complete-sequence protection, file selection, persistence and missing-member rejection.");
    }
    private static void CheckProtectedGroupDefaults(object a, object b, object dayMedia, string day, string night)
    {
        var context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        object originalDefault = Get(a, "DefaultMediaId"), originalProtection = Get(dayMedia, "PreventInterrupt");
        var group = FileGroup(); Set(group, "FileGroupPlayAll", true); Set(group, "PreventInterrupt", true);
        var files = (IList)Get(group, "FilePaths"); files.Clear(); files.Add(day); files.Add(night); media.Add(group);
        try
        {
            SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            Set(dayMedia, "PreventInterrupt", true);
            var idle = DefaultBatch(a).Single();
            Check((bool)Get(idle, "PreventInterrupt") && (bool)Get(idle, "Loop"), "Single-file defaults inherit library protection while looping");
            Set(dayMedia, "PreventInterrupt", false);
            Check(!(bool)Get(DefaultBatch(a).Single(), "PreventInterrupt") && (bool)Get(idle, "PreventInterrupt"),
                "Default snapshots preserve their selected interruption policy when the library is edited");
            Invoke(owner, "ClearInteractiveRuntime", true);
            Set(idle, "BackendFrameId", 900L); QueueState(idle, null); SetField("interactiveActive", true);
            var project = GroupBatch(media[2])[0];
            var request = ProtectedRequest(project); var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", request);
            string command = null; Func<string, Task<string>> send = text => { command = text; return Task.FromResult("OK"); };
            ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
            Check(command.StartsWith("PLAY_NEXT " + Get(project, "Path")) && Field(owner, "interactiveCurrent") == idle && !HandoffTask(handoff).IsCompleted,
                "A protected single-file default finishes the current pass through PLAY_NEXT instead of being interrupted");
            Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                Enum.Parse(Nested("BackendInputEventType"), "Started"), 901L, Get(project, "InputSummary") }));
            Check(HandoffTask(handoff).IsCompleted && Field(owner, "interactiveCurrent") == project, "A protected looping default yields after one pass, without waiting forever");

            Set(a, "DefaultMediaId", Get(group, "RuntimeId"));
            foreach (string action in new[] { "PLAY", "PLAY_NEXT", "group switch" })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
                var batch = DefaultBatch(a); var tail = DefaultBatch(b).Single();
                Set(batch[0], "BackendFrameId", 1000L);
                QueueState(batch[0], batch[1], tail); SetField("interactiveActive", true); SetField("interactiveNextQueueTask", null);
                request = ProtectedRequest(GroupBatch(media[2])[0]);
                if (action == "group switch")
                {
                    Invoke(owner, "SwitchInteractiveProjectGroup", b, "protected idle switch");
                    request = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single();
                    Invoke(Field(owner, "interactiveTriggerRequests"), "Clear");
                    Check((bool)Get(((IList)Get(request, "Entries"))[0], "IsGroupTransition"), "A protected default defers the real configured group transition");
                }
                string method = action == "PLAY_NEXT" ? "AppendInteractiveRouteAsync" : "StartInteractiveImmediatelyAsync";
                var task = (Task)Invoke(owner, method, request);
                var firstTarget = ((IList)Get(request, "Entries"))[0];
                Check(!task.IsCompleted && Field(owner, "interactiveCurrent") == batch[0] && Field(owner, "interactivePending") == batch[1] &&
                    QueuedEntries()[0] == firstTarget && (bool)Get(Get(batch[0], "GroupLoop"), "Cancelled"),
                    action + " waits for the complete protected default pass and cancels further repeats");
                Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                    Enum.Parse(Nested("BackendInputEventType"), "Started"), 1001L, Get(batch[1], "InputSummary") }));
                Check(!task.IsCompleted && Field(owner, "interactiveCurrent") == batch[1], action + " cannot start at an internal default group boundary");
                ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
                Check(command.StartsWith("PLAY_NEXT " + Get(firstTarget, "Path")), action + " is prequeued after the final protected default member");
                Invoke(owner, "ApplyInteractiveOutputStarted", Activator.CreateInstance(Nested("BackendInputEvent"), new object[] {
                    Enum.Parse(Nested("BackendInputEventType"), "Started"), 1002L, Get(firstTarget, "InputSummary") }));
                CompleteSoon(task, action + " releases its protected wait").GetAwaiter().GetResult();
                Check(Field(owner, "interactiveCurrent") == firstTarget, action + " proceeds once the protected default has fully finished");
            }
            Set(group, "FileGroupPlayAll", false);
            Check(DefaultBatch(a).Length == 1 && (bool)Get(DefaultBatch(a).Single(), "PreventInterrupt"), "One-file default group mode protects the selected file");
            Set(group, "PreventInterrupt", false);
            Check(DefaultBatch(a).All(entry => !(bool)Get(entry, "PreventInterrupt")), "Default file groups also honor permission to interrupt");
        }
        finally
        {
            Set(a, "DefaultMediaId", originalDefault); Set(dayMedia, "PreventInterrupt", originalProtection); media.Remove(group);
            Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false);
            SynchronizationContext.SetSynchronizationContext(context);
        }
        Console.WriteLine("PASS: default library interruption policy, protected single-file loop handoff, complete default group protection for PLAY/PLAY_NEXT and group-switch transitions.");
    }
    private static void CheckProjectGroups(Assembly assembly)
    {
        object oldGroups = Field(owner, "interactiveProjectGroups"), oldTransitions = Field(owner, "interactiveGroupTransitions"), oldRoutes = Field(owner, "interactiveRoutes");
        object oldActive = Field(owner, "activeInteractiveProjectGroupId"), oldVersion = Field(owner, "interactiveProjectGroupVersion");
        object oldSwitchRandom = Field(owner, "interactiveGroupSwitchRandom"), oldSwitchHotkey = Field(owner, "interactiveGroupSwitchHotkey");
        object[] originalMedia = media.Cast<object>().ToArray();
        var context = SynchronizationContext.Current;
        try
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var a = ProjectGroup("Day", Keys.F21, 0); var b = ProjectGroup("Night", Keys.F22, 2); var c = ProjectGroup("Other", Keys.F24, 3);
            var state = New("ConnectionTabState");
            var groups = (IList)Get(state, "InteractiveProjectGroups"); groups.Add(a); groups.Add(b); groups.Add(c);
            Set(state, "ActiveInteractiveProjectGroupId", Get(a, "Id"));
            SetField("interactiveProjectGroups", groups); SetField("interactiveGroupTransitions", Get(state, "InteractiveGroupTransitions"));
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            SetField("interactiveGroupSwitchRandom", false); SetField("interactiveGroupSwitchHotkey", null);
            foreach (var expected in new[] { b, c, a, b })
            {
                var selected = Invoke(owner, "SelectNextInteractiveProjectGroup");
                Check(selected == expected, "General group switching follows list order and wraps around");
                SetField("activeInteractiveProjectGroupId", Get(selected, "Id"));
            }
            SetField("interactiveGroupSwitchRandom", true);
            var randomGroups = new HashSet<object>();
            for (int i = 0; i < 120; i++)
            {
                object current = Field(owner, "activeInteractiveProjectGroupId");
                var selected = Invoke(owner, "SelectNextInteractiveProjectGroup"); randomGroups.Add(selected);
                Check(!Equals(current, Get(selected, "Id")), "Random group switching never selects the current group");
                SetField("activeInteractiveProjectGroupId", Get(selected, "Id"));
            }
            Check(randomGroups.Count == 3, "Random group switching can reach every group");
            groups.Remove(c); groups.Remove(b);
            Check(Invoke(owner, "SelectNextInteractiveProjectGroup") == null, "One group has no switch target");
            groups.Add(b); groups.Add(c);
            SetField("interactiveGroupSwitchRandom", false); SetField("activeInteractiveProjectGroupId", Get(a, "Id"));
            var pairs = (IList)Invoke(owner, "BuildInteractiveTransitionPairs");
            Check(pairs.Count == 6 && pairs.Cast<object>().Select(item => Get(item, "FromId") + "/" + Get(item, "ToId")).Distinct().Count() == 6,
                "Three groups expose exactly six independent directed transitions");
            var ab = pairs.Cast<object>().First(item => Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
            var ba = pairs.Cast<object>().First(item => Equals(Get(item, "FromId"), Get(b, "Id")) && Equals(Get(item, "ToId"), Get(a, "Id")));
            Set(ab, "MediaId", Get(media[0], "RuntimeId")); Set(ba, "MediaId", Get(media[2], "RuntimeId"));
            ((IList)Field(owner, "interactiveGroupTransitions")).Add(ab); ((IList)Field(owner, "interactiveGroupTransitions")).Add(ba);
            var forward = Invoke(owner, "BuildInteractiveGroupTransition", a, b); var reverse = Invoke(owner, "BuildInteractiveGroupTransition", b, a);
            var transitionEntry = ((IList)Get(forward, "Entries"))[0];
            Check((string)Get(transitionEntry, "Path") == (string)Get(media[0], "Path") &&
                (string)Get(((IList)Get(reverse, "Entries"))[0], "Path") == (string)Get(media[2], "Path") && Invoke(owner, "BuildInteractiveGroupTransition", a, c) == null,
                "Each direction uses its own video and an unset direction has no transition");
            Check((bool)Get(forward, "Immediate") && (bool)Get(transitionEntry, "PreventInterrupt") == (bool)Get(media[0], "PreventInterrupt") && !(bool)Get(transitionEntry, "Loop"),
                "A transition plays once and inherits library interruption protection");
            object previousMedia = Get(ab, "MediaId");
            Set(ab, "MediaId", Guid.NewGuid());
            int groupVersion = (int)Field(owner, "interactiveProjectGroupVersion");
            Invoke(owner, "SwitchInteractiveProjectGroup", b, "missing transition");
            Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")) &&
                (int)Field(owner, "interactiveProjectGroupVersion") == groupVersion,
                "A missing transition video retains the current group without dispatching playback");
            Set(ab, "MediaId", previousMedia);
            using (var dialog = (Form)Activator.CreateInstance(Nested("GroupTransitionsDialog"), new object[] { groups, Invoke(owner, "BuildInteractiveTransitionPairs"), media }))
            {
                var view = (ListView)Field(dialog, "pairs"); IntPtr unused = view.Handle;
                Check(view.Items.Count == 6, "The transition editor displays all six directed pairs");
                view.Items[0].Selected = true;
                var box = (ComboBox)Field(dialog, "mediaBox");
                Check(box.Enabled && box.DropDownStyle == ComboBoxStyle.DropDownList, "Transition media is selected only from the library");
                box.SelectedIndex = 3;
                Check(Equals(Get(ab, "MediaId"), previousMedia), "Cancelling transition editing cannot mutate saved library references");
                Check(Equals(Get(((IList)Get(dialog, "Transitions"))[0], "MediaId"), Get(media[2], "RuntimeId")), "Choosing library media edits only the detached transition");
                box.SelectedIndex = 0;
                Check((Guid)Get(((IList)Get(dialog, "Transitions"))[0], "MediaId") == Guid.Empty, "Selecting no transition clears the optional mapping");
            }
            var route = ((IList)Get(a, "Routes"))[0]; Set(route, "NextCombinationIndex", 8);
            var copy = CopyProject(route); var ids = (IList)Get(((IList)Get(copy, "Combinations"))[0], "MediaIds");
            ids.Add(Get(media[1], "RuntimeId")); Set(Get(copy, "Hotkey"), "KeyCode", Keys.F20);
            Check(((IList)Get(((IList)Get(route, "Combinations"))[0], "MediaIds")).Count == 1 &&
                (Keys)Get(Get(route, "Hotkey"), "KeyCode") == Keys.F23 && (int)Get(copy, "NextCombinationIndex") == 0 && (int)Get(route, "NextCombinationIndex") == 8,
                "Copied projects have independent combinations, shortcuts and selection progress");

            foreach (var item in media) ((IList)Get(state, "InteractiveMedia")).Add(item);
            Set(state, "ActiveInteractiveProjectGroupId", Get(b, "Id"));
            var switchHotkey = New("InteractiveHotkey"); Set(switchHotkey, "KeyCode", Keys.F20);
            Set(switchHotkey, "Modifiers", Enum.ToObject(Nested("HotkeyModifiers"), 7));
            Set(state, "InteractiveGroupSwitchRandom", true); Set(state, "InteractiveGroupSwitchHotkey", switchHotkey);
            var saved = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            Invoke(owner, "LoadInteractivePlaybackElement", XElement.Parse(saved.ToString()));
            Check(((IList)Field(owner, "interactiveProjectGroups")).Count == 3 &&
                Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(b, "Id")) &&
                ((IList)Field(owner, "interactiveGroupTransitions")).Count == 2 &&
                (string)Get(((IList)Field(owner, "interactiveRoutes"))[0], "Name") == "Night project",
                "Connection XML preserves groups, active selection, project lists, shortcuts and directed videos");
            Check((bool)Field(owner, "interactiveGroupSwitchRandom") &&
                (Keys)Get(Field(owner, "interactiveGroupSwitchHotkey"), "KeyCode") == Keys.F20 &&
                Convert.ToInt32(Get(Field(owner, "interactiveGroupSwitchHotkey"), "Modifiers")) == 7,
                "Connection XML preserves general switch mode and shortcut modifiers");
            var invalid = XElement.Parse(saved.ToString()); invalid.SetAttributeValue("activeGroup", Guid.NewGuid());
            invalid.Element("Transitions").Add(new XElement("Transition", new XAttribute("from", Get(a, "Id")), new XAttribute("to", Guid.NewGuid()), new XAttribute("media", Get(media[0], "RuntimeId"))));
            Invoke(owner, "LoadInteractivePlaybackElement", invalid);
            Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")) && ((IList)Field(owner, "interactiveGroupTransitions")).Count == 2,
                "Invalid selected groups fall back safely and orphan transitions are ignored");
            var legacy = new XElement("InteractivePlayback", saved.Element("Media"), new XElement("Routes", Save(route)));
            Invoke(owner, "LoadInteractivePlaybackElement", legacy);
            Check(((IList)Field(owner, "interactiveProjectGroups")).Count == 1 &&
                Save(((IList)Field(owner, "interactiveRoutes"))[0]).ToString() == Save(route).ToString(),
                "Legacy project configurations migrate into a default group without changing media references or shortcuts");
            Invoke(owner, "LoadInteractivePlaybackElement", new object[] { null });
            Check(((IList)Field(owner, "interactiveProjectGroups")).Count == 1 && ((IList)Field(owner, "interactiveRoutes")).Count == 0,
                "A missing configuration creates one empty default group");
            Check(!(bool)Field(owner, "interactiveGroupSwitchRandom") && Field(owner, "interactiveGroupSwitchHotkey") == null,
                "Legacy and missing settings default to sequential switching without a shortcut");
            var otherState = New("ConnectionTabState");
            Invoke(owner, "BindInteractiveProjectGroups", otherState);
            Check(!(bool)Field(owner, "interactiveGroupSwitchRandom") && Field(owner, "interactiveGroupSwitchHotkey") == null,
                "A different connection has independent group switching settings");
            Invoke(owner, "BindInteractiveProjectGroups", state);
            Check((bool)Field(owner, "interactiveGroupSwitchRandom") && Field(owner, "interactiveGroupSwitchHotkey") == switchHotkey,
                "Returning to a connection restores its general switch settings");
            media.Clear(); foreach (var item in originalMedia) media.Add(item);
            // XML loading replaces the working route list; restore the fixture's source project.
            ((IList)Get(a, "Routes")).Clear(); ((IList)Get(a, "Routes")).Add(route);
            // Restore the three real groups for OS shortcut and playback boundary checks.
            SetField("interactiveProjectGroups", groups); SetField("interactiveGroupTransitions", Get(state, "InteractiveGroupTransitions"));
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            ((IList)Field(owner, "interactiveGroupTransitions")).Clear();
            var uiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
                {
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                    using (var dialog = (Form)Activator.CreateInstance(Nested("GroupSwitchSettingsDialog"), new object[] { true, switchHotkey }))
                    {
                        dialog.Size = dialog.MinimumSize; IntPtr dialogHandle = dialog.Handle; dialog.PerformLayout();
                        var modes = (ComboBox)Field(dialog, "modeBox"); var hotkeyBox = (TextBox)Field(dialog, "hotkeyBox");
                        Check((bool)Get(dialog, "RandomOrder") && (Keys)Get(Get(dialog, "Hotkey"), "KeyCode") == Keys.F20,
                            "The switch editor loads mode and shortcut in " + language);
                        Check(modes.Width > 150 && hotkeyBox.Width > 150 && hotkeyBox.Bottom < dialog.ClientSize.Height,
                            "The switch editor keeps its settings visible at minimum size in " + language);
                        modes.SelectedIndex = 0;
                        Invoke(hotkeyBox, "CaptureKey", hotkeyBox, new KeyEventArgs(Keys.Back));
                        Check(!(bool)Get(dialog, "RandomOrder") && Get(dialog, "Hotkey") == null && (Keys)Get(switchHotkey, "KeyCode") == Keys.F20,
                            "Editing or clearing a detached shortcut leaves the saved shortcut unchanged");
                    }
                }
            }
            finally { Thread.CurrentThread.CurrentUICulture = uiCulture; }
            SetField("interactiveGroupSwitchRandom", false); SetField("interactiveGroupSwitchHotkey", switchHotkey);
            Check((bool)Invoke(owner, "IsInteractiveGroupSwitchHotkey", Invoke(switchHotkey, "Clone")), "Group and project edits detect the general switch shortcut as reserved");
            SetField("interactiveWindowOpen", false);
            using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
            using (var probe = new Form())
            {
                SetField("interactiveForm", window); window.StartPosition = FormStartPosition.Manual;
                window.Location = new System.Drawing.Point(-32000, -32000); window.ShowInTaskbar = false;
                IntPtr unused = window.Handle; IntPtr probeHandle = probe.Handle;
                Invoke(window, "ReloadAll");
                Check(((IDictionary)Field(window, "registeredProjectGroups")).Count == 0 && !(bool)Field(window, "groupSwitchHotkeyRegistered"),
                    "Hidden windows do not bind group or general switch shortcuts");
                window.Show(); window.Size = window.MinimumSize; window.PerformLayout();
                Check(((ListView)Field(window, "routeView")).Height > 90 && ((ListView)Field(window, "mediaView")).Height > 90 &&
                    ((FlowLayoutPanel)Field(window, "projectGroupButtons")).Height > 20,
                    "Group switching and both editors remain visible at the minimum window size: routes=" + ((ListView)Field(window, "routeView")).Height +
                    " routeWidth=" + ((ListView)Field(window, "routeView")).Width + " media=" + ((ListView)Field(window, "mediaView")).Height + " mediaWidth=" + ((ListView)Field(window, "mediaView")).Width + " groups=" + ((FlowLayoutPanel)Field(window, "projectGroupButtons")).Height);
                Check(((IDictionary)Field(window, "registeredProjectGroups")).Count == 3 && ((IDictionary)Field(window, "registeredRoutes")).Count == 1,
                    "The open window binds all group shortcuts and only the active group's project shortcuts: groups=" + ((IDictionary)Field(window, "registeredProjectGroups")).Count +
                    " routes=" + ((IDictionary)Field(window, "registeredRoutes")).Count + " log=" + ((TextBox)Field(window, "operationBox")).Text);
                Check(!RegisterHotKey(probeHandle, 0x7201, 7, (uint)Keys.F21), "A group switching shortcut is actually registered with Windows");
                Check((bool)Field(window, "groupSwitchHotkeyRegistered") && !RegisterHotKey(probeHandle, 0x7202, 7, (uint)Keys.F20),
                    "The general switch shortcut is actually registered with Windows");
                CheckInteractiveHotkeyMaster(window, probe, a, b);
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x6101, IntPtr.Zero));
                Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(b, "Id")) &&
                    ((IDictionary)Field(window, "registeredRoutes"))[0x5100] == ((IList)Get(b, "Routes"))[0],
                    "A group shortcut switches the displayed projects and rebinds the shared project shortcut to the target version");
                var buttons = (FlowLayoutPanel)Field(window, "projectGroupButtons"); ((Button)buttons.Controls[0]).PerformClick();
                Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")), "Clicking a group button switches back manually");
                ((Button)Field(window, "switchProjectGroupButton")).PerformClick();
                Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(b, "Id")), "The general switch button selects the next group");
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x6000, IntPtr.Zero));
                Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(c, "Id")), "The general switch shortcut uses the same sequence as the button");
                SetField("interactiveGroupSwitchRandom", true);
                ((Button)Field(window, "switchProjectGroupButton")).PerformClick();
                Check(!Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(c, "Id")), "The button respects random switching without repeating the current group");
                SetField("interactiveGroupSwitchRandom", false);
                ((Button)((FlowLayoutPanel)Field(window, "projectGroupButtons")).Controls[0]).PerformClick();
                window.Hide();
                Check(((IDictionary)Field(window, "registeredProjectGroups")).Count == 0 &&
                    RegisterHotKey(probeHandle, 0x7201, 7, (uint)Keys.F21), "Hiding the window releases all group shortcuts");
                UnregisterHotKey(probeHandle, 0x7201);
                Check(!(bool)Field(window, "groupSwitchHotkeyRegistered") && RegisterHotKey(probeHandle, 0x7202, 7, (uint)Keys.F20),
                    "Hiding the window releases the general switch shortcut");
                UnregisterHotKey(probeHandle, 0x7202);
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x6000, IntPtr.Zero));
                Invoke(window, "WndProc", Message.Create(window.Handle, 0x0312, (IntPtr)0x6101, IntPtr.Zero));
                Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(a, "Id")), "A late group shortcut cannot switch hidden interaction");
            }
            SetField("interactiveForm", null); SetField("interactiveWindowOpen", true);
            SetField("processingInteractiveTriggers", true); // Inspect the exact requests before allowing network dispatch.
            SetField("interactiveActive", true);
            string video = Path.GetTempFileName();
            var transitionMedia = New("InteractiveMediaItem"); Set(transitionMedia, "Path", video); Set(transitionMedia, "Title", "Transition"); Set(transitionMedia, "PreventInterrupt", true); media.Add(transitionMedia);
            try
            {
                Set(ab, "MediaId", Get(transitionMedia, "RuntimeId")); Set(ba, "MediaId", Get(transitionMedia, "RuntimeId"));
                ((IList)Field(owner, "interactiveGroupTransitions")).Add(ab); ((IList)Field(owner, "interactiveGroupTransitions")).Add(ba);
                Invoke(owner, "SwitchNextInteractiveProjectGroup", "test");
                var request = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Single();
                Check((bool)Get(request, "IsGroupTransition") && ((IList)Get(request, "Entries")).Count == 1, "Switching requests the configured transition exactly once");
                Invoke(owner, "SwitchInteractiveProjectGroup", b, "test");
                Check(((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Count() == 1, "Selecting the current group does not replay its transition");
                Invoke(owner, "RequestInteractiveRoute", ((IList)Get(b, "Routes"))[0], "target test");
                var oldTrigger = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().Last();
                Invoke(owner, "SwitchInteractiveProjectGroup", a, "test");
                var requests = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
                Check(requests.Length == 2 && requests.All(item => (bool)Get(item, "IsGroupTransition")) &&
                    (bool)Invoke(owner, "IsStaleInteractiveGroupRequest", oldTrigger),
                    "Rapid switches preserve transition order, discard waiting old-group project triggers and invalidate in-flight verification");
                Invoke(owner, "ClearInteractiveRuntime", true);
                Set(transitionEntry, "BackendFrameId", 123L);
                QueueState(transitionEntry, null); SetField("interactiveActive", true);
                var next = GroupBatch(media[2])[0];
                var handoff = Invoke(owner, "PrepareInteractiveProtectedHandoff", ProtectedRequest(next));
                string command = null;
                Func<string, Task<string>> send = value => { command = value; return Task.FromResult("OK queued"); };
                SynchronizationContext.SetSynchronizationContext(null);
                ((Task)Invoke(owner, "QueueInteractiveNextCoreAsync", send)).GetAwaiter().GetResult();
                Check(command.StartsWith("PLAY_NEXT ") && Field(owner, "interactivePending") == next && Field(owner, "interactiveCurrent") == transitionEntry,
                    "A target project waits for the full transition output boundary instead of interrupting the transition");
                Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "playing", next));
                Check(Field(owner, "interactiveCurrent") == next && HandoffTask(handoff).IsCompleted,
                    "Verified target output completes the protected transition handoff");
                Invoke(owner, "ClearInteractiveRuntime", true);
                QueueState(transitionEntry, null); SetField("interactiveActive", true);
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(next, "ended", next)) && Field(owner, "interactiveCurrent") == transitionEntry,
                    "An ended status from an older input cannot complete a newly requested transition");
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(transitionEntry, "ended", transitionEntry) + " actual_frame_id=122") && Field(owner, "interactiveCurrent") == transitionEntry,
                    "An old invocation of the same transition cannot release the new transition's protection");
                Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(transitionEntry, "ended", transitionEntry)) &&
                    !(bool)Field(owner, "interactiveActive") && Field(owner, "interactiveCurrent") == null,
                    "A finished standalone transition releases protection immediately without the ten-second mismatch delay");
                Invoke(owner, "ClearInteractiveRuntime", true);
                Set(transitionEntry, "BackendFrameId", 0L); Set(transitionEntry, "MinimumBackendFrameId", 123L);
                QueueState(transitionEntry, null); SetField("interactiveActive", true);
                Check(!(bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(transitionEntry, "ended", transitionEntry) + " actual_frame_id=123"),
                    "An older identical transition cannot complete a new invocation before its first output is observed");
                Check((bool)Invoke(owner, "ApplyInteractiveBackendStatus", BackendStatus(transitionEntry, "ended", transitionEntry) + " actual_frame_id=124") &&
                    Field(owner, "interactiveCurrent") == null,
                    "A very short transition can finish between status polls without leaving protection stuck");
                SetField("processingInteractiveTriggers", false);
                CheckProjectGroupDispatch(a, b);
                CheckGroupDefaults(a, b);
            }
            finally { media.Remove(transitionMedia); File.Delete(video); SetField("processingInteractiveTriggers", false); }
            var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
                foreach (string key in new[] { "InteractiveProjectGroups", "InteractiveDefaultGroup", "InteractiveAddGroup", "InteractiveEditGroup", "InteractiveRemoveGroup", "InteractiveGroupTransitions",
                    "InteractiveGroupName", "InteractiveCopyCurrentGroup", "InteractiveCopyProject", "InteractiveCopyProjectHint", "InteractiveGroupNameExists", "InteractiveGroupHotkeyConflict",
                    "InteractiveConfirmRemoveGroup", "InteractiveGroupTransitionTitle", "InteractiveGroupSwitch", "InteractiveGroupSwitched", "InteractiveTransitionFileMissing", "InteractiveTransitionHint",
                    "InteractiveTransitionFrom", "InteractiveTransitionTo", "InteractiveChooseTransition", "InteractiveClearTransition", "InteractiveNoTransitionMedia", "InteractiveTransitionMediaMissing",
                    "InteractiveGroupDefaultVideo", "InteractiveGroupDefaultTitle", "InteractiveGroupDefaultHint", "InteractiveDefaultVideoMissing", "InteractiveNoDefaultMedia",
                    "InteractiveGroupSwitchSettings", "InteractiveGroupSwitchMode", "InteractiveGroupSwitchHint" })
                {
                    string text = resources.GetString(key, CultureInfo.GetCultureInfo(language));
                    Check(!string.IsNullOrWhiteSpace(text), "A group resource exists: " + language + "/" + key);
                    if (language != "en") Check(text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'), "Chinese group UI uses translated resx strings");
                }
        }
        finally
        {
            SetField("interactiveForm", null); Invoke(owner, "ClearInteractiveRuntime", true);
            SetField("interactiveProjectGroups", oldGroups); SetField("interactiveGroupTransitions", oldTransitions); SetField("interactiveRoutes", oldRoutes);
            SetField("activeInteractiveProjectGroupId", oldActive); SetField("interactiveProjectGroupVersion", oldVersion);
            SetField("interactiveGroupSwitchRandom", oldSwitchRandom); SetField("interactiveGroupSwitchHotkey", oldSwitchHotkey);
            SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", false);
            media.Clear(); foreach (var item in originalMedia) media.Add(item);
            SynchronizationContext.SetSynchronizationContext(context);
        }
        Console.WriteLine("PASS: project group migration/persistence, random/sequential switch controls, per-connection settings, translated editors, Windows hotkey lifecycle, six directed transitions and protected transition handoff.");
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
            CheckRandomSelectionPolicies();
            CheckEditor(assembly);
            CheckFileGroups(assembly);
            CheckGroupLooping();
            CheckGroupQueueCommands();
            CheckPlaybackDuringConfigurationEdits();
            CheckInteractiveDisplayOrder();
            CheckInteractiveEventOrdering();
            CheckInterruptProtection();
            CheckFileGroupPhases(assembly);
            CheckInteractiveCombos();
            CheckActualOutputVerification();
            CheckPlaybackWatchdog();
            CheckBackendRestartRecovery();
            CheckProjectGroups(assembly);
            CheckPersistence();
            CheckInteractiveObsScenes(assembly);
            Console.WriteLine("All interactive combination checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
