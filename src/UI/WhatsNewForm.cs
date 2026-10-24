namespace StremioWT
{
    // Après une mise à jour : les nouveautés de cette version (liste dans WhatsNew.cs), une seule fois.
    class WhatsNewForm : Form
    {
        static (string Icon, string Title, string Body)[] Items => WhatsNew.Items;

        public WhatsNewForm(string version)
        {
            Dialogs.Dark(this);
            Text = "Stremio Watch Together";
            const int rowHeight = 74;
            ClientSize = new Size(460, 160 + Items.Length * rowHeight);

            Controls.Add(Dialogs.Text(L.T("Nouveautés"), Theme.Title, Theme.Text, 24, 18, 412, 36));
            Controls.Add(Dialogs.Text(L.F("StremioWT {0} est installé ✓", version), Theme.Body, Theme.Ok, 24, 56, 412, 22));

            int y = 92;
            foreach (var item in Items)
            {
                var card = new Card(null) { Location = new Point(24, y), Size = new Size(412, rowHeight - 10), Padding = new Padding(0) };
                card.Controls.Add(Dialogs.Text(item.Icon, new Font("Segoe UI Emoji", 16f), Theme.Text, 10, 14, 40, 36));
                card.Controls.Add(Dialogs.Text(item.Title, Theme.Bold, Theme.Text, 52, 8, 350, 20));
                card.Controls.Add(Dialogs.Text(item.Body, Theme.Small, Theme.Muted, 52, 28, 350, 34));
                Controls.Add(card);
                y += rowHeight;
            }

            var ok = new FlatButton(L.T("C'est parti"), primary: true) { Location = new Point(24, y + 10), Size = new Size(412, 42) };
            ok.Click += (s, e) => Close();
            Controls.Add(ok);
        }
    }
}
