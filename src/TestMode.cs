using static StremioWT.Program;

namespace StremioWT
{
    // Essais automatiques sans interface, entre Windows et Linux :
    //   --test-host [--lan]   héberge un salon et affiche « INVITE <code> »
    //   --test-join <code>    rejoint ce salon
    // Tout s'affiche sur la sortie d'erreur ; la config de l'utilisateur n'est pas modifiée.
    static class TestMode
    {
        internal static bool Active;

        internal static bool TryRun(string[] args)
        {
            // --test-update : cherche la dernière version, l'installe à la place de cet exécutable et dit ce qui s'est passé.
            if (args.Contains("--test-update"))
            {
                Active = true;
                var version = Updates.NewerVersion().GetAwaiter().GetResult();
                Console.Error.WriteLine("NOUVELLE VERSION " + (version ?? "aucune"));
                if (version != null) Console.Error.WriteLine("INSTALLATION " + (Updates.Install(p => { if (p % 25 == 0) Console.Error.WriteLine(p + " %"); }).GetAwaiter().GetResult() ? "OK" : "ECHEC"));
                return true;
            }
            int join = Array.IndexOf(args, "--test-join");
            bool host = args.Contains("--test-host");
            if (!host && (join < 0 || join + 1 >= args.Length)) return false;
            Active = true;
            Config["telemetry"] = false;
            Output += line => Console.Error.WriteLine(line);
            var os = IsWindows ? "Windows" : IsMac ? "Mac" : "Linux";
            try
            {
                Session session;
                if (host)
                {
                    var (s, code, _) = Rooms.Host("Hote-" + os, args.Contains("--lan")).GetAwaiter().GetResult();
                    session = s;
                    Console.Error.WriteLine("INVITE " + code);
                }
                else session = Rooms.Join("Ami-" + os, args[join + 1]).GetAwaiter().GetResult();
                session.ChatReceived += m => { if (!m.Mine) Console.Error.WriteLine("CHAT " + m.From + " : " + m.Text); };
                // Dès qu'un autre membre arrive, on le salue : chaque côté vérifie ainsi qu'il reçoit l'autre.
                int seen = 1;
                session.StateChanged += () =>
                {
                    if (session.Members.Count <= seen) return;
                    seen = session.Members.Count;
                    Console.Error.WriteLine("MEMBRES " + string.Join(", ", session.Members));
                    session.SendChat("hello from " + os, null);
                };
                session.Run().GetAwaiter().GetResult();
            }
            catch (Exception e) { Console.Error.WriteLine("ECHEC " + e.Message); Environment.Exit(1); }
            return true;
        }
    }
}
