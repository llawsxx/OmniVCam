using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

internal static partial class InteractivePlaybackChecks
{
    private static void CheckTransitionTriggerModes(object a, object b)
    {
        var transition = ((IList)Field(owner, "interactiveGroupTransitions")).Cast<object>().First(item =>
            Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
        object previousMode = Get(transition, "Immediate");
        var culture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Set(transition, "Immediate", false);
            Check(!(bool)Get(Invoke(owner, "BuildInteractiveGroupTransition", a, b), "Immediate"), "A PLAY_NEXT transition produces a queued playback request");
            foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                var pairs = (IList)Invoke(owner, "BuildInteractiveTransitionPairs");
                var detached = pairs.Cast<object>().First(item => Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
                Check(!(bool)Get(detached, "Immediate"), "Editing copies each direction's configured command");
                using (var dialog = (Form)Activator.CreateInstance(Nested("GroupTransitionsDialog"), new object[] { Field(owner, "interactiveProjectGroups"), pairs, media }))
                {
                    var view = (ListView)Field(dialog, "pairs"); IntPtr unused = view.Handle;
                    view.Items.Cast<ListViewItem>().Single(row => row.Tag == detached).Selected = true;
                    Invoke(dialog, "RefreshSelection");
                    var command = (ComboBox)Field(dialog, "commandBox");
                    Check(command.Enabled && command.DropDownStyle == ComboBoxStyle.DropDownList && command.SelectedIndex == 1 && view.SelectedItems[0].SubItems[3].Text == "PLAY_NEXT",
                        "The transition editor loads PLAY_NEXT and shows it in the selected direction's row");
                    command.SelectedIndex = 0;
                    Check((bool)Get(detached, "Immediate") && !(bool)Get(transition, "Immediate") && view.SelectedItems[0].SubItems[3].Text == "PLAY",
                        "Command edits update only the detached dialog selection until confirmed");
                    var chooser = (ComboBox)Field(dialog, "mediaBox"); int selected = chooser.SelectedIndex;
                    chooser.SelectedIndex = 0; Check(!command.Enabled, "A direction without transition media disables the command selector");
                    chooser.SelectedIndex = selected; Check(command.Enabled && command.SelectedIndex == 0, "Choosing transition media enables its command without changing the selected mode");
                    IntPtr dialogHandle = dialog.Handle;
                    foreach (var size in new[] { dialog.Size, dialog.MinimumSize })
                    {
                        dialog.Size = size; dialog.PerformLayout();
                        var commandBounds = command.Bounds;
                        for (var parent = command.Parent; parent != dialog; parent = parent.Parent) commandBounds.Offset(parent.Location);
                        var mediaBounds = chooser.Bounds;
                        for (var parent = chooser.Parent; parent != dialog; parent = parent.Parent) mediaBounds.Offset(parent.Location);
                        Check(command.Width >= 120 && chooser.Width >= 120 && dialog.ClientRectangle.Contains(commandBounds) &&
                            dialog.ClientRectangle.Contains(mediaBounds) && commandBounds.Left >= mediaBounds.Right,
                            "Both transition selectors fit the actual dialog viewport at " + size + " in " + language + "; client=" + dialog.ClientRectangle + "; media=" + mediaBounds + "; command=" + commandBounds + "; selector=" + command.Parent.Bounds + "; panel=" + command.Parent.Parent.Bounds);
                    }
                }
            }
            var state = New("ConnectionTabState"); Set(state, "InteractiveMedia", media);
            Set(state, "InteractiveProjectGroups", Field(owner, "interactiveProjectGroups"));
            Set(state, "InteractiveGroupTransitions", Field(owner, "interactiveGroupTransitions"));
            var xml = ((XElement)Invoke(owner, "SaveConnectionTabElement", state)).Element("InteractivePlayback");
            var saved = xml.Element("Transitions").Elements("Transition").Single(item =>
                item.Attribute("from").Value == Get(a, "Id").ToString() && item.Attribute("to").Value == Get(b, "Id").ToString());
            Check(saved.Attribute("immediate").Value == "0", "Connection XML persists the transition's PLAY_NEXT mode");
            var isolated = FormatterServices.GetUninitializedObject(main);
            SetFieldOn(isolated, "interactiveMedia", Activator.CreateInstance(media.GetType()));
            SetFieldOn(isolated, "interactiveRoutes", Activator.CreateInstance(Field(owner, "interactiveRoutes").GetType()));
            Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            var loaded = ((IList)Field(isolated, "interactiveGroupTransitions")).Cast<object>().Single(item =>
                Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
            Check(!(bool)Get(loaded, "Immediate"), "The transition's PLAY_NEXT mode survives configuration loading");
            saved.Attribute("immediate").Remove(); Invoke(isolated, "LoadInteractivePlaybackElement", xml);
            loaded = ((IList)Field(isolated, "interactiveGroupTransitions")).Cast<object>().Single(item =>
                Equals(Get(item, "FromId"), Get(a, "Id")) && Equals(Get(item, "ToId"), Get(b, "Id")));
            Check((bool)Get(loaded, "Immediate"), "Older transition configurations retain PLAY behavior");
            CheckTransitionCommands(a, b, transition);
        }
        finally { Set(transition, "Immediate", previousMode); Thread.CurrentThread.CurrentUICulture = culture; }
        Console.WriteLine("PASS: per-direction transition PLAY/PLAY_NEXT, detached translated editing, XML round trips and legacy defaults, actual TCP commands and current-media protection for single files and file groups.");
    }
    private static void SetFieldOn(object target, string name, object value)
    {
        main.GetField(name, PrivateInstance).SetValue(target, value);
    }
    private static async Task CheckTransitionCommandsAsync(TcpListener listener, int port, object a, object b, object transition)
    {
        Action<string, int> ignore = (text, generation) => { };
        var client = Activator.CreateInstance(Nested("TcpCommandClient"), new object[] { ignore, ignore, (Action)(() => { }) });
        TcpClient peer = null; object originalMedia = Get(transition, "MediaId"), originalMode = Get(transition, "Immediate");
        SetField("commandClient", client); SetField("observedBackendConnectionLoss", 0);
        try
        {
            var accept = listener.AcceptTcpClientAsync(); var connecting = ClientSend(client, port, "STATUS");
            peer = await CompleteSoon(accept, "Transition command fixture connects");
            var reader = new StreamReader(peer.GetStream()); var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true };
            Check(await CompleteSoon(reader.ReadLineAsync(), "Transition command fixture reads STATUS") == "STATUS", "Transition mode tests use a local backend");
            await writer.WriteLineAsync("OK initial"); await CompleteSoon(connecting, "Transition command fixture establishes its generation");
            foreach (bool immediate in new[] { false, true })
            foreach (bool protectedCurrent in new[] { false, true })
            foreach (bool fileGroup in new[] { false, true })
            {
                Invoke(owner, "ClearInteractiveRuntime", true);
                var library = fileGroup ? FileGroup() : New("InteractiveMediaItem");
                Set(library, "Title", "Test transition"); Set(library, "Path", "transition.mp4"); Set(library, "Loop", true);
                Set(library, "PreventInterrupt", true); if (fileGroup) Set(library, "FileGroupPlayAll", true);
                media.Add(library);
                try
                {
                    Set(transition, "MediaId", Get(library, "RuntimeId")); Set(transition, "Immediate", immediate);
                    var request = Invoke(owner, "BuildInteractiveGroupTransition", a, b);
                    var entries = ((IList)Get(request, "Entries")).Cast<object>().ToArray();
                    var current = GroupBatch(media[0])[0]; Set(current, "PreventInterrupt", protectedCurrent); Set(current, "BackendFrameId", 100L);
                    QueueState(current, null); SetField("interactiveActive", true); SetField("interactiveObservedBackendFrameId", 100L);
                    var playing = (Task)Invoke(owner, (bool)Get(request, "Immediate") ? "StartInteractiveImmediatelyAsync" : "AppendInteractiveRouteAsync", request);
                    bool startsNow = immediate && !protectedCurrent;
                    string command = await CompleteSoon(reader.ReadLineAsync(), "The configured transition sends its playback command");
                    Check(command.StartsWith((startsNow ? "PLAY " : "PLAY_NEXT ") + Get(entries[0], "Path")), "Transition commands respect the selected mode and current protection");
                    await writer.WriteLineAsync("OK transition");
                    if (startsNow && entries.Length > 1)
                    {
                        Check((await CompleteSoon(reader.ReadLineAsync(), "An immediate transition group prefetches its second file")).StartsWith("PLAY_NEXT " + Get(entries[1], "Path")), "An immediate group retains its complete transition sequence");
                        await writer.WriteLineAsync("OK second transition file");
                    }
                    if (protectedCurrent)
                    {
                        await WaitForPhasePendingAsync();
                        Check(!playing.IsCompleted && Field(owner, "interactiveCurrent") == current, "Both transition modes wait for a protected current file's output boundary");
                    }
                    else await CompleteSoon(playing, "An unprotected transition command is acknowledged");
                    Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(entries[0], 101)), "The first transition output confirms its invocation");
                    await CompleteSoon(playing, "The protected transition request releases only on its actual output");
                    for (int i = 1; i < entries.Length; i++)
                    {
                        if (Field(owner, "interactivePending") == null)
                        {
                            var queuing = (Task)Invoke(owner, "EnsureInteractiveNextQueuedAsync");
                            Check((await CompleteSoon(reader.ReadLineAsync(), "The transition group queues its next file")).StartsWith("PLAY_NEXT " + Get(entries[i], "Path")), "A transition group plays every selected file in order");
                            await writer.WriteLineAsync("OK transition successor"); await CompleteSoon(queuing, "The transition successor is acknowledged");
                        }
                        Check((bool)Invoke(owner, "ApplyInteractiveOutputStarted", Started(entries[i], 101 + i)), "Each transition group file confirms its own actual output");
                    }
                    Check(entries.All(entry => !(bool)Get(entry, "Loop") && Get(entry, "GroupLoop") == null && (bool)Get(entry, "PreventInterrupt")) && QueuedEntries().Length == 0,
                        "Both transition modes ignore library looping and retain library interruption protection");
                }
                finally { Invoke(owner, "ClearInteractiveRuntime", true); media.Remove(library); }
            }
        }
        finally
        {
            Invoke(owner, "ClearInteractiveRuntime", true); Set(transition, "MediaId", originalMedia); Set(transition, "Immediate", originalMode);
            ((IDisposable)client).Dispose(); if (peer != null) peer.Close(); SetField("commandClient", null); SetField("observedBackendConnectionLoss", 0);
        }
    }
    private static void CheckTransitionCommands(object a, object b, object transition)
    {
        var context = SynchronizationContext.Current; var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using (var host = new TextBox { Text = "127.0.0.1" })
        using (var portBox = new NumericUpDown { Maximum = 65535, Value = port })
        {
            SetField("hostBox", host); SetField("portBox", portBox); SynchronizationContext.SetSynchronizationContext(null);
            try { CheckTransitionCommandsAsync(listener, port, a, b, transition).GetAwaiter().GetResult(); }
            finally { listener.Stop(); SetField("hostBox", null); SetField("portBox", null); SynchronizationContext.SetSynchronizationContext(context); }
        }
    }
}
