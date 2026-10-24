using Avalonia;
using Avalonia.Threading;

namespace StremioWT
{
    // Démarrage et services propres à Linux (Avalonia).
    static partial class Program
    {
        static FileStream instanceLock;
        internal static string ClipboardSnapshot = ""; // lu par l'interface au démarrage (le presse-papier Avalonia est asynchrone)

        [STAThread]
        static void Main(string[] args)
        {
            L.Init();
            // Un seul exemplaire à la fois : deux copies ouvriraient deux salons et piloteraient le même Stremio.
            try
            {
                Directory.CreateDirectory(DataDir);
                instanceLock = new FileStream(Path.Combine(DataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Console.Error.WriteLine(L.T("StremioWT est déjà ouvert sur ce PC.\nUtilise la fenêtre déjà ouverte, ou ferme-la avant d'en relancer une."));
                return;
            }
            OpenLog();
            if (TestMode.TryRun(args)) return;
            LaunchInvite = InviteFromArgs(args);
            _ = Telemetry.Send("launch");
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
        }

        internal static void Fail(string msg)
        {
            if (App.MainWindow == null) { Console.Error.WriteLine("ECHEC " + msg); Environment.Exit(1); }
            Log("arrêt : " + msg);
            Dispatcher.UIThread.Post(async () =>
            {
                if (await Dialogs.ErrorWithReport(App.MainWindow, msg))
                {
                    var report = new ReportWindow(msg);
                    var closed = new TaskCompletionSource<bool>();
                    report.Closed += (s, e) => closed.TrySetResult(true);
                    if (App.MainWindow.IsVisible) _ = report.ShowDialog(App.MainWindow); else report.Show();
                    await closed.Task;
                }
                Environment.Exit(1);
            });
        }

        internal static void Restart()
        {
            Log("relance (changement de langue)");
            try { instanceLock?.Dispose(); } catch { }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false });
            Environment.Exit(0);
        }

        internal static string GetClipboardText() => ClipboardSnapshot ?? "";

        internal static void SetClipboardText(string text)
        {
            if (App.MainWindow == null) return;
            Dispatcher.UIThread.Post(async () => { try { await App.MainWindow.Clipboard.SetTextAsync(text); } catch { } });
        }
    }
}
