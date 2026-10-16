using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
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
                foreach (string key in new[] { "InteractiveCombinationSelection", "InteractiveRandom", "InteractiveSequential", "InteractiveCombinations", "InteractiveAddCombination", "InteractiveDuplicateCombination", "InteractiveEmptyCombination" })
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
            // Avoid connecting, loading user configuration or starting application timers.
            owner = FormatterServices.GetUninitializedObject(main);
            media = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveMediaItem")));
            foreach (string title in new[] { "A", "B", "A2", "B2" })
            {
                var item = New("InteractiveMediaItem"); Set(item, "Title", title); Set(item, "Path", title + ".mp4"); media.Add(item);
            }
            main.GetField("interactiveMedia", PrivateInstance).SetValue(owner, media);
            main.GetField("interactiveRoutes", PrivateInstance).SetValue(owner, Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("InteractiveRouteItem"))));
            main.GetField("random", PrivateInstance).SetValue(owner, new Random(17));
            CheckSelection();
            CheckEditor(assembly);
            CheckPersistence();
            Console.WriteLine("All interactive combination checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
