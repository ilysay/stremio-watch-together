using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace StremioWT
{
    static class Dialogs
    {
        // Message simple avec un bouton OK (Avalonia n'a pas de MessageBox).
        public static Task Message(Window owner, string text)
        {
            var ok = new Button { Content = "OK", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 7) };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(ok);
            var w = new Window { Title = "Stremio Watch Together", Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Background = Palette.Brush(Palette.Background), Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            ok.Click += (s, e) => w.Close();
            return owner != null && owner.IsVisible ? w.ShowDialog(owner) : ShowAlone(w);
        }

        // Message d'erreur avec « Envoyer un rapport » : renvoie true si l'utilisateur veut signaler le problème.
        public static Task<bool> ErrorWithReport(Window owner, string text)
        {
            var result = new TaskCompletionSource<bool>();
            var report = new Button { Content = Icons.Label("alert", L.T("Signaler ce problème")), Padding = new Thickness(14, 8) };
            var ok = new Button { Content = "OK", Classes = { "accent" }, Padding = new Thickness(18, 7) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { report, ok } };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(buttons);
            var w = new Window { Title = "Stremio Watch Together", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, Background = Palette.Brush(Palette.Background), Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            report.Click += (s, e) => { result.TrySetResult(true); w.Close(); };
            ok.Click += (s, e) => w.Close();
            w.Closed += (s, e) => result.TrySetResult(false);
            if (owner != null && owner.IsVisible) w.ShowDialog(owner); else w.Show();
            return result.Task;
        }

        // Question Oui / Non (Avalonia n'a pas de MessageBox) : renvoie true pour Oui.
        public static Task<bool> Confirm(Window owner, string text)
        {
            var result = new TaskCompletionSource<bool>();
            var yes = new Button { Content = L.T("Oui"), Classes = { "accent" }, Padding = new Thickness(18, 7) };
            var no = new Button { Content = L.T("Non"), Padding = new Thickness(18, 7) };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } });
            var w = new Window { Title = "Stremio Watch Together", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, Background = Palette.Brush(Palette.Background), Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            yes.Click += (s, e) => { result.TrySetResult(true); w.Close(); };
            no.Click += (s, e) => w.Close();
            w.Closed += (s, e) => result.TrySetResult(false);
            if (owner != null && owner.IsVisible) w.ShowDialog(owner); else w.Show();
            return result.Task;
        }

        static Task ShowAlone(Window w) { var done = new TaskCompletionSource<bool>(); w.Closed += (s, e) => done.TrySetResult(true); w.Show(); return done.Task; }
    }

    // « Signaler un bug » : l'utilisateur voit exactement ce qui sera envoyé, et rien ne part sans son clic.
    class ReportWindow : Window
    {
        public ReportWindow(string error = null)
        {
            Title = L.T("Signaler un bug");
            Width = 560; Height = 600;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var report = BugReport.Build(error);
            var message = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80, MaxLength = 1000 };
            var preview = new TextBox { Text = report, IsReadOnly = true, FontFamily = new FontFamily("monospace"), FontSize = 11, Foreground = Palette.Brush(Palette.Muted) };
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted), VerticalAlignment = VerticalAlignment.Center };
            var send = new Button { Content = L.T("Envoyer"), Classes = { "accent" }, Padding = new Thickness(18, 7) };
            var cancel = new Button { Content = L.T("Annuler"), Padding = new Thickness(18, 7) };
            cancel.Click += (s, e) => Close();
            send.Click += async (s, e) =>
            {
                var wait = BugReport.WaitBeforeNext();
                if (wait > TimeSpan.Zero)
                {
                    status.Foreground = Palette.Brush(Palette.Bad);
                    status.Text = L.F("Un rapport vient d'être envoyé. Réessaie dans {0} min.", Math.Ceiling(wait.TotalMinutes));
                    return;
                }
                send.IsEnabled = cancel.IsEnabled = message.IsEnabled = false;
                status.Foreground = Palette.Brush(Palette.Muted);
                status.Text = L.T("Envoi…");
                bool ok = await BugReport.Send(message.Text, report);
                status.Foreground = Palette.Brush(ok ? Palette.Ok : Palette.Bad);
                status.Text = ok ? L.T("Rapport envoyé, merci !") : L.T("Envoi impossible. Vérifie ta connexion et réessaie plus tard.");
                cancel.IsEnabled = true;
                cancel.Content = L.T("Fermer");
                if (!ok) send.IsEnabled = message.IsEnabled = true;
            };
            var top = new StackPanel { Spacing = 8 };
            top.Children.Add(new TextBlock { Text = L.T("Décris ce qui s'est passé (facultatif) :"), FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            top.Children.Add(message);
            top.Children.Add(new TextBlock { Text = L.T("Ce qui sera envoyé au développeur : ton message, ta version de StremioWT et de ton système, et le journal de l'appli ci-dessous (il peut contenir ton pseudo et les titres regardés)."), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) });
            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { cancel, send } };
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(status);
            var root = new DockPanel { Margin = new Thickness(20) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            root.Children.Add(new Border { Margin = new Thickness(0, 8, 0, 0), Child = new ScrollViewer { Content = preview } });
            Content = root;
        }
    }

    // Paramètres : langue (appliquée en relançant l'appli) et statistiques anonymes.
    class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            Title = L.T("Paramètres");
            Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var lang = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = L.Languages.Select(l => l.Name).ToList(), SelectedIndex = Array.FindIndex(L.Languages, l => l.Code == L.Lang) };
            lang.SelectionChanged += (s, e) =>
            {
                var code = L.Languages[Math.Max(0, lang.SelectedIndex)].Code;
                if (code == L.Lang) return;
                L.Save(code);
                Program.Restart();
            };
            var stats = new ToggleSwitch { OnContent = L.T("Statistiques anonymes"), OffContent = L.T("Statistiques anonymes"), IsChecked = Telemetry.Enabled };
            // Avant de couper : on montre ce qui est envoyé et à quoi ça sert ; « Garder » remet l'interrupteur.
            bool reverting = false;
            stats.IsCheckedChanged += async (s, e) =>
            {
                if (reverting) return;
                if (stats.IsChecked != true && !await new StatsWindow().ShowDialog<bool>(this))
                {
                    reverting = true; stats.IsChecked = true; reverting = false;
                    return;
                }
                if (stats.IsChecked == true) Telemetry.Enabled = true; else Telemetry.TurnOff();
            };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = L.T("Langue"), FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(lang);
            panel.Children.Add(stats);
            panel.Children.Add(new TextBlock { Text = L.T("Compte combien de personnes utilisent StremioWT.\nAucun pseudo, titre, lien ni adresse IP n'est enregistré."), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) });
            var report = new Button { Content = Icons.Label("alert", L.T("Signaler un bug")), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
            report.Click += async (s, e) => await new ReportWindow().ShowDialog(this);
            panel.Children.Add(report);
            Content = panel;
        }
    }

    // Avant de couper les statistiques anonymes : ce qui est envoyé, ce qui ne l'est jamais, et à quoi ça sert.
    // Renvoie true seulement pour « Désactiver » : fermer la fenêtre garde les statistiques.
    class StatsWindow : Window
    {
        public StatsWindow()
        {
            Title = L.T("Statistiques anonymes");
            Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var keep = new Button { Content = L.T("Garder les statistiques"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 11), FontWeight = FontWeight.SemiBold };
            var off = new Button { Content = L.T("Désactiver"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 8) };
            keep.Click += (s, e) => Close(false);
            off.Click += (s, e) => Close(true);
            var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = L.T("Garder les statistiques anonymes ?"), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(new TextBlock { Text = L.T("Elles m'aident à voir ce qui marche, ce qui plante, et à maintenir StremioWT. Rien de privé n'en sort."), TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) });
            panel.Children.Add(Section(L.T("Ce qui est envoyé"), Palette.Ok, L.T("• un identifiant d'installation tiré au hasard (pas lié à toi)\n• la version de l'appli, la langue et le pays\n• appli ouverte, salon créé ou rejoint, taille du salon\n• la durée arrondie des séances et les codes d'erreur")));
            panel.Children.Add(Section(L.T("Jamais envoyé"), Palette.Bad, L.T("• ton pseudo et ton adresse IP\n• ce que tu regardes, les liens, le chat\n• les codes de salon, sauf le code court : gardé 24 h, juste pour que tes amis puissent entrer")));
            panel.Children.Add(new TextBlock { Text = L.T("Si tu désactives, un dernier signal anonyme « statistiques désactivées » est envoyé, puis plus rien. Le code du serveur est public."), FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) });
            panel.Children.Add(keep);
            panel.Children.Add(off);
            Content = panel;
        }

        static Border Section(string title, Color mark, string body)
        {
            var inner = new StackPanel { Spacing = 6 };
            inner.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(mark) });
            inner.Children.Add(new TextBlock { Text = body, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Text) });
            return new Border { Background = Palette.Brush(Palette.Card), BorderBrush = Palette.Brush(Palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), Child = inner };
        }
    }

    // Après une mise à jour : les nouveautés de cette version, une seule fois (liste commune avec Windows, dans WhatsNew.cs).
    class WhatsNewWindow : Window
    {
        public WhatsNewWindow(string version)
        {
            Title = "Stremio Watch Together";
            Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = L.T("Nouveautés"), FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
            panel.Children.Add(new TextBlock { Text = L.F("StremioWT {0} est installé ✓", version), Foreground = Palette.Brush(Palette.Ok), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var item in WhatsNew.Items)
            {
                var text = new StackPanel { Spacing = 3 };
                text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) });
                text.Children.Add(new TextBlock { Text = item.Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) });
                var row = new DockPanel();
                var icon = new TextBlock { Text = item.Icon, FontSize = 22, Width = 40, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(text);
                panel.Children.Add(new Border { Background = Palette.Brush(Palette.Card), BorderBrush = Palette.Brush(Palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10), Child = row });
            }
            var ok = new Button { Content = L.T("C'est parti"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 11), Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeight.SemiBold };
            ok.Click += (s, e) => Close();
            panel.Children.Add(ok);
            Content = panel;
        }
    }

    // Recherche de GIF (Giphy, via le serveur qui garde la clé). Renvoie l'adresse du GIF choisi, ou null.
    class GifWindow : Window
    {
        readonly TextBox search = new TextBox();
        readonly WrapPanel results = new WrapPanel();
        readonly TextBlock status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Muted) };
        CancellationTokenSource running;

        public GifWindow()
        {
            Title = "GIF";
            Width = 560; Height = 560;
            Background = Palette.Brush(Palette.Background);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(16) };
            var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(search); top.Children.Add(status);
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
            panel.Children.Add(new ScrollViewer { Content = results });
            Content = panel;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            timer.Tick += (s, e) => { timer.Stop(); _ = Search((search.Text ?? "").Trim()); };
            search.TextChanged += (s, e) => { timer.Stop(); timer.Start(); };
            search.KeyDown += (s, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                var link = Chat.GifFromLink(search.Text);
                if (link != null) Close(link);
            };
            Opened += (s, e) => { search.Focus(); _ = Search(""); };
        }

        async Task Search(string query)
        {
            running?.Cancel();
            var cts = running = new CancellationTokenSource();
            status.Text = query.Length == 0 ? L.T("Tendances : tape pour chercher, ou colle un lien Giphy puis Entrée") : L.T("Recherche…");
            try
            {
                using (var res = await Program.Http.GetAsync(Chat.GifServer + "?q=" + Uri.EscapeDataString(query) + "&lang=" + L.Lang, cts.Token))
                {
                    if ((int)res.StatusCode == 503) { status.Text = L.T("Recherche de GIF pas encore activée. Colle un lien Giphy dans la barre ci-dessus puis Entrée."); return; }
                    var data = Program.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
                    if (cts.IsCancellationRequested) return;
                    results.Children.Clear();
                    var list = (data.TryGetValue("results", out var r) ? r as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList();
                    foreach (var g in list.OfType<Dictionary<string, object>>())
                    {
                        var gif = g.TryGetValue("gif", out var gg) ? gg as string : null;
                        var preview = g.TryGetValue("preview", out var pp) ? pp as string : gif;
                        if (!Chat.IsAllowedGif(gif)) continue;
                        var img = new Image { Width = 160, Height = 110, Stretch = Stretch.UniformToFill };
                        var b = new Button { Content = img, Padding = new Thickness(2), Margin = new Thickness(4) };
                        b.Click += (s, e) => Close(gif);
                        results.Children.Add(b);
                        _ = MainWindow.LoadImage(img, preview);
                    }
                    status.Text = L.T(list.Count == 0 ? "Aucun GIF trouvé." : (query.Length == 0 ? "Tendances : clique sur un GIF pour l'envoyer" : "Clique sur un GIF pour l'envoyer"));
                }
            }
            catch (OperationCanceledException) { }
            catch { status.Text = L.T("Recherche impossible pour le moment. Tu peux coller un lien Giphy puis Entrée."); }
        }
    }
}
