namespace StremioWT
{
    // Carte arrondie avec un titre (remplace les GroupBox, impossibles à thémer).
    class Card : Panel
    {
        public string Title;
        public Card(string title) { Title = title; DoubleBuffered = true; BackColor = Theme.Background; Padding = new Padding(16, 40, 16, 14); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 12))
            using (var fill = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (!string.IsNullOrEmpty(Title))
                TextRenderer.DrawText(g, Title, Theme.Section, new Point(16, 12), Theme.Text);
        }
    }

    class FlatButton : Control
    {
        public bool Primary;
        public Image Picture;
        bool hover, down;
        public FlatButton(string text, bool primary)
        {
            Text = text; Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.Bold;
            Height = 38;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            var accent = !Enabled ? Theme.CardBorder : (hover && !down ? Theme.AccentHover : Theme.Accent);
            using (var path = Theme.Rounded(r, 8))
            {
                if (Primary) using (var b = new SolidBrush(accent)) g.FillPath(b, path);
                else using (var b = new SolidBrush(hover ? Color.FromArgb(30, accent) : Theme.Input)) g.FillPath(b, path);
                using (var pen = new Pen(accent, 1.4f)) g.DrawPath(pen, path);
            }
            var fore = Primary ? Color.White : (Enabled ? Theme.Text : Theme.Muted);
            if (Picture != null)
            {
                int icon = Height - 12, gap = 8, textWidth = TextRenderer.MeasureText(Text, Font).Width;
                int x = (Width - icon - gap - textWidth) / 2;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Picture, new Rectangle(x, (Height - icon) / 2, icon, icon));
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, textWidth + 4, Height), fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }
            TextRenderer.DrawText(g, Text, Font, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Interrupteur on/off avec libellé (la case à cocher Windows ne montre pas son état en thème sombre).
    class ToggleSwitch : Control
    {
        bool isChecked;
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get => isChecked;
            set { if (isChecked == value) return; isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }
        public ToggleSwitch(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            Height = 24;
        }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, (Height - 20) / 2, 38, 20);
            using (var path = Theme.Rounded(track, 10))
            using (var b = new SolidBrush(isChecked ? Theme.Accent : Theme.Input))
            using (var pen = new Pen(isChecked ? Theme.Accent : Theme.CardBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            int knob = 14, x = isChecked ? track.Right - knob - 3 : track.X + 3;
            using (var b = new SolidBrush(isChecked ? Color.White : Theme.Muted)) g.FillEllipse(b, x, track.Y + 3, knob, knob);
            var fore = Enabled ? ForeColor : Theme.Muted;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(48, 0, Width - 48, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    // Zone de saisie sombre avec bordure arrondie (le TextBox Windows ne se colore pas bien seul).
    class InputBox : Panel
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body };
        public InputBox()
        {
            DoubleBuffered = true; BackColor = Theme.Card; Height = 36; Padding = new Padding(10, 8, 10, 6);
            Box.Dock = DockStyle.Fill;
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            using (var b = new SolidBrush(Theme.Input))
            using (var pen = new Pen(Box.Focused ? Theme.Accent : Theme.CardBorder))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
    }

    class StarRating : Control
    {
        double value, hover = -1;
        public event EventHandler ValueChanged;
        public double Value { get => value; set { this.value = value; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
        const int Star = 34;

        public StarRating()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(Star * 5 + 8, Star + 4);
            Cursor = Cursors.Hand;
        }

        double At(int x) => Math.Max(0.5, Math.Min(5, Math.Ceiling(x / (Star / 2.0)) / 2));
        protected override void OnMouseMove(MouseEventArgs e) { hover = At(e.X); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { var v = At(e.X); Value = v == value ? 0 : v; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double shown = hover >= 0 ? hover : value;
            for (int i = 0; i < 5; i++)
            {
                using (var path = StarPath(new RectangleF(i * Star + 2, 2, Star - 4, Star - 4)))
                {
                    using (var empty = new SolidBrush(Theme.Input)) g.FillPath(empty, path);
                    double fill = Math.Max(0, Math.Min(1, shown - i));
                    if (fill > 0)
                    {
                        var clip = g.Clip;
                        g.SetClip(new RectangleF(i * Star, 0, (float)(Star * fill), Star));
                        using (var full = new SolidBrush(Color.FromArgb(0, 224, 84))) g.FillPath(full, path);
                        g.Clip = clip;
                    }
                    using (var pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, path);
                }
            }
        }

        static GraphicsPath StarPath(RectangleF r)
        {
            var pts = new PointF[10];
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + 1, outer = r.Width / 2, inner = outer * 0.45f;
            for (int k = 0; k < 10; k++)
            {
                double a = Math.PI / 5 * k - Math.PI / 2;
                float rad = k % 2 == 0 ? outer : inner;
                pts[k] = new PointF(cx + (float)(Math.Cos(a) * rad), cy + (float)(Math.Sin(a) * rad));
            }
            var p = new GraphicsPath();
            p.AddPolygon(pts);
            return p;
        }
    }
}
