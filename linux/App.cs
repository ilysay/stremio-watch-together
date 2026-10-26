using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace StremioWT
{
    // Couleurs de Stremio (fond presque noir, violet #7b5bf5), comme l'édition Windows et le panneau dans Stremio.
    static class Palette
    {
        public static readonly Color Background = Color.FromRgb(13, 12, 20);
        public static readonly Color Card = Color.FromRgb(24, 22, 36);
        public static readonly Color Border = Color.FromArgb(34, 255, 255, 255);
        public static readonly Color Input = Color.FromRgb(33, 31, 48);
        public static readonly Color InputHover = Color.FromRgb(45, 42, 64);
        public static readonly Color Accent = Color.FromRgb(123, 91, 245);
        public static readonly Color AccentHover = Color.FromRgb(142, 114, 255);
        public static readonly Color Text = Color.FromRgb(236, 235, 242);
        public static readonly Color Muted = Color.FromRgb(150, 146, 172);
        public static readonly Color Ok = Color.FromRgb(52, 199, 118);
        public static readonly Color Bad = Color.FromRgb(255, 107, 107);
        public static readonly Color Danger = Color.FromRgb(220, 38, 38);
        // Remplissage translucide des cartes : sur le fond uni il donne Card, et laisse passer le halo violet.
        public static readonly Color CardGlass = Color.FromArgb(128, 35, 32, 52);
        public static IBrush Brush(Color c) => new SolidColorBrush(c);
    }

    class App : Application
    {
        public static MainWindow MainWindow { get; private set; }

        public override void Initialize()
        {
            Name = "StremioWT"; // nom affiché dans la barre de menus du Mac (sinon « Avalonia Application »)
            RequestedThemeVariant = ThemeVariant.Dark;
            Resources["SystemAccentColor"] = Palette.Accent;
            Resources["SystemAccentColorLight1"] = Palette.AccentHover;
            Resources["SystemAccentColorDark1"] = Palette.Accent;
            // Boutons et champs aux couleurs de Stremio (clés de ressources du thème Fluent).
            var input = Palette.Brush(Palette.Input);
            foreach (var (key, color) in new[] {
                ("ButtonBackground", Palette.Input), ("ButtonBackgroundPointerOver", Palette.InputHover), ("ButtonBackgroundPressed", Palette.Input),
                ("ButtonBackgroundDisabled", Color.FromArgb(120, 33, 31, 48)), ("ButtonForeground", Palette.Text), ("ButtonForegroundPointerOver", Colors.White),
                ("ButtonForegroundPressed", Palette.Text), ("ButtonForegroundDisabled", Palette.Muted),
                ("ButtonBorderBrush", Colors.Transparent), ("ButtonBorderBrushPointerOver", Colors.Transparent), ("ButtonBorderBrushPressed", Colors.Transparent), ("ButtonBorderBrushDisabled", Colors.Transparent),
                ("AccentButtonBackground", Palette.Accent), ("AccentButtonBackgroundPointerOver", Palette.AccentHover), ("AccentButtonBackgroundPressed", Palette.Accent),
                ("AccentButtonBackgroundDisabled", Color.FromRgb(62, 52, 104)), ("AccentButtonForeground", Colors.White), ("AccentButtonForegroundPointerOver", Colors.White),
                ("AccentButtonForegroundPressed", Colors.White), ("AccentButtonForegroundDisabled", Color.FromRgb(170, 160, 210)),
                ("AccentButtonBorderBrush", Colors.Transparent), ("AccentButtonBorderBrushPointerOver", Colors.Transparent), ("AccentButtonBorderBrushPressed", Colors.Transparent), ("AccentButtonBorderBrushDisabled", Colors.Transparent),
                ("TextControlBackground", Palette.Input), ("TextControlBackgroundPointerOver", Palette.InputHover), ("TextControlBackgroundFocused", Palette.Input),
                ("TextControlBackgroundDisabled", Color.FromArgb(120, 33, 31, 48)), ("TextControlBorderBrush", Color.FromArgb(28, 255, 255, 255)),
                ("TextControlBorderBrushPointerOver", Color.FromArgb(50, 255, 255, 255)), ("TextControlBorderBrushFocused", Palette.Accent),
                ("TextControlForeground", Palette.Text), ("TextControlForegroundPointerOver", Palette.Text), ("TextControlForegroundFocused", Palette.Text),
                ("TextControlPlaceholderForeground", Color.FromArgb(110, 255, 255, 255)), ("TextControlPlaceholderForegroundPointerOver", Color.FromArgb(110, 255, 255, 255)),
                ("TextControlPlaceholderForegroundFocused", Color.FromArgb(110, 255, 255, 255)),
                ("ToggleSwitchFillOff", Color.FromRgb(58, 55, 76)), ("ToggleSwitchFillOffPointerOver", Color.FromRgb(70, 66, 90)),
                ("ToggleSwitchStrokeOff", Colors.Transparent), ("ToggleSwitchStrokeOffPointerOver", Colors.Transparent),
                ("ToggleSwitchKnobFillOff", Colors.White), ("ToggleSwitchKnobFillOffPointerOver", Colors.White),
            })
                Resources[key] = new SolidColorBrush(color);
            Styles.Add(new FluentTheme());
            // Boutons en pilule et champs arrondis, comme dans Stremio.
            Styles.Add(new Style(x => x.OfType<Avalonia.Controls.Button>())
            {
                Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty, new CornerRadius(999)), new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontWeightProperty, FontWeight.SemiBold) }
            });
            Styles.Add(new Style(x => x.OfType<Avalonia.Controls.Button>().Class("danger"))
            {
                Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    new Setter(Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(120, 220, 38, 38))),
                    new Setter(Avalonia.Controls.Primitives.TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                    new Setter(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 138, 138))) }
            });
            Styles.Add(new Style(x => x.OfType<Avalonia.Controls.TextBox>())
            {
                Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty, new CornerRadius(12)), new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 40.0),
                    new Setter(Avalonia.Controls.ContentControl.VerticalContentAlignmentProperty, Avalonia.Layout.VerticalAlignment.Center) }
            });
            Styles.Add(new Style(x => x.OfType<Avalonia.Controls.ComboBox>())
            {
                Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty, new CornerRadius(12)) }
            });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Test : --test-gif <lien Giphy> ouvre seulement une fenêtre qui joue ce GIF comme dans le chat.
                var args = desktop.Args ?? Array.Empty<string>();
                int gif = Array.IndexOf(args, "--test-gif");
                if (gif >= 0 && gif + 1 < args.Length)
                {
                    var img = new Avalonia.Controls.Image { MaxHeight = 120, MaxWidth = 200 };
                    desktop.MainWindow = new Avalonia.Controls.Window { Title = "GIF test", Width = 260, Height = 180, Content = img, Background = Palette.Brush(Palette.Background) };
                    _ = MainWindow.LoadImage(img, args[gif + 1], animate: true);
                }
                else desktop.MainWindow = MainWindow = new MainWindow();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
