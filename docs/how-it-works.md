# How it works

Notes for anyone reading or changing the code. The [README](../README.md) covers using the plugin.

## The two halves

- **`KaraFunMediaProvider`** handles everything that talks to KaraFun: search, download, sign-in,
  guest requests, and the playback gate.
- **`KaraFunKitRenderer`** (the `IMediaRenderer`) handles playback. It reads a downloaded `.kit`,
  demuxes its Ogg stems into KHost's stream session, and hands them over at whatever levels KHost's
  mix asks for. It makes no mixing decisions, draws nothing and never runs ffmpeg.

KHost does the rest. On a screen that can mix, it plays the stems as they are. For a screen that
can't (a Cast receiver), a key or tempo change, or burned-in lyrics, it encodes one stream from the
same stems. Lyrics come from `KaraFunTimedLyricsProvider`, read from the kit's own timing stream.

The library row is the `.kit` exactly as KaraFun served it, and it's the only file the plugin reads
or writes.

## Search

- **Catalog:** `search/top`, then `song/info` for the full records. Read-only, nothing downloads.
  `search/top` returns a mixed list; only its `<song>` hits are used.
- **Community:** the "Search Community Songs" row re-runs the same words against
  `search/community` → `community/info`. Community songs have **their own id space** (community
  6229 is not catalog 6229), so their rows carry a `community:` prefix, request through
  `community/request`, and download to `community-<id>.kit`. One click is one community search;
  the next normal search is back on the catalog.
- **How sure are we?** The `<community id meta/>` element is confirmed from a real `search/top`
  response. The `community/*` responses haven't been captured yet, so they're parsed leniently and
  nothing is assumed about the stream URL. Treat the first live community download as a test.

## Download and playback

- **Enqueue** downloads the `.kit` (the same file KaraFun's app fetches) and imports it unchanged.
  It plays through KHost's player, not KaraFun's.
- **Playback gate:** a `.kit` answers `IMediaPlaybackGate.Claims` / `IMediaProbe.CanProbe`, and
  nothing else does. KHost asks before playing one and only allows it while signed in. "Signed in"
  means the in-memory session, not a fresh server check, because the gate runs on every load and
  a live show can't wait on the network. If that ever needs tightening, add a `session/info`
  entitlement check in `CanAsync`.

## Sign-in and sessions

- Sign-in prompts go through KHost's `IInteractionDispatcher` (a `TextPromptRequest`), from either
  the plugin's **Sign in / Sign out** button or the "Sign in to KaraFun" search row.
- Stored afterwards: the login and the `pwd` value (md5 of the substituted password, the only form
  `session/open` accepts). The typed password is never written anywhere.
- **Session keys aren't credentials.** They expire on their own and there's no refresh endpoint.
  KaraFun's app answers `INVALID_SESSION` / `action="restart"` with another `session/open`, and so
  does this plugin: same code path as launch, carrying `last_ses` for continuity. Only a credential
  the server actually refuses signs the host out, with a warning naming the reason. A dropped
  connection keeps the session.
- The `pwd` value is password-equivalent and sits as plain JSON in KHost's cache. An OS keychain
  would be better but isn't portably reachable from a plugin.

## Guest requests (KCS)

- Once a session settles, the plugin connects to the `kcs_url` from `session/info`: binary
  Protocol Buffers, one message per frame, `kcpp` subprotocol. The URL is used byte for byte,
  because the token in it is the socket's only identity.
- The message schema follows the field numbers in KaraFun's desktop app, which don't follow
  declaration order.
- A guest's `addToQueue` becomes a singer and a song on KHost's queue, under the singer the request
  names.
- **Phone first, then name.** A request with a known `remoteId` goes to the singer that phone used
  last time, whatever name was typed. Otherwise the name on the request (or the name they joined
  with) decides, and the phone is tied to that singer.
- Ties are **ephemeral** `KHostUser.ForeignKeys`, because a `remoteId` identifies a pairing, not a
  person. Leaving isn't unpairing: phones leave whenever they lock, so ties survive it, and a
  rejoin (or the room's guest list on reconnect/restart) re-ties a guest by `remoteId` + nickname.
  The same `remoteId` with a new nickname is a different person, and the tie moves.
- Ties are cleared on sign-out, when guest requests are turned off, and when the room changes. A
  room is its sign-up link: a restart reuses it, but a session renewal has been seen to replace it.
- Across nights, regulars are matched by name. KaraFun's own app does the same; its singer history
  is keyed on a normalised name with no id.
- **Access:** the protocol advertises permissions but nothing enforces them (KaraFun's app reads
  none), so the room link is the whole access boundary. `remotePickLimit` /
  `remotePickWindowMinutes` is the only brake.
- **Not supported yet:** `player.status` answers idle and `player.queue` answers empty, so guests
  can't see their song land. Remove, reorder and transport commands are refused. Pitch, tempo and
  per-stem options on a request are dropped. See [`TODO.md`](../TODO.md).

## Settings

`KaraFunPlugin` declares `KaraFunSettings` through `IPlugin<TSettings>`, and the provider reads
`IOptionsMonitor<KaraFunSettings>.CurrentValue` on every use, so a saved change applies to the next
search, request or download.
