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

  const V5 = /(^|\.)stremio\.com$/.test(location.hostname);
  // Stremio Web dans un navigateur (Mac) : même interface que Stremio 5, mais la vidéo est un <video> HTML.
  const WEB = V5 && !(window.chrome && window.chrome.webview);
  W.shell = WEB ? 'web' : V5 ? 5 : 4;
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

  W.state = () => {
    const d = device();
    if (!inPlayer() || !isReady(d) || loading) return null;
    return Object.assign(ids(), { paused: !!d.paused, time: d.time || 0, length: d.length || 0 });
  };

  function onOut(m) {
    if (!m || m.type !== 6 || !Array.isArray(m.args)) return;
    const ev = m.args[0], args = m.args[1] || [];
    if (ev === 'mpv-command' && args[0] === 'loadfile') {
      loading = true; readyAt = 0;
      const h = location.hash;
      privateSource = !isLocalUrl(args[1]) && !(WEB && /^blob:/.test(args[1] || ''));
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
    const quiet = () => { quietUntil = now() + 900; };
    const out = (ev, args) => { if (now() >= quietUntil) onOut({ type: 6, args: [ev, args] }); };
    const dev = {
      get time() { return video ? video.currentTime * 1000 : 0; },
      set time(ms) { if (video) { quiet(); video.currentTime = ms / 1000; } },
      get paused() { return !video || video.paused; },
      set paused(p) { if (!video) return; quiet(); if (p) video.pause(); else video.play().catch(() => {}); },
      get length() { return video && isFinite(video.duration) ? video.duration * 1000 : 0; },
      get state() { return !video || video.readyState < 1 ? 1 : video.seeking || video.readyState < 3 ? 2 : video.paused ? 4 : 3; },
    };
    function attach(v) {
      video = v;
      v.addEventListener('loadstart', () => onOut({ type: 6, args: ['mpv-command', ['loadfile', v.currentSrc || v.src || '']] }));
      v.addEventListener('play', () => out('mpv-set-prop', ['pause', false]));
      v.addEventListener('pause', () => { if (!v.ended) out('mpv-set-prop', ['pause', true]); });
      v.addEventListener('seeked', () => out('mpv-set-prop', ['time-pos', v.currentTime]));
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
    if (sameVideo(location.hash, hash)) return false;
    let target = sourceFor(msg), kind = 'player';
    if (!target) {
      const parts = key.split('/');
      if (parts[2] === 'other' || !parts[3]) return false;
      target = '#/detail/' + (V5 ? parts.slice(2, 5).map(encodeURIComponent) : parts.slice(2, 5)).join('/');
      kind = 'detail';
      if (location.hash === target) return false;
    }
    if (!force && lastOpen.key === key && now() - lastOpen.at < REOPEN_GUARD_MS) { debug('réouverture ignorée de ' + key); return false; }
    lastOpen = { key, at: now() };
    remoteOpenKey = key;
    debug('ouverture demandée par un autre : ' + key + (kind === 'detail' ? ' (source privée : choix de la source)' : ''));
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

  function openChatInput() {
    if (chatInputWrap && document.body.contains(chatInputWrap)) { chatInputEl.focus(); return; }
    chatInputWrap = document.createElement('div');
    chatInputWrap.style.cssText = 'position:fixed;left:20px;bottom:70px;z-index:2147483647;display:flex;flex-direction:column;gap:3px;font:13px "Segoe UI",sans-serif';
    chatInputEl = document.createElement('input');
    chatInputEl.maxLength = 300;
    chatInputEl.placeholder = T('Message (ou lien Giphy)…');
    chatInputEl.style.cssText = 'width:380px;padding:8px 12px;border-radius:10px;border:1px solid #2f80ff;background:rgba(8,24,56,.95);color:#e8f0ff;outline:none;font:14px "Segoe UI",sans-serif';
    const hint = document.createElement('div');
    hint.textContent = T('Entrée pour envoyer · Échap pour fermer');
    hint.style.cssText = 'color:#8ca0c8;text-shadow:0 1px 2px #000';
    chatInputWrap.append(chatInputEl, hint);
    document.body.appendChild(chatInputWrap);
    chatInputEl.focus();
  }

  function closeChatInput() {
    if (chatInputWrap) chatInputWrap.remove();
    chatInputWrap = chatInputEl = null;
  }

  // Clavier : T ouvre le chat ; pendant la saisie, les touches ne vont pas à Stremio (sinon espace = pause…).
  const typingElsewhere = () => { const a = document.activeElement; return a && a !== chatInputEl && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable); };
  ['keydown', 'keypress', 'keyup'].forEach(type => window.addEventListener(type, e => {
    if (chatInputEl && document.activeElement === chatInputEl) {
      e.stopImmediatePropagation();
      if (type !== 'keydown') return;
      if (e.key === 'Enter') { const t = chatInputEl.value.trim(); if (t) toNode({ type: 'chat', text: t }); closeChatInput(); e.preventDefault(); }
      else if (e.key === 'Escape') { closeChatInput(); e.preventDefault(); }
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

  window.addEventListener('hashchange', () =>
    debug('page : ' + (inPlayer() ? videoKey(location.hash) : location.hash.split('/').slice(0, 2).join('/'))));
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

  function isBuffering() {
    if (!inPlayer()) return false;
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
    if (!inPlayer()) { loading = false; return; }
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
})();
