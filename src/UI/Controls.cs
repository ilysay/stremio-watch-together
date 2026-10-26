namespace StremioWT
{
    // Carte arrondie avec un titre et son icône (remplace les GroupBox, impossibles à thémer).
    class Card : Panel
    {
        public string Title, Icon;
        public Card(string title, string icon = null)
        {
            Title = title; Icon = icon; DoubleBuffered = true; Padding = new Padding(16, 40, 16, 14);
            // Coins transparents et remplissage translucide : le fond de la fenêtre (et son halo) se voit autour et à travers.
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 16))
            using (var fill = new SolidBrush(Theme.CardGlass))
            using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255)))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            if (string.IsNullOrEmpty(Title)) return;
            int x = 16;
            if (Icon != null && Theme.IconFont(11f) != null)
            {
                Theme.DrawIcon(g, Icon, new Rectangle(14, 10, 22, 24), Theme.Accent, 11f);
                x = 40;
            }
            TextRenderer.DrawText(g, Title, Theme.Section, new Point(x, 12), Theme.Text);
        }
    }

    // Bouton en pilule, comme dans Stremio : violet plein (principal), gris discret (secondaire) ou contour rouge (Danger).
    class FlatButton : Control
    {
        public bool Primary, Danger;
        public Image Picture;
        public string Icon;
        bool hover, down;
        public FlatButton(string text, bool primary, string icon = null)
        {
            Text = text; Primary = primary; Icon = icon;
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
            bool lit = hover && !down && Enabled;
            Color fill, fore;
            if (Danger) { fill = lit ? Color.FromArgb(50, Theme.Danger) : Color.Transparent; fore = Enabled ? Color.FromArgb(255, 138, 138) : Theme.Muted; }
            else if (Primary) { fill = !Enabled ? Color.FromArgb(62, 52, 104) : lit ? Theme.AccentHover : Theme.Accent; fore = Enabled ? Color.White : Color.FromArgb(170, 160, 210); }
            else { fill = lit ? Theme.InputHover : Theme.Input; fore = Enabled ? Theme.Text : Theme.Muted; }
            using (var path = Theme.Rounded(r, Height / 2))
            {
                if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (Danger) using (var pen = new Pen(Color.FromArgb(120, Theme.Danger), 1.2f)) g.DrawPath(pen, path);
            }
            int textWidth = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (Picture != null || (Icon != null && Theme.IconFont(12f) != null))
            {
                int icon = Picture != null ? Height - 14 : 18, gap = textWidth > 0 ? 8 : 0;
                int x = (Width - icon - gap - textWidth) / 2;
                var box = new Rectangle(x, (Height - icon) / 2, icon, icon);
                if (Picture != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(Picture, box); }
                else Theme.DrawIcon(g, Icon, new Rectangle(x - 2, 0, icon + 4, Height), fore, 11.5f);
                if (textWidth > 0)
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, textWidth + 6, Height), fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            TextRenderer.DrawText(g, Text, Font, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Interrupteur on/off avec libellé (la case à cocher Windows ne montre pas son état en thème sombre).
    class ToggleSwitch : Control
    {
        bool isChecked;
        public string Icon;
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
            var track = new Rectangle(0, (Height - 22) / 2, 40, 22);
            var on = isChecked ? (Enabled ? Theme.Accent : Color.FromArgb(80, 66, 140)) : Color.FromArgb(58, 55, 76);
            using (var path = Theme.Rounded(track, 11))
            using (var b = new SolidBrush(on))
                g.FillPath(b, path);
            int knob = 16, x = isChecked ? track.Right - knob - 3 : track.X + 3;
            using (var b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(190, 190, 200))) g.FillEllipse(b, x, track.Y + 3, knob, knob);
            int left = 52;
            if (Icon != null && Theme.IconFont(11f) != null)
            {
                Theme.DrawIcon(g, Icon, new Rectangle(left - 2, 0, 22, Height), Theme.Muted, 11f);
                left += 26;
            }
            var fore = Enabled ? ForeColor : Theme.Muted;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Width - left, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.WordBreak);
        }
    }

    // Zone de saisie sombre et arrondie, avec une icône à gauche (le TextBox Windows ne se colore pas bien seul).
    class InputBox : Panel
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body };
        string icon;
        public string Icon { get => icon; set { icon = value; Padding = new Padding(value != null && Theme.IconFont(11f) != null ? 38 : 12, 9, 12, 6); Invalidate(); } }
        public InputBox()
        {
            DoubleBuffered = true; BackColor = Theme.Card; Height = 36; Padding = new Padding(12, 9, 12, 6);
            Box.Dock = DockStyle.Fill;
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            using (var b = new SolidBrush(Box.BackColor))
            using (var pen = new Pen(Box.Focused ? Theme.Accent : Theme.CardBorder, Box.Focused ? 1.4f : 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
            if (icon != null) Theme.DrawIcon(g, icon, new Rectangle(8, 0, 26, Height), Box.Focused ? Theme.Accent : Theme.Muted, 11f);
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
