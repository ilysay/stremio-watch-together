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
  // « ?forceTranscoding » (vidéo convertie par le serveur, voir plus bas) : même épisode, même source.
  const noQuery = h => (h || '').split('?')[0];
  const videoKey = h => {
    const p = noQuery(h).split('/');
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
  const ids = () => V5 && !privateSource && inPlayer() ? { hash: sharedHash(), v5: noQuery(location.hash) } : { hash: sharedHash() };
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
    const otherSource = msg.type === 'open' && !!target && inPlayer() && dec(noQuery(target)) !== dec(noQuery(location.hash));
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
    if (msg.type === 'roulette-wait') return rouletteWait(msg.by);
    if (msg.type === 'roulette-spin') return rouletteSpin(msg);
    if (msg.type === 'roulette-error') return rouletteError();
    if (msg.type === 'roulette-open') return rouletteOpen(msg.id);
    if (msg.type === 'roulette-vote-open') return rouletteVoteOpen(msg);
    if (msg.type === 'roulette-votes') return rouletteVotes(msg);
    if (msg.type === 'roulette-vetoed') return rouletteVetoed(msg);
    if (msg.type === 'roulette-react') return floatEmoji(msg.e, msg.from);
    if (msg.type === 'roulette-feedback-ask') return rouletteFeedback(msg.name);
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
  // Stremio Web s'arrête sur « Error occurred when decoding » (ou « Video is not supported ») : vidéo que le navigateur
  // croit lire mais ne décode pas (H.264 10 bits des animés…), ou accroc du flux converti. On relance la même vidéo en la
  // faisant convertir par le serveur de Stremio (H.264 8 bits, lisible partout), puis une seconde fois si l'erreur revient.
  // L'écran d'erreur garde le message d'origine en anglais dans son info-bulle, quelle que soit la langue de l'interface.
  // Même épisode : ces nouveaux essais ne sont pas renvoyés aux autres.
  const PLAYER_ERRORS = /^(Error occurred when decoding|Video is not supported|Your device does not support the stream)$/;
  let errorRetry = { key: null, n: 0, at: 0 };
  function recoverPlayerError() {
    if (!WEB || !inPlayer()) return;
    const el = document.querySelector('[class*="error-label"][title]');
    if (!el || !PLAYER_ERRORS.test(el.getAttribute('title') || '')) return;
    const key = videoKey(location.hash);
    if (errorRetry.key !== key) errorRetry = { key, n: 0, at: 0 };
    if (errorRetry.n >= 2 || now() - errorRetry.at < 5000) return;
    errorRetry.n++; errorRetry.at = now();
    debug('erreur du lecteur (' + el.getAttribute('title') + ') : nouvel essai ' + errorRetry.n + ', vidéo convertie par le serveur');
    toast(T('Vidéo illisible par le navigateur : nouvel essai en la convertissant…'));
    location.hash = noQuery(location.hash) + '?forceTranscoding' + (errorRetry.n > 1 ? '&essai=' + errorRetry.n : '');
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
    recoverPlayerError();
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
    // Toujours signalé quand on suit un autre : l'hôte attend ce signal pour lancer tout le monde ensemble.
    if (wasFollowing) toNode({ type: 'need-sync' });
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
  let nameVal = null, codeVal = '';
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
    alert: '<circle cx="12" cy="12" r="10"/><path d="M12 8v4"/><path d="M12 16h.01"/>',
    shuffle: '<path d="M16 3h5v5"/><path d="M4 20 21 3"/><path d="M21 16v5h-5"/><path d="M15 15l6 6"/><path d="M4 4l5 5"/>'
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
#wt-chatbar .wt-grid img:hover { transform: scale(1.04); box-shadow: 0 0 0 2px var(--wt-accent); }
#wt-roulette { position: fixed; top: 0; left: 0; right: 0; bottom: 0; z-index: 2147483647; display: flex; flex-direction: column; align-items: center; justify-content: center;
  overflow: hidden; background: rgba(6,8,22,.88); backdrop-filter: blur(10px); -webkit-backdrop-filter: blur(10px); color: #fff;
  font-family: PlusJakartaSans, "Segoe UI", system-ui, sans-serif; --wt-accent: var(--primary-accent-color, #7b5bf5); animation: wt-in .2s ease-out; }
#wt-roulette * { box-sizing: border-box; font-family: inherit; }
#wt-roulette .wt-r-top { position: absolute; top: 16px; right: 16px; display: flex; z-index: 3; }
#wt-roulette .wt-r-icon { width: 40px; height: 40px; margin-left: 8px; border-radius: 50%; border: 1px solid rgba(255,255,255,.15); background: rgba(255,255,255,.08);
  color: #fff; font-size: 17px; cursor: pointer; }
/* Marge autour du contenu : sans elle, les lueurs (bouton, carte principale, gagnant) sont coupées net au bord de cette zone. */
#wt-roulette .wt-r-body { display: flex; flex-direction: column; align-items: center; max-width: 100vw; padding: 40px 60px 70px; }
#wt-roulette .wt-r-title { font-size: 26px; font-weight: 800; text-align: center; margin: 0 16px 6px; }
#wt-roulette .wt-r-sub { font-size: 14px; color: rgba(255,255,255,.7); text-align: center; margin: 0 16px 8px; }
#wt-roulette .wt-r-compat { font-size: 13px; color: #c9b8ff; text-align: center; margin: 0 16px 16px; }
#wt-roulette .wt-r-window { position: relative; width: 920px; max-width: 94vw; height: 380px; overflow: hidden; margin-top: -10px;
  -webkit-mask-image: linear-gradient(90deg, transparent, #000 15%, #000 85%, transparent); mask-image: linear-gradient(90deg, transparent, #000 15%, #000 85%, transparent); }
/* La rangée qui défile est plus haute que les affiches : l'affiche gagnante, agrandie, ne doit pas être coupée à ses bords. */
#wt-roulette .wt-r-reel { position: absolute; top: 30px; left: 0; display: flex; align-items: center; padding: 40px 0; }
#wt-roulette .wt-r-card { flex: none; width: 160px; height: 240px; margin: 0 8px; border-radius: 14px; overflow: hidden; position: relative;
  background: #1b1d33 center / cover no-repeat; box-shadow: 0 10px 30px rgba(0,0,0,.5); transition: transform .4s, box-shadow .4s, opacity .6s, filter .6s; }
#wt-roulette .wt-r-card span { position: absolute; left: 0; right: 0; bottom: 0; padding: 26px 8px 8px; font-size: 12px; font-weight: 700; line-height: 1.25;
  background: linear-gradient(transparent, rgba(0,0,0,.85)); }
#wt-roulette .wt-r-card.mys { background: linear-gradient(135deg, #2a1f5e, #13112b); }
#wt-roulette .wt-r-card.mys::before { content: "?"; position: absolute; top: 50%; left: 0; right: 0; margin-top: -40px; text-align: center; font-size: 64px; font-weight: 800; color: rgba(255,255,255,.35); }
#wt-roulette .wt-r-card.mys span { display: none; }
#wt-roulette .wt-r-card.flip { animation: wt-flip .6s ease-out; }
@keyframes wt-flip { from { transform: perspective(900px) rotateY(90deg) scale(1.08); } to { transform: perspective(900px) rotateY(0) scale(1.08); } }
#wt-roulette .wt-r-card.win { transform: scale(1.08); box-shadow: 0 0 0 3px var(--wt-accent), 0 0 40px rgba(123,91,245,.8); }
#wt-roulette .wt-r-mark { position: absolute; top: 0; bottom: 0; left: 50%; width: 3px; margin-left: -1px; background: var(--wt-accent); box-shadow: 0 0 14px var(--wt-accent); z-index: 1; }
#wt-roulette .wt-r-finalists { display: flex; justify-content: center; margin-top: 18px; }
#wt-roulette .wt-r-slot { width: 144px; height: 216px; margin: 0 10px; border-radius: 14px; border: 2px dashed rgba(255,255,255,.18); position: relative; transition: transform .3s; }
#wt-roulette .wt-r-slot.filled { border-color: transparent; }
#wt-roulette .wt-r-slot .wt-r-card { width: 140px; height: 210px; margin: 1px; }
#wt-roulette .wt-r-slot.hot { transform: scale(1.07); }
#wt-roulette .wt-r-slot.hot .wt-r-card { box-shadow: 0 0 0 3px #ff5c5c, 0 0 30px rgba(255,92,92,.7); }
#wt-roulette .wt-r-slot.out .wt-r-card { opacity: .25; filter: grayscale(1); }
#wt-roulette .wt-r-slot.out::after { content: "✖"; position: absolute; top: 50%; left: 0; right: 0; margin-top: -48px; text-align: center; font-size: 80px; color: #ff5c5c; text-shadow: 0 0 20px #000; }
#wt-roulette .wt-r-slot.champion { transform: scale(1.12); }
#wt-roulette .wt-r-slot.champion .wt-r-card { box-shadow: 0 0 0 3px #ffd34d, 0 0 50px rgba(255,211,77,.8); }
#wt-roulette .wt-r-result { display: flex; align-items: center; margin-top: 24px; max-width: 94vw; animation: wt-in .3s ease-out; }
#wt-roulette .wt-r-result img { width: 92px; height: 138px; border-radius: 10px; object-fit: cover; margin-right: 18px; flex: none; }
#wt-roulette .wt-r-name { font-size: 22px; font-weight: 800; }
#wt-roulette .wt-r-meta { font-size: 13px; color: rgba(255,255,255,.7); margin-top: 4px; }
#wt-roulette .wt-r-btns { display: flex; flex-wrap: wrap; margin-top: 12px; }
#wt-roulette .wt-r-btns button, #wt-roulette .wt-r-vote button { height: 40px; padding: 0 18px; margin: 0 8px 8px 0; border-radius: 999px; border: 1px solid rgba(255,255,255,.2);
  background: rgba(255,255,255,.08); color: #fff; font-weight: 700; font-size: 14px; cursor: pointer; }
#wt-roulette .wt-r-btns button.primary { background: var(--wt-accent); border-color: transparent; }
#wt-roulette .wt-r-again { justify-content: center; margin-top: 14px; }
#wt-roulette .wt-r-btns button.veto { border-color: rgba(255,92,92,.6); color: #ff9a9a; }
#wt-roulette .wt-r-vote { display: none; align-items: center; margin-top: 4px; }
#wt-roulette .wt-r-vote button { font-size: 20px; padding: 0 16px; }
#wt-roulette .wt-r-vote button.on { background: var(--wt-accent); border-color: transparent; }
#wt-roulette .wt-r-vote span { font-size: 13px; color: rgba(255,255,255,.75); margin-right: 12px; }
#wt-roulette .wt-r-banner { margin-top: 18px; font-size: 22px; font-weight: 800; text-align: center; animation: wt-in .3s ease-out; }
#wt-roulette .wt-r-spin { width: 46px; height: 46px; border-radius: 50%; border: 4px solid rgba(255,255,255,.15); border-top-color: var(--wt-accent); animation: wt-spin .8s linear infinite; margin-bottom: 18px; }
#wt-roulette .wt-r-react { position: absolute; bottom: 18px; left: 0; right: 0; display: flex; justify-content: center; z-index: 3; }
#wt-roulette .wt-r-react button { width: 44px; height: 44px; margin: 0 4px; border-radius: 50%; border: 1px solid rgba(255,255,255,.12); background: rgba(255,255,255,.07);
  font-size: 22px; cursor: pointer; transition: transform .1s; }
#wt-roulette .wt-r-react button:active { transform: scale(.85); }
#wt-roulette .wt-float { position: absolute; bottom: 70px; z-index: 2; font-size: 34px; text-align: center; pointer-events: none; animation: wt-rise 2.6s ease-out forwards; }
#wt-roulette .wt-float small { display: block; font-size: 11px; font-weight: 700; color: rgba(255,255,255,.8); }
@keyframes wt-rise { from { transform: translateY(0) scale(.6); opacity: 0; } 15% { opacity: 1; transform: translateY(-40px) scale(1.1); } to { transform: translateY(-420px) scale(1); opacity: 0; } }
#wt-roulette .wt-confetti { position: absolute; top: -20px; width: 9px; height: 14px; border-radius: 2px; z-index: 2; pointer-events: none; animation: wt-fall 2.4s linear forwards; }
@keyframes wt-fall { to { top: 105%; transform: rotate(720deg) translateX(40px); } }
#wt-r-feedback { position: fixed; right: 20px; bottom: 90px; z-index: 2147483647; display: flex; align-items: center; padding: 12px 14px; border-radius: 16px;
  background: rgba(15,13,32,.92); border: 1px solid rgba(123,91,245,.5); color: #fff; font: 600 14px "Segoe UI", system-ui, sans-serif; box-shadow: 0 12px 40px rgba(0,0,0,.5); }
#wt-r-feedback .wt-artl { display: inline-flex; align-items: center; } #wt-r-feedback .wt-art { margin-right: 8px; display: inline-flex; } #wt-r-feedback span { font-size: inherit; }
#wt-r-feedback button { margin-left: 8px; width: 38px; height: 38px; border-radius: 50%; border: 0; background: rgba(255,255,255,.1); font-size: 18px; cursor: pointer; }
#wt-roulette.picker .wt-r-react { display: none; }
#wt-roulette .wt-pick-hero { position: relative; display: flex; align-items: center; width: 640px; max-width: 92vw; margin-top: 16px; padding: 22px 26px; border-radius: 22px;
  overflow: hidden; text-align: left; background: linear-gradient(130deg, #7b5bf5, #4b2fd0 55%, #ff5c8a); border: 2px solid rgba(255,255,255,.85);
  box-shadow: 0 0 0 4px rgba(123,91,245,.25), 0 18px 50px rgba(123,91,245,.45); transition: transform .15s ease-out; --mx: 50%; }
#wt-roulette .wt-pick-hero::after { content: ""; position: absolute; top: 0; bottom: 0; left: calc(var(--mx) - 60%); width: 120%; pointer-events: none;
  background: linear-gradient(105deg, transparent 40%, rgba(255,255,255,.22) 50%, transparent 60%); }
#wt-roulette .wt-pick-hero::before { content: "✓"; position: absolute; top: 14px; right: 16px; width: 28px; height: 28px; border-radius: 50%; background: #fff; color: #4b2fd0;
  font-weight: 900; text-align: center; line-height: 28px; }
#wt-roulette .wt-pick-hero .wt-pick-em { font-size: 56px; margin-right: 20px; }
#wt-roulette .wt-pick-hero .wt-pick-name { font-size: 22px; margin-top: 0; }
#wt-roulette .wt-pick-hero .wt-pick-tag { font-size: 13px; max-width: 470px; }
#wt-roulette .wt-pick-hero-theme { margin-top: 8px; font-size: 15px; font-weight: 800; }
#wt-roulette .wt-pick-hero-theme:empty { display: none; }
#wt-roulette .wt-pick-em { line-height: 1; filter: drop-shadow(0 4px 10px rgba(0,0,0,.4)); }
#wt-roulette .wt-pick-name { font-weight: 800; }
#wt-roulette .wt-pick-tag { line-height: 1.35; color: rgba(255,255,255,.88); margin-top: 4px; }
#wt-roulette .wt-themes-label { margin-top: 20px; font-size: 13px; color: rgba(255,255,255,.55); }
#wt-roulette .wt-themes { display: flex; flex-wrap: wrap; justify-content: center; max-width: 680px; margin-top: 6px; }
#wt-roulette .wt-theme { margin: 4px; height: 34px; padding: 0 14px; border-radius: 999px; border: 1px solid rgba(255,255,255,.14); background: rgba(255,255,255,.06);
  color: rgba(255,255,255,.85); font-size: 13px; font-weight: 700; cursor: pointer; transition: background .15s, border-color .15s, transform .1s; }
#wt-roulette .wt-theme:hover { background: rgba(255,255,255,.12); transform: translateY(-1px); }
#wt-roulette .wt-theme.on { color: #fff; border-color: transparent; background: linear-gradient(90deg, #7b5bf5, #ff5c8a); }
#wt-roulette .wt-theme-gem.on { background: linear-gradient(90deg, #f9e27d, #b8860b); color: #2a1a00; }
#wt-roulette .wt-opts { display: flex; flex-wrap: wrap; justify-content: center; margin-top: 10px; }
#wt-roulette .wt-opt { display: flex; align-items: center; margin: 4px 8px; height: 34px; padding: 0 6px 0 12px; border-radius: 999px; border: 0; background: transparent;
  color: rgba(255,255,255,.7); font-size: 13px; font-weight: 700; cursor: pointer; }
#wt-roulette .wt-opt .wt-pick-sw { margin-left: 10px; transform: scale(.8); }
#wt-roulette .wt-opt.on { color: #fff; }
#wt-roulette .wt-opt.on .wt-pick-sw { background: var(--wt-accent); }
#wt-roulette .wt-opt.on .wt-pick-sw::before { transform: translateX(18px); }
#wt-roulette .wt-opt.off { opacity: .35; cursor: default; }
#wt-roulette .wt-pick-sw { position: relative; width: 42px; height: 24px; border-radius: 999px; background: rgba(255,255,255,.2); flex: none; transition: background .2s; }
#wt-roulette .wt-pick-sw::before { content: ""; position: absolute; top: 3px; left: 3px; width: 18px; height: 18px; border-radius: 50%; background: #fff; transition: transform .2s; }
#wt-roulette .wt-pick-go { margin-top: 18px; height: 58px; padding: 0 34px; border: 0; border-radius: 999px; color: #fff; font-size: 18px; font-weight: 800; cursor: pointer;
  background: linear-gradient(90deg, #7b5bf5, #ff5c8a); box-shadow: 0 10px 34px rgba(123,91,245,.6); animation: wt-pulse 1.6s ease-in-out infinite; }
@keyframes wt-pulse { 0%, 100% { transform: scale(1); } 50% { transform: scale(1.05); box-shadow: 0 10px 44px rgba(255,92,138,.7); } }
#wt-panel .wt-rbig { display: flex; align-items: center; justify-content: center; width: 100%; height: 56px; border: 0; border-radius: 16px; cursor: pointer; color: #fff;
  font-weight: 800; font-size: 16px; background: linear-gradient(90deg, #7b5bf5, #ff5c8a); box-shadow: 0 8px 24px rgba(123,91,245,.45); transition: transform .12s, box-shadow .2s; }
#wt-panel .wt-rbig:hover { transform: translateY(-2px); box-shadow: 0 12px 30px rgba(255,92,138,.55); }
#wt-panel .wt-rbig .wt-rbig-ic { font-size: 24px; margin-right: 10px; }
#wt-roulette .wt-art, #wt-panel .wt-art { display: inline-flex; flex: none; filter: drop-shadow(0 3px 6px rgba(0,0,0,.45)); }
#wt-roulette .wt-artl, #wt-panel .wt-artl { display: inline-flex; align-items: center; }
#wt-roulette .wt-artl > .wt-art, #wt-panel .wt-artl > .wt-art { margin-right: 6px; }
#wt-roulette .wt-r-title.wt-artl > .wt-art { margin-right: 12px; }
#wt-roulette .wt-plus { margin: 0 6px 0 2px; font-weight: 800; }
/* Stremio impose « * { font-size: 1rem } » : nos textes reprennent la taille de leur conteneur. */
#wt-roulette span, #wt-panel .wt-artl span, #wt-panel .wt-rbig span, #wt-panel .wt-rchoice span { font-size: inherit; }
#wt-roulette .wt-r-subicons { display: flex; flex-wrap: wrap; justify-content: center; align-items: center; }
#wt-roulette .wt-r-subicons > * { margin: 2px 10px; }
#wt-roulette .wt-pick-hero > .wt-art { margin-right: 18px; }
#wt-roulette .wt-pick-hero-theme { display: flex; align-items: center; }
#wt-roulette .wt-theme, #wt-roulette .wt-opt { display: inline-flex; align-items: center; }
#wt-roulette .wt-theme > .wt-art { margin: 0 6px 0 -4px; }
#wt-roulette .wt-opt > .wt-art { margin-right: 6px; }
#wt-roulette .wt-pick-go { display: inline-flex; align-items: center; }
#wt-roulette .wt-pick-go > .wt-art { margin-right: 10px; }
#wt-panel .wt-rbig > .wt-art { margin-right: 10px; }
#wt-panel .wt-rchoice { display: flex; flex-wrap: wrap; align-items: center; }
#wt-panel .wt-rchoice > * { margin: 2px 8px 2px 0; }
#wt-panel .wt-rchoice .wt-plus { margin: 0 6px 0 0; }
#wt-roulette .wt-bgs { display: flex; align-items: center; margin-top: 18px; font-size: 12px; color: rgba(255,255,255,.55); }
#wt-roulette .wt-bg-pick { margin-left: 8px; height: 28px; padding: 0 12px; border-radius: 999px; border: 1px solid rgba(255,255,255,.14); background: rgba(255,255,255,.05);
  color: rgba(255,255,255,.75); font-size: 12px; font-weight: 700; cursor: pointer; }
#wt-roulette .wt-bg-pick.on { background: #fff; color: #1a1030; border-color: transparent; }
/* Décors de fond : un seul est visible, selon le choix de chacun. */
#wt-roulette .wt-r-decor { position: absolute; top: 0; left: 0; right: 0; bottom: 0; overflow: hidden; pointer-events: none; z-index: 0; }
#wt-roulette .wt-r-decor > div { position: absolute; display: none; }
#wt-roulette .wt-r-body, #wt-roulette .wt-r-top, #wt-roulette .wt-r-react { position: relative; z-index: 1; }
#wt-roulette .wt-r-top, #wt-roulette .wt-r-react { position: absolute; }
#wt-roulette.bg-cinema { background: radial-gradient(ellipse at 50% 0%, rgba(80,14,34,.96) 0%, rgba(20,4,11,.96) 60%, rgba(8,2,6,.97) 100%); }
#wt-roulette.bg-cinema .curtain { display: block; top: 0; bottom: 0; width: clamp(50px, 8vw, 140px);
  background: repeating-linear-gradient(90deg, #6d0f22 0 14px, #a11d36 14px 24px, #5a0b1b 24px 36px); box-shadow: inset 0 0 40px rgba(0,0,0,.6), 0 0 40px rgba(0,0,0,.7); }
#wt-roulette.bg-cinema .curtain.l { left: 0; border-bottom-right-radius: 70% 30%; }
#wt-roulette.bg-cinema .curtain.r { right: 0; border-bottom-left-radius: 70% 30%; }
#wt-roulette.bg-cinema .valance { display: block; left: 0; right: 0; top: 0; height: 34px; background: repeating-linear-gradient(90deg, #8a1a2e 0 22px, #6d0f22 22px 44px);
  box-shadow: 0 8px 20px rgba(0,0,0,.6); border-bottom: 3px solid #d4a24a; }
#wt-roulette.bg-spot { background: radial-gradient(ellipse at 50% 120%, rgba(43,26,94,.96) 0%, rgba(11,10,31,.96) 55%, rgba(5,5,15,.97) 100%); }
#wt-roulette.bg-spot .beam { display: block; top: -60px; width: 28vw; height: 140vh; background: linear-gradient(rgba(255,240,200,.16), transparent 75%); filter: blur(8px); transform-origin: top center; }
#wt-roulette.bg-spot .beam.b1 { left: 8vw; transform: rotate(-20deg); animation: wt-sweep1 9s ease-in-out infinite alternate; }
#wt-roulette.bg-spot .beam.b2 { right: 8vw; transform: rotate(20deg); animation: wt-sweep2 11s ease-in-out infinite alternate; }
@keyframes wt-sweep1 { to { transform: rotate(-8deg); } }
@keyframes wt-sweep2 { to { transform: rotate(9deg); } }
#wt-roulette.bg-spot .grain { display: block; top: 0; left: 0; right: 0; bottom: 0; opacity: .07; background-image: radial-gradient(#fff .6px, transparent .7px); background-size: 3px 3px; }
#wt-roulette.bg-aurora { background: rgba(7,8,24,.94); }
#wt-roulette.bg-aurora .blob { display: block; width: 46vw; height: 46vw; border-radius: 50%; filter: blur(90px); opacity: .5; }
#wt-roulette.bg-aurora .blob.x1 { background: #7b5bf5; top: -16vw; left: -12vw; animation: wt-drift 14s ease-in-out infinite alternate; }
#wt-roulette.bg-aurora .blob.x2 { background: #ff5c8a; bottom: -20vw; right: -14vw; animation: wt-drift 17s ease-in-out infinite alternate-reverse; }
#wt-roulette.bg-aurora .blob.x3 { background: #22d3ee; top: 30%; left: 34%; opacity: .2; animation: wt-drift 20s ease-in-out infinite alternate; }
@keyframes wt-drift { to { transform: translate(6vw, 4vw) scale(1.1); } }
#wt-roulette.lite { backdrop-filter: none; -webkit-backdrop-filter: none; }
#wt-roulette.lite .wt-r-decor .beam, #wt-roulette.lite .wt-r-decor .blob, #wt-roulette.lite .wt-r-decor .grain { display: none; }
#wt-roulette.lite .wt-r-window { -webkit-mask-image: none; mask-image: none; }
#wt-roulette.lite .wt-r-reel { will-change: transform; }
#wt-roulette.lite .wt-r-card:not(.win) { box-shadow: none; }
#wt-roulette.lite .wt-r-card { transition: none; }
/* Aurore sans halos animés : les mêmes couleurs, en dégradés fixes. */
#wt-roulette.lite.bg-aurora { background: radial-gradient(circle at 12% 8%, rgba(123,91,245,.5), transparent 42%), radial-gradient(circle at 88% 92%, rgba(255,92,138,.42), transparent 45%),
  radial-gradient(circle at 50% 55%, rgba(34,211,238,.12), transparent 40%), rgba(7,8,24,.97); }`;
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
      // Roulette : un film tiré au sort selon les goûts de tout le salon (historique Stremio de chacun).
      const rou = section('shuffle', T('Roulette : un film pour le salon'));
      const big = make('button', 'wt-rbig');
      big.append(art('wheel', 30), make('span', null, T('Lancer la roulette')));
      big.onclick = e => { e.stopPropagation(); panelOpen = false; renderPanel(); roulettePicker(); };
      // Seul l'hôte fait tourner la roue ; les invités la voient tourner chez eux.
      if (s.host) rou.append(big, rouletteChoice('wt-hint wt-rchoice'));
      else rou.append(make('div', 'wt-hint', T('Seul l\'hôte peut lancer la roulette.')));
      panelEl.append(rou);
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
      const hb = button('play', T('Héberger un salon'), 'primary', () => send('host', { name: nameVal }));
      hb.disabled = busy;
      host.append(hb);
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

  // ---- Roulette : la même roue tourne chez tout le monde et s'arrête sur le film tiré par l'hôte ----
  // Tout le spectacle (rotation, battle royale, éliminations) est calculé par l'hôte et rejoué à l'identique ici :
  // la page ne choisit rien, elle anime. Le vote, le veto et les réactions repartent vers l'appli.
  let rouletteEl = null, rouletteTimer = 0, rouletteState = null, rouletteAnim = 0;
  // Stremio 4 (Qt) dessine la page sans l'aide de la carte graphique : le flou plein écran, le masque de la roue,
  // les ombres de 70 affiches et les halos animés le figeaient. Version allégée, même déroulé ('wt-lite' = '1' la force ailleurs).
  const rouletteLite = () => { if (!V5) return true; try { return localStorage.getItem('wt-lite') === '1'; } catch (e) { return false; } };
  // Genres du catalogue de Stremio (en anglais) → texte français, qui sert de clé aux traductions.
  const GENRE_FR = { Action: 'Action', Adventure: 'Aventure', Animation: 'Animation', Biography: 'Biographie', Comedy: 'Comédie', Crime: 'Policier',
    Documentary: 'Documentaire', Drama: 'Drame', Family: 'Famille', Fantasy: 'Fantastique', History: 'Histoire', Horror: 'Horreur', Mystery: 'Mystère',
    Romance: 'Romance', 'Sci-Fi': 'Science-fiction', Sport: 'Sport', Thriller: 'Thriller', War: 'Guerre', Western: 'Western' };
  const genreName = g => T(GENRE_FR[g] || g);
  // ---- Icônes dessinées de la roulette (SVG 64×64) : identiques sur Windows, Mac et Linux, sans fichier image ----
  // Chaque dessin reçoit un préfixe unique pour ses dégradés : plusieurs exemplaires peuvent cohabiter dans la page.
  const ART = {
    // Logo : une roue de roulette vue de face, avec son repère.
    wheel: p => `
      <defs><linearGradient id="${p}a" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#9b7bff"/><stop offset="1" stop-color="#5a3fd8"/></linearGradient>
      <linearGradient id="${p}b" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff7aa2"/><stop offset="1" stop-color="#ff3d77"/></linearGradient>
      <radialGradient id="${p}h" cx=".4" cy=".35" r=".7"><stop offset="0" stop-color="#fff6c9"/><stop offset="1" stop-color="#f2b33d"/></radialGradient></defs>
      <circle cx="32" cy="34" r="27" fill="#1a1440"/><circle cx="32" cy="34" r="25" fill="url(#${p}a)"/>
      ${[0, 1, 2, 3, 4, 5, 6, 7].map(i => { const a1 = (i * 45 - 90) * Math.PI / 180, a2 = ((i + 1) * 45 - 90) * Math.PI / 180;
        return i % 2 ? '' : `<path d="M32 34 L${32 + 25 * Math.cos(a1)} ${34 + 25 * Math.sin(a1)} A25 25 0 0 1 ${32 + 25 * Math.cos(a2)} ${34 + 25 * Math.sin(a2)}Z" fill="url(#${p}b)"/>`; }).join('')}
      <circle cx="32" cy="34" r="25" fill="none" stroke="#fff" stroke-opacity=".35" stroke-width="1.5"/>
      <circle cx="32" cy="34" r="9" fill="url(#${p}h)"/><circle cx="32" cy="34" r="3" fill="#a5620f"/>
      <path d="M32 13 l-6 -9 h12z" fill="#fff"/><path d="M32 13 l-6 -9 h12z" fill="none" stroke="#1a1440" stroke-width="1.5" stroke-linejoin="round"/>`,

    // Le mélange des goûts : un pot de pop-corn rayé qui déborde.
    popcorn: p => `
      <defs><linearGradient id="${p}k" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fffbe9"/><stop offset="1" stop-color="#ffd978"/></linearGradient>
      <clipPath id="${p}c"><path d="M13 27 H51 L46 59 H18Z"/></clipPath></defs>
      ${[[20, 22, 8], [30, 17, 9], [41, 21, 8], [25, 12, 7], [37, 11, 7], [14, 27, 6], [49, 27, 6], [31, 25, 8]].map(([x, y, r]) =>
        `<circle cx="${x}" cy="${y}" r="${r}" fill="url(#${p}k)" stroke="#e8b34a" stroke-width="1.2"/>`).join('')}
      <g clip-path="url(#${p}c)"><rect x="10" y="26" width="44" height="34" fill="#fff"/>
      ${[0, 1, 2, 3, 4].map(i => `<path d="M${9 + i * 10} 26 h5 l-3 34 h-5z" fill="#ef3b4f"/>`).join('')}</g>
      <path d="M13 27 H51 L46 59 H18Z" fill="none" stroke="#7a1020" stroke-opacity=".35" stroke-width="1.5"/>
      <rect x="11" y="25" width="42" height="5" rx="2.5" fill="#d92b40"/>`,

    // Horreur : une citrouille sculptée qui s'illumine de l'intérieur.
    pumpkin: p => `
      <defs><radialGradient id="${p}o" cx=".45" cy=".4" r=".75"><stop offset="0" stop-color="#ffb04d"/><stop offset="1" stop-color="#e2580f"/></radialGradient>
      <radialGradient id="${p}g" cx=".5" cy=".5" r=".6"><stop offset="0" stop-color="#fff3a0"/><stop offset="1" stop-color="#ffb300"/></radialGradient></defs>
      <path d="M30 14 q2 -8 9 -9" stroke="#3f7d2a" stroke-width="4" fill="none" stroke-linecap="round"/>
      <ellipse cx="20" cy="37" rx="13" ry="20" fill="url(#${p}o)"/><ellipse cx="44" cy="37" rx="13" ry="20" fill="url(#${p}o)"/>
      <ellipse cx="32" cy="37" rx="14" ry="21" fill="url(#${p}o)"/>
      <path d="M32 17 v40 M22 19 q-6 18 0 36 M42 19 q6 18 0 36" stroke="#b9420a" stroke-opacity=".45" stroke-width="1.5" fill="none"/>
      <path d="M18 31 l7 -6 l3 8z M46 31 l-7 -6 l-3 8z" fill="url(#${p}g)" stroke="#5a1d02" stroke-width="1.2" stroke-linejoin="round"/>
      <path d="M17 41 q15 12 30 0 l-4 2 l-3 -2 l-4 3 l-4 -3 l-4 3 l-4 -3 l-3 2z" fill="url(#${p}g)" stroke="#5a1d02" stroke-width="1.2" stroke-linejoin="round"/>`,

    // Nanar : un clap de cinéma cabossé, avec un pansement.
    clap: p => `
      <defs><linearGradient id="${p}d" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#3b3d52"/><stop offset="1" stop-color="#1c1d2b"/></linearGradient></defs>
      <rect x="9" y="25" width="46" height="32" rx="4" fill="url(#${p}d)"/>
      <g transform="rotate(-16 10 24)"><rect x="9" y="14" width="46" height="10" rx="2" fill="#f4f4f8"/>
      ${[0, 1, 2, 3, 4].map(i => `<path d="M${12 + i * 10} 14 h6 l-5 10 h-6z" fill="#1c1d2b"/>`).join('')}</g>
      <path d="M30 26 l-3 9 l5 4 l-4 9" stroke="#8b8fae" stroke-width="1.6" fill="none" stroke-linecap="round"/>
      <g transform="rotate(30 42 43)"><rect x="33" y="39" width="20" height="8" rx="4" fill="#ffcf9e"/><rect x="40" y="39" width="6" height="8" fill="#f2b37a"/>
      <circle cx="36.5" cy="43" r=".9" fill="#c98a52"/><circle cx="49.5" cy="43" r=".9" fill="#c98a52"/></g>
      <text x="17" y="50" font-family="Arial Black,Arial" font-weight="900" font-size="9" fill="#ffd34d">0/10</text>`,

    // Années 90 : une cassette VHS aux couleurs rétro.
    vhs: p => `
      <defs><linearGradient id="${p}r" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#ff3dbb"/><stop offset=".5" stop-color="#ffb347"/><stop offset="1" stop-color="#22d3ee"/></linearGradient></defs>
      <rect x="6" y="15" width="52" height="34" rx="4" fill="#1b1b26"/><rect x="6" y="15" width="52" height="34" rx="4" fill="none" stroke="#3a3a50" stroke-width="1.5"/>
      <rect x="11" y="19" width="42" height="11" rx="2" fill="url(#${p}r)"/>
      <text x="14" y="27.5" font-family="Arial Black,Arial" font-weight="900" font-size="7" fill="#1b1b26">VHS · 1994</text>
      <rect x="16" y="33" width="32" height="12" rx="6" fill="#0d0d14"/>
      ${[22, 42].map(x => `<circle cx="${x}" cy="39" r="4.5" fill="#f4f4f8"/><circle cx="${x}" cy="39" r="1.6" fill="#1b1b26"/>
        ${[0, 60, 120].map(a => `<rect x="${x - .6}" y="35" width="1.2" height="2.2" fill="#1b1b26" transform="rotate(${a} ${x} 39)"/>`).join('')}`).join('')}`,

    // Pépite cachée : un diamant taillé qui scintille.
    gem: p => `
      <defs><linearGradient id="${p}1" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#dff6ff"/><stop offset="1" stop-color="#5ac8ff"/></linearGradient>
      <linearGradient id="${p}2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#3fa7ff"/><stop offset="1" stop-color="#1c5fd6"/></linearGradient></defs>
      <path d="M18 14 H46 L57 27 L32 58 L7 27Z" fill="url(#${p}2)"/>
      <path d="M18 14 L25 27 H39 L46 14Z" fill="url(#${p}1)"/><path d="M7 27 H25 L18 14Z M57 27 H39 L46 14Z" fill="#9fe0ff"/>
      <path d="M25 27 L32 58 L39 27Z" fill="#7fd0ff"/><path d="M7 27 L32 58 L25 27Z" fill="#2b7fe0"/>
      <path d="M18 14 H46 L57 27 L32 58 L7 27Z" fill="none" stroke="#fff" stroke-opacity=".6" stroke-width="1.2" stroke-linejoin="round"/>
      <path d="M52 6 l1.6 4 l4 1.6 l-4 1.6 l-1.6 4 l-1.6 -4 l-4 -1.6 l4 -1.6z" fill="#fff"/>`,

    // Moins de 1 h 40 : un chronomètre, l'aiguille avant la fin du tour.
    timer: p => `
      <defs><linearGradient id="${p}t" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#5eead4"/><stop offset="1" stop-color="#0d9488"/></linearGradient></defs>
      <rect x="28" y="5" width="8" height="7" rx="2" fill="#0f766e"/><rect x="25" y="3" width="14" height="4" rx="2" fill="#14b8a6"/>
      <path d="M47 13 l5 -4 l3 3 l-4 5z" fill="#0f766e"/>
      <circle cx="32" cy="36" r="24" fill="url(#${p}t)"/><circle cx="32" cy="36" r="19" fill="#f4fffd"/>
      <path d="M32 17 A19 19 0 1 1 15.6 26.5 L32 36Z" fill="#99f6e4"/>
      ${[...Array(12)].map((_, i) => `<rect x="31.2" y="18.5" width="1.6" height="${i % 3 ? 2.5 : 4}" fill="#0f766e" transform="rotate(${i * 30} 32 36)"/>`).join('')}
      <path d="M32 36 L18.5 28" stroke="#0b3b37" stroke-width="2.6" stroke-linecap="round"/><circle cx="32" cy="36" r="2.6" fill="#0b3b37"/>`,

    // Surprise : un dé en relief.
    dice: p => `
      <path d="M32 6 L56 19 L32 32 L8 19Z" fill="#ffffff"/><path d="M8 19 L32 32 V59 L8 46Z" fill="#e6e3ff"/><path d="M56 19 L32 32 V59 L56 46Z" fill="#c9c3ff"/>
      <path d="M32 6 L56 19 V46 L32 59 L8 46 V19Z M8 19 L32 32 L56 19 M32 32 V59" fill="none" stroke="#5a3fd8" stroke-width="1.6" stroke-linejoin="round"/>
      <ellipse cx="32" cy="19" rx="3.2" ry="2" fill="#ff3d77"/>
      <ellipse cx="14" cy="28" rx="2" ry="2.6" fill="#5a3fd8"/><ellipse cx="20" cy="38" rx="2" ry="2.6" fill="#5a3fd8"/><ellipse cx="26" cy="48" rx="2" ry="2.6" fill="#5a3fd8"/>
      <ellipse cx="38" cy="38" rx="2" ry="2.6" fill="#5a3fd8"/><ellipse cx="50" cy="28" rx="2" ry="2.6" fill="#5a3fd8"/>
      <ellipse cx="38" cy="50" rx="2" ry="2.6" fill="#5a3fd8"/><ellipse cx="50" cy="40" rx="2" ry="2.6" fill="#5a3fd8"/>`,

    // Battle royale : deux épées croisées.
    swords: p => `
      <defs><linearGradient id="${p}s" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#9aa4c4"/></linearGradient></defs>
      ${[45, -45].map(a => `<g transform="rotate(${a} 32 32)">
        <path d="M29 6 h6 v34 l-3 4 l-3 -4z" fill="url(#${p}s)" stroke="#4b5374" stroke-width="1"/>
        <rect x="22" y="40" width="20" height="4" rx="2" fill="#f2b33d"/><rect x="30" y="44" width="4" height="10" rx="1.5" fill="#7a4a1c"/>
        <circle cx="32" cy="56" r="3" fill="#f2b33d"/></g>`).join('')}`,

    // Affiches cachées : une carte retournée avec un point d'interrogation.
    hidden: p => `
      <defs><linearGradient id="${p}m" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#8b6cff"/><stop offset="1" stop-color="#3b1fa8"/></linearGradient></defs>
      <rect x="20" y="10" width="30" height="44" rx="5" fill="#2a2450" transform="rotate(12 35 32)"/>
      <rect x="14" y="10" width="30" height="44" rx="5" fill="url(#${p}m)" stroke="#fff" stroke-opacity=".5" stroke-width="1.5" transform="rotate(-8 29 32)"/>
      <text x="29" y="42" text-anchor="middle" font-family="Arial Black,Arial" font-weight="900" font-size="26" fill="#fff" transform="rotate(-8 29 32)">?</text>`,
  };
  let artId = 0;
  const art = (name, size) => { const e = document.createElement('span'); e.className = 'wt-art';
    e.innerHTML = '<svg width="' + size + '" height="' + size + '" viewBox="0 0 64 64">' + ART[name]('wa' + (++artId)) + '</svg>'; return e; };
  // Une icône suivie d'un texte.
  const artLabel = (name, size, text, cls) => { const e = make('span', 'wt-artl' + (cls ? ' ' + cls : '')); e.append(art(name, size), make('span', null, text)); return e; };
  const MODE_ART = { classic: 'popcorn', horror: 'pumpkin', nanar: 'clap', '90s': 'vhs', gem: 'gem', short: 'timer', surprise: 'dice' };
  const ROULETTE_MODES = { classic: ['🎬', 'Classique'], horror: ['🎃', 'Horreur'], nanar: ['🤣', 'Nanar'], '90s': ['📼', 'Années 90'],
    gem: ['💎', 'Pépite cachée'], short: ['⏱️', 'Moins de 1 h 40'] };
  const ROULETTE_BGS = [['cinema', 'Salle de cinéma'], ['spot', 'Projecteurs'], ['aurora', 'Aurore']];
  const REACTIONS = ['🔥', '😂', '😱', '🤞', '😍', '💀', '👏', '🍿'];
  const CARD_W = 176; // largeur d'une affiche + ses marges
  // Choix gardés d'une fois sur l'autre sur ce PC (mode, battle royale, mystère, son).
  const rouletteOpts = { mode: 'classic', battle: false, mystery: false, surprise: false, muted: false, bg: 'cinema' };
  try { Object.assign(rouletteOpts, JSON.parse(localStorage.getItem('wt-roulette') || '{}')); } catch (e) {}
  const saveRouletteOpts = () => { try { localStorage.setItem('wt-roulette', JSON.stringify(rouletteOpts)); } catch (e) {} };
  const launchRoulette = () => toNode(rouletteOpts.surprise
    ? { type: 'roulette', mode: ['classic', 'horror', 'nanar', '90s', 'gem', 'short'][Math.floor(Math.random() * 6)], battle: Math.random() < 0.4, mystery: Math.random() < 0.4 }
    : { type: 'roulette', mode: rouletteOpts.mode, battle: !!rouletteOpts.battle, mystery: !!rouletteOpts.mystery });

  // Sons de la roue, fabriqués sur place (aucun fichier) : tic à chaque affiche, fanfare, élimination, veto.
  let audioCtx = null;
  function sfx(kind, n) {
    if (rouletteOpts.muted) return;
    try {
      audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
      if (audioCtx.state === 'suspended') audioCtx.resume();
      const t0 = audioCtx.currentTime;
      const note = (freq, at, dur, type, vol) => {
        const o = audioCtx.createOscillator(), g = audioCtx.createGain();
        o.type = type; o.frequency.value = freq;
        g.gain.setValueAtTime(vol, t0 + at); g.gain.exponentialRampToValueAtTime(0.0001, t0 + at + dur);
        o.connect(g); g.connect(audioCtx.destination); o.start(t0 + at); o.stop(t0 + at + dur + 0.02);
      };
      if (kind === 'tick') note(850 + (n % 6) * 40, 0, 0.035, 'square', 0.025);
      else if (kind === 'win') [523, 659, 784, 1047].forEach((f, i) => note(f, i * 0.11, 0.25, 'triangle', 0.08));
      else if (kind === 'out') { note(170, 0, 0.35, 'sawtooth', 0.05); note(110, 0.1, 0.45, 'sawtooth', 0.05); }
      else if (kind === 'veto') { note(300, 0, 0.18, 'square', 0.05); note(200, 0.18, 0.4, 'square', 0.05); }
      else if (kind === 'hover') note(1300, 0, 0.04, 'sine', 0.02);
      else if (kind === 'select') { note(660, 0, 0.07, 'triangle', 0.06); note(990, 0.06, 0.1, 'triangle', 0.05); }
    } catch (e) {}
  }

  function rouletteStop() { clearTimeout(rouletteTimer); cancelAnimationFrame(rouletteAnim); rouletteAnim = 0; if (rouletteState) clearInterval(rouletteState.countdown); }
  function rouletteClose() { rouletteStop(); rouletteState = null; if (rouletteEl) rouletteEl.remove(); rouletteEl = null; }

  // Le cadre de la roulette (bouton son, fermer, barre de réactions) ; renvoie la zone centrale, vidée.
  function rouletteBody() {
    ensureStyle();
    if (!rouletteEl || !document.body.contains(rouletteEl)) {
      rouletteEl = make('div');
      rouletteEl.id = 'wt-roulette';
      ['pointerdown', 'mousedown', 'click', 'dblclick'].forEach(t => rouletteEl.addEventListener(t, e => e.stopPropagation()));
      const top = make('div', 'wt-r-top');
      const snd = make('button', 'wt-r-icon', rouletteOpts.muted ? '🔇' : '🔊');
      snd.onclick = () => { rouletteOpts.muted = !rouletteOpts.muted; snd.textContent = rouletteOpts.muted ? '🔇' : '🔊'; saveRouletteOpts(); };
      const x = make('button', 'wt-r-icon', '✕');
      x.onclick = rouletteClose;
      top.append(snd, x);
      const react = make('div', 'wt-r-react');
      REACTIONS.forEach(em => { const b = make('button', null, em); b.onclick = () => { floatEmoji(em, T('toi')); toNode({ type: 'roulette-react', e: em }); }; react.append(b); });
      const decor = make('div', 'wt-r-decor');
      ['curtain l', 'curtain r', 'valance', 'beam b1', 'beam b2', 'grain', 'blob x1', 'blob x2', 'blob x3'].forEach(c => decor.append(make('div', c)));
      rouletteEl.append(decor, top, make('div', 'wt-r-body'), react);
      if (rouletteLite()) rouletteEl.classList.add('lite');
      document.body.appendChild(rouletteEl);
    }
    rouletteStop();
    rouletteEl.classList.remove('picker');
    ROULETTE_BGS.forEach(b => rouletteEl.classList.toggle('bg-' + b[0], b[0] === (rouletteOpts.bg || 'cinema')));
    const body = rouletteEl.querySelector('.wt-r-body');
    body.textContent = '';
    return body;
  }

  // ---- Écran de sélection : de grandes cartes de mode, comme le menu d'un jeu ----
  // Le choix en cours, en icônes : le mélange des goûts, plus le thème et les réglages éventuels.
  function rouletteChoice(cls) {
    const e = make('div', cls);
    e.append(artLabel('popcorn', 18, T('Le mélange de vos goûts')));
    const m = rouletteOpts.surprise ? 'surprise' : rouletteOpts.mode;
    if (m !== 'classic') e.append(make('span', 'wt-plus', '+'), artLabel(MODE_ART[m], 18, m === 'surprise' ? T('Surprise') : T(ROULETTE_MODES[m][1])));
    if (rouletteOpts.battle && !rouletteOpts.surprise) e.append(artLabel('swords', 18, T('Battle royale')));
    if (rouletteOpts.mystery && !rouletteOpts.surprise) e.append(artLabel('hidden', 18, T('Affiches cachées')));
    return e;
  }
  // Carte qui s'incline vers la souris, avec un reflet qui la traverse.
  function tilt(card) {
    card.addEventListener('mousemove', e => {
      const r = card.getBoundingClientRect(), x = (e.clientX - r.left) / r.width - 0.5, y = (e.clientY - r.top) / r.height - 0.5;
      card.style.transform = 'perspective(700px) rotateY(' + (x * 14) + 'deg) rotateX(' + (-y * 14) + 'deg) translateY(-4px)';
      card.style.setProperty('--mx', (x + 0.5) * 100 + '%');
    });
    card.addEventListener('mouseenter', () => sfx('hover'));
    card.addEventListener('mouseleave', () => { card.style.transform = ''; });
  }
  function roulettePicker() {
    const body = rouletteBody();
    rouletteEl.classList.add('picker');
    body.append(artLabel('wheel', 46, 'Roulette', 'wt-r-title'));
    // La base, toujours active : le mélange des goûts du salon.
    const hero = make('div', 'wt-pick-hero');
    const heroTheme = make('div', 'wt-pick-hero-theme');
    const heroText = make('div', 'wt-pick-hero-text');
    heroText.append(make('div', 'wt-pick-name', T('Le mélange de vos goûts')),
      make('div', 'wt-pick-tag', T('Un film au hasard parmi ceux qui plaisent à tout le monde, d\'après ce que chacun a déjà regardé.')), heroTheme);
    hero.append(art('popcorn', 84), heroText);
    tilt(hero);
    // En option : un thème (un seul, désélectionnable) et deux petits réglages.
    const themes = make('div', 'wt-themes');
    const theme = (mode, emoji, name) => {
      const t = make('button', 'wt-theme wt-theme-' + mode.replace(/\W/g, ''));
      t.append(art(MODE_ART[mode], 22), make('span', null, name));
      t.dataset.mode = mode;
      t.onclick = () => {
        sfx('select');
        const was = rouletteOpts.surprise ? 'surprise' : rouletteOpts.mode;
        rouletteOpts.surprise = mode === 'surprise' && was !== 'surprise';
        rouletteOpts.mode = mode === 'surprise' || was === mode ? 'classic' : mode;
        saveRouletteOpts(); refresh();
      };
      t.onmouseenter = () => sfx('hover');
      themes.append(t);
    };
    Object.keys(ROULETTE_MODES).filter(m => m !== 'classic').forEach(m => theme(m, ROULETTE_MODES[m][0], T(ROULETTE_MODES[m][1])));
    theme('surprise', '🎲', T('Surprise'));
    const opts = make('div', 'wt-opts');
    [['battle', 'swords', T('Battle royale'), T('3 tirages, puis élimination jusqu\'au dernier')],
     ['mystery', 'hidden', T('Affiches cachées'), T('Les affiches restent cachées jusqu\'à l\'arrêt')]].forEach(([opt, pic, name, hint]) => {
      const o = make('button', 'wt-opt');
      o.append(art(pic, 24), make('span', null, name));
      o.dataset.opt = opt;
      o.title = hint;
      o.append(make('span', 'wt-pick-sw'));
      o.onclick = () => { if (rouletteOpts.surprise) return; sfx('select'); rouletteOpts[opt] = !rouletteOpts[opt]; saveRouletteOpts(); refresh(); };
      opts.append(o);
    });
    const launch = make('button', 'wt-pick-go');
    const refresh = () => {
      const current = rouletteOpts.surprise ? 'surprise' : rouletteOpts.mode;
      themes.querySelectorAll('.wt-theme').forEach(t => t.classList.toggle('on', t.dataset.mode === current));
      opts.querySelectorAll('.wt-opt').forEach(o => { o.classList.toggle('on', !!rouletteOpts[o.dataset.opt]); o.classList.toggle('off', !!rouletteOpts.surprise); });
      const m = ROULETTE_MODES[rouletteOpts.mode];
      heroTheme.textContent = '';
      const cur = rouletteOpts.surprise ? 'surprise' : rouletteOpts.mode;
      if (cur !== 'classic') heroTheme.append(make('span', 'wt-plus', '+'), artLabel(MODE_ART[cur], 22, cur === 'surprise' ? T('Surprise') : T(m[1])));
      launch.textContent = '';
      launch.append(art('wheel', 32), make('span', null, T('Lancer !')));
    };
    const go = () => { sfx('win'); launchRoulette(); };
    launch.onclick = go;
    hero.ondblclick = go;
    const bgs = make('div', 'wt-bgs');
    bgs.append(make('span', null, T('Fond :')));
    ROULETTE_BGS.forEach(([id, name]) => {
      const b = make('button', 'wt-bg-pick' + (rouletteOpts.bg === id ? ' on' : ''), T(name));
      b.onclick = () => {
        sfx('select'); rouletteOpts.bg = id; saveRouletteOpts();
        ROULETTE_BGS.forEach(x => rouletteEl.classList.toggle('bg-' + x[0], x[0] === id));
        bgs.querySelectorAll('.wt-bg-pick').forEach(x => x.classList.toggle('on', x === b));
      };
      bgs.append(b);
    });
    body.append(hero, make('div', 'wt-themes-label', T('Envie d\'un thème ? (facultatif)')), themes, opts, launch, bgs);
    refresh();
  }


  function floatEmoji(em, who) {
    if (!rouletteEl) return;
    const f = make('div', 'wt-float', em);
    if (who) f.append(make('small', null, who));
    f.style.left = (10 + Math.random() * 80) + '%';
    rouletteEl.append(f);
    setTimeout(() => f.remove(), 2700);
  }

  function confetti() {
    if (!rouletteEl) return;
    const colors = ['#7b5bf5', '#ffd34d', '#3ddc84', '#ff5c8a', '#5ac8ff', '#ff8a3d'];
    for (let i = 0, n = rouletteLite() ? 24 : 90; i < n; i++) {
      const p = make('i', 'wt-confetti');
      p.style.left = Math.random() * 100 + '%';
      p.style.background = colors[i % colors.length];
      p.style.animationDelay = Math.random() * 0.5 + 's';
      p.style.animationDuration = 1.8 + Math.random() * 1.4 + 's';
      rouletteEl.append(p);
      setTimeout(() => p.remove(), 4200);
    }
  }

  const posterUrl = f => f && f.poster && /^https:\/\//.test(f.poster) ? f.poster.replace(/["\\]/g, '') : null;
  function rouletteCard(f, mystery) {
    const c = make('div', 'wt-r-card' + (mystery ? ' mys' : ''));
    if (!mystery && posterUrl(f)) c.style.backgroundImage = 'url("' + posterUrl(f) + '")';
    c.append(make('span', null, f.name || f.id));
    return c;
  }
  function reveal(c, f) {
    if (!c.classList.contains('mys')) return;
    c.classList.remove('mys');
    if (posterUrl(f)) c.style.backgroundImage = 'url("' + posterUrl(f) + '")';
    c.classList.add('flip');
  }

  function rouletteWait(by) {
    const body = rouletteBody();
    body.append(make('div', 'wt-r-spin'), artLabel('wheel', 40, T('Roulette lancée par {0}', by || '?'), 'wt-r-title'),
      make('div', 'wt-r-sub', T('On mélange les goûts de tout le salon…')));
    rouletteTimer = setTimeout(rouletteError, 60000); // filet de sécurité si l'hôte ne répond plus
  }
  function rouletteError() {
    const body = rouletteBody();
    body.append(art('wheel', 56), make('div', 'wt-r-sub', T('Roulette impossible pour le moment. Réessaie dans un instant.')));
    rouletteTimer = setTimeout(rouletteClose, 4000);
  }

  // Une rotation : les affiches défilent, ralentissent fort à la fin (on frôle la voisine) et s'arrêtent sur stopAt.
  function spinReel(win, items, order, stopAt, duration, mystery, done) {
    const reel = make('div', 'wt-r-reel'), cards = [], LAPS = rouletteLite() ? 4 : 6;
    for (let lap = 0; lap < LAPS; lap++) order.forEach(i => { const c = rouletteCard(items[i], mystery); reel.append(c); cards.push(c); });
    win.append(reel);
    const width = win.clientWidth || 920;
    const target = (LAPS - 2) * order.length + stopAt;
    const start = width / 2 - CARD_W / 2;
    const end = width / 2 - (target * CARD_W + CARD_W / 2) + (Math.random() - 0.5) * 90;
    const dist = start - end;
    // Une seule décélération, de plus en plus forte : les dernières affiches défilent très lentement jusqu'à l'arrêt.
    const ease = t => 1 - Math.pow(1 - t, 4);
    const t0 = performance.now();
    let last = -1;
    const step = time => {
      const t = Math.min(1, (time - t0) / duration);
      const x = start - dist * ease(t);
      reel.style.transform = 'translateX(' + x + 'px)';
      const idx = Math.floor((width / 2 - x) / CARD_W);
      if (idx !== last) { last = idx; sfx('tick', idx); }
      if (t < 1) rouletteAnim = requestAnimationFrame(step);
      else { rouletteAnim = 0; done(cards[target]); }
    };
    reel.style.transform = 'translateX(' + start + 'px)';
    rouletteAnim = requestAnimationFrame(step);
  }

  function rouletteHeader(body, msg) {
    body.append(artLabel('wheel', 40, T('Roulette lancée par {0}', msg.by || '?'), 'wt-r-title'));
    const mode = ROULETTE_MODES[msg.mode];
    const common = (msg.common || []).slice(0, 3).map(genreName);
    const sub = make('div', 'wt-r-sub wt-r-subicons');
    if (msg.battle) sub.append(artLabel('swords', 20, T('Battle royale : 3 tirages, puis on élimine jusqu\'au dernier')));
    if (mode && msg.mode !== 'classic') sub.append(artLabel(MODE_ART[msg.mode], 20, T(mode[1])));
    else sub.append(make('span', null, common.length ? T('Vos goûts communs : {0}', common.join(', ')) : T('Films populaires du moment (pas assez d\'historique pour connaître vos goûts)')));
    body.append(sub);
    const compat = (msg.compat || []).slice(0, 3).map(c => T('{0} et {1} : {2} % compatibles', c.a, c.b, c.pct));
    if (compat.length) body.append(make('div', 'wt-r-compat', '💞 ' + compat.join(' · ')));
  }

  function rouletteSpin(msg) {
    const items = (msg.items || []).slice(0, 24).map(f => f && /^tt\d+$/.test(f.id) ? f : null);
    const picks = (msg.picks || []).filter(i => items[i]);
    const reels = (msg.reels || []).map(r => (r || []).filter(i => items[i]));
    if (!items[msg.winner] || !picks.length || reels.length < picks.length) return rouletteError();
    rouletteState = { round: msg.round, winner: items[msg.winner], host: !!msg.host };
    const body = rouletteBody();
    rouletteHeader(body, msg);
    const win = make('div', 'wt-r-window');
    win.append(make('div', 'wt-r-mark'));
    if (!msg.battle) {
      body.append(win);
      spinReel(win, items, reels[0], reels[0].indexOf(picks[0]), 8500, msg.mystery, c => {
        reveal(c, items[picks[0]]);
        c.classList.add('win');
        rouletteTimer = setTimeout(() => rouletteResult(body, msg, items), 500);
      });
      return;
    }
    // Battle royale : trois tirages remplissent les places des finalistes, puis on élimine jusqu'au dernier.
    const stage = make('div', 'wt-r-sub');
    const finals = make('div', 'wt-r-finalists');
    const slots = picks.map(() => { const s = make('div', 'wt-r-slot'); finals.append(s); return s; });
    body.append(stage, win, finals);
    let k = 0;
    const nextDraw = () => {
      if (k >= picks.length) return eliminate();
      stage.textContent = '';
      stage.append(artLabel('swords', 22, T('Tirage {0} sur {1}', k + 1, picks.length)));
      win.querySelectorAll('.wt-r-reel').forEach(r => r.remove());
      spinReel(win, items, reels[k], reels[k].indexOf(picks[k]), 6000, msg.mystery, c => {
        reveal(c, items[picks[k]]);
        c.classList.add('win');
        sfx('win');
        slots[k].append(rouletteCard(items[picks[k]], false));
        slots[k].classList.add('filled');
        k++;
        rouletteTimer = setTimeout(nextDraw, 1300);
      });
    };
    const alive = picks.map((_, i) => i);
    let round = 0;
    const eliminate = () => {
      win.remove();
      stage.textContent = '';
      stage.append(artLabel('swords', 22, T('Les finalistes')));
      rouletteTimer = setTimeout(eliminationRound, 900);
    };
    const eliminationRound = () => {
      const out = (msg.eliminated || [])[round];
      if (out == null || alive.length <= 1) {
        const last = alive[0];
        slots.forEach(s => s.classList.remove('hot'));
        slots[last].classList.add('champion');
        stage.textContent = '🏆 ' + T('Dernier survivant');
        rouletteTimer = setTimeout(() => rouletteResult(body, msg, items), 1300);
        return;
      }
      // La surbrillance passe d'un finaliste à l'autre, de plus en plus lentement, et s'arrête sur l'éliminé.
      let hops = 12 + alive.length;
      while (alive[(hops - 1) % alive.length] !== out) hops++;
      let h = 0;
      const hop = () => {
        slots.forEach(s => s.classList.remove('hot'));
        slots[alive[h % alive.length]].classList.add('hot');
        sfx('tick', h);
        h++;
        if (h < hops) { rouletteTimer = setTimeout(hop, 60 + h * h * 3); return; }
        rouletteTimer = setTimeout(() => {
          slots[out].classList.remove('hot');
          slots[out].classList.add('out');
          sfx('out');
          stage.textContent = '❌ ' + T('Éliminé : {0}', items[picks[out]].name);
          alive.splice(alive.indexOf(out), 1);
          round++;
          rouletteTimer = setTimeout(eliminationRound, 1700);
        }, 600);
      };
      hop();
    };
    nextDraw();
  }

  // Le film gagnant : fanfare, confettis, infos, boutons (regarder, bande-annonce, veto, relancer) et vote express.
  function rouletteResult(body, msg, items) {
    const w = items[msg.winner];
    sfx('win');
    confetti();
    const res = make('div', 'wt-r-result');
    if (posterUrl(w)) { const img = make('img'); img.onerror = () => img.remove(); img.src = posterUrl(w); res.append(img); }
    const info = make('div');
    info.append(make('div', 'wt-r-name', w.name + (w.year ? ' (' + w.year + ')' : '')),
      make('div', 'wt-r-meta', [(w.genres || []).map(genreName).join(', '), w.rating ? '★ ' + w.rating + ' IMDb' : ''].filter(Boolean).join(' · ')));
    if (msg.fits) info.append(make('div', 'wt-r-meta', '🎯 ' + T('Ce film colle surtout aux goûts de {0}', msg.fits)));
    const st = msg.stats || {};
    if (st.picks > 0) info.append(make('div', 'wt-r-meta', '📊 ' + T('Films choisis par la roue : {0}', st.picks) + (st.rated > 0 ? ' · ' + T('La roue a vu juste {0} fois sur {1}', st.good, st.rated) : '')));
    const btns = make('div', 'wt-r-btns');
    const btn = (text, cls, fn) => { const b = make('button', cls, text); b.onclick = () => fn(b); btns.append(b); return b; };
    if (msg.host) btn(T('On regarde !'), 'primary', () => toNode({ type: 'roulette-go', id: w.id }));
    if (w.trailer && /^[A-Za-z0-9_-]{11}$/.test(w.trailer)) btn('▶ ' + T('Bande-annonce'), '', () => toNode({ type: 'roulette-trailer', yt: w.trailer }));
    if (msg.vetoLeft !== false) btn('🚫 ' + T('Veto'), 'veto', b => { b.remove(); toNode({ type: 'roulette-veto' }); });
    if (msg.host) btn(T('Relancer'), '', launchRoulette);
    info.append(btns, make('div', 'wt-r-vote'));
    if (!msg.host) info.append(make('div', 'wt-r-meta', T("L'hôte peut lancer ce film pour tout le monde.")));
    res.append(info);
    body.append(res);
    if (msg.host) toNode({ type: 'roulette-done', round: msg.round });
  }

  function rouletteVoteOpen(msg) {
    const box = rouletteEl && rouletteEl.querySelector('.wt-r-vote');
    if (!box || !rouletteState || rouletteState.round !== msg.round) return;
    box.textContent = '';
    box.style.display = 'flex';
    let left = msg.seconds || 12;
    const yes = make('button', null, '👍'), no = make('button', null, '👎');
    const vote = v => { toNode({ type: 'roulette-vote', yes: v }); yes.classList.toggle('on', v); no.classList.toggle('on', !v); };
    yes.onclick = () => vote(true);
    no.onclick = () => vote(false);
    const timer = make('span', null, T('Vote : {0} s', left));
    const tally = make('span', 'wt-r-tally', T('{0} pour, {1} contre', 0, 0));
    box.append(yes, no, timer, tally);
    clearInterval(rouletteState.countdown);
    rouletteState.countdown = setInterval(() => { left--; timer.textContent = T('Vote : {0} s', Math.max(0, left)); if (left <= 0) clearInterval(rouletteState.countdown); }, 1000);
  }

  function rouletteVotes(msg) {
    if (!rouletteEl || !rouletteState || rouletteState.round !== msg.round) return;
    const tally = rouletteEl.querySelector('.wt-r-tally');
    if (tally) tally.textContent = T('{0} pour, {1} contre', msg.yes || 0, msg.no || 0);
    if (!msg.result) return;
    clearInterval(rouletteState.countdown);
    const box = rouletteEl.querySelector('.wt-r-vote');
    if (box) box.style.display = 'none';
    if (msg.result === 'yes') rouletteEl.querySelector('.wt-r-body').append(make('div', 'wt-r-banner', '🎉 ' + T('Le salon a dit oui : on regarde !')));
    if (msg.result === 'no') { sfx('out'); rouletteEl.querySelector('.wt-r-body').append(make('div', 'wt-r-banner', '👎 ' + T('Le salon a dit non : film écarté.'))); afterRefusal(); }
  }

  // Film refusé (vote ou veto) : l'hôte relance quand il veut, les invités l'attendent.
  function afterRefusal() {
    const body = rouletteEl && rouletteEl.querySelector('.wt-r-body');
    if (!body) return;
    body.querySelectorAll('.wt-r-btns').forEach(e => e.remove());
    if (rouletteState && rouletteState.host) {
      const btns = make('div', 'wt-r-btns wt-r-again'), b = make('button', 'primary', T('Relancer'));
      b.onclick = launchRoulette;
      btns.append(b);
      body.append(btns);
    } else body.append(make('div', 'wt-r-meta', T('L\'hôte peut relancer la roulette.')));
  }

  function rouletteVetoed(msg) {
    if (!rouletteEl) return;
    rouletteStop();
    sfx('veto');
    const body = rouletteEl.querySelector('.wt-r-body');
    body.querySelectorAll('.wt-r-btns, .wt-r-vote').forEach(e => e.remove());
    body.append(make('div', 'wt-r-banner', '🚫 ' + T('{0} a mis son veto !', msg.by || '?')));
    afterRefusal();
  }

  function rouletteOpen(id) {
    rouletteClose();
    if (!/^tt\d+$/.test(id || '')) return;
    location.hash = '#/detail/movie/' + id + '/' + id;
  }

  // Après le film choisi par la roue : « elle avait raison ? » (compte dans les statistiques de la roulette).
  function rouletteFeedback(name) {
    ensureStyle();
    const old = document.getElementById('wt-r-feedback');
    if (old) old.remove();
    const box = make('div');
    box.id = 'wt-r-feedback';
    box.append(artLabel('wheel', 26, T('La roue avait raison avec {0} ?', name || '?')));
    [['👍', true], ['👎', false]].forEach(([em, good]) => {
      const b = make('button', null, em);
      b.onclick = e => { e.stopPropagation(); toNode({ type: 'roulette-feedback', good }); box.remove(); };
      box.append(b);
    });
    document.body.appendChild(box);
    setTimeout(() => box.remove(), 60000);
  }

  window.addEventListener('keydown', e => { if (rouletteEl && e.key === 'Escape') { e.stopImmediatePropagation(); e.preventDefault(); rouletteClose(); } }, true);

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
