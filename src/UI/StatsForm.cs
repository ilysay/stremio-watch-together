namespace StremioWT
{
    // Avant de couper les statistiques anonymes : ce qui est envoyé, ce qui ne l'est jamais, et à quoi ça sert.
    // « Désactiver » reste un vrai bouton bien visible : refuser doit être aussi simple qu'accepter.
    class StatsForm : Form
    {
        public bool Keep { get; private set; } = true;

        public StatsForm()
        {
            Dialogs.Dark(this);
            Text = L.T("Statistiques anonymes");
            ClientSize = new Size(460, 534);

            Controls.Add(Dialogs.Text(L.T("Garder les statistiques anonymes ?"), Theme.Section, Theme.Text, 24, 20, 412, 28));
            Controls.Add(Dialogs.Text(L.T("Elles m'aident à voir ce qui marche, ce qui plante, et à maintenir StremioWT. Rien de privé n'en sort."), Theme.Body, Theme.Muted, 24, 54, 412, 44));

            Controls.Add(Section(L.T("Ce qui est envoyé"), Theme.Ok, L.T("• un identifiant d'installation tiré au hasard (pas lié à toi)\n• la version de l'appli, la langue et le pays\n• appli ouverte, salon créé ou rejoint, taille du salon\n• la durée arrondie des séances et les codes d'erreur"), 24, 108, 122));
            Controls.Add(Section(L.T("Jamais envoyé"), Theme.Bad, L.T("• ton pseudo et ton adresse IP\n• ce que tu regardes, les liens, le chat\n• les codes de salon, sauf le code court : gardé 24 h, juste pour que tes amis puissent entrer"), 24, 242, 118));

            Controls.Add(Dialogs.Text(L.T("Si tu désactives, un dernier signal anonyme « statistiques désactivées » est envoyé, puis plus rien. Le code du serveur est public."), Theme.Small, Theme.Muted, 24, 372, 412, 54));

            var keep = new FlatButton(L.T("Garder les statistiques"), primary: true) { Location = new Point(24, 434), Size = new Size(412, 42) };
            var off = new FlatButton(L.T("Désactiver"), primary: false) { Location = new Point(24, 484), Size = new Size(412, 34) };
            keep.Click += (s, e) => { Keep = true; Close(); };
            off.Click += (s, e) => { Keep = false; Close(); };
            Controls.AddRange(new Control[] { keep, off });
        }

        static Card Section(string title, Color mark, string body, int x, int y, int h)
        {
            var card = new Card(null) { Location = new Point(x, y), Size = new Size(412, h), Padding = new Padding(0) };
            card.Controls.Add(Dialogs.Text(title, Theme.Bold, mark, 14, 10, 384, 22));
            card.Controls.Add(Dialogs.Text(body, Theme.Small, Theme.Text, 14, 34, 384, h - 40));
            return card;
        }
    }
}
