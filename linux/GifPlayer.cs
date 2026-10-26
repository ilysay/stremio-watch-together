using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace StremioWT
{
    // Avalonia n'anime pas les GIF : on décode chaque image avec SkiaSharp (déjà fourni par Avalonia) et on les fait défiler.
    // Mémoire bornée : images réduites à 200 px de large, 80 images au plus par GIF, et seuls les 8 derniers GIF du chat
    // bougent ; les plus anciens se figent sur leur première image.
    sealed class GifPlayer
    {
        const int MaxWidth = 200, MaxFrames = 80, MaxPlaying = 8;
        static readonly List<GifPlayer> playing = new List<GifPlayer>();

        readonly Image target;
        readonly Bitmap[] frames;
        readonly int[] delays;
        readonly DispatcherTimer timer = new DispatcherTimer();
        int index;

        GifPlayer(Image target, Bitmap[] frames, int[] delays)
        {
            this.target = target; this.frames = frames; this.delays = delays;
            timer.Interval = TimeSpan.FromMilliseconds(delays[0]);
            timer.Tick += (s, e) =>
            {
                index = (index + 1) % frames.Length;
                target.Source = frames[index];
                timer.Interval = TimeSpan.FromMilliseconds(delays[index]);
            };
        }

        // Hors du fil de l'interface. Renvoie null si ce n'est pas un GIF animé lisible (on affiche alors l'image fixe).
        internal static (Bitmap[] Frames, int[] Delays)? Decode(byte[] bytes)
        {
            using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
            {
                if (codec == null || codec.FrameCount < 2) return null;
                var full = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                double scale = Math.Min(1.0, (double)MaxWidth / full.Width);
                var small = new SKImageInfo(Math.Max(1, (int)(full.Width * scale)), Math.Max(1, (int)(full.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
                int count = Math.Min(codec.FrameCount, MaxFrames);
                var canvases = new SKBitmap[count];
                var frames = new Bitmap[count];
                var delays = new int[count];
                var info = codec.FrameInfo;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        // Une image GIF ne contient souvent que ce qui change : on part de l'image dont elle dépend.
                        var canvas = new SKBitmap(full);
                        int prior = info[i].RequiredFrame;
                        if (prior >= 0 && prior < i) canvases[prior].CopyTo(canvas); else canvas.Erase(SKColors.Transparent);
                        codec.GetPixels(full, canvas.GetPixels(), new SKCodecOptions(i, prior >= 0 && prior < i ? prior : -1));
                        canvases[i] = canvas;
                        // Même règle que les navigateurs : une durée de 0 ou 10 ms se joue à 100 ms.
                        delays[i] = info[i].Duration <= 10 ? 100 : info[i].Duration;
                        if (scale < 1)
                            using (var resized = canvas.Resize(small, SKFilterQuality.Medium)) frames[i] = ToAvalonia(resized);
                        else frames[i] = ToAvalonia(canvas);
                    }
                }
                finally { foreach (var c in canvases) c?.Dispose(); }
                return (frames, delays);
            }
        }

        static Bitmap ToAvalonia(SKBitmap source)
        {
            var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var pixels = source.Bytes;
            using (var buffer = bitmap.Lock())
                for (int y = 0; y < source.Height; y++)
                    Marshal.Copy(pixels, y * source.RowBytes, buffer.Address + y * buffer.RowBytes, source.Width * 4);
            return bitmap;
        }

        // Sur le fil de l'interface.
        internal static void Play(Image target, Bitmap[] frames, int[] delays)
        {
            target.Source = frames[0];
            var player = new GifPlayer(target, frames, delays);
            playing.Add(player);
            if (playing.Count > MaxPlaying) { playing[0].Freeze(); playing.RemoveAt(0); }
            target.DetachedFromVisualTree += (s, e) => { player.timer.Stop(); playing.Remove(player); };
            player.timer.Start();
        }

        // Garde la première image et libère les autres.
        void Freeze()
        {
            timer.Stop();
            target.Source = frames[0];
            for (int i = 1; i < frames.Length; i++) frames[i].Dispose();
        }
    }
}
