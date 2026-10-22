using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace StremioWT.Tests
{
    // Relay réel sur un port libre de la machine, interrogé en HTTP comme le ferait l'appli.
    public class RelayTests : IDisposable
    {
        readonly Relay relay = new Relay { HostId = "host" };
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        readonly string room;

        public RelayTests()
        {
            int port = relay.Start(0, lan: false);
            room = $"http://127.0.0.1:{port}/r/TEST{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant()}";
        }

        public void Dispose() => http.Dispose();

        Task<HttpResponseMessage> Poll(string id, string name, string version = null) =>
            http.GetAsync($"{room}/poll?id={id}&name={name}&v={version ?? Program.ProtocolVersion}");

        async Task<string> PollText(string id, string name) => await (await Poll(id, name)).Content.ReadAsStringAsync();

        Task<HttpResponseMessage> Send(string id, string json) =>
            http.PostAsync($"{room}/send?id={id}", new StringContent(json, Encoding.UTF8, "application/json"));

        static int Count(string body, string type) => Regex.Matches(body, $"\"type\":\"{type}\"").Count;

        async Task JoinBoth()
        {
            await PollText("host", "Hote");
            await PollText("guest", "Invite");
            await PollText("host", "Hote"); // vide la présence de l'invité
        }

        [Fact]
        public async Task RefusesOtherVersions()
        {
            var res = await Poll("guest", "Invite", "0.1");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal(Program.ProtocolVersion, await res.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task SendRequiresJoiningFirst()
        {
            var res = await Send("stranger", "{\"type\":\"play\"}");
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        [Fact]
        public async Task DeliversMessagesToOthersWithSenderName()
        {
            await JoinBoth();
            await Send("guest", "{\"type\":\"pause\",\"time\":1000}");
            var body = await PollText("host", "Hote");
            Assert.Equal(1, Count(body, "pause"));
            Assert.Contains("\"from\":\"Invite\"", body);
        }

        [Fact]
        public async Task LimitsChatToFiveMessagesPerTenSeconds()
        {
            await JoinBoth();
            foreach (var i in Enumerable.Range(1, 7)) await Send("guest", "{\"type\":\"chat\",\"text\":\"msg " + i + "\"}");
            Assert.Equal(5, Count(await PollText("host", "Hote"), "chat"));
        }

        [Fact]
        public async Task AdminModeIgnoresGuestControls()
        {
            await JoinBoth();
            relay.AdminOn = true;
            await Send("guest", "{\"type\":\"pause\"}");
            await Send("guest", "{\"type\":\"chat\",\"text\":\"le chat reste permis\"}");
            var body = await PollText("host", "Hote");
            Assert.Equal(0, Count(body, "pause"));
            Assert.Equal(1, Count(body, "chat"));
        }

        [Fact]
        public async Task OnlyHostCanSendHostCommands()
        {
            await JoinBoth();
            await Send("guest", "{\"type\":\"admin\",\"on\":true}");
            await Send("guest", "{\"type\":\"chat\",\"text\":\"marqueur\"}");
            var body = await PollText("host", "Hote");
            Assert.Equal(0, Count(body, "admin"));
        }
    }
}
