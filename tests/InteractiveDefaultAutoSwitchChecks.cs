using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

internal static partial class InteractivePlaybackChecks
{
    private static void CheckDefaultAutoSwitchModel(Assembly assembly, object group, object source, object target)
    {
        object active = Get(group, "ActiveDefaultPlaybackId"), oldReturn = Get(group, "ReturnDefaultPlaybackId");
        var culture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Invoke(owner, "ClearInteractiveRuntime", true); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id")); Set(source, "AutoSwitchEnabled", true);
            Set(source, "AutoSwitchMinimumSeconds", 10); Set(source, "AutoSwitchMaximumSeconds", 10);
            var batch = DefaultBatch(group); QueueState(batch[1], null); SetField("interactiveActive", true);
            long start = Stopwatch.Frequency * 100L;
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start, true);
            Check((Guid)Field(owner, "interactiveTimedDefaultId") == Guid.Empty, "Assigned but unconfirmed default playback cannot start a countdown");
            Invoke(owner, "ObserveInteractiveDefaultOutput", batch[1], start);
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 4 * Stopwatch.Frequency, true);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 4 && ((string)Invoke(owner, "InteractiveDefaultCountdown", source)).Contains("00:00:06"), "Fixed default duration counts from confirmed output and displays remaining seconds");
            CheckDefaultCountdownButton(source, "00:00:06");
            SetField("interactiveCurrent", batch[2]); Invoke(owner, "ObserveInteractiveDefaultOutput", batch[2], start + 4 * Stopwatch.Frequency);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 4, "Advancing inside the same default group cannot restart the countdown");
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 5 * Stopwatch.Frequency, false);
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 50 * Stopwatch.Frequency, false);
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 51 * Stopwatch.Frequency, true);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 4, "Paused playback and its resume gap do not count toward automatic switching");
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 53 * Stopwatch.Frequency, true);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 6, "Resumed playback advances the countdown again");
            Invoke(owner, "MarkInteractivePlaybackMismatch");
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 100 * Stopwatch.Frequency, true);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 6, "Unverified playback does not advance an automatic deadline");
            SetField("interactiveNeedsStatusCheck", false); Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 101 * Stopwatch.Frequency, true);
            Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 105 * Stopwatch.Frequency, true);
            Check(((string)Invoke(owner, "InteractiveDefaultCountdown", source)).Length > 0 && !((string)Invoke(owner, "InteractiveDefaultCountdown", source)).Contains("00:00:"), "An expired protected/deferred switch is displayed as waiting");
            Check(WaitingTriggerCount() == 0 && !(bool)Field(owner, "interactiveDefaultAutoRequested"), "A busy project dispatch has priority over an expired default timer");
            CheckDefaultCountdownButton(source, (string)StaticCall("T", "InteractiveDefaultWaitingSwitch"));
            Invoke(owner, "ObserveInteractiveDefaultOutput", GroupBatch(media[0])[0], start + 106 * Stopwatch.Frequency);
            Check((Guid)Field(owner, "interactiveTimedDefaultId") == Guid.Empty && (string)Invoke(owner, "InteractiveDefaultCountdown", source) == "", "Actual project output clears the old default countdown");
            Invoke(owner, "ObserveInteractiveDefaultOutput", batch[1], start + 107 * Stopwatch.Frequency);
            Check((double)Field(owner, "interactiveDefaultElapsedSeconds") == 0 && (int)Field(owner, "interactiveDefaultDurationSeconds") == 10, "Returning after an interactive project starts a fresh default duration");
            Set(source, "AutoSwitchMinimumSeconds", 300); Set(source, "AutoSwitchMaximumSeconds", 600);
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 200; i++)
            {
                Invoke(owner, "ResetInteractiveDefaultAutoSwitch"); Invoke(owner, "ObserveInteractiveDefaultOutput", batch[1], start);
                int seconds = (int)Field(owner, "interactiveDefaultDurationSeconds");
                Check(seconds >= 300 && seconds <= 600, "Random default durations stay inside the configured inclusive range"); seen.Add(seconds);
                Invoke(owner, "ObserveInteractiveDefaultOutput", batch[2], start + Stopwatch.Frequency);
                Check((int)Field(owner, "interactiveDefaultDurationSeconds") == seconds, "Random duration is chosen once per default session, not once per media file");
            }
            Check(seen.Count > 80, "Entering the default again draws a fresh random duration");
            Set(group, "ReturnDefaultPlaybackId", Get(target, "Id"));
            var xml = (XElement)StaticCall("SaveInteractiveDefaults", group); var copy = New("InteractiveProjectGroup");
            StaticCall("LoadInteractiveDefaults", new XElement("Group", xml), copy);
            Check(((XElement)StaticCall("SaveInteractiveDefaults", copy)).ToString() == xml.ToString(), "Return selection and automatic durations round trip through XML");
            StaticCall("CopyInteractiveDefaults", group, copy);
            Check(Equals(Get(copy, "ReturnDefaultPlaybackId"), Get(target, "Id")) && (int)Get(((IList)Get(copy, "DefaultPlaybacks"))[0], "AutoSwitchMinimumSeconds") == 300, "Detached settings retain automatic and return configuration");
            var malformed = new XElement(xml); malformed.SetAttributeValue("returnAfterProject", Guid.NewGuid());
            var node = malformed.Elements("Default").First(); node.SetAttributeValue("minimumSeconds", -1); node.SetAttributeValue("maximumSeconds", 99999999);
            StaticCall("LoadInteractiveDefaults", new XElement("Group", malformed), copy);
            Check((Guid)Get(copy, "ReturnDefaultPlaybackId") == Guid.Empty && (int)Get(((IList)Get(copy, "DefaultPlaybacks"))[0], "AutoSwitchMinimumSeconds") == 300 && (int)Get(((IList)Get(copy, "DefaultPlaybacks"))[0], "AutoSwitchMaximumSeconds") == 600, "Invalid deadlines and missing return targets fall back safely");
            var legacy = new XElement(xml); legacy.Attribute("returnAfterProject").Remove();
            foreach (var entry in legacy.Elements("Default")) { entry.Attribute("autoSwitch").Remove(); entry.Attribute("minimumSeconds").Remove(); entry.Attribute("maximumSeconds").Remove(); }
            StaticCall("LoadInteractiveDefaults", new XElement("Group", legacy), copy);
            Check((Guid)Get(copy, "ReturnDefaultPlaybackId") == Guid.Empty && !((IList)Get(copy, "DefaultPlaybacks")).Cast<object>().Any(item => (bool)Get(item, "AutoSwitchEnabled")), "Existing defaults keep automatic switching disabled and preserve their return behavior");

            Invoke(owner, "ClearInteractiveRuntime", true); var project = GroupBatch(media[0])[0]; Set(project, "Loop", false);
            QueueState(project, null); SetField("interactiveActive", true); Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(Equals(Get(group, "ActiveDefaultPlaybackId"), Get(target, "Id")) && QueuedEntries().All(entry => Equals(Get(entry, "DefaultPlaybackId"), Get(target, "Id"))), "Finite interactive projects return to the configured default");
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var finiteDefault = DefaultBatch(group); QueueState(finiteDefault.Last(), null);
            Set(source, "LoopMiddleFiles", false); finiteDefault = DefaultBatch(group); QueueState(finiteDefault.Last(), null);
            Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(Equals(Get(group, "ActiveDefaultPlaybackId"), Get(source, "Id")), "A default's own completed pass does not apply the interactive-return target");
            QueueState(project, null); Set(project, "Loop", true); Invoke(owner, "QueueInteractiveGroupDefaultIfNeeded");
            Check(QueuedEntries().Length == 0, "A configured return target cannot terminate an interactive loop");
            Set(source, "LoopMiddleFiles", true);

            var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (string key in new[] { "InteractiveDefaultReturnAfterProject", "InteractiveDefaultCountdown", "InteractiveDefaultWaitingSwitch", "InteractiveDefaultAutoSwitchHint" })
                    Check(!string.IsNullOrWhiteSpace(resources.GetString(key, CultureInfo.GetCultureInfo(language))), "Automatic default UI uses resx: " + language + "/" + key);
                using (var dialog = (Form)Activator.CreateInstance(Nested("DefaultPlaybackSettingsDialog"), new object[] { media, group }))
                {
                    var returnBox = (ComboBox)Field(dialog, "returnDefaultBox"); Check(returnBox.SelectedIndex == 2, "Return picker restores the configured default");
                    returnBox.SelectedIndex = 0; var edited = New("InteractiveProjectGroup"); Invoke(dialog, "Apply", edited);
                    Check((Guid)Get(edited, "ReturnDefaultPlaybackId") == Guid.Empty && Equals(Get(group, "ReturnDefaultPlaybackId"), Get(target, "Id")), "Return selection supports cancel/apply isolation");
                }
                using (var dialog = (Form)Activator.CreateInstance(Nested("DefaultPlaybackEditDialog"), new object[] { media, source }))
                {
                    var min = (NumericUpDown)Field(dialog, "minimumSecondsBox"); var max = (NumericUpDown)Field(dialog, "maximumSecondsBox");
                    min.Value = 700; Check(max.Value == 700, "Duration editor prevents an inverted range");
                    max.Value = 20; Check(min.Value == 20, "Duration editor also normalizes a lowered maximum");
                    ((CheckBox)Field(dialog, "autoSwitchBox")).Checked = true;
                    var edited = StaticCall("CopyInteractiveDefault", source); Invoke(dialog, "Apply", edited);
                    Check((bool)Get(edited, "AutoSwitchEnabled") && (int)Get(edited, "AutoSwitchMinimumSeconds") == 20 && (int)Get(source, "AutoSwitchMinimumSeconds") == 300, "Automatic duration edits apply only to the selected detached item");
                    dialog.Size = dialog.MinimumSize; var handle = dialog.Handle; dialog.PerformLayout();
                    Check(((ComboBox)Field(dialog, "mediaBox")).Width > 200 && max.Bounds.Width >= 90, "Automatic controls remain usable in each language");
                }
            }
        }
        finally
        {
            Set(source, "AutoSwitchEnabled", false); Set(source, "AutoSwitchMinimumSeconds", 300); Set(source, "AutoSwitchMaximumSeconds", 600);
            Set(group, "ReturnDefaultPlaybackId", oldReturn); Set(group, "ActiveDefaultPlaybackId", active);
            SetField("processingInteractiveTriggers", false); Invoke(owner, "ClearInteractiveRuntime", true);
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        Console.WriteLine("PASS: fixed/random default deadlines, once-per-session draws, countdown, pause/recovery handling, fresh interactive returns, fixed return destinations, loop preservation, legacy/malformed XML and detached translated controls.");
    }

    private static async Task CheckDefaultAutoSwitchTcpAsync(StreamReader reader, StreamWriter writer, object group, object source, object target, object library)
    {
        object active = Get(group, "ActiveDefaultPlaybackId");
        try
        {
            Set(source, "AutoSwitchEnabled", true); Set(source, "AutoSwitchMinimumSeconds", 2); Set(source, "AutoSwitchMaximumSeconds", 2);
            foreach (string mode in new[] { "Any", "AfterMiddle", "ImmediateMiddle", "WholeGroup" })
            {
                Invoke(owner, "ClearInteractiveRuntime", true); SetField("processingInteractiveTriggers", false); SetField("interactiveWindowOpen", true);
                Set(source, "MediaId", Get(library, "RuntimeId")); Set(source, "InterruptionMode", Enum.Parse(Nested("InteractiveGroupInterruptionMode"), mode));
                Set(library, "FileGroupPlayAll", true); Set(library, "FileGroupMinimumMiddleFiles", 0);
                Set(group, "DefaultSwitchRandom", false); Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
                var batch = DefaultBatch(group); Set(batch[1], "BackendFrameId", 800L); Set(batch[2], "BackendFrameId", 801L);
                QueueState(batch[1], batch[2], batch.Skip(3).ToArray()); SetField("interactiveActive", true);
                long start = Stopwatch.GetTimestamp(); Invoke(owner, "ObserveInteractiveDefaultOutput", batch[1], start);
                Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 2 * Stopwatch.Frequency, true);
                Check((bool)Field(owner, "interactiveDefaultAutoRequested") && Equals(Get(group, "ActiveDefaultPlaybackId"), Get(target, "Id")), "At expiry the next default is selected once: " + mode);
                string command = await CompleteSoon(reader.ReadLineAsync(), "Automatic switch verifies output: " + mode);
                Check(command == "STATUS", "Automatic switches reuse the normal playback dispatch path");
                string status = BackendStatus(batch[1], "playing", batch[2]) + " actual_frame_id=800 input_frame_id=801";
                await writer.WriteLineAsync(status);
                var expected = new System.Collections.Generic.List<object>();
                if (mode == "WholeGroup") expected.AddRange(batch.Skip(2));
                else if (mode != "Any") expected.Add(batch.Last());
                if (mode == "WholeGroup")
                {
                    var watch = Stopwatch.StartNew();
                    while (Field(owner, "interactiveProtectedHandoff") == null) { Check(watch.ElapsedMilliseconds < 3000, "Automatic whole-group switch reserves a protected handoff"); await Task.Delay(1); }
                    Check(Field(owner, "interactivePending") == batch[2], "Automatic whole-group switch retains the existing next middle");
                    Invoke(owner, "ApplyInteractiveOutputStarted", Started(batch[2], 801));
                    long frame = 802;
                    foreach (string path in new[] { (string)Get(batch[3], "Path"), (string)Get(batch.Last(), "Path"), (string)Get(GetLibraryForDefault(target), "Path") })
                    {
                        var queued = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                        string next = await CompleteSoon(reader.ReadLineAsync(), "Automatic whole-group protected tail");
                        Check(next.StartsWith("PLAY_NEXT " + path), "Automatic whole-group switch finishes every remaining middle and outro");
                        await writer.WriteLineAsync("OK auto whole tail"); await CompleteSoon(queued, "Automatic protected tail queued");
                        Invoke(owner, "ApplyInteractiveOutputStarted", Started(Field(owner, "interactivePending"), frame++));
                    }
                    await WaitForDefaultDispatcherAsync(); continue;
                }
                command = await CompleteSoon(reader.ReadLineAsync(), "Automatic switch first playback command: " + mode);
                bool immediate = mode == "Any" || mode == "ImmediateMiddle";
                string expectedPath = mode == "Any" ? (string)Get(GetLibraryForDefault(target), "Path") : (string)Get(batch.Last(), "Path");
                Check(command.StartsWith((immediate ? "PLAY " : "PLAY_NEXT ") + expectedPath), "Automatic deadline uses outgoing default's independent policy: " + mode);
                await writer.WriteLineAsync("OK auto boundary");
                if (mode == "ImmediateMiddle")
                {
                    command = await CompleteSoon(reader.ReadLineAsync(), "Immediate automatic switch preserves outro");
                    Check(command.StartsWith("PLAY_NEXT " + Get(GetLibraryForDefault(target), "Path")), "The selected outro precedes the next default"); await writer.WriteLineAsync("OK auto destination");
                }
                if (mode == "Any")
                {
                    await WaitForDefaultDispatcherAsync(); Check((Guid)Get(Field(owner, "interactiveCurrent"), "DefaultPlaybackId") == (Guid)Get(target, "Id"), "Any interruption switches directly at its deadline");
                }
                else
                {
                    await WaitForPhasePendingAsync();
                    Invoke(owner, "AdvanceInteractiveDefaultAutoSwitch", start + 30 * Stopwatch.Frequency, true);
                    Check(WaitingTriggerCount() == 0 && (bool)Field(owner, "interactiveDefaultAutoRequested"), "An expired protected default cannot generate a second timed request");
                    if (mode == "AfterMiddle")
                    {
                        var outro = Field(owner, "interactivePending"); Invoke(owner, "ApplyInteractiveOutputStarted", Started(outro, 802));
                        var queued = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                        command = await CompleteSoon(reader.ReadLineAsync(), "Protected automatic switch queues destination");
                        Check(command.StartsWith("PLAY_NEXT " + Get(GetLibraryForDefault(target), "Path")), "The deadline waits for its outro before the destination");
                        await writer.WriteLineAsync("OK auto destination"); await CompleteSoon(queued, "Automatic destination queued");
                    }
                    Invoke(owner, "ApplyInteractiveOutputStarted", Started(Field(owner, "interactivePending"), 803)); await WaitForDefaultDispatcherAsync();
                }
            }
            Invoke(owner, "ClearInteractiveRuntime", true); Set(source, "AutoSwitchEnabled", false);
            Set(group, "ActiveDefaultPlaybackId", Get(source, "Id"));
            var oldDefault = DefaultBatch(group)[0]; Set(oldDefault, "BackendFrameId", 901L);
            var project = GroupBatch(media[0])[0]; Set(project, "Loop", false); Set(project, "BackendFrameId", 900L);
            QueueState(project, oldDefault); SetField("interactiveActive", true); Set(group, "ReturnDefaultPlaybackId", Get(target, "Id"));
            Invoke(owner, "RefreshInteractiveDefaultAfterSettings", Get(source, "Id"));
            string returnCommand = await CompleteSoon(reader.ReadLineAsync(), "Edited return target replaces old idle lookahead");
            Check(returnCommand.StartsWith("PLAY_NEXT " + Get(GetLibraryForDefault(target), "Path")) && returnCommand.Contains("after_frame_id=900"), "Editing a return target retains the currently playing project");
            await writer.WriteLineAsync("OK edited return"); await WaitForPhasePendingAsync();
            Check(Field(owner, "interactiveCurrent") == project && Equals(Get(Field(owner, "interactivePending"), "DefaultPlaybackId"), Get(target, "Id")), "A newly configured return takes effect after the project, even with old media prefetched");
        }
        finally
        {
            Set(group, "ReturnDefaultPlaybackId", Guid.Empty);
            Invoke(owner, "ClearInteractiveRuntime", true); Set(source, "AutoSwitchEnabled", false); Set(source, "AutoSwitchMinimumSeconds", 300); Set(source, "AutoSwitchMaximumSeconds", 600);
            Set(group, "ActiveDefaultPlaybackId", active); SetField("processingInteractiveTriggers", false);
        }
    }
    private static void CheckDefaultCountdownButton(object source, string expected)
    {
        var previousForm = Field(owner, "interactiveForm");
        using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
        {
            try
            {
                SetField("interactiveForm", window); Invoke(window, "ReloadAll");
                var panel = (FlowLayoutPanel)Field(window, "defaultPlaybackButtons");
                var button = panel.Controls.Cast<Button>().First(item => Equals(Get(item.Tag, "Id"), Get(source, "Id")));
                Check(button.Text.Contains(expected), "The actual default button displays its remaining/waiting state");
                Invoke(window, "RefreshDefaultPlaybackCountdown");
                Check(panel.Controls.Contains(button) && button.Text.Contains(expected), "Countdown updates reuse the existing button");
            }
            finally { SetField("interactiveForm", previousForm); }
        }
    }

    private static object GetLibraryForDefault(object item) { return media.Cast<object>().First(entry => Equals(Get(entry, "RuntimeId"), Get(item, "MediaId"))); }
    private static async Task WaitForDefaultDispatcherAsync()
    {
        var watch = Stopwatch.StartNew();
        while ((bool)Field(owner, "processingInteractiveTriggers")) { Check(watch.ElapsedMilliseconds < 3000, "Automatic default dispatcher completes"); await Task.Delay(1); }
    }
}
