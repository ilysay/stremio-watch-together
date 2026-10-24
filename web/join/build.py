# Construit worker.js (Cloudflare Worker « join ») à partir de index.html : un seul fichier à coller dans Cloudflare.
#   python web/join/build.py
from pathlib import Path

here = Path(__file__).parent
html = (here / 'index.html').read_text(encoding='utf-8')
html = html.replace('\\', '\\\\').replace('`', '\\`').replace('${', '\\${')

worker = '''// Page d'invitation de Stremio Watch Together : https://join.stremiowt.workers.dev/#WT-…
// Généré par web/join/build.py à partir de index.html, ne pas modifier à la main.
// Le code d'invitation est après le « # » : le navigateur ne l'envoie jamais ici. On compte seulement les visites
// par jour (liaison D1 facultative « DB »), sans adresse IP ni autre donnée.

const PAGE = `''' + html + '''`;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.pathname !== '/' && url.pathname !== '/index.html') return Response.redirect(url.origin + '/', 302);
    if (env.DB) ctx.waitUntil(countVisit(env.DB).catch(() => {}));
    return new Response(PAGE, { headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'public, max-age=300' } });
  },
};

async function countVisit(db) {
  await db.prepare(`CREATE TABLE IF NOT EXISTS invite_opens (day TEXT PRIMARY KEY, n INTEGER NOT NULL DEFAULT 0)`).run();
  await db.prepare(`INSERT INTO invite_opens (day, n) VALUES (?1, 1) ON CONFLICT (day) DO UPDATE SET n = n + 1`)
    .bind(new Date().toISOString().slice(0, 10)).run();
}
'''
(here / 'worker.js').write_text(worker, encoding='utf-8', newline='\n')
print('worker.js', len(worker), 'octets')
