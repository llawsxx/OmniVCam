using System;
using System.Drawing;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private static string InteractiveOverlapOverrideText(bool? enabled)
        {
            return enabled.HasValue ? enabled.Value.ToString() : "inherit";
        }

        private static bool? ReadInteractiveOverlapOverride(XElement element)
        {
            bool enabled;
            return bool.TryParse(GetAttribute(element, "overlapEnabled", null), out enabled) ? (bool?)enabled : null;
        }

        private static decimal ResolveInteractiveOverlapSeconds(InteractiveProjectGroup group, InteractiveRouteItem route,
            InteractiveMediaCombination combination)
        {
            if (combination?.OverlapEnabled != null)
                return combination.OverlapEnabled.Value ? combination.OverlapSeconds : 0m;
            if (route.OverlapEnabled.HasValue) return route.OverlapEnabled.Value ? route.OverlapSeconds : 0m;
            return group.OverlapEnabled ? group.OverlapSeconds : 0m;
        }

        private sealed partial class RouteEditDialog
        {
            private bool? routeOverlapEnabled;
            private decimal routeOverlapSeconds = 0.1m;

            private InteractiveOverlapDialog CreateOverlapDialog(bool combination)
            {
                var item = combination ? combinations[combinationsBox.SelectedIndex] : null;
                return new InteractiveOverlapDialog(combination,
                    item == null ? routeOverlapEnabled : item.OverlapEnabled,
                    item == null ? routeOverlapSeconds : item.OverlapSeconds);
            }

            private void EditOverlapSettings(bool combination)
            {
                if (combination && combinationsBox.SelectedIndex < 0) return;
                var item = combination ? combinations[combinationsBox.SelectedIndex] : null;
                using (var dialog = CreateOverlapDialog(combination))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (item == null) { routeOverlapEnabled = dialog.OverlapEnabled; routeOverlapSeconds = dialog.OverlapSeconds; }
                    else { item.OverlapEnabled = dialog.OverlapEnabled; item.OverlapSeconds = dialog.OverlapSeconds; }
                }
            }
        }

        private sealed class InteractiveOverlapDialog : Form
        {
            private readonly ComboBox modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            private readonly NumericUpDown secondsBox = new NumericUpDown { DecimalPlaces = 3, Minimum = 0, Maximum = 5, Increment = 0.01m, Width = 110 };
            public bool? OverlapEnabled => modeBox.SelectedIndex == 0 ? (bool?)null : modeBox.SelectedIndex == 1;
            public decimal OverlapSeconds => secondsBox.Value;

            public InteractiveOverlapDialog(bool combination, bool? enabled, decimal seconds)
            {
                Text = T(combination ? "InteractiveCombinationOverlapSettings" : "InteractiveProjectOverlapSettings");
                ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                Size = new Size(680, 300); MinimumSize = new Size(640, 280);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(12) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);
                modeBox.Items.AddRange(new object[] { T("InteractiveOverlapInherit"), T("InteractiveOverlapOn"), T("InteractiveOverlapOff") });
                modeBox.SelectedIndex = !enabled.HasValue ? 0 : enabled.Value ? 1 : 2;
                secondsBox.Value = seconds; secondsBox.Enabled = modeBox.SelectedIndex == 1;
                modeBox.SelectedIndexChanged += (_, __) => secondsBox.Enabled = modeBox.SelectedIndex == 1;
                AddDialogRow(root, T("InteractiveOverlapMode"), modeBox, 0);
                AddDialogRow(root, T("InteractiveGroupOverlapSeconds"), secondsBox, 1);
                var hint = new Label { Text = T("InteractiveOverlapHierarchyHint"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
                root.Controls.Add(hint, 0, 2); root.SetColumnSpan(hint, 2);
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
                root.Controls.Add(buttons, 0, 3); root.SetColumnSpan(buttons, 2);
                AcceptButton = ok; CancelButton = cancel;
            }
        }
    }
}
