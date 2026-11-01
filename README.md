# KHost.Plugins.KaraFun

KaraFun media provider for KHost. Searches a host's own KaraFun catalog from the
console's Song Search panel, downloads the chosen track, and enqueues it for the selected singer.

> **A kit is handed over as stems; the host does the rest.** The library row is the downloaded
> `.kit`, exactly as KaraFun served it, and it is the only file this plugin reads or writes. Its
> renderer (`KaraFunKitRenderer`, the `IMediaRenderer`) demuxes the kit's Ogg stems into the
> host's stream session and answers with them — for every display, every key and every tempo,
> each at the level the host's mix asked for. It makes no mixing decision: KHost plays the stems
> as they are on a display that mixes, and encodes one stream from those same stems for a display
> that cannot (a Cast receiver), for a key or tempo change, and for a display that wants the words
> burned in. The words come from this plugin's `ITimedLyricsProvider`, read out of the kit's own
> timing stream; a screen draws them itself and the host paints them into its encode for a display
> that cannot. The plugin paints nothing and runs no ffmpeg. The service side — search, download,
> sign-in, the guest remote and the playback gate — is `KaraFunMediaProvider`.

## What it does, and what it deliberately does not

- **Search** calls `search/top` then `song/info` for full records — read-only, no download. The
  `top` section answers with a mixed list; only its `<song>` hits are taken.
- **Community search** is offered as a last row, "Search Community Songs", under every catalog
  search. Clicking it re-runs the same words against `search/community` → `community/info`
  — KaraFun's user-uploaded karaoke, which is a **separate id space**: community id
  6229 is a different track from catalog id 6229, so community rows carry a `community:` prefix,
  request through `community/request`, and download to their own `community-<id>.kit` file. One
  click buys one community search; the next plain search goes back to the catalog.
  **Confidence:** the `<community id meta/>` result element is live-confirmed (it appears in a
  captured `search/top` list), but the `community/*` endpoint family is read from the binary and
  was never captured on the wire. The client therefore parses
  those responses leniently and assumes nothing about the community stream URL's shape. Treat the
  first live community search and download as a test, not a formality.
- **Enqueue** downloads the `.kit` (the same CDN object the app itself fetches) and imports it
  as the library row, unchanged. At play time its stems go to KHost as they are, and KHost asks
  this plugin for the timing. That plays the track through KHost's own player rather than KaraFun's, which is a
  deliberate choice made with the tradeoff understood: KaraFun's own subscription/device-slot
  enforcement governs the app, not this path.
- **Playback is gated on a live account.** A `.kit` answers
  `IMediaPlaybackGate.Claims`/`IMediaProbe.CanProbe`, and nothing else does. KHost asks this
  plugin before playing one and allows it only while signed in —
  signing out mid-show flashes "Sign in to KaraFun to play this track." "Valid account" here means
  the in-memory session, not a fresh server check: the gate runs on every load and a live show
  cannot wait on the network. Tighten it to a `session/info` entitlement check in `CanAsync` if a
  stale session is ever worth the round trip.
- **The account is remembered, and the session renews itself.** Sign in from either the Plugins
  page — the plugin's row carries a single **Sign in / Sign out** button that toggles with the
  session — or the "Sign in to KaraFun" row that search shows until a session exists. Both prompt
  for an email/password through KHost's `IInteractionDispatcher` (a `TextPromptRequest`).
  What is kept afterwards is the login and the `pwd` value — md5 of the substituted password, which
  is the only form `session/open` is ever given. The typed password is never written anywhere.
  **A session key is not a credential.** KaraFun's expire on their own, nothing renews them, and
  there is no refresh endpoint; the real client's answer to `INVALID_SESSION` / `action="restart"`
  is another `session/open`, built by the same code that opens one at launch. So this does the
  same: a dead key is re-opened from what is stored, carrying `last_ses` for continuity, and only a
  credential the server actually *refuses* signs the host out — with a warning naming the reason.
  A dropped connection keeps the session rather than making a venue sign in because a router
  blinked.
  **Be clear about what that stores.** The `pwd` hash is password-*equivalent*: anything holding it
  can open sessions on the account. It is kept in KHost's cache rather than in plugin settings, so
  it is off the Plugins page and out of `plugins.json`, but it is plaintext JSON on the venue's
  disk. An OS keychain would be better and is not portably reachable from a plugin. The alternative
  is prompting every launch, which a venue whose first singer arrives through the remote cannot
  use — nobody is standing at the console to answer it. Signing out drops the credential with the
  session. A host with no KaraFun account yet signs up on KaraFun's own site; the plugin offers no
  in-app path to that.
- **The room's picks arrive over KaraFun's Control System socket (KCS).** Once a session settles
  the plugin connects to the `kcs_url` that `session/info` hands back — binary Protocol Buffers,
  one message per frame, `kcpp` subprotocol, the URL used byte for byte because the token inside it
  is the socket's only identity. A guest's `addToQueue` becomes a singer and a song on KHost's
  queue, under the singer it names — never under whoever the console has selected.
  **The phone is asked before the name.** A pick carrying a `remoteId` KHost already knows goes
  straight to the singer it named last time, whatever was typed this time; only on a miss does the
  name on the pick (or failing that the name they joined the room under) decide, and the phone is
  then tied to whoever that turned out to be so their next pick skips the name entirely. Those ties
  are **ephemeral** `KHostUser.ForeignKeys`, because a KaraFun `remoteId` names a pairing rather
  than a person. **Leaving is not unpairing**: a phone leaves whenever it locks, so a tie survives
  the guest leaving, and a join (or the room's guest list, on a reconnect or a restart) ties a
  returning guest back by `remoteId` plus nickname without waiting for a pick. The same `remoteId`
  under a different nickname is somebody else, and the tie moves. The plugin clears its own ties when the account signs out, when the venue turns the guest
  remote off, and when the room is new: a room is its sign-up link, which a restart reuses and a
  session renewal has been seen to replace. Across nights a regular is matched by name again — which is all
  KaraFun's own host does, its singer history keying on a normalised name and holding no id at all.
  The schema is transcribed from the shipped desktop build's own field
  numbers, none of which follow declaration order.
  **Limits:** a guest can only add. `player.status` answers idle and `player.queue` an empty
  queue, so a guest cannot see their song land, and removing, reordering and transport controls
  are refused. A pick's pitch, tempo and per-stem options are dropped. All are logged in `TODO.md`.
- **Anything on the channel may queue.** The protocol's permissions are advertised and never
  enforced — KaraFun's own desktop reads none of them — so the code on the screen is the whole
  access boundary, and taking it down is the only way to stop a room. The one brake is a
  per-guest rate limit (the `remotePickLimit` and `remotePickWindowMinutes` settings).
- **No "signup events from KaraFun's server."** There is no webhook, callback or push API for
  being told when someone completes a signup, only the in-app REST/WebSocket surfaces the desktop
  client itself uses. Nothing here polls or listens for one.

## Building

The contracts come from the `KHost.Abstractions` and `KHost.Common` packages (0.58.0), which are not
on nuget.org. Build them into a local feed with KHost's `./build/pack-contracts.sh` and register it
once with `dotnet nuget add source ~/.nuget/khost-local -n khost-local`; after that no checkout of
KHost is needed to build:

```bash
dotnet build KHost.Plugins.KaraFun.slnx
dotnet test tests/KHost.Plugins.KaraFun.Tests
```

## Installing

From a host: Plugins, then Available, install KaraFun Integration, and restart KHost. It is one
portable build that runs on Windows, macOS and Linux.

By hand: unzip a release (or copy a local build's output: entry dll, `manifest.json` and dependency
dlls) into its own folder under KHost's `plugins/` directory, enable it on KHost's Plugins page, and
restart KHost. There is nothing to configure before first use; searching prompts for the KaraFun
account to sign in with.

## Settings

| Key | What it's for |
|---|---|
| `remotePickLimit` | Song requests one guest may send per window; default 5, 0 for unlimited. |
| `remotePickWindowMinutes` | The window `remotePickLimit` counts over; default 10. |
| `singleUse` | On by default. A downloaded song's file is deleted once it has been sung and nothing queued still needs it, and is downloaded again the next time the song is queued (needs a signed-in account). |

Settings are read live: the plugin entry point names `KaraFunSettings` through `IPlugin<TSettings>`, and the
provider reads `IOptionsMonitor<KaraFunSettings>.CurrentValue` at each use, so a save applies to the next
search, pick or download with no restart.

The KaraFun login itself is deliberately not a setting — see "What it does" above.
