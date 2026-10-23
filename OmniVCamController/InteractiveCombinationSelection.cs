using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace OmniVCamController
{
    public sealed partial class MainForm
    {
        private const int MaxCombinationWeight = 1000;
        private const int MaxCombinationCooldownSeconds = 86400;
        private const int MaxRecentCombinations = 1000;

        private static int GetInteractiveSelectionInt(XElement element, string name, int fallback, int minimum, int maximum)
        {
            int value;
            return int.TryParse(GetAttribute(element, name, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? Math.Max(minimum, Math.Min(maximum, value)) : fallback;
        }

        private static InteractiveMediaCombination CopyInteractiveCombination(InteractiveMediaCombination source)
        {
            return new InteractiveMediaCombination
            {
                MediaIds = source.MediaIds.ToList(), Weight = source.Weight, CooldownSeconds = source.CooldownSeconds,
                ObsStartScene = source.ObsStartScene, ObsEndScene = source.ObsEndScene
            };
        }

        private static void ResetInteractiveCombinationSelection(InteractiveRouteItem route)
        {
            route.NextCombinationIndex = 0;
            route.RecentCombinations.Clear();
            route.RandomSelectionUnavailable = false;
            foreach (var combination in route.Combinations) combination.LastSelectedTimestamp = null;
        }

        private List<InteractivePlaybackEntry> BuildInteractiveEntriesAt(InteractiveRouteItem route, long timestamp)
        {
            // Reserve a combination once per accepted trigger, before asynchronous playback dispatch.
            // Availability checks must not advance file-group selection progress.
            route.RandomSelectionUnavailable = false;
            var playable = route.Combinations.Where(combination => combination.MediaIds.Count > 0 &&
                combination.MediaIds.All(id => CanPlayInteractiveMedia(interactiveMedia.FirstOrDefault(item => item.RuntimeId == id)))).ToList();
            if (playable.Count == 0) return new List<InteractivePlaybackEntry>();
            InteractiveMediaCombination selected;
            int sequentialIndex = route.NextCombinationIndex % playable.Count;
            if (route.RandomOrder)
            {
                int recentCount = Math.Min(route.RecentCombinationCount, playable.Count - 1);
                var recent = new HashSet<InteractiveMediaCombination>(route.RecentCombinations.Where(playable.Contains).Reverse().Take(recentCount));
                var candidates = playable.Where(combination => !recent.Contains(combination) &&
                    (!combination.LastSelectedTimestamp.HasValue || combination.CooldownSeconds == 0 ||
                        timestamp - combination.LastSelectedTimestamp.Value >= (long)combination.CooldownSeconds * Stopwatch.Frequency)).ToList();
                if (candidates.Count == 0)
                {
                    route.RandomSelectionUnavailable = true;
                    return new List<InteractivePlaybackEntry>();
                }
                double ticket = random.NextDouble() * candidates.Sum(combination => (double)combination.Weight);
                selected = candidates[candidates.Count - 1];
                foreach (var candidate in candidates)
                {
                    ticket -= candidate.Weight;
                    if (ticket < 0) { selected = candidate; break; }
                }
            }
            else selected = playable[sequentialIndex];
            var entries = BuildInteractiveCombinationEntries(selected);
            if (entries.Count == 0) return entries;
            foreach (var entry in entries)
            {
                entry.RouteId = route.RuntimeId;
                if (entry.PhaseRun != null) entry.PhaseRun.RouteId = route.RuntimeId;
            }
            AttachInteractiveObsPlayback(route, selected, entries);
            if (route.RandomOrder)
            {
                selected.LastSelectedTimestamp = timestamp;
                if (route.RecentCombinationCount > 0)
                {
                    route.RecentCombinations.Add(selected);
                    int retained = Math.Min(route.RecentCombinationCount, MaxRecentCombinations);
                    if (route.RecentCombinations.Count > retained)
                        route.RecentCombinations.RemoveRange(0, route.RecentCombinations.Count - retained);
                }
                else route.RecentCombinations.Clear();
            }
            else route.NextCombinationIndex = (sequentialIndex + 1) % playable.Count;
            return entries;
        }

        private sealed partial class RouteEditDialog
        {
            private readonly NumericUpDown recentCountBox = new NumericUpDown { Minimum = 0, Maximum = MaxRecentCombinations, Width = 75 };
            private readonly NumericUpDown combinationWeightBox = new NumericUpDown { Minimum = 1, Maximum = MaxCombinationWeight, Value = 1, Dock = DockStyle.Fill };
            private readonly NumericUpDown combinationCooldownBox = new NumericUpDown { Minimum = 0, Maximum = MaxCombinationCooldownSeconds, Dock = DockStyle.Fill };
            private bool refreshingRandomSettings;

            private Control BuildRandomSelectionBar()
            {
                var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
                selectionBox.Width = 160;
                panel.Controls.Add(selectionBox);
                panel.Controls.Add(new Label { Text = T("InteractiveRecentCombinationCount"), AutoSize = true, Anchor = AnchorStyles.Left });
                panel.Controls.Add(recentCountBox);
                selectionBox.SelectedIndexChanged += (_, __) => RefreshCombinationRandomSettings();
                return panel;
            }

            private Control BuildCombinationRandomSettings()
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 3 };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.Controls.Add(new Label { Text = T("InteractiveCombinationWeight"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                panel.Controls.Add(combinationWeightBox, 1, 0);
                panel.Controls.Add(new Label { Text = T("InteractiveCombinationCooldown"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                panel.Controls.Add(combinationCooldownBox, 1, 1);
                var obsButton = MakeButton(T("InteractiveCombinationObsScenes"), (_, __) => EditObsScenes(true));
                panel.Controls.Add(obsButton, 0, 2); panel.SetColumnSpan(obsButton, 2);
                combinationWeightBox.ValueChanged += (_, __) => ApplyCombinationRandomSettings();
                combinationCooldownBox.ValueChanged += (_, __) => ApplyCombinationRandomSettings();
                return panel;
            }

            private void RefreshCombinationRandomSettings()
            {
                refreshingRandomSettings = true;
                try
                {
                    bool selected = combinationsBox.SelectedIndex >= 0;
                    recentCountBox.Enabled = selectionBox.SelectedIndex == 0;
                    combinationWeightBox.Enabled = combinationCooldownBox.Enabled = selected && recentCountBox.Enabled;
                    var combination = selected ? combinations[combinationsBox.SelectedIndex] : null;
                    combinationWeightBox.Value = combination?.Weight ?? 1;
                    combinationCooldownBox.Value = combination?.CooldownSeconds ?? 0;
                }
                finally { refreshingRandomSettings = false; }
            }

            private void ApplyCombinationRandomSettings()
            {
                if (refreshingRandomSettings || combinationsBox.SelectedIndex < 0) return;
                var combination = combinations[combinationsBox.SelectedIndex];
                combination.Weight = (int)combinationWeightBox.Value;
                combination.CooldownSeconds = (int)combinationCooldownBox.Value;
            }
        }
    }
}
