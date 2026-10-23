using static StremioWT.Program;

namespace StremioWT
{
    static class Rooms
    {
        const int RelayPort = 8787;

        internal static string DefaultName() =>
            Config.TryGetValue("name", out var n) && n is string s && s.Length > 0 ? s : (Environment.UserName ?? L.T("Invité"));

        // Code d'invitation présent dans le presse-papier (sauf celui qu'on a soi-même créé en hébergeant).
        internal static (string server, string room)? InviteFromClipboard()
        {
            var clip = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "";
            var lastHosted = Config.TryGetValue("lastHosted", out var lh) ? lh as string : null;
            return lastHosted != null && clip.Contains(lastHosted) ? null : Invite.Parse(clip);
        }

        internal static string RequireStremio()
        {
            var exe = StremioApp.FindExe(Config);
            if (exe != null) return exe;
            Telemetry.Error("stremio-missing");
            throw new Exception(L.T("Stremio est introuvable. Installe Stremio pour Windows depuis https://www.stremio.com/downloads puis relance."));
        }

        static void Remember(string name, string stremioExe)
        {
            Config["name"] = name;
            Config["stremioPath"] = stremioExe;
            SaveConfig(Config);
        }

        internal static async Task<(Session session, string code)> Host(string name, bool local)
        {
            var stremioExe = RequireStremio();
            var relay = new Relay();
            int port = relay.Start(RelayPort, lan: local);
            if (!await Relay.Answers(port))
            {
                // Port 8787 occupé de façon invisible (ancienne session) : on en prend un libre.
                Log($"relay : le port {port} ne répond pas, passage sur un autre port");
                relay = new Relay();
                port = relay.Start(0, lan: local);
            }
            var room = RandomHex(3).ToUpperInvariant();
            string publicUrl;
            if (local)
            {
                publicUrl = LanUrl(port);
                Console.WriteLine(L.T("Mode réseau local uniquement."));
            }
            else
            {
                Console.WriteLine(L.T("Ouverture de l'accès Internet…"));
                try { publicUrl = await Tunnel.Start(port); }
                catch (Exception e) { Telemetry.Error("tunnel"); throw new Exception(L.F("Accès Internet impossible ({0}). Si vous êtes sur le même wifi, coche l'option « même wifi seulement ».", e.Message)); }
            }
            var code = Invite.Make(publicUrl, room);
            Config["lastHosted"] = code;
            Sta(() => { Clipboard.SetText(code); return true; });
            Console.WriteLine(L.T("Code d'invitation copié : colle-le à tes amis."));
            Remember(name, stremioExe);
            _ = Telemetry.Send("host", 1);
            return (new Session($"http://127.0.0.1:{port}", room, name, true, stremioExe, relay), code);
        }

        // Message à coller aux amis : le lien de téléchargement pour ceux qui n'ont pas l'appli, puis le code
        // (l'appli retrouve le code WT-… n'importe où dans le texte copié).
        internal static string InviteMessage(string code) => L.F("Rejoins ma soirée film sur Stremio !\n1. Télécharge l'appli : {0}\n2. Copie ce message puis ouvre StremioWT.\n\n{1}", Updates.DownloadUrl, code);

        internal static Session Join(string name, string inviteText)
        {
            var invite = Invite.Parse(inviteText) ?? throw new Exception(L.T("Ce code n'est pas valide : il doit commencer par WT-"));
            var stremioExe = RequireStremio();
            Remember(name, stremioExe);
            Telemetry.Send("join");
            return new Session(invite.server, invite.room, name, false, stremioExe, null);
        }

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        static string LanUrl(int port)
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return $"http://{ip?.ToString() ?? "127.0.0.1"}:{port}";
        }
    }
}
