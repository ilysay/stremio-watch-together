#!/usr/bin/env bash
# Test de l'édition Linux sur un Ubuntu sans écran (Xvfb) : interface, branchement à Stremio 4 Linux, salon avec un faux ami.
set -u
OUT=artifacts; mkdir -p $OUT
APP=./out/StremioWT
shot() { import -window root "$OUT/$1.png" 2>/dev/null || true; }
eval_js() { node tests/linux/cdp-eval.js 9333 "$1" 2>&1; }

# Machine de test : pas de statistiques (sinon elle compterait comme un utilisateur).
mkdir -p ~/.local/share/StremioWT && echo '{"telemetry":false}' > ~/.local/share/StremioWT/config.json

echo "=== 1. Interface seule"
$APP > $OUT/app.log 2>&1 &
APP_PID=$!
sleep 10
shot 1-interface
kill $APP_PID 2>/dev/null; sleep 1; rm -f ~/.local/share/StremioWT/instance.lock

echo "=== 2. Salon local + branchement à Stremio"
$APP --test-host --lan > $OUT/host.log 2>&1 &
HOST_PID=$!
for i in $(seq 1 60); do grep -q "Connecté à Stremio\|Connected to Stremio\|ECHEC" $OUT/host.log && break; sleep 2; done
cat $OUT/host.log
curl -s --max-time 5 http://127.0.0.1:9333/json | head -c 600; echo
echo "script dans Stremio : $(eval_js 'JSON.stringify({ wt: window.__wt ? { version: window.__wt.version, shell: window.__wt.shell } : null, hash: location.hash.slice(0, 60), url: location.href.slice(0, 60) })')"
shot 2-stremio

INVITE=$(grep -o 'WT-[A-Za-z0-9_-]*' $OUT/host.log | head -1)
SERVER=$(node -e "const c=process.argv[1].slice(3).replace(/-/g,'+').replace(/_/g,'/');const o=JSON.parse(Buffer.from(c,'base64').toString());console.log(o.s.replace(/\/\/[^:/]+/,'//127.0.0.1')+'/r/'+o.r)" "$INVITE")
V=$(echo "$INVITE" | head -c 0; grep -o 'StremioWT [0-9]*\.[0-9]*' ~/.local/share/StremioWT/log.txt | head -1 | awk '{print $2}')
echo "salon : $SERVER (version $V)"

echo "=== 3. Un faux ami rejoint"
curl -s --max-time 40 "$SERVER/poll?id=ami&name=Sam&v=$V"; echo
sleep 3
echo "bandeau : $(eval_js 'JSON.stringify([...document.querySelectorAll("#wt-overlay div")].map(e => e.textContent).filter(Boolean).slice(0, 3))')"
curl -s -X POST -H 'Content-Type: application/json' -d '{"type":"chat","text":"hello from Linux test 🍿"}' "$SERVER/send?id=ami" -o /dev/null -w "chat envoyé : %{http_code}\n"
sleep 2
echo "chat affiché : $(eval_js 'JSON.stringify([...document.querySelectorAll("#wt-chat div")].map(e => e.textContent).slice(-2))')"
shot 3-salon

echo "=== 4. L'ami lance un film (source privée) : Stremio doit ouvrir la fiche"
curl -s -X POST -H 'Content-Type: application/json' -d '{"type":"open","hash":"#/player/movie/tt0111161/tt0111161","private":true}' "$SERVER/send?id=ami" -o /dev/null -w "open envoyé : %{http_code}\n"
sleep 6
echo "page : $(eval_js 'JSON.stringify({ hash: location.hash, bandeau: [...document.querySelectorAll("#wt-overlay div")].map(e => e.textContent).filter(t => /source/i.test(t)).slice(0, 1) })')"
shot 4-fiche

kill $HOST_PID 2>/dev/null
cp ~/.local/share/StremioWT/log.txt $OUT/stremiowt-log.txt 2>/dev/null || true
echo "=== fin"
