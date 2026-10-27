<div align="center">

<img src="../docs/logo.png" width="112" alt="Stremio Watch Together logo">

# Stremio Watch Together for Linux

**Watch Stremio with your friends from Linux, perfectly in sync.**
Same rooms, same invite codes: Linux and Windows friends watch together.

[![Download](https://img.shields.io/github/v/release/ilysay/stremio-watch-together?label=download&style=for-the-badge&color=2f80ff)](https://github.com/ilysay/stremio-watch-together/releases/latest)

![Beta](https://img.shields.io/badge/status-beta-f5a623?style=flat-square)
![Linux x64](https://img.shields.io/badge/Linux-x64-0c2048?style=flat-square&logo=linux&logoColor=white)
![Works with Windows](https://img.shields.io/badge/rooms-Windows%20%E2%86%94%20Linux-0c2048?style=flat-square)

<img src="../docs/linux/preview-linux.png" width="760" alt="The Linux app: home screen and a room with friends chatting">

</div>

> [!NOTE]
> **This is a beta, and your feedback is very welcome!** Rooms, chat and sync are tested between Linux and Windows, but Linux setups are all different. Whether it worked perfectly or not at all, tell us your distro and desktop: click **🐞 Report a bug** in the app, or [open an issue](https://github.com/ilysay/stremio-watch-together/issues).

## What you get

- 🎬 **Real sync**: play, pause, seek and next episode follow the whole room.
- 🐧 **Linux ↔ Windows**: one invite code works for everyone, whatever their system.
- 🔗 **Join in one click**: click the invite link your friend sent (or copy the code and open the app), you're in.
- 💬 **Chat and emotes**, in the window and right over the video (press **T** in Stremio).
- 👀 **See everyone's Stremio version** next to their name, with a heads-up when versions don't open torrents the same way.
- 📖 **My diary**: everything you watch is logged with posters and ratings, and movies export to Letterboxd.
- 🛡️ **Private sources stay private**: debrid links are never sent. Friends get the title and pick their own source.

<div align="center">
<img src="../docs/linux/stremio-linux.jpg" width="820" alt="Stremio with the room bar and the chat over the movie page">
<br><sub>The room bar and the chat, right inside Stremio.</sub>
</div>

## Install (one command)

Download **[install.sh](https://github.com/ilysay/stremio-watch-together/releases/latest/download/install.sh)** and **[StremioWT-linux-x64](https://github.com/ilysay/stremio-watch-together/releases/latest/download/StremioWT-linux-x64)** into the same folder, open a terminal there and run:

```bash
bash install.sh
```

It checks what you already have and only installs what's missing (your password is asked once if needed):

1. **Stremio** from Flathub (skipped if you already have it).
2. A **Chromium browser** for Stremio Web (skipped if you have Chrome, Chromium, Edge or Brave).
3. **Stremio's official streaming server**, started with your session: it plays torrents and the video formats browsers can't.
4. **Stremio Watch Together**, with an icon in your applications menu.

Then open **Stremio Watch Together** from your applications menu. The first time you host or join, a Stremio Web window opens: log in to your Stremio account there.

The script is [short and readable](install.sh): have a look before running it. It works on Ubuntu, Debian, Mint, Pop!_OS, Fedora, Arch and openSUSE, and never needs to run as root.

## How it works on Linux

The new Stremio app for Linux (Flathub) can't be driven by another app, so StremioWT opens **Stremio Web** (web.stremio.com, your same account and addons) in a separate browser window, with its own profile: your usual browser isn't touched.

If you still have **Stremio 4** installed (the old `.deb`), StremioWT drives it directly instead, with no browser needed.

## How to use it

| | Host | Friends |
|---|---|---|
| **1** | Click **Host a room** | Get the code from the host |
| **2** | The invite code is copied: send it | Copy it and open the app: it joins by itself |
| **3** | Pick something in Stremio | Everyone's Stremio follows |

Keep the app open during the movie (you can minimize it).

## Prefer to do it yourself?

- **Stremio**: `flatpak install flathub com.stremio.Stremio` (or keep your Stremio 4).
- **Browser**: Google Chrome, Chromium (`sudo snap install chromium` on Ubuntu), Edge or Brave. Firefox isn't supported yet.
- **Streaming server**: Node.js and ffmpeg, then run Stremio's [server.js](https://dl.strem.io/server/v4.20.12/desktop/server.js) with `node server.js`.
- **The app**: `chmod +x StremioWT-linux-x64` then `./StremioWT-linux-x64`.

## Build it yourself

You need git and the [.NET 9 SDK](https://dot.net).

```bash
git clone https://github.com/ilysay/stremio-watch-together.git
cd stremio-watch-together
dotnet publish linux/StremioWT.Linux.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
./out/StremioWT
```

## Not in the beta yet

| Feature | Windows | Linux beta |
|---|:---:|:---:|
| Rooms, sync, invites, chat | ✅ | ✅ |
| Animated GIFs in the chat | ✅ | first frame only |
| My diary + Letterboxd export | ✅ | ✅ |
| Firefox | n/a | coming (Chrome, Chromium, Edge or Brave for now) |

## Problems?

<details>
<summary><b>"Video is not supported" or "Streaming server is not available"</b></summary>

The browser needs Stremio's streaming server for torrents and for formats like x265 or AC3/DTS audio. Run `bash install.sh` again: it installs and starts the server. You can check it's running with `curl http://127.0.0.1:11470/settings`.
</details>

<details>
<summary><b>"Neither Stremio 4 nor a compatible browser was found"</b></summary>

Run `bash install.sh` again, or install Google Chrome or Chromium yourself.
</details>

<details>
<summary><b>My friends can't join</b></summary>

The first time you host, the app downloads `cloudflared` (Cloudflare's official tool, ~60 MB) to open the room to the Internet. Wait for it to finish.
</details>

<details>
<summary><b>Something else</b></summary>

Click **🐞 Report a bug** in ⚙ Settings (or under the error message): you see exactly what's sent before sending. Or open an issue and attach `~/.local/share/StremioWT/log.txt`.
</details>

<details>
<summary><b>Uninstall</b></summary>

```bash
systemctl --user disable --now stremiowt-server.service
rm -rf ~/.local/bin/stremiowt ~/.local/bin/stremiowt-launch ~/.local/share/applications/stremiowt.desktop ~/.config/systemd/user/stremiowt-server.service ~/.local/share/StremioWT
```
Stremio and the browser stay installed.
</details>

---

<div align="center">
<sub>Made with 🍿 for movie nights. <a href="../README.md">Back to the main page</a></sub>
</div>
