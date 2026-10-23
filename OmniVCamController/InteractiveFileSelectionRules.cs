using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private static int LoadInteractiveFileCount(XElement element, string name, int fallback)
        {
            int count;
            return int.TryParse(GetAttribute(element, name, string.Empty), out count) ? Math.Max(1, Math.Min(10000, count)) : fallback;
        }

        private static List<string> SelectInteractiveRandomCountPaths(InteractiveMediaItem item, Random random)
        {
            var selected = new List<string>();
            if (item.FilePaths.Count == 0) return selected;
            int minimum = Math.Min(10000, Math.Max(1, item.FileGroupMinimumFiles));
            int maximum = Math.Min(10000, Math.Max(minimum, item.FileGroupMaximumFiles));
            int count = minimum == maximum ? minimum : random.Next(minimum, maximum + 1);
            // Counts describe playback occurrences. Each draw uses the existing sequential,
            // weighted random, recent-history and no-repeat-round rules, even across pool exhaustion.
            for (int i = 0; i < count; i++) selected.Add(SelectInteractiveMediaPath(item, random));
            return selected;
        }

        private sealed class InteractiveFileChoiceRules
        {
            public Dictionary<string, int> Weights { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int RecentCount { get; set; }
            public int WeightFor(string path)
            {
                int weight;
                return Weights.TryGetValue(path, out weight) ? Math.Max(1, Math.Min(1000000, weight)) : 1;
            }
            public InteractiveFileChoiceRules Clone()
            {
                var copy = new InteractiveFileChoiceRules { RecentCount = RecentCount };
                foreach (var pair in Weights) copy.Weights[pair.Key] = pair.Value;
                return copy;
            }
        }

        private static string SelectInteractiveWeightedFile(IList<string> candidates, InteractiveFileChoiceRules rules,
            List<string> history, int poolCount, Random random)
        {
            if (candidates.Count == 0) return null;
            int recent = Math.Min(Math.Max(0, rules.RecentCount), Math.Max(0, poolCount - 1));
            var excluded = new HashSet<string>(history.Skip(Math.Max(0, history.Count - recent)), StringComparer.OrdinalIgnoreCase);
            var eligible = candidates.Where(path => !excluded.Contains(path)).ToList();
            // A changed pool may contain only recent files. Relax oldest history first to keep selection live.
            for (int skip = 1; eligible.Count == 0 && skip <= history.Count; skip++)
            {
                excluded = new HashSet<string>(history.Skip(Math.Max(0, history.Count - recent) + skip), StringComparer.OrdinalIgnoreCase);
                eligible = candidates.Where(path => !excluded.Contains(path)).ToList();
            }
            if (eligible.Count == 0) eligible = candidates.ToList();
            double total = eligible.Sum(path => (double)rules.WeightFor(path));
            bool equalWeights = eligible.All(path => rules.WeightFor(path) == rules.WeightFor(eligible[0]));
            double ticket = equalWeights ? random.Next(eligible.Count) * (double)rules.WeightFor(eligible[0]) : random.NextDouble() * total;
            string selected = eligible[eligible.Count - 1];
            foreach (string path in eligible)
            {
                ticket -= rules.WeightFor(path);
                if (ticket < 0) { selected = path; break; }
            }
            history.Add(selected);
            if (history.Count > recent) history.RemoveRange(0, history.Count - recent);
            return selected;
        }

        private static XElement SaveInteractiveFileChoices(string name, IEnumerable<string> paths, InteractiveFileChoiceRules rules)
        {
            return new XElement(name, new XAttribute("recent", Math.Max(0, rules.RecentCount)), paths.Select(path =>
                new XElement("File", new XAttribute("path", path), new XAttribute("weight", rules.WeightFor(path)))));
        }

        private static InteractiveFileChoiceRules LoadInteractiveFileChoiceRules(XElement element)
        {
            var rules = new InteractiveFileChoiceRules();
            if (element == null) return rules;
            int recent;
            if (int.TryParse(GetAttribute(element, "recent", "0"), out recent)) rules.RecentCount = Math.Max(0, Math.Min(10000, recent));
            foreach (var file in element.Elements("File"))
            {
                string path = GetAttribute(file, "path", string.Empty).Trim();
                int weight;
                if (path.Length > 0 && int.TryParse(GetAttribute(file, "weight", "1"), out weight))
                    rules.Weights[path] = Math.Max(1, Math.Min(1000000, weight));
            }
            return rules;
        }

        private sealed class FileGroupFileRulesDialog : Form
        {
            private readonly InteractiveFileChoiceRules rules;
            private readonly List<string> paths;
            private readonly ListView filesView = new ListView { Dock = DockStyle.Fill, View = View.Details,
                FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
            private readonly NumericUpDown weightBox = new NumericUpDown { Minimum = 1, Maximum = 1000000, Width = 115 };
            private readonly NumericUpDown recentBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Width = 115 };
            private bool updating;
            public InteractiveFileChoiceRules Result => rules.Clone();

            public FileGroupFileRulesDialog(List<string> files, InteractiveFileChoiceRules settings)
            {
                paths = files.ToList(); rules = settings.Clone();
                Text = T("FileGroupFileRules"); ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
                Size = new Size(780, 530); MinimumSize = new Size(680, 460);
                filesView.Columns.Add(T("FileGroupFileName"), 340);
                filesView.Columns.Add(T("FileGroupFileWeight"), 130);
                filesView.Columns.Add(T("FileGroupProbability"), 170);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int row = 1; row < 5; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(filesView, 0, 0);
                var weightPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                weightPanel.Controls.Add(new Label { Text = T("FileGroupFileWeight"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
                weightPanel.Controls.Add(weightBox); root.Controls.Add(weightPanel, 0, 1);
                var recentPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
                recentPanel.Controls.Add(new Label { Text = T("FileGroupRecentFiles"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
                recentBox.Value = Math.Max(0, Math.Min(10000, rules.RecentCount));
                recentPanel.Controls.Add(recentBox); root.Controls.Add(recentPanel, 0, 2);
                root.Controls.Add(new Label { Text = T("FileGroupFileRulesHint"), AutoSize = true, Dock = DockStyle.Fill,
                    Padding = new Padding(0, 8, 0, 8) }, 0, 3);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var ok = MakeButton(T("InteractiveOk"), (_, __) => DialogResult = DialogResult.OK);
                var cancel = MakeButton(T("Cancel"), (_, __) => DialogResult = DialogResult.Cancel);
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 4);
                Controls.Add(root); AcceptButton = ok; CancelButton = cancel;
                filesView.SelectedIndexChanged += (_, __) => UpdateSelectedWeight();
                weightBox.ValueChanged += (_, __) =>
                {
                    if (updating || filesView.SelectedItems.Count == 0) return;
                    rules.Weights[(string)filesView.SelectedItems[0].Tag] = (int)weightBox.Value;
                    RefreshProbabilities();
                };
                recentBox.ValueChanged += (_, __) => rules.RecentCount = (int)recentBox.Value;
                foreach (string path in paths)
                {
                    var row = new ListViewItem(Path.GetFileName(path)) { Tag = path, ToolTipText = path };
                    row.SubItems.Add(string.Empty); row.SubItems.Add(string.Empty); filesView.Items.Add(row);
                }
                RefreshProbabilities();
                if (filesView.Items.Count > 0) filesView.Items[0].Selected = true;
                UpdateSelectedWeight();
            }
            private void UpdateSelectedWeight()
            {
                updating = true;
                weightBox.Enabled = filesView.SelectedItems.Count > 0;
                if (weightBox.Enabled) weightBox.Value = rules.WeightFor((string)filesView.SelectedItems[0].Tag);
                updating = false;
            }
            private void RefreshProbabilities()
            {
                double total = paths.Sum(path => (double)rules.WeightFor(path));
                foreach (ListViewItem row in filesView.Items)
                {
                    int weight = rules.WeightFor((string)row.Tag);
                    row.SubItems[1].Text = weight.ToString(CultureInfo.CurrentCulture);
                    row.SubItems[2].Text = (weight / total).ToString("P2", CultureInfo.CurrentCulture);
                }
            }
        }
    }
}
