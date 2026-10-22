namespace StremioWT
{
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(6, 19, 45);
        public static readonly Color Card = Color.FromArgb(12, 32, 72);
        public static readonly Color CardBorder = Color.FromArgb(30, 62, 130);
        public static readonly Color Input = Color.FromArgb(8, 24, 56);
        public static readonly Color Accent = Color.FromArgb(47, 128, 255);
        public static readonly Color AccentHover = Color.FromArgb(84, 152, 255);
        public static readonly Color Text = Color.FromArgb(232, 240, 255);
        public static readonly Color Muted = Color.FromArgb(140, 160, 200);
        public static readonly Color Ok = Color.FromArgb(61, 220, 132);
        public static readonly Color Bad = Color.FromArgb(255, 99, 99);
        public static readonly Font Body = new Font("Segoe UI", 10f);
        public static readonly Font Small = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 17f);
        public static readonly Font Section = new Font("Segoe UI Semibold", 11f);

        public static Image Logo() => Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"));
        public static Image Histo() => Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.png"));
        public static Icon HistoIcon() => new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.ico"));
        public static Icon AppIcon() => Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
