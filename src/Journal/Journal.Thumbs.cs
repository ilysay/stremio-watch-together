namespace StremioWT
{
    // Vignettes des affiches pour le journal (Windows Forms : System.Drawing).
    static partial class Journal
    {
        public static event Action ThumbReady;

        static readonly Dictionary<string, System.Drawing.Image> thumbs = new Dictionary<string, System.Drawing.Image>();
        static readonly HashSet<string> loading = new HashSet<string>();

        public static System.Drawing.Image Thumb(string imdbId)
        {
            lock (thumbs)
            {
                if (thumbs.TryGetValue(imdbId, out var img)) return img;
                if (!loading.Add(imdbId)) return null;
            }
            _ = Task.Run(async () =>
            {
                System.Drawing.Image img = null;
                var cached = Path.Combine(PosterDir, imdbId + ".jpg");
                try
                {
                    if (System.IO.File.Exists(cached)) img = System.Drawing.Image.FromStream(new MemoryStream(System.IO.File.ReadAllBytes(cached)));
                    else
                    {
                        // live.metahub.space sert du JPEG (images.metahub.space renvoie du WebP, illisible ici)
                        var bytes = await Program.Http.GetByteArrayAsync($"https://live.metahub.space/poster/small/{imdbId}/img");
                        using (var full = System.Drawing.Image.FromStream(new MemoryStream(bytes)))
                        {
                            var small = new System.Drawing.Bitmap(92, 138);
                            using (var g = System.Drawing.Graphics.FromImage(small))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(full, 0, 0, 92, 138);
                            }
                            Directory.CreateDirectory(PosterDir);
                            small.Save(cached, System.Drawing.Imaging.ImageFormat.Jpeg);
                            img = small;
                        }
                    }
                }
                catch { }
                lock (thumbs) { thumbs[imdbId] = img; loading.Remove(imdbId); }
                if (img != null) ThumbReady?.Invoke();
            });
            return null;
        }
    }
}
