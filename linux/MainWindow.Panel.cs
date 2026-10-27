using Avalonia.Controls;
using Avalonia.Threading;

namespace StremioWT
{
    // Le panneau Watch Together dans Stremio (comme sur Windows) : dès le lancement, Stremio s'ouvre avec un bouton
    // « Watch Together » qui permet d'héberger, rejoindre et inviter sans passer par cette fenêtre. Chaque clic du
    // panneau reprend exactement le chemin des boutons de la fenêtre (StartHost, StartJoin…), qui reste disponible.
    partial class MainWindow
    {
        bool panelShownOnce, startError;
        DateTime lostSince = DateTime.MaxValue;

        void StartPanel()
        {
            var exe = StremioApp.FindExe(Program.Config);
            if (exe == null) return; // ni Stremio ni navigateur : la fenêtre explique quoi faire au premier clic, comme avant
            StremioLobby.Command += msg => Dispatcher.UIThread.Post(() => OnPanelCommand(msg));
            StremioLobby.Attached += ok => Dispatcher.UIThread.Post(() => OnPanelAttached(ok));
            _ = Task.Run(async () =>
            {
                try { await StremioLobby.Start(exe); }
                catch (Exception e) { Program.Log("panneau : " + e.Message); }
            });
            // Liaison perdue sans salon en cours (Stremio fermé) : on remontre la fenêtre au bout de quelques secondes.
            var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            watch.Tick += (s, e) =>
            {
                if (session == null && !StremioLobby.IsAttached && DateTime.Now - lostSince > TimeSpan.FromSeconds(6) && WindowState == WindowState.Minimized)
                    ShowWindow();
            };
            watch.Start();
        }

        void ShowWindow()
        {
            WindowState = WindowState.Normal;
            Activate();
        }

        void OnPanelAttached(bool ok)
        {
            lostSince = ok ? DateTime.MaxValue : DateTime.Now;
            if (!ok) return;
            PushPanel();
            // Premier panneau installé : on s'efface, tout se passe dans Stremio. La fenêtre reste dans le dock / la barre des tâches.
            if (!panelShownOnce && session == null)
            {
                panelShownOnce = true;
                WindowState = WindowState.Minimized;
            }
        }

        void OnPanelCommand(Dictionary<string, object> msg)
        {
            string Text(string key) => msg.TryGetValue(key, out var v) ? (v as string ?? "").Trim() : "";
            var action = Text("action");
            Program.Log("panneau : " + action);
            switch (action)
            {
                case "ready": PushPanel(); break;
                case "host":
                case "join":
                    if (session != null || !hostButton.IsEnabled) return;
                    if (Text("name").Length > 0) nameBox.Text = Text("name");
                    if (action == "host") StartHost();
                    else { codeBox.Text = Text("code"); StartJoin(); }
                    break;
                case "copy-link": if (inviteLong.Length > 0) Program.SetClipboardText(Invite.Link(inviteLong)); break;
                case "copy-code": if (!string.IsNullOrEmpty(inviteBox.Text)) Program.SetClipboardText(inviteBox.Text); break;
                // Bêta Linux/Mac : le mode hébergeur admin reste toujours activé (voir ShowSession), le panneau l'affiche verrouillé.
                case "leave": if (session != null) Program.Restart(); break; // une appli neuve revient à l'accueil, le panneau se réinstalle
                case "window": ShowWindow(); break;
            }
        }

        // État du panneau : accueil (avec le message en cours ou l'erreur) ou salon (code, membres, mode hôte).
        void PushPanel()
        {
            var state = new Dictionary<string, object> { ["name"] = Pseudo(), ["open"] = !panelShownOnce };
            if (session != null)
            {
                state["mode"] = "room";
                state["room"] = session.Room;
                state["host"] = session.IsHost;
                state["connected"] = session.Connected;
                state["admin"] = session.IsHost || session.AdminOn;
                state["adminLocked"] = true;
                if (session.IsHost) state["adminHint"] = adminHint.Text;
                state["members"] = session.Members.Select(n => n + ShellNote(n)).ToList();
                if (session.IsHost) state["code"] = inviteBox.Text ?? "";
            }
            else
            {
                state["mode"] = hostButton.IsEnabled ? "idle" : "busy";
                state["message"] = startStatus.Text ?? "";
                state["error"] = startError;
                state["invite"] = codeBox.Text ?? "";
            }
            StremioLobby.Push(state);
        }

        // « · Stremio 5 », avec ⚠ si cette version ne comprend pas les liens de torrent de la nôtre.
        string ShellNote(string who)
        {
            var label = Session.ShellLabel(session.ShellOf(who));
            return label == null ? "" : "  · " + label + (session.SameShellFamily(who) ? "" : " ⚠");
        }
    }
}
