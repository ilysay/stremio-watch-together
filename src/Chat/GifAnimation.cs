using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace StremioWT
{
    // GIF animés de la fenêtre, sans faire ramer l'interface. WinForms anime un GIF en redécodant chaque image en taille
    // réelle sur le fil de l'interface : avec quelques GIF dans le chat, la fenêtre saccadait pendant les épisodes.
    // Ici chaque GIF est décodé une seule fois en arrière-plan, réduit à la taille affichée (80 images au plus), puis un
    // seul minuteur partagé fait défiler les images. Seuls les 4 derniers GIF du chat bougent (les plus anciens se figent),
    // et rien ne bouge quand la fenêtre est réduite.
    sealed class GifAnimation
    {
        const int MaxFrames = 80, MaxPlayingInChat = 4;
        static readonly List<GifAnimation> all = new List<GifAnimation>();
        static readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 20 };

        readonly PictureBox box;
        readonly Bitmap[] frames;
        readonly int[] delays;
        readonly bool inChat;
        int index;
        DateTime next;
        bool frozen;

        static GifAnimation() { clock.Tick += (s, e) => Tick(); }

        GifAnimation(PictureBox box, Bitmap[] frames, int[] delays, bool inChat)
        {
            this.box = box; this.frames = frames; this.delays = delays; this.inChat = inChat;
            next = DateTime.Now.AddMilliseconds(delays[0]);
        }

        // Télécharge et affiche un GIF dans la case. inChat : compte parmi les GIF du chat (seuls les derniers bougent).
        public static async void Load(PictureBox box, string url, bool inChat)
        {
            if (!Chat.IsAllowedGif(url)) return;
            try
            {
                var bytes = await Download(url);
                var size = box.Size;
                var decoded = await Task.Run(() => Decode(bytes, size));
                if (box.IsDisposed || decoded == null) return;
                var (frames, delays) = decoded.Value;
                box.Size = frames[0].Size;
                box.SizeMode = PictureBoxSizeMode.Normal;
                box.Image = frames[0];
                if (frames.Length < 2) return;
                var anim = new GifAnimation(box, frames, delays, inChat);
                box.Disposed += (s, e) => anim.Release();
                all.Add(anim);
                if (inChat)
                {
                    var playing = all.Where(a => a.inChat && !a.frozen).ToList();
                    foreach (var old in playing.Take(Math.Max(0, playing.Count - MaxPlayingInChat))) old.Freeze();
                }
                clock.Start();
            }
            catch { }
        }

        // Giphy propose une version de 200 px de large : bien plus légère que l'original pour une vignette.
        static async Task<byte[]> Download(string url)
        {
            var small = Regex.Replace(url, @"^(https://(?:media\d?|i)\.giphy\.com/media/[^/]+)/giphy\.gif$", "$1/200w.gif", RegexOptions.IgnoreCase);
            if (small != url)
                try { return await Program.Http.GetByteArrayAsync(small); } catch { }
            return await Program.Http.GetByteArrayAsync(url);
        }

        // Hors du fil de l'interface : chaque image est rendue une fois, à la taille de la case (proportions gardées).
        static (Bitmap[], int[])? Decode(byte[] bytes, Size boxSize)
        {
            using (var img = Image.FromStream(new MemoryStream(bytes)))
            {
                if (img.Width <= 0 || img.Height <= 0) return null;
                double ratio = Math.Min(1.0, Math.Min(boxSize.Width / (double)img.Width, boxSize.Height / (double)img.Height));
                var size = new Size(Math.Max(40, (int)(img.Width * ratio)), Math.Max(30, (int)(img.Height * ratio)));
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                int count = Math.Min(MaxFrames, Math.Max(1, img.GetFrameCount(dim)));
                byte[] times = null;
                try { times = img.GetPropertyItem(0x5100).Value; } catch { }
                var frames = new Bitmap[count];
                var delays = new int[count];
                for (int i = 0; i < count; i++)
                {
                    img.SelectActiveFrame(dim, i);
                    var f = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(f))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(Point.Empty, size));
                    }
                    frames[i] = f;
                    // Délai en centièmes de seconde ; 0 ou 1 veut dire « par défaut », comme dans les navigateurs (100 ms).
                    int cs = times != null && times.Length >= (i + 1) * 4 ? BitConverter.ToInt32(times, i * 4) : 10;
                    delays[i] = cs <= 1 ? 100 : cs * 10;
                }
                return (frames, delays);
            }
        }

        static void Tick()
        {
            var now = DateTime.Now;
            foreach (var a in all.ToList())
            {
                if (a.frozen || now < a.next) continue;
                var form = a.box.FindForm();
                if (form == null || form.WindowState == FormWindowState.Minimized || !form.Visible) { a.next = now.AddMilliseconds(250); continue; }
                a.index = (a.index + 1) % a.frames.Length;
                a.box.Image = a.frames[a.index];
                a.next = now.AddMilliseconds(a.delays[a.index]);
            }
            if (!all.Any(a => !a.frozen)) clock.Stop();
        }

        void Freeze()
        {
            if (frozen) return;
            frozen = true;
            if (!box.IsDisposed) box.Image = frames[0];
            // Les autres images ne serviront plus : on rend leur mémoire.
            for (int i = 1; i < frames.Length; i++) frames[i].Dispose();
        }

        void Release()
        {
            all.Remove(this);
            if (!frozen) for (int i = 1; i < frames.Length; i++) frames[i].Dispose();
            frames[0].Dispose();
            if (all.Count == 0) clock.Stop();
        }
    }
}
