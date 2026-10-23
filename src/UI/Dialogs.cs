namespace StremioWT
{
    static class Dialogs
    {
        public static void Dark(Form f)
        {
            f.BackColor = Theme.Background;
            f.ForeColor = Theme.Text;
            f.Font = Theme.Body;
            f.FormBorderStyle = FormBorderStyle.FixedSingle;
            f.MaximizeBox = f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowInTaskbar = false;
            f.RightToLeft = L.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
            try { f.Icon = Theme.AppIcon(); } catch { }
            f.HandleCreated += (s, e) => MainForm.DarkTitleBar(f.Handle);
        }

        public static void AskImport(IWin32Window owner, System.Collections.Generic.List<StremioFilm> items)
        {
            int films = items.Count(f => f.Type == "movie"), series = items.Count - films;
            var what = string.Join(L.T(" et "), new[] { films > 0 ? L.F("{0} film(s)", films) : null, series > 0 ? L.F("{0} série(s) ou anime(s)", series) : null }.Where(s => s != null));
            var sample = string.Join("\n", items.Take(5).Select(f => "• " + f.Display)) + (items.Count > 5 ? L.F("\n… et {0} autre(s)", items.Count - 5) : "");
            var add = MessageBox.Show(owner,
                L.F("J'ai trouvé {0} déjà vu(s) dans ton Stremio qui ne sont pas dans ton journal :\n\n{1}\n\nLes ajouter à ton journal ?", what, sample),
                L.T("Mon journal"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (add != DialogResult.Yes) { Journal.MarkSeen(items); return; }
            bool onLetterboxd = false;
            if (films > 0)
                onLetterboxd = MessageBox.Show(owner,
                    L.T("Ces films sont-ils déjà sur ton compte Letterboxd ?\n\nOui : ils restent dans ton journal mais ne seront pas exportés (pas de doublons).\nNon : ils partiront au prochain envoi vers Letterboxd.\n\n(Les séries ne vont pas sur Letterboxd, qui ne gère que les films.)"),
                    L.T("Mon journal"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            int n = Journal.ImportHistory(items, onLetterboxd);
            Console.WriteLine($"{n} titre(s) de ton historique Stremio ajouté(s) au journal.");
        }

        public static string Date(string iso) =>
            DateTime.TryParse(iso, out var d) ? d.ToString("dd'/'MM'/'yyyy") : iso;

        public static Label Text(string text, Font font, Color color, int x, int y, int w = 0, int h = 0)
        {
            var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, Location = new Point(x, y) };
            if (w > 0) { l.AutoSize = false; l.Size = new Size(w, h); } else l.AutoSize = true;
            return l;
        }
    }
}
