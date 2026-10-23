using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using static StremioWT.MainWindow;

namespace StremioWT
{
    // « Mon journal » sous Linux (Avalonia) : mêmes fonctions que la version Windows (JournalForms.cs),
    // la logique (Journal.cs) est partagée.
    static class Posters
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        static readonly Color Green = Color.FromRgb(0, 224, 84);
        internal static IBrush StarBrush => Palette.Brush(Green);

        // Affiche mise en cache sur le disque (même dossier que la version Windows).
        internal static async void Load(Image target, string imdbId, string fallback = null)
        {
            if (string.IsNullOrEmpty(imdbId)) return;
            lock (cache) if (cache.TryGetValue(imdbId, out var known)) { target.Source = known; return; }
            Bitmap bitmap = null;
            var file = Path.Combine(Journal.PosterDir, imdbId + ".jpg");
            try
            {
                byte[] bytes;
                if (File.Exists(file)) bytes = File.ReadAllBytes(file);
                else
                {
                    bytes = null;
                    // live.metahub.space sert du JPEG ; images.metahub.space (champ Poster) en secours.
                    foreach (var url in new[] { $"https://live.metahub.space/poster/small/{imdbId}/img", fallback })
                    {
                        if (string.IsNullOrEmpty(url)) continue;
                        try { bytes = await Program.Http.GetByteArrayAsync(url); break; } catch { }
                    }
                    if (bytes == null) return;
                    Directory.CreateDirectory(Journal.PosterDir);
                    File.WriteAllBytes(file, bytes);
                }
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch { return; }
            lock (cache) cache[imdbId] = bitmap;
            Dispatcher.UIThread.Post(() => target.Source = bitmap);
        }

        internal static Image Histo(double size)
        {
            var img = new Image { Width = size, Height = size };
            try { img.Source = new Bitmap(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.png")); } catch { }
            return img;
        }
    }

    // Cinq étoiles cliquables, demi-étoiles comprises (clic sur la moitié gauche d'une étoile).
    class StarPicker : StackPanel
    {
        readonly TextBlock[] stars = new TextBlock[5];
        double value;
        public double Value { get => value; set { this.value = value; Paint(); } }

        public StarPicker()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 2;
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                var star = new TextBlock { Text = "★", FontSize = 30, Width = 32, TextAlignment = TextAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
                star.PointerPressed += (s, e) =>
                {
                    var half = e.GetPosition(star).X < star.Bounds.Width / 2;
                    var picked = index + (half ? 0.5 : 1);
                    Value = Math.Abs(picked - value) < 0.01 ? 0 : picked; // recliquer sur la même note l'efface
                };
                stars[i] = star;
                Children.Add(star);
            }
            Paint();
        }

        void Paint()
        {
            for (int i = 0; i < 5; i++)
            {
                bool full = value >= i + 1, half = !full && value >= i + 0.5;
                stars[i].Foreground = full || half ? Posters.StarBrush : Palette.Brush(Palette.Border);
                stars[i].Opacity = half ? 0.55 : 1;
            }
        }
    }

    // Noter un film / une série : à la fin d'un film, ou depuis le journal.
    class RatingWindow : Window
    {
        public RatingWindow(JournalEntry entry, Action<JournalEntry> saved)
        {
            Title = L.T(entry.IsMovie ? "Noter le film" : entry.Type == "anime" ? "Noter l'anime" : "Noter la série");
            Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var poster = new Image { Width = 92, Height = 138, Stretch = Stretch.UniformToFill };
            Posters.Load(poster, entry.ImdbId, entry.Poster);
            var info = new StackPanel { Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
            info.Children.Add(new TextBlock { Text = entry.Display, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            info.Children.Add(Muted((entry.IsMovie ? L.F("Vu le {0}", Diary.Date(entry.WatchedDate)) : $"{entry.Progress} · {Diary.Date(entry.WatchedDate)}")
                + (entry.With.Count > 0 ? L.T(" avec ") + string.Join(", ", entry.With) : "") + (entry.Rewatch ? L.T(" · revisionnage") : "")));
            var stars = new StarPicker { Value = entry.Rating };
            info.Children.Add(stars);
            info.Children.Add(Muted(L.T("Clique sur une étoile (demi-étoiles possibles)")));
            var top = new DockPanel();
            DockPanel.SetDock(poster, Dock.Left);
            top.Children.Add(poster);
            top.Children.Add(info);

            var review = new TextBox { Text = entry.Review, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
            var later = new Button { Content = L.T("Plus tard"), Padding = new Thickness(16, 7) };
            var save = new Button { Content = L.T("Enregistrer"), Classes = { "accent" }, Padding = new Thickness(16, 7) };
            later.Click += (s, e) => Close();
            save.Click += (s, e) =>
            {
                Journal.Update(entry, stars.Value, (review.Text ?? "").Trim());
                saved?.Invoke(entry);
                Close();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { later, save } };
            var panel = new StackPanel { Margin = new Thickness(22), Spacing = 10 };
            panel.Children.Add(top);
            panel.Children.Add(Muted(L.T("Critique (facultatif)")));
            panel.Children.Add(review);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }

    class Diary : Window
    {
        static readonly string[] TabTypes = { "movie", "series", "anime" };
        readonly Button[] tabs = new Button[3];
        readonly TextBlock count = Muted("");
        readonly ListBox list = new ListBox { Height = 360, Background = Palette.Brush(Palette.Card) };
        readonly TextBlock exportTitle = Heading("");
        readonly TextBlock exportHint = Muted("");
        readonly Button exportNew = Primary(""), exportAll = Secondary(""), openLetterboxd = Secondary(L.T("Ouvrir Letterboxd"));
        readonly StackPanel exportButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        readonly Action<JournalEntry> rated;
        int tab;
        bool fileReady;

        internal static string Date(string iso) => DateTime.TryParse(iso, out var d) ? d.ToString("dd'/'MM'/'yyyy") : iso;

        public Diary(Action<JournalEntry> rated)
        {
            this.rated = rated;
            Title = L.T("Mon journal");
            Width = 600; Height = 760; MinWidth = 520; MinHeight = 600;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(Posters.Histo(44));
            header.Children.Add(new TextBlock { Text = L.T("Mon journal"), FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Brush(Palette.Text) });
            var tabRow = new UniformGrid { Columns = 3 };
            for (int i = 0; i < 3; i++)
            {
                int t = i;
                tabs[i] = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, i < 2 ? 8 : 0, 0), Padding = new Thickness(8, 7) };
                tabs[i].Click += (s, e) => { tab = t; Reload(); };
                tabRow.Children.Add(tabs[i]);
            }
            list.DoubleTapped += (s, e) => Rate();

            var rate = Secondary(L.T("Noter / modifier"));
            var remove = Secondary(L.T("Supprimer"));
            var import = Secondary(L.T("Importer depuis Stremio"));
            rate.Click += (s, e) => Rate();
            remove.Click += async (s, e) => await Remove();
            import.Click += async (s, e) => await ImportFromStremio();
            var actions = new UniformGrid { Columns = 3 };
            foreach (var (b, last) in new[] { (rate, false), (remove, false), (import, true) }) { b.Margin = new Thickness(0, 0, last ? 0 : 8, 0); actions.Children.Add(b); }

            exportNew.Click += async (s, e) => await Export(all: false);
            exportAll.Click += async (s, e) => await Export(all: true);
            openLetterboxd.Click += (s, e) => OpenUrl("https://letterboxd.com/import/");
            exportButtons.Children.Add(exportNew); exportButtons.Children.Add(exportAll); exportButtons.Children.Add(openLetterboxd);
            exportNew.HorizontalAlignment = exportAll.HorizontalAlignment = openLetterboxd.HorizontalAlignment = HorizontalAlignment.Left;

            var page = new StackPanel { Margin = new Thickness(22, 18), Spacing = 12 };
            page.Children.Add(header);
            page.Children.Add(tabRow);
            page.Children.Add(count);
            page.Children.Add(new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderBrush = Palette.Brush(Palette.Border), BorderThickness = new Thickness(1), Child = list });
            page.Children.Add(actions);
            page.Children.Add(Card(exportTitle, exportHint, exportButtons));
            Content = new ScrollViewer { Content = page };

            Action changed = () => Dispatcher.UIThread.Post(Reload);
            Journal.Changed += changed;
            Closed += (s, e) => Journal.Changed -= changed;
            Reload();
        }

        void Reload()
        {
            var all = Journal.All();
            int CountOf(string type) => all.Count(e => e.Type == type);
            tabs[0].Content = L.F("Films ({0})", CountOf("movie"));
            tabs[1].Content = L.F("Séries ({0})", CountOf("series"));
            tabs[2].Content = L.F("Animes ({0})", CountOf("anime"));
            for (int i = 0; i < 3; i++)
            {
                if (i == tab) { if (!tabs[i].Classes.Contains("accent")) tabs[i].Classes.Add("accent"); }
                else tabs[i].Classes.Remove("accent");
            }

            var shown = all.Where(e => e.Type == TabTypes[tab]).ToList();
            list.ItemsSource = shown.Select(Row).ToList();

            int pending = Journal.PendingExport(), movies = Journal.MovieCount();
            if (tab == 0)
            {
                count.Text = shown.Count == 0
                    ? L.T("Aucun film pour l'instant : les films regardés jusqu'au bout (85 %) s'ajoutent tout seuls.")
                    : L.F("{0} film(s) · {1} pas encore envoyé(s) vers Letterboxd", shown.Count, pending);
                exportTitle.Text = L.T("Envoyer vers Letterboxd");
                if (!fileReady) exportHint.Text = L.T("Notés ou non. Les films déjà envoyés ne repartent pas : pas de doublons.");
                exportNew.Content = pending == 0 ? L.T("Rien de nouveau à envoyer") : pending == 1 ? L.T("Envoyer le nouveau film") : L.F("Envoyer les {0} nouveaux films", pending);
                exportNew.IsEnabled = pending > 0;
                exportAll.Content = L.F("Tout renvoyer ({0})", movies);
                exportAll.IsEnabled = movies > 0;
                exportButtons.IsVisible = true;
            }
            else
            {
                count.Text = shown.Count == 0
                    ? L.T(tab == 1 ? "Aucune série pour l'instant : les épisodes regardés jusqu'au bout s'ajoutent tout seuls." : "Aucun anime pour l'instant : les épisodes regardés jusqu'au bout s'ajoutent tout seuls.")
                    : L.F(tab == 1 ? "{0} série(s) · {1} terminée(s)" : "{0} anime(s) · {1} terminé(s)", shown.Count, shown.Count(e => e.Completed));
                exportTitle.Text = "Letterboxd";
                exportHint.Text = L.T("Letterboxd ne gère que les films : tes séries et animes restent dans ton journal.");
                exportButtons.IsVisible = false;
            }
        }

        // Une ligne du journal : affiche, titre, étoiles, puis date, avec qui, critique, envoi.
        static Control Row(JournalEntry entry)
        {
            var poster = new Image { Width = 42, Height = 63, Stretch = Stretch.UniformToFill };
            Posters.Load(poster, entry.ImdbId, entry.Poster);
            var stars = new TextBlock { Text = Journal.Stars(entry.Rating), FontWeight = FontWeight.SemiBold, Foreground = entry.Rating > 0 ? Posters.StarBrush : Palette.Brush(Palette.Muted), VerticalAlignment = VerticalAlignment.Top };
            var title = new TextBlock { Text = entry.Display, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text), TextTrimming = TextTrimming.CharacterEllipsis };
            var line2 = (entry.IsMovie ? "" : entry.Progress + " · ") + Date(entry.WatchedDate)
                + (entry.With.Count > 0 ? L.T(" · avec ") + string.Join(", ", entry.With) : "")
                + (entry.Rewatch ? L.T(" · revisionnage") : "") + (string.IsNullOrEmpty(entry.Review) ? "" : L.T(" · critique"))
                + (entry.IsMovie && entry.Exported ? L.T(" · ✓ envoyé") : "");
            var second = new TextBlock { Text = line2, FontSize = 12, Foreground = entry.Completed ? Posters.StarBrush : Palette.Brush(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis };
            var titleRow = new DockPanel();
            DockPanel.SetDock(stars, Dock.Right);
            titleRow.Children.Add(stars);
            titleRow.Children.Add(title);
            var texts = new StackPanel { Spacing = 6, Margin = new Thickness(12, 6, 4, 0), Children = { titleRow, second } };
            var row = new DockPanel { Tag = entry, Margin = new Thickness(2) };
            DockPanel.SetDock(poster, Dock.Left);
            row.Children.Add(poster);
            row.Children.Add(texts);
            return row;
        }

        JournalEntry Selected() => (list.SelectedItem as Control)?.Tag as JournalEntry;

        void Rate()
        {
            var entry = Selected();
            if (entry != null) _ = new RatingWindow(entry, rated).ShowDialog(this);
        }

        async Task Remove()
        {
            var entry = Selected();
            if (entry != null && await Dialogs.Confirm(this, L.F("Retirer {0} du journal ?", entry.Display))) Journal.Remove(entry);
        }

        async Task ImportFromStremio()
        {
            var session = Session.Current;
            if (session == null)
            {
                await Dialogs.Message(this, L.T("Pour lire ton historique, StremioWT doit être branché sur Stremio :\nhéberge ou rejoins un salon, puis reviens ici.\n\n(À l'ouverture d'un salon, l'import est aussi proposé automatiquement.)"));
                return;
            }
            try
            {
                var history = await session.ReadHistory();
                Journal.SyncSeries(history);
                var fresh = Journal.NotInJournal(history);
                if (fresh.Count == 0) { await Dialogs.Message(this, L.F("Ton historique Stremio contient {0} titre(s) vu(s), tous déjà dans ton journal.", history.Count)); return; }
                await AskImport(this, fresh);
            }
            catch (Exception e) { await Dialogs.Message(this, L.T("Lecture de l'historique impossible : ") + e.Message); }
        }

        // Même dialogue que sous Windows : ajouter les titres vus, puis (films) déjà sur Letterboxd ou non.
        internal static async Task AskImport(Window owner, List<StremioFilm> items)
        {
            int films = items.Count(f => f.Type == "movie"), series = items.Count - films;
            var what = string.Join(L.T(" et "), new[] { films > 0 ? L.F("{0} film(s)", films) : null, series > 0 ? L.F("{0} série(s) ou anime(s)", series) : null }.Where(s => s != null));
            var sample = string.Join("\n", items.Take(5).Select(f => "• " + f.Display)) + (items.Count > 5 ? L.F("\n… et {0} autre(s)", items.Count - 5) : "");
            if (!await Dialogs.Confirm(owner, L.F("J'ai trouvé {0} déjà vu(s) dans ton Stremio qui ne sont pas dans ton journal :\n\n{1}\n\nLes ajouter à ton journal ?", what, sample)))
            {
                Journal.MarkSeen(items);
                return;
            }
            bool onLetterboxd = films > 0 && await Dialogs.Confirm(owner,
                L.T("Ces films sont-ils déjà sur ton compte Letterboxd ?\n\nOui : ils restent dans ton journal mais ne seront pas exportés (pas de doublons).\nNon : ils partiront au prochain envoi vers Letterboxd.\n\n(Les séries ne vont pas sur Letterboxd, qui ne gère que les films.)"));
            int n = Journal.ImportHistory(items, onLetterboxd);
            Console.WriteLine($"{n} titre(s) de ton historique Stremio ajouté(s) au journal.");
        }

        async Task Export(bool all)
        {
            if (all)
            {
                int already = Journal.MovieCount() - Journal.PendingExport();
                if (already > 0 && !await Dialogs.Confirm(this, L.F("Le fichier contiendra tous tes films, y compris les {0} déjà envoyé(s).\n\nSi tu l'importes sur Letterboxd, ces films apparaîtront une deuxième fois dans ton journal Letterboxd.\n\nContinuer ?", already)))
                    return;
            }
            try
            {
                var (n, path) = Journal.ExportCsv(all);
                OpenUrl("https://letterboxd.com/import/");
                OpenUrl(Path.GetDirectoryName(path));
                exportHint.Foreground = Posters.StarBrush;
                exportHint.Text = L.F("Fichier prêt ({0} film(s)) : glisse-le sur la page Letterboxd, puis « Import ».", n) + "\n" + path;
                fileReady = true;
                Reload();
            }
            catch (Exception e) { await Dialogs.Message(this, L.T("Envoi impossible : ") + e.Message); }
        }
    }
}
