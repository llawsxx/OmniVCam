using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using ZXing;

internal static class CalibrationChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Bitmap SearchPreview(Bitmap card, Size size, Rectangle preview)
    {
        var image = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.DimGray);
            graphics.DrawImage(card, preview);
        }
        return image;
    }
    private static void CheckMultiDisplaySearch(Assembly assembly)
    {
        var type = assembly.GetType("OmniVCamController.CalibrationCapture", true);
        var method = type.GetMethod("SearchDisplays", BindingFlags.Static | BindingFlags.NonPublic);
        var frameType = assembly.GetType("OmniVCamController.CalibrationFrame", true);
        using (var card = (Bitmap)Image.FromFile("tests/out/card-1-0.bmp"))
        using (var normal = SearchPreview(card, new Size(800, 600), new Rectangle(80, 60, 640, 480)))
        using (var smaller = SearchPreview(card, new Size(800, 600), new Rectangle(80, 60, 320, 240)))
        {
            var payload = new BarcodeReader().Decode(card).Text;
            var parsed = frameType.GetMethod("Parse").Invoke(null, new object[] { payload });
            long process = (long)frameType.GetField("Process").GetValue(parsed);
            Func<string[], Func<string, Rectangle>, Func<string, int, Bitmap>, Action<string>, int,
                System.Threading.CancellationToken, double, object> search = (names, open, next, close, port, token, seconds) =>
                    method.Invoke(null, new object[] { names, open, next, close, port, process, null, token, seconds });
            var negative = new Rectangle(-30000, 15000, 800, 600);
            var closed = new List<string>(); var waits = new List<KeyValuePair<string, int>>(); int validCalls = 0;
            object found = search(new[] { "unsupported virtual", "idle virtual", "physical" }, name =>
            {
                if (name == "unsupported virtual") throw new InvalidOperationException("DXGI_TEST_UNSUPPORTED");
                return name == "physical" ? negative : new Rectangle(30000, -15000, 1920, 1080);
            }, (name, wait) =>
            {
                waits.Add(new KeyValuePair<string, int>(name, wait));
                return name == "physical" && ++validCalls >= 2 ? (Bitmap)normal.Clone() : null;
            }, closed.Add, 16999, System.Threading.CancellationToken.None, 2);
            Rectangle crop = (Rectangle)found.GetType().GetField("Bounds").GetValue(found);
            Check(negative.Contains(crop) && crop.Width < negative.Width, "Discovery scans each display independently and preserves negative desktop coordinates across large gaps");
            Check(closed.Count == 2 && closed.Contains("physical") && closed.Contains("idle virtual"), "Unsupported displays do not block healthy outputs and every opened output is disposed");
            Check(waits.Count == 4 && waits.Count(pair => pair.Value > 0) == 2 &&
                waits.Where(pair => pair.Value > 0).Select(pair => pair.Key).Distinct().Count() == 2, "Idle outputs only wait in rotation; other outputs are polled without serial waits");

            closed.Clear();
            found = search(new[] { "small preview", "large preview" }, name =>
                name == "small preview" ? negative : new Rectangle(8000, 0, 800, 600),
                (name, wait) => (Bitmap)(name == "small preview" ? smaller : normal).Clone(), closed.Add, 16999,
                System.Threading.CancellationToken.None, 2);
            crop = (Rectangle)found.GetType().GetField("Bounds").GetValue(found);
            Check(crop.Left >= 8000 && closed.Count == 2, "Discovery chooses the larger matching QR across multiple displays");

            closed.Clear(); int failures = 0;
            found = search(new[] { "lost display", "physical" }, name => negative, (name, wait) =>
            {
                if (name == "lost display") { failures++; throw new InvalidOperationException("DXGI_TEST_ACCESS_LOST"); }
                return (Bitmap)normal.Clone();
            }, closed.Add, 16999, System.Threading.CancellationToken.None, 2);
            Check(found != null && failures == 1 && closed.Count == 2, "A display lost during capture does not abort other display discovery");

            closed.Clear();
            try
            {
                search(new[] { "physical" }, name => negative, (name, wait) => (Bitmap)normal.Clone(), closed.Add,
                    17000, System.Threading.CancellationToken.None, .001);
                throw new Exception("A QR from another camera must not match");
            }
            catch (TargetInvocationException ex)
            {
                Check(ex.InnerException is InvalidOperationException && ex.InnerException.Message.Contains("physical") &&
                    !ex.InnerException.Message.Contains("DXGI_TEST"), "No-match diagnostics distinguish captured output from capture failures");
            }
            Check(closed.Count == 1, "A no-match timeout disposes its capture");

            closed.Clear();
            try
            {
                search(new[] { "idle virtual", "unsupported virtual" }, name =>
                {
                    if (name == "unsupported virtual") throw new InvalidOperationException("DXGI_TEST_UNSUPPORTED");
                    return negative;
                }, (name, wait) => null, closed.Add, 16999, System.Threading.CancellationToken.None, .001);
                throw new Exception("No captured image must report a failure");
            }
            catch (TargetInvocationException ex)
            {
                Check(ex.InnerException.Message.Contains("idle virtual") && ex.InnerException.Message.Contains("0") &&
                    ex.InnerException.Message.Contains("unsupported virtual") && ex.InnerException.Message.Contains("DXGI_TEST_UNSUPPORTED"),
                    "Discovery timeout reports zero-frame outputs alongside skipped DXGI failures");
            }
            Check(closed.Count == 1, "Only successfully opened idle captures are disposed");

            using (var stop = new System.Threading.CancellationTokenSource())
            {
                closed.Clear();
                try
                {
                    search(new[] { "physical" }, name => negative, (name, wait) =>
                    { stop.Cancel(); return (Bitmap)normal.Clone(); }, closed.Add, 16999, stop.Token, 2);
                    throw new Exception("Cancellation must not return a found target");
                }
                catch (TargetInvocationException ex) { Check(ex.InnerException is OperationCanceledException, "Discovery respects cancellation even when a valid QR arrives"); }
                Check(closed.Count == 1, "Cancellation disposes every opened display");
            }
        }
        var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
        foreach (string language in new[] { "en", "zh-CN", "zh-TW" })
        {
            var culture = CultureInfo.GetCultureInfo(language);
            foreach (string key in new[] { "CalibrationSearchDisplayFailed", "CalibrationSearchDisplaySummary" })
                Check(!string.IsNullOrWhiteSpace(resources.GetString(key, culture)), "Display diagnostics use the existing language resources");
        }
        Console.WriteLine("PASS: independent multi-display QR discovery, unsupported/idle/lost outputs, largest-preview selection, negative coordinates, camera identity, diagnostic details, cancellation and capture disposal.");
    }
    private static void CheckDesktopCapture(Assembly assembly)
    {
        var type = assembly.GetType("OmniVCamController.CalibrationDesktopCapture", true);
        Check(System.Runtime.InteropServices.Marshal.SizeOf(type.GetNestedType("FrameInfo", BindingFlags.NonPublic)) == 48, "Desktop frame metadata ABI");
        Check(System.Runtime.InteropServices.Marshal.SizeOf(type.GetNestedType("TextureDescription", BindingFlags.NonPublic)) == 44, "Staging texture description ABI");
        var sourceRegion = type.GetMethod("SourceRegion", BindingFlags.Static | BindingFlags.NonPublic);
        using (var raw = new Bitmap(96, 64))
        {
            for (int y = 0; y < raw.Height; y++) for (int x = 0; x < raw.Width; x++)
                raw.SetPixel(x, y, Color.FromArgb(x * 2, y * 3, (x + y) % 256));
            for (uint rotation = 1; rotation <= 4; rotation++)
            using (var upright = (Bitmap)raw.Clone())
            {
                var flip = rotation == 2 ? RotateFlipType.Rotate90FlipNone : rotation == 3 ? RotateFlipType.Rotate180FlipNone : rotation == 4 ? RotateFlipType.Rotate270FlipNone : RotateFlipType.RotateNoneFlipNone;
                upright.RotateFlip(flip);
                Rectangle requested = new Rectangle(5, 7, 21, 13);
                Rectangle source = (Rectangle)sourceRegion.Invoke(null, new object[] { requested, raw.Size, rotation });
                using (var crop = raw.Clone(source, raw.PixelFormat))
                {
                    crop.RotateFlip(flip);
                    Check(crop.Size == requested.Size, "Portrait/landscape crop dimensions");
                    for (int y = 0; y < crop.Height; y++) for (int x = 0; x < crop.Width; x++)
                        Check(crop.GetPixel(x, y) == upright.GetPixel(requested.X + x, requested.Y + y), "Rotated monitor crop pixel coordinates");
                }
            }
        }
        var dpi = assembly.GetType("OmniVCamController.CalibrationCapture", true).GetNestedType("DpiScope", BindingFlags.NonPublic);
        using ((IDisposable)Activator.CreateInstance(dpi, true))
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        using (var capture = (IDisposable)Activator.CreateInstance(type, new object[] { screen.DeviceName }))
        {
            Rectangle bounds = (Rectangle)type.GetProperty("Bounds").GetValue(capture);
            var next = type.GetMethod("Next");
            object first = null;
            for (int i = 0; i < 10 && first == null; i++) first = next.Invoke(capture, new object[] { bounds, 100 });
            Check(first != null, "Initial DXGI desktop image");
            var ft = first.GetType();
            using ((IDisposable)first)
            {
                var image = (Bitmap)ft.GetField("Bitmap").GetValue(first);
                Check(image.Size == bounds.Size, "Live desktop orientation and dimensions");
                Check((long)ft.GetField("Timestamp").GetValue(first) > 0, "System presentation timestamp");
            }
            Rectangle region = new Rectangle(bounds.Left + 20, bounds.Top + 20, Math.Min(128, bounds.Width - 20), Math.Min(96, bounds.Height - 20));
            // Reopening provides an initial frame even if the desktop is stationary.
            capture.Dispose(); // DXGI allows only one duplication per process and output.
            using (var cropped = (IDisposable)Activator.CreateInstance(type, new object[] { screen.DeviceName }))
            {
                object sample = null;
                for (int i = 0; i < 10 && sample == null; i++) sample = next.Invoke(cropped, new object[] { region, 100 });
                Check(sample != null, "Live GPU region readback");
                using ((IDisposable)sample)
                {
                    var image = (Bitmap)ft.GetField("Bitmap").GetValue(sample);
                    Check(image.Size == region.Size, "Only the requested region reaches the CPU");
                    using (var reference = new Bitmap(region.Width, region.Height))
                    {
                        using (var g = Graphics.FromImage(reference)) g.CopyFromScreen(region.Location, Point.Empty, region.Size);
                        int same = 0, total = 0;
                        for (int y = 0; y < image.Height; y += 8) for (int x = 0; x < image.Width; x += 8)
                        {
                            Color actual = image.GetPixel(x, y), expected = reference.GetPixel(x, y);
                            if (Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2) same++;
                            total++;
                        }
                        Check(same > total * .8, "Live region pixels match the desktop screenshot");
                    }
                }
                // An OS wait remains bounded when no new image is available; no scheduling timer is involved.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                object idle = next.Invoke(cropped, new object[] { region, 100 });
                if (idle != null) ((IDisposable)idle).Dispose();
                Check(watch.ElapsedMilliseconds < 1000, "Idle capture allows prompt cancellation");
            }
            Console.WriteLine("PASS: live DXGI desktop and cropped GPU capture on " + screen.DeviceName);
        }
    }
    private static void CheckLivePreview(Assembly assembly)
    {
        var type = assembly.GetType("OmniVCamController.CalibrationCapture", true);
        var dpi = type.GetNestedType("DpiScope", BindingFlags.NonPublic);
        using ((IDisposable)Activator.CreateInstance(dpi, true))
        using (var preview = new System.Windows.Forms.Form())
        using (var picture = new System.Windows.Forms.PictureBox())
        using (var animation = new System.Windows.Forms.Timer())
        using (var stop = new System.Threading.CancellationTokenSource())
        {
            int process = System.Diagnostics.Process.GetCurrentProcess().Id, frame = 0;
            var writer = new BarcodeWriter { Format = BarcodeFormat.QR_CODE,
                Options = new ZXing.Common.EncodingOptions { Width = 320, Height = 320, Margin = 4 } };
            Action draw = () =>
            {
                string payload = "OVCAL2:" + "29199" + process.ToString("D10") + 123456L.ToString("D19") +
                    "00640" + "00480" + 30.ToString("D10") + 1.ToString("D10") + frame.ToString("D19") + "0" + 8333L.ToString("D19");
                var old = picture.Image;
                picture.Image = writer.Write(payload);
                if (old != null) old.Dispose();
                frame++;
            };
            preview.Text = "Calibration capture integration test";
            preview.ClientSize = new Size(360, 360);
            preview.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            preview.Location = new Point(bounds.Left + 120, bounds.Top + 120);
            preview.TopMost = true;
            preview.ShowInTaskbar = false;
            preview.BackColor = Color.White;
            picture.Location = new Point(20, 20);
            picture.Size = new Size(320, 320);
            preview.Controls.Add(picture);
            draw();
            animation.Interval = 33;
            var measure = type.GetMethod("Measure");
            System.Threading.Tasks.Task<object> task = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            animation.Tick += (sender, e) =>
            {
                draw();
                if (watch.Elapsed.TotalSeconds > 15) stop.Cancel();
                if (task != null && task.IsCompleted) preview.Close();
            };
            preview.Shown += (sender, e) =>
            {
                animation.Start();
                task = System.Threading.Tasks.Task.Run(() => measure.Invoke(null, new object[] { 2, 29199, (long)process, null, (long?)8333, stop.Token }));
            };
            try
            {
                System.Windows.Forms.Application.Run(preview);
                animation.Stop();
                Check(task != null && task.IsCompleted, "Live preview measurement completes");
                object stats = task.GetAwaiter().GetResult();
                var statsType = stats.GetType();
                Check((int)statsType.GetField("Unique").GetValue(stats) > 20, "Decode advancing QR frames from desktop updates");
                Check((double)statsType.GetProperty("DisplayFps").GetValue(stats) > 10, "Presentation timestamps produce observed FPS");
                object stream = statsType.GetField("Stream").GetValue(stats);
                Check((long)stream.GetType().GetField("ShiftUs").GetValue(stream) == 8333, "Live Shift confirmed before measuring");
                Console.WriteLine("PASS: dynamic desktop QR, cropped event capture, Shift confirmation; unique=" + statsType.GetField("Unique").GetValue(stats) +
                    ", QR failures=" + statsType.GetField("DecodeFailures").GetValue(stats) + ", capture misses=" + statsType.GetField("CaptureMisses").GetValue(stats));
            }
            finally
            {
                stop.Cancel();
                if (picture.Image != null) { picture.Image.Dispose(); picture.Image = null; }
            }
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL type: " + ex.GetType().FullName);
            Console.WriteLine("FAIL message: " + ex.Message);
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                Console.WriteLine("INNER type: " + inner.GetType().FullName);
                Console.WriteLine("INNER message: " + inner.Message);
                Console.WriteLine(inner.StackTrace);
            }
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
    private static int Run(string[] args)
    {
        var assembly = Assembly.LoadFrom(args.Length > 0 ? args[0] : "OmniVCamController/bin/Debug/OmniVCamController.exe");
        CheckMultiDisplaySearch(assembly);
        CheckDesktopCapture(assembly);
        CheckLivePreview(assembly);
        var frameType = assembly.GetType("OmniVCamController.CalibrationFrame", true);
        var measurementType = assembly.GetType("OmniVCamController.CalibrationMeasurement", true);
        var formType = assembly.GetType("OmniVCamController.CameraCalibrationForm", true);
        var captureType = assembly.GetType("OmniVCamController.CalibrationCapture", true);
        var decoderType = assembly.GetType("OmniVCamController.CalibrationQrDecoder", true);
        Check(System.Runtime.InteropServices.Marshal.SizeOf(captureType.GetNestedType("DisplayPath", BindingFlags.NonPublic)) == 72, "DisplayConfig path ABI");
        Check(System.Runtime.InteropServices.Marshal.SizeOf(captureType.GetNestedType("DisplaySourceName", BindingFlags.NonPublic)) == 84, "DisplayConfig source name ABI");
        double monitorHz = (double)captureType.GetMethod("RefreshRate", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { System.Windows.Forms.Screen.PrimaryScreen });
        Check(monitorHz > 1, "Read actual monitor refresh rate");
        Console.WriteLine("Monitor refresh: " + monitorHz.ToString("F3") + " Hz");
        var parse = frameType.GetMethod("Parse");
        Func<string, object> frame = payload => parse.Invoke(null, new object[] { payload });
        Func<int, object> numbered = n => frame("OVCAL1|16999|42|123456|640|480|60000|1001|" + n);
        Check(frame("OVCAL1|16999|42|123456|640|480|60|0|1") == null, "Reject zero denominator");
        Check(frame("not a calibration card") == null, "Reject unrelated QR");
        Check(frame("OVCAL1|16999|42|123456|640|480|60|1|-1") == null, "Reject negative frame");
        object stats = Activator.CreateInstance(measurementType, true);
        measurementType.GetField("Stream").SetValue(stats, numbered(100));
        var observe = measurementType.GetMethod("Observe");
        int[] sequence = { 100, 100, 101, 104, 104, 105 };
        for (int i = 0; i < sequence.Length; i++) observe.Invoke(stats, new object[] { numbered(sequence[i]), i * .02 });
        Check((long)measurementType.GetField("Missing").GetValue(stats) == 2, "100,100,101,104,104,105 must lose exactly 2 frames");
        Check((int)measurementType.GetField("Repeated").GetValue(stats) == 2, "Repeat count");
        Check((int)measurementType.GetField("Unique").GetValue(stats) == 4, "Unique count");
        Check(Math.Abs((double)measurementType.GetProperty("DisplayFps").GetValue(stats) - 30) < .0001, "FPS uses unique observed frames, not frame span");
        try { observe.Invoke(stats, new object[] { numbered(1), .12 }); throw new Exception("Restart was accepted"); }
        catch (TargetInvocationException ex) { Check(ex.InnerException is InvalidOperationException, "Restart must abort"); }
        var retryType = assembly.GetType("OmniVCamController.CalibrationRetryPolicy", true);
        var tryRetry = retryType.GetMethod("TryRetry");
        object retry = Activator.CreateInstance(retryType, new object[] { 3 });
        Check(!(bool)tryRetry.Invoke(retry, new[] { stats }), "Actual frame-number gaps must not trigger capture retries");
        foreach (string error in new[] { "CaptureMisses", "QueueDrops", "DecodeFailures" })
        {
            object dirty = Activator.CreateInstance(measurementType, true);
            measurementType.GetField(error).SetValue(dirty, 1);
            Check((bool)measurementType.GetProperty("HasSamplingErrors").GetValue(dirty), "Even one " + error + " invalidates an attempt");
            Check((bool)tryRetry.Invoke(retry, new[] { dirty }), "Retry each kind of sampling failure");
        }
        object failed = Activator.CreateInstance(measurementType, true);
        measurementType.GetField("DecodeFailures").SetValue(failed, 1);
        Check(!(bool)tryRetry.Invoke(retry, new[] { failed }), "Three retries means at most four attempts");
        Check((int)retryType.GetProperty("UsedRetries").GetValue(retry) == 3, "Retry count excludes the initial attempt");
        Check(!(bool)tryRetry.Invoke(Activator.CreateInstance(retryType, new object[] { 0 }), new[] { failed }), "Zero disables retries");
        Check((bool)tryRetry.Invoke(Activator.CreateInstance(retryType, new object[] { 3 }), new[] { failed }), "Each Shift has its own retry allowance");
        object healthy = Activator.CreateInstance(measurementType, true);
        measurementType.GetField("Stream").SetValue(healthy, numbered(100));
        measurementType.GetField("Unique").SetValue(healthy, 30);
        measurementType.GetField("Span").SetValue(healthy, 30L);
        measurementType.GetField("Captures").SetValue(healthy, 301);
        measurementType.GetField("CaptureSeconds").SetValue(healthy, 5.0);
        measurementType.GetField("RefreshHz").SetValue(healthy, 60.0);
        Check((bool)measurementType.GetProperty("Reliable").GetValue(healthy), "Clean, fast sampling accepted");
        measurementType.GetField("DecodeFailures").SetValue(healthy, 1);
        Check(!(bool)measurementType.GetProperty("Reliable").GetValue(healthy), "One failed decode rejects an otherwise healthy attempt");
        var shift = formType.GetMethod("CalibrationShift", BindingFlags.Static | BindingFlags.NonPublic);
        Check((long)shift.Invoke(null, new object[] { 0L, 1000000.0 / 60 }) == 8333, "60fps half period");
        Check((long)shift.Invoke(null, new object[] { 12000L, 1000000.0 / 60 }) == 3667, "Wrap one period");
        var reader = new BarcodeReader { Options = new ZXing.Common.DecodingOptions { PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
        for (int size = 0; size < 3; size++) for (int n = 0; n < 3; n++)
        using (var bitmap = (Bitmap)Image.FromFile("tests/out/card-" + size + "-" + n + ".bmp"))
        {
            var result = reader.Decode(bitmap);
            Check(result != null, "Native card QR failed: size " + size + ", frame " + n);
            object parsed = frame(result.Text);
            Check(parsed != null && (long)frameType.GetField("Frame").GetValue(parsed) == n, "Native QR must carry the changing frame index");
            Check((int)frameType.GetField("Width").GetValue(parsed) == bitmap.Width, "Native QR output width");
            Check((int)frameType.GetField("Numerator").GetValue(parsed) == 60000 && (int)frameType.GetField("Denominator").GetValue(parsed) == 1001, "Native QR rational FPS");
            Check((long)frameType.GetField("ShiftUs").GetValue(parsed) == new long[] { 0, 1042, 8333 }[n], "Native QR carries current Shift");
        }
        var resources = new System.Resources.ResourceManager("OmniVCamController.Resources.AppStrings", assembly);
        foreach (string culture in new[] { "en", "zh-CN", "zh-TW" })
        {
            Check(resources.GetString("CalibrationStatistics", CultureInfo.GetCultureInfo(culture)).Contains("{5}"), "Localized drop count missing");
            foreach (string key in new[] { "CalibrationRetries", "CalibrationDiscardedSample", "CalibrationRetrying", "CalibrationRetriesExhausted" })
                Check(!string.IsNullOrEmpty(resources.GetString(key, CultureInfo.GetCultureInfo(culture))), "Localized retry resource missing: " + key);
        }
        var decode = decoderType.GetMethod("Decode");
        var gateType = assembly.GetType("OmniVCamController.CalibrationShiftGate", true);
        var gateObserve = gateType.GetMethod("Observe");
        object gate = Activator.CreateInstance(gateType, new object[] { numbered(100), 8333L });
        Func<int, long, object> shiftedFrame = (n, value) =>
        {
            object f = numbered(n);
            frameType.GetField("ShiftUs").SetValue(f, (long?)value);
            return f;
        };
        Check(!(bool)gateObserve.Invoke(gate, new[] { shiftedFrame(100, 0) }), "Old Shift must not start counting");
        Check(!(bool)gateObserve.Invoke(gate, new[] { shiftedFrame(101, 8333) }), "One matching QR is insufficient");
        Check(!(bool)gateObserve.Invoke(gate, new[] { shiftedFrame(101, 8333) }), "Repeated QR does not confirm a transition");
        Check((bool)gateObserve.Invoke(gate, new[] { shiftedFrame(102, 8333) }), "Two advancing matching QR frames confirm Shift");
        Check(!(bool)gateObserve.Invoke(gate, new[] { shiftedFrame(103, 0) }), "Old Shift resets confirmation");
        Check(!(bool)gateObserve.Invoke(gate, new[] { shiftedFrame(104, 8333) }), "Confirmation must restart after a mismatched Shift");
        Check((bool)gateObserve.Invoke(gate, new[] { shiftedFrame(105, 8333) }), "Reconfirm switched Shift");
        object shiftedStats = Activator.CreateInstance(measurementType, true);
        measurementType.GetField("Stream").SetValue(shiftedStats, shiftedFrame(100, 8333));
        observe.Invoke(shiftedStats, new object[] { shiftedFrame(100, 8333), 0.0 });
        try { observe.Invoke(shiftedStats, new object[] { shiftedFrame(101, 0), .02 }); throw new Exception("Mismatched Shift was counted"); }
        catch (TargetInvocationException ex) { Check(ex.InnerException is InvalidOperationException, "Shift mismatch must abort measurement"); }
        var findTarget = captureType.GetMethod("FindTarget", BindingFlags.Static | BindingFlags.NonPublic);
        using (var desktopImage = new Bitmap(1920, 1080))
        using (var card = (Bitmap)Image.FromFile("tests/out/card-1-0.bmp"))
        {
            using (var graphics = Graphics.FromImage(desktopImage))
            {
                graphics.Clear(Color.DimGray);
                graphics.DrawImageUnscaled(card, 400, 250);
            }
            long process = (long)frameType.GetField("Process").GetValue(frame(reader.Decode(card).Text));
            Rectangle desktop = new Rectangle(-1920, 0, 1920, 1080);
            object detected = findTarget.Invoke(null, new object[] { desktopImage, desktop, 16999, process, null });
            Check(detected != null, "Discover matching QR on full desktop");
            Rectangle crop = (Rectangle)detected.GetType().GetField("Bounds").GetValue(detected);
            Check(crop.Left < 0 && crop.Width < desktop.Width && crop.Height < desktop.Height, "Shrink capture with negative monitor coordinates");
            Check(findTarget.Invoke(null, new object[] { desktopImage, desktop, 17000, process, null }) == null, "Ignore QR from a different camera connection");
        }
        object trackedDecoder = Activator.CreateInstance(decoderType, true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int scale = 0; scale < 2; scale++)
        {
            trackedDecoder = Activator.CreateInstance(decoderType, true);
            for (int n = 0; n < 3; n++)
            using (var original = (Bitmap)Image.FromFile("tests/out/card-1-" + n + ".bmp"))
            using (var preview = new Bitmap(scale == 0 ? 640 : 317, scale == 0 ? 480 : 238))
            {
                using (var graphics = Graphics.FromImage(preview))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    graphics.DrawImage(original, new Rectangle(Point.Empty, preview.Size));
                }
                string payload = (string)decode.Invoke(trackedDecoder, new object[] { preview });
                Check(payload != null && (long)frameType.GetField("Frame").GetValue(frame(payload)) == n, "Tracked decode at fractional preview scale");
            }
        }
        // Erase the top-left finder after acquisition: data remains readable with the learned grid.
        trackedDecoder = Activator.CreateInstance(decoderType, true);
        using (var first = (Bitmap)Image.FromFile("tests/out/card-1-0.bmp"))
        using (var next = (Bitmap)Image.FromFile("tests/out/card-1-1.bmp"))
        {
            Check(decode.Invoke(trackedDecoder, new object[] { first }) != null, "Acquire stable grid");
            Result reference = reader.Decode(next);
            ResultPoint finder = reference.ResultPoints[1];
            int modules = (int)decoderType.GetField("dimension", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(trackedDecoder);
            float pitch = ResultPoint.distance(reference.ResultPoints[1], reference.ResultPoints[2]) / (modules - 7);
            using (var graphics = Graphics.FromImage(next))
                graphics.FillRectangle(Brushes.White, finder.X - 3.5f * pitch, finder.Y - 3.5f * pitch, 7 * pitch, 7 * pitch);
            string recovered = (string)decode.Invoke(trackedDecoder, new object[] { next });
            Check(recovered != null && (long)frameType.GetField("Frame").GetValue(frame(recovered)) == 1, "Learned grid recovers without searching for the damaged finder");
        }
        Console.WriteLine("Tracked decode checks: " + watch.ElapsedMilliseconds + " ms");
        Console.WriteLine("PASS: native QR with Shift, fullscreen discovery/crop, scaled preview, cached grid, Shift confirmation, statistics, retry limits, three languages.");
        return 0;
    }
}
