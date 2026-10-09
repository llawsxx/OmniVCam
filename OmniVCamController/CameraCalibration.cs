using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZXing;

namespace OmniVCamController
{
    public partial class MainForm
    {
        private void OpenCameraCalibration()
        {
            // A modal, separate window keeps commands bound to the selected connection.
            using (var dialog = new CameraCalibrationForm(hostBox.Text.Trim(), (int)portBox.Value,
                (long)shiftBox.Value, SendRawCommandAsync, value =>
                {
                    bool ready = controlsReady;
                    controlsReady = false;
                    shiftBox.Minimum = Math.Min(shiftBox.Minimum, value);
                    shiftBox.Maximum = Math.Max(shiftBox.Maximum, value);
                    shiftBox.Value = value;
                    controlsReady = ready;
                    SaveActiveConnectionState();
                    SaveAutoConfig();
                })) dialog.ShowDialog(this);
        }
    }

    internal sealed class CalibrationFrame
    {
        public int Port, Width, Height, Numerator, Denominator;
        public long Process, Session, Frame;
        public long? ShiftUs;
        public double Fps => (double)Numerator / Denominator;
        public string Identity => Port + "/" + Process + "/" + Session;
        public static CalibrationFrame Parse(string text)
        {
            if (text != null && text.StartsWith("OVCAL2:", StringComparison.Ordinal)) return ParseCompact(text);
            string[] parts = (text ?? "").Split(new[] { '|', ':' });
            if (parts.Length != 9 || parts[0] != "OVCAL1") return null;
            var n = new long[8];
            for (int i = 0; i < n.Length; i++)
                if (!long.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
            if (n[0] < 1 || n[0] > 65535 || n[1] < 1 || n[2] < 1 ||
                n[3] < 1 || n[3] > 32768 || n[4] < 1 || n[4] > 32768 ||
                n[5] < 1 || n[5] > int.MaxValue || n[6] < 1 || n[6] > int.MaxValue ||
                (double)n[5] / n[6] > 1000) return null;
            return new CalibrationFrame { Port = (int)n[0], Process = n[1], Session = n[2],
                Width = (int)n[3], Height = (int)n[4], Numerator = (int)n[5], Denominator = (int)n[6], Frame = n[7] };
        }
        private static CalibrationFrame ParseCompact(string text)
        {
            if (text.Length != 110) return null;
            for (int i = 7; i < text.Length; i++) if (text[i] < '0' || text[i] > '9') return null;
            int[] widths = { 5, 10, 19, 5, 5, 10, 10, 19 };
            var legacy = new System.Text.StringBuilder("OVCAL1");
            int offset = 7;
            foreach (int width in widths) { legacy.Append('|').Append(text.Substring(offset, width)); offset += width; }
            var result = Parse(legacy.ToString());
            if (result == null || (text[offset] != '0' && text[offset] != '1')) return null;
            string shift = (text[offset] == '1' ? "-" : "") + text.Substring(offset + 1, 19);
            long value;
            if (!long.TryParse(shift, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)) return null;
            result.ShiftUs = value;
            return result;
        }
        public bool SameStream(CalibrationFrame other) => other != null && Identity == other.Identity &&
            Width == other.Width && Height == other.Height && Numerator == other.Numerator && Denominator == other.Denominator;
    }

    internal sealed class CalibrationShiftGate
    {
        private readonly CalibrationFrame stream;
        private readonly long shift;
        private long previous = -1;
        public CalibrationShiftGate(CalibrationFrame stream, long shift) { this.stream = stream; this.shift = shift; }
        public bool Observe(CalibrationFrame frame)
        {
            if (!stream.SameStream(frame)) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationStreamChanged", CultureInfo.CurrentUICulture));
            if (frame.ShiftUs != shift) { previous = -1; return false; }
            bool confirmed = previous >= 0 && frame.Frame > previous;
            previous = frame.Frame;
            return confirmed;
        }
    }

    internal sealed class CalibrationMeasurement
    {
        public CalibrationFrame Stream;
        public int Captures, Decoded, Unique, Repeated, DecodeFailures, QueueDrops, CaptureMisses;
        public long Missing, Span;
        public long FirstFrame = -1, LastFrame = -1;
        public double Seconds, CaptureSeconds, RefreshHz, MaxCaptureMs;
        public Rectangle CaptureBounds;
        public string Monitor;
        private double firstSeen;
        public double DisplayFps => Seconds > 0 ? (Unique - 1) / Seconds : 0;
        public double CaptureFps => CaptureSeconds > 0 ? (Captures - 1) / CaptureSeconds : 0;
        public double MissingRate => Span > 0 ? (double)Missing / Span : 0;
        public bool HasSamplingErrors => CaptureMisses > 0 || QueueDrops > 0 || DecodeFailures > 0;
        public bool Reliable => Stream != null && Unique >= 20 && Span > 0 &&
            RefreshHz >= Stream.Fps * .995 &&
            !HasSamplingErrors && MaxCaptureMs < 1500 / RefreshHz;
        public void Observe(CalibrationFrame frame, double time)
        {
            if (!Stream.SameStream(frame)) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationStreamChanged", CultureInfo.CurrentUICulture));
            if (frame.ShiftUs != Stream.ShiftUs) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationShiftChanged", CultureInfo.CurrentUICulture));
            Decoded++;
            if (LastFrame < 0) { firstSeen = time; FirstFrame = frame.Frame; Unique++; }
            else if (frame.Frame < LastFrame) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationFrameRestarted", CultureInfo.CurrentUICulture));
            else if (frame.Frame == LastFrame) Repeated++;
            else
            {
                long delta = frame.Frame - LastFrame;
                Span += delta;
                Missing += delta - 1;
                Unique++;
            }
            LastFrame = frame.Frame;
            Seconds = time - firstSeen;
        }
    }

    internal sealed class CalibrationRetryPolicy
    {
        private readonly int maxRetries;
        public int UsedRetries { get; private set; }
        public CalibrationRetryPolicy(int maxRetries)
        {
            if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
            this.maxRetries = maxRetries;
        }
        public bool TryRetry(CalibrationMeasurement measurement)
        {
            if (!measurement.HasSamplingErrors || UsedRetries >= maxRetries) return false;
            UsedRetries++;
            return true;
        }
    }

    internal sealed class CameraCalibrationForm : Form
    {
        private readonly Func<string, Task<string>> send;
        private readonly Action<long> commit;
        private readonly int port;
        private long savedShift;
        private readonly NumericUpDown duration = new NumericUpDown { Minimum = 2, Maximum = 30, Value = 2, Width = 55 };
        private readonly NumericUpDown steps = new NumericUpDown { Minimum = 4, Maximum = 64, Value = 16, Width = 55 };
        private readonly NumericUpDown retries = new NumericUpDown { Minimum = 0, Maximum = 20, Value = 3, Width = 55 };
        private readonly TextBox output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Button stop = new Button { AutoSize = true, Enabled = false };
        private readonly List<Control> runControls = new List<Control>();
        private CancellationTokenSource cancellation;
        private bool closePending;
        private static string T(string key) => Resources.AppStrings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

        public CameraCalibrationForm(string host, int port, long shift, Func<string, Task<string>> send, Action<long> commit)
        {
            this.port = port;
            savedShift = shift;
            this.send = send;
            this.commit = commit;
            Text = T("CalibrationTitle") + " — " + host + ":" + port;
            Size = new Size(920, 660);
            MinimumSize = new Size(740, 500);
            StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            var hint = new Label { AutoSize = true, MaximumSize = new Size(860, 0), Text = T("CalibrationHint") };
            root.Controls.Add(hint, 0, 0);
            var targets = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            AddButton(targets, T("CalibrationPlayCard"), async (_, __) =>
            {
                try { await RequireOk("PLAY <TESTCARDCAL>"); Log(T("CalibrationCardRequested")); }
                catch (Exception ex) { Log(ex.Message); }
            });
            root.Controls.Add(targets, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            actions.Controls.Add(new Label { AutoSize = true, Text = T("CalibrationSeconds"), Padding = new Padding(0, 6, 0, 0) });
            actions.Controls.Add(duration);
            actions.Controls.Add(new Label { AutoSize = true, Text = T("CalibrationSweepPoints"), Padding = new Padding(0, 6, 0, 0) });
            actions.Controls.Add(steps);
            actions.Controls.Add(new Label { AutoSize = true, Text = T("CalibrationRetries"), Padding = new Padding(0, 6, 0, 0) });
            actions.Controls.Add(retries);
            AddButton(actions, T("CalibrationDiagnose"), async (_, __) => await Run(false));
            AddButton(actions, T("CalibrationAutoAdjust"), async (_, __) => await Run(true));
            stop.Text = T("CalibrationCancel");
            stop.Click += (_, __) => cancellation?.Cancel();
            actions.Controls.Add(stop);
            root.Controls.Add(actions, 0, 2);
            output.Font = new Font(FontFamily.GenericMonospace, 10);
            root.Controls.Add(output, 0, 3);
            runControls.AddRange(new Control[] { duration, steps, retries });
            FormClosing += (_, e) =>
            {
                if (cancellation != null) { e.Cancel = true; closePending = true; cancellation.Cancel(); }
            };
        }

        private void AddButton(Control parent, string text, EventHandler handler)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += handler;
            parent.Controls.Add(button);
            runControls.Add(button);
        }
        private void Log(string text) { if (!output.IsDisposed) output.AppendText(text + Environment.NewLine); }
        private async Task RequireOk(string command)
        {
            string reply = await send(command);
            if (reply == null || !reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(command + ": " + (reply ?? Resources.AppStrings.ResourceManager.GetString("CalibrationNoReply", CultureInfo.CurrentUICulture)));
        }
        private string Describe(CalibrationMeasurement m)
        {
            return string.Format(CultureInfo.CurrentCulture, T("CalibrationStatistics"),
                m.DisplayFps, m.CaptureFps, m.RefreshHz, m.Unique, m.Repeated, m.Missing, m.MissingRate,
                m.DecodeFailures, m.QueueDrops, m.CaptureMisses, m.MaxCaptureMs,
                m.Reliable ? T("CalibrationHealthy") : T("CalibrationInsufficient"));
        }
        private async Task<CalibrationMeasurement> MeasureWithRetries(int seconds, long process,
            CalibrationFrame expected, long? expectedShift, int maxRetries, CancellationToken token)
        {
            var policy = new CalibrationRetryPolicy(maxRetries);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var measurement = await Task.Run(() => CalibrationCapture.Measure(seconds, port, process, expected, expectedShift, token));
                if (!measurement.HasSamplingErrors) return measurement;
                // Discard this entire attempt; every retry starts fresh and confirms the same visible Shift.
                expected = expected ?? measurement.Stream;
                expectedShift = expectedShift ?? measurement.Stream.ShiftUs;
                string shift = expectedShift.HasValue ? expectedShift.Value.ToString(CultureInfo.CurrentCulture) : T("CalibrationShiftUnknown");
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationDiscardedSample"), shift) + Describe(measurement));
                if (!policy.TryRetry(measurement))
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, T("CalibrationRetriesExhausted"), shift, maxRetries));
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationRetrying"), shift, policy.UsedRetries, maxRetries));
                await Task.Delay(250, token);
            }
        }
        private async Task Run(bool adjust)
        {
            cancellation = new CancellationTokenSource();
            foreach (var control in runControls) control.Enabled = false;
            stop.Enabled = true;
            bool shifted = false, applied = false;
            var token = cancellation.Token;
            try
            {
                string identity = await send("CALIBRATION_ID");
                long process;
                if (identity == null || !identity.StartsWith("OK pid=") || !long.TryParse(identity.Substring(7).Trim(), out process))
                    throw new InvalidOperationException(T("CalibrationUpdateDll"));
                int seconds = (int)duration.Value, count = (int)steps.Value, maxRetries = (int)retries.Value;
                Log(T("CalibrationBaseline"));
                var baseline = adjust
                    ? await MeasureWithRetries(seconds, process, null, null, maxRetries, token)
                    : await Task.Run(() => CalibrationCapture.Measure(seconds, port, process, null, null, token));
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationStreamInfo"), baseline.Stream.Identity,
                    baseline.Stream.Width, baseline.Stream.Height, baseline.Stream.Numerator, baseline.Stream.Denominator));
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationCaptureDetected"), baseline.CaptureBounds, baseline.Monitor));
                Log(Describe(baseline));
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationObservedShift"), baseline.Stream.ShiftUs.HasValue ? baseline.Stream.ShiftUs.Value.ToString(CultureInfo.CurrentCulture) : T("CalibrationShiftUnknown")));
                if (!adjust) return;
                if (!baseline.Stream.ShiftUs.HasValue) throw new InvalidOperationException(T("CalibrationUpdateDll"));
                savedShift = baseline.Stream.ShiftUs.Value;
                if (!baseline.Reliable) throw new InvalidOperationException(T("CalibrationSamplingInsufficient"));
                double interval = 1000000.0 / baseline.Stream.Fps;
                var measurements = new List<KeyValuePair<long, CalibrationMeasurement>>();
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    long candidate = (long)Math.Round(i * interval / count);
                    shifted = true;
                    await RequireOk("SET_SHIFT " + candidate.ToString(CultureInfo.InvariantCulture));
                    Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationWaitingShift"), candidate));
                    var measurement = await MeasureWithRetries(seconds, process, baseline.Stream, candidate, maxRetries, token);
                    Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationSweepStatus"), i + 1, count, candidate) + Describe(measurement));
                    if (!measurement.Reliable) throw new InvalidOperationException(T("CalibrationSamplingFailed"));
                    measurements.Add(new KeyValuePair<long, CalibrationMeasurement>(candidate, measurement));
                }
                double min = measurements.Min(x => x.Value.MissingRate), max = measurements.Max(x => x.Value.MissingRate);
                if (max - min < .01) throw new InvalidOperationException(T("CalibrationNoPeak"));
                var worst = measurements.OrderByDescending(x => x.Value.MissingRate).First();
                // Timing phase repeats every camera frame. Keep the result within that period.
                long result = CalibrationShift(worst.Key, interval);
                token.ThrowIfCancellationRequested();
                await RequireOk("SET_SHIFT " + result.ToString(CultureInfo.InvariantCulture));
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationWaitingShift"), result));
                var verification = await MeasureWithRetries(seconds, process, baseline.Stream, result, maxRetries, token);
                Log(T("CalibrationVerification") + Describe(verification));
                if (!verification.Reliable || verification.MissingRate > baseline.MissingRate + .01)
                    throw new InvalidOperationException(T("CalibrationVerificationFailed"));
                token.ThrowIfCancellationRequested();
                commit(result);
                savedShift = result;
                applied = true;
                Log(string.Format(CultureInfo.CurrentCulture, T("CalibrationAppliedFormula"), worst.Key, interval / 2, interval, result));
            }
            catch (OperationCanceledException) { Log(T("CalibrationCancelled")); }
            catch (Exception ex) { Log(ex.Message); }
            finally
            {
                if (shifted && !applied)
                {
                    try { await RequireOk("SET_SHIFT " + savedShift.ToString(CultureInfo.InvariantCulture)); commit(savedShift); Log(T("CalibrationRestored") + savedShift + " us"); }
                    catch (Exception ex) { Log(T("CalibrationRestoreFailed") + ex.Message); closePending = false; }
                }
                cancellation.Dispose();
                cancellation = null;
                foreach (var control in runControls) control.Enabled = true;
                stop.Enabled = false;
                if (closePending) Close();
            }
        }
        internal static long CalibrationShift(long worst, double interval)
        {
            return (long)Math.Floor(((worst + interval / 2) % interval) + .5) % (long)Math.Ceiling(interval);
        }
    }

    internal static class CalibrationCapture
    {
        internal sealed class CalibrationQrTarget
        {
            public CalibrationFrame Stream;
            public Rectangle Bounds;
        }

        private static Rectangle DesktopBounds()
        {
            return Screen.AllScreens.Select(screen => screen.Bounds).Aggregate(Rectangle.Union);
        }

        internal static CalibrationQrTarget FindTarget(Bitmap bitmap, Rectangle desktop, int port, long process, CalibrationFrame expected)
        {
            var reader = new BarcodeReader { Options = new ZXing.Common.DecodingOptions
                { PossibleFormats = new[] { BarcodeFormat.QR_CODE }, TryHarder = true } };
            var matches = (reader.DecodeMultiple(bitmap) ?? new Result[0]).Where(result =>
            {
                var frame = CalibrationFrame.Parse(result.Text);
                return frame != null && frame.Port == port && frame.Process == process &&
                    (expected == null || expected.SameStream(frame)) && result.ResultPoints != null && result.ResultPoints.Length >= 3;
            }).OrderByDescending(result =>
            {
                var points = result.ResultPoints;
                return (points.Max(p => p.X) - points.Min(p => p.X)) * (points.Max(p => p.Y) - points.Min(p => p.Y));
            });
            // If the same camera appears in multiple previews, use the largest matching QR.
            foreach (var match in matches)
            {
                var points = match.ResultPoints;
                float left = points.Min(p => p.X), right = points.Max(p => p.X);
                float top = points.Min(p => p.Y), bottom = points.Max(p => p.Y);
                int pad = (int)Math.Ceiling(Math.Max(right - left, bottom - top) * .35 + 12);
                Rectangle region = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size),
                    Rectangle.FromLTRB((int)left - pad, (int)top - pad, (int)right + pad, (int)bottom + pad));
                if (region.IsEmpty) continue;
                region.Offset(desktop.Location);
                return new CalibrationQrTarget { Stream = CalibrationFrame.Parse(match.Text), Bounds = region };
            }
            return null;
        }

        private sealed class Sample : IDisposable
        {
            public Bitmap Bitmap;
            public double Time;
            public void Dispose() => Bitmap.Dispose();
        }
        private static double RefreshRate(Screen screen)
        {
            string device = screen.DeviceName;
            uint pathCount, modeCount;
            // DisplayConfig supplies the rational rate (e.g. 60000/1001), unlike DEVMODE's integer Hz.
            if (GetDisplayConfigBufferSizes(2, out pathCount, out modeCount) == 0)
            {
                var paths = new DisplayPath[pathCount];
                var modes = new DisplayMode[modeCount];
                if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) == 0)
                {
                    for (int i = 0; i < pathCount; i++)
                    {
                        var name = new DisplaySourceName { Type = 1, Size = (uint)Marshal.SizeOf(typeof(DisplaySourceName)),
                            Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id };
                        if (DisplayConfigGetDeviceInfo(ref name) == 0 && string.Equals(name.Name, device, StringComparison.OrdinalIgnoreCase) &&
                            paths[i].Target.Rate.Denominator != 0 && paths[i].Target.Rate.Numerator > 0)
                            return (double)paths[i].Target.Rate.Numerator / paths[i].Target.Rate.Denominator;
                    }
                }
            }
            var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)), DeviceName = "", FormName = "" };
            if (!EnumDisplaySettings(device, -1, ref mode) || mode.Frequency <= 1)
                throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationRefreshUnknown", CultureInfo.CurrentUICulture));
            return mode.Frequency;
        }

        public static CalibrationMeasurement Measure(int seconds, int port, long process,
            CalibrationFrame expected, long? expectedShift, CancellationToken token)
        {
            using (new DpiScope()) return MeasureCore(seconds, port, process, expected, expectedShift, token);
        }
        private static CalibrationMeasurement MeasureCore(int seconds, int port, long process,
            CalibrationFrame expected, long? expectedShift, CancellationToken token)
        {
            Rectangle desktop = DesktopBounds();
            CalibrationQrTarget target = FindDesktopTarget(desktop, port, process, expected, token);
            Rectangle captureBounds = target.Bounds;
            Screen screen = Screen.FromPoint(new Point(captureBounds.Left + captureBounds.Width / 2, captureBounds.Top + captureBounds.Height / 2));
            double refresh = RefreshRate(screen);
            var decoder = new CalibrationQrDecoder();
            using (var capture = new CalibrationDesktopCapture(screen.DeviceName))
            {
                if (!capture.Bounds.Contains(captureBounds)) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationQrCrossMonitor", CultureInfo.CurrentUICulture));
                // Warm the learned QR grid before counting, and confirm the visible setting for every attempt.
                long? visibleShift = expectedShift ?? target.Stream.ShiftUs;
                if (visibleShift.HasValue) target.Stream = WaitForShift(target, visibleShift.Value, capture, decoder, token);
                var m = new CalibrationMeasurement { Stream = target.Stream, RefreshHz = refresh, CaptureBounds = captureBounds, Monitor = screen.DeviceName };
                using (var samples = new BlockingCollection<Sample>(8))
                using (var localStop = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    long start = Stopwatch.GetTimestamp();
                    var clock = Stopwatch.StartNew();
                    Exception captureError = null;
                    var producer = Task.Factory.StartNew(() =>
                    {
                        using (new DpiScope())
                        {
                            try
                            {
                                double first = -1, last = 0;
                                while (clock.Elapsed.TotalSeconds < seconds && !localStop.IsCancellationRequested)
                                {
                                    using (var frame = capture.Next(captureBounds, 100))
                                    {
                                        if (frame == null || frame.Timestamp <= start || clock.Elapsed.TotalSeconds >= seconds) continue;
                                        // The first image establishes the interval; accumulation before it is outside the measurement.
                                        if (first >= 0 && frame.AccumulatedFrames > 1)
                                            m.CaptureMisses += checked((int)frame.AccumulatedFrames - 1);
                                        m.MaxCaptureMs = Math.Max(m.MaxCaptureMs, frame.CopyMs);
                                        var sample = new Sample { Bitmap = frame.Bitmap,
                                            Time = (double)(frame.Timestamp - start) / Stopwatch.Frequency };
                                        // Transfer ownership to the bounded decode queue without another pixel copy.
                                        frame.Bitmap = null;
                                        if (first < 0) first = sample.Time;
                                        last = sample.Time;
                                        m.Captures++;
                                        if (!samples.TryAdd(sample)) { m.QueueDrops++; sample.Dispose(); }
                                    }
                                }
                                m.CaptureSeconds = first >= 0 ? last - first : 0;
                            }
                            catch (Exception ex) { captureError = ex; }
                            finally { samples.CompleteAdding(); }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    try
                    {
                        foreach (var sample in samples.GetConsumingEnumerable(token))
                        using (sample)
                        {
                            token.ThrowIfCancellationRequested();
                            var frame = CalibrationFrame.Parse(decoder.Decode(sample.Bitmap));
                            if (frame == null) { m.DecodeFailures++; continue; }
                            m.Observe(frame, sample.Time);
                        }
                    }
                    finally
                    {
                        localStop.Cancel();
                        producer.GetAwaiter().GetResult();
                        Sample remaining;
                        while (samples.TryTake(out remaining)) remaining.Dispose();
                    }
                    token.ThrowIfCancellationRequested();
                    if (captureError != null) throw captureError;
                }
                if (m.Unique < 2 && !m.HasSamplingErrors) throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationFrozenFrames", CultureInfo.CurrentUICulture));
                return m;
            }
        }
        private static CalibrationQrTarget FindDesktopTarget(Rectangle desktop, int port, long process,
            CalibrationFrame expected, CancellationToken token)
        {
            var captures = new List<CalibrationDesktopCapture>();
            try
            {
                foreach (var screen in Screen.AllScreens) captures.Add(new CalibrationDesktopCapture(screen.DeviceName));
                using (var image = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppRgb))
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Black);
                    var timeout = Stopwatch.StartNew();
                    while (timeout.Elapsed.TotalSeconds < 10)
                    {
                        bool updated = false;
                        foreach (var capture in captures)
                        {
                            token.ThrowIfCancellationRequested();
                            using (var frame = capture.Next(capture.Bounds, 100))
                            {
                                if (frame == null) continue;
                                graphics.DrawImageUnscaled(frame.Bitmap, capture.Bounds.Left - desktop.Left, capture.Bounds.Top - desktop.Top);
                                updated = true;
                            }
                        }
                        if (!updated) continue;
                        var target = FindTarget(image, desktop, port, process, expected);
                        if (target != null) return target;
                    }
                }
                throw new InvalidOperationException(Resources.AppStrings.ResourceManager.GetString("CalibrationQrNotFound", CultureInfo.CurrentUICulture));
            }
            finally { foreach (var capture in captures) capture.Dispose(); }
        }
        private static CalibrationFrame WaitForShift(CalibrationQrTarget target, long shift,
            CalibrationDesktopCapture capture, CalibrationQrDecoder decoder, CancellationToken token)
        {
            var timeout = Stopwatch.StartNew();
            var gate = new CalibrationShiftGate(target.Stream, shift);
            while (timeout.Elapsed.TotalSeconds < 10)
            {
                token.ThrowIfCancellationRequested();
                CalibrationFrame frame;
                using (var image = capture.Next(target.Bounds, 100))
                {
                    if (image == null) continue;
                    frame = CalibrationFrame.Parse(decoder.Decode(image.Bitmap));
                }
                if (frame != null)
                {
                    // Two advancing frames confirm the requested value reached the visible preview.
                    if (gate.Observe(frame)) return frame;
                }
            }
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                Resources.AppStrings.ResourceManager.GetString("CalibrationShiftTimeout", CultureInfo.CurrentUICulture), shift));
        }
        internal sealed class DpiScope : IDisposable
        {
            private readonly IntPtr previous;
            public DpiScope()
            {
                try { previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { }
            }
            public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct AdapterId { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct RationalRate { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)] private struct DisplaySource { public AdapterId Adapter; public uint Id, Mode, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct DisplayTarget
        {
            public AdapterId Adapter;
            public uint Id, Mode, Technology, Rotation, Scaling;
            public RationalRate Rate;
            public uint ScanLine;
            public int Available;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DisplayPath { public DisplaySource Source; public DisplayTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)] private struct DisplayMode { [FieldOffset(0)] public uint Type; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplaySourceName
        {
            public uint Type, Size;
            public AdapterId Adapter;
            public uint Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, PositionX, PositionY, Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPixel, Width, Height, DisplayFlags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int index, ref DevMode mode);
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths, ref uint modeCount, [Out] DisplayMode[] modes, IntPtr topology);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref DisplaySourceName name);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }

}
