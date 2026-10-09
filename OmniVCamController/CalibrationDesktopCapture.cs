using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OmniVCamController
{
    // All calls on an instance are serialized on its capture thread. No timer or GDI capture fallback.
    internal sealed class CalibrationDesktopCapture : IDisposable
    {
        private const int NotFound = unchecked((int)0x887A0002);
        private const int WaitTimeout = unchecked((int)0x887A0027);
        private const int AccessLost = unchecked((int)0x887A0026);
        private IntPtr device, context, duplication, staging;
        private AcquireFrame acquire;
        private ReleaseFrame release;
        private CopyRegion copy;
        private MapResource map;
        private UnmapResource unmap;
        private CreateTexture createTexture;
        private uint rotation;
        private Size stagingSize;
        public Rectangle Bounds { get; private set; }

        public sealed class DesktopFrame : IDisposable
        {
            public Bitmap Bitmap;
            public long Timestamp;
            public uint AccumulatedFrames;
            public double CopyMs;
            public void Dispose() => Bitmap?.Dispose();
        }

        public CalibrationDesktopCapture(string displayName)
        {
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Check(CreateDXGIFactory1(ref factoryId, out factory));
                // DuplicateOutput requires a device created using an adapter from IDXGIFactory1.
                var adapters = Method<Enumerate>(factory, 12);
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    int hr = adapters(factory, a, out adapter);
                    if (hr == NotFound) break;
                    Check(hr);
                    try
                    {
                        var outputs = Method<Enumerate>(adapter, 7);
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            hr = outputs(adapter, o, out output);
                            if (hr == NotFound) break;
                            Check(hr);
                            try
                            {
                                OutputDescription desc;
                                Check(Method<GetOutputDescription>(output, 7)(output, out desc));
                                if (desc.Attached == 0 || !string.Equals(desc.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                                Bounds = Rectangle.FromLTRB(desc.Desktop.Left, desc.Desktop.Top, desc.Desktop.Right, desc.Desktop.Bottom);
                                rotation = desc.Rotation;
                                int feature;
                                Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out feature, out context));
                                Guid outputId = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
                                IntPtr output1;
                                Check(Marshal.QueryInterface(output, ref outputId, out output1));
                                try { Check(Method<DuplicateOutput>(output1, 22)(output1, device, out duplication)); }
                                finally { Free(ref output1); }
                                acquire = Method<AcquireFrame>(duplication, 8);
                                release = Method<ReleaseFrame>(duplication, 14);
                                createTexture = Method<CreateTexture>(device, 5);
                                map = Method<MapResource>(context, 14);
                                unmap = Method<UnmapResource>(context, 15);
                                copy = Method<CopyRegion>(context, 46);
                                return;
                            }
                            finally { Free(ref output); }
                        }
                    }
                    finally { Free(ref adapter); }
                }
                throw new InvalidOperationException(Text("CalibrationDisplayChanged"));
            }
            catch { Dispose(); throw; }
            finally { Free(ref factory); }
        }

        // Bounds are desktop coordinates. Only this region is transferred from GPU to CPU.
        // A bounded wait lets callers check cancellation; a timeout is not a missed frame.
        public DesktopFrame Next(Rectangle region, int timeoutMs)
        {
            if (!Bounds.Contains(region) || region.IsEmpty) throw new InvalidOperationException(Text("CalibrationQrCrossMonitor"));
            FrameInfo info;
            IntPtr resource;
            int hr = acquire(duplication, (uint)timeoutMs, out info, out resource);
            if (hr == WaitTimeout) return null;
            if (hr == AccessLost) throw new InvalidOperationException(Text("CalibrationDisplayChanged"));
            Check(hr);
            DesktopFrame result = null;
            try
            {
                // Pointer-only updates must not count as desktop frames or QR failures.
                if (info.LastPresentTime == 0) return null;
                var watch = Stopwatch.StartNew();
                Rectangle local = region;
                local.Offset(-Bounds.Left, -Bounds.Top);
                Size rawSize = rotation == 2 || rotation == 4 ? new Size(Bounds.Height, Bounds.Width) : Bounds.Size;
                Rectangle source = SourceRegion(local, rawSize, rotation);
                EnsureStaging(source.Size);
                Guid textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                IntPtr texture;
                Check(Marshal.QueryInterface(resource, ref textureId, out texture));
                try
                {
                    var box = new ResourceBox { Left = (uint)source.Left, Top = (uint)source.Top,
                        Right = (uint)source.Right, Bottom = (uint)source.Bottom, Back = 1 };
                    copy(context, staging, 0, 0, 0, 0, texture, 0, ref box);
                }
                finally { Free(ref texture); }
                MappedResource mapped;
                Check(map(context, staging, 0, 1, 0, out mapped));
                Bitmap bitmap = null;
                try
                {
                    bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgb);
                    BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        for (int y = 0; y < source.Height; y++)
                            MoveMemory(IntPtr.Add(data.Scan0, y * data.Stride),
                                IntPtr.Add(mapped.Data, checked(y * (int)mapped.RowPitch)), (UIntPtr)(source.Width * 4));
                    }
                    finally { bitmap.UnlockBits(data); }
                    if (rotation == 2) bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (rotation == 3) bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (rotation == 4) bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    result = new DesktopFrame { Bitmap = bitmap, Timestamp = info.LastPresentTime,
                        AccumulatedFrames = info.AccumulatedFrames, CopyMs = watch.Elapsed.TotalMilliseconds };
                    return result;
                }
                catch { bitmap?.Dispose(); throw; }
                finally { unmap(context, staging, 0); }
            }
            finally
            {
                Free(ref resource);
                try { Check(release(duplication)); }
                catch { result?.Dispose(); throw; }
            }
        }

        internal static Rectangle SourceRegion(Rectangle local, Size raw, uint rotate)
        {
            if (rotate == 2) return Rectangle.FromLTRB(local.Top, raw.Height - local.Right, local.Bottom, raw.Height - local.Left);
            if (rotate == 3) return Rectangle.FromLTRB(raw.Width - local.Right, raw.Height - local.Bottom, raw.Width - local.Left, raw.Height - local.Top);
            if (rotate == 4) return Rectangle.FromLTRB(raw.Width - local.Bottom, local.Left, raw.Width - local.Top, local.Right);
            return local;
        }

        private void EnsureStaging(Size size)
        {
            if (staging != IntPtr.Zero && size == stagingSize) return;
            Free(ref staging);
            var desc = new TextureDescription { Width = (uint)size.Width, Height = (uint)size.Height,
                MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3, CpuAccess = 0x20000 };
            Check(createTexture(device, ref desc, IntPtr.Zero, out staging));
            stagingSize = size;
        }

        public void Dispose()
        {
            Free(ref staging);
            Free(ref duplication);
            Free(ref context);
            Free(ref device);
        }
        private static void Free(ref IntPtr ptr) { if (ptr != IntPtr.Zero) { Marshal.Release(ptr); ptr = IntPtr.Zero; } }
        private static string Text(string key) => Resources.AppStrings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        private static void Check(int hr)
        {
            if (hr == AccessLost) throw new InvalidOperationException(Text("CalibrationDisplayChanged"));
            if (hr < 0) throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                Text("CalibrationDesktopUnavailable"), hr.ToString("X8")), Marshal.GetExceptionForHR(hr));
        }
        private static T Method<T>(IntPtr instance, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T));
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public NativeRect Desktop;
            public int Attached;
            public uint Rotation;
            public IntPtr Monitor;
        }
        [StructLayout(LayoutKind.Sequential)] private struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
            public uint MetadataSize, PointerShapeSize;
        }
        [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, Bind, CpuAccess, Misc;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ResourceBox { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr self, uint index, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescription(IntPtr self, out OutputDescription value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutput(IntPtr self, IntPtr device, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireFrame(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrame(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDescription desc, IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegion(IntPtr self, IntPtr destination, uint subresource, uint x, uint y, uint z, IntPtr source, uint sourceSubresource, ref ResourceBox box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(IntPtr self, IntPtr resource, uint subresource, uint type, uint flags, out MappedResource mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(IntPtr self, IntPtr resource, uint subresource);
        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint type, IntPtr software, uint flags,
            IntPtr levels, uint count, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void MoveMemory(IntPtr destination, IntPtr source, UIntPtr length);
    }
}
