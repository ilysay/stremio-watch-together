using Xunit;

namespace StremioWT.Tests
{
    public class ChatTests
    {
        [Theory]
        [InlineData("https://media.giphy.com/media/abc123/giphy.gif")]
        [InlineData("https://media2.giphy.com/media/abc123/200.gif")]
        [InlineData("https://i.giphy.com/abc123.gif")]
        [InlineData("https://media.tenor.com/xyz/tenor.gif")]
        public void AcceptsGiphyAndTenorImages(string url) => Assert.True(Chat.IsAllowedGif(url));

        [Theory]
        [InlineData("http://media.giphy.com/media/abc/giphy.gif")]
        [InlineData("https://evil.example.com/x.gif")]
        [InlineData("https://media.giphy.com.evil.com/x.gif")]
        [InlineData("https://media.giphy.com/a\"onerror=\"x")]
        [InlineData("")]
        [InlineData(null)]
        public void RejectsOtherImages(string url) => Assert.False(Chat.IsAllowedGif(url));

        [Fact]
        public void ConvertsGiphyPageLinkToImage() =>
            Assert.Equal("https://media.giphy.com/media/JIX9t2j0ZTN9S/giphy.gif", Chat.GifFromLink("https://giphy.com/gifs/funny-cat-JIX9t2j0ZTN9S"));

        [Fact]
        public void SanitizeTruncatesAndFlattensText()
        {
            var m = Chat.Sanitize("Omar", "ligne 1\nligne 2" + new string('a', 400), null);
            Assert.Equal(Chat.MaxText, m.Text.Length);
            Assert.DoesNotContain("\n", m.Text);
            Assert.Equal("Omar", m.From);
        }

        [Fact]
        public void SanitizeDropsForbiddenGif()
        {
            var m = Chat.Sanitize("Omar", "salut", "https://evil.example.com/x.gif");
            Assert.Null(m.Gif);
            Assert.Equal("salut", m.Text);
        }

        [Fact]
        public void SanitizeRejectsEmptyMessages()
        {
            Assert.Null(Chat.Sanitize("Omar", "   ", null));
            Assert.Null(Chat.Sanitize("Omar", 42, new object()));
        }

        [Fact]
        public void ShortcutsBecomeEmotes() => Assert.Equal("trop bien 🔥 😂", Chat.ApplyShortcuts("trop bien :feu: :mdr:"));
    }
}
