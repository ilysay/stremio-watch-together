// Script injecté dans l'interface de Stremio : Stremio 4 (app.strem.io, Qt) ou Stremio 5 (web.stremio.com, WebView2).
// Il observe les commandes que l'interface envoie à mpv (qt.webChannelTransport sous Stremio 4,
// chrome.webview.postMessage sous Stremio 5) pour détecter pause / lecture / déplacement, et applique ceux reçus des autres.
(() => {
  const VERSION = '__WT_VERSION__';
  if (window.__wt) return;
  const W = (window.__wt = { version: VERSION });
  const I18N = __WT_I18N__;
  const T = (s, ...a) => (I18N[s] || s).replace(/\{(\d)\}/g, (_, i) => a[i]);
  const LOAD_QUIET_MS = 1500;
  const SEEK_EPS_FORCED = 1000;
  const SEEK_EPS_SYNC = 2500;

  let applying = false;
  let loading = false;
  let readyAt = 0;
  let pending = null;
  let remoteOpenKey = null;
  let lastOpen = { key: null, at: 0 };
  let wasFollowing = false;
  const REOPEN_GUARD_MS = 10000;
  let adminLock = false;
  let lastLockToast = 0;
  const BUFFER_REPORT_MS = 800;
  let bufSince = 0, bufReported = false;
  let waiting = false;
  const WATCHED_RATIO = 0.85; // film considéré comme vu à 85 % (le générique de fin n'est pas compté)
  let watchedKey = null;
  // Dernier épisode chargé dans ce lecteur : sous Stremio Web, un nouvel essai de lecture du même épisode
  // (par exemple après une erreur de décodage) ne doit pas être renvoyé aux autres comme une nouvelle ouverture.
  let loadedKey = null;

  const V5 = /(^|\.)stremio\.com$/.test(location.hostname);
  // Stremio Web dans un navigateur (Mac) : même interface que Stremio 5, mais la vidéo est un <video> HTML.
  const WEB = V5 && !(window.chrome && window.chrome.webview);
  W.shell = WEB ? 'web' : V5 ? 5 : 4;
  // Stremio Web sous Linux : Chromium affirme lire le HEVC (h265) mais n'y arrive souvent pas (erreur de décodage).
  // On répond « non » pour le HEVC : le serveur de Stremio convertit alors la vidéo en H.264, lisible partout.
  if (WEB && /Linux/.test(navigator.userAgent) && !/Android/.test(navigator.userAgent) && !window.__wtNoHevc) {
    window.__wtNoHevc = true;
    const hevc = t => /hev1|hvc1|hevc|h265/i.test(t || '');
    const isType = MediaSource.isTypeSupported.bind(MediaSource);
    MediaSource.isTypeSupported = t => hevc(t) ? false : isType(t);
    const canPlay = HTMLMediaElement.prototype.canPlayType;
    HTMLMediaElement.prototype.canPlayType = function (t) { return hevc(t) ? '' : canPlay.call(this, t); };
    const mc = navigator.mediaCapabilities;
    if (mc && mc.decodingInfo) {
      const info = mc.decodingInfo.bind(mc);
      mc.decodingInfo = c => c && c.video && hevc(c.video.contentType) ? Promise.resolve({ supported: false, smooth: false, powerEfficient: false }) : info(c);
    }
  }
  // Script arrivé après le chargement de la page : sous Stremio 5 l'interface a déjà gardé sa propre
  // référence à postMessage, nos espions ne verraient rien. Le programme recharge alors la page.
  W.needsReload = V5 && !WEB && document.readyState !== 'loading';
  const now = () => Date.now();
  const toNode = m => { try { window.__wtSend(JSON.stringify(m)); } catch (e) {} };
  const debug = text => toNode({ type: 'debug', text: String(text).slice(0, 500) });
  // "#/player/series/tt0121955/tt0121955:18:1/<source>" → "#/player/series/tt0121955/tt0121955:18:1" :
  // on compare l'épisode, pas la source (le lien complet peut différer d'un PC à l'autre).
  // Stremio 5 : "#/player/<flux>/<addon flux>/<addon fiche>/<type>/<id>/<épisode>", ramené à la forme de Stremio 4
  // pour que les deux versions (et les anciennes versions de StremioWT) parlent du même épisode.
  const TYPES = ['movie', 'series', 'channel', 'tv', 'other'];
  const dec = s => { try { return decodeURIComponent(s || ''); } catch (e) { return s || ''; } };
  const videoKey = h => {
    const p = (h || '').split('/');
    if (p[1] !== 'player' || TYPES.includes(p[2]) || p.length < 3) return p.slice(0, 5).join('/');
    return p.length >= 8 && TYPES.includes(dec(p[5])) ? ['#', 'player', dec(p[5]), dec(p[6]), dec(p[7])].join('/') : '#/player/other/' + (p[2] || '').slice(0, 24);
  };
  const sameVideo = (a, b) => videoKey(a) === videoKey(b);
  const device = () => WEB ? html5.device() : V5 ? mpv.device() : (() => { try { return angular.element(document.body).injector().get('player').device || null; } catch (e) { return null; } })();
  const inPlayer = () => location.hash.indexOf('#/player/') === 0;
  const isReady = d => !!d && d.length > 0;
  const fmt = ms => { const s = Math.max(0, Math.round(ms / 1000)), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = s % 60;
    return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); };

  // Source « privée » : un lien direct (Real-Debrid, autre debrid…) au lieu d'un torrent lu par le serveur
  // local de Stremio (127.0.0.1:11470). Son lien (lié au compte, souvent avec la clé) n'est jamais partagé :
  // les autres ne reçoivent que l'épisode et choisissent leur propre source.
  let privateSource = false;
  const isLocalUrl = u => /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//i.test(u || '');
  const sharedHash = () => V5 ? (privateSource && videoKey(location.hash).split('/')[2] === 'other' ? '#/player/other/prive' : videoKey(location.hash))
    : !privateSource ? location.hash
    : location.hash.split('/')[2] === 'other' ? '#/player/other/prive' : videoKey(location.hash);
  // Sous Stremio 5, le lien complet (avec la source) voyage à part : seul un autre Stremio 5 sait l'ouvrir.
  const ids = () => V5 && !privateSource && inPlayer() ? { hash: sharedHash(), v5: location.hash } : { hash: sharedHash() };
  const hasSource = h => !!((h || '').split('/')[5]);
  const sourceFor = msg => V5 ? (msg.v5 || null) : (hasSource(msg.hash) ? msg.hash : null);
  let choosingSource = false;
  function showChooseSource(who) {
    choosingSource = true;
    showBanner(T('{0} utilise une source privée (Real-Debrid…) : choisis ta propre source pour cet épisode', who || T("L'hôte")));
  }

  // Torrent lu, pour l'alerte de téléchargement trop lent (l'appli lit sa vitesse sur le serveur local de Stremio).
  // Dans le lien du lecteur (Stremio 4 et 5) ; sinon dans l'adresse de la page, où Stremio 5 / Web range la
  // source compressée (zlib + base64) : Stremio Web lit une vidéo convertie, sans le torrent dans son lien.
  async function reportTorrent(src) {
    let hash = null, idx = 0;
    const m = /\/([0-9a-f]{40})\/(\d+)/i.exec(src || '');
    if (m) { hash = m[1].toLowerCase(); idx = +m[2]; }
    else if (V5) try {
      let b64 = dec(location.hash.split('/')[2] || '').replace(/-/g, '+').replace(/_/g, '/');
      b64 += '='.repeat((4 - b64.length % 4) % 4);
      const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
      const stream = JSON.parse(await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate'))).text());
      if (stream && /^[0-9a-f]{40}$/i.test(stream.infoHash || '')) { hash = stream.infoHash.toLowerCase(); idx = stream.fileIdx || 0; }
    } catch (e) {}
    toNode({ type: 'torrent', hash, idx });
  }
  W.duration = () => { const d = device(); return (d && d.length) || 0; };

  W.state = () => {
    const d = device();
    if (!inPlayer() || !isReady(d) || loading) return null;
    return Object.assign(ids(), { paused: !!d.paused, time: d.time || 0, length: d.length || 0 });
  };

  function onOut(m) {
    if (!m || m.type !== 6 || !Array.isArray(m.args)) return;
    const ev = m.args[0], args = m.args[1] || [];
    if (ev === 'mpv-command' && args[0] === 'loadfile') {
      const h = location.hash;
      // Stremio Web : en quittant le lecteur, la vidéo vidée déclenche aussi un « chargement ». Ce n'est pas une
      // ouverture : la traiter comme telle effaçait l'ouverture demandée par un autre (on restait à l'accueil).
      if (!inPlayer()) return;
      // Stremio Web relance parfois le chargement du même épisode (autre format, nouvel essai) : rien de nouveau
      // à partager, et un lien vide ne veut pas dire « source privée ».
      if (WEB && loadedKey === videoKey(h) && inPlayer()) { debug('nouvel essai de lecture de ' + videoKey(h) + ' (non partagé)'); loading = true; readyAt = 0; return; }
      loading = true; readyAt = 0;
      loadedKey = videoKey(h);
      privateSource = !!args[1] && !isLocalUrl(args[1]) && !(WEB && /^blob:/.test(args[1] || ''));
      reportTorrent(args[1]);
      if (V5) mpv.reset();
      if (choosingSource) { choosingSource = false; hideBanner(); }
      wasFollowing = remoteOpenKey !== null && videoKey(h) === remoteOpenKey;
      debug('chargement de ' + videoKey(h) + (wasFollowing ? ' (lancé par un autre)' : '') + (privateSource ? ' · source privée, lien non partagé' : ''));
      remoteOpenKey = null;
      watchedKey = null;
      if (!applying && !wasFollowing) { if (adminLock) blocked(); else toNode(Object.assign({ type: 'open', private: privateSource }, ids())); }
      return;
    }
    if (ev !== 'mpv-set-prop' || applying || loading || !inPlayer()) return;
    if (args[0] !== 'pause' && args[0] !== 'time-pos') return;
    if (adminLock) return blocked();
    const d = device();
    if (args[0] === 'pause') toNode(Object.assign({ type: args[1] ? 'pause' : 'play', time: (d && d.time) || 0 }, ids()));
    else toNode(Object.assign({ type: 'seek', time: args[1] * 1000, paused: !!(d && d.paused) }, ids()));
  }

  let resyncTimer = null;
  function blocked() {
    if (now() - lastLockToast > 3000) { lastLockToast = now(); toast(T("Mode hébergeur admin : seul l'hôte contrôle la lecture")); }
    debug('action locale bloquée (mode admin)');
    clearTimeout(resyncTimer);
    resyncTimer = setTimeout(() => toNode({ type: 'need-sync' }), 400);
  }

  // Lecteur de Stremio 5 : son état arrive en messages « mpv-prop-change » ; on le pilote avec les mêmes
  // commandes que l'interface, envoyées en texte JSON (le seul format accepté par le shell).
  const mpv = (() => {
    let props = {}, ready = false, post = null;
    const send = (ev, args) => { if (post) post(JSON.stringify({ id: 0, type: 6, args: [ev, args] })); };
    const onIn = data => {
      let m = data; try { if (typeof m === 'string') m = JSON.parse(m); } catch (e) { return; }
      const a = m && m.args; if (!Array.isArray(a)) return;
      if (a[0] === 'mpv-prop-change' && a[1]) props[a[1].name] = a[1].data;
      else if (a[0] === 'mpv-event-video-ready' && a[1]) ready = !!a[1].ready;
    };
    const dev = {
      get time() { return (props['time-pos'] || 0) * 1000; },
      set time(ms) { props['time-pos'] = ms / 1000; send('mpv-set-prop', ['time-pos', ms / 1000]); },
      get paused() { return !!props.pause; },
      set paused(v) { props.pause = !!v; send('mpv-set-prop', ['pause', !!v]); },
      get length() { return (props.duration || 0) * 1000; },
      get state() { return !ready ? 1 : props['paused-for-cache'] || props.seeking ? 2 : props.pause ? 4 : 3; },
    };
    return {
      device: () => dev,
      reset: () => { props = {}; ready = false; },
      install() {
        const wv = window.chrome && window.chrome.webview;
        if (!wv || wv.__wtHooked) return;
        post = wv.postMessage.bind(wv);
        wv.postMessage = function (m) { try { onOut(typeof m === 'string' ? JSON.parse(m) : m); } catch (e) {} return post(m); };
        wv.addEventListener('message', e => onIn(e.data));
        wv.__wtHooked = true;
      },
    };
  })();
  if (V5 && !WEB) mpv.install();

  // Stremio Web : on observe le <video> de la page. Ses évènements sont traduits dans le format des commandes mpv
  // (chargement, pause, déplacement) pour réutiliser la même logique. Nos propres changements déclenchent aussi
  // ces évènements, un peu plus tard : ceux qui suivent de peu une action appliquée par nous sont ignorés.
  const html5 = (() => {
    let video = null, quietUntil = 0;
    // Ce que la synchro vient de demander au lecteur : les événements qui y correspondent sont les nôtres et ne
    // sont pas renvoyés aux autres. Un simple délai ne suffit pas : sous Stremio Web, un saut dans une vidéo
    // convertie arrive souvent plusieurs secondes après (on renvoyait alors notre propre saut, en boucle).
    let wantTime = null, wantPaused = null;
    const OURS_MS = 10000;
    const quiet = () => { quietUntil = now() + 900; };
    const out = (ev, args) => { if (now() >= quietUntil) onOut({ type: 6, args: [ev, args] }); };
    const oursSeek = () => wantTime && now() - wantTime.at < OURS_MS && Math.abs(video.currentTime - wantTime.v) < 1.5;
    const oursPause = () => wantPaused && now() - wantPaused.at < OURS_MS && video.paused === wantPaused.v;
    const dev = {
      get time() { return video ? video.currentTime * 1000 : 0; },
      set time(ms) { if (video) { quiet(); wantTime = { v: ms / 1000, at: now() }; video.currentTime = ms / 1000; } },
      get paused() { return !video || video.paused; },
      set paused(p) { if (!video) return; quiet(); wantPaused = { v: !!p, at: now() }; if (p) video.pause(); else video.play().catch(() => {}); },
      get length() { return video && isFinite(video.duration) ? video.duration * 1000 : 0; },
      get state() { return !video || video.readyState < 1 ? 1 : video.seeking || video.readyState < 3 ? 2 : video.paused ? 4 : 3; },
    };
    function attach(v) {
      video = v;
      v.addEventListener('loadstart', () => onOut({ type: 6, args: ['mpv-command', ['loadfile', v.currentSrc || v.src || '']] }));
      // Erreurs de lecture du navigateur (format non pris en charge, décodage…) : visibles dans le journal de l'appli.
      v.addEventListener('error', () => {
        const e = v.error, src = v.currentSrc || v.src || '';
        debug('erreur vidéo du navigateur : ' + (e ? 'code ' + e.code + (e.message ? ' · ' + e.message : '') : 'inconnue') + ' · ' + (isLocalUrl(src) ? 'serveur local ' + src.split('?')[0].split('/').slice(3, 5).join('/') : /^blob:/.test(src) ? 'flux transcodé' : src ? 'lien externe' : 'aucun lien'));
      });
      v.addEventListener('play', () => { if (!oursPause()) out('mpv-set-prop', ['pause', false]); });
      v.addEventListener('pause', () => { if (!v.ended && !oursPause()) out('mpv-set-prop', ['pause', true]); });
      v.addEventListener('seeked', () => { if (!oursSeek()) out('mpv-set-prop', ['time-pos', v.currentTime]); });
      // La vidéo a pu commencer à charger avant qu'on la voie : on signale ce chargement.
      if (v.readyState > 0 || v.currentSrc) onOut({ type: 6, args: ['mpv-command', ['loadfile', v.currentSrc || v.src || '']] });
    }
    return {
      device: () => dev,
      poll() {
        const v = inPlayer() ? document.querySelector('video') : null;
        if (v !== video) { if (v) attach(v); else video = null; }
      },
    };
  })();

  function hook() {
    if (V5) return;
    const t = window.qt && window.qt.webChannelTransport;
    if (!t || t.__wtHooked || typeof t.send !== 'function') return;
    const origSend = t.send;
    t.send = function (s) {
      try { onOut(JSON.parse(s)); } catch (e) {}
      return origSend.apply(this, arguments);
    };
    t.__wtHooked = true;
  }

  function applyState(msg) {
    const d = device();
    const paused = msg.type === 'pause' ? true : msg.type === 'play' ? false : !!msg.paused;
    let target = msg.time;
    if (!paused && msg.receivedAt) target += now() - msg.receivedAt;
    const eps = msg.type === 'sync' && !msg.force ? SEEK_EPS_SYNC : SEEK_EPS_FORCED;
    applying = true;
    try {
      if (typeof target === 'number' && Math.abs((d.time || 0) - target) > eps) d.time = Math.max(0, Math.min(target, d.length - 1000));
      if (!!d.paused !== paused) d.paused = paused;
    } finally { applying = false; }
  }

  // Ouvre l'épisode lancé par un autre. Retourne false si on le regarde déjà ou s'il vient
  // d'être ouvert (évite les rechargements en boucle du lecteur → écran blanc), 'detail' si c'est
  // une source privée (pas de lien : on ouvre la page de l'épisode pour choisir sa source), 'player' sinon.
  function openHash(msg, force) {
    const hash = msg.hash, key = videoKey(hash);
    let target = sourceFor(msg), kind = 'player';
    // Même épisode : on ne rouvre que si l'autre a explicitement changé de source (« open » avec un autre lien).
    const otherSource = msg.type === 'open' && !!target && inPlayer() && dec(target) !== dec(location.hash);
    if (sameVideo(location.hash, hash) && !otherSource) return false;
    if (!target) {
      const parts = key.split('/');
      if (parts[2] === 'other' || !parts[3]) return false;
      target = '#/detail/' + (V5 ? parts.slice(2, 5).map(encodeURIComponent) : parts.slice(2, 5)).join('/');
      kind = 'detail';
      if (location.hash === target) return false;
    }
    // Garde anti-boucle par lien (épisode + source) : changer de source juste après avoir ouvert reste possible.
    const guard = target || key;
    if (!force && lastOpen.key === guard && now() - lastOpen.at < REOPEN_GUARD_MS) { debug('réouverture ignorée de ' + key); return false; }
    lastOpen = { key: guard, at: now() };
    remoteOpenKey = key;
    debug('ouverture demandée par un autre : ' + key + (kind === 'detail' ? ' (source privée : choix de la source)' : otherSource && sameVideo(location.hash, hash) ? ' (autre source)' : ''));
    if (inPlayer()) {
      // Changer de lien directement dans le lecteur fait arrêter la nouvelle vidéo par l'ancien
      // lecteur qui se ferme (écran blanc). On quitte d'abord le lecteur, puis on ouvre l'épisode.
      location.hash = '#/';
      setTimeout(() => { if (remoteOpenKey === key) location.hash = target; }, 1500);
    } else {
      location.hash = target;
    }
    return kind;
  }

  // Pause « système » (attente d'un participant) : ne doit pas être partagée comme une action.
  function silentPause() {
    const d = device();
    if (!inPlayer() || !isReady(d) || d.paused) return;
    applying = true;
    try { d.paused = true; } finally { applying = false; }
  }

  const ALLOWED_GIF = /^https:\/\/(media\d?\.giphy\.com|i\.giphy\.com|media\.tenor\.com|c\.tenor\.com)\/[^\s"'<>]+$/i;
  const NAME_COLORS = ['#5498ff', '#3ddc84', '#ffaa46', '#e66edc', '#5ad2dc', '#ff6e6e', '#bea0ff', '#f0dc5a'];
  const nameColor = n => NAME_COLORS[Math.abs([...(n || '')].reduce((h, c) => h * 31 + c.charCodeAt(0) | 0, 17)) % NAME_COLORS.length];
  let chatBox = null, chatInputWrap = null, chatInputEl = null;

  function ensureChatBox() {
    if (chatBox && document.body.contains(chatBox)) return;
    chatBox = document.createElement('div');
    chatBox.id = 'wt-chat';
    chatBox.style.cssText = 'position:fixed;left:20px;bottom:120px;z-index:2147483647;display:flex;flex-direction:column;align-items:flex-start;gap:6px;max-width:420px;pointer-events:none;font:14px/1.4 "Segoe UI",sans-serif';
    document.body.appendChild(chatBox);
  }

  function addChatLine(from, text, gif, mine) {
    ensureChatBox();
    const line = document.createElement('div');
    line.style.cssText = 'background:rgba(6,19,45,.82);border:1px solid rgba(47,128,255,.45);border-radius:10px;padding:6px 10px;color:#e8f0ff;transition:opacity .8s;max-width:420px;word-wrap:break-word';
    const name = document.createElement('b');
    name.textContent = from + ' ';
    name.style.color = mine ? '#8ca0c8' : nameColor(from);
    line.appendChild(name);
    if (text) line.appendChild(document.createTextNode(text));
    if (gif && ALLOWED_GIF.test(gif)) {
      const img = document.createElement('img');
      img.src = gif;
      img.referrerPolicy = 'no-referrer';
      img.style.cssText = 'display:block;max-height:140px;max-width:240px;border-radius:6px;margin-top:4px';
      line.appendChild(img);
    }
    chatBox.appendChild(line);
    while (chatBox.children.length > 6) chatBox.removeChild(chatBox.firstChild);
    setTimeout(() => { line.style.opacity = '0'; setTimeout(() => line.remove(), 900); }, 12000);
  }

  // Barre de saisie du chat (touche T), avec un bouton GIF : la recherche passe par l'appli (message gif-search → réponse gifs).
  let gifPicker = null, gifSearchIn = null, gifGrid = null, gifStatus = null, gifReq = 0, gifTimer = 0;
  function openChatInput() {
    if (chatInputWrap && document.body.contains(chatInputWrap)) { chatInputEl.focus(); return; }
    ensureStyle();
    chatInputWrap = make('div');
    chatInputWrap.id = 'wt-chatbar';
    ['pointerdown', 'mousedown', 'click', 'dblclick'].forEach(t => chatInputWrap.addEventListener(t, e => e.stopPropagation()));
    gifPicker = make('div', 'wt-gifs');
    gifPicker.style.display = 'none';
    const [sf, si] = input('search', '', T("Tendances : tape pour chercher, ou colle un lien Giphy puis Entrée"), v => { clearTimeout(gifTimer); gifTimer = setTimeout(() => searchGifs(v), 400); }, null);
    gifSearchIn = si;
    gifStatus = make('div', 'wt-hint');
    gifGrid = make('div', 'wt-grid');
    gifPicker.append(sf, gifStatus, gifGrid);
    const bar = make('div', 'wt-bar');
    chatInputEl = make('input');
    chatInputEl.maxLength = 300;
    chatInputEl.placeholder = T('Message (ou lien Giphy)…');
    chatInputEl.spellcheck = false;
    const gifBtn = make('button', 'wt-gifbtn', 'GIF');
    gifBtn.onclick = () => toggleGifs();
    bar.append(withIcon(make('span', 'wt-bar-ic'), 'chat', null, 16), chatInputEl, gifBtn);
    const hint = make('div', 'wt-hint', T('Entrée pour envoyer · Échap pour fermer'));
    chatInputWrap.append(gifPicker, bar, hint);
    document.body.appendChild(chatInputWrap);
    chatInputEl.focus();
  }

  function toggleGifs(show) {
    if (!gifPicker) return;
    show = show == null ? gifPicker.style.display === 'none' : show;
    gifPicker.style.display = show ? '' : 'none';
    chatInputWrap.querySelector('.wt-gifbtn').classList.toggle('on', show);
    if (show) { gifSearchIn.focus(); if (!gifGrid.firstChild) searchGifs(gifSearchIn.value); }
    else chatInputEl.focus();
  }

  function searchGifs(q) {
    q = (q || '').trim();
    gifStatus.textContent = q ? T('Recherche…') : T("Tendances : clique sur un GIF pour l'envoyer");
    toNode({ type: 'gif-search', q, id: ++gifReq });
  }

  function showGifs(msg) {
    if (!gifGrid || msg.id !== gifReq) return;
    gifGrid.textContent = '';
    if (msg.off) { gifStatus.textContent = T('Recherche de GIF pas encore activée. Colle un lien Giphy dans la barre ci-dessus puis Entrée.'); return; }
    if (msg.error) { gifStatus.textContent = T('Recherche impossible pour le moment. Tu peux coller un lien Giphy puis Entrée.'); return; }
    const list = (msg.results || []).filter(g => ALLOWED_GIF.test(g.gif) && ALLOWED_GIF.test(g.preview));
    gifStatus.textContent = list.length ? (gifSearchIn.value.trim() ? T("Clique sur un GIF pour l'envoyer") : T("Tendances : clique sur un GIF pour l'envoyer")) : T('Aucun GIF trouvé.');
    list.forEach(g => {
      const img = make('img');
      img.src = g.preview; img.referrerPolicy = 'no-referrer'; img.loading = 'lazy';
      img.onclick = () => { toNode({ type: 'chat', text: '', gif: g.gif }); closeChatInput(); };
      gifGrid.append(img);
    });
  }

  function closeChatInput() {
    if (chatInputWrap) chatInputWrap.remove();
    chatInputWrap = chatInputEl = gifPicker = gifSearchIn = gifGrid = gifStatus = null;
    clearTimeout(gifTimer);
  }

  // Clavier : T ouvre le chat ; pendant la saisie, les touches ne vont pas à Stremio (sinon espace = pause…).
  const typingElsewhere = () => { const a = document.activeElement; return a && !(chatInputWrap && chatInputWrap.contains(a)) && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable); };
  ['keydown', 'keypress', 'keyup'].forEach(type => window.addEventListener(type, e => {
    // Champs du panneau Watch Together : les raccourcis de Stremio ne doivent pas réagir à la frappe.
    if (panelEl && panelEl.contains(document.activeElement)) {
      e.stopImmediatePropagation();
      if (type === 'keydown' && e.key === 'Enter' && document.activeElement.wtEnter) { e.preventDefault(); document.activeElement.wtEnter(); }
      if (type === 'keydown' && e.key === 'Escape') { panelOpen = false; renderPanel(); }
      return;
    }
    if (chatInputWrap && chatInputWrap.contains(document.activeElement)) {
      e.stopImmediatePropagation();
      if (type !== 'keydown') return;
      const inGifs = document.activeElement === gifSearchIn;
      if (e.key === 'Enter') {
        e.preventDefault();
        // Dans la recherche GIF, Entrée n'envoie qu'un lien Giphy collé (l'appli le reconnaît) ; sinon on attend un clic.
        const t = (inGifs ? gifSearchIn : chatInputEl).value.trim();
        if (inGifs && !/^https?:\/\//i.test(t)) return;
        if (t) toNode({ type: 'chat', text: t });
        closeChatInput();
      }
      else if (e.key === 'Escape') { e.preventDefault(); if (inGifs) toggleGifs(false); else closeChatInput(); }
      return;
    }
    if (type === 'keydown' && (e.key === 't' || e.key === 'T') && !e.ctrlKey && !e.altKey && !e.metaKey && !typingElsewhere() && W.inRoom) {
      e.stopImmediatePropagation(); e.preventDefault();
      openChatInput();
    }
  }, true));

  W.onNode = msg => {
    if (msg.type === 'status') { W.inRoom = true; return setStatus(msg); }
    if (msg.type === 'toast') return toast(msg.text);
    if (msg.type === 'chat') return addChatLine(msg.from, msg.text, msg.gif, msg.mine);
    if (msg.type === 'gifs') return showGifs(msg);
    if (msg.type === 'wait') {
      if (msg.mine) {
        // C'est nous qu'on attend : surtout ne pas se mettre en pause, sinon le chargement s'arrête.
        waiting = false;
        const others = (msg.names || []).filter(n => n !== 'toi');
        showBanner(T("Les autres t'attendent (chargement)…") + (others.length ? T(' · aussi : ') + others.join(', ') : ''));
        return;
      }
      waiting = true;
      showBanner(T('En attente de {0} (chargement)…', (msg.names || []).join(', ')));
      silentPause();
      return;
    }
    if (msg.type === 'go') {
      waiting = false;
      hideBanner();
      if (msg.hash && !sameVideo(msg.hash, location.hash)) return;
      const go = { type: 'sync', force: true, time: msg.time, paused: !!msg.paused, receivedAt: msg.receivedAt };
      if (loading || !isReady(device())) { pending = go; return; }
      applyState(go);
      return;
    }
    if (msg.type === 'admin') {
      adminLock = !!msg.on && !msg.isHost;
      if (!msg.isHost) toast(T(msg.on ? "L'hôte contrôle maintenant la lecture" : 'Tout le monde peut de nouveau contrôler la lecture'));
      return;
    }
    if (typeof msg.hash === 'string' && msg.hash.indexOf('#/player/') !== 0) return;
    if (msg.type === 'open') {
      pending = null;
      const opened = openHash(msg);
      if (opened === 'player') toast(T('{0} a lancé une vidéo', msg.from));
      else if (opened === 'detail') showChooseSource(msg.from);
      else if (!sourceFor(msg) && !sameVideo(msg.hash, location.hash)) toast(T('{0} a lancé une source privée : lance la même vidéo de ton côté', msg.from));
      return;
    }
    if (!['play', 'pause', 'seek', 'sync'].includes(msg.type)) return;
    checkLength(msg);
    if (msg.type === 'play') toast(T('{0} a repris la lecture', msg.from));
    if (msg.type === 'pause') toast(T('{0} a mis pause', msg.from));
    if (msg.type === 'seek') toast(T('{0} est allé à {1}', msg.from, fmt(msg.time)));
    if (msg.hash && !sameVideo(msg.hash, location.hash)) {
      if (msg.type === 'sync' && !msg.force && !adminLock) return;
      pending = msg;
      const opened = openHash(msg, msg.force || adminLock);
      if (opened === 'detail') { pending = null; showChooseSource(msg.from); }
      else if (!opened && !inPlayer()) pending = null;
      return;
    }
    if (loading || !isReady(device())) { pending = msg; return; }
    applyState(msg);
  };

  window.addEventListener('hashchange', () => {
    debug('page : ' + (inPlayer() ? videoKey(location.hash) : location.hash.split('/').slice(0, 2).join('/')));
    // Torrent connu dès l'ouverture : l'alerte de téléchargement lent marche même si la vidéo ne démarre jamais.
    if (V5 && inPlayer()) reportTorrent('');
  });
  let playerHooked = false, lastState = null;
  function hookPlayer() {
    if (playerHooked || V5) return;
    try {
      const p = angular.element(document.body).injector().get('player');
      p.on('error', e => { let t; try { t = JSON.stringify(e); } catch (x) { t = String(e); } debug('erreur du lecteur : ' + t); });
      p.on('statechanged', () => {
        const d = device(), s = d && d.state;
        if (s !== lastState) { lastState = s; debug('état du lecteur : ' + s + (d && d.length ? ' à ' + fmt(d.time || 0) : '')); }
      });
      playerHooked = true;
    } catch (e) {}
  }

  // Stremio Web : lecteur ouvert mais pas encore de vidéo. Le serveur analyse d'abord le début du fichier : tant qu'aucun
  // morceau n'est arrivé du torrent (source sans peer joignable), Stremio Web attend sans limite, avec ou sans nous.
  // Compté comme un chargement : sinon l'hôte n'attend pas et lit seul pendant que l'autre reste bloqué.
  let noVideoSince = 0;
  function webWaiting() {
    const v = WEB && inPlayer() ? document.querySelector('video') : null;
    if (!WEB || !inPlayer() || (v && (v.currentSrc || v.src))) { noVideoSince = 0; return false; }
    if (!noVideoSince) noVideoSince = now();
    return true;
  }
  function isBuffering() {
    if (!inPlayer()) return false;
    if (webWaiting()) return now() - noVideoSince > BUFFER_REPORT_MS;
    if (loading) return true;
    const d = device();
    return !!d && !d.paused && (d.state === 1 || d.state === 2);
  }

  function reportBuffering() {
    if (isBuffering()) {
      if (!bufSince) bufSince = now();
      if (!bufReported && now() - bufSince >= BUFFER_REPORT_MS) {
        bufReported = true;
        debug('chargement en cours, signalé aux autres');
        toNode(Object.assign({ type: 'buffer', on: true }, ids()));
      }
    } else {
      bufSince = 0;
      if (bufReported) {
        bufReported = false;
        debug('chargement terminé, signalé aux autres');
        toNode(Object.assign({ type: 'buffer', on: false }, ids()));
      }
    }
  }

  function checkWatched() {
    if (!inPlayer() || loading) return;
    const parts = videoKey(location.hash).split('/');
    if ((parts[2] !== 'movie' && parts[2] !== 'series') || !/^tt\d+$/.test(parts[3] || '')) return;
    const key = videoKey(location.hash);
    if (watchedKey === key) return;
    const d = device();
    if (!isReady(d) || !d.time || d.time / d.length < WATCHED_RATIO) return;
    watchedKey = key;
    debug((parts[2] === 'movie' ? 'film vu : ' : 'épisode vu : ') + parts[3] + (parts[2] === 'series' ? ' ' + decodeURIComponent(parts[4] || '') : ''));
    toNode({ type: 'watched', imdb: parts[3], kind: parts[2] });
  }

  // Fond sombre tant que la vidéo n'a pas de première image : l'interface de Stremio est transparente
  // au-dessus de mpv, donc sans image on voit le fond blanc de la fenêtre (« écran blanc »).
  let backdropEl = null, noFrameSince = 0;
  function updateBackdrop() {
    const d = device();
    const noFrame = inPlayer() && (loading || !isReady(d) || !(d.time > 0));
    if (!noFrame) { noFrameSince = 0; if (backdropEl) backdropEl.style.display = 'none'; return; }
    if (!noFrameSince) noFrameSince = now();
    if (!backdropEl || !document.body.contains(backdropEl)) {
      backdropEl = document.createElement('div');
      backdropEl.id = 'wt-backdrop';
      // z-index négatif : derrière les commandes de Stremio, devant le fond blanc de la fenêtre
      backdropEl.style.cssText = 'position:fixed;inset:0;z-index:-1;background:#06132d;display:none;align-items:center;justify-content:center;flex-direction:column;gap:8px;font:15px/1.4 sans-serif;color:#8ca0c8;pointer-events:none';
      document.body.appendChild(backdropEl);
    }
    const slow = now() - noFrameSince > 20000;
    backdropEl.textContent = '';
    const l1 = document.createElement('div'); l1.textContent = T('Chargement de la vidéo…');
    backdropEl.append(l1);
    if (slow) { const l2 = document.createElement('div'); l2.textContent = T('Ça bloque ? Reviens en arrière et essaie une autre source.'); l2.style.fontSize = '13px'; backdropEl.append(l2); }
    backdropEl.style.display = 'flex';
  }

  setInterval(() => {
    if (WEB) html5.poll();
    hook();
    hookPlayer();
    reportBuffering();
    checkWatched();
    updateBackdrop();
    if (!inPlayer()) { loading = false; loadedKey = null; return; }
    if (!loading) return;
    const d = device();
    if (!isReady(d)) return;
    if (!readyAt) readyAt = now();
    if (now() - readyAt < LOAD_QUIET_MS) return;
    loading = false;
    debug('vidéo prête (durée ' + fmt(d.length) + ')');
    if (pending) { const p = pending; pending = null; applyState(p); }
    else if (wasFollowing) toNode({ type: 'need-sync' });
    if (waiting) silentPause();
  }, 250);

  let box, statusEl, toastsEl, bannerEl, fadeTimer;
  function ensureUi() {
    if (box && document.body.contains(box)) return;
    box = document.createElement('div');
    box.id = 'wt-overlay';
    box.style.cssText = 'position:fixed;top:12px;left:50%;transform:translateX(-50%);z-index:2147483647;font:13px/1.4 sans-serif;color:#fff;pointer-events:none;display:flex;flex-direction:column;align-items:center;gap:6px;transition:opacity .4s';
    statusEl = document.createElement('div');
    statusEl.style.cssText = 'background:rgba(20,20,40,.85);border:1px solid rgba(123,91,245,.7);border-radius:999px;padding:4px 12px;white-space:nowrap';
    toastsEl = document.createElement('div');
    toastsEl.style.cssText = 'display:flex;flex-direction:column;align-items:center;gap:4px';
    bannerEl = document.createElement('div');
    bannerEl.style.cssText = 'display:none;background:rgba(47,128,255,.92);border-radius:8px;padding:6px 14px;font-weight:600';
    box.append(statusEl, bannerEl, toastsEl);
    document.body.appendChild(box);
  }
  function showBanner(text) {
    ensureUi();
    bannerEl.textContent = text;
    bannerEl.style.display = 'block';
    box.style.opacity = '1';
    clearTimeout(fadeTimer);
  }
  function hideBanner() {
    if (!bannerEl) return;
    bannerEl.style.display = 'none';
    wake();
  }
  function wake() {
    ensureUi();
    box.style.opacity = '1';
    clearTimeout(fadeTimer);
    fadeTimer = setTimeout(() => {
      if (!box || (bannerEl && bannerEl.style.display === 'block')) return;
      box.style.opacity = inPlayer() ? '0.25' : '0.8';
    }, 5000);
  }
  function setStatus(s) {
    ensureUi();
    adminLock = !!s.admin && !s.host;
    const dot = s.connected ? '#3ddc84' : '#ff5c5c';
    statusEl.textContent = '';
    const d = document.createElement('span');
    d.style.cssText = 'display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:6px;background:' + dot;
    statusEl.append(d, document.createTextNode(T('Watch Together · salon {0} · ', s.room) +
      (s.connected ? T('{0} connecté(s) : {1}', (s.members || []).length, (s.members || []).join(', ')) : T('déconnecté, reconnexion…')) +
      (s.admin ? T(' · hôte admin') : '')));
    wake();
  }
  // Même épisode mais pas la même durée : ce n'est pas le même fichier (autre version, autre montage),
  // donc le même minutage ne montre pas la même scène. Prévenu une fois par épisode.
  let lengthWarned = null;
  function checkLength(msg) {
    if (!msg.length || !msg.hash || !sameVideo(msg.hash, location.hash)) return;
    const d = device(); if (!isReady(d) || loading) return;
    const diff = d.length - msg.length, key = videoKey(location.hash);
    if (Math.abs(diff) < 20000 || lengthWarned === key) return;
    lengthWarned = key;
    toast(T(diff < 0 ? 'Ta vidéo dure {0} de moins que celle de {1} : prends la même source (même nom, même taille) pour rester calé.' : 'Ta vidéo dure {0} de plus que celle de {1} : prends la même source (même nom, même taille) pour rester calé.', fmt(Math.abs(diff)), msg.from || T("L'hôte")), 10000);
  }
  function toast(text, duration = 4000) {
    ensureUi();
    const t = document.createElement('div');
    t.textContent = text;
    t.style.cssText = 'background:rgba(0,0,0,.75);border-radius:8px;padding:4px 10px';
    toastsEl.appendChild(t);
    setTimeout(() => t.remove(), duration);
    wake();
  }

  // ---- Panneau Watch Together : héberger, rejoindre et inviter sans quitter Stremio ----
  // L'appli envoie son état par W.panel(état) ; les clics repartent par toNode({ type: 'panel', action }).
  // Sans appli à l'écoute (ancienne version), W.panel n'est jamais appelé et rien n'apparaît.
  // Habillé aux couleurs de Stremio (ses variables CSS, avec des valeurs de secours pour les anciennes versions) et en verre dépoli.
  let panelState = null, panelOpen = false, fab, panelEl;
  let nameVal = null, codeVal = '', localVal = false;
  const ICONS = {
    users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
    x: '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
    external: '<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
    user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1"/>',
    play: '<path d="M6 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L7.5 3.64A1 1 0 0 0 6 4.5z" fill="currentColor"/>',
    login: '<path d="M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4"/><path d="m10 17 5-5-5-5"/><path d="M15 12H3"/>',
    wifi: '<path d="M5 12.55a11 11 0 0 1 14.08 0"/><path d="M1.42 9a16 16 0 0 1 21.16 0"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><path d="M12 20h.01"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
    copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
    check: '<path d="M20 6 9 17l-5-5"/>',
    crown: '<path d="m2 6 4.5 4L12 4l5.5 6L22 6l-2 13H4z"/>',
    chat: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
    leave: '<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><path d="m16 17 5-5-5-5"/><path d="M21 12H9"/>',
    search: '<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>',
    alert: '<circle cx="12" cy="12" r="10"/><path d="M12 8v4"/><path d="M12 16h.01"/>'
  };
  const icon = (name, size) => '<svg width="' + (size || 18) + '" height="' + (size || 18) + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + ICONS[name] + '</svg>';
  const CSS = `
#wt-fab, #wt-panel, #wt-chatbar { --wt-accent: var(--primary-accent-color, #7b5bf5); --wt-green: var(--secondary-accent-color, #22b365); --wt-red: var(--danger-accent-color, #dc2626);
  --wt-fg: var(--primary-foreground-color, rgba(255,255,255,.9)); --wt-muted: rgba(255,255,255,.55); --wt-glass: rgba(15,13,32,.72);
  font-family: PlusJakartaSans, "Segoe UI", system-ui, sans-serif; color: var(--wt-fg); box-sizing: border-box; -webkit-font-smoothing: antialiased; }
#wt-panel *, #wt-fab *, #wt-chatbar * { box-sizing: border-box; font-family: inherit; }
#wt-fab { position: fixed; z-index: 2147483647; display: flex; align-items: center; border: 1px solid rgba(255,255,255,.1); border-radius: 999px; cursor: pointer;
  background: var(--wt-glass); backdrop-filter: blur(20px) saturate(160%); -webkit-backdrop-filter: blur(20px) saturate(160%);
  box-shadow: 0 8px 28px rgba(0,0,0,.45); font-weight: 700; font-size: 14px; color: var(--wt-fg); transition: opacity .2s, box-shadow .2s, transform .2s, background .2s; }
#wt-fab:hover, #wt-fab.open { box-shadow: 0 8px 28px rgba(0,0,0,.45), 0 0 18px rgba(123,91,245,.45); border-color: rgba(123,91,245,.6); }
#wt-fab:active { transform: scale(.97); }
#wt-fab .wt-fab-ic { display: flex; align-items: center; justify-content: center; width: 28px; height: 28px; border-radius: 50%; background: var(--wt-accent); color: #fff; }
#wt-fab .wt-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--wt-green); box-shadow: 0 0 8px var(--wt-green); }
#wt-fab .wt-count { display: flex; align-items: center; color: var(--wt-muted); font-weight: 600; }
#wt-fab.small { font-size: 12px; padding: 4px 12px 4px 4px; }
#wt-fab.small .wt-fab-ic { width: 24px; height: 24px; }
#wt-panel { position: fixed; z-index: 2147483647; width: 360px; max-height: calc(100vh - 150px); overflow: auto; padding: 20px;
  background: var(--wt-glass); backdrop-filter: blur(28px) saturate(160%); -webkit-backdrop-filter: blur(28px) saturate(160%);
  border: 1px solid rgba(255,255,255,.09); border-radius: 20px; box-shadow: 0 24px 64px rgba(0,0,0,.55), 0 0 15px rgba(123,91,245,.2);
  font-size: 14px; line-height: 1.4; animation: wt-in .18s ease-out; scrollbar-width: thin; scrollbar-color: rgba(255,255,255,.2) transparent; }
@keyframes wt-in { from { opacity: 0; transform: translateY(8px) scale(.98); } to { opacity: 1; transform: none; } }
#wt-panel .wt-head { display: flex; align-items: center; }
#wt-panel .wt-logo { display: flex; align-items: center; justify-content: center; width: 34px; height: 34px; border-radius: 11px; color: #fff;
  background: linear-gradient(135deg, var(--wt-accent), #5a3fd8); box-shadow: 0 0 15px rgba(123,91,245,.4); flex: none; }
#wt-panel .wt-title { flex: 1; font-weight: 700; font-size: 17px; }
#wt-panel .wt-sub { display: block; font-weight: 500; font-size: 12px; color: var(--wt-muted); }
#wt-panel .wt-ib { display: flex; align-items: center; justify-content: center; width: 32px; height: 32px; border: 0; border-radius: 50%; padding: 0;
  background: transparent; color: var(--wt-muted); cursor: pointer; transition: background .15s, color .15s; }
#wt-panel .wt-ib:hover { background: rgba(255,255,255,.1); color: #fff; }
#wt-panel .wt-sec { margin-top: 14px; padding: 16px; border-radius: 14px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.05); display: flex; flex-direction: column; }
#wt-panel .wt-label { display: flex; align-items: center; font-weight: 700; font-size: 13px; color: var(--wt-fg); }
#wt-panel .wt-label svg { color: var(--wt-accent); }
#wt-panel .wt-hint { color: var(--wt-muted); font-size: 12px; }
#wt-panel .wt-code { text-align: center; font-size: 22px; font-weight: 800; letter-spacing: .12em; padding: 6px 0; color: #fff; user-select: all; }
#wt-panel .wt-btn { display: flex; align-items: center; justify-content: center; width: 100%; min-height: 44px; padding: 0 16px; border-radius: 999px; cursor: pointer;
  font-weight: 700; font-size: 14px; color: #fff; border: 1px solid transparent; background: rgba(255,255,255,.08); transition: background .15s, box-shadow .15s, transform .1s, opacity .15s; }
#wt-panel .wt-btn:hover { background: rgba(255,255,255,.14); }
#wt-panel .wt-btn:active { transform: scale(.98); }
#wt-panel .wt-btn.primary { background: var(--wt-accent); }
#wt-panel .wt-btn.primary:hover { box-shadow: 0 0 15px rgba(123,91,245,.55); background: #8a6dff; }
#wt-panel .wt-btn.danger { background: transparent; border-color: rgba(220,38,38,.45); color: #ff8a8a; }
#wt-panel .wt-btn.danger:hover { background: rgba(220,38,38,.15); }
#wt-panel .wt-btn.done { background: var(--wt-green); }
#wt-panel .wt-btn:disabled { opacity: .45; cursor: default; box-shadow: none; }
#wt-panel .wt-field { display: flex; align-items: center; padding: 0 12px; border-radius: 12px; background: rgba(255,255,255,.06);
  border: 1px solid rgba(255,255,255,.08); color: var(--wt-muted); transition: border-color .15s, background .15s; }
#wt-panel .wt-field:focus-within { border-color: var(--wt-accent); background: rgba(123,91,245,.08); color: var(--wt-accent); }
#wt-panel .wt-field input { flex: 1; min-width: 0; height: 42px; border: 0; outline: 0; background: transparent; color: var(--wt-fg); font-size: 14px; padding: 0; }
#wt-panel .wt-field input::placeholder { color: rgba(255,255,255,.35); }
#wt-panel .wt-row { display: flex; align-items: center; cursor: pointer; font-weight: 600; font-size: 13px; }
#wt-panel .wt-row.off { cursor: default; }
#wt-panel .wt-row .wt-row-ic { color: var(--wt-muted); display: flex; }
#wt-panel .wt-row span.wt-grow { flex: 1; }
#wt-panel input.wt-sw { appearance: none; -webkit-appearance: none; position: relative; flex: none; width: 38px; height: 22px; margin: 0; opacity: 1; border-radius: 999px;
  background: rgba(255,255,255,.18); cursor: inherit; transition: background .2s; }
#wt-panel input.wt-sw::before { content: ""; position: absolute; top: 3px; left: 3px; width: 16px; height: 16px; border-radius: 50%; background: #fff; transition: transform .2s; box-shadow: 0 1px 3px rgba(0,0,0,.4); }
#wt-panel input.wt-sw:checked { background: var(--wt-accent); }
#wt-panel input.wt-sw:checked::before { transform: translateX(16px); }
#wt-panel input.wt-sw:disabled { opacity: .6; }
#wt-panel .wt-status { display: flex; align-items: center; margin-top: 14px; padding: 10px 12px; border-radius: 12px; font-weight: 600; font-size: 13px; background: rgba(34,179,101,.12); color: #6fe3a2; }
#wt-panel .wt-status.wait { background: rgba(255,165,0,.12); color: #ffc46b; }
#wt-panel .wt-status .wt-dot { width: 8px; height: 8px; border-radius: 50%; background: currentColor; box-shadow: 0 0 8px currentColor; }
#wt-panel .wt-member { display: flex; align-items: center; font-weight: 600; }
#wt-panel .wt-av { display: flex; align-items: center; justify-content: center; width: 30px; height: 30px; border-radius: 50%; font-size: 13px; font-weight: 800; color: #fff; flex: none; }
#wt-panel .wt-member small { color: var(--wt-muted); font-weight: 500; }
#wt-panel .wt-msg { display: flex; align-items: flex-start; margin-top: 12px; font-size: 13px; color: var(--wt-muted); }
#wt-panel .wt-msg.err { color: #ff8a8a; }
#wt-panel .wt-msg svg { flex: none; margin-top: 1px; }
#wt-panel .wt-spin { width: 14px; height: 14px; flex: none; margin-top: 2px; border-radius: 50%; border: 2px solid rgba(255,255,255,.2); border-top-color: var(--wt-accent); animation: wt-spin .8s linear infinite; }
@keyframes wt-spin { to { transform: rotate(360deg); } }
#wt-panel .wt-foot { display: flex; align-items: center; margin: 12px 2px; color: var(--wt-muted); font-size: 12px; }
#wt-panel kbd { all: unset; display: inline-block; margin: 0 2px; padding: 0 7px; line-height: 20px; font-size: 12px; font-weight: 700; color: var(--wt-fg); background: rgba(255,255,255,.12); border-radius: 6px; }
/* Espacements en marges : le navigateur intégré à Stremio est trop ancien pour « gap » en flexbox. */
#wt-fab > * + *, #wt-panel .wt-head > * + *, #wt-panel .wt-btn > * + *, #wt-panel .wt-status > * + *, #wt-panel .wt-msg > * + * { margin-left: 10px; }
#wt-panel .wt-label > * + *, #wt-panel .wt-member > * + *, #wt-panel .wt-row > * + * { margin-left: 10px; }
#wt-panel .wt-field > * + * { margin-left: 10px; }
#wt-panel .wt-head > .wt-ib { margin-left: 4px; }
#wt-fab .wt-count > * + * { margin-left: 4px; }
#wt-panel .wt-foot > svg { margin-right: 8px; }
#wt-panel .wt-sec > * + * { margin-top: 12px; }
#wt-panel .wt-sec > .wt-hint { margin-top: 8px; }
#wt-chatbar { position: fixed; left: 20px; bottom: 70px; z-index: 2147483647; width: 440px; max-width: calc(100vw - 40px); }
#wt-chatbar .wt-bar { display: flex; align-items: center; height: 48px; padding: 0 6px 0 14px; border-radius: 999px; background: var(--wt-glass);
  backdrop-filter: blur(20px) saturate(160%); -webkit-backdrop-filter: blur(20px) saturate(160%); border: 1px solid rgba(123,91,245,.6);
  box-shadow: 0 8px 28px rgba(0,0,0,.45), 0 0 15px rgba(123,91,245,.3); }
#wt-chatbar .wt-bar-ic { display: flex; color: var(--wt-accent); }
#wt-chatbar .wt-bar input { flex: 1; min-width: 0; height: 46px; margin: 0 10px; border: 0; outline: 0; padding: 0; background: transparent; color: var(--wt-fg); font-size: 15px; }
#wt-chatbar input::placeholder { color: rgba(255,255,255,.4); }
#wt-chatbar .wt-gifbtn { flex: none; height: 34px; padding: 0 14px; border-radius: 999px; border: 1px solid rgba(255,255,255,.15); background: rgba(255,255,255,.08);
  color: var(--wt-fg); font-weight: 800; font-size: 12px; letter-spacing: .06em; cursor: pointer; transition: background .15s, box-shadow .15s; }
#wt-chatbar .wt-gifbtn:hover { background: rgba(255,255,255,.16); }
#wt-chatbar .wt-gifbtn.on { background: var(--wt-accent); border-color: transparent; box-shadow: 0 0 12px rgba(123,91,245,.6); }
#wt-chatbar > .wt-hint { margin: 6px 0 0 16px; color: rgba(255,255,255,.6); font-size: 12px; text-shadow: 0 1px 2px #000; }
#wt-chatbar .wt-gifs { margin-bottom: 10px; padding: 14px; border-radius: 18px; background: var(--wt-glass); backdrop-filter: blur(28px) saturate(160%); -webkit-backdrop-filter: blur(28px) saturate(160%);
  border: 1px solid rgba(255,255,255,.09); box-shadow: 0 24px 64px rgba(0,0,0,.55); animation: wt-in .18s ease-out; }
#wt-chatbar .wt-field { display: flex; align-items: center; padding: 0 12px; border-radius: 12px; background: rgba(255,255,255,.06); border: 1px solid rgba(255,255,255,.08); color: var(--wt-muted); }
#wt-chatbar .wt-field:focus-within { border-color: var(--wt-accent); color: var(--wt-accent); }
#wt-chatbar .wt-field > * + * { margin-left: 10px; }
#wt-chatbar .wt-field input { flex: 1; min-width: 0; height: 40px; border: 0; outline: 0; padding: 0; background: transparent; color: var(--wt-fg); font-size: 14px; }
#wt-chatbar .wt-gifs > .wt-hint { margin: 10px 2px; color: var(--wt-muted); font-size: 12px; }
#wt-chatbar .wt-grid { display: flex; flex-wrap: wrap; max-height: 300px; overflow: auto; margin: -3px; scrollbar-width: thin; scrollbar-color: rgba(255,255,255,.2) transparent; }
#wt-chatbar .wt-grid img { width: calc(33.333% - 6px); height: 96px; margin: 3px; object-fit: cover; border-radius: 10px; cursor: pointer; background: rgba(255,255,255,.06); transition: transform .12s, box-shadow .12s; }
#wt-chatbar .wt-grid img:hover { transform: scale(1.04); box-shadow: 0 0 0 2px var(--wt-accent); }`;
  function ensureStyle() {
    if (document.getElementById('wt-panel-style')) return;
    const st = document.createElement('style');
    st.id = 'wt-panel-style';
    st.textContent = CSS;
    (document.head || document.documentElement).appendChild(st);
  }
  const make = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
  const withIcon = (el, name, text, size) => { el.innerHTML = icon(name, size); if (text != null) el.append(make('span', null, text)); return el; };
  const button = (ic, text, kind, onClick) => {
    const b = withIcon(make('button', 'wt-btn' + (kind ? ' ' + kind : '')), ic, text, 17);
    b.onclick = e => { e.stopPropagation(); onClick(b); };
    return b;
  };
  const section = (ic, title) => {
    const c = make('div', 'wt-sec');
    if (title) c.append(withIcon(make('div', 'wt-label'), ic, title, 16));
    return c;
  };
  const input = (ic, value, placeholder, onInput, onEnter) => {
    const f = make('label', 'wt-field');
    f.innerHTML = icon(ic, 16);
    const i = make('input');
    i.value = value || ''; i.placeholder = placeholder || ''; i.spellcheck = false;
    i.oninput = () => onInput(i.value);
    i.wtEnter = onEnter;
    f.append(i);
    return [f, i];
  };
  const toggle = (ic, text, checked, enabled, onChange) => {
    const row = make('label', 'wt-row' + (enabled ? '' : ' off'));
    row.append(withIcon(make('span', 'wt-row-ic'), ic, null, 16), make('span', 'wt-grow', text));
    const sw = make('input', 'wt-sw');
    sw.type = 'checkbox'; sw.checked = !!checked; sw.disabled = !enabled;
    sw.onchange = () => onChange(sw.checked);
    row.append(sw);
    return row;
  };
  const flash = (b, text) => {
    const old = b.innerHTML;
    withIcon(b, 'check', text, 17); b.classList.add('done');
    setTimeout(() => { b.innerHTML = old; b.classList.remove('done'); }, 1500);
  };
  const AV = ['#7b5bf5', '#22b365', '#e8569b', '#f59e0b', '#3b82f6', '#14b8a6', '#ef4444', '#a855f7'];
  const avatar = name => {
    let h = 0; for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) | 0;
    const a = make('span', 'wt-av', (name.trim()[0] || '?').toUpperCase());
    a.style.background = AV[Math.abs(h) % AV.length];
    return a;
  };
  const send = (action, extra) => toNode(Object.assign({ type: 'panel', action }, extra || {}));

  function ensureFab() {
    ensureStyle();
    if (fab && document.body.contains(fab)) return;
    fab = make('button');
    fab.id = 'wt-fab';
    fab.onclick = e => { e.stopPropagation(); panelOpen = !panelOpen; renderPanel(); };
    document.body.appendChild(fab);
    placeFab();
  }
  // Hors lecture : en bas à droite. Pendant un film : petit et discret en haut à droite, pour ne pas gêner les commandes.
  function placeFab() {
    if (!fab) return;
    const player = inPlayer(), s = panelState || {};
    fab.className = (player ? 'small' : '') + (panelOpen ? ' open' : '');
    fab.innerHTML = '<span class="wt-fab-ic">' + icon('users', player ? 14 : 16) + '</span>';
    if (s.mode === 'room') {
      fab.append(make('span', 'wt-dot'), make('span', null, s.room));
      fab.append(withIcon(make('span', 'wt-count'), 'user', String((s.members || []).length), 13));
    } else fab.append(make('span', null, 'Watch Together'));
    fab.style.cssText = player ? 'top:64px;right:16px;opacity:' + (panelOpen ? 1 : .55) : 'bottom:24px;right:24px;padding:6px 18px 6px 6px';
    fab.onmouseenter = () => { fab.style.opacity = 1; };
    fab.onmouseleave = () => { if (inPlayer() && !panelOpen) fab.style.opacity = .55; };
  }
  window.addEventListener('hashchange', () => { placeFab(); if (panelOpen) renderPanel(); });

  function renderPanel() {
    ensureStyle();
    placeFab();
    if (!panelEl || !document.body.contains(panelEl)) {
      panelEl = make('div');
      panelEl.id = 'wt-panel';
      panelEl.onclick = e => e.stopPropagation();
      document.body.appendChild(panelEl);
    }
    const player = inPlayer();
    const wasHidden = panelEl.style.display === 'none';
    panelEl.style.cssText = (player ? 'top:108px;right:16px;' : 'bottom:84px;right:24px;') + (panelOpen && panelState ? '' : 'display:none');
    if (!panelOpen || !panelState) return;
    // L'animation d'ouverture ne rejoue qu'à l'ouverture, pas à chaque mise à jour.
    panelEl.style.animation = wasHidden || !panelEl.firstChild ? '' : 'none';
    // Garder le curseur dans le champ en cours de saisie quand on redessine.
    const focused = document.activeElement && panelEl.contains(document.activeElement) ? document.activeElement.dataset.key : null;
    panelEl.textContent = '';
    const s = panelState;
    if (nameVal == null) nameVal = s.name || '';
    if (s.invite && !codeVal) codeVal = s.invite;

    const head = make('div', 'wt-head');
    const logo = make('div', 'wt-logo'); logo.innerHTML = icon('users', 18);
    const title = make('div', 'wt-title', 'Watch Together');
    title.append(make('span', 'wt-sub', s.mode === 'room' ? T('Participants') + ' · ' + (s.members || []).length : 'StremioWT'));
    const app = make('button', 'wt-ib'); app.innerHTML = icon('external', 17); app.title = 'StremioWT';
    app.onclick = e => { e.stopPropagation(); send('window'); };
    const close = make('button', 'wt-ib'); close.innerHTML = icon('x', 18);
    close.onclick = e => { e.stopPropagation(); panelOpen = false; renderPanel(); };
    head.append(logo, title, app, close);
    panelEl.append(head);

    if (s.mode === 'room') {
      const st = make('div', 'wt-status' + (s.connected ? '' : ' wait'));
      st.append(make('span', 'wt-dot'), make('span', null, T(!s.connected ? 'Salon {0} · connexion…' : s.host ? 'Salon {0} · tu héberges' : 'Salon {0} · connecté', s.room)));
      panelEl.append(st);
      if (s.host && s.code) {
        const inv = section('link', T('Inviter des amis'));
        // Code court (« K7MQ2X9F ») : assez court pour être lu à voix haute ou recopié.
        if (/^[0-9A-Z]{8}$/.test(s.code)) inv.append(make('div', 'wt-code', s.code));
        inv.append(button('link', T("Copier le lien d'invitation"), 'primary', b => { send('copy-link'); flash(b, T('Lien copié ✓').replace(' ✓', '')); }));
        inv.append(button('copy', T('Copier le code'), '', b => { send('copy-code'); flash(b, T('Code copié ✓').replace(' ✓', '')); }));
        inv.append(make('div', 'wt-hint', T("Le lien fait aussi télécharger StremioWT aux amis qui ne l'ont pas.")));
        panelEl.append(inv);
      }
      const who = section('users', T('Participants'));
      (s.members || []).forEach(m => {
        const at = m.indexOf(' · '), name = (at < 0 ? m : m.slice(0, at)).trim();
        const row = make('div', 'wt-member');
        row.append(avatar(name), make('span', null, name));
        if (at >= 0) row.append(make('small', null, m.slice(at + 3)));
        who.append(row);
      });
      panelEl.append(who);
      const adm = section(null, null);
      // adminLocked : édition Linux/Mac (bêta), où l'hôte garde toujours la main ; l'appli fournit alors son explication.
      adm.append(toggle('crown', T('Hébergeur admin'), s.admin, s.host && !s.adminLocked, on => send('admin', { on })));
      adm.append(make('div', 'wt-hint', s.adminHint ? s.adminHint : s.host
        ? (s.admin ? T('Activé : toi seul peux mettre pause, avancer ou changer d\'épisode. Les autres suivent.') : T('Désactivé : tout le monde peut mettre pause, avancer ou changer d\'épisode.'))
        : (s.admin ? T("Activé par l'hôte : seul l'hôte contrôle la lecture.") : T('Désactivé : tout le monde peut mettre pause, avancer ou changer d\'épisode.'))));
      panelEl.append(adm);
      const foot = make('div', 'wt-foot');
      foot.innerHTML = icon('chat', 15);
      const tip = T('Touche T dans Stremio'), k = tip.search(/\bT\b/);
      if (k >= 0) foot.append(tip.slice(0, k), make('kbd', null, 'T'), tip.slice(k + 1)); else foot.append(tip);
      panelEl.append(foot);
      panelEl.append(button('leave', T('Quitter'), 'danger', () => send('leave')));
    } else {
      const busy = s.mode === 'busy';
      const me = section('user', T('Ton pseudo'));
      const [nameF, nameIn] = input('user', nameVal, '', v => { nameVal = v; }, null);
      nameIn.dataset.key = 'name'; nameIn.maxLength = 32; nameIn.disabled = busy;
      me.append(nameF);
      panelEl.append(me);

      const host = section('play', T('Héberger un salon'));
      const hb = button('play', T('Héberger un salon'), 'primary', () => send('host', { name: nameVal, local: localVal }));
      hb.disabled = busy;
      host.append(hb, toggle('wifi', T('Option : amis sur le même wifi seulement (sans Internet)'), localVal, !busy, on => { localVal = on; }));
      panelEl.append(host);

      const join = section('login', T('Rejoindre un salon'));
      const doJoin = () => { if (codeVal.trim()) send('join', { name: nameVal, code: codeVal.trim() }); };
      const [codeF, codeIn] = input('link', codeVal, T("Colle ou tape le code reçu (8 caractères ou WT-…)"), v => { codeVal = v; }, doJoin);
      codeIn.dataset.key = 'code'; codeIn.disabled = busy;
      nameIn.wtEnter = doJoin;
      const jb = button('login', T('Rejoindre'), '', doJoin);
      jb.disabled = busy;
      join.append(codeF, jb);
      panelEl.append(join);

      if (s.message) {
        const m = make('div', 'wt-msg' + (s.error ? ' err' : ''));
        if (busy) m.append(make('span', 'wt-spin')); else if (s.error) m.innerHTML = icon('alert', 16);
        m.append(make('span', null, s.message));
        panelEl.append(m);
      }
    }
    if (focused) { const f = panelEl.querySelector('[data-key="' + focused + '"]'); if (f) { f.focus(); f.setSelectionRange(f.value.length, f.value.length); } }
  }

  W.panel = s => {
    const first = !panelState;
    panelState = s;
    if (s.open && first) panelOpen = true;
    if (s.mode === 'room' && first) panelOpen = false;
    ensureFab();
    renderPanel();
  };
  // Au chargement (et après chaque rechargement de l'interface), on redemande l'état à l'appli.
  send('ready');
  // Page ouverte (ou rechargée) directement sur le lecteur : le torrent est signalé tout de suite.
  if (V5 && inPlayer()) reportTorrent('');
  setInterval(() => { if (panelState) ensureFab(); }, 2000);
})();
