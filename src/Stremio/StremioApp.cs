using static StremioWT.Program;

namespace StremioWT
{
    static class StremioApp
    {
        const int DebugPort = 9333;

        // Version du script injecté : change aussi avec la langue (Stremio recharge alors son interface).
        internal static string InjectVersion => ScriptVersion + "/" + L.Lang;

        internal static string ReadInject()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("inject.js"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().Replace("__WT_VERSION__", InjectVersion).Replace("__WT_I18N__", Json.Serialize(L.ForPage()));
        }

        // Stremio 5 (stremio-shell-ng, WebView2) ou Stremio 4 (stremio, Qt). Priorité à celui qui est ouvert,
        // puis à celui utilisé la dernière fois, puis à Stremio 5, la version proposée aujourd'hui par Stremio.
        internal static string FindExe(Dictionary<string, object> config)
        {
            if (IsMac) return FindBrowser();
            if (!IsWindows) return FindLinuxStremio(config);
            var candidates = new List<string>();
            foreach (var name in new[] { "stremio-shell-ng", "stremio" })
                try { var p = Process.GetProcessesByName(name).FirstOrDefault(); if (p != null) candidates.Add(p.MainModule.FileName); } catch { }
            if (config.TryGetValue("stremioPath", out var sp) && sp is string spath) candidates.Add(spath);
            var roots = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            candidates.AddRange(roots.Select(r => Path.Combine(r, "Stremio", "stremio-shell-ng.exe")));
            candidates.AddRange(roots.Select(r => Path.Combine(r, "LNV", "Stremio-4", "stremio.exe")));
            return candidates.FirstOrDefault(File.Exists);
        }

        internal static bool IsStremio5(string exe) => Path.GetFileName(exe ?? "").Equals("stremio-shell-ng.exe", StringComparison.OrdinalIgnoreCase);

        // Linux : Stremio 4 (Qt) installé en paquet (.deb, AUR…), qui se pilote directement. Sinon (nouvelle appli
        // Stremio de Flathub, Ubuntu 24.04 sans Stremio 4…), on passe par Stremio Web dans un navigateur, comme sur Mac.
        static string FindLinuxStremio(Dictionary<string, object> config)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>();
            if (config.TryGetValue("stremioPath", out var sp) && sp is string spath && !IsBrowser(spath)) candidates.Add(spath);
            candidates.AddRange(new[] { "/usr/bin/stremio", "/usr/local/bin/stremio", "/opt/stremio/stremio", Path.Combine(home, ".local/bin/stremio") });
            return candidates.FirstOrDefault(File.Exists) ?? FindBrowser();
        }

        static ProcessStartInfo LinuxLaunch(string exe)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            psi.EnvironmentVariables["QTWEBENGINE_REMOTE_DEBUGGING"] = $"127.0.0.1:{DebugPort}";
            return psi;
        }

        // Stremio Web (web.stremio.com) dans un navigateur Chromium, avec un profil à part (le navigateur habituel
        // n'est pas touché) : sur Mac (Stremio 5 pour Mac ne se laisse pas piloter) et sur Linux sans Stremio 4.
        // Les torrents passent par le serveur de l'appli Stremio si elle est ouverte, les liens Real-Debrid marchent sans.
        internal const string StremioWeb = "https://web.stremio.com/";

        static string FindBrowser()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsMac)
            {
                var apps = new[] { "Google Chrome", "Microsoft Edge", "Brave Browser", "Chromium", "Vivaldi" };
                return new[] { "/Applications", Path.Combine(home, "Applications") }
                    .SelectMany(dir => apps.Select(a => Path.Combine(dir, a + ".app", "Contents", "MacOS", a)))
                    .FirstOrDefault(File.Exists);
            }
            var names = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable", "brave-browser", "vivaldi" };
            return new[] { "/usr/bin", "/usr/local/bin", "/snap/bin" }.SelectMany(dir => names.Select(n => Path.Combine(dir, n))).FirstOrDefault(File.Exists);
        }

        internal static bool IsBrowser(string exe) => IsMac || Regex.IsMatch(Path.GetFileName(exe ?? ""), "chrom|edge|brave|vivaldi", RegexOptions.IgnoreCase);

        static ProcessStartInfo BrowserLaunch(string browser)
        {
            // Un navigateur en snap (Ubuntu) ne peut écrire que dans son propre dossier ~/snap/<nom>/common.
            var profile = browser.StartsWith("/snap/")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap", Path.GetFileName(browser), "common", "stremiowt-profile")
                : Path.Combine(DataDir, "browser");
            Directory.CreateDirectory(profile);
            return new ProcessStartInfo(browser,
                $"--remote-debugging-port={DebugPort} \"--user-data-dir={profile}\" --no-first-run --no-default-browser-check " +
                // Accès au serveur local de Stremio (torrents) autorisé d'avance : sinon Chrome le demande à chaque fois.
                $"--autoplay-policy=no-user-gesture-required --disable-features=LocalNetworkAccessChecks --app={StremioWeb}") { UseShellExecute = false };
        }

        internal static async Task<bool> StreamingServerUp()
        {
            try { using (var cts = new CancellationTokenSource(1500)) using (await Http.GetAsync("http://127.0.0.1:11470/settings", cts.Token)) return true; }
            catch { return false; }
        }

        internal static async Task<string> FindPageWsUrl()
        {
            try
            {
                using (var cts = new CancellationTokenSource(2000))
                {
                    var text = await Http.GetStringAsync($"http://127.0.0.1:{DebugPort}/json").WithCancel(cts.Token);
                    foreach (var t in Json.Deserialize<List<Dictionary<string, object>>>(text))
                        if ((t["type"] as string) == "page" && Regex.IsMatch(t["url"] as string ?? "", @"strem\.io|stremio\.com|127\.0\.0\.1:11470"))
                            return t["webSocketDebuggerUrl"] as string;
                }
            }
            catch { }
            return null;
        }

        internal static async Task<string> EnsureRunning(string exe)
        {
            var ws = await FindPageWsUrl();
            if (ws != null) return ws;
            Console.WriteLine(L.T("Lancement de Stremio en mode Watch Together…"));
            if (IsBrowser(exe))
            {
                // Rien à fermer : l'appli Stremio reste ouverte pour son serveur de torrents.
                Process.Start(BrowserLaunch(exe));
                if (!await StreamingServerUp()) Console.WriteLine(L.T("Astuce : ouvre aussi l'appli Stremio pour lire les torrents (les liens Real-Debrid marchent sans)."));
                return await WaitForPage(exe);
            }
            // Stremio 4 et 5 se disputeraient le serveur local (port 11470) : on ferme les deux.
            foreach (var p in new[] { "stremio-shell-ng", "stremio", "stremio-runtime" }.SelectMany(Process.GetProcessesByName))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            await Task.Delay(1500);
            // Lancement « normal » (ShellExecute) : Stremio n'hérite d'aucun descripteur de StremioWT.
            // Les variables de débogage passent par notre propre environnement, que Stremio reçoit
            // (QTWEBENGINE_… pour Stremio 4, WEBVIEW2_… pour Stremio 5). Le port n'écoute que sur ce PC.
            if (IsWindows)
            {
                Environment.SetEnvironmentVariable("QTWEBENGINE_REMOTE_DEBUGGING", $"127.0.0.1:{DebugPort}");
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", $"--remote-debugging-port={DebugPort}");
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            else Process.Start(LinuxLaunch(exe));
            return await WaitForPage(exe);
        }

        static async Task<string> WaitForPage(string exe)
        {
            string ws;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                if ((ws = await FindPageWsUrl()) != null) return ws;
            }
            Telemetry.Error("stremio-noreply");
            throw new Exception(L.T("Stremio ne répond pas."));
        }
    }
}
