# WatchParty for Emby

A synchronized watch party plugin for Emby Media Server. Create watch parties that keep everyone in sync — when the host plays, pauses, or seeks, all participants follow along.

## Features

- **Synchronized Playback** — All participants stay in sync with the host's playback position
- **Waiting Room** — Optionally hold playback until a minimum number of participants are ready
- **Auto-Start** — Automatically begin playback when enough participants have joined
- **Master-Authoritative Controls** — Only the master changes room playback; participant pause/resume cannot control other sessions
- **Capability-Aware Control** — Unsupported clients can act as a reporting master without being treated as remotely controllable followers
- **Web Dashboard** — Optional authenticated web UI for creating and managing watch parties
- **SSO Support** — Authentik forward-auth integration when the reverse proxy connects over loopback
- **Original Item Binding** — Controls the existing Emby item directly, preserving its subtitles, media streams, artwork, and metadata
- **Series Parties** — Matches every original episode in an ordered multi-season queue, follows manual episode changes by the master, and advances everyone after natural completion
- **Auto-Kick** — Optionally remove inactive participants after a configurable timeout
- **Session-Safe Recovery** — Multiple devices per account, delayed stop/progress events, missing start events, and episode changes are isolated by session and playback ID; stopped followers remain dormant until they report their own playback activity

## How It Works

1. An admin creates a watch party from the dashboard, selecting content from any Emby library
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

## Configuration

In the Emby plugin settings:

| Setting | Description |
|---|---|
| Enable External Web Server | Enables the watch party dashboard |
| Web Server Port | Port for the dashboard (default: 8097) |
| Listen Address | Interface for the dashboard (default: `127.0.0.1`; use a reverse proxy for remote access) |
| Admin Password | Creates authenticated dashboard sessions for management operations |
| External Server URL | Public URL of Emby used by dashboard playback links (e.g., `https://emby.example.com`) |
| Emby Server URL | URL of your Emby server for dashboard integration |
| Emby API Key | Required for the dashboard to access Emby libraries and users |

The server URL is also used for plugin-to-Emby API calls, so configure the actual internal port when Emby does not listen on the default `8096` (for example, `http://localhost:6908`).

The external dashboard is disabled on first install and binds to loopback by default. Creating or deleting rooms and browsing Emby metadata requires either an admin-password session or trusted SSO forwarded by a loopback reverse proxy. The Emby API key is only used server-side and the proxy exposes a small read-only metadata allowlist.

## Building from Source

```bash
dotnet build WatchPartyForEmby.csproj -c Release
```

The built DLL will be at `bin/Release/net6.0/WatchPartyForEmby.dll`.

## Reverse Proxy Setup (Optional)

If you want to expose the dashboard externally with SSO, configure your reverse proxy to forward auth headers. Example with Caddy and Authentik:

```
watchparty.example.com {
    forward_auth your-authentik-server:9000 {
        uri /outpost.goauthentik.io/auth/caddy
        copy_headers X-Authentik-Username X-Authentik-Groups X-Authentik-Email X-Authentik-Name X-Authentik-Uid
        trusted_proxies private_ranges
    }
    reverse_proxy 127.0.0.1:8097
}
```

For a reverse proxy on another host or container network, use the dashboard admin login instead of trusting forwarded identity headers, or explicitly design a private authenticated network path before changing the listen address.

## Credits

Forked from [WatchParty-for-Emby](https://github.com/yocksers/WatchParty-for-Emby) by yocksers.

## License

See [LICENSE](LICENSE) for details.
