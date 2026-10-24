using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace StremioWT
{
    // Fenêtre principale de l'édition Linux : accueil (pseudo, rejoindre, héberger) puis salon (participants, code, chat).
    class MainWindow : Window
    {
        readonly ContentControl screen = new ContentControl();
        readonly TextBox nameBox = new TextBox { MaxLength = 32 };
        readonly TextBox codeBox = new TextBox { Watermark = "WT-…" };
        readonly ToggleSwitch localBox = new ToggleSwitch();
        readonly TextBlock startStatus = Muted("");
        // Affiché sous une erreur de l'accueil : ouvre le rapport de bug avec ce message d'erreur.
        readonly Button reportButton = new Button { Content = "🐞  " + L.T("Signaler ce problème"), IsVisible = false, Padding = new Thickness(12, 7) };
        readonly Button joinButton, hostButton;

        // Salon
        Session session;
        readonly TextBlock statusText = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) };
        readonly Avalonia.Controls.Shapes.Ellipse statusDot = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = Palette.Brush(Palette.Muted), VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel members = new StackPanel { Spacing = 2 };
        readonly TextBlock membersCaption = Muted(L.T("Participants"));
        readonly TextBox inviteBox = new TextBox { IsReadOnly = true };
        readonly ToggleSwitch adminBox = new ToggleSwitch();
        readonly TextBlock adminHint = Muted("");
        readonly StackPanel chatFeed = new StackPanel { Spacing = 6 };
        readonly ScrollViewer chatScroll = new ScrollViewer { Height = 260 };
        readonly TextBox chatInput = new TextBox { MaxLength = Chat.MaxText };

        public MainWindow()
        {
            Title = "Stremio Watch Together";
            Width = 500; Height = 860; MinWidth = 460; MinHeight = 640;
            Background = Palette.Brush(Palette.Background);
            FlowDirection = L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            try { Icon = new WindowIcon(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")); } catch { }
            joinButton = Primary(L.T("Rejoindre"));
            hostButton = Secondary(L.T("Héberger un salon"));
            Content = screen;
            screen.Content = BuildStart();
            Opened += async (s, e) =>
            {
                if (Program.LaunchInvite != null)
                {
                    codeBox.Text = Program.LaunchInvite;
                    startStatus.Text = L.T("Invitation reçue, connexion au salon…");
                    Program.LaunchInvite = null;
                    StartJoin();
                    return;
                }
                try { Program.ClipboardSnapshot = await Clipboard.GetTextAsync() ?? ""; } catch { }
                var invite = Rooms.InviteFromClipboard();
                if (invite != null)
                {
                    codeBox.Text = Program.ClipboardSnapshot.Trim();
                    if (Rooms.AlreadyJoined(invite.Value))
                    {
                        startStatus.Text = L.T("Code d'une soirée précédente : clique sur Rejoindre si elle est encore en cours.");
                        return;
                    }
                    startStatus.Text = L.T("Code d'invitation trouvé dans le presse-papier, connexion…");
                    StartJoin();
                }
            };
            Closing += (s, e) =>
            {
                var current = session;
                if (current != null) Task.WhenAll(Task.Run(() => current.ReleaseStremio()), current.ReportEnd()).Wait(5000);
            };
        }

        // --- Petits éléments de style ---------------------------------------------------------------
        internal static TextBlock Muted(string text) => new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Brush(Palette.Muted), TextWrapping = TextWrapping.Wrap };
        internal static TextBlock Heading(string text) => new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) };
        internal static Button Primary(string text) => new Button { Content = text, Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
        internal static Button Secondary(string text) => new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
        internal static Border Card(params Control[] children)
        {
            var panel = new StackPanel { Spacing = 10 };
            foreach (var c in children) panel.Children.Add(c);
            return new Border { Background = Palette.Brush(Palette.Card), BorderBrush = Palette.Brush(Palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14), Child = panel };
        }

        Control Header()
        {
            var logo = new Image { Width = 56, Height = 56 };
            try { logo.Source = new Bitmap(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")); } catch { }
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = "Watch Together", FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            titles.Children.Add(Muted(L.F("Regarder Stremio ensemble, synchronisés · v{0}", Program.ProtocolVersion) + (Program.IsMac ? " · Mac beta" : " · Linux beta")));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(logo); row.Children.Add(titles);
            return row;
        }

        static ScrollViewer Page(params Control[] children)
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18) };
            foreach (var c in children) panel.Children.Add(c);
            return new ScrollViewer { Content = panel };
        }

        // --- Accueil --------------------------------------------------------------------------------
        Control BuildStart()
        {
            nameBox.Text = Rooms.DefaultName();
            localBox.Content = Muted(L.T("Option : amis sur le même wifi seulement (sans Internet)"));
            joinButton.Click += (s, e) => StartJoin();
            hostButton.Click += (s, e) => StartHost();
            codeBox.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Enter) StartJoin(); };

            var update = Primary("");
            update.IsVisible = false;
            update.Click += async (s, e) =>
            {
                // Mise à jour dans l'appli ; en cas d'échec, la page de téléchargement s'ouvre comme avant.
                update.IsEnabled = false;
                bool ok = await Updates.Install(p => Dispatcher.UIThread.Post(() => update.Content = L.F("Mise à jour… {0} %", p)));
                if (ok) { Program.Restart(); return; }
                update.IsEnabled = true;
                update.Content = L.T("Mise à jour impossible : la page de téléchargement s'ouvre.");
                OpenUrl(Updates.DownloadUrl);
            };
            Opened += (s, e) => OpenDiaryIfAsked();
            Opened += async (s, e) =>
            {
                var updated = Updates.AfterUpdate();
                if (updated != null && string.IsNullOrEmpty(startStatus.Text)) startStatus.Text = L.F("Mis à jour vers la version {0} ✓", updated);
                if (Updates.WhatsNewPending(updated)) await new WhatsNewWindow(Updates.CurrentVersion).ShowDialog(this);
            };
            Opened += async (s, e) =>
            {
                var version = await Updates.NewerVersion();
                if (version != null) { update.Content = L.F("Nouvelle version {0} disponible : mettre à jour", version); update.IsVisible = true; }
            };
            var settings = Secondary("⚙  " + L.T("Paramètres"));
            settings.Click += async (s, e) => await new SettingsWindow().ShowDialog(this);
            reportButton.Click += async (s, e) => await new ReportWindow(startStatus.Text).ShowDialog(this);

            return Page(Header(),
                Card(Heading(L.T("Ton pseudo")), nameBox),
                Card(Heading(L.T("Rejoindre un salon")), Muted(L.T("Colle le code d'invitation reçu (WT-…)")), codeBox, joinButton),
                Card(Heading(L.T("Héberger un salon")), hostButton, localBox),
                startStatus, reportButton, update, DiaryButton(), settings);
        }

        string Pseudo() => string.IsNullOrWhiteSpace(nameBox.Text) ? L.T("Invité") : nameBox.Text.Trim();

        void SetBusy(bool busy, string message)
        {
            joinButton.IsEnabled = hostButton.IsEnabled = nameBox.IsEnabled = codeBox.IsEnabled = localBox.IsEnabled = !busy;
            startStatus.Foreground = Palette.Brush(Palette.Muted);
            startStatus.Text = message;
            reportButton.IsVisible = false;
        }

        void ShowError(string message)
        {
            SetBusy(false, "");
            startStatus.Foreground = Palette.Brush(Palette.Bad);
            startStatus.Text = message;
            reportButton.IsVisible = true;
        }

        void StartJoin()
        {
            if (!joinButton.IsEnabled) return;
            Session s;
            try { s = Rooms.Join(Pseudo(), codeBox.Text ?? ""); }
            catch (Exception e) { ShowError(e.Message); return; }
            ShowSession(s, null);
        }

        async void StartHost()
        {
            SetBusy(true, localBox.IsChecked == true ? L.T("Création du salon…") : L.T("Création du salon et ouverture de l'accès Internet…"));
            Action<string> progress = line => Dispatcher.UIThread.Post(() => startStatus.Text = line);
            Program.Output += progress;
            try
            {
                // Lu ici : les contrôles ne se lisent que depuis le fil de l'interface.
                bool local = localBox.IsChecked == true;
                var name = Pseudo();
                var (s, code) = await Task.Run(() => Rooms.Host(name, local));
                ShowSession(s, code);
            }
            catch (Exception e) { ShowError(e.Message); }
            finally { Program.Output -= progress; }
        }

        // --- Salon ----------------------------------------------------------------------------------
        void ShowSession(Session s, string code)
        {
            session = s;
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            statusRow.Children.Add(statusDot); statusRow.Children.Add(statusText);
            var statusCard = Card(statusRow, membersCaption, members);

            Control inviteCard = null;
            if (s.IsHost)
            {
                inviteBox.Text = code ?? "";
                // Un seul bouton, sans ambiguïté : c'est une invitation (page de téléchargement + entrée dans le salon).
                var copy = Primary("📋  " + L.T("Copier le lien d'invitation"));
                copy.Click += async (o, e) => { await Clipboard.SetTextAsync(Invite.Link(inviteBox.Text)); Flash(copy, L.T("Lien copié ✓")); };
                // Le code seul, pour ceux qui ont déjà l'appli et préfèrent le coller.
                var copyCode = new Button { Content = L.T("Copier le code"), Padding = new Thickness(12, 9) };
                copyCode.Click += async (o, e) => { await Clipboard.SetTextAsync(inviteBox.Text); Flash(copyCode, L.T("Code copié ✓")); };
                // Le code (pour ceux qui ont déjà l'appli), puis le lien d'invitation, qui sert aussi aux autres.
                var codeRow = new DockPanel();
                DockPanel.SetDock(copyCode, Dock.Right);
                copyCode.Margin = new Thickness(8, 0, 0, 0);
                codeRow.Children.Add(copyCode); codeRow.Children.Add(inviteBox);
                inviteCard = Card(Heading(L.T("Inviter des amis")), codeRow, copy, Muted(L.T("Le lien fait aussi télécharger StremioWT aux amis qui ne l'ont pas.")));
            }

            adminBox.Content = new TextBlock { Text = L.T("Hébergeur admin"), FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) };
            // Bêta Linux : l'hôte garde toujours la main (les actions des autres ne s'appliquent pas encore bien ici).
            if (s.IsHost) s.SetAdmin(true);
            adminBox.IsChecked = s.IsHost || s.AdminOn;
            adminBox.IsEnabled = false;
            if (s.IsHost) adminHint.Text = L.T("Toujours activé sur Linux (bêta) : toi seul peux mettre pause, avancer ou changer d'épisode. Les autres suivent.");

            chatScroll.Content = chatFeed;
            chatInput.KeyDown += (o, e) => { if (e.Key == Avalonia.Input.Key.Enter) SendChat(); };
            var send = new Button { Content = L.T("Envoyer"), Classes = { "accent" }, Padding = new Thickness(14, 8) };
            send.Click += (o, e) => SendChat();
            var emotes = new Button { Content = "😊", Padding = new Thickness(10, 8), Flyout = EmoteFlyout() };
            var gif = new Button { Content = "GIF", Padding = new Thickness(10, 8) };
            gif.Click += async (o, e) => { var chosen = await new GifWindow().ShowDialog<string>(this); if (chosen != null) session?.SendChat("", chosen); };
            var inputRow = new DockPanel();
            foreach (var b in new Control[] { send, gif, emotes }) { DockPanel.SetDock(b, Dock.Right); b.Margin = new Thickness(6, 0, 0, 0); inputRow.Children.Add(b); }
            inputRow.Children.Add(chatInput);
            var chatCard = Card(Heading(L.T("Chat")), chatScroll, inputRow, Muted(L.T("Touche T dans Stremio")));

            var quit = Secondary(L.T("Quitter"));
            quit.Click += (o, e) => Close();
            var parts = new List<Control> { Header(), statusCard };
            if (inviteCard != null) parts.Add(inviteCard);
            parts.Add(Card(adminBox, adminHint));
            parts.Add(chatCard);
            parts.Add(DiaryButton());
            parts.Add(quit);
            screen.Content = Page(parts.ToArray());

            s.StateChanged += () => Dispatcher.UIThread.Post(RefreshState);
            // Journal : noter le film à la fin, et proposer d'importer l'historique Stremio à l'arrivée.
            s.FilmFinished += entry => Dispatcher.UIThread.Post(() => new RatingWindow(entry, e => s.ShareRating(e)).Show(this));
            s.HistoryFound += films => Dispatcher.UIThread.Post(async () => await Diary.AskImport(this, films));
            s.ChatReceived += m => Dispatcher.UIThread.Post(() => AddChat(m));
            RefreshState();
            _ = Task.Run(async () =>
            {
                try { await s.Run(); }
                catch (Exception e)
                {
                    if (!Telemetry.AnyErrorSent) await Telemetry.Send("error", detail: "crash");
                    Program.Fail(L.T("Erreur : ") + e.Message);
                }
            });
        }

        void RefreshState()
        {
            if (session == null) return;
            statusDot.Fill = Palette.Brush(session.Connected ? Palette.Ok : Palette.Bad);
            statusText.Text = session.Connected ? L.F(session.IsHost ? "Salon {0} · tu héberges" : "Salon {0} · connecté", session.Room) : L.F("Salon {0} · connexion…", session.Room);
            membersCaption.Text = session.IsHost ? L.T("Participants · clique sur un pseudo pour ne plus l'attendre") : L.T("Participants");
            members.Children.Clear();
            foreach (var name in session.Members.ToArray())
            {
                var shell = Session.ShellLabel(session.ShellOf(name));
                var line = name + (shell == null ? "" : "  · " + shell + (session.SameShellFamily(name) ? "" : " ⚠")) + (session.IsLoading(name) ? L.T("   ⏳ chargement…") : "") + (session.IsNotWaited(name) ? L.T("   ⏩ on ne l'attend pas") : "");
                var label = new TextBlock { Text = line, Foreground = Palette.Brush(Palette.Text), Cursor = session.IsHost ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null };
                var who = name;
                if (session.IsHost) label.PointerPressed += (o, e) => session.ToggleDontWait(who);
                members.Children.Add(label);
            }
            if (!session.IsHost)
            {
                if (adminBox.IsChecked != session.AdminOn) adminBox.IsChecked = session.AdminOn;
                adminHint.Text = session.AdminOn ? L.T("Activé par l'hôte : seul l'hôte contrôle la lecture.") : L.T("Désactivé : tout le monde peut mettre pause, avancer ou changer d'épisode.");
            }
        }

        void SendChat()
        {
            if (session == null) return;
            var text = (chatInput.Text ?? "").Trim();
            var gif = Chat.GifFromLink(text);
            if (session.SendChat(gif != null ? "" : text, gif)) chatInput.Text = "";
        }

        static readonly Color[] NameColors =
        {
            Color.FromRgb(84, 152, 255), Color.FromRgb(61, 220, 132), Color.FromRgb(255, 170, 70), Color.FromRgb(230, 110, 220),
            Color.FromRgb(90, 210, 220), Color.FromRgb(255, 110, 110), Color.FromRgb(190, 160, 255), Color.FromRgb(240, 220, 90),
        };

        void AddChat(ChatMessage m)
        {
            var who = m.Mine ? L.T("Toi") : m.From;
            var color = m.Mine ? Palette.Muted : NameColors[Math.Abs((who ?? "").Aggregate(17, (h, c) => h * 31 + c)) % NameColors.Length];
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = who, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(color) });
            if (!string.IsNullOrEmpty(m.Text)) row.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            if (m.Gif != null)
            {
                var img = new Image { MaxHeight = 120, MaxWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
                row.Children.Add(img);
                _ = LoadImage(img, m.Gif);
            }
            chatFeed.Children.Add(row);
            while (chatFeed.Children.Count > 120) chatFeed.Children.RemoveAt(0);
            Dispatcher.UIThread.Post(() => chatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        // Avalonia n'anime pas les GIF : la première image s'affiche.
        internal static async Task LoadImage(Image target, string url)
        {
            if (!Chat.IsAllowedGif(url)) return;
            try
            {
                var bytes = await Program.Http.GetByteArrayAsync(url);
                var bitmap = new Bitmap(new MemoryStream(bytes));
                Dispatcher.UIThread.Post(() => target.Source = bitmap);
            }
            catch { }
        }

        Flyout EmoteFlyout()
        {
            var grid = new WrapPanel { Width = 330 };
            var flyout = new Flyout { Content = grid };
            foreach (var emote in Chat.Emotes)
            {
                var b = new Button { Content = emote, FontSize = 20, Padding = new Thickness(6, 2), Background = Brushes.Transparent };
                b.Click += (o, e) => { chatInput.Text = (chatInput.Text ?? "") + emote; flyout.Hide(); chatInput.Focus(); };
                grid.Children.Add(b);
            }
            return flyout;
        }

        static async void Flash(Button b, string text)
        {
            var old = b.Content;
            b.Content = text;
            await Task.Delay(1500);
            b.Content = old;
        }

        // « stremiowt --diary » (raccourci du menu) ouvre directement le journal.
        void OpenDiaryIfAsked()
        {
            if (Environment.GetCommandLineArgs().Contains("--diary")) _ = new Diary(entry => session?.ShareRating(entry)).ShowDialog(this);
        }

        Button DiaryButton()
        {
            var b = Secondary("📖  " + L.T("Mon journal"));
            b.Click += async (o, e) => await new Diary(entry => session?.ShareRating(entry)).ShowDialog(this);
            return b;
        }

        internal static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(Program.IsMac ? "open" : "xdg-open", url) { UseShellExecute = false }); } catch { } }
    }
}
