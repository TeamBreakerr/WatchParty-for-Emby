# Watch Party rollback point: pause-sync

- Created: 2026-08-19
- Plugin DLL: `WatchPartyForEmby.dll`
- DLL SHA-256: `8ef94d0e38bf7fba2bc6f64986f9e3a8b3bd5d71eeef6a09cefbb6c1e9b854f0`
- Remote path: `/opt/xiaoya_emby/config/plugins/WatchPartyForEmby.dll`
- Test result: 287 passed, 0 failed
- Build: Release `net6.0`

This point includes reconnect reconciliation, participant pause broadcasting,
authoritative pause/play clock updates, and resume-position reconciliation.
The remote server also retains the previous DLL in its plugin backups.
