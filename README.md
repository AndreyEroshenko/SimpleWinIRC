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

Open the solution in Visual Studio 2022 (with the Windows App SDK workload) or run:

```
dotnet build SimpleWinIRC/SimpleWinIRC.csproj
```

The project is an unpackaged WinUI 3 desktop app targeting `net8.0-windows10.0.19041.0`.
