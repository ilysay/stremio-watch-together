using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace StremioWT
{
    // Icônes au trait (style Lucide), les mêmes que celles du panneau Watch Together dans Stremio (inject.js).
    static class Icons
    {
        static string Circle(double cx, double cy, double r) => $"M{cx - r} {cy} a{r} {r} 0 1 0 {2 * r} 0 a{r} {r} 0 1 0 {-2 * r} 0 ";

        static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["user"] = Circle(12, 8, 4) + "M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1",
            ["users"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 " + Circle(9, 7, 4) + "M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75",
            ["play"] = "M6 4.5v15a1 1 0 0 0 1.5 0.86l12.5-7.5a1 1 0 0 0 0-1.72L7.5 3.64A1 1 0 0 0 6 4.5z",
            ["login"] = "M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4 M10 17l5-5-5-5 M15 12H3",
            ["link"] = "M10 13a5 5 0 0 0 7.54 0.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-0.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
            ["copy"] = "M11 9h9a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-9a2 2 0 0 1-2-2v-9a2 2 0 0 1 2-2z M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1",
            ["chat"] = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z",
            ["leave"] = "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9",
            ["crown"] = "M2 6l4.5 4L12 4l5.5 6L22 6l-2 13H4z",
            ["wifi"] = "M5 12.55a11 11 0 0 1 14.08 0 M1.42 9a16 16 0 0 1 21.16 0 M8.53 16.11a6 6 0 0 1 6.95 0 M12 20h0.01",
            ["alert"] = Circle(12, 12, 10) + "M12 8v4 M12 16h0.01",
            ["send"] = "M22 2l-7 20-4-9-9-4z M22 2L11 13",
            ["smile"] = Circle(12, 12, 10) + "M8 14s1.5 2 4 2 4-2 4-2 M9 9h0.01 M15 9h0.01",
            ["settings"] = Circle(12, 12, 3) + "M19.4 15a1.65 1.65 0 0 0 0.33 1.82l0.06 0.06a2 2 0 1 1-2.83 2.83l-0.06-0.06a1.65 1.65 0 0 0-1.82-0.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-0.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82 0.33l-0.06 0.06a2 2 0 1 1-2.83-2.83l0.06-0.06a1.65 1.65 0 0 0 0.33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h0.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-0.33-1.82l-0.06-0.06a2 2 0 1 1 2.83-2.83l0.06 0.06a1.65 1.65 0 0 0 1.82 0.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v0.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-0.33l0.06-0.06a2 2 0 1 1 2.83 2.83l-0.06 0.06a1.65 1.65 0 0 0-0.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-0.09a1.65 1.65 0 0 0-1.51 1z",
            ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3",
            ["book"] = "M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20",
            ["search"] = Circle(11, 11, 8) + "M21 21l-4.3-4.3",
        };

        // brush null : l'icône prend la couleur du texte du bouton qui la contient (et suit son survol).
        public static Control Make(string name, double size = 16, IBrush brush = null)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(Paths[name].Replace(",", " ")),
                StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            };
            if (brush != null) path.Stroke = brush;
            else path[!Shape.StrokeProperty] = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ContentPresenter) } };
            return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        }

        // Contenu de bouton : icône + texte (ou icône seule).
        public static object Label(string icon, string text)
        {
            if (icon == null) return text;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(Make(icon, 16));
            if (!string.IsNullOrEmpty(text)) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
    }
}
