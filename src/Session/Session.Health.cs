namespace StremioWT
{
    // Deux filets de sécurité pendant la séance :
    // 1. Stremio fermé ou planté : le salon reste ouvert, les amis sont prévenus, et on se rebranche dès que
    //    Stremio revient (relancé à la main ou par le bouton « Relancer Stremio »).
    // 2. Téléchargement trop lent : quand notre lecteur reste bloqué en chargement, le serveur local de Stremio donne la
    //    vitesse du torrent. Si elle ne suit pas le débit du film, on le dit à tout le monde une fois (source avec plus de seeds).
    partial class Session
    {
        // --- 1. Stremio fermé ------------------------------------------------------------------------
        public bool StremioDown { get; private set; }
        int restarting;

        async Task WaitForStremioBack()
        {
            StremioDown = true;
            cdp = null;
            Program.Log("Stremio fermé (plus aucun processus Stremio) : le salon reste ouvert");
            Console.WriteLine(L.T("Stremio s'est fermé. Le salon reste ouvert : relance Stremio pour continuer."));
            SendChat(L.T("⚠ Mon Stremio s'est fermé, je le relance…"), null);
            StateChanged?.Invoke();
            while (StremioDown)
            {
                await Task.Delay(2000);
                if (restarting == 1) continue;
                if (cdp != null) { StremioBack(); continue; }
                var url = await StremioApp.FindPageWsUrl();
                if (url == null) continue;
                try { await AttachStremio(url); }
                catch (Exception e) { Program.Log("rebranchement Stremio : " + e.Message); continue; }
                StremioBack();
            }
        }

        void StremioBack()
        {
            if (!StremioDown) return;
            StremioDown = false;
            Console.WriteLine(L.T("Stremio est de retour : reprise de la séance."));
            SendChat(L.T("✓ Mon Stremio est relancé, je reprends."), null);
            if (!isHost) _ = Post(new Dictionary<string, object> { ["type"] = "need-sync" });
            StateChanged?.Invoke();
        }

        // Bouton « Relancer Stremio » : relance Stremio en mode Watch Together et s'y rebranche.
        public async Task RestartStremio()
        {
            if (Interlocked.Exchange(ref restarting, 1) == 1) return;
            try
            {
                var url = await StremioApp.EnsureRunning(stremioExe);
                if (cdp == null) { await AttachStremio(url); StremioBack(); }
            }
            finally { restarting = 0; }
        }

        // --- 2. Téléchargement trop lent -------------------------------------------------------------
        const int HealthEveryMs = 5000, SlowChecks = 3;
        string torrentHash; int torrentFile, slowCount; bool slowReported;
        readonly Dictionary<string, DateTime> slowShown = new Dictionary<string, DateTime>();

        // Envoyé par la page à chaque nouvelle vidéo : le torrent lu (null si lien direct, Real-Debrid…).
        void OnTorrent(Dictionary<string, object> msg)
        {
            var previous = torrentHash;
            torrentHash = msg.TryGetValue("hash", out var h) && h is string hs && Regex.IsMatch(hs, "^[0-9a-f]{40}$") ? hs : null;
            // Autre torrent : Stremio continuerait de télécharger l'ancien fichier en entier pendant une vingtaine de secondes,
            // en prenant la moitié du débit au nouveau (mesuré : 11 à 18 s au lieu de 7 s pour démarrer). On l'arrête tout de suite.
            // Même torrent (épisode suivant d'un pack de saison) : on le garde, ses pairs sont déjà connectés.
            if (previous != null && previous != torrentHash)
                _ = Program.Http.GetAsync($"http://127.0.0.1:11470/{previous}/remove").ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); });
            torrentFile = msg.TryGetValue("idx", out var i) && i != null ? Convert.ToInt32(i) : 0;
            slowCount = 0;
            slowReported = false;
        }

        async Task WatchDownload()
        {
            for (;;)
            {
                await Task.Delay(HealthEveryMs);
                try { await CheckDownload(); }
                catch (Exception e) { Program.Log("vitesse du torrent : " + e.Message); }
            }
        }

        async Task CheckDownload()
        {
            var hash = torrentHash;
            if (hash == null || slowReported || cdp == null) return;
            // Seulement quand notre lecteur attend vraiment des données. Sinon la vitesse ne veut rien dire : Stremio
            // ralentit exprès le téléchargement dès qu'il a assez d'avance, et un fichier déjà sur le disque (déjà vu)
            // se lit sans aucun peer. Ces deux cas donnaient de fausses alertes en pleine lecture fluide.
            bool stuck;
            lock (waitGate) stuck = bufferingSince.ContainsKey(name);
            if (!stuck) { slowCount = 0; return; }
            Dictionary<string, object> stats;
            using (var cts = new CancellationTokenSource(3000))
            using (var res = await Program.Http.GetAsync($"http://127.0.0.1:11470/{hash}/{torrentFile}/stats.json", cts.Token))
            {
                if (!res.IsSuccessStatusCode) return;
                stats = Program.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
            }
            if (stats == null || hash != torrentHash) return;
            double Num(string k) => stats.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v) : 0;
            double speed = Num("downloadSpeed"), size = Num("streamLen"), progress = Num("streamProgress"), peers = Num("peers");
            // Source morte : aucun peer et rien de reçu, pas même la taille du fichier. Stremio attendrait sans fin.
            if (peers == 0 && Num("downloaded") == 0)
            {
                if (++slowCount >= SlowChecks) Report(0, 0, 0);
                return;
            }
            if (size <= 0 || progress >= 0.97) { slowCount = 0; return; }
            // Débit du film : taille du fichier / durée (2 h si la durée n'est pas encore connue).
            double lengthMs = 0;
            try { lengthMs = Convert.ToDouble(await cdp.Eval("window.__wt && window.__wt.duration ? window.__wt.duration() : 0") ?? 0); } catch { }
            double need = size / (lengthMs > 60000 ? lengthMs / 1000 : 7200);
            if (speed >= need * 0.8) { slowCount = 0; return; }
            if (++slowCount >= SlowChecks) Report(speed, need, (int)peers);
        }

        void Report(double speed, double need, int peers)
        {
            slowReported = true;
            Program.Log(need == 0 ? "source sans aucun peer : la vidéo ne pourra pas démarrer"
                : $"téléchargement trop lent : {speed / 1024:0} Ko/s pour {need / 1024:0} Ko/s nécessaires, {peers} peer(s)");
            ShowSlow(name, speed, need, peers);
            _ = Post(new Dictionary<string, object> { ["type"] = "slow", ["speed"] = Math.Round(speed), ["need"] = Math.Round(need), ["peers"] = peers });
        }

        void ShowSlow(string who, double speed, double need, int peers)
        {
            // need = 0 : source morte (aucun peer, rien reçu).
            var text = need <= 0
                ? (who == name ? L.T("Ta source ne trouve aucun peer : la vidéo ne peut pas démarrer.") : L.F("La source de {0} ne trouve aucun peer : sa vidéo ne peut pas démarrer.", who))
                    + " " + L.T("Choisissez une source avec plus de seeds 👤 (ou Real-Debrid).")
                : ((who == name
                    ? L.F("Ton téléchargement est trop lent pour ce film ({0}, il faut environ {1})", Rate(speed), Rate(need))
                    : L.F("{0} télécharge trop lentement ({1}, il faut environ {2})", who, Rate(speed), Rate(need)))
                + (peers == 0 ? L.T(" · aucune source jointe") : "")
                + ". " + L.T("Choisissez une source avec plus de seeds 👤 (ou Real-Debrid)."));
            ShowChat(new ChatMessage { From = "StremioWT", System = true, Text = text });
            ToPage(new Dictionary<string, object> { ["type"] = "toast", ["text"] = text });
        }

        static string Rate(double bytesPerSecond) =>
            bytesPerSecond >= 1024 * 1024 ? L.F("{0} Mo/s", (bytesPerSecond / 1024 / 1024).ToString("0.0")) : L.F("{0} Ko/s", Math.Round(bytesPerSecond / 1024));
    }
}
