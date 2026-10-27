using Xunit;

namespace StremioWT.Tests
{
    public class InviteTests
    {
        [Fact]
        public void RoundTrip()
        {
            var code = Invite.Make("https://abc-def.trycloudflare.com", "A1B2C3");
            Assert.StartsWith("WT-", code);
            Assert.Equal(("https://abc-def.trycloudflare.com", "A1B2C3"), Invite.Parse(code));
        }

        [Fact]
        public void FoundInsideSurroundingText()
        {
            var code = Invite.Make("http://192.168.1.10:8787", "ff00aa");
            var parsed = Invite.Parse("Rejoins-nous : " + code + " (ce soir 21h)");
            Assert.Equal(("http://192.168.1.10:8787", "FF00AA"), parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bonjour")]
        [InlineData("WT-pasdubase64!!")]
        public void RejectsInvalidCodes(string text) => Assert.Null(Invite.Parse(text));

        [Theory]
        [InlineData("WT-K7MQ-2X9F")]
        [InlineData("wt-k7mq-2x9f")]
        [InlineData("K7MQ2X9F")]
        [InlineData(" k7mq 2x9f ")]
        public void ShortCodeTypedByHand(string text) => Assert.Equal("K7MQ2X9F", Invite.ShortIn(text, anywhere: false));

        [Fact]
        public void ShortCodeConfusableLettersBecomeDigits() => Assert.Equal("K7M02X91", Invite.ShortIn("K7MO-2X9I", anywhere: false));

        [Fact]
        public void ShortCodeFoundInsideMessage() => Assert.Equal("K7MQ2X9F", Invite.ShortIn("Rejoins-nous : WT-K7MQ-2X9F (21h)", anywhere: true));

        [Theory]
        [InlineData("K7MQ-2X9F")]                     // dans un texte, il faut le préfixe WT-
        [InlineData("bonjour à tous")]
        [InlineData("WT-K7MQ-2X9FA")]                 // trop long
        public void ShortCodeNotFoundInText(string text) => Assert.Null(Invite.ShortIn(text, anywhere: true));

        [Fact]
        public void LongCodeIsNotTakenForAShortOne() =>
            Assert.Null(Invite.ShortIn(Invite.Make("https://abc-def.trycloudflare.com", "A1B2C3"), anywhere: true));

        [Fact]
        public void ShortCodeShownPlain() => Assert.Equal("K7MQ2X9F", Invite.ShowShort("K7MQ2X9F"));

        [Theory]
        [InlineData("K7MQ2X9F")]
        [InlineData("  k7mq2x9f\n")]
        public void ShortCodeCopiedAloneFoundInClipboard(string clip) => Assert.Equal("K7MQ2X9F", Invite.ShortIn(clip, anywhere: true));

        [Theory]
        [InlineData("PASSWORD")]                      // un mot de 8 lettres n'est pas un code
        [InlineData("12345678")]                      // ni un nombre
        [InlineData("le code K7MQ2X9F")]              // seul dans le texte, ou précédé de WT-
        public void WordsAndNumbersAreNotCodes(string clip) => Assert.Null(Invite.ShortIn(clip, anywhere: true));

        [Fact]
        public void RejectsNonHttpServer()
        {
            var raw = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"s\":\"file:///C:/\",\"r\":\"AAA\"}"));
            Assert.Null(Invite.Parse("WT-" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        }

        // Un code piégé ne doit pas pouvoir brancher l'invité sur un serveur quelconque.
        [Theory]
        [InlineData("https://evil.example.com", "A1B2C3")]
        [InlineData("http://8.8.8.8:8787", "A1B2C3")]
        [InlineData("https://abc.trycloudflare.com.evil.com", "A1B2C3")]
        [InlineData("https://abc-def.trycloudflare.com", "../../x")]
        public void RejectsUnknownServerOrRoom(string server, string room) => Assert.Null(Invite.Parse(Invite.Make(server, room)));
    }
}
