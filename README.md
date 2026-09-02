# WatchParty for Emby

> Current production-stable engineering archive and rollback instructions:
> [`docs/operations/watchparty-stable-20260902.md`](docs/operations/watchparty-stable-20260902.md)

A synchronized watch party plugin for Emby Media Server. Create watch parties that keep everyone in sync — when the host plays, pauses, or seeks, all participants follow along.

## Features

- **Synchronized Playback** — All participants stay in sync with the host's playback position
- **Waiting Room** — Optionally hold playback until a minimum number of participants are ready
- **Auto-Start** — Automatically begin playback when enough participants have joined
- **Master-Authoritative Controls** — Only the master changes room playback; participant pause/resume cannot control other sessions
- **Capability-Aware Control** — Unsupported clients can act as a reporting master without being treated as remotely controllable followers
- **Explicit One-Click Launch** — Select any live, remotely controllable Emby Session (including an idle Web master) and start the room's exact media version
- **Concrete Version Visibility** — Media-source choices show the exact server path before a room is created
- **Homepage Dashboard Shortcut** — A self-hosted Homepage card can link directly to the configuration page
- **Embedded Control Center** — Manage rooms, inspect live Sessions, and launch selected clients from Emby's authenticated plugin page
- **Original Item Binding** — Controls the existing Emby item directly, preserving its subtitles, media streams, artwork, and metadata
- **Series Parties** — Matches every original episode in an ordered multi-season queue, follows manual episode changes by the master, and advances everyone after natural completion
- **Episode Selector** — Change the current queued episode from the room overview; a playing room sends the exact episode to its online controllable sessions, while a paused or waiting room only updates the next selection
- **Auto-Kick** — Optionally remove inactive participants after a configurable timeout
- **Session-Safe Recovery** — Multiple devices per account, delayed stop/progress events, missing start events, and episode changes are isolated by session and playback ID; stopped followers remain dormant until they report their own playback activity

## How It Works

1. An admin creates a watch party from the embedded Emby control center, selecting content from recent playback or unified search
2. The room stores the original Emby item ID; no media file, directory, or STRM entry is created
3. Users play the original movie or episode from its existing library to join
4. If the waiting room is enabled, playback is paused until enough participants are ready
5. The host controls playback — episode changes, pause, play, and seek are synced to all participants

For a Series Party, select the episode where the room should start and enable **Create a Series Party**. The plugin queues all regular episodes in season/episode order, keeps the same participants and permissions between episodes, and stores the current episode and position across server restarts.

Client support depends on the Emby remote-control commands that the client implements. A client that can browse and play Emby media but ignores remote pause, seek, or play commands cannot reliably act as a synchronized follower.

## Requirements

- Emby Server 4.8+
- .NET 6.0 runtime

## Installation

1. Download `WatchPartyForEmby.dll` from the [Releases](https://github.com/jeremiahbeasley/WatchParty-for-Emby/releases) page
2. Place the DLL in your Emby plugins directory (e.g., `/var/lib/emby/plugins/`)
3. Restart Emby Server
4. Go to Emby Dashboard > Plugins > Watch Party to configure

For a self-hosted [Homepage](https://gethomepage.dev/) dashboard, add a normal
service card whose `href` points to the plugin route:

```yaml
- 影音媒体 (Media):
    - 一起看:
        icon: mdi-account-multiple
        href: https://emby.example.com/web/index.html#!/configurationpage?name=watchpartyconfig
        description: 同步观影配置与控制
```

## Configuration

The embedded Emby page is the only control center. It uses the current authenticated
Emby account and does not start a second HTTP listener or require a server API key.
Use it to select an exact media source, create rooms, inspect participants and online
Sessions, and send one-click PlayNow commands to explicitly selected clients.

## Building from Source

```bash
dotnet build WatchPartyForEmby.csproj -c Release
```

The built DLL will be at `bin/Release/net6.0/WatchPartyForEmby.dll`.

## Credits

Forked from [WatchParty-for-Emby](https://github.com/yocksers/WatchParty-for-Emby) by yocksers.

## License

See [LICENSE](LICENSE) for details.
