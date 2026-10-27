using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StremioWT.Tests
{
    public class TasteTests
    {
        static readonly DateTime Today = new DateTime(2026, 10, 27);

        static StremioFilm Film(string id, string date = "2026-01-01", string type = "movie", int episodes = 0) =>
            new StremioFilm { ImdbId = id, WatchedDate = date, Type = type, EpisodesWatched = episodes };

        static TasteProfile Profile(int titles, params (string genre, double share)[] genres) =>
            new TasteProfile { Titles = titles, Genres = genres.ToDictionary(g => g.genre, g => g.share) };

        [Fact]
        public void ProfileIsTheShareOfEachGenreInTheHistory()
        {
            var history = new[] { Film("tt1"), Film("tt2"), Film("tt3"), Film("tt4") };
            var genres = new Dictionary<string, List<string>>
            {
                ["tt1"] = new List<string> { "Comedy" }, ["tt2"] = new List<string> { "Comedy", "Action" },
                ["tt3"] = new List<string> { "Action" }, ["tt4"] = new List<string> { "Comedy" },
            };
            var p = Taste.Build(history, genres, Today);
            Assert.Equal(4, p.Titles);
            Assert.Equal(0.75, p.Genres["Comedy"]);
            Assert.Equal(0.5, p.Genres["Action"]);
        }

        [Fact]
        public void RecentTitlesCountMore()
        {
            var history = new[] { Film("tt1", "2026-10-20"), Film("tt2", "2025-01-01") };
            var genres = new Dictionary<string, List<string>> { ["tt1"] = new List<string> { "Horror" }, ["tt2"] = new List<string> { "Drama" } };
            var p = Taste.Build(history, genres, Today);
            Assert.True(p.Genres["Horror"] > p.Genres["Drama"]);
        }

        [Fact]
        public void TitlesWithoutKnownGenresAreIgnored()
        {
            var p = Taste.Build(new[] { Film("tt1"), Film("tt2") }, new Dictionary<string, List<string>> { ["tt1"] = new List<string> { "Drama" } }, Today);
            Assert.Equal(1, p.Titles);
        }

        [Fact]
        public void ThinHistoriesDoNotWeighOnTheMix()
        {
            var (combined, _) = Taste.Merge(new[] { Profile(20, ("Comedy", 0.8)), Profile(2, ("Horror", 1.0)) });
            Assert.True(combined.ContainsKey("Comedy"));
            Assert.False(combined.ContainsKey("Horror"));
        }

        [Fact]
        public void MixIsTheAverageOfEveryone()
        {
            var (combined, _) = Taste.Merge(new[] { Profile(10, ("Comedy", 0.6), ("Action", 0.4)), Profile(10, ("Comedy", 0.2), ("Drama", 0.8)) });
            Assert.Equal(0.4, combined["Comedy"], 3);
            Assert.Equal(0.2, combined["Action"], 3);
            Assert.Equal(0.4, combined["Drama"], 3);
        }

        [Fact]
        public void DivisiveGenreIsAvoidedWhenSomeoneNeverWatchesIt()
        {
            var (combined, avoided) = Taste.Merge(new[] { Profile(30, ("Horror", 0.7), ("Comedy", 0.3)), Profile(30, ("Comedy", 0.9), ("Drama", 0.1)) });
            Assert.Contains("Horror", avoided);
            Assert.False(combined.ContainsKey("Horror"));
            Assert.Equal("Comedy", Taste.TopGenres(combined, 1)[0]);
        }

        [Fact]
        public void NobodyWithHistoryMeansPopularMovies()
        {
            var (combined, avoided) = Taste.Merge(new[] { Profile(1, ("Drama", 1.0)) });
            Assert.Empty(combined);
            Assert.Empty(avoided);
            Assert.Empty(Taste.TopGenres(combined, 3));
        }

        [Fact]
        public void RankDropsSeenAvoidedShortAndWeakMovies()
        {
            var combined = new Dictionary<string, double> { ["Comedy"] = 0.6, ["Action"] = 0.3 };
            var pool = new[]
            {
                new RouletteFilm { Id = "tt1", Genres = { "Comedy" }, Rating = 7.5, Minutes = 100 },
                new RouletteFilm { Id = "tt2", Genres = { "Action" }, Rating = 7.5, Minutes = 100 },
                new RouletteFilm { Id = "tt3", Genres = { "Comedy" }, Rating = 8.0, Minutes = 100 },      // déjà vu
                new RouletteFilm { Id = "tt4", Genres = { "Comedy", "Horror" }, Rating = 8.0, Minutes = 100 }, // genre écarté
                new RouletteFilm { Id = "tt5", Genres = { "Comedy" }, Rating = 8.0, Minutes = 30 },      // pas un long métrage
                new RouletteFilm { Id = "tt6", Genres = { "Comedy" }, Rating = 4.0, Minutes = 100 },     // trop mal noté
                new RouletteFilm { Id = "tt1", Genres = { "Comedy" }, Rating = 7.5, Minutes = 100 },     // en double
            };
            var ranked = Taste.Rank(pool, combined, new HashSet<string> { "Horror" }, new HashSet<string> { "tt3" });
            Assert.Equal(new[] { "tt1", "tt2" }, ranked.Select(f => f.Id));
        }

        [Fact]
        public void WinnerFavorsTheBestMatchButStaysRandom()
        {
            var shortlist = new List<RouletteFilm> { new RouletteFilm { Id = "a", Score = 0.9 }, new RouletteFilm { Id = "b", Score = 0.3 } };
            var rng = new Random(42);
            var wins = Enumerable.Range(0, 2000).Select(_ => Taste.PickWinner(shortlist, rng)).ToList();
            Assert.True(wins.Count(i => i == 0) > 1500);
            Assert.Contains(1, wins);
        }

        // Pour un même salon, la roue ne doit pas montrer toujours les mêmes films.
        [Fact]
        public void ReelChangesFromOneSpinToTheNext()
        {
            var ranked = Enumerable.Range(1, 60).Select(i => new RouletteFilm { Id = "tt" + i, Score = 1.0 - i / 100.0 }).ToList();
            var rng = new Random(7);
            var first = Taste.SampleReel(ranked, 12, rng, new HashSet<string>());
            var second = Taste.SampleReel(ranked, 12, rng, new HashSet<string>(first.Select(f => f.Id)));
            Assert.Equal(12, first.Select(f => f.Id).Distinct().Count());
            Assert.Empty(first.Select(f => f.Id).Intersect(second.Select(f => f.Id)));
        }

        [Fact]
        public void ReelStillFillsWhenEverythingWasShownRecently()
        {
            var ranked = Enumerable.Range(1, 8).Select(i => new RouletteFilm { Id = "tt" + i, Score = 0.5 }).ToList();
            var reel = Taste.SampleReel(ranked, 12, new Random(1), new HashSet<string>(ranked.Select(f => f.Id)));
            Assert.Equal(8, reel.Count);
        }

        [Fact]
        public void CompatibilityMeasuresSharedGenres()
        {
            var a = Profile(20, ("Comedy", 0.7), ("Action", 0.3));
            Assert.Equal(100, Taste.Compatibility(a, Profile(20, ("Comedy", 0.7), ("Action", 0.3))));
            Assert.Equal(0, Taste.Compatibility(a, Profile(20, ("Horror", 1.0))));
            var partial = Taste.Compatibility(a, Profile(20, ("Comedy", 0.5), ("Drama", 0.5)));
            Assert.InRange(partial, 40, 80);
        }

        [Fact]
        public void BestFitNamesWhoseTasteTheFilmMatches()
        {
            var profiles = new Dictionary<string, TasteProfile> { ["Sam"] = Profile(20, ("Comedy", 0.8)), ["Léa"] = Profile(20, ("Horror", 0.9)), ["Nouveau"] = Profile(1, ("Horror", 1.0)) };
            Assert.Equal("Léa", Taste.BestFit(profiles, new RouletteFilm { Genres = { "Horror" } }));
            Assert.Equal("Sam", Taste.BestFit(profiles, new RouletteFilm { Genres = { "Comedy", "Family" } }));
            Assert.Null(Taste.BestFit(profiles, new RouletteFilm { Genres = { "Western" } }));
        }

        [Fact]
        public void BattleRoyaleEliminatesAllButOne()
        {
            var finalists = new List<RouletteFilm> { new RouletteFilm { Score = 0.9 }, new RouletteFilm { Score = 0.5 }, new RouletteFilm { Score = 0.1 } };
            var rng = new Random(3);
            var survivors = new int[3];
            for (int i = 0; i < 3000; i++)
            {
                var order = Taste.EliminationOrder(finalists, rng);
                Assert.Equal(2, order.Count);
                Assert.Equal(2, order.Distinct().Count());
                survivors[Enumerable.Range(0, 3).Single(k => !order.Contains(k))]++;
            }
            // Le mieux classé survit le plus souvent, mais chacun a sa chance.
            Assert.True(survivors[0] > survivors[1] && survivors[1] > survivors[2]);
            Assert.True(survivors[2] > 0);
        }

        [Theory]
        [InlineData("nanar", 4.5, 95, "1999", "Comedy", true)]
        [InlineData("nanar", 7.5, 95, "1999", "Comedy", false)]
        [InlineData("gem", 7.8, 95, "2015", "Drama", true)]
        [InlineData("gem", 6.8, 95, "2015", "Drama", false)]
        [InlineData("short", 7.0, 95, "2015", "Drama", true)]
        [InlineData("short", 7.0, 130, "2015", "Drama", false)]
        [InlineData("90s", 7.0, 110, "1994", "Drama", true)]
        [InlineData("90s", 7.0, 110, "2004", "Drama", false)]
        [InlineData("horror", 6.0, 90, "2010", "Horror", true)]
        [InlineData("horror", 8.0, 90, "2010", "Comedy", false)]
        [InlineData("classic", 5.0, 90, "2010", "Comedy", false)]
        public void ModesFilterTheCatalog(string mode, double rating, int minutes, string year, string genre, bool kept) =>
            Assert.Equal(kept, RouletteMode.Accepts(mode, new RouletteFilm { Rating = rating, Minutes = minutes, Year = year, Genres = { genre } }));

        [Fact]
        public void HorrorModeIgnoresTheHorrorRejection()
        {
            var pool = new[] { new RouletteFilm { Id = "tt1", Genres = { "Horror" }, Rating = 7, Minutes = 95 } };
            var avoided = new HashSet<string> { "Horror" };
            Assert.Empty(Taste.Rank(pool, new Dictionary<string, double>(), avoided, new HashSet<string>()));
            Assert.Single(Taste.Rank(pool, new Dictionary<string, double>(), avoided, new HashSet<string>(), RouletteMode.Horror));
        }

        [Fact]
        public void NanarModePrefersWorseRatings()
        {
            var bad = new RouletteFilm { Rating = 3.5 };
            var meh = new RouletteFilm { Rating = 5.5 };
            Assert.True(Taste.Score(bad, new Dictionary<string, double>(), RouletteMode.Nanar) > Taste.Score(meh, new Dictionary<string, double>(), RouletteMode.Nanar));
            Assert.Equal("classic", RouletteMode.Check("n'importe quoi"));
        }

        [Fact]
        public void CatalogEntryBecomesAFilm()
        {
            var meta = new Dictionary<string, object>
            {
                ["id"] = "tt0107120", ["name"] = "Hocus Pocus", ["year"] = "1993", ["imdbRating"] = "6.9", ["runtime"] = "96 min",
                ["genres"] = new System.Collections.ArrayList { "Comedy", "Family", "Fantasy" },
                ["trailerStreams"] = new System.Collections.ArrayList { new Dictionary<string, object> { ["ytId"] = "AbCdEfGhI_1" } },
            };
            var f = Taste.ToFilm(meta);
            Assert.Equal(("tt0107120", "1993", 96, 6.9), (f.Id, f.Year, f.Minutes, f.Rating));
            Assert.Equal(new[] { "Comedy", "Family", "Fantasy" }, f.Genres);
            Assert.Equal("AbCdEfGhI_1", f.Trailer);
            Assert.Null(Taste.ToFilm(new Dictionary<string, object> { ["id"] = "javascript:alert(1)" }));
        }
    }
}
