<div align="center">

<img src="../docs/logo.png" width="112" alt="Stremio Watch Together logo">

# Stremio Watch Together for Mac

**Watch Stremio with your friends from a Mac, in sync.**
Same rooms, same invite codes as Windows and Linux.

[![Download](https://img.shields.io/github/v/release/ilysay/stremio-watch-together?label=download&style=for-the-badge&color=2f80ff)](https://github.com/ilysay/stremio-watch-together/releases/latest)

![Test version](https://img.shields.io/badge/status-test%20version-e5484d?style=flat-square)
![macOS 13+](https://img.shields.io/badge/macOS-13%2B-0c2048?style=flat-square&logo=apple&logoColor=white)
![Apple Silicon and Intel](https://img.shields.io/badge/Apple%20Silicon%20%2B%20Intel-0c2048?style=flat-square)

</div>

> [!WARNING]
> **This is a test version, and it's not stable yet.** In our tests it starts, hosts rooms and stays in sync with Windows, but it hasn't been used on real Macs by real people yet. Expect bugs: it may freeze, lose sync or refuse to start. If something goes wrong, click **🐞 Report a bug** in the app, or [open an issue](https://github.com/ilysay/stremio-watch-together/issues) with your Mac model and macOS version. Every report helps!

## Download

**[Download StremioWT-mac.zip](https://github.com/ilysay/stremio-watch-together/releases/latest/download/StremioWT-mac.zip)**

One download for every Mac: it runs natively on **Apple Silicon** (M1 to M4) and on **Intel** Macs, and picks the right version by itself.

**You also need:** macOS 13 Ventura or newer, and **Google Chrome**, **Microsoft Edge** or **Brave**.

## Install

1. Double-click the zip, then drag **StremioWT** into your **Applications** folder.
2. Open it. macOS will say it can't check the app, because it isn't notarized by Apple (that costs $99 a year). To open it anyway:
   - **macOS 15 Sequoia and newer**: click **Done**, then go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to StremioWT.
   - **macOS 13 and 14**: right-click the app → **Open** → **Open**.
   - Or, in Terminal: `xattr -dr com.apple.quarantine /Applications/StremioWT.app`

You only need to do this once. The app is open source: every line is in this repository, and you can [build it yourself](#build-it-yourself).

## How it works on Mac

The Stremio app for Mac can't be driven by another app, so StremioWT opens **Stremio Web** (web.stremio.com, your same account and addons) in a separate Chrome, Edge or Brave window with its own profile. Your usual browser isn't touched. The first time, log in to your Stremio account in that window.

**Torrents** need Stremio's streaming server: keep the **Stremio app for Mac** open in the background if you have it. Real-Debrid and other debrid links play without it.

## How to use it

| | Host | Friends |
|---|---|---|
| **1** | Click **Host a room** | Get the invite link or code from the host |
| **2** | Copy the invite link and send it | Copy the code, then open StremioWT: it joins by itself |
| **3** | Pick something in Stremio | Everyone's Stremio follows |

Keep the app open during the movie (you can minimize it). Mac, Windows and Linux friends can all be in the same room.

## Not in the test version yet

| Feature | Windows | Mac (test) |
|---|:---:|:---:|
| Rooms, sync, chat | ✅ | ✅ |
| Opening the room by clicking the invite link | ✅ | copy or type the code instead |
| One-click updates | ✅ | opens the download page |
| Animated GIFs in the chat | ✅ | ✅ |
| My diary + Letterboxd export | ✅ | should work, not tested yet |
| Safari or Firefox | n/a | not yet (Chrome, Edge or Brave) |

## Problems?

<details>
<summary><b>"StremioWT is damaged and can't be opened"</b></summary>

It isn't damaged: that's how macOS describes apps that aren't notarized. Run this in Terminal, then open the app again:

```bash
xattr -dr com.apple.quarantine /Applications/StremioWT.app
```
</details>

<details>
<summary><b>"No compatible browser found"</b></summary>

Install [Google Chrome](https://www.google.com/chrome/) (or Edge or Brave), then open StremioWT again.
</details>

<details>
<summary><b>A torrent doesn't play</b></summary>

Open the Stremio app for Mac and leave it running in the background: it provides the streaming server Stremio Web needs for torrents.
</details>

<details>
<summary><b>My friends can't join</b></summary>

The first time you host, the app downloads `cloudflared` (Cloudflare's official tool) to open the room to the Internet. Wait for it to finish. If you're all on the same Wi-Fi, tick **same Wi-Fi only** instead.
</details>

<details>
<summary><b>Something else</b></summary>

Click **🐞 Report a bug** in ⚙ Settings (or under the error message): you see exactly what's sent before sending. Or [open an issue](https://github.com/ilysay/stremio-watch-together/issues) with your Mac model and macOS version.
</details>

## Build it yourself

You need git and the [.NET 9 SDK](https://dot.net).

```bash
git clone https://github.com/ilysay/stremio-watch-together.git
cd stremio-watch-together
dotnet publish linux/StremioWT.Linux.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o out
./out/StremioWT
```

Use `-r osx-x64` on an Intel Mac. The Mac and Linux editions share the same code (in `linux/`).

---

<div align="center">
<sub>Made with 🍿 for movie nights. <a href="../README.md">Back to the main page</a></sub>
</div>
