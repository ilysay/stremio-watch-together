// Compteur anonyme de Stremio Watch Together (Cloudflare Worker + base D1).
//
//   POST /ping   { id, v, event, size?, lang?, detail? }   ← envoyé par StremioWT
//   GET  /stats?key=<STATS_KEY>                          → page de statistiques (protégée par ta clé)
//   GET  /gif?q=…                                         → recherche de GIF pour le chat (via Giphy)
//   POST /report { v, message, report }                   ← rapport de bug, envoyé seulement si l'utilisateur clique « Envoyer »
//
// Ce qui est enregistré : un identifiant d'installation ALÉATOIRE (sans lien avec la personne),
// la version, la langue de l'appli, le type d'événement (launch / host / join / error / session / optout),
// la taille du salon, le type de problème rencontré, la durée arrondie d'une séance,
// et le pays (déduit par Cloudflare, l'adresse IP elle-même n'est jamais stockée).
// Ce qui n'est JAMAIS enregistré : adresse IP, pseudo, titres, liens, messages du chat, codes de salon.
// Exception volontaire : un rapport de bug (message + journal de l'appli, que l'utilisateur voit avant d'envoyer),
// gardé 30 jours. Limites : 3 par minute et par expéditeur (liaison facultative « REPORT_LIMIT », rien n'est stocké), 30 par heure au total.
//
// Liaisons à configurer dans Cloudflare : base D1 nommée « DB », secrets « STATS_KEY » et « GIPHY_KEY ».

const EVENTS = ['launch', 'host', 'join', 'error', 'session', 'optout'];
// Valeurs acceptées pour chaque détail : tout le reste est refusé (rien de libre ne peut être envoyé).
const ERRORS = ['stremio-missing', 'stremio-noreply', 'tunnel', 'room-unreachable', 'version', 'room-full', 'crash'];
const DURATIONS = ['0-5m', '5-30m', '30-60m', '1-2h', '2-3h', '3h+'];
const LANGS = ['fr', 'en', 'zh', 'hi', 'es', 'ar', 'bn', 'pt', 'ru', 'id', 'de', 'it', 'tr', 'pl', 'cs'];

const ERROR_NAMES = {
  'stremio-missing': 'Stremio introuvable sur le PC',
  'stremio-noreply': 'Stremio ne répond pas au lancement',
  'tunnel': "Accès Internet impossible (tunnel de l'hôte)",
  'room-unreachable': 'Salon injoignable (invité)',
  'version': "Versions différentes entre l'hôte et l'invité",
  'room-full': 'Salon plein',
  'crash': 'Erreur inattendue',
};

let tablesReady = false;
async function ensureTables(db) {
  if (tablesReady) return;
  await db.batch([
    db.prepare(`CREATE TABLE IF NOT EXISTS pings (
       day TEXT NOT NULL, id TEXT NOT NULL, event TEXT NOT NULL, v TEXT NOT NULL, size INTEGER NOT NULL DEFAULT 0,
       PRIMARY KEY (day, id, event, v))`),
    db.prepare(`CREATE TABLE IF NOT EXISTS details (
       day TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL, v TEXT NOT NULL, n INTEGER NOT NULL DEFAULT 1,
       PRIMARY KEY (day, id, kind, detail, v))`),
    db.prepare(`CREATE TABLE IF NOT EXISTS reports (
       id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, v TEXT NOT NULL, message TEXT NOT NULL, report TEXT NOT NULL,
       sent INTEGER NOT NULL DEFAULT 0)`),
  ]);
  tablesReady = true;
}

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    try {
      if (url.pathname === '/gif') return await gifs(url, env, ctx);
      await ensureTables(env.DB);
      if (url.pathname === '/ping' && request.method === 'POST') return await ping(request, env);
      if (url.pathname === '/report' && request.method === 'POST') return await report(request, env);
      if (url.pathname === '/stats' && request.method === 'GET') return await stats(url, env);
      return new Response('Stremio Watch Together : compteur anonyme\n', { headers: { 'content-type': 'text/plain; charset=utf-8' } });
    } catch (e) {
      return new Response('erreur : ' + e.message, { status: 500 });
    }
  },
};

async function gifs(url, env, ctx) {
  const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'public, max-age=600' } });
  if (!env.GIPHY_KEY) return json({ error: 'no-key' }, 503);
  const q = (url.searchParams.get('q') || '').trim().slice(0, 80);
  const lang = (url.searchParams.get('lang') || 'fr').slice(0, 2);
  const cacheKey = new Request('https://cache.stremiowt/gif?q=' + encodeURIComponent(q.toLowerCase()) + '&lang=' + lang);
  const cache = caches.default;
  const hit = await cache.match(cacheKey);
  if (hit) return hit;
  const api = q
    ? `https://api.giphy.com/v1/gifs/search?api_key=${env.GIPHY_KEY}&q=${encodeURIComponent(q)}&limit=24&rating=pg-13&lang=${encodeURIComponent(lang)}`
    : `https://api.giphy.com/v1/gifs/trending?api_key=${env.GIPHY_KEY}&limit=24&rating=pg-13`;
  const r = await fetch(api);
  if (!r.ok) return json({ error: 'giphy ' + r.status }, 502);
  const data = await r.json();
  const results = (data.data || []).map(g => ({
    gif: g.images?.fixed_height?.url || g.images?.original?.url,
    preview: g.images?.fixed_height_small?.url || g.images?.fixed_height?.url,
  })).filter(g => g.gif).map(g => ({ gif: g.gif.split('?')[0], preview: (g.preview || g.gif).split('?')[0] }));
  const res = json({ results });
  ctx.waitUntil(cache.put(cacheKey, res.clone()));
  return res;
}

async function ping(request, env) {
  const text = await request.text();
  if (text.length > 512) return new Response('trop long', { status: 400 });
  let m;
  try { m = JSON.parse(text); } catch { return new Response('json invalide', { status: 400 }); }
  const id = String(m.id || ''), v = String(m.v || ''), event = String(m.event || ''), detail = String(m.detail || '');
  if (!/^[0-9a-f]{32}$/.test(id) || !/^[0-9.]{1,12}$/.test(v) || !EVENTS.includes(event)) return new Response('invalide', { status: 400 });
  const day = new Date().toISOString().slice(0, 10);
  const addDetail = (kind, value) => env.DB.prepare(
    `INSERT INTO details (day, id, kind, detail, v, n) VALUES (?1, ?2, ?3, ?4, ?5, 1)
     ON CONFLICT (day, id, kind, detail, v) DO UPDATE SET n = n + 1`).bind(day, id, kind, value, v);

  if (event === 'error' || event === 'session') {
    const allowed = event === 'error' ? ERRORS : DURATIONS;
    if (!allowed.includes(detail)) return new Response('invalide', { status: 400 });
    await addDetail(event, detail).run();
    return new Response(null, { status: 204 });
  }

  const size = Math.max(0, Math.min(50, parseInt(m.size, 10) || 0));
  const writes = [env.DB.prepare(
    `INSERT INTO pings (day, id, event, v, size) VALUES (?1, ?2, ?3, ?4, ?5)
     ON CONFLICT (day, id, event, v) DO UPDATE SET size = MAX(size, excluded.size)`)
    .bind(day, id, event, v, size)];
  if (event === 'launch') {
    // Pays : fourni par Cloudflare (2 lettres). L'adresse IP n'est ni lue ni stockée.
    const country = String((request.cf && request.cf.country) || '').toUpperCase();
    if (/^[A-Z]{2}$/.test(country)) writes.push(addDetail('country', country));
    const lang = String(m.lang || '');
    if (LANGS.includes(lang)) writes.push(addDetail('lang', lang));
  }
  await env.DB.batch(writes);
  return new Response(null, { status: 204 });
}

async function report(request, env) {
  if (env.REPORT_LIMIT) {
    // Compté en mémoire par Cloudflare, l'adresse n'est pas enregistrée.
    const { success } = await env.REPORT_LIMIT.limit({ key: request.headers.get('cf-connecting-ip') || '?' });
    if (!success) return new Response('trop de rapports', { status: 429 });
  }
  const text = await request.text();
  if (text.length > 80000) return new Response('trop long', { status: 413 });
  let m;
  try { m = JSON.parse(text); } catch { return new Response('json invalide', { status: 400 }); }
  const v = String(m.v || ''), message = String(m.message || '').slice(0, 1000), body = String(m.report || '').slice(0, 70000);
  if (!/^[0-9.]{1,12}$/.test(v) || !body) return new Response('invalide', { status: 400 });
  const now = new Date();
  const recent = await env.DB.prepare(`SELECT COUNT(*) AS n FROM reports WHERE at >= ?1`).bind(new Date(now - 3600000).toISOString()).first();
  if (recent.n >= 30) return new Response('trop de rapports', { status: 429 });
  await env.DB.batch([
    env.DB.prepare(`INSERT INTO reports (at, v, message, report) VALUES (?1, ?2, ?3, ?4)`).bind(now.toISOString(), v, message, body),
    env.DB.prepare(`DELETE FROM reports WHERE at < ?1`).bind(new Date(now - 30 * 86400000).toISOString()),
  ]);
  return new Response(null, { status: 204 });
}

async function stats(url, env) {
  if (!env.STATS_KEY || url.searchParams.get('key') !== env.STATS_KEY) return new Response('accès refusé', { status: 403 });
  const q = (sql, ...args) => env.DB.prepare(sql).bind(...args).all().then(r => r.results);
  const since = d => new Date(Date.now() - d * 86400000).toISOString().slice(0, 10);
  const [total] = await q(`SELECT COUNT(DISTINCT id) AS n FROM pings`);
  const [week] = await q(`SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1`, since(6));
  const [month] = await q(`SELECT COUNT(DISTINCT id) AS n FROM pings WHERE day >= ?1`, since(29));
  const days = await q(
    `SELECT day, COUNT(DISTINCT id) AS users,
            SUM(event = 'host') AS hosts, SUM(event = 'join') AS joins,
            ROUND(AVG(CASE WHEN event = 'host' AND size > 0 THEN size END), 1) AS avg_size
     FROM pings WHERE day >= ?1 GROUP BY day ORDER BY day DESC`, since(29));
  const versions = await q(
    `SELECT v, COUNT(DISTINCT id) AS users FROM pings WHERE day >= ?1 GROUP BY v ORDER BY users DESC`, since(6));
  const detail = (kind, d) => q(
    `SELECT detail, SUM(n) AS times, COUNT(DISTINCT id) AS users FROM details WHERE kind = ?1 AND day >= ?2
     GROUP BY detail ORDER BY users DESC, times DESC`, kind, since(d));
  const errors = await detail('error', 6);
  const sessions = await detail('session', 29);
  const countries = await detail('country', 29);
  const langs = await detail('lang', 29);
  sessions.sort((a, b) => DURATIONS.indexOf(a.detail) - DURATIONS.indexOf(b.detail));

  const esc = s => String(s ?? '–').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const row = cells => '<tr>' + cells.map(c => `<td>${esc(c)}</td>`).join('') + '</tr>';
  const table = (head, rows, empty) => rows.length
    ? `<table><tr>${head.map(h => `<th>${h}</th>`).join('')}</tr>${rows.map(row).join('')}</table>`
    : `<p class="empty">${empty}</p>`;
  const countryName = new Intl.DisplayNames(['fr'], { type: 'region' });
  const langName = new Intl.DisplayNames(['fr'], { type: 'language' });
  const name = (f, c) => { try { return f.of(c); } catch { return c; } };

  const html = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>StremioWT · statistiques</title>
<style>
  body{margin:0;padding:24px;font:15px/1.5 system-ui,sans-serif;background:#06132d;color:#e8f0ff}
  h1{font-size:22px;margin:0 0 16px} h2{font-size:16px;margin:28px 0 8px;color:#8ca0c8}
  .cards{display:flex;gap:12px;flex-wrap:wrap} .card{background:#0c2048;border:1px solid #1e3e82;border-radius:12px;padding:14px 18px;min-width:150px}
  .card b{display:block;font-size:28px;color:#2f80ff} table{border-collapse:collapse;width:100%;max-width:720px}
  td,th{padding:6px 10px;border-bottom:1px solid #1e3e82;text-align:left} th{color:#8ca0c8;font-weight:600}
  .empty{color:#8ca0c8;margin:4px 0} .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:0 32px;max-width:1100px}
</style>
<h1>Stremio Watch Together · statistiques</h1>
<div class="cards">
  <div class="card"><b>${week.n}</b>utilisateurs sur 7 jours</div>
  <div class="card"><b>${month.n}</b>utilisateurs sur 30 jours</div>
  <div class="card"><b>${total.n}</b>installations depuis le début</div>
</div>
<h2>Problèmes rencontrés (7 derniers jours)</h2>
${table(['Problème', 'Fois', 'Personnes touchées'], errors.map(e => [ERROR_NAMES[e.detail] || e.detail, e.times, e.users]), 'Aucun problème signalé. 🎉')}
<div class="grid">
<div><h2>Durée des séances (30 jours)</h2>
${table(['Durée', 'Séances'], sessions.map(s => [s.detail.replace('m', ' min').replace('h+', ' h et plus').replace(/h$/, ' h'), s.times]), 'Pas encore de séance terminée.')}</div>
<div><h2>Pays (30 jours)</h2>
${table(['Pays', 'Utilisateurs'], countries.map(c => [name(countryName, c.detail), c.users]), 'Pas encore de données.')}</div>
<div><h2>Langue de l'appli (30 jours)</h2>
${table(['Langue', 'Utilisateurs'], langs.map(l => [name(langName, l.detail), l.users]), 'Pas encore de données.')}</div>
<div><h2>Versions (7 jours)</h2>
${table(['Version', 'Utilisateurs'], versions.map(v => [v.v, v.users]), 'Pas encore de données.')}</div>
</div>
<h2>Par jour (30 derniers jours)</h2>
${table(['Jour', 'Utilisateurs', 'Salons créés', 'Salons rejoints', 'Taille moyenne'], days.map(d => [d.day, d.users, d.hosts, d.joins, d.avg_size]), 'Pas encore de données.')}`;
  return new Response(html, { headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' } });
}
