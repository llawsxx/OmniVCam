using System;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AboControls.ExtendedControls
{
    [Flags]
    public enum SmartLocking { None, Left, Middle, Right, All = Left | Middle | Right };

    [DefaultEvent("ValueChanged")]
    public class Slider : Control
    {
        private Bitmap _knobBitmap, _knobRolloverBitmap, _disabledImage;
        private BufferedGraphics _bufGraphics;
        private readonly BufferedGraphicsContext _bufContext = BufferedGraphicsManager.Current;
        private Rectangle _knobRect;
        private int _lastX;
        private float _value;
        private bool _isMouseOverSlider;
        private bool _draggingKnob; // Denotes whether we are dragging over the knob

        [Description("Occurs when the scroll percentage is changed")]
        public event EventHandler ValueChanged;

        public Slider()
        {
            this.SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            AllowKeyNavigation = true;
            RetainPercent = true;
            UpdateGraphicsBuffer();
            SetDefaultBitmaps();
        }

        #region Overrides
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (AllowKeyNavigation)
            {
                switch (e.KeyCode)
                {
                    case Keys.D1: Value = 0.1f; break;
                    case Keys.D2: Value = 0.2f; break;
                    case Keys.D3: Value = 0.3f; break;
                    case Keys.D4: Value = 0.4f; break;
                    case Keys.D5: Value = 0.5f; break;
                    case Keys.D6: Value = 0.6f; break;
                    case Keys.D7: Value = 0.7f; break;
                    case Keys.D8: Value = 0.8f; break;
                    case Keys.D9: Value = 0.9f; break;
                    case Keys.D0: Value = 0.0f; break;
                    case Keys.Oemplus: Value += 0.1f; break;
                    case Keys.OemMinus: Value -= 0.1f; break;
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);

            if (AllowQuickTracking && !_draggingKnob)
            {
                SliderX = e.X - _knobBitmap.Width / 2;
                this.Invalidate(); // Need to invalidate everything here
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_isMouseOverSlider)
            {
                _isMouseOverSlider = false;
                this.Invalidate(_knobRect);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (SmartLockAmount > 0)
            {
                int wholePercent = (int)((float)_knobRect.Left / (this.Width - _knobRect.Width) * 100 + 0.5);

                // If around 0%
                if (SmartLockingFlags.HasFlag(SmartLocking.Left) && wholePercent < SmartLockAmount)
                {
                    Value = 0;
                }
                // If around 50%
                else if (SmartLockingFlags.HasFlag(SmartLocking.Middle) && Math.Abs(wholePercent - 50) < SmartLockAmount)
                {
                    Value = 0.5f;
                }
                // If around 100%
                else if (SmartLockingFlags.HasFlag(SmartLocking.Right) && 100 - wholePercent < SmartLockAmount)
                {
                    Value = 1f;
                }
            }

            _draggingKnob = false;
            this.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (UseHandCursorForKnob)
            {
                if (_knobRect.Contains(e.Location))
                {
                    if (!this.Cursor.Equals(Cursors.Hand))
                        this.Cursor = Cursors.Hand;
                }
                else if (!this.Cursor.Equals(Cursors.Hand))
                {
                    this.Cursor = Cursors.Default;
                }
            }

            bool maxedLeft = e.X < 0 && _value == 0f;
            bool maxedRight = e.X > this.Width && _value == 1f;

            // If left mouse button pressed and cursor x is within slider bounds
            if (_draggingKnob && !(maxedLeft || maxedRight))
            {
                SliderX += e.X - _lastX;
                _lastX = e.X;
                this.Invalidate(_knobRect);
            }

            // Only update rollover effects if a change has occured
            if (!_isMouseOverSlider && _knobRect.Contains(e.Location))
            {
                _isMouseOverSlider = true;
                this.Invalidate(_knobRect);
            }
            else if (_isMouseOverSlider && !_knobRect.Contains(e.Location))
            {
                _isMouseOverSlider = false;
                this.Invalidate(_knobRect);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _lastX = e.X;
            _draggingKnob = (_knobRect.Contains(e.Location) && e.Button == MouseButtons.Left);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);

            // Retain slider percent position on sizing
            if (RetainPercent)
            {
                _knobRect.X = (int)((this.ClientSize.Width - _knobRect.Width) * _value + 0.5);
            }
            else // Reassign to SliderX to retain bounds
            {
                SliderX = _knobRect.X;
            }

            UpdateGraphicsBuffer();

            if (_knobBitmap != null)
            {
                _knobRect.Height = _knobBitmap.Height;
                this.Height = _knobBitmap.Height;
            }

            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_bufGraphics == null) UpdateGraphicsBuffer();
            if (_bufGraphics == null) return;

            _bufGraphics.Graphics.Clear(this.BackColor);
            DrawBar(_bufGraphics.Graphics);

            if (_isMouseOverSlider || _draggingKnob)
                _bufGraphics.Graphics.DrawImage(_knobRolloverBitmap, _knobRect);
            else if (!this.Enabled && _disabledImage != null)
                _bufGraphics.Graphics.DrawImage(_disabledImage, _knobRect);
            else
                _bufGraphics.Graphics.DrawImage(_knobBitmap, _knobRect);

            _bufGraphics.Render(e.Graphics);
        }
        #endregion

        /// <summary>
        /// Provides default bitmaps for the rollover and default slider images
        /// </summary>
        private void SetDefaultBitmaps()
        {
            KnobImage = new Bitmap(10, 20);

            using (Graphics graphics = Graphics.FromImage(_knobBitmap))
            {
                using (Brush brush = new SolidBrush(Color.FromArgb(0, 120, 215)))
                {
                    graphics.FillRectangle(brush, 0, 0, _knobBitmap.Width, _knobBitmap.Height);
                }
            }

            _knobRolloverBitmap = new Bitmap(10, 20);

            using (Graphics graphics = Graphics.FromImage(_knobRolloverBitmap))
            {
                using (Brush brush = new SolidBrush(Color.FromArgb(0, 102, 184)))
                {
                    graphics.FillRectangle(brush, 0, 0, _knobRolloverBitmap.Width, _knobRolloverBitmap.Height);
                }
            }

            _disabledImage = new Bitmap(10, 20);
            using (Graphics graphics = Graphics.FromImage(_disabledImage))
            using (Brush brush = new SolidBrush(Color.FromArgb(170, 170, 170)))
            {
                graphics.FillRectangle(brush, 0, 0, _disabledImage.Width, _disabledImage.Height);
            }
        }

        private void UpdateGraphicsBuffer()
        {
            if (_bufGraphics != null)
            {
                _bufGraphics.Dispose();
                _bufGraphics = null;
            }

            if (this.Width > 0 && this.Height > 0)
            {
                _bufContext.MaximumBuffer = new Size(this.Size.Width + 1, this.Size.Height + 1);
                _bufGraphics = _bufContext.Allocate(this.CreateGraphics(), this.ClientRectangle);
            }
        }

        /// <summary>
        /// Draws the back bar or any graphics, behind the knob
        /// </summary>
        protected virtual void DrawBar(Graphics graphics)
        {
            int barHeight = 6;
            int barY = this.Height / 2 - barHeight / 2;
            Rectangle rect = new Rectangle(_knobRect.Width / 2, barY, this.Width - _knobRect.Width, barHeight);
            using (Brush brush = new SolidBrush(Color.FromArgb(238, 238, 238)))
            using (Pen border = new Pen(Color.FromArgb(205, 205, 205)))
            {
                graphics.FillRectangle(brush, rect);
                graphics.DrawRectangle(border, rect.X, rect.Y, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bufGraphics?.Dispose();
                _knobBitmap?.Dispose();
                _knobRolloverBitmap?.Dispose();
                _disabledImage?.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Properties
        [Category("Appearance")]
        [DefaultValue(null)]
        [Description("The image to be displayed when this control is disabled")]
        public Bitmap DisabledImage
        {
            get { return _disabledImage; }
            set
            {
                _disabledImage = value;
                this.Invalidate(_knobRect);
            }
        }

        [Category("Appearance")]
        [DefaultValue(null)]
        [Description("The image to be displayed when a users mouse leaves the slider")]
        public Bitmap KnobImage
        {
            get { return _knobBitmap; }
            set
            {
                _knobBitmap = value;

                if (_knobBitmap != null)
                {
                    _knobRect.Size = new Size(_knobBitmap.Width, _knobBitmap.Height);
                    this.Height = _knobBitmap.Height;
                }

                this.Invalidate(_knobRect);
            }
        }

        [Category("Appearance")]
        [DefaultValue(null)]
        [Description("The image to be displayed when a user rolls the mouse over the slider")]
        public Bitmap KnobRolloverImage
        {
            get { return _knobRolloverBitmap; }
            set
            {
                _knobRolloverBitmap = value;
                this.Invalidate(_knobRect);
            }
        }

        private int SliderX
        {
            get { return _knobRect.X; }
            set
            {
                if (value < 0) value = 0;
                else if (value > this.Width - _knobRect.Width) value = this.Width - _knobRect.Width;

                _knobRect.X = value;
                int range = this.ClientSize.Width - _knobRect.Width;
                Value = range > 0 ? (float)value / range : 0f;
            }
        }

        /// <summary>
        /// Gets or sets the position of the knob as a percentage of the control
        /// </summary>
        [Category("Data")]
        [DefaultValue(0)]
        [Description("The current value as a percentage from 0-1")]
        public float Value
        {
            get { return _value; }
            set
            {
                if (value != _value)
                {
                    if (value > 1) value = 1;
                    if (value < 0) value = 0;
                    _value = value;
                    _knobRect.X = (int)(_value * (this.Width - _knobRect.Width));
                    this.Invalidate();

                    if (ValueChanged != null)
                        ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        [Category("Behavior")]
        [DefaultValue(0)]
        [Description("Determines the range in percentage to snap at the specified locking points")]
        public int SmartLockAmount { get; set; }

        [Category("Behavior")]
        [DefaultValue(SmartLocking.None)]
        [Description("Determines the snapping behavior of the tracker")]
        public SmartLocking SmartLockingFlags { get; set; }

        [Category("Behavior")]
        [DefaultValue(false)]
        [Description("Determines whether to allow the user to set the knob's position by clicking anywhere on the control")]
        public bool AllowQuickTracking { get; set; }

        [Category("Appearance")]
        [DefaultValue(false)]
        [Description("Determines whether to use the hand cursor when the mouse is over the knob")]
        public bool UseHandCursorForKnob { get; set; }

        [Category("Behavior")]
        [DefaultValue(true)]
        [Description("Determines whether to retain percent value when resizing")]
        public bool RetainPercent { get; set; }

        [Category("Behavior")]
        [DefaultValue(true)]
        [Description("When true, allows the user to slider the knob using 0-9 and -+")]
        public bool AllowKeyNavigation { get; set; }
        #endregion
    }
}
