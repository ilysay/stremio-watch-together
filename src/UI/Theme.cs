namespace StremioWT
{
    // Couleurs de Stremio (fond presque noir, violet #7b5bf5), comme le panneau Watch Together installé dans Stremio.
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(13, 12, 20);
        public static readonly Color Card = Color.FromArgb(24, 22, 36);
        public static readonly Color CardBorder = Color.FromArgb(40, 37, 58);
        public static readonly Color Input = Color.FromArgb(33, 31, 48);
        public static readonly Color InputHover = Color.FromArgb(45, 42, 64);
        public static readonly Color Accent = Color.FromArgb(123, 91, 245);
        public static readonly Color AccentHover = Color.FromArgb(142, 114, 255);
        public static readonly Color Text = Color.FromArgb(236, 235, 242);
        public static readonly Color Muted = Color.FromArgb(150, 146, 172);
        public static readonly Color Ok = Color.FromArgb(52, 199, 118);
        public static readonly Color Bad = Color.FromArgb(255, 107, 107);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Font Body = new Font("Segoe UI", 10f);
        public static readonly Font Small = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 10f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 17f);
        public static readonly Font Section = new Font("Segoe UI Semibold", 11f);

        public static Image Logo() => Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"));
        public static Image Histo() => Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.png"));
        public static Icon HistoIcon() => new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.ico"));
        public static Icon AppIcon() => Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        // Icônes de Windows (police Segoe Fluent Icons sur Windows 11, Segoe MDL2 Assets sur Windows 10) : nettes à toute taille.
        static readonly string IconFamily = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
            .FirstOrDefault(f => { using (var font = new Font(f, 10f)) return font.Name == f; });
        static readonly Dictionary<float, Font> iconFonts = new Dictionary<float, Font>();
        public static Font IconFont(float size)
        {
            if (IconFamily == null) return null;
            if (!iconFonts.TryGetValue(size, out var f)) iconFonts[size] = f = new Font(IconFamily, size);
            return f;
        }
        public static void DrawIcon(Graphics g, string glyph, Rectangle r, Color color, float size = 12f)
        {
            var font = IconFont(size);
            if (font == null || string.IsNullOrEmpty(glyph)) return;
            TextRenderer.DrawText(g, glyph, font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Fond des fenêtres : halo violet en haut, calculé pixel par pixel avec un léger bruit pour éviter les paliers
        // visibles d'un dégradé GDI+. Les cartes posées dessus sont translucides (voir Card).
        static readonly Dictionary<Size, Bitmap> backdrops = new Dictionary<Size, Bitmap>();
        public static Bitmap Backdrop(Size size)
        {
            if (backdrops.TryGetValue(size, out var cached)) return cached;
            int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            var px = new int[w * h];
            var noise = new Random(7);
            double cx = w / 2.0, cy = -70, rx = Math.Max(380, w * 0.8), ry = 240;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2));
                    double t = d >= 1 ? 0 : 1 - d * d * (3 - 2 * d);
                    // Nul tout en haut : le fond rejoint sans démarcation la barre de titre, de la même couleur (voir DarkTitleBar).
                    double top = Math.Min(1, y / 70.0), k = 0.30 * t * t * top * top * (3 - 2 * top), n = noise.NextDouble() - 0.5;
                    int r = (int)(Background.R + (Accent.R - Background.R) * k + n);
                    int g = (int)(Background.G + (Accent.G - Background.G) * k + n);
                    int b = (int)(Background.B + (Accent.B - Background.B) * k + n);
                    px[y * w + x] = (255 << 24) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
                }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return backdrops[size] = bmp;
        }
        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        // Remplissage translucide des cartes : sur le fond uni il donne exactement Card, et laisse passer le halo.
        public static readonly Color CardGlass = Color.FromArgb(128,
            Clamp(2 * Card.R - Background.R), Clamp(2 * Card.G - Background.G), Clamp(2 * Card.B - Background.B));

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Codes des icônes Segoe (identiques dans Fluent Icons et MDL2 Assets).
    static class Glyph
    {
        public const string User = "", People = "", Play = "", Join = "", Link = "", Copy = "",
            Chat = "", Leave = "", Settings = "", Send = "", Emoji = "", Wifi = "",
            Shield = "", Bug = "", Download = "", Globe = "", Code = "";
    }
}
