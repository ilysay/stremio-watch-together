namespace StremioWT
{
    // Nouveautés montrées une fois après une mise à jour (Windows : WhatsNewForm, Linux et Mac : WhatsNewWindow).
    // La liste est à réécrire à chaque version publiée (textes traduits dans L.cs).
    static class WhatsNew
    {
        internal static (string Icon, string Title, string Body)[] Items => new[]
        {
            ("🎬", L.T("Watch Together dans Stremio"), L.T("Un bouton Watch Together apparaît dans Stremio : héberge, rejoins et invite tes amis sans quitter Stremio.")),
            ("🔢", L.T("Codes de salon courts"), L.T("Ton salon a un code de 8 caractères, facile à dicter ou à taper. Le lien d'invitation marche toujours.")),
            ("🎨", L.T("Nouveau look"), L.T("Les couleurs et les icônes de Stremio, dans l'appli comme dans Stremio.")),
            ("🔄", L.T("Synchro plus fiable"), L.T("Changements d'épisode mieux suivis, salon gardé si Stremio se ferme, et alerte si le téléchargement de quelqu'un est trop lent.")),
        };
    }
}
