# Stats & GIF server (Cloudflare Worker)

[`worker.js`](worker.js) is the only server the app talks to, and it is optional:

- `POST /ping` receives the anonymous usage signals (see *Privacy & security* in the main README) and stores them in a D1 database. Every field is checked against a fixed list, so nothing free-form can be stored.
- `GET /stats?key=…` shows the dashboard, protected by a secret key.
- `GET /gif?q=…` proxies Giphy search for the chat, so the Giphy API key never ships inside the app.

Rooms, sync and chat messages never go through this server.

## Deploy your own (free plan, about 5 minutes)

Everything happens on [dash.cloudflare.com](https://dash.cloudflare.com), nothing to install.

1. **Database**: *Storage & Databases → D1 SQL Database → Create*, name it `stremiowt-stats`.
2. **Worker**: *Workers & Pages → Create → Start with Hello World*, name it `stremiowt-stats`, *Deploy*.
3. **Code**: on the Worker page, *Edit code*, replace everything with [`worker.js`](worker.js), *Deploy*.
4. **Bind the database**: *Settings → Bindings → Add → D1 database*, variable name `DB`, database `stremiowt-stats`.
5. **Dashboard key**: *Settings → Variables and Secrets → Add*, type **Secret**, name `STATS_KEY`, any long random value.
6. **GIF search** (optional): create a free API key on [developers.giphy.com](https://developers.giphy.com) (*Create an App → API*), then add it as a **Secret** named `GIPHY_KEY`.
7. Put your Worker address (`https://stremiowt-stats.<your-subdomain>.workers.dev`) in `src/Telemetry.cs` (`Url`) and `src/Chat/Chat.cs` (`GifServer`), then rebuild.

Your dashboard is then at `https://stremiowt-stats.<your-subdomain>.workers.dev/stats?key=<STATS_KEY>`.
