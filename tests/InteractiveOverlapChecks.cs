using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;

internal static partial class InteractivePlaybackChecks
{
    private static void CheckInteractiveGroupOverlap(object a, object b)
    {
        object enabledA = Get(a, "OverlapEnabled"), secondsA = Get(a, "OverlapSeconds"), enabledB = Get(b, "OverlapEnabled"), secondsB = Get(b, "OverlapSeconds");
        object active = Field(owner, "activeInteractiveProjectGroupId"), routes = Field(owner, "interactiveRoutes"), defaultB = Get(b, "DefaultMediaId");
        var culture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Set(a, "OverlapEnabled", false); Set(a, "OverlapSeconds", 0.375m);
            Set(b, "OverlapEnabled", true); Set(b, "OverlapSeconds", 0.275m);
            SetField("activeInteractiveProjectGroupId", Get(a, "Id")); SetField("interactiveRoutes", Get(a, "Routes"));
            var disabled = ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", ((IList)Get(a, "Routes"))[0])).Cast<object>().ToArray();
            Check(disabled.All(item => ((string)Get(item, "Options")).Split(',').Contains("overlap=0")), "A disabled group explicitly disables backend overlap");
            SetField("activeInteractiveProjectGroupId", Get(b, "Id")); SetField("interactiveRoutes", Get(b, "Routes"));
            var enabled = ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", ((IList)Get(b, "Routes"))[0])).Cast<object>().ToArray();
            Check(enabled.All(item => ((string)Get(item, "Options")).Split(',').Contains("overlap=0.275") &&
                Equals(Get(item, "InputSummary"), main.GetMethod("BuildInputSummary", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new[] { Get(item, "Path"), Get(item, "Options") }))), "Group duration reaches playback commands and matching input identities");
            Set(b, "OverlapSeconds", 0.6m); Set(b, "OverlapEnabled", false);
            Check(enabled.All(item => ((string)Get(item, "Options")).Contains("overlap=0.275")), "Editing group settings preserves accepted playback snapshots");
            Set(b, "OverlapEnabled", true);
            var transition = Invoke(owner, "BuildInteractiveGroupTransition", a, b);
            Check(((IEnumerable)Get(transition, "Entries")).Cast<object>().All(item => ((string)Get(item, "Options")).Contains("overlap=0.6")),
                "Incoming transitions follow the destination group instead of the source group");
            var mapping = ((IList)Field(owner, "interactiveGroupTransitions")).Cast<object>().First(item =>
                Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
            Set(b, "DefaultMediaId", Get(mapping, "MediaId"));
            Check(((IEnumerable)Invoke(owner, "BuildInteractiveGroupDefault", b, false)).Cast<object>().All(item => ((string)Get(item, "Options")).Contains("overlap=0.6")),
                "Group defaults share the group's overlap duration");
            var library = PhasedGroup("AfterMiddle"); Set(library, "Loop", true); Set(library, "Options", "overlap=3,custom=1");
            var batch = GroupBatch(library);
            InvokeStaticGroupOverlap(batch, a);
            var run = Get(batch[0], "PhaseRun");
            var middle = Invoke(owner, "CreateInteractivePhasedEntry", run, "later-middle.mp4", Enum.Parse(Nested("InteractiveGroupPhase"), "Middle"));
            Check(((string)Get(middle, "Options")).Contains("overlap=0") && ((string)Get(Get(run, "Outro"), "Options")).Contains("overlap=0") &&
                (string)Get(library, "Options") == "overlap=3,custom=1", "Deferred middles/outros/loops inherit the snapshot without changing library media");
            var state = New("ConnectionTabState"); Set(state, "InteractiveMedia", media);
            Set(state, "InteractiveProjectGroups", Field(owner, "interactiveProjectGroups"));
            var xml = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            var isolated = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(main);
            SetFieldOn(isolated, "interactiveMedia", Activator.CreateInstance(media.GetType()));
            SetFieldOn(isolated, "interactiveRoutes", Activator.CreateInstance(routes.GetType()));
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            var loaded = ((IList)Field(isolated, "interactiveProjectGroups")).Cast<object>().First(item => Equals(Get(item, "Id"), Get(a, "Id")));
            Check(!(bool)Get(loaded, "OverlapEnabled") && (decimal)Get(loaded, "OverlapSeconds") == 0.375m, "Each connection persists independent group switches and fractional durations");
            foreach (var group in xml.Element("Groups").Elements("Group")) { group.Attribute("overlapEnabled").Remove(); group.Attribute("overlapSeconds").Remove(); }
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            Check(((IList)Field(isolated, "interactiveProjectGroups")).Cast<object>().All(item => (bool)Get(item, "OverlapEnabled") && (decimal)Get(item, "OverlapSeconds") == 0.1m),
                "Legacy groups default to enabled 0.1-second overlap");
            CheckInteractiveOverlapHierarchy(a, b);
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using (var dialog = (Form)Activator.CreateInstance(Nested("ProjectGroupEditDialog"), new object[] { media, a }))
                {
                    var toggle = (CheckBox)Field(dialog, "overlapBox"); var duration = (NumericUpDown)Field(dialog, "overlapSecondsBox");
                    Check(!toggle.Checked && !duration.Enabled && duration.Value == 0.375m, "Group editor loads the saved disabled duration in " + language);
                    toggle.Checked = true; duration.Value = 0.625m;
                    Check(duration.Enabled && (decimal)Get(dialog, "OverlapSeconds") == 0.625m && !(bool)Get(a, "OverlapEnabled") && (decimal)Get(a, "OverlapSeconds") == 0.375m,
                        "Group edits and cancel remain detached from live settings");
                    IntPtr handle = dialog.Handle; dialog.PerformLayout();
                    var bounds = duration.Bounds;
                    for (var parent = duration.Parent; parent != dialog; parent = parent.Parent) bounds.Offset(parent.Location);
                    Check(dialog.ClientRectangle.Contains(bounds), "Duration selector fits within the translated group editor");
                }
            }
        }
        finally
        {
            Set(a, "OverlapEnabled", enabledA); Set(a, "OverlapSeconds", secondsA); Set(b, "OverlapEnabled", enabledB); Set(b, "OverlapSeconds", secondsB);
            Set(b, "DefaultMediaId", defaultB); SetField("activeInteractiveProjectGroupId", active); SetField("interactiveRoutes", routes);
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        Console.WriteLine("PASS: per-group overlap switches/durations, incoming transitions, defaults, deferred group phases and loop snapshots, independent persistence/legacy defaults and translated editors.");
    }
    private static object InvokeOverlapStatic(string name, params object[] args)
    {
        return main.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    }

    private static void CheckInteractiveOverlapHierarchy(object a, object b)
    {
        var parentRoutes = (IList)Get(b, "Routes");
        var route = Load(Save(parentRoutes[0]));
        var combinations = (IList)Get(route, "Combinations");
        while (combinations.Count > 1) combinations.RemoveAt(combinations.Count - 1);
        var combination = combinations[0];
        Set(route, "RandomOrder", false);
        object enabled = Get(b, "OverlapEnabled"), duration = Get(b, "OverlapSeconds");
        object active = Field(owner, "activeInteractiveProjectGroupId");
        var culture = Thread.CurrentThread.CurrentUICulture;
        parentRoutes.Add(route);
        try
        {
            // Route ownership, like OBS, wins over whichever group the UI currently displays.
            SetField("activeInteractiveProjectGroupId", Get(a, "Id"));
            Set(b, "OverlapSeconds", 0.8m); Set(route, "OverlapSeconds", 0.375m); Set(combination, "OverlapSeconds", 0.625m);
            foreach (bool groupEnabled in new[] { false, true })
                foreach (bool? projectMode in new bool?[] { null, false, true })
                    foreach (bool? combinationMode in new bool?[] { null, false, true })
                    {
                        Set(b, "OverlapEnabled", groupEnabled); Set(route, "OverlapEnabled", projectMode); Set(combination, "OverlapEnabled", combinationMode);
                        decimal expected = combinationMode.HasValue ? (combinationMode.Value ? 0.625m : 0m)
                            : projectMode.HasValue ? (projectMode.Value ? 0.375m : 0m) : groupEnabled ? 0.8m : 0m;
                        Check((decimal)InvokeOverlapStatic("ResolveInteractiveOverlapSeconds", b, route, combination) == expected,
                            "Combination/project/group priority resolves explicit on, off and inheritance");
                        var entries = ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", route)).Cast<object>().ToArray();
                        string option = "overlap=" + expected.ToString("0.###", CultureInfo.InvariantCulture);
                        Check(entries.Length > 0 && entries.All(item => ((string)Get(item, "Options")).Split(',').Contains(option)),
                            "All 18 override combinations reach actual playback entries using the route's owning group");
                    }
            Set(combination, "OverlapEnabled", true); Set(combination, "OverlapSeconds", 0m);
            Check((decimal)InvokeOverlapStatic("ResolveInteractiveOverlapSeconds", b, route, combination) == 0m, "Explicit zero duration disables overlap without inheriting");
            Set(combination, "OverlapSeconds", 0.625m); Set(route, "OverlapEnabled", false);
            var accepted = ((IEnumerable)Invoke(owner, "BuildInteractiveEntries", route)).Cast<object>().ToArray();
            var library = PhasedGroup("AfterMiddle"); Set(library, "Loop", true);
            var batch = GroupBatch(library);
            var typed = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Nested("InteractivePlaybackEntry")));
            foreach (var entry in batch) typed.Add(entry);
            InvokeOverlapStatic("ApplyInteractiveOverlapSeconds", typed, 0.625m);
            Set(combination, "OverlapEnabled", false); Set(combination, "OverlapSeconds", 0.9m);
            Check(accepted.All(item => ((string)Get(item, "Options")).Contains("overlap=0.625")), "Editing a combination preserves accepted playback options");
            var run = Get(batch[0], "PhaseRun");
            var middle = Invoke(owner, "CreateInteractivePhasedEntry", run, "later-middle.mp4", Enum.Parse(Nested("InteractiveGroupPhase"), "Middle"));
            Check(((string)Get(middle, "Options")).Contains("overlap=0.625") && ((string)Get(Get(run, "Outro"), "Options")).Contains("overlap=0.625"),
                "Deferred middles and outros retain the winning combination duration");

            var saved = Save(route); var loaded = Load(saved);
            Check(Equals(Get(loaded, "OverlapEnabled"), false) && (decimal)Get(loaded, "OverlapSeconds") == 0.375m &&
                Equals(Get(((IList)Get(loaded, "Combinations"))[0], "OverlapEnabled"), false) && (decimal)Get(((IList)Get(loaded, "Combinations"))[0], "OverlapSeconds") == 0.9m,
                "Project and combination switches/durations independently survive XML round trips");
            var copy = InvokeOverlapStatic("CopyInteractiveProject", route);
            var comboCopy = InvokeOverlapStatic("CopyInteractiveCombination", combination);
            Set(combination, "OverlapSeconds", 0.4m);
            Check((decimal)Get(((IList)Get(copy, "Combinations"))[0], "OverlapSeconds") == 0.9m && (decimal)Get(comboCopy, "OverlapSeconds") == 0.9m,
                "Project/group copy and combination duplicate retain independent overlap settings");
            foreach (var node in saved.DescendantsAndSelf()) { if (node.Attribute("overlapEnabled") != null) node.Attribute("overlapEnabled").Remove(); if (node.Attribute("overlapSeconds") != null) node.Attribute("overlapSeconds").Remove(); }
            var legacy = Load(saved);
            Check(Get(legacy, "OverlapEnabled") == null && (decimal)Get(legacy, "OverlapSeconds") == 0.1m &&
                Get(((IList)Get(legacy, "Combinations"))[0], "OverlapEnabled") == null, "Legacy project and combination settings inherit the group");
            saved.SetAttributeValue("overlapEnabled", "invalid"); saved.SetAttributeValue("overlapSeconds", "9");
            var invalid = Load(saved);
            Check(Get(invalid, "OverlapEnabled") == null && (decimal)Get(invalid, "OverlapSeconds") == 0.1m, "Malformed override settings safely fall back to inheritance and the default duration");

            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                using (var editor = (Form)Activator.CreateInstance(Nested("RouteEditDialog"), new object[] { media, route }))
                {
                    editor.Size = editor.MinimumSize; IntPtr editorHandle = editor.Handle; editor.PerformLayout();
                    var pendingControls = new Queue<Control>(); pendingControls.Enqueue(editor);
                    var buttonTexts = new[] { (string)InvokeOverlapStatic("T", "InteractiveProjectOverlapSettings"), (string)InvokeOverlapStatic("T", "InteractiveCombinationOverlapSettings") };
                    int visibleButtons = 0;
                    while (pendingControls.Count > 0)
                    {
                        var control = pendingControls.Dequeue();
                        foreach (Control child in control.Controls) pendingControls.Enqueue(child);
                        if (!(control is Button) || !buttonTexts.Contains(control.Text)) continue;
                        visibleButtons++;
                        var rectangle = control.Bounds;
                        for (var parent = control.Parent; parent != null; parent = parent.Parent)
                        {
                            Check(parent.ClientRectangle.Contains(rectangle), "Overlap settings button fits at minimum editor size in " + language + ": " + control.Text);
                            if (parent == editor) break;
                            rectangle.Offset(parent.Location);
                        }
                    }
                    Check(visibleButtons == 2, "Both project and combination expose their overlap settings buttons");
                    foreach (string key in new[] { "InteractiveProjectOverlapSettings", "InteractiveCombinationOverlapSettings", "InteractiveOverlapMode", "InteractiveOverlapInherit", "InteractiveOverlapOn", "InteractiveOverlapOff", "InteractiveOverlapHierarchyHint" })
                    {
                        string text = (string)InvokeOverlapStatic("T", key);
                        Check(text != key && !string.IsNullOrWhiteSpace(text) && (language == "en" || text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff')), "Overlap settings use translated resx: " + language + "/" + key);
                    }
                    Check(Equals(Field(editor, "routeOverlapEnabled"), false) && (decimal)Field(editor, "routeOverlapSeconds") == 0.375m,
                        "Route editor loads its own overlap settings in " + language);
                    var editedCombination = ((IList)Field(editor, "combinations"))[0];
                    Check(Equals(Get(editedCombination, "OverlapEnabled"), false) && (decimal)Get(editedCombination, "OverlapSeconds") == 0.4m,
                        "Combination editor loads its own overlap settings");
                    foreach (bool forCombination in new[] { false, true })
                        using (var dialog = (Form)Invoke(editor, "CreateOverlapDialog", forCombination))
                        {
                            var modes = (ComboBox)Field(dialog, "modeBox"); var seconds = (NumericUpDown)Field(dialog, "secondsBox");
                            Check(modes.SelectedIndex == 2 && !seconds.Enabled, "Disabled override loads without enabling the duration control");
                            modes.SelectedIndex = 1; seconds.Value = 0.725m;
                            Check(Equals(Get(dialog, "OverlapEnabled"), true) && seconds.Enabled && (decimal)Get(dialog, "OverlapSeconds") == 0.725m, "Explicit overlap exposes its duration");
                            modes.SelectedIndex = 0;
                            Check(Get(dialog, "OverlapEnabled") == null && !seconds.Enabled, "Inheritance is distinct from explicitly disabled overlap");
                            IntPtr handle = dialog.Handle; dialog.PerformLayout();
                            var bounds = seconds.Bounds;
                            for (var parent = seconds.Parent; parent != dialog; parent = parent.Parent) bounds.Offset(parent.Location);
                            Check(dialog.ClientRectangle.Contains(bounds), "Translated duration editor fits within its window");
                        }
                    Check(Equals(Get(route, "OverlapEnabled"), false) && (decimal)Get(combination, "OverlapSeconds") == 0.4m,
                        "Canceling either overlap dialog leaves the live project/combination unchanged");
                    editor.GetType().GetField("routeOverlapEnabled", PrivateInstance).SetValue(editor, true); editor.GetType().GetField("routeOverlapSeconds", PrivateInstance).SetValue(editor, 0.525m);
                    Set(editedCombination, "OverlapEnabled", null); Set(editedCombination, "OverlapSeconds", 0.225m);
                    var edited = Load(Save(route)); Invoke(editor, "Apply", edited);
                    Check(Equals(Get(edited, "OverlapEnabled"), true) && (decimal)Get(edited, "OverlapSeconds") == 0.525m &&
                        Get(((IList)Get(edited, "Combinations"))[0], "OverlapEnabled") == null, "Accepting the project editor commits its own and combination overlap settings");
                }
            }
        }
        finally
        {
            parentRoutes.Remove(route); Set(b, "OverlapEnabled", enabled); Set(b, "OverlapSeconds", duration);
            SetField("activeInteractiveProjectGroupId", active); Thread.CurrentThread.CurrentUICulture = culture;
        }
        Console.WriteLine("PASS: 18 overlap inheritance/override cases, route ownership, explicit zero, accepted/deferred snapshots, independent persistence/copy, legacy/malformed settings and detached three-language editors.");
    }

    private static void InvokeStaticGroupOverlap(object entries, object group)
    {
        var typed = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Nested("InteractivePlaybackEntry")));
        foreach (var item in (IEnumerable)entries) typed.Add(item);
        main.GetMethod("ApplyInteractiveGroupOverlap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { typed, group });
    }
}
