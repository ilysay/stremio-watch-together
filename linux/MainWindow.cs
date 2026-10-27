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
    partial class MainWindow : Window
    {
        readonly ContentControl screen = new ContentControl();
        readonly TextBox nameBox = new TextBox { MaxLength = 32 };
        readonly TextBox codeBox = new TextBox { Watermark = "WT-…" };
        readonly TextBlock startStatus = Muted("");
        // Affiché sous une erreur de l'accueil : ouvre le rapport de bug avec ce message d'erreur.
        readonly Button reportButton = new Button { Content = Icons.Label("alert", L.T("Signaler ce problème")), IsVisible = false, Padding = new Thickness(14, 8) };
        readonly Button joinButton, hostButton;

        // Salon
        Session session;
        readonly TextBlock statusText = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) };
        readonly Avalonia.Controls.Shapes.Ellipse statusDot = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = Palette.Brush(Palette.Muted), VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel members = new StackPanel { Spacing = 4 };
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
            joinButton = Primary(L.T("Rejoindre"), "login");
            hostButton = Secondary(L.T("Héberger un salon"), "play");
            nameBox.InnerLeftContent = FieldIcon("user");
            codeBox.InnerLeftContent = FieldIcon("link");
            chatInput.InnerLeftContent = FieldIcon("chat");
            // Halo violet en haut de la fenêtre, derrière le contenu (comme l'éclairage de Stremio).
            var glow = new Avalonia.Controls.Shapes.Ellipse { Width = 900, Height = 560, Margin = new Thickness(0, -340, 0, 0), IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(80, 123, 91, 245), 0), new GradientStop(Color.FromArgb(28, 123, 91, 245), 0.55), new GradientStop(Color.FromArgb(0, 123, 91, 245), 1) } } };
            var root = new Grid { ClipToBounds = true, Children = { glow, screen } };
            // Mac : la barre de titre devient transparente (les boutons ronds restent), le fond passe dessous.
            // Une bande en haut sert de poignée pour déplacer la fenêtre.
            if (Program.IsMac)
            {
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
                var grip = new Border { Height = TitleBarHeight, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
                grip.PointerPressed += (s, e) => BeginMoveDrag(e);
                root.Children.Add(grip);
            }
            Content = root;
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
                var invite = Rooms.ClipboardInvite();
                if (invite != null)
                {
                    codeBox.Text = invite.Value.code;
                    if (invite.Value.already)
                    {
                        startStatus.Text = L.T("Code d'une soirée précédente : clique sur Rejoindre si elle est encore en cours.");
                        return;
                    }
                    startStatus.Text = L.T("Code d'invitation trouvé dans le presse-papier, connexion…");
                    StartJoin();
                }
            };
            Opened += (s, e) => StartPanel();
            Closing += (s, e) =>
            {
                var current = session;
                if (current != null) Task.WhenAll(Task.Run(() => current.ReleaseStremio()), current.ReportEnd()).Wait(5000);
            };
        }

        const int TitleBarHeight = 28;

        // --- Petits éléments de style ---------------------------------------------------------------
        internal static TextBlock Muted(string text) => new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Brush(Palette.Muted), TextWrapping = TextWrapping.Wrap };
        internal static TextBlock Heading(string text) => new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text), VerticalAlignment = VerticalAlignment.Center };
        // Titre de carte précédé de son icône violette.
        internal static Control Heading(string text, string icon)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(Icons.Make(icon, 17, Palette.Brush(Palette.Accent)));
            row.Children.Add(Heading(text));
            return row;
        }
        static Control FieldIcon(string icon) { var i = Icons.Make(icon, 16, Palette.Brush(Palette.Muted)); i.Margin = new Thickness(12, 0, 0, 0); return i; }
        internal static Button Primary(string text, string icon = null) => new Button { Content = Icons.Label(icon, text), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 11) };
        internal static Button Secondary(string text, string icon = null) => new Button { Content = Icons.Label(icon, text), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 11) };
        internal static Border Card(params Control[] children)
        {
            var panel = new StackPanel { Spacing = 10 };
            foreach (var c in children) panel.Children.Add(c);
            return new Border { Background = Palette.Brush(Palette.CardGlass), BorderBrush = Palette.Brush(Palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14), Child = panel };
        }

        Control Header()
        {
            var logo = new Image { Width = 56, Height = 56 };
            try { logo.Source = new Bitmap(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")); } catch { }
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            titles.Children.Add(new TextBlock { Text = "Watch Together", FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            titles.Children.Add(Muted(L.F("Regarder Stremio ensemble, synchronisés · v{0}", Program.DisplayVersion) + (Program.IsMac ? " · Mac beta" : " · Linux beta")));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(logo); row.Children.Add(titles);
            return row;
        }

        static ScrollViewer Page(params Control[] children)
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18 + (Program.IsMac ? TitleBarHeight : 0), 22, 18) };
            foreach (var c in children) panel.Children.Add(c);
            return new ScrollViewer { Content = panel };
        }

        // --- Accueil --------------------------------------------------------------------------------
        Control BuildStart()
        {
            nameBox.Text = Rooms.DefaultName();
            joinButton.Click += (s, e) => StartJoin();
            hostButton.Click += (s, e) => StartHost();
            codeBox.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Enter) StartJoin(); };

            var update = Primary("", "download");
            update.IsVisible = false;
            update.Click += async (s, e) =>
            {
                // Mise à jour dans l'appli ; en cas d'échec, la page de téléchargement s'ouvre comme avant.
                update.IsEnabled = false;
                bool ok = await Updates.Install(p => Dispatcher.UIThread.Post(() => update.Content = Icons.Label("download", L.F("Mise à jour… {0} %", p))));
                if (ok) { Program.Restart(); return; }
                update.IsEnabled = true;
                update.Content = Icons.Label("download", L.T("Mise à jour impossible : la page de téléchargement s'ouvre."));
                OpenUrl(Updates.DownloadUrl);
            };
            Opened += (s, e) => OpenDiaryIfAsked();
            Opened += async (s, e) =>
            {
                var updated = Updates.AfterUpdate();
                if (updated != null && string.IsNullOrEmpty(startStatus.Text)) startStatus.Text = L.F("Mis à jour vers la version {0} ✓", updated);
                if (Updates.WhatsNewPending(updated)) await new WhatsNewWindow(Program.DisplayVersion).ShowDialog(this);
            };
            Opened += async (s, e) =>
            {
                var version = await Updates.NewerVersion();
                if (version != null) { update.Content = Icons.Label("download", L.F("Nouvelle version {0} disponible : mettre à jour", version)); update.IsVisible = true; }
            };
            var settings = Secondary(L.T("Paramètres"), "settings");
            settings.Click += async (s, e) => await new SettingsWindow().ShowDialog(this);
            reportButton.Click += async (s, e) => await new ReportWindow(startStatus.Text).ShowDialog(this);

            return Page(Header(),
                Card(Heading(L.T("Ton pseudo"), "user"), nameBox),
                Card(Heading(L.T("Rejoindre un salon"), "login"), Muted(L.T("Colle ou tape le code reçu (8 caractères ou WT-…)")), codeBox, joinButton),
                Card(Heading(L.T("Héberger un salon"), "play"), hostButton),
                startStatus, reportButton, update, DiaryButton(), settings);
        }

        string Pseudo() => string.IsNullOrWhiteSpace(nameBox.Text) ? L.T("Invité") : nameBox.Text.Trim();

        void SetBusy(bool busy, string message)
        {
            joinButton.IsEnabled = hostButton.IsEnabled = nameBox.IsEnabled = codeBox.IsEnabled = !busy;
            startStatus.Foreground = Palette.Brush(Palette.Muted);
            startStatus.Text = message;
            startError = false;
            reportButton.IsVisible = false;
            PushPanel();
        }

        void ShowError(string message)
        {
            SetBusy(false, "");
            startStatus.Foreground = Palette.Brush(Palette.Bad);
            startStatus.Text = message;
            startError = true;
            reportButton.IsVisible = true;
            PushPanel();
            if (WindowState == WindowState.Minimized) ShowWindow();
        }

        async void StartJoin()
        {
            if (!joinButton.IsEnabled) return;
            // Un code court se vérifie d'abord auprès du serveur : on bloque les boutons le temps de la réponse.
            SetBusy(true, startStatus.Text);
            Session s;
            try { s = await Rooms.Join(Pseudo(), codeBox.Text ?? ""); }
            catch (Exception e) { ShowError(e.Message); return; }
            ShowSession(s, null, null);
        }

        async void StartHost()
        {
            SetBusy(true, L.T("Création du salon et ouverture de l'accès Internet…"));
            Action<string> progress = line => Dispatcher.UIThread.Post(() => { startStatus.Text = line; PushPanel(); });
            Program.Output += progress;
            try
            {
                // Lu ici : les contrôles ne se lisent que depuis le fil de l'interface.
                var name = Pseudo();
                var (s, code, shortCode) = await Task.Run(() => Rooms.Host(name, local: false));
                ShowSession(s, code, shortCode);
            }
            catch (Exception e) { ShowError(e.Message); }
            finally { Program.Output -= progress; }
        }

        // --- Salon ----------------------------------------------------------------------------------
        // Code long du salon : sert au lien d'invitation, même quand la case affiche le code court.
        string inviteLong = "";
        // Stremio fermé pendant la séance : le salon reste ouvert, ce bouton le relance.
        Button restartButton;

        void ShowSession(Session s, string code, string shortCode)
        {
            session = s;
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            statusRow.Children.Add(statusDot); statusRow.Children.Add(statusText);
            var statusCard = Card(statusRow, membersCaption, members);

            Control inviteCard = null;
            if (s.IsHost)
            {
                inviteLong = code ?? "";
                inviteBox.Text = shortCode != null ? Invite.ShowShort(shortCode) : inviteLong;
                // Un seul bouton, sans ambiguïté : c'est une invitation (page de téléchargement + entrée dans le salon).
                var copy = Primary(L.T("Copier le lien d'invitation"), "link");
                copy.Click += async (o, e) => { await Clipboard.SetTextAsync(Invite.Link(inviteLong)); Flash(copy, L.T("Lien copié ✓")); };
                // Le code seul, pour ceux qui ont déjà l'appli et préfèrent le coller.
                var copyCode = new Button { Content = Icons.Label("copy", L.T("Copier le code")), Padding = new Thickness(14, 9) };
                copyCode.Click += async (o, e) => { await Clipboard.SetTextAsync(inviteBox.Text); Flash(copyCode, L.T("Code copié ✓")); };
                // Le code (pour ceux qui ont déjà l'appli), puis le lien d'invitation, qui sert aussi aux autres.
                var codeRow = new DockPanel();
                DockPanel.SetDock(copyCode, Dock.Right);
                copyCode.Margin = new Thickness(8, 0, 0, 0);
                codeRow.Children.Add(copyCode); codeRow.Children.Add(inviteBox);
                inviteCard = Card(Heading(L.T("Inviter des amis"), "link"), codeRow, copy, Muted(L.T("Le lien fait aussi télécharger StremioWT aux amis qui ne l'ont pas.")));
            }

            SwitchLabel(adminBox, () => IconText("crown", new TextBlock { Text = L.T("Hébergeur admin"), FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) }));
            // Bêta Linux : l'hôte garde toujours la main (les actions des autres ne s'appliquent pas encore bien ici).
            if (s.IsHost) s.SetAdmin(true);
            adminBox.IsChecked = s.IsHost || s.AdminOn;
            adminBox.IsEnabled = false;
            if (s.IsHost) adminHint.Text = L.T("Toujours activé sur Linux (bêta) : toi seul peux mettre pause, avancer ou changer d'épisode. Les autres suivent.");

            chatScroll.Content = chatFeed;
            chatInput.KeyDown += (o, e) => { if (e.Key == Avalonia.Input.Key.Enter) SendChat(); };
            var send = new Button { Content = Icons.Make("send", 16), Classes = { "accent" }, Padding = new Thickness(13, 10) };
            ToolTip.SetTip(send, L.T("Envoyer"));
            send.Click += (o, e) => SendChat();
            var emotes = new Button { Content = Icons.Make("smile", 17), Padding = new Thickness(12, 10), Flyout = EmoteFlyout() };
            var gif = new Button { Content = "GIF", Padding = new Thickness(14, 10) };
            gif.Click += async (o, e) => { var chosen = await new GifWindow().ShowDialog<string>(this); if (chosen != null) session?.SendChat("", chosen); };
            var inputRow = new DockPanel();
            foreach (var b in new Control[] { send, gif, emotes }) { DockPanel.SetDock(b, Dock.Right); b.Margin = new Thickness(6, 0, 0, 0); inputRow.Children.Add(b); }
            inputRow.Children.Add(chatInput);
            var chatCard = Card(Heading(L.T("Chat"), "chat"), chatScroll, inputRow, Muted(L.T("Touche T dans Stremio")));

            var quit = Secondary(L.T("Quitter"), "leave");
            quit.Classes.Add("danger");
            quit.Click += (o, e) => Close();
            var parts = new List<Control> { Header(), statusCard };
            if (inviteCard != null) parts.Add(inviteCard);
            parts.Add(Card(adminBox, adminHint));
            parts.Add(chatCard);
            restartButton = Primary(L.T("Relancer Stremio"), "play");
            restartButton.IsVisible = false;
            restartButton.Click += async (o, e) =>
            {
                restartButton.IsEnabled = false;
                try { await s.RestartStremio(); }
                catch (Exception ex) { Program.Log("relance de Stremio : " + ex.Message); }
                restartButton.IsEnabled = true;
            };
            parts.Add(restartButton);
            parts.Add(DiaryButton());
            parts.Add(quit);
            screen.Content = Page(parts.ToArray());

            s.StateChanged += () => Dispatcher.UIThread.Post(RefreshState);
            // Journal : noter le film à la fin, et proposer d'importer l'historique Stremio à l'arrivée.
            s.FilmFinished += entry => Dispatcher.UIThread.Post(() => new RatingWindow(entry, e => s.ShareRating(e)).Show(this));
            s.HistoryFound += films => Dispatcher.UIThread.Post(async () => await Diary.AskImport(this, films));
            s.ChatReceived += m => Dispatcher.UIThread.Post(() => AddChat(m));
            RefreshState();
            PushPanel();
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
            statusDot.Fill = Palette.Brush(session.Connected && !session.StremioDown ? Palette.Ok : Palette.Bad);
            statusText.Text = session.StremioDown ? L.F("Salon {0} · Stremio fermé", session.Room)
                : session.Connected ? L.F(session.IsHost ? "Salon {0} · tu héberges" : "Salon {0} · connecté", session.Room) : L.F("Salon {0} · connexion…", session.Room);
            if (restartButton != null && restartButton.IsVisible != session.StremioDown)
            {
                restartButton.IsVisible = session.StremioDown;
                // Stremio fermé : on remontre la fenêtre, c'est là qu'est le bouton pour le relancer.
                if (session.StremioDown && WindowState == WindowState.Minimized) ShowWindow();
            }
            membersCaption.Text = session.IsHost ? L.T("Participants · clique sur un pseudo pour ne plus l'attendre") : L.T("Participants");
            members.Children.Clear();
            foreach (var name in session.Members.ToArray())
            {
                var line = name + ShellNote(name) + (session.IsLoading(name) ? L.T("   ⏳ chargement…") : "") + (session.IsNotWaited(name) ? L.T("   ⏩ on ne l'attend pas") : "");
                var label = MemberRow(name, line.Substring(name.Length).Trim().TrimStart('·', ' '));
                label.Cursor = session.IsHost ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null;
                var who = name;
                if (session.IsHost) label.PointerPressed += (o, e) => session.ToggleDontWait(who);
                members.Children.Add(label);
            }
            if (!session.IsHost)
            {
                if (adminBox.IsChecked != session.AdminOn) adminBox.IsChecked = session.AdminOn;
                adminHint.Text = session.AdminOn ? L.T("Activé par l'hôte : seul l'hôte contrôle la lecture.") : L.T("Désactivé : tout le monde peut mettre pause, avancer ou changer d'épisode.");
            }
            PushPanel();
        }

        // Libellé à droite de l'interrupteur (au lieu de « On / Off » et d'un titre au-dessus), comme sur Windows.
        internal static void SwitchLabel(ToggleSwitch t, Func<Control> label)
        {
            t.Content = null;
            t.OnContent = label();
            t.OffContent = label();
        }

        static Control IconText(string icon, Control text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(Icons.Make(icon, 16, Palette.Brush(Palette.Muted)));
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }

        static readonly Color[] AvatarColors =
        {
            Color.FromRgb(123, 91, 245), Color.FromRgb(34, 179, 101), Color.FromRgb(232, 86, 155), Color.FromRgb(245, 158, 11),
            Color.FromRgb(59, 130, 246), Color.FromRgb(20, 184, 166), Color.FromRgb(239, 68, 68), Color.FromRgb(168, 85, 247),
        };

        static Control MemberRow(string name, string note)
        {
            var avatar = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                Background = Palette.Brush(AvatarColors[Math.Abs(name.Aggregate(17, (h, c) => h * 31 + c)) % AvatarColors.Length]),
                Child = new TextBlock { Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?", FontWeight = FontWeight.Bold, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Background = Brushes.Transparent, Margin = new Thickness(0, 2) };
            row.Children.Add(avatar);
            row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text), VerticalAlignment = VerticalAlignment.Center });
            if (note.Length > 0) row.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Palette.Brush(Palette.Muted), VerticalAlignment = VerticalAlignment.Center });
            return row;
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
                _ = LoadImage(img, m.Gif, animate: true);
            }
            chatFeed.Children.Add(row);
            while (chatFeed.Children.Count > 120) chatFeed.Children.RemoveAt(0);
            Dispatcher.UIThread.Post(() => chatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        // Avalonia n'anime pas les GIF : dans le chat, GifPlayer les fait défiler ; ailleurs, la première image s'affiche.
        internal static async Task LoadImage(Image target, string url, bool animate = false)
        {
            if (!Chat.IsAllowedGif(url)) return;
            try
            {
                var bytes = await Program.Http.GetByteArrayAsync(url);
                if (animate)
                {
                    var anim = await Task.Run(() => { try { return GifPlayer.Decode(bytes); } catch { return null; } });
                    if (anim != null) { Dispatcher.UIThread.Post(() => GifPlayer.Play(target, anim.Value.Frames, anim.Value.Delays)); return; }
                }
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
            var b = Secondary(L.T("Mon journal"));
            // Icône en couleur du journal, comme sur Windows.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            try { row.Children.Add(new Image { Width = 24, Height = 24, Source = new Bitmap(Assembly.GetExecutingAssembly().GetManifestResourceStream("histo.png")) }); } catch { }
            row.Children.Add(new TextBlock { Text = L.T("Mon journal"), VerticalAlignment = VerticalAlignment.Center });
            b.Content = row;
            b.Click += async (o, e) => await new Diary(entry => session?.ShareRating(entry)).ShowDialog(this);
            return b;
        }

        internal static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(Program.IsMac ? "open" : "xdg-open", url) { UseShellExecute = false }); } catch { } }
    }
}
