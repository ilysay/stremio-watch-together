using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace StremioWT.Tests
{
    public class TranslationTests
    {
        static string[][] Table() => (string[][])typeof(L).GetField("Table", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Cast<Match>().Select(m => m.Value).OrderBy(x => x));

        [Fact]
        public void EveryTextHasEveryLanguage()
        {
            foreach (var row in Table())
            {
                Assert.True(row.Length == L.Languages.Length, $"« {row[0]} » : {row.Length} colonnes au lieu de {L.Languages.Length}");
                Assert.All(row, t => Assert.False(string.IsNullOrEmpty(t), $"traduction vide pour « {row[0]} »"));
            }
        }

        [Fact]
        public void KeysAreUnique()
        {
            var duplicates = Table().GroupBy(r => r[0]).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.Empty(duplicates);
        }

        [Fact]
        public void PlaceholdersMatchInEveryLanguage()
        {
            foreach (var row in Table())
                for (int i = 1; i < row.Length; i++)
                    Assert.True(Placeholders(row[0]) == Placeholders(row[i]), $"« {row[0]} » ({L.Languages[i].Code}) : repères différents");
        }

        [Fact]
        public void NoEmDashInVisibleTexts() => Assert.DoesNotContain(Table().SelectMany(r => r), t => t.Contains("—"));
    }
}
