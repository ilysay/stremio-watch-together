namespace StremioWT
{
    class SettingsForm : Form
    {
        public SettingsForm()
        {
            Dialogs.Dark(this);
            Text = L.T("Paramètres");
            ClientSize = new Size(400, 262);
            // Langue : appliquée en relançant StremioWT (toute l'interface est construite au démarrage).
            Controls.Add(Dialogs.Text(L.T("Langue"), Theme.Bold, Theme.Text, 20, 20));
            var lang = new FlatButton(L.Languages.First(l => l.Code == L.Lang).Name + "   ▾", primary: false) { Location = new Point(20, 46), Size = new Size(360, 38) };
            var menu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            foreach (var l in L.Languages)
            {
                var item = new ToolStripMenuItem(l.Name) { ForeColor = Theme.Text, Checked = l.Code == L.Lang, AutoSize = false, Size = new Size(356, 30) };
                var code = l.Code;
                item.Click += (s, e) =>
                {
                    if (code == L.Lang) return;
                    L.Save(code);
                    Program.Restart();
                };
                menu.Items.Add(item);
            }
            lang.Click += (s, e) => menu.Show(lang, new Point(0, lang.Height));
            Controls.Add(lang);
            var stats = new ToggleSwitch(L.T("Statistiques anonymes")) { Location = new Point(20, 104), Size = new Size(360, 26), ForeColor = Theme.Text, BackColor = Theme.Background, Checked = Telemetry.Enabled };
            stats.CheckedChanged += (s, e) => Telemetry.Enabled = stats.Checked;
            Controls.Add(stats);
            Controls.Add(Dialogs.Text(L.T("Compte combien de personnes utilisent StremioWT.\nAucun pseudo, titre, lien ni adresse IP n'est enregistré."), Theme.Small, Theme.Muted, 20, 138, 360, 56));
            var report = new FlatButton("🐞  " + L.T("Signaler un bug"), primary: false) { Location = new Point(20, 204), Size = new Size(360, 38) };
            report.Click += (s, e) => new ReportForm().ShowDialog(this);
            Controls.Add(report);
        }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuBorder => Theme.CardBorder;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuItemSelected => Color.FromArgb(24, 52, 110);
        public override Color CheckBackground => Theme.Accent;
        public override Color CheckSelectedBackground => Theme.AccentHover;
        public override Color CheckPressedBackground => Theme.Accent;
    }
}
