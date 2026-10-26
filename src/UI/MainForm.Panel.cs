namespace StremioWT
{
    // Le panneau Watch Together dans Stremio : dès le lancement, Stremio s'ouvre avec un bouton « Watch Together »
    // qui permet d'héberger, rejoindre et inviter sans passer par cette fenêtre. Chaque clic du panneau
    // reprend exactement le chemin des boutons de la fenêtre (StartHost, StartJoin…), qui reste disponible.
    partial class MainForm
    {
        bool panelShownOnce;
        DateTime lostSince = DateTime.MaxValue;

        void StartPanel()
        {
            var exe = StremioApp.FindExe(Program.Config);
            if (exe == null) return; // pas de Stremio : la fenêtre explique quoi faire au premier clic, comme avant
            StremioLobby.Command += msg => Ui(() => OnPanelCommand(msg));
            StremioLobby.Attached += ok => Ui(() => OnPanelAttached(ok));
            _ = Task.Run(async () =>
            {
                try { await StremioLobby.Start(exe); }
                catch (Exception e) { Program.Log("panneau : " + e.Message); }
            });
            // Liaison perdue sans salon en cours (Stremio fermé) : on remontre la fenêtre au bout de quelques secondes.
            var watch = new System.Windows.Forms.Timer { Interval = 2000 };
            watch.Tick += (s, e) =>
            {
                if (session == null && !StremioLobby.IsAttached && DateTime.Now - lostSince > TimeSpan.FromSeconds(6) && WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                    Activate();
                }
            };
            watch.Start();
        }

        void OnPanelAttached(bool ok)
        {
            lostSince = ok ? DateTime.MaxValue : DateTime.Now;
            if (!ok) return;
            PushPanel();
            // Premier panneau installé : on s'efface, tout se passe dans Stremio. La fenêtre reste dans la barre des tâches.
            if (!panelShownOnce && session == null)
            {
                panelShownOnce = true;
                WindowState = FormWindowState.Minimized;
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
                    if (session != null || !hostButton.Enabled) return;
                    if (Text("name").Length > 0) nameBox.Box.Text = Text("name");
                    if (action == "host") { localBox.Checked = msg.TryGetValue("local", out var l) && l is bool lb && lb; StartHost(); }
                    else { codeBox.Box.Text = Text("code"); StartJoin(); }
                    break;
                case "copy-link": if (inviteLong.Length > 0) Program.SetClipboardText(Invite.Link(inviteLong)); break;
                case "copy-code": if (inviteBox.Box.Text.Length > 0) Program.SetClipboardText(inviteBox.Box.Text); break;
                case "admin": if (session != null && session.IsHost) adminBox.Checked = msg.TryGetValue("on", out var on) && on is bool ob && ob; break;
                case "leave": if (session != null) Program.Restart(); break; // une appli neuve revient à l'accueil, le panneau se réinstalle
                case "window": WindowState = FormWindowState.Normal; Activate(); break;
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
                state["admin"] = session.AdminOn;
                state["members"] = session.Members.Select(n => n + ShellNote(n)).ToList();
                if (session.IsHost) state["code"] = inviteBox.Box.Text;
            }
            else
            {
                state["mode"] = hostButton.Enabled ? "idle" : "busy";
                state["message"] = startStatus.Text;
                state["error"] = startStatus.ForeColor == Theme.Bad;
                state["invite"] = codeBox.Box.Text;
            }
            StremioLobby.Push(state);
        }
    }
}
