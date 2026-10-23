// Évalue une expression dans la page Stremio via le port de débogage : node cdp-eval.js <port> <fichier.js | expression>
const fs = require('fs');
const [, , port, arg] = process.argv;
const expr = fs.existsSync(arg) ? fs.readFileSync(arg, 'utf8') : arg;
(async () => {
  const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
  const page = pages.find(p => p.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise(r => ws.onopen = r);
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id !== 1) return;
    const r = m.result && m.result.result;
    console.log(m.result && m.result.exceptionDetails ? 'ERREUR: ' + JSON.stringify(m.result.exceptionDetails.exception || m.result.exceptionDetails).slice(0, 800) : (typeof r.value === 'string' ? r.value : JSON.stringify(r.value, null, 1)));
    ws.close(); process.exit(0);
  };
  ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true, awaitPromise: true } }));
  setTimeout(() => { console.log('timeout'); process.exit(1); }, 15000);
})();
