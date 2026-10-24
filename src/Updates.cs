using static StremioWT.Program;

namespace StremioWT
{
    static class Updates
    {
        public const string RepoUrl = "https://github.com/ilysay/stremio-watch-together";
        public const string DownloadUrl = RepoUrl + "/releases/latest";
        const string LatestApi = "https://api.github.com/repos/ilysay/stremio-watch-together/releases/latest";

        static Version Current => Assembly.GetExecutingAssembly().GetName().Version;
        static Dictionary<string, object> latest; // dernière version publiée (réponse de l'API GitHub)

        // Numéro de la dernière version publiée si elle est plus récente que celle-ci, sinon null (aussi en cas d'erreur réseau).
        public static async Task<string> NewerVersion()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, LatestApi))
                using (var cts = new CancellationTokenSource(6000))
                {
                    req.Headers.UserAgent.ParseAdd("StremioWT/" + Current.ToString(3)); // exigé par l'API GitHub
                    using (var res = await Http.SendAsync(req, cts.Token))
                    {
                        if (!res.IsSuccessStatusCode) return null;
                        latest = Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
                        var tag = latest["tag_name"] as string;
                        return IsNewer(tag, Current) ? tag.TrimStart('v', 'V') : null;
                    }
                }
            }
            catch (Exception e) { Log("mise à jour : " + e.Message); return null; }
        }

        // Mise à jour dans l'appli : télécharge le fichier de la dernière version depuis GitHub, vérifie son empreinte
        // SHA-256 (fournie par GitHub pour chaque fichier publié), puis le met à la place de l'appli en cours.
        // Windows refuse d'écraser un .exe lancé, mais accepte de le renommer : l'ancien devient « .old », effacé au
        // prochain démarrage. Renvoie true si tout est prêt pour relancer.
        static string AssetName => IsWindows ? "StremioWT.exe" : "StremioWT-linux-x64";

        static string CurrentExe =>
#if NETFRAMEWORK
            Assembly.GetEntryAssembly().Location;
#else
            Environment.ProcessPath;
#endif

        public static async Task<bool> Install(Action<int> progress)
        {
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

        internal static bool IsNewer(string tag, Version current) =>
            Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var latest) && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }
}
