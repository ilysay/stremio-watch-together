namespace StremioWT
{
    // Nouveautés montrées une fois après une mise à jour (Windows : WhatsNewForm, Linux et Mac : WhatsNewWindow).
    // La liste est à réécrire à chaque version publiée (textes traduits dans L.cs).
    static class WhatsNew
    {
        internal static (string Icon, string Title, string Body)[] Items => new[]
        {
            ("🎰", L.T("La roulette"), L.T("Personne n'arrive à choisir ? La roulette tire un film selon les goûts de tout le salon. Thèmes, battle royale, vote et veto.")),
            ("▶️", L.T("On démarre ensemble"), L.T("À chaque nouvel épisode, le salon attend que tout le monde ait chargé, puis tout le monde démarre au même moment.")),
            ("⚡", L.T("Changement de vidéo plus rapide"), L.T("L'ancienne vidéo arrête de se télécharger dès qu'on en change : la nouvelle démarre plus vite.")),
            ("🔧", L.T("Plus fiable"), L.T("Stremio 4 ne décroche plus, fini les fausses alertes de lenteur, et relance automatique après une erreur de lecture sur Linux et Mac.")),
        };
    }
}
