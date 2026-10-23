using System;
using Xunit;

namespace StremioWT.Tests
{
    public class UpdatesTests
    {
        [Theory]
        [InlineData("v3.5.12", "3.5.11.0", true)]
        [InlineData("v3.6.0", "3.5.11.0", true)]
        [InlineData("3.5.11", "3.5.11.0", false)]
        [InlineData("v3.5.10", "3.5.11.0", false)]
        [InlineData("not-a-version", "3.5.11.0", false)]
        [InlineData(null, "3.5.11.0", false)]
        public void ComparesReleaseTags(string tag, string current, bool newer) => Assert.Equal(newer, Updates.IsNewer(tag, Version.Parse(current)));

        [Fact]
        public void InviteMessageStillContainsAJoinableCode()
        {
            var code = Invite.Make("https://abc.trycloudflare.com", "7F3A2C");
            var message = Rooms.InviteMessage(code);
            Assert.Contains(Updates.DownloadUrl, message);
            Assert.Equal(("https://abc.trycloudflare.com", "7F3A2C"), Invite.Parse(message));
        }
    }
}
