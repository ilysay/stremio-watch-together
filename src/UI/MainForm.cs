namespace StremioWT
{
    partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Panel startPanel = new Panel { Dock = DockStyle.Fill };
        readonly InputBox nameBox = new InputBox();
        readonly InputBox codeBox = new InputBox();
        readonly FlatButton joinButton = new FlatButton(L.T("Rejoindre"), primary: true, Glyph.Join);
        readonly FlatButton hostButton = new FlatButton(L.T("Héberger un salon"), primary: false, Glyph.Play);
        readonly Label startStatus = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Color.Transparent };
        // Affiché sous une erreur de l'accueil : ouvre le rapport de bug avec ce message d'erreur.
        readonly FlatButton reportButton = new FlatButton(L.T("Signaler ce problème"), primary: false, Glyph.Bug) { Location = new Point(24, 548), Size = new Size(220, 34), Visible = false };

        readonly Panel sessionPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        readonly Label statusDot = new Label { Text = "●", AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 13f), BackColor = Color.Transparent };
        readonly Label statusText = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Section, BackColor = Color.Transparent };
        readonly ListBox membersList = new ListBox { IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body };
        readonly Card inviteCard = new Card(L.T("Inviter des amis"), Glyph.Link);
        readonly InputBox inviteBox = new InputBox();
        readonly FlatButton copyButton = new FlatButton(L.T("Copier le lien d'invitation"), primary: true, Glyph.Link);
        readonly ToggleSwitch adminBox = new ToggleSwitch(L.T("Hébergeur admin")) { ForeColor = Theme.Text, Font = Theme.Bold, BackColor = Theme.Card, Icon = Glyph.Shield };
        readonly Label adminHint = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Card };
        readonly ChatFeed chatFeed = new ChatFeed();
        readonly InputBox chatInput = new InputBox();
        readonly FlatButton emoteButton = new FlatButton("", primary: false, Glyph.Emoji);
        readonly FlatButton gifButton = new FlatButton("GIF", primary: false);
        readonly FlatButton sendButton = new FlatButton("", primary: true, Glyph.Send);
        readonly FlatButton quitButton = new FlatButton(L.T("Quitter"), primary: false, Glyph.Leave) { Danger = true };
        // Stremio fermé pendant la séance : le salon reste ouvert, ce bouton le relance (à la place de l'aide « Touche T »).
        readonly FlatButton restartButton = new FlatButton(L.T("Relancer Stremio"), primary: true, Glyph.Play) { Visible = false };
        Label chatHint;
        // Code long du salon : sert au lien d'invitation, même quand la case affiche le code court.
        string inviteLong = "";
        readonly Card adminCard = new Card(null) { Padding = new Padding(0) };
        readonly Label membersCaption = new Label { Text = L.T("Participants"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Card };
        string[] shownMembers = new string[0];
        readonly Card chatCard = new Card(L.T("Chat"), Glyph.Chat);

        Session session;

        public MainForm()
        {
            Text = "Stremio Watch Together";
            Font = Theme.Body;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            ClientSize = new Size(480, 790);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            RightToLeft = L.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
            try { Icon = Theme.AppIcon(); } catch { }
            BuildStart();
            BuildSession();
            Controls.Add(sessionPanel);
            Controls.Add(startPanel);
            DarkScrollbars(membersList);
            membersList.DrawMode = DrawMode.OwnerDrawFixed;
            membersList.ItemHeight = 24;
            membersList.DrawItem += DrawMember;
            startPanel.BackgroundImage = sessionPanel.BackgroundImage = Theme.Backdrop(ClientSize);
            startPanel.BackgroundImageLayout = sessionPanel.BackgroundImageLayout = ImageLayout.None;
            FormClosed += (s, e) =>
            {
                Program.Log("fenêtre fermée" + (session == null ? "" : ", salon " + session.Room));
                // Rend sa transparence à Stremio (sinon les vidéos suivantes resteraient blanches).
                // Sur un autre fil : attendre de l'asynchrone depuis celui de la fenêtre bloquerait la fermeture.
                var current = session;
                if (current != null) Task.WhenAll(Task.Run(() => current.ReleaseStremio()), current.ReportEnd()).Wait(5000);
                Environment.Exit(0);
            };
            Shown += (s, e) => AutoJoinFromClipboard();
            Shown += (s, e) => StartPanel();
            Shown += (s, e) =>
            {
                var updated = Updates.AfterUpdate();
                if (updated != null && string.IsNullOrEmpty(startStatus.Text)) startStatus.Text = L.F("Mis à jour vers la version {0} ✓", updated);
                // Après la fenêtre principale, pour ne pas bloquer une invitation reçue au lancement.
                if (Updates.WhatsNewPending(updated)) BeginInvoke(new Action(() => { using (var news = new WhatsNewForm(Program.DisplayVersion)) news.ShowDialog(this); }));
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTitleBar(Handle);
        }

        public static void DarkTitleBar(IntPtr handle)
        {
            try { int on = 1; if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(handle, 19, ref on, 4); } catch { }
            // Windows 11 : barre de titre de la couleur du fond, pour qu'elle se fonde dans la fenêtre (ignoré sur Windows 10).
            try
            {
                int Ref(Color c) => c.R | c.G << 8 | c.B << 16;
                int caption = Ref(Theme.Background), text = Ref(Theme.Muted), border = Ref(Theme.CardBorder);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 36, ref text, 4);
                DwmSetWindowAttribute(handle, 34, ref border, 4);
            }
            catch { }
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        public static void DarkScrollbars(Control c)
        {
            if (c.IsHandleCreated) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
            c.HandleCreated += (s, e) => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        }

        // Participant : pastille colorée avec son initiale, son pseudo, puis la note (version de Stremio, chargement…) en gris.
        static readonly Color[] AvatarColors =
        {
            Color.FromArgb(123, 91, 245), Color.FromArgb(34, 179, 101), Color.FromArgb(232, 86, 155), Color.FromArgb(245, 158, 11),
            Color.FromArgb(59, 130, 246), Color.FromArgb(20, 184, 166), Color.FromArgb(239, 68, 68), Color.FromArgb(168, 85, 247),
        };
        void DrawMember(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, e.Bounds);
            var line = membersList.Items[e.Index] as string ?? "";
            int cut = line.IndexOf("  ", StringComparison.Ordinal);
            string who = (cut < 0 ? line : line.Substring(0, cut)).Trim(), note = cut < 0 ? "" : line.Substring(cut).Trim();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var dot = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, 20, 20);
            using (var b = new SolidBrush(AvatarColors[Math.Abs(who.Aggregate(17, (h, ch) => h * 31 + ch)) % AvatarColors.Length])) g.FillEllipse(b, dot);
            TextRenderer.DrawText(g, who.Length > 0 ? char.ToUpper(who[0]).ToString() : "?", Theme.Bold, dot, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int x = dot.Right + 10;
            var nameSize = TextRenderer.MeasureText(g, who, Theme.Bold, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, who, Theme.Bold, new Rectangle(x, e.Bounds.Y, nameSize.Width + 4, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (note.Length > 0)
                TextRenderer.DrawText(g, note.TrimStart('·', ' '), Theme.Small, new Rectangle(x + nameSize.Width + 10, e.Bounds.Y, e.Bounds.Right - x - nameSize.Width - 10, e.Bounds.Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void OpenJournal() => new JournalForm(e => session?.ShareRating(e)).ShowDialog(this);

        Control Header()
        {
            var header = new Panel { Location = new Point(0, 0), Size = new Size(480, 96), BackColor = Color.Transparent };
            var logo = new PictureBox { Location = new Point(24, 18), Size = new Size(60, 60), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { logo.Image = Theme.Logo(); } catch { }
            var title = new Label { Text = "Watch Together", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(94, 20), BackColor = Color.Transparent };
            var subtitle = new Label { Text = L.F("Regarder Stremio ensemble, synchronisés · v{0}", Program.DisplayVersion), Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(97, 54), BackColor = Color.Transparent };
            header.Controls.AddRange(new Control[] { logo, title, subtitle });
            return header;
        }

        static Label Caption(string text, int x, int y) =>
            new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Card };

        void BuildStart()
        {
            var nameCard = new Card(L.T("Ton pseudo"), Glyph.User) { Location = new Point(24, 104), Size = new Size(432, 92) };
            nameBox.SetBounds(16, 42, 400, 36);
            nameBox.Box.Text = Rooms.DefaultName();
            nameBox.Box.MaxLength = 32;
            nameBox.Icon = Glyph.User;
            nameCard.Controls.Add(nameBox);

            var joinCard = new Card(L.T("Rejoindre un salon"), Glyph.Join) { Location = new Point(24, 208), Size = new Size(432, 158) };
            joinCard.Controls.Add(Caption(L.T("Colle ou tape le code reçu (8 caractères ou WT-…)"), 16, 42));
            codeBox.SetBounds(16, 64, 400, 36);
            codeBox.Icon = Glyph.Link;
            joinButton.SetBounds(16, 108, 400, 38);
            joinCard.Controls.AddRange(new Control[] { codeBox, joinButton });

            var hostCard = new Card(L.T("Héberger un salon"), Glyph.Play) { Location = new Point(24, 378), Size = new Size(432, 94) };
            hostButton.SetBounds(16, 44, 400, 38);
            hostCard.Controls.Add(hostButton);

            startStatus.SetBounds(24, 484, 432, 60);
            reportButton.Click += (s, e) => new ReportForm(startStatus.Text).ShowDialog(this);
            joinButton.Click += (s, e) => StartJoin();
            hostButton.Click += (s, e) => StartHost();
            codeBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartJoin(); } };
            var journalStart = new FlatButton(L.T("Mon journal"), primary: false) { Location = new Point(24, 740), Size = new Size(160, 38), Picture = Theme.Histo() };
            journalStart.Click += (s, e) => OpenJournal();
            var settings = new FlatButton("", primary: false, Glyph.Settings) { Location = new Point(416, 740), Size = new Size(40, 38) };
            settings.Click += (s, e) => new SettingsForm().ShowDialog(this);
            new ToolTip().SetToolTip(settings, L.T("Paramètres"));
            var update = new FlatButton("", primary: true, Glyph.Download) { Location = new Point(24, 680), Size = new Size(432, 44), Visible = false };
            update.Click += async (s, e) =>
            {
                // Mise à jour dans l'appli ; en cas d'échec, la page de téléchargement s'ouvre comme avant.
                update.Enabled = false;
                bool ok = await Updates.Install(p => Ui(() => update.Text = L.F("Mise à jour… {0} %", p)));
                if (ok) { Program.Restart(); return; }
                update.Enabled = true;
                update.Text = L.T("Mise à jour impossible : la page de téléchargement s'ouvre.");
                OpenUrl(Updates.DownloadUrl);
            };
            Shown += async (s, e) =>
            {
                var version = await Updates.NewerVersion();
                if (version != null && !IsDisposed) { update.Text = L.F("Nouvelle version {0} disponible : mettre à jour", version); update.Visible = true; }
            };
            startPanel.Controls.AddRange(new Control[] { Header(), nameCard, joinCard, hostCard, startStatus, reportButton, update, journalStart, settings });
        }

        void BuildSession()
        {
            var statusCard = new Card(null) { Location = new Point(24, 104), Size = new Size(432, 150), Padding = new Padding(0) };
            statusDot.Location = new Point(14, 9);
            statusDot.BackColor = Color.Transparent;
            statusText.Location = new Point(38, 13);
            statusText.BackColor = Color.Transparent;
            statusCard.Controls.Add(membersCaption);
            membersCaption.Location = new Point(16, 46);
            membersList.SetBounds(16, 66, 400, 76);
            membersList.Click += (s, e) =>
            {
                int i = membersList.SelectedIndex;
                if (session != null && session.IsHost && i >= 0 && i < shownMembers.Length) session.ToggleDontWait(shownMembers[i]);
                membersList.ClearSelected();
                ActiveControl = null;
            };
            statusCard.Controls.AddRange(new Control[] { statusDot, statusText, membersList });

            inviteCard.Location = new Point(24, 266);
            inviteCard.Size = new Size(432, 150);
            // Le code (pour ceux qui ont déjà l'appli), puis le lien d'invitation, qui sert aussi aux autres :
            // la page leur fait télécharger l'appli, puis les fait entrer dans le salon.
            inviteBox.SetBounds(16, 40, 276, 34);
            inviteBox.Box.ReadOnly = true;
            inviteBox.Box.BackColor = Theme.Input;
            var copyCode = new FlatButton(L.T("Copier le code"), primary: false, Glyph.Copy) { Font = Theme.Small };
            copyCode.SetBounds(300, 38, 116, 36);
            copyCode.Click += (s, e) => { try { Clipboard.SetText(inviteBox.Box.Text); FlashText(copyCode, L.T("Code copié ✓")); } catch { } };
            copyButton.SetBounds(16, 82, 400, 36);
            copyButton.Click += (s, e) => { try { Clipboard.SetText(Invite.Link(inviteLong)); FlashText(copyButton, L.T("Lien copié ✓")); } catch { } };
            var inviteHint = Dialogs.Text(L.T("Le lien fait aussi télécharger StremioWT aux amis qui ne l'ont pas."), Theme.Small, Theme.Muted, 16, 124, 400, 20);
            inviteCard.Controls.AddRange(new Control[] { inviteBox, copyButton, copyCode, inviteHint });

            adminCard.Location = new Point(24, 428);
            adminCard.Size = new Size(432, 76);
            adminBox.SetBounds(16, 12, 400, 26);
            adminHint.SetBounds(78, 38, 342, 34);
            adminBox.CheckedChanged += (s, e) => { if (session != null && session.IsHost) session.SetAdmin(adminBox.Checked); };
            adminCard.Controls.AddRange(new Control[] { adminBox, adminHint });

            chatCard.Location = new Point(24, 516);
            chatCard.Size = new Size(432, 216);
            chatFeed.SetBounds(16, 40, 404, 118);
            chatInput.SetBounds(16, 166, 214, 36);
            chatInput.Box.MaxLength = Chat.MaxText;
            new ToolTip().SetToolTip(sendButton, L.T("Envoyer"));
            chatInput.Width = 238;
            emoteButton.SetBounds(260, 165, 44, 38);
            gifButton.SetBounds(310, 165, 58, 38);
            sendButton.SetBounds(374, 165, 46, 38);
            chatInput.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendChat(); } };
            sendButton.Click += (s, e) => SendChat();
            emoteButton.Click += (s, e) =>
            {
                var picker = new EmotePicker(emote => { chatInput.Box.SelectedText = emote; chatInput.Box.Focus(); });
                var at = emoteButton.PointToScreen(new Point(0, 0));
                picker.Location = new Point(at.X - 120, at.Y - picker.Height - 8);
                picker.Show(this);
            };
            gifButton.Click += (s, e) => new GifPicker(gif => session?.SendChat("", gif)).ShowDialog(this);
            chatCard.Controls.AddRange(new Control[] { chatFeed, chatInput, emoteButton, gifButton, sendButton });

            quitButton.SetBounds(356, 742, 100, 34);
            quitButton.Click += (s, e) => Close();
            var journalSession = new FlatButton(L.T("Mon journal"), primary: false) { Location = new Point(24, 740), Size = new Size(160, 38), Picture = Theme.Histo() };
            journalSession.Click += (s, e) => OpenJournal();
            chatHint = Dialogs.Text(L.T("Touche T dans Stremio"), Theme.Small, Theme.Muted, 196, 751);
            restartButton.SetBounds(192, 740, 156, 38);
            restartButton.Click += async (s, e) =>
            {
                if (session == null) return;
                restartButton.Enabled = false;
                try { await session.RestartStremio(); }
                catch (Exception ex) { Program.Log("relance de Stremio : " + ex.Message); }
                restartButton.Enabled = true;
            };
            sessionPanel.Controls.AddRange(new Control[] { Header(), statusCard, inviteCard, adminCard, chatCard, journalSession, chatHint, restartButton, quitButton });
        }

        void SendChat()
        {
            if (session == null) return;
            var text = chatInput.Box.Text.Trim();
            var gif = Chat.GifFromLink(text);
            if (session.SendChat(gif != null ? "" : text, gif)) chatInput.Box.Clear();
        }

        void AutoJoinFromClipboard()
        {
            if (Program.LaunchInvite != null)
            {
                codeBox.Box.Text = Program.LaunchInvite;
                startStatus.Text = L.T("Invitation reçue, connexion au salon…");
                Program.LaunchInvite = null;
                StartJoin();
                return;
            }
            var invite = Rooms.ClipboardInvite();
            if (invite == null) return;
            codeBox.Box.Text = invite.Value.code;
            if (invite.Value.already)
            {
                startStatus.Text = L.T("Code d'une soirée précédente : clique sur Rejoindre si elle est encore en cours.");
                return;
            }
            startStatus.Text = L.T("Code d'invitation trouvé dans le presse-papier, connexion…");
            StartJoin();
        }

        string Pseudo() => string.IsNullOrWhiteSpace(nameBox.Box.Text) ? L.T("Invité") : nameBox.Box.Text.Trim();

        void SetStartBusy(bool busy, string message)
        {
            joinButton.Enabled = hostButton.Enabled = codeBox.Enabled = nameBox.Enabled = !busy;
            startStatus.ForeColor = Theme.Muted;
            startStatus.Text = message;
            reportButton.Visible = false;
            UseWaitCursor = busy;
            PushPanel();
        }

        void ShowError(string message)
        {
            startStatus.ForeColor = Theme.Bad;
            startStatus.Text = message;
            reportButton.Visible = true;
            PushPanel();
            // Erreur pendant que l'on était dans Stremio : la fenêtre revient avec son bouton « Signaler ce problème ».
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; Activate(); }
        }

        async void StartJoin()
        {
            if (!joinButton.Enabled) return;
            // Un code court se vérifie d'abord auprès du serveur : on bloque les boutons le temps de la réponse.
            SetStartBusy(true, startStatus.Text);
            Session s;
            try { s = await Rooms.Join(Pseudo(), codeBox.Box.Text); }
            catch (Exception e) { SetStartBusy(false, ""); ShowError(e.Message); return; }
            ShowSession(s, null, null);
        }

        async void StartHost()
        {
            SetStartBusy(true, L.T("Création du salon et ouverture de l'accès Internet…"));
            var startLog = new Action<string>(line => Ui(() => { startStatus.Text = line; PushPanel(); }));
            Program.Output += startLog;
            try
            {
                var (s, code, shortCode) = await Task.Run(() => Rooms.Host(Pseudo(), local: false));
                Program.Output -= startLog;
                ShowSession(s, code, shortCode);
            }
            catch (Exception e)
            {
                Program.Output -= startLog;
                SetStartBusy(false, "");
                ShowError(e.Message);
            }
        }

        void ShowSession(Session s, string code, string shortCode)
        {
            session = s;
            UseWaitCursor = false;
            startPanel.Visible = false;
            sessionPanel.Visible = true;
            bool host = s.IsHost;
            inviteCard.Visible = host;
            inviteLong = code ?? "";
            inviteBox.Box.Text = shortCode != null ? Invite.ShowShort(shortCode) : inviteLong;
            if (!host)
            {
                int extra = inviteCard.Height + 12;
                adminCard.Top = inviteCard.Top;
                chatCard.Top = adminCard.Bottom + 12;
                chatCard.Height += extra;
                chatFeed.Height += extra;
                foreach (var c in new Control[] { chatInput, emoteButton, gifButton, sendButton }) c.Top += extra;
            }
            adminBox.Enabled = host;
            if (host) adminHint.Text = L.T("Activé : toi seul peux mettre pause, avancer ou changer d'épisode. Les autres suivent.");
            s.StateChanged += () => Ui(RefreshState);
            s.ChatReceived += m => Ui(() => chatFeed.AddMessage(m));
            s.FilmFinished += entry => Ui(() => new RatingForm(entry, e => s.ShareRating(e)).Show(this));
            s.HistoryFound += films => Ui(() => Dialogs.AskImport(this, films));
            RefreshState();
            PushPanel();
            _ = Task.Run(async () =>
            {
                try { await s.Run(); }
                catch (Exception e)
                {
                    if (!Telemetry.AnyErrorSent) Telemetry.Send("error", detail: "crash").Wait(3000);
                    Ui(() => Program.Fail(L.T("Erreur : ") + e.Message));
                }
            });
        }

        // « · Stremio 5 », avec ⚠ si cette version ne comprend pas les liens de torrent de la nôtre.
        string ShellNote(string who)
        {
            var label = Session.ShellLabel(session.ShellOf(who));
            return label == null ? "" : "  · " + label + (session.SameShellFamily(who) ? "" : " ⚠");
        }

        void RefreshState()
        {
            if (session == null) return;
            statusDot.ForeColor = session.Connected && !session.StremioDown ? Theme.Ok : Theme.Bad;
            statusText.Text = session.StremioDown ? L.F("Salon {0} · Stremio fermé", session.Room)
                : session.Connected ? L.F(session.IsHost ? "Salon {0} · tu héberges" : "Salon {0} · connecté", session.Room) : L.F("Salon {0} · connexion…", session.Room);
            if (restartButton.Visible != session.StremioDown)
            {
                restartButton.Visible = session.StremioDown;
                chatHint.Visible = !session.StremioDown;
                // Stremio fermé : on remontre la fenêtre, c'est là qu'est le bouton pour le relancer.
                if (session.StremioDown && WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; Activate(); }
            }
            shownMembers = session.Members.ToArray();
            var lines = shownMembers.Select(n =>
                n + ShellNote(n) + (session.IsLoading(n) ? L.T("   ⏳ chargement…") : "") + (session.IsNotWaited(n) ? L.T("   ⏩ on ne l'attend pas") : "")).ToList();
            if (!membersList.Items.Cast<string>().SequenceEqual(lines))
            {
                membersList.Items.Clear();
                foreach (var l in lines) membersList.Items.Add(l);
            }
            membersList.Cursor = session.IsHost ? Cursors.Hand : Cursors.Default;
            membersCaption.Text = session.IsHost ? L.T("Participants · clique sur un pseudo pour ne plus l'attendre") : L.T("Participants");
            if (!session.IsHost)
            {
                if (adminBox.Checked != session.AdminOn) adminBox.Checked = session.AdminOn;
                adminHint.Text = session.AdminOn
                    ? L.T("Activé par l'hôte : seul l'hôte contrôle la lecture.")
                    : L.T("Désactivé : tout le monde peut mettre pause, avancer ou changer d'épisode.");
            }
            PushPanel();
        }

        static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        static async void FlashText(Control c, string text)
        {
            var old = c.Text;
            c.Text = text;
            await Task.Delay(1500);
            if (!c.IsDisposed) c.Text = old;
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { }
        }
    }
}
