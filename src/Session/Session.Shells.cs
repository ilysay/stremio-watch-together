namespace StremioWT
{
    // Version de Stremio de chacun (« 4 », « 5 » ou « web ») : annoncée en arrivant et à chaque nouveau venu.
    // Stremio 4 ne décrit pas un lien de torrent comme Stremio 5 / Stremio Web : entre les deux, la vidéo
    // lancée par l'un ouvre chez l'autre la page de l'épisode, où il choisit sa source. On prévient une fois.
    partial class Session
    {
        readonly Dictionary<string, string> shells = new Dictionary<string, string>();
        readonly HashSet<string> shellWarned = new HashSet<string>();
        string myShell;

        public string ShellOf(string who)
        {
            if (who == name) return myShell;
            lock (shells) return shells.TryGetValue(who, out var s) ? s : null;
        }

        public static string ShellLabel(string shell) => shell == null ? null : shell == "web" ? "Stremio Web" : "Stremio " + shell;

        // Même « famille » : les deux comprennent le lien de torrent de l'autre.
        public bool SameShellFamily(string who)
        {
            var other = ShellOf(who);
            return other == null || myShell == null || (other == "4") == (myShell == "4");
        }

        async Task ReadMyShell()
        {
            try { myShell = cdp == null ? null : await cdp.Eval("window.__wt ? String(window.__wt.shell) : null") as string; }
            catch { myShell = null; }
        }

        void AnnounceShell()
        {
            if (myShell != null) _ = Post(new Dictionary<string, object> { ["type"] = "info", ["shell"] = myShell });
        }

        void OnShellInfo(string who, string shell)
        {
            if (who == null || who == name || !(shell == "4" || shell == "5" || shell == "web")) return;
            lock (shells) shells[who] = shell;
            StateChanged?.Invoke();
            if (SameShellFamily(who)) return;
            lock (shellWarned) if (!shellWarned.Add(who)) return;
            ShowChat(new ChatMessage
            {
                From = "StremioWT", System = true,
                Text = L.F("{0} utilise {1} et toi {2} : avec un torrent, l'un de vous arrivera sur la page de l'épisode et choisira sa source (un clic). Avec Real-Debrid, rien ne change. Pour tout ouvrir directement, prenez la même version de Stremio.",
                    who, ShellLabel(shell), ShellLabel(myShell)),
            });
        }

        void ForgetShell(string who)
        {
            lock (shells) shells.Remove(who);
            lock (shellWarned) shellWarned.Remove(who);
        }
    }
}
