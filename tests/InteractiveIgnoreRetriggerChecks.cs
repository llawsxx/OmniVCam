using System;
using System.Collections;
using System.Globalization;
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
            Check(WaitingTriggerCount() == 2, "A project waiting in the queue can still be triggered repeatedly");
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
        Console.WriteLine("PASS: repeat-trigger suppression across commands, phases, looping, combo precedence, project identity, queue preservation, opt-out, save/copy/legacy configuration and translated editors.");
    }
}
