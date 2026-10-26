using static StremioWT.Program;

namespace StremioWT
{
    static class Updates
    {
        public const string RepoUrl = "https://github.com/ilysay/stremio-watch-together";
        public const string DownloadUrl = RepoUrl + "/releases/latest";
        const string ReleasesApi = "https://api.github.com/repos/ilysay/stremio-watch-together/releases";
        const string LatestApi = ReleasesApi + "/latest";

        static Version Current => Assembly.GetExecutingAssembly().GetName().Version;
        static Dictionary<string, object> latest; // dernière version publiée (réponse de l'API GitHub)

        // Numéro de la dernière version publiée si elle apporte quelque chose à ce système, sinon null (aussi en cas
        // d'erreur réseau). Une version peut ne changer qu'un système : les autres y gardent leur fichier à l'identique.
        // On compare donc l'empreinte du fichier de ce système avec celle de la version installée ; identique = rien à faire.
        public static async Task<string> NewerVersion()
        {
            try
            {
                latest = await Release(LatestApi);
                var tag = latest?["tag_name"] as string;
                if (latest == null || !IsNewer(tag, Current)) return null;
                var mine = await Release(ReleasesApi + "/tags/v" + Current.ToString(3));
                return HasNewFile(latest, mine, AssetName) ? tag.TrimStart('v', 'V') : null;
            }
            catch (Exception e) { Log("mise à jour : " + e.Message); return null; }
        }

        static async Task<Dictionary<string, object>> Release(string url)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var cts = new CancellationTokenSource(6000))
            {
                req.Headers.UserAgent.ParseAdd("StremioWT/" + Current.ToString(3)); // exigé par l'API GitHub
                using (var res = await Http.SendAsync(req, cts.Token))
                    return res.IsSuccessStatusCode ? Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync()) : null;
            }
        }

        // La nouvelle version a-t-elle un fichier pour ce système, différent de celui de la version installée ?
        // Sans la version installée sur GitHub (version de développement), on se fie au numéro.
        internal static bool HasNewFile(Dictionary<string, object> latestRelease, Dictionary<string, object> installedRelease, string asset)
        {
            var fresh = Digest(latestRelease, asset);
            if (fresh == null) return false;
            return installedRelease == null || Digest(installedRelease, asset) != fresh;
        }

        static string Digest(Dictionary<string, object> release, string asset) =>
            (release?["assets"] as System.Collections.ArrayList)?.Cast<Dictionary<string, object>>()
                .Where(a => a["name"] as string == asset)
                .Select(a => a.TryGetValue("digest", out var d) ? d as string : null)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d));

        // Mise à jour dans l'appli : télécharge le fichier de la dernière version depuis GitHub, vérifie son empreinte
        // SHA-256 (fournie par GitHub pour chaque fichier publié), puis le met à la place de l'appli en cours.
        // Windows refuse d'écraser un .exe lancé, mais accepte de le renommer : l'ancien devient « .old », effacé au
        // prochain démarrage. Renvoie true si tout est prêt pour relancer.
        static string AssetName => IsWindows ? "StremioWT.exe" : IsMac ? "StremioWT-mac.zip" : "StremioWT-linux-x64";

        static string CurrentExe =>
#if NETFRAMEWORK
            Assembly.GetEntryAssembly().Location;
#else
            Environment.ProcessPath;
#endif

        public static async Task<bool> Install(Action<int> progress)
        {
            // Mac : l'appli est un paquet StremioWT.app dans un zip, pas un fichier unique à remplacer.
            // On ouvre la page de téléchargement à la place (même chemin qu'un échec).
            if (IsMac) { Log("mise à jour : sur Mac, téléchargement manuel"); return false; }
            var asset = (latest?["assets"] as System.Collections.ArrayList)?.Cast<Dictionary<string, object>>()
                .FirstOrDefault(a => a["name"] as string == AssetName);
            var url = asset?["browser_download_url"] as string;
            var digest = (asset != null && asset.TryGetValue("digest", out var d) ? d as string : null) ?? "";
            if (url == null || !digest.StartsWith("sha256:")) { Log("mise à jour : fichier ou empreinte introuvable"); return false; }
            var exe = CurrentExe;
            var fresh = exe + ".new";
            try
            {
                using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    long total = res.Content.Headers.ContentLength ?? 0, done = 0;
                    using (var input = await res.Content.ReadAsStreamAsync())
                    using (var output = File.Create(fresh))
                    {
                        var buffer = new byte[81920];
                        int n;
                        while ((n = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, n);
                            done += n;
                            if (total > 0) progress?.Invoke((int)(done * 100 / total));
                        }
                    }
                }
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var file = File.OpenRead(fresh))
                    hash = string.Concat(sha.ComputeHash(file).Select(b => b.ToString("x2")));
                if (hash != digest.Substring(7).ToLowerInvariant())
                {
                    Log($"mise à jour : empreinte différente ({hash}), fichier refusé");
                    File.Delete(fresh);
                    return false;
                }
#if !NETFRAMEWORK
                if (!IsWindows) File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
                var old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                File.Move(fresh, exe);
                Config["updatedTo"] = (latest["tag_name"] as string ?? "").TrimStart('v', 'V');
                SaveConfig(Config);
                Log("mise à jour installée : " + Config["updatedTo"]);
                return true;
            }
            catch (Exception e)
            {
                Log("mise à jour : " + e.Message);
                try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
                return false;
            }
        }

        // Au démarrage qui suit une mise à jour : efface l'ancienne version et renvoie le numéro installé (une seule fois).
        public static string AfterUpdate()
        {
            try { var old = CurrentExe + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            if (!(Config.TryGetValue("updatedTo", out var v) && v is string version)) return null;
            Config.Remove("updatedTo");
            SaveConfig(Config);
            return version;
        }

        // « Quoi de neuf » à montrer au démarrage : une seule fois par version, et seulement à ceux qui avaient déjà
        // l'appli (mise à jour dans l'appli, ou nouvel exe téléchargé à la main) : pas à une toute première installation.
        public static bool WhatsNewPending(string updated)
        {
            var now = Current.ToString(3);
            var seen = Config.TryGetValue("seenVersion", out var s) ? s as string : null;
            if (seen == now) return false;
            bool hadApp = seen != null || updated != null || Config.ContainsKey("name");
            Config["seenVersion"] = now;
            SaveConfig(Config);
            return hadApp;
        }

        internal static string CurrentVersion => Current.ToString(3);

        internal static bool IsNewer(string tag, Version current) =>
            Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var latest) && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }
}
