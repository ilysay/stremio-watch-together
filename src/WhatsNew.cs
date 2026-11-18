namespace StremioWT
{
    // Nouveautés montrées une fois après une mise à jour (Windows : WhatsNewForm, Linux et Mac : WhatsNewWindow).
    // La liste est à réécrire à chaque version publiée (textes traduits dans L.cs).
    static class WhatsNew
    {
        internal static (string Icon, string Title, string Body)[] Items => new[]
        {
            ("🎰", L.T("Roulette à votre façon"), L.T("Choisis tes genres, ou faites votre liste : tout le salon cherche et ajoute des films, avec un aperçu de chacun.")),
            ("🎬", L.T("Rien devant le film"), L.T("Pendant le film, le bandeau Watch Together se cache avec les commandes de Stremio et revient dès que tu bouges la souris.")),
            ("🖱️", L.T("Le bouton où tu veux"), L.T("Le bouton Watch Together se déplace où tu veux : clique dessus et fais-le glisser, il garde sa place.")),
        };
    }
}
