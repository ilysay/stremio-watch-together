namespace StremioWT
{
    // « Signaler un bug » : l'utilisateur voit exactement ce qui sera envoyé, et rien ne part sans son clic.
    class ReportForm : Form
    {
        public ReportForm(string error = null)
        {
            Dialogs.Dark(this);
            Text = L.T("Signaler un bug");
            ClientSize = new Size(520, 560);
            var report = BugReport.Build(error);

            Controls.Add(Dialogs.Text(L.T("Décris ce qui s'est passé (facultatif) :"), Theme.Bold, Theme.Text, 20, 16));
            var message = Box(20, 44, 480, 80, false);
            message.MaxLength = 1000;
            Controls.Add(message);

            Controls.Add(Dialogs.Text(L.T("Ce qui sera envoyé au développeur : ton message, ta version de StremioWT et de ton système, et le journal de l'appli ci-dessous (il peut contenir ton pseudo et les titres regardés)."),
                Theme.Small, Theme.Muted, 20, 136, 480, 52));
            var preview = Box(20, 192, 480, 288, true);
            preview.Text = report.Replace("\n", "\r\n");
            Controls.Add(preview);

            var status = Dialogs.Text("", Theme.Small, Theme.Muted, 20, 496, 260, 40);
            var send = new FlatButton(L.T("Envoyer"), primary: true) { Location = new Point(390, 500), Size = new Size(110, 38) };
            var cancel = new FlatButton(L.T("Annuler"), primary: false) { Location = new Point(270, 500), Size = new Size(110, 38) };
            cancel.Click += (s, e) => Close();
            send.Click += async (s, e) =>
            {
                var wait = BugReport.WaitBeforeNext();
                if (wait > TimeSpan.Zero)
                {
                    status.ForeColor = Theme.Bad;
                    status.Text = L.F("Un rapport vient d'être envoyé. Réessaie dans {0} min.", Math.Ceiling(wait.TotalMinutes));
                    return;
                }
                send.Enabled = cancel.Enabled = message.Enabled = false;
                status.ForeColor = Theme.Muted;
                status.Text = L.T("Envoi…");
                bool ok = await BugReport.Send(message.Text, report);
                status.ForeColor = ok ? Theme.Ok : Theme.Bad;
                status.Text = ok ? L.T("Rapport envoyé, merci !") : L.T("Envoi impossible. Vérifie ta connexion et réessaie plus tard.");
                cancel.Enabled = true;
                cancel.Text = L.T("Fermer");
                if (!ok) send.Enabled = message.Enabled = true;
            };
            Controls.AddRange(new Control[] { status, cancel, send });
        }

        static TextBox Box(int x, int y, int w, int h, bool readOnly) => new TextBox
        {
            Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical, WordWrap = !readOnly,
            Location = new Point(x, y), Size = new Size(w, h),
            BackColor = Theme.Input, ForeColor = readOnly ? Theme.Muted : Theme.Text, BorderStyle = BorderStyle.FixedSingle,
            Font = readOnly ? new Font("Consolas", 8.5f) : Theme.Body,
        };
    }
}
