# WatchParty for Emby

A synchronized watch party plugin for Emby Media Server. Create watch parties that keep everyone in sync — when the host plays, pauses, or seeks, all participants follow along.

## Features

- **Synchronized Playback** — All participants stay in sync with the host's playback position
- **Waiting Room** — Optionally hold playback until a minimum number of participants are ready
- **Auto-Start** — Automatically begin playback when enough participants have joined
- **Playback Controls** — Configurable pause control (host-only or anyone), seek restrictions, and lock seek-ahead
- **Web Dashboard** — External web UI for creating, managing, and joining watch parties
- **SSO Support** — Authentik forward auth integration for the dashboard
- **Chat** — Built-in chat for each watch party
- **STRM Library Integration** — Creates STRM files in a dedicated Emby library so users can join parties by browsing and playing from the Watch Parties library
- **Series Parties** — Keeps an ordered multi-season episode queue in one room and advances everyone when the master naturally completes an episode
- **Nested STRM Resolution** — Resolves local STRM sources (including Xiaoya/115 libraries) so generated party entries point to the actual stream instead of another STRM file
- **Auto-Kick** — Optionally remove inactive participants after a configurable timeout
- **Network Latency Compensation** — Automatic latency measurement and adjustment for remote viewers

## How It Works

1. An admin creates a watch party from the dashboard, selecting content from any Emby library
2. The plugin creates a STRM file in a dedicated "Watch Parties" library in Emby
3. Users browse the Watch Parties library and play the item to join
4. If the waiting room is enabled, playback is paused until enough participants are ready
5. The host controls playback — pause, play, and seek are synced to all participants

For a Series Party, select the episode where the room should start and enable **Create a Series Party**. The plugin queues all regular episodes in season/episode order, keeps the same participants and permissions between episodes, and stores the current episode and position across server restarts.

Client support depends on the Emby remote-control commands that the client implements. A client that can browse and play Emby media but ignores remote pause, seek, or play commands cannot reliably act as a synchronized follower.

## Requirements

- Emby Server 4.8+
- .NET 6.0 runtime
- A dedicated Emby library folder for Watch Party STRM files (e.g., `/mnt/Movies/WatchParty`)

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
| External Server URL | Public URL for the dashboard (e.g., `https://watchparty.example.com`) |
| Emby Server URL | URL of your Emby server for dashboard integration |
| Emby API Key | Required for the dashboard to access Emby libraries and users |
| STRM Target Library | The Emby library where watch party STRM files are created |

The server URL is also used for plugin-to-Emby API calls, so configure the actual internal port when Emby does not listen on the default `8096` (for example, `http://localhost:6908`).

The external dashboard is disabled on first install. Enable it only after configuring authentication and an HTTPS reverse proxy.

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
    reverse_proxy your-emby-server:8097
}
```

## Credits

Forked from [WatchParty-for-Emby](https://github.com/yocksers/WatchParty-for-Emby) by yocksers.

## License

See [LICENSE](LICENSE) for details.
