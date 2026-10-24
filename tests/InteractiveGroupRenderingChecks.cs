using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal static partial class InteractivePlaybackChecks
{
    private static void CheckProjectGroupRendering()
    {
        string[] fields = { "interactiveProjectGroups", "interactiveGroupTransitions", "interactiveRoutes", "activeInteractiveProjectGroupId",
            "interactiveProjectGroupVersion", "interactiveForm", "interactiveHotkeysDisabled", "interactiveWindowOpen", "processingInteractiveTriggers" };
        var saved = fields.ToDictionary(name => name, name => Field(owner, name));
        try
        {
            Invoke(owner, "ClearInteractiveRuntime", true);
            var groups = (IList)Activator.CreateInstance(saved["interactiveProjectGroups"].GetType());
            for (int i = 0; i < 3; i++)
            {
                var group = New("InteractiveProjectGroup"); Set(group, "Name", "Render group " + i);
                for (int j = 0; j < (i == 2 ? 0 : 3 - i); j++)
                {
                    var route = Route("sequential"); Set(route, "Name", "Render route " + i + " / " + j);
                    ((IList)Get(group, "Routes")).Add(route);
                }
                groups.Add(group);
            }
            SetField("interactiveProjectGroups", groups);
            SetField("interactiveGroupTransitions", Activator.CreateInstance(saved["interactiveGroupTransitions"].GetType()));
            SetField("interactiveRoutes", Get(groups[0], "Routes")); SetField("activeInteractiveProjectGroupId", Get(groups[0], "Id"));
            SetField("interactiveHotkeysDisabled", true); SetField("interactiveWindowOpen", true); SetField("processingInteractiveTriggers", true);
            SetField("interactiveActive", true);
            using (var window = (Form)Activator.CreateInstance(Nested("InteractivePlaybackForm"), new object[] { owner }))
            {
                SetField("interactiveForm", window); Invoke(window, "ReloadAll");
                var routes = (FlowLayoutPanel)Field(window, "routeButtons");
                var groupButtons = (FlowLayoutPanel)Field(window, "projectGroupButtons");
                var library = (ListView)Field(window, "mediaView");
                var rows = library.Items.Cast<ListViewItem>().ToArray();
                var retained = routes.Controls.Cast<Control>().Take(2).ToArray();
                var handles = retained.Select(button => button.Handle).ToArray();
                var originalGroups = groupButtons.Controls.Cast<Control>().ToArray();
                var groupHandles = originalGroups.Select(button => button.Handle).ToArray();
                Control surplus = routes.Controls[2]; IntPtr surplusHandle = surplus.Handle;
                for (int pass = 0; pass < 12; pass++)
                {
                    int target = pass % 2 == 0 ? 1 : 0;
                    typeof(Button).GetMethod("OnClick", PrivateInstance).Invoke(groupButtons.Controls[target], new object[] { EventArgs.Empty });
                    Check(Equals(Field(owner, "activeInteractiveProjectGroupId"), Get(groups[target], "Id")), "Reused group buttons select their current group");
                    Check(routes.Controls.Count == ((IList)Get(groups[target], "Routes")).Count, "Switching updates the number of trigger buttons");
                    for (int i = 0; i < retained.Length; i++)
                        Check(routes.Controls[i] == retained[i] && routes.Controls[i].Handle == handles[i] &&
                            routes.Controls[i].Tag == ((IList)Get(groups[target], "Routes"))[i], "Common trigger buttons retain native handles and bind the new group's projects");
                    for (int i = 0; i < originalGroups.Length; i++)
                        Check(groupButtons.Controls[i] == originalGroups[i] && groupButtons.Controls[i].Handle == groupHandles[i] &&
                            groupButtons.Controls[i].Font.Bold == (i == target) &&
                            groupButtons.Controls[i].BackColor == (i == target ? Color.LightSkyBlue : SystemColors.Control), "Group controls retain native handles and reset inactive styling");
                    Check(library.Items.Cast<ListViewItem>().SequenceEqual(rows), "Switching groups preserves media-library rows instead of reloading them");
                    typeof(Button).GetMethod("OnClick", PrivateInstance).Invoke(routes.Controls[0], new object[] { EventArgs.Empty });
                    var requests = ((IEnumerable)Field(owner, "interactiveTriggerRequests")).Cast<object>().ToArray();
                    Check((string)Get(requests.Last(), "RouteName") == (string)Get(((IList)Get(groups[target], "Routes"))[0], "Name") &&
                        Equals(Get(requests.Last(), "ProjectGroupId"), Get(groups[target], "Id")), "Clicking a reused trigger button queues the displayed group's project");
                }
                Check(surplus.IsDisposed, "Buttons removed by a smaller group are disposed");
                typeof(Button).GetMethod("OnClick", PrivateInstance).Invoke(groupButtons.Controls[2], new object[] { EventArgs.Empty });
                Check(routes.Controls.Count == 0 && retained.All(button => button.IsDisposed), "An empty group removes stale trigger buttons");
                typeof(Button).GetMethod("OnClick", PrivateInstance).Invoke(groupButtons.Controls[0], new object[] { EventArgs.Empty });
                Check(routes.Controls.Count == 3 && !routes.Controls[0].IsDisposed, "Switching back from an empty group creates usable trigger buttons");
                groups.RemoveAt(2); Invoke(window, "ReloadProjectGroup");
                Check(groupButtons.Controls.Count == 2 && originalGroups[2].IsDisposed && groupButtons.Controls[0] == originalGroups[0], "Removing a group disposes only the excess button");
            }
        }
        finally
        {
            SetField("interactiveForm", null); Invoke(owner, "ClearInteractiveRuntime", true);
            foreach (var item in saved) SetField(item.Key, item.Value);
        }
        Console.WriteLine("PASS: repeated real group-button switches preserve native controls and library rows, update trigger bindings and active styling, and handle smaller/empty/deleted groups.");
    }
}
