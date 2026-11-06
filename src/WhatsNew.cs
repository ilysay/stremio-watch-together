namespace StremioWT
{
    // Nouveautés montrées une fois après une mise à jour (Windows : WhatsNewForm, Linux et Mac : WhatsNewWindow).
    // La liste est à réécrire à chaque version publiée (textes traduits dans L.cs).
    static class WhatsNew
    {
        internal static (string Icon, string Title, string Body)[] Items => new[]
        {
            ("🛠️", L.T("Plus stable"), L.T("Une connexion lente ne coupe plus la lecture toutes les quelques secondes, et on peut de nouveau taper le code du salon dans Stremio.")),
            ("🍎", L.T("Bug du port 9333 corrigé sur Mac"), L.T("Fini l'erreur « port 9333 déjà utilisé » : si le navigateur est resté ouvert, StremioWT y rouvre simplement Stremio.")),
        };
    }
}
