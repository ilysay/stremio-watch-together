namespace StremioWT
{
    // Démarrage et services propres à Windows (Windows Forms).
    static partial class Program
    {
        static Mutex singleInstance;

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            L.Init();
            LaunchInvite = InviteFromArgs(args);
            // Un seul exemplaire à la fois : deux copies ouvriraient deux salons et piloteraient le même Stremio.
            singleInstance = new Mutex(true, @"Local\StremioWatchTogether", out bool firstInstance);
            if (!firstInstance && LaunchInvite != null)
            {
                // Lien cliqué alors que l'appli tourne déjà : l'invitation part dans le presse-papier.
                SetClipboardText(LaunchInvite);
                MessageBox.Show(L.T("StremioWT est déjà ouvert : l'invitation a été copiée. Colle-la dans « Rejoindre un salon »."),
                    "Stremio Watch Together", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!firstInstance)
            {
                MessageBox.Show(L.T("StremioWT est déjà ouvert sur ce PC.\nUtilise la fenêtre déjà ouverte, ou ferme-la avant d'en relancer une."),
                    "Stremio Watch Together", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenLog();
            if (TestMode.TryRun(args)) return;
            RegisterLinkHandler();
            Telemetry.Send("launch");
            Application.Run(new MainForm());
        }

        // Les liens « stremiowt:// » de la page d'invitation ouvrent cette appli (pour l'utilisateur, sans droits admin).
        static void RegisterLinkHandler()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\stremiowt"))
                {
                    key.SetValue("", "URL:Stremio Watch Together");
                    key.SetValue("URL Protocol", "");
                    using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{Application.ExecutablePath}\" \"%1\"");
                }
            }
            catch (Exception e) { Log("liens stremiowt:// : " + e.Message); }
        }

        internal static void Fail(string msg)
        {
            Log("arrêt : " + msg);
            var report = MessageBox.Show(msg + "\n\n" + L.T("Envoyer un rapport de bug au développeur ?"), "Stremio Watch Together", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (report == DialogResult.Yes) new ReportForm(msg).ShowDialog();
            Environment.Exit(1);
        }

        internal static void Restart()
        {
            Log("relance");
            try { singleInstance.ReleaseMutex(); singleInstance.Dispose(); } catch { }
            Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
            Environment.Exit(0);
        }

        internal static string GetClipboardText() => Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "";

        internal static void SetClipboardText(string text) => Sta(() => { Clipboard.SetText(text); return true; });

        // Le presse-papier Windows exige un fil STA ; les appels peuvent venir de n'importe quel fil.
        static T Sta<T>(Func<T> f)
        {
            T result = default;
            var t = new Thread(() => { try { result = f(); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return result;
        }
    }
}
