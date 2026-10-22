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

        [Fact]
        public void RejectsNonHttpServer()
        {
            var raw = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"s\":\"file:///C:/\",\"r\":\"AAA\"}"));
            Assert.Null(Invite.Parse("WT-" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        }
    }
}
