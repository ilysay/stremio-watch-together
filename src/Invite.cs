using static StremioWT.Program;

namespace StremioWT
{
    static class Invite
    {
        // Lien d'invitation : une page qui reconnaît le système de l'ami (téléchargement adapté) ou ouvre l'appli.
        // Le code est après le « # », donc jamais envoyé au serveur de la page.
        internal const string JoinPage = "https://join.stremiowt.workers.dev/";
        internal static string Link(string code) => JoinPage + "#" + code;

        internal static string Make(string server, string room)
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["s"] = server, ["r"] = room })));
            return "WT-" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Code court « K7MQ2X9F » : 8 caractères faciles à dicter (sans I, L, O ni U), que le serveur de StremioWT
        // relie au salon pendant 24 h. Le code long reste la référence : le lien d'invitation l'utilise, et si le
        // serveur ne répond pas, l'hôte n'affiche simplement que le code long.
        const string ShortAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        static readonly string ShortServer = Telemetry.Url + "/room";
        static readonly Regex ShortTarget = new Regex(@"^(https://[a-z0-9-]{3,80}\.trycloudflare\.com|http://(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}):\d{1,5})$");

        // Affiché tel quel, sans préfixe ni tiret : c'est ce qu'on recopie le plus facilement.
        internal static string ShowShort(string code) => code;

        // anywhere : cherché dans un texte (presse-papier) : précédé de « WT- », ou seul dans le texte (code copié
        // depuis l'appli de l'hôte) ; seul, il doit mêler chiffres et lettres, pour ne pas prendre un mot pour un code.
        // Sinon le texte entier est le code tapé à la main, avec ou sans WT- et tiret (« k7mq2x9f », « K7MQ-2X9F »).
        internal static string ShortIn(string text, bool anywhere)
        {
            var m = anywhere
                ? Regex.Match(text ?? "", @"(?<![0-9A-Za-z_-])WT-?([0-9A-Za-z]{4})-?([0-9A-Za-z]{4})(?![0-9A-Za-z_-])", RegexOptions.IgnoreCase)
                : Regex.Match(text ?? "", @"^\s*(?:WT-?)?([0-9A-Za-z]{4})[-\s]?([0-9A-Za-z]{4})\s*$", RegexOptions.IgnoreCase);
            bool alone = false;
            if (!m.Success && anywhere) { m = Regex.Match(text ?? "", @"^\s*([0-9A-Za-z]{4})([0-9A-Za-z]{4})\s*$"); alone = true; }
            if (!m.Success) return null;
            // Ce qu'on confond à l'oral ou à la lecture : O → 0, I et L → 1.
            var raw = m.Groups[1].Value + m.Groups[2].Value;
            if (alone && !(raw.Any(char.IsDigit) && raw.Any(char.IsLetter))) return null;
            var code = raw.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');
            return code.All(c => ShortAlphabet.IndexOf(c) >= 0) ? code : null;
        }

        // Hôte : réserve un code court pour ce salon. null si le serveur ne répond pas (on garde le code long).
        internal static async Task<string> RegisterShort(string server, string room)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var b = new byte[8];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
                var code = new string(b.Select(x => ShortAlphabet[x % 32]).ToArray());
                // Toujours chiffres et lettres mêlés : un code copié seul est ainsi reconnu dans le presse-papier.
                if (!code.Any(char.IsDigit) || !code.Any(char.IsLetter)) { attempt--; continue; }
                try
                {
                    var body = Json.Serialize(new Dictionary<string, object> { ["code"] = code, ["s"] = server, ["r"] = room });
                    using (var cts = new CancellationTokenSource(6000))
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var res = await Http.PostAsync(ShortServer, content, cts.Token))
                    {
                        // Seule une vraie réponse { "ok": true } compte : un serveur pas à jour répond 200 avec du texte.
                        if (res.IsSuccessStatusCode && Answer(await res.Content.ReadAsStringAsync()) is Dictionary<string, object> ok && ok.TryGetValue("ok", out var v) && v is bool done && done) return code;
                        if ((int)res.StatusCode != 409) { Log($"code court : HTTP {(int)res.StatusCode}, pas de code court"); return null; }
                    }
                }
                catch (Exception e) { Log("code court : " + e.Message); return null; }
            }
            return null;
        }

        // Invité : retrouve le salon d'un code court.
        internal static async Task<(string server, string room)> ResolveShort(string code)
        {
            HttpResponseMessage res;
            try
            {
                using (var cts = new CancellationTokenSource(8000))
                    res = await Http.GetAsync(ShortServer + "/" + code, cts.Token);
            }
            catch (Exception e)
            {
                Log("code court : " + e.Message);
                throw new Exception(L.T("Impossible de vérifier ce code pour le moment. Demande le lien d'invitation à ton ami."));
            }
            using (res)
            {
                if ((int)res.StatusCode == 404) throw new Exception(L.F("Aucun salon ne correspond au code {0}. Vérifie-le, ou demande le lien d'invitation.", ShowShort(code)));
                if (!res.IsSuccessStatusCode) throw new Exception(L.T("Impossible de vérifier ce code pour le moment. Demande le lien d'invitation à ton ami."));
                var o = Answer(await res.Content.ReadAsStringAsync());
                // Mêmes formats que ceux acceptés par le serveur : un tunnel Cloudflare jetable ou une adresse du réseau local.
                if (o != null && o.TryGetValue("s", out var s) && s is string srv && ShortTarget.IsMatch(srv) && o.TryGetValue("r", out var r) && r is string rm && Regex.IsMatch(rm, "^[0-9A-Fa-f]{6}$"))
                    return (srv, rm.ToUpperInvariant());
                throw new Exception(L.T("Impossible de vérifier ce code pour le moment. Demande le lien d'invitation à ton ami."));
            }
        }

        // Réponse JSON du serveur, ou null si ce n'en est pas une (serveur pas à jour, page d'erreur…).
        static Dictionary<string, object> Answer(string text)
        {
            try { return Json.Deserialize<Dictionary<string, object>>(text); }
            catch { return null; }
        }

        internal static (string server, string room)? Parse(string text)
        {
            var m = Regex.Match(text ?? "", @"WT-([A-Za-z0-9_-]{8,})");
            if (!m.Success) return null;
            try
            {
                var b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                b64 += new string('=', (4 - b64.Length % 4) % 4);
                var o = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                if (o.TryGetValue("s", out var s) && s is string srv && Regex.IsMatch(srv, "^https?://") && o.TryGetValue("r", out var r) && r is string rm)
                    return (srv.TrimEnd('/'), rm.ToUpperInvariant());
            }
            catch { }
            return null;
        }
    }
}
