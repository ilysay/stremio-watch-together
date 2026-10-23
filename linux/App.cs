using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace StremioWT
{
    // Couleurs du logo, comme l'édition Windows.
    static class Palette
    {
        public static readonly Color Background = Color.FromRgb(6, 19, 45);
        public static readonly Color Card = Color.FromRgb(12, 32, 72);
        public static readonly Color Border = Color.FromRgb(30, 62, 130);
        public static readonly Color Input = Color.FromRgb(8, 24, 56);
        public static readonly Color Accent = Color.FromRgb(47, 128, 255);
        public static readonly Color Text = Color.FromRgb(232, 240, 255);
        public static readonly Color Muted = Color.FromRgb(140, 160, 200);
        public static readonly Color Ok = Color.FromRgb(61, 220, 132);
        public static readonly Color Bad = Color.FromRgb(255, 99, 99);
        public static IBrush Brush(Color c) => new SolidColorBrush(c);
    }

    class App : Application
    {
        public static MainWindow MainWindow { get; private set; }

        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Resources["SystemAccentColor"] = Palette.Accent;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }
}
