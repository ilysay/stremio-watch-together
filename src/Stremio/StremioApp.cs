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
        // puis à celui utilisé la dernière fois, puis aux dossiers habituels, puis à ce que Windows connaît
        // (registre). Sans appli Stremio : Stremio Web dans Edge (toujours présent sur Windows 10/11).
        internal static string FindExe(Dictionary<string, object> config)
        {
            if (IsMac) return FindBrowser();
            if (!IsWindows) return FindLinuxStremio(config);
            var candidates = new List<string>();
            foreach (var name in new[] { "stremio-shell-ng", "stremio" })
                try { var p = Process.GetProcessesByName(name).FirstOrDefault(); if (p != null) candidates.Add(p.MainModule.FileName); } catch { }
            if (config.TryGetValue("stremioPath", out var sp) && sp is string spath && !IsBrowser(spath)) candidates.Add(spath);
            var roots = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            candidates.AddRange(roots.Select(r => Path.Combine(r, "Stremio", "stremio-shell-ng.exe")));
            candidates.AddRange(roots.Select(r => Path.Combine(r, "LNV", "Stremio-4", "stremio.exe")));
            candidates.AddRange(RegistryStremio());
            return candidates.FirstOrDefault(File.Exists) ?? FindBrowser();
        }

        // Stremio installé ailleurs (autre disque, autre dossier) : Windows le note dans « Programmes et fonctionnalités ».
        static List<string> RegistryStremio()
        {
            var found = new List<string>();
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    try
                    {
                        using (var root = hive.OpenSubKey(path))
                            foreach (var name in root?.GetSubKeyNames() ?? new string[0])
                                using (var key = root.OpenSubKey(name))
                                {
                                    if (!((key?.GetValue("DisplayName") as string) ?? "").StartsWith("Stremio", StringComparison.OrdinalIgnoreCase)) continue;
                                    var icon = ((key.GetValue("DisplayIcon") as string) ?? "").Split(',')[0].Trim('"');
                                    foreach (var dir in new[] { key.GetValue("InstallLocation") as string, icon.Length > 0 ? Path.GetDirectoryName(icon) : null })
                                        if (!string.IsNullOrEmpty(dir)) { found.Add(Path.Combine(dir, "stremio-shell-ng.exe")); found.Add(Path.Combine(dir, "stremio.exe")); }
                                }
                    }
                    catch { }
            return found;
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
            if (IsWindows)
            {
                var apps = new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe" };
                return new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                    .SelectMany(f => apps.Select(a => Path.Combine(Environment.GetFolderPath(f), a))).FirstOrDefault(File.Exists);
            }
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

        // Pourquoi Stremio s'ouvre dans un navigateur : expliqué à l'utilisateur à chaque fois.
        internal static string WebReason() =>
            IsWindows ? L.T("Aucune appli Stremio trouvée sur ce PC : StremioWT a ouvert Stremio Web dans ton navigateur à la place. C'est ton Stremio habituel (même compte, mêmes addons) : connecte-toi une fois dans cette fenêtre. Pour les torrents et tous les formats vidéo, installe l'appli Stremio pour Windows (stremio.com/downloads).")
            : IsMac ? L.T("Stremio pour Mac ne peut pas être piloté par StremioWT : il a ouvert Stremio Web dans ton navigateur à la place. C'est ton Stremio habituel (même compte, mêmes addons) : connecte-toi une fois dans cette fenêtre.")
            : L.T("L'appli Stremio pour Linux ne peut pas être pilotée par StremioWT : il a ouvert Stremio Web dans ton navigateur à la place. C'est ton Stremio habituel (même compte, mêmes addons) : connecte-toi une fois dans cette fenêtre.");

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
            if (await PortTakenByOther())
            {
                Telemetry.Error("stremio-noreply");
                throw new Exception(L.F("Le port {0} est déjà utilisé par un autre programme (souvent un navigateur lancé en mode développeur). Ferme-le, puis réessaie.", DebugPort));
            }
            Console.WriteLine(L.T("Lancement de Stremio en mode Watch Together…"));
            if (IsBrowser(exe))
            {
                // Rien à fermer : l'appli Stremio reste ouverte pour son serveur de torrents.
                Process.Start(BrowserLaunch(exe));
                if (!await StreamingServerUp() && !IsWindows) Console.WriteLine(L.T("Astuce : ouvre aussi l'appli Stremio pour lire les torrents (les liens Real-Debrid marchent sans)."));
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
            try { return await WaitForPage(exe, retrying: true); }
            catch (TimeoutException)
            {
                // Stremio lent à démarrer (PC chargé, mise à jour au lancement) : on le relance une fois.
                Program.Log("Stremio ne répond pas, nouvelle tentative");
                Console.WriteLine(L.T("Stremio met du temps à répondre, nouvelle tentative…"));
                foreach (var p in new[] { "stremio-shell-ng", "stremio", "stremio-runtime" }.SelectMany(Process.GetProcessesByName))
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                await Task.Delay(1500);
                if (IsWindows) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                else Process.Start(LinuxLaunch(exe));
                return await WaitForPage(exe);
            }
        }

        // Le port de pilotage répond, mais ce n'est pas Stremio : un autre programme l'occupe.
        static async Task<bool> PortTakenByOther()
        {
            try { using (var cts = new CancellationTokenSource(1500)) using (await Http.GetAsync($"http://127.0.0.1:{DebugPort}/json", cts.Token)) return true; }
            catch { return false; }
        }

        // Jusqu'à 60 s (premier lancement, PC lent). « retrying » : un échec sera retenté, pas encore signalé.
        static async Task<string> WaitForPage(string exe, bool retrying = false)
        {
            string ws;
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                if ((ws = await FindPageWsUrl()) != null) return ws;
            }
            if (retrying) throw new TimeoutException();
            Telemetry.Error("stremio-noreply");
            throw new Exception(L.T("Stremio ne répond pas. Ferme Stremio complètement (aussi l'icône près de l'horloge), puis réessaie."));
        }
    }
}
