namespace StremioWT
{
    class RatingForm : Form
    {
        public RatingForm(JournalEntry entry, Action<JournalEntry> saved)
        {
            Dialogs.Dark(this);
            Text = L.T(entry.IsMovie ? "Noter le film" : entry.Type == "anime" ? "Noter l'anime" : "Noter la série");
            try { Icon = Theme.HistoIcon(); } catch { }
            ClientSize = new Size(460, 330);

            var poster = new PictureBox { Location = new Point(24, 24), Size = new Size(92, 138), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card };
            LoadPoster(poster, entry);
            var title = Dialogs.Text(entry.Display, Theme.Section, Theme.Text, 132, 24, 304, 48);
            var info = Dialogs.Text(
                (entry.IsMovie ? L.F("Vu le {0}", Dialogs.Date(entry.WatchedDate)) : $"{entry.Progress} · {Dialogs.Date(entry.WatchedDate)}")
                + (entry.With.Count > 0 ? L.T(" avec ") + string.Join(", ", entry.With) : "") + (entry.Rewatch ? L.T(" · revisionnage") : ""),
                Theme.Small, Theme.Muted, 132, 74, 304, 36);
            var stars = new StarRating { Location = new Point(130, 114), BackColor = Theme.Background, Value = entry.Rating };
            var hint = Dialogs.Text(L.T("Clique sur une étoile (demi-étoiles possibles)"), Theme.Small, Theme.Muted, 132, 154);

            var reviewLabel = Dialogs.Text(L.T("Critique (facultatif)"), Theme.Small, Theme.Muted, 24, 178);
            var review = new InputBox { Location = new Point(24, 200), Size = new Size(412, 66), BackColor = Theme.Background };
            review.Box.Multiline = true;
            review.Box.Text = entry.Review;

            var later = new FlatButton(L.T("Plus tard"), primary: false) { Location = new Point(212, 280), Size = new Size(108, 36) };
            var save = new FlatButton(L.T("Enregistrer"), primary: true) { Location = new Point(328, 280), Size = new Size(108, 36) };
            later.Click += (s, e) => Close();
            save.Click += (s, e) =>
            {
                Journal.Update(entry, stars.Value, review.Box.Text.Trim());
                saved?.Invoke(entry);
                Close();
            };
            Controls.AddRange(new Control[] { poster, title, info, stars, hint, reviewLabel, review, later, save });
        }

        // images.metahub.space renvoie du WebP, illisible par Windows Forms : live.metahub.space donne la même affiche en JPEG.
        static async void LoadPoster(PictureBox box, JournalEntry entry)
        {
            foreach (var url in new[] { $"https://live.metahub.space/poster/medium/{entry.ImdbId}/img", entry.Poster })
            {
                if (string.IsNullOrEmpty(url)) continue;
                try
                {
                    var bytes = await Program.Http.GetByteArrayAsync(url);
                    var image = Image.FromStream(new MemoryStream(bytes));
                    if (!box.IsDisposed) box.Image = image;
                    return;
                }
                catch { }
            }
        }
    }

    class JournalForm : Form
    {
        readonly ListBox list = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 76, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, IntegralHeight = false };
        readonly Label count = Dialogs.Text("", Theme.Small, Theme.Muted, 24, 106);
        readonly FlatButton[] tabs =
        {
            new FlatButton("", primary: true) { Location = new Point(24, 66), Size = new Size(160, 32) },
            new FlatButton("", primary: false) { Location = new Point(192, 66), Size = new Size(160, 32) },
            new FlatButton("", primary: false) { Location = new Point(360, 66), Size = new Size(160, 32) },
        };
        static readonly string[] TabTypes = { "movie", "series", "anime" };
        int tab;

        readonly Card exportCard = new Card(L.T("Envoyer vers Letterboxd")) { Location = new Point(24, 552), Size = new Size(512, 114) };
        readonly Label exportHint = Dialogs.Text("", Theme.Small, Theme.Muted, 16, 40, 480, 20);
        readonly FlatButton exportNew = new FlatButton("", primary: true) { Location = new Point(16, 66), Size = new Size(232, 36) };
        readonly FlatButton exportAll = new FlatButton("", primary: false) { Location = new Point(256, 66), Size = new Size(122, 36), Font = Theme.Small };
        readonly FlatButton open = new FlatButton(L.T("Ouvrir Letterboxd"), primary: false) { Location = new Point(386, 66), Size = new Size(110, 36), Font = Theme.Small };
        readonly Action<JournalEntry> rated;
        bool fileReady;

        public JournalForm(Action<JournalEntry> rated)
        {
            this.rated = rated;
            Dialogs.Dark(this);
            Text = L.T("Mon journal");
            ClientSize = new Size(560, 680);
            try { Icon = Theme.HistoIcon(); } catch { }

            var headerIcon = new PictureBox { Location = new Point(22, 10), Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { headerIcon.Image = Theme.Histo(); } catch { }
            var title = Dialogs.Text(L.T("Mon journal"), Theme.Title, Theme.Text, 72, 14);
            for (int i = 0; i < tabs.Length; i++) { int t = i; tabs[i].Click += (s, e) => { tab = t; Reload(); }; }

            var listCard = new Card(null) { Location = new Point(24, 130), Size = new Size(512, 362), Padding = new Padding(0) };
            list.SetBounds(10, 10, 492, 342);
            listCard.Controls.Add(list);
            list.DrawItem += DrawEntry;
            MainForm.DarkScrollbars(list);
            list.DoubleClick += (s, e) => Rate();

            var rate = new FlatButton(L.T("Noter / modifier"), primary: false) { Location = new Point(24, 504), Size = new Size(160, 36) };
            var remove = new FlatButton(L.T("Supprimer"), primary: false) { Location = new Point(192, 504), Size = new Size(110, 36) };
            var import = new FlatButton(L.T("Importer depuis Stremio"), primary: false) { Location = new Point(310, 504), Size = new Size(226, 36) };
            rate.Click += (s, e) => Rate();
            remove.Click += (s, e) => Remove();
            import.Click += (s, e) => ImportFromStremio();

            exportHint.BackColor = Theme.Card;
            exportNew.Click += (s, e) => Export(all: false);
            exportAll.Click += (s, e) => Export(all: true);
            open.Click += (s, e) => OpenImportPage();
            exportCard.Controls.AddRange(new Control[] { exportHint, exportNew, exportAll, open });

            Controls.AddRange(new Control[] { headerIcon, title, tabs[0], tabs[1], tabs[2], count, listCard, rate, remove, import, exportCard });
            Journal.Changed += Reload;
            Journal.ThumbReady += Repaint;
            FormClosed += (s, e) => { Journal.Changed -= Reload; Journal.ThumbReady -= Repaint; };
            Reload();
        }

        void Repaint()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)Repaint); return; }
            list.Invalidate();
        }

        async void ImportFromStremio()
        {
            var session = Session.Current;
            if (session == null)
            {
                MessageBox.Show(this, L.T("Pour lire ton historique, StremioWT doit être branché sur Stremio :\nhéberge ou rejoins un salon, puis reviens ici.\n\n(À l'ouverture d'un salon, l'import est aussi proposé automatiquement.)"),
                    L.T("Mon journal"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                var history = await session.ReadHistory();
                Journal.SyncSeries(history);
                var fresh = Journal.NotInJournal(history);
                if (fresh.Count == 0)
                {
                    MessageBox.Show(this, L.F("Ton historique Stremio contient {0} titre(s) vu(s), tous déjà dans ton journal.", history.Count), L.T("Mon journal"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Dialogs.AskImport(this, fresh);
            }
            catch (Exception e) { MessageBox.Show(this, L.T("Lecture de l'historique impossible : ") + e.Message, L.T("Mon journal"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void Reload()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)Reload); return; }
            var all = Journal.All();
            int CountOf(string type) => all.Count(e => e.Type == type);
            tabs[0].Text = L.F("Films ({0})", CountOf("movie"));
            tabs[1].Text = L.F("Séries ({0})", CountOf("series"));
            tabs[2].Text = L.F("Animes ({0})", CountOf("anime"));
            for (int i = 0; i < tabs.Length; i++) { tabs[i].Primary = i == tab; tabs[i].Invalidate(); }

            var shown = all.Where(e => e.Type == TabTypes[tab]).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in shown) list.Items.Add(e);
            list.EndUpdate();

            int pending = Journal.PendingExport(), movies = Journal.MovieCount();
            if (tab == 0)
            {
                count.Text = shown.Count == 0
                    ? L.T("Aucun film pour l'instant : les films regardés jusqu'au bout (85 %) s'ajoutent tout seuls.")
                    : L.F("{0} film(s) · {1} pas encore envoyé(s) vers Letterboxd", shown.Count, pending);
                exportCard.Title = L.T("Envoyer vers Letterboxd");
                if (!fileReady)
                    exportHint.Text = L.T("Notés ou non. Les films déjà envoyés ne repartent pas : pas de doublons.");
                exportNew.Text = pending == 0 ? L.T("Rien de nouveau à envoyer") : pending == 1 ? L.T("Envoyer le nouveau film") : L.F("Envoyer les {0} nouveaux films", pending);
                exportNew.Enabled = pending > 0;
                exportAll.Text = L.F("Tout renvoyer ({0})", movies);
                exportAll.Enabled = movies > 0;
                exportNew.Visible = exportAll.Visible = open.Visible = true;
            }
            else
            {
                count.Text = shown.Count == 0
                    ? L.T(tab == 1 ? "Aucune série pour l'instant : les épisodes regardés jusqu'au bout s'ajoutent tout seuls." : "Aucun anime pour l'instant : les épisodes regardés jusqu'au bout s'ajoutent tout seuls.")
                    : L.F(tab == 1 ? "{0} série(s) · {1} terminée(s)" : "{0} anime(s) · {1} terminé(s)", shown.Count, shown.Count(e => e.Completed));
                exportCard.Title = "Letterboxd";
                exportHint.Text = L.T("Letterboxd ne gère que les films : tes séries et animes restent dans ton journal.");
                exportNew.Visible = exportAll.Visible = open.Visible = false;
            }
            exportCard.Invalidate();
        }

        void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var entry = (JournalEntry)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(selected ? Color.FromArgb(24, 52, 110) : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds;
            var posterRect = new Rectangle(r.X + 6, r.Y + 6, 42, 63);
            var thumb = Journal.Thumb(entry.ImdbId);
            if (thumb != null) { e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.DrawImage(thumb, posterRect); }
            else using (var b = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(b, posterRect);
            int x = posterRect.Right + 12;
            var stars = Journal.Stars(entry.Rating);
            int starsWidth = TextRenderer.MeasureText(stars, Theme.Bold).Width;
            TextRenderer.DrawText(e.Graphics, entry.Display, Theme.Bold, new Rectangle(x, r.Y + 14, r.Right - x - starsWidth - 16, 22), Theme.Text, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, stars, Theme.Bold, new Point(r.Right - 8 - starsWidth, r.Y + 14),
                entry.Rating > 0 ? Color.FromArgb(0, 224, 84) : Theme.Muted);
            var line2 = (entry.IsMovie ? "" : entry.Progress + " · ") + Dialogs.Date(entry.WatchedDate)
                + (entry.With.Count > 0 ? L.T(" · avec ") + string.Join(", ", entry.With) : "")
                + (entry.Rewatch ? L.T(" · revisionnage") : "") + (string.IsNullOrEmpty(entry.Review) ? "" : L.T(" · critique"))
                + (entry.IsMovie && entry.Exported ? L.T(" · ✓ envoyé") : "");
            TextRenderer.DrawText(e.Graphics, line2, Theme.Small, new Rectangle(x, r.Y + 40, r.Right - x - 8, 20), entry.Completed ? Color.FromArgb(0, 224, 84) : Theme.Muted, TextFormatFlags.EndEllipsis);
            using (var pen = new Pen(Theme.CardBorder)) e.Graphics.DrawLine(pen, r.X + 8, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
        }

        JournalEntry Selected() => list.SelectedItem as JournalEntry;

        void Rate()
        {
            var entry = Selected();
            if (entry == null) return;
            new RatingForm(entry, rated).ShowDialog(this);
        }

        void Remove()
        {
            var entry = Selected();
            if (entry == null) return;
            if (MessageBox.Show(this, L.F("Retirer {0} du journal ?", entry.Display), L.T("Mon journal"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Journal.Remove(entry);
        }

        void Export(bool all)
        {
            if (all)
            {
                int already = Journal.MovieCount() - Journal.PendingExport();
                if (already > 0 && MessageBox.Show(this,
                        L.F("Le fichier contiendra tous tes films, y compris les {0} déjà envoyé(s).\n\nSi tu l'importes sur Letterboxd, ces films apparaîtront une deuxième fois dans ton journal Letterboxd.\n\nContinuer ?", already),
                        L.T("Tout renvoyer"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            try
            {
                var (n, path) = Journal.ExportCsv(all);
                OpenImportPage();
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                exportHint.ForeColor = Color.FromArgb(0, 224, 84);
                exportHint.Text = L.F("Fichier prêt ({0} film(s)) : glisse-le sur la page Letterboxd, puis « Import ».", n);
                fileReady = true;
            }
            catch (Exception e) { MessageBox.Show(this, L.T("Envoi impossible : ") + e.Message, "Letterboxd", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static void OpenImportPage()
        {
            try { Process.Start(new ProcessStartInfo("https://letterboxd.com/import/") { UseShellExecute = true }); } catch { }
        }
    }
}
