namespace StremioWT
{
    // Statistiques anonymes (serveur : telemetry/worker.js). Envoie un identifiant d'installation aléatoire,
    // la version, la langue, l'événement et un détail court (taille du salon, code de problème, durée arrondie).
    // Jamais de pseudo, de titre, de lien, de message ni de code de salon.
    static class Telemetry
    {
        const string Url = "https://stremiowt-stats.stremiowt.workers.dev";

        public static bool Enabled
        {
            get => !(Program.Config.TryGetValue("telemetry", out var t) && t is bool b && !b);
            set { Program.Config["telemetry"] = value; Program.SaveConfig(Program.Config); Program.Log("statistiques anonymes : " + (value ? "activées" : "désactivées")); }
        }

        static string InstallId()
        {
            if (Program.Config.TryGetValue("installId", out var v) && v is string id && id.Length == 32) return id;
            id = Guid.NewGuid().ToString("N");
            Program.Config["installId"] = id;
            Program.SaveConfig(Program.Config);
            return id;
        }

        static int lastSize;
        static readonly HashSet<string> errorsSent = new HashSet<string>();

        public static void Error(string code)
        {
            lock (errorsSent) if (!errorsSent.Add(code)) return;
            Send("error", detail: code);
        }

        public static Task SessionEnded(TimeSpan length)
        {
            var m = length.TotalMinutes;
            var bucket = m < 5 ? "0-5m" : m < 30 ? "5-30m" : m < 60 ? "30-60m" : m < 120 ? "1-2h" : m < 180 ? "2-3h" : "3h+";
            return Send("session", detail: bucket);
        }

        public static Task Send(string ev, int size = 0, string detail = null)
        {
            if (string.IsNullOrEmpty(Url) || !Enabled) return Task.CompletedTask;
            if (ev == "host" && size > 0) { if (size <= lastSize) return Task.CompletedTask; lastSize = size; }
            var data = new Dictionary<string, object> { ["id"] = InstallId(), ["v"] = Program.ScriptVersion, ["event"] = ev, ["size"] = size, ["lang"] = L.Lang };
            if (detail != null) data["detail"] = detail;
            var body = Program.Json.Serialize(data);
            return Task.Run(async () =>
            {
                try
                {
                    using (var cts = new CancellationTokenSource(5000))
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (await Program.Http.PostAsync(Url.TrimEnd('/') + "/ping", content, cts.Token)) { }
                }
                catch { }
            });
        }
    }
}
