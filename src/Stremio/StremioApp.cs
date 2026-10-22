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

        internal static string FindExe(Dictionary<string, object> config)
        {
            var candidates = new List<string>();
            try { var p = Process.GetProcessesByName("stremio").FirstOrDefault(); if (p != null) candidates.Add(p.MainModule.FileName); } catch { }
            if (config.TryGetValue("stremioPath", out var sp) && sp is string spath) candidates.Add(spath);
            foreach (var root in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                candidates.Add(Path.Combine(root, "LNV", "Stremio-4", "stremio.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        internal static async Task<string> FindPageWsUrl()
        {
            try
            {
                using (var cts = new CancellationTokenSource(2000))
                {
                    var text = await Http.GetStringAsync($"http://127.0.0.1:{DebugPort}/json").WithCancel(cts.Token);
                    foreach (var t in Json.Deserialize<List<Dictionary<string, object>>>(text))
                        if ((t["type"] as string) == "page" && Regex.IsMatch(t["url"] as string ?? "", @"strem\.io|127\.0\.0\.1:11470"))
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
            foreach (var p in Process.GetProcessesByName("stremio").Concat(Process.GetProcessesByName("stremio-runtime")))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            await Task.Delay(1500);
            // Lancement « normal » (ShellExecute) : Stremio n'hérite d'aucun descripteur de StremioWT.
            // La variable de débogage passe par notre propre environnement, que Stremio reçoit.
            Environment.SetEnvironmentVariable("QTWEBENGINE_REMOTE_DEBUGGING", $"127.0.0.1:{DebugPort}");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
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
