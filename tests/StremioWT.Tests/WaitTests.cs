using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace StremioWT.Tests
{
    // Hôte réel (Session) sur un relay réel : ce que les autres reçoivent quand quelqu'un charge.
    public class WaitTests : IDisposable
    {
        readonly Relay relay = new Relay();
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        readonly string server, room = "TEST" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        readonly Session host;
        readonly string hostId;

        public WaitTests()
        {
            server = $"http://127.0.0.1:{relay.Start(0, lan: false)}";
            host = new Session(server, room, "Hote", isHost: true, stremioExe: null, relay);
            hostId = (string)typeof(Session).GetField("clientId", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);
        }

        public void Dispose() => http.Dispose();

        async Task<string> Poll(string id, string name) =>
            await (await http.GetAsync($"{server}/r/{room}/poll?id={id}&name={name}&v={Program.ProtocolVersion}")).Content.ReadAsStringAsync();

        async Task Join()
        {
            await Poll(hostId, "Hote");
            await Poll("guest", "Invite");
            await Poll(hostId, "Hote");
            await Poll("guest", "Invite"); // vide les présences
        }

        void Buffer(string who, bool on, Dictionary<string, object> msg = null) =>
            typeof(Session).GetMethod("OnBuffer", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(host, new object[] { who, on, msg });

        // Attend un message de ce type chez l'invité (les autres sont ignorés).
        async Task<string> Next(string type)
        {
            for (int i = 0; i < 5; i++)
            {
                var body = await Poll("guest", "Invite");
                var m = Regex.Match(body, "\\{[^{}]*\"type\":\"" + type + "\"[^{}]*(\\{[^{}]*\\}[^{}]*)?\\}");
                if (m.Success) return m.Value;
            }
            throw new Exception("pas de message " + type);
        }

        [Fact]
        public async Task ResumesAtThePositionOfTheOneWeWaitedFor()
        {
            await Join();
            Buffer("Invite", true, new Dictionary<string, object> { ["progress"] = 0 });
            Assert.Contains("\"names\":[\"Invite\"]", await Next("wait"));
            Buffer("Invite", true, new Dictionary<string, object> { ["progress"] = 40 });
            Assert.Contains("\"Invite\":40", await Next("wait"));
            Buffer("Invite", false, new Dictionary<string, object> { ["time"] = 42000.0 });
            var go = await Next("go");
            Assert.Contains("\"time\":42000", go);
            Assert.Contains("\"exact\":true", go);
        }

        [Fact]
        public async Task OldVersionsWithoutPositionStillResume()
        {
            await Join();
            Buffer("Invite", true);
            await Next("wait");
            Buffer("Invite", false);
            Assert.DoesNotContain("\"time\"", await Next("go")); // pas de Stremio dans ce test : ni position de l'hôte, ni la sienne
        }
    }
}
