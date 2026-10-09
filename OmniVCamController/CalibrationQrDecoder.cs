using System;
using System.Collections.Generic;
using System.Drawing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace OmniVCamController
{
    // The preview is a flat screen image. Its QR grid stays put while data changes every frame.
    // Learn module centres once, then decode that grid without searching for finders on every sample.
    internal sealed class CalibrationQrDecoder
    {
        private readonly Decoder decoder = new Decoder();
        private readonly IDictionary<DecodeHintType, object> hints = new Dictionary<DecodeHintType, object>
            { { DecodeHintType.TRY_HARDER, true } };
        private Point[] centres;
        private int dimension;
        private Size imageSize;

        public string Decode(Bitmap bitmap)
        {
            var source = new BitmapLuminanceSource(bitmap);
            var binary = new BinaryBitmap(new HybridBinarizer(source)).BlackMatrix;
            if (binary == null) return null;
            if (centres != null && imageSize == bitmap.Size)
            {
                var grid = new BitMatrix(dimension);
                for (int y = 0; y < dimension; y++)
                    for (int x = 0; x < dimension; x++)
                    {
                        Point centre = centres[y * dimension + x];
                        grid[x, y] = binary[centre.X, centre.Y];
                    }
                var decoded = decoder.decode(grid, hints);
                if (decoded != null && CalibrationFrame.Parse(decoded.Text) != null) return decoded.Text;
            }
            // Reacquire after movement, resizing, or a failed cached-grid decode.
            string result = Locate(binary, bitmap.Size);
            if (result != null) return result;
            // A global threshold can recover samples whose local threshold was disrupted by scaling.
            var global = new BinaryBitmap(new GlobalHistogramBinarizer(source)).BlackMatrix;
            return global == null ? null : Locate(global, bitmap.Size);
        }

        private string Locate(BitMatrix binary, Size size)
        {
            var detected = new Detector(binary).detect(hints);
            if (detected == null) return null;
            var decoded = decoder.decode(detected.Bits, hints);
            if (decoded == null || CalibrationFrame.Parse(decoded.Text) == null) return null;
            // Finder centres lie 3.5 modules from the top/left edges, and 7 modules apart
            // from their opposite edges. Use their two screen-space axes to map module centres.
            int modules = detected.Bits.Width;
            var bottomLeft = detected.Points[0];
            var topLeft = detected.Points[1];
            var topRight = detected.Points[2];
            var learned = new Point[modules * modules];
            for (int y = 0; y < modules; y++)
                for (int x = 0; x < modules; x++)
                {
                    float alongX = (x - 3.0f) / (modules - 7);
                    float alongY = (y - 3.0f) / (modules - 7);
                    int px = (int)Math.Floor(topLeft.X + alongX * (topRight.X - topLeft.X) + alongY * (bottomLeft.X - topLeft.X));
                    int py = (int)Math.Floor(topLeft.Y + alongX * (topRight.Y - topLeft.Y) + alongY * (bottomLeft.Y - topLeft.Y));
                    if (px < 0 || py < 0 || px >= size.Width || py >= size.Height) return decoded.Text;
                    learned[y * modules + x] = new Point(px, py);
                }
            dimension = modules;
            centres = learned;
            imageSize = size;
            return decoded.Text;
        }
    }
}
