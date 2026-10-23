using static StremioWT.Program;

namespace StremioWT
{
    // Rapport de bug envoyé au développeur, seulement quand l'utilisateur clique sur « Envoyer »
    // après avoir vu exactement ce qui part : son message, les versions, et la fin du journal de l'appli.
    static class BugReport
    {
        const int MaxLogLines = 300;
        static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

        // Un rapport toutes les 10 minutes au plus depuis ce PC (le serveur limite aussi de son côté).
        internal static TimeSpan WaitBeforeNext()
        {
            if (!Config.TryGetValue("lastReport", out var v) || !DateTime.TryParse(v as string, null, DateTimeStyles.RoundtripKind, out var last)) return TimeSpan.Zero;
            var left = last + MinInterval - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        // Texte complet du rapport, montré tel quel avant l'envoi.
        internal static string Build(string error)
        {
            var sb = new StringBuilder();
            sb.AppendLine("StremioWT " + ScriptVersion + " · " + RuntimeInformation.OSDescription.Trim() + " · " + L.Lang);
            if (!string.IsNullOrEmpty(error)) sb.AppendLine(L.T("Erreur : ") + error);
            sb.AppendLine();
            sb.AppendLine("--- log ---");
            sb.Append(LogTail(MaxLogLines));
            return sb.ToString();
        }

        internal static async Task<bool> Send(string message, string report)
        {
            var body = Json.Serialize(new Dictionary<string, object>
            {
                ["v"] = ScriptVersion,
                ["message"] = (message ?? "").Trim(),
                ["report"] = report,
            });
            try
            {
                using (var cts = new CancellationTokenSource(15000))
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var r = await Http.PostAsync(Telemetry.Url + "/report", content, cts.Token))
                {
                    Log("rapport de bug : " + (int)r.StatusCode);
                    if (!r.IsSuccessStatusCode) return false;
                    Config["lastReport"] = DateTime.UtcNow.ToString("o");
                    SaveConfig(Config);
                    return true;
                }
            }
            catch (Exception e) { Log("rapport de bug non envoyé : " + e.Message); return false; }
        }
    }
}
