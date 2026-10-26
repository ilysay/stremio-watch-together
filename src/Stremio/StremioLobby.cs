namespace StremioWT
{
    // Connexion « accueil » à Stremio, ouverte dès le lancement, avant tout salon : elle installe le script
    // (donc le bouton et le panneau Watch Together dans Stremio) et transmet les clics du panneau à l'appli.
    // Le salon garde sa propre connexion (Session) ; les deux reçoivent les messages __wtSend et chacune
    // ne traite que les siens (« panel » ici, la lecture et le chat là-bas).
    static class StremioLobby
    {
        static Cdp cdp;
        static volatile bool running;
        static Dictionary<string, object> last;

        // Commande venue du panneau : action = host, join, copy-link, copy-code, admin, leave, window, ready.
        public static event Action<Dictionary<string, object>> Command;
        // Vrai quand le panneau est installé dans Stremio, faux quand la liaison est perdue.
        public static event Action<bool> Attached;

        public static bool IsAttached => cdp != null;

        public static async Task Start(string stremioExe)
        {
            running = true;
            await Attach(await StremioApp.EnsureRunning(stremioExe));
        }

        static async Task Attach(string wsUrl)
        {
            var c = new Cdp();
            c.BindingCalled += payload =>
            {
                try
                {
                    var msg = Program.Json.Deserialize<Dictionary<string, object>>(payload);
                    if ((msg.TryGetValue("type", out var t) ? t as string : null) == "panel") Command?.Invoke(msg);
                }
                catch { }
            };
            c.Closed += () =>
            {
                if (cdp == c) cdp = null;
                Attached?.Invoke(false);
                if (running) _ = Task.Run(Reattach);
            };
            await c.Connect(wsUrl);
            await StremioApp.Prepare(c, () => running && (cdp == null || cdp == c));
            cdp = c;
            Program.Log("panneau Watch Together installé dans Stremio");
            if (last != null) Push(last);
            Attached?.Invoke(true);
        }

        // Interface rechargée : la page revient vite. Stremio fermé : on attend qu'il revienne en mode Watch Together.
        static async Task Reattach()
        {
            while (running)
            {
                await Task.Delay(1000);
                var url = await StremioApp.FindPageWsUrl();
                if (url == null) continue;
                try { await Attach(url); return; }
                catch (Exception e) { Program.Log("panneau : " + e.Message); }
            }
        }

        // État affiché par le panneau (voir W.panel dans inject.js). Gardé pour le renvoyer après un rechargement.
        public static void Push(Dictionary<string, object> state)
        {
            last = state;
            var c = cdp;
            if (c != null) _ = c.Eval("window.__wt && window.__wt.panel && window.__wt.panel(" + Program.Json.Serialize(state) + ")").ContinueWith(t => { var _ = t.Exception; });
        }

        public static void Stop()
        {
            running = false;
            try { cdp?.Close(); } catch { }
        }
    }
}
