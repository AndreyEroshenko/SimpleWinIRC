# SimpleWinIRC

A vibe-coding exercise with Claude Code — a minimal WinUI 3 IRC client built for joining a couple of specific channels (`#ebooks` on `irc.irchighway.net`, `#Bookz` on `irc.undernet.org`) to search for and download ebooks via the channels' bots. See [The Idiot-Proof Guide to Downloading eBooks via IRC](https://www.reddit.com/r/Piracy/comments/2oftbu/guide_the_idiot_proof_guide_to_downloading_ebooks/) for background on the workflow.

This is a personal-use client; it intentionally trades general IRC ergonomics (multi-channel UI, nicklists, scrollback search, etc.) for a focused search-and-download flow.

## Implemented features

- **IRC connection** — TCP/SSL with NICK/USER registration and PING/PONG keepalive.
- **Server presets** — editable combo box prefilled with `irc.irchighway.net` (port 9999, SSL on, ignore self-signed cert) and `irc.undernet.org` (port 6667, plain). An `<add new>` entry clears the field so a fresh server name can be typed in; freshly connected servers are persisted to the dropdown for the rest of the session.
- **Advanced options toggle** — hides the Port, SSL, and "Ignore cert errors" controls by default and reveals them when ticked, so the standard flow stays minimal.
- **Channel auto-fill** — selecting a server preset also fills the matching default channel (`#ebooks` for irchighway, `#Bookz` for undernet).
- **Persistent nickname** — the last-used nickname is stored to `%LOCALAPPDATA%\SimpleWinIRC\settings.json` and pre-filled on next launch.
- **Chat input** — typed messages are wrapped as `PRIVMSG <channel> :…` to the most recently joined channel, so commands like `@search ...` reach the channel bots correctly. Before any channel is joined, the box still accepts raw IRC commands.
- **DCC SEND receive (active mode)** — incoming CTCP `DCC SEND` offers are detected, surfaced in an Accept/Decline dialog with a save-file picker, and streamed to disk with the legacy 4-byte big-endian ack so older bots don't drop the connection. This covers the `@search` results delivery in `#ebooks` and `!trigger`-style book downloads from XDCC bots.

Deliberately not implemented (and unlikely to be added unless actually needed): passive/reverse DCC, `DCC ACCEPT` resume, DCC CHAT, multi-channel UI, nicklists.

## Build

Open the solution in Visual Studio 2022 (with the Windows App SDK workload), or build from the command line. The project is an unpackaged WinUI 3 desktop app targeting `net8.0-windows10.0.19041.0`, x64.

**Debug build** — small, fast, depends on the Windows App Runtime being installed on the dev machine:

```
dotnet build SimpleWinIRC/SimpleWinIRC.csproj -c Debug -p:Platform=x64
```

Output: `SimpleWinIRC/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/` (~108 MB, ~235 files).

**Release build** — fully self-contained for distribution; bundles both the .NET 8 runtime and the Windows App SDK runtime, so the target machine needs nothing pre-installed beyond Windows itself:

```
dotnet build SimpleWinIRC/SimpleWinIRC.csproj -c Release -p:Platform=x64
```

Output: `SimpleWinIRC/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/` (~207 MB, ~512 files). Copy that folder (drop `*.pdb` if you want to save ~50 KB) onto another x64 Windows 10 1809+ machine and run `SimpleWinIRC.exe` directly — no installer, no runtime install.

The self-contained switches (`WindowsAppSDKSelfContained`, `SelfContained`) are conditional on `Configuration=Release` in the csproj, so Debug builds stay lean for fast dev iteration.
