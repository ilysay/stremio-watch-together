using System;
using System.Collections.Generic;
using System.Linq;
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

        static Dictionary<string, object> Release(params (string Name, string Digest)[] assets) => new Dictionary<string, object>
        {
            ["assets"] = new System.Collections.ArrayList(assets.Select(a => (object)new Dictionary<string, object> { ["name"] = a.Name, ["digest"] = a.Digest }).ToList()),
        };

        [Fact]
        public void UpdateOnlyWhenThisSystemsFileChanged()
        {
            var installed = Release(("StremioWT.exe", "sha256:aaa"), ("StremioWT-linux-x64", "sha256:bbb"));
            // Version qui ne change que Linux : l'exe Windows est republié à l'identique.
            var linuxOnly = Release(("StremioWT.exe", "sha256:aaa"), ("StremioWT-linux-x64", "sha256:ccc"));
            Assert.False(Updates.HasNewFile(linuxOnly, installed, "StremioWT.exe"));
            Assert.True(Updates.HasNewFile(linuxOnly, installed, "StremioWT-linux-x64"));
            // Pas de fichier pour ce système dans la nouvelle version : rien à proposer.
            Assert.False(Updates.HasNewFile(linuxOnly, installed, "StremioWT-mac.zip"));
            // Version installée introuvable sur GitHub (développement) : on se fie au numéro.
            Assert.True(Updates.HasNewFile(linuxOnly, null, "StremioWT.exe"));
        }

        [Fact]
        public void InviteMessageStillContainsAJoinableCode()
        {
            var code = Invite.Make("https://abc.trycloudflare.com", "7F3A2C");
            var message = Rooms.InviteMessage(code);
            Assert.Contains(Invite.Link(code), message);
            Assert.Equal(("https://abc.trycloudflare.com", "7F3A2C"), Invite.Parse(message));
        }

        [Fact]
        public void InviteLinkCarriesTheCodeAfterTheHash()
        {
            var code = Invite.Make("https://abc.trycloudflare.com", "7F3A2C");
            var link = Invite.Link(code);
            Assert.StartsWith(Invite.JoinPage + "#WT-", link);
            Assert.Equal(("https://abc.trycloudflare.com", "7F3A2C"), Invite.Parse(link));
            Assert.Equal(("https://abc.trycloudflare.com", "7F3A2C"), Invite.Parse("stremiowt://join/" + code));
        }
    }
}
