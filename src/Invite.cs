using static StremioWT.Program;

namespace StremioWT
{
    static class Invite
    {
        internal static string Make(string server, string room)
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["s"] = server, ["r"] = room })));
            return "WT-" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
