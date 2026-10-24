namespace StremioWT
{
    // Nouveautés montrées une fois après une mise à jour (Windows : WhatsNewForm, Linux et Mac : WhatsNewWindow).
    // La liste est à réécrire à chaque version publiée (textes traduits dans L.cs).
    static class WhatsNew
    {
        internal static (string Icon, string Title, string Body)[] Items => new[]
        {
            ("🔗", L.T("Lien d'invitation"), L.T("Envoie le lien à tes amis : ceux qui n'ont pas l'appli la téléchargent et rejoignent ton salon tout seuls.")),
            ("🔄", L.T("Mise à jour en un clic"), L.T("Plus besoin de retourner sur GitHub : l'appli télécharge la nouvelle version, vérifie son empreinte et redémarre.")),
            ("🌍", L.T("Disponible en 15 langues"), L.T("L'appli s'affiche directement dans la langue de ton ordinateur. Tu peux la changer dans les Paramètres.")),
        };
    }
}
