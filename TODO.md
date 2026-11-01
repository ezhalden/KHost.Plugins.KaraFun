# TODO

## Needs a real KaraFun account — nothing here has touched the live API

- [ ] Smoke-test sign-in: search for anything, click Sign in on the row that appears, enter the
      venue's KaraFun email/password, confirm the dialog closes and a second search returns songs
      rather than the sign-in row again.
- [ ] Smoke-test search: type a query in Song Search, confirm titles/artists/durations look right
      (not just that something came back — `song/info`'s exact XML shape was confirmed from two
      live-captured samples, not the full catalog).
- [ ] Smoke-test enqueue: pick a result, confirm the Downloads page shows progress, and on a Cast
      receiver confirm the host's burned-in stream plays and looks right (lyric timing/colouring,
      audio stems). The host paints the words from this plugin's `TimedLyrics` now; the kit's own
      cover art is no longer drawn behind them.

## Known gaps, called out rather than guessed at

- [ ] **The `community/*` endpoints were never captured on the wire.** `community/info` and
      `community/request` are read from the binary, and nothing states that a
      live community stream URL was captured. What IS live-confirmed is only the
      `<community id meta/>` search-hit element, from a captured `search/top` list. So the client
      parses those responses leniently — `community/info` accepts `<community>` or `<song>`
      records, `community/request` looks for its `<stream>`/`<md5>` under either wrapper or the
      root — and assumes nothing about the stream URL's shape. Capture a real `search/community`,
      `community/info` and `community/request` against an account that has community enabled, then
      tighten the parsing to what actually comes back. Until then, treat community search and
      enqueue as the least-verified path in the plugin.
- [ ] **Community songs may be plan-gated.** The app carries its own error strings
      `minimumPlanNotReachedToAddCommunitySongToQueue` / `…ToPlayCommunitySong`, so an account
      below some tier probably gets an error rather than a download. Nothing here recognises those
      codes yet — a gated account will surface as a failed import with no explanation.
- [ ] **No "signup events from KaraFun's server."** No documented webhook/callback/push API for
      account signups was ever found — only the in-app REST/WebSocket surfaces
      the desktop client itself uses. If KaraFun exposes something for business partners, it needs
      its own reverse-engineering pass before this
      plugin can react to it.

- [x] **A guest can sign up and queue a song.** Walked end to end against a real phone on
      2026-09-14: guest joined, two catalog picks arrived, both landed on one singer's queue with
      the right titles and an encoded `.khv` each. Three things had to be true at once, and the
      first two were the ones missing.

      **`player.status` and `player.queue` must be answered.** The phone asks for both the moment
      it connects, before it draws anything, and it asks for neither permission nor capacity
      first. A `NOT_SUPPORTED` there does not read as "you may not" — it reads as *there is no
      player in this room*, and a guest cannot add to a room with nothing to add to. This was the
      whole blocker; permissions were a red herring until it was fixed. `status` answers `IDLE`,
      `queue` answers an empty queue.

      **`remote.getPermissions` must grant `addToQueue`**, and `shownTypes.karaoke` with it.
      Everything else is granted false because everything else is still genuinely refused —
      advertising one paints a control on the phone that errors when pressed. `viewQueue` is off
      because `queue` is empty; `community` is off to agree with `socket.getCapacities`, which
      answers `canHandleCommunityAddToQueue` off. The two must not contradict each other on one
      socket, and this plugin *can* encode a community song, so that pair is worth revisiting
      together rather than one at a time.

      `getPreferences` answers `askOptions` off, so a pick arrives under the nickname the guest
      joined with — which is the singer KHost puts on the queue. `getConfiguration` answers a
      degenerate pitch/tempo range, since KHost carries neither per performance; the *steps* stay
      1, because a zero step is a divisor on the phone's side.

      Every request this host still refuses is now logged by domain and type. That line is what
      found `player.status`, and it is the first thing to read when a control is missing from a
      phone.

- [x] **The code on screen means the room is live.** Its lifetime is now exactly the socket's
      authenticated lifetime: minting happens on `socket.authenticatedEvent` — the one moment this
      host knows something is listening — and the socket reports every pass it spends not
      connected, which takes the code down. A code that outlives its connection pairs a phone to a
      channel nothing is reading, and the guest gets a page saying the session is closed.

      Two races are guarded rather than tolerated, because both leave a *stable* wrong state that
      nothing later corrects. Minting is a REST round trip, so the socket can drop during one: the
      connection is re-read under the screen-code lock before drawing, and a room epoch is bumped
      on every disconnect so a mint that outlived its room discards itself. The hide reaches the
      screens *inside* that lock too — releasing first lets a mint draw in the gap and set the
      flag, and the hide then wipes what it drew while the flag still says a code is up, which is
      a room that never mints again.

- [ ] **A guest still cannot see their song land.** `player.queue` answers an *empty* queue, which
      is what unblocked adding but is not true: the guest's own pick vanishes from their view, and
      the risk is a guest re-adding out of confusion. KaraFun's own host pushes the whole queue on
      every change. Answering it properly means mapping KHost's queue onto `Player_Models_Queue`,
      which needs a KaraFun song id per row — rows this plugin imported have one, and a row from
      anywhere else has none. Decide what those foreign rows look like to a guest before building
      it. Pushing `player.Event.queue` on change is the other half.

- [ ] **A guest can only add.** `removeFromQueue`, `moveQueue`, `setQueueItemOptions` and the
      transport controls are all refused. Removing needs the same id mapping as above.

- [ ] **A pick's options are dropped.** `pitch`, `tempo`, per-stem `tracks` and `mod` come in on
      `SongOptions` and are read only for `singer`. Nothing in KHost carries a per-performance
      pitch or tempo yet, so this is a host change first.

- [ ] **Capacities are reported off**, exactly as the shipped desktop reports them
      (`canHandleCommunityAddToQueue`, `canHandleQuizAddToQueue`,
      `allSingerRotationGroupsQueueForAdmins`). This plugin *can* download a community song, so
      claiming that one may well be right — but it is a negotiation nobody here has watched
      succeed, and a live room is the wrong place to find out. Capture it before changing it.

- [ ] **Permissions are advertised and never enforced**, by KaraFun's own host as much as this
      one: the desktop reads none of them, and `ExecutorError.forbidden` has no call sites. Anything
      on the channel may queue. The code on the screen is the whole access boundary, so the only
      answer to a room being spammed is taking the code down. Worth a host-side rate limit before
      this is used in anger.

- [ ] **A guest is recognised only within one room.** The `remoteId` a phone carries is stored
      ephemerally and kept through a guest leaving and a restart into the same room (its sign-up
      link); it is cleared when the room is new, when the account signs out, or when the venue
      turns the guest remote off —
      so a regular who comes in every Friday is still matched by name across nights, with all the
      duplicate risk that carries. Whether either KaraFun id is durable is genuinely unrecorded;
      settling it needs a live capture of the same guest reloading the web remote. What *is* now
      confirmed (2026-09-14) is that the id holds across picks within one pairing: a second pick
      from the same phone matched the singer the first created rather than adding a duplicate. Note that KaraFun's own host does not try: its singer history keys on
      a normalised name string and holds no id at all.

- [x] **`RemoteQueueChannel` has seen real frames.** 2026-09-14, against the live server: the
      transcribed schema decodes `socket.authenticatedEvent`, `remote.userJoined`,
      `player.status`, `player.queue` and `player.addToQueue` correctly, and the responses this
      host encodes are accepted. The field numbers taken from the desktop's own maps were right.

## Smaller things

- [ ] A kit's text-overlay effects (floating decorative lyrics like Wolf Like Me's "oh oh oh") are
      not mapped into `TimedLyrics`, so neither the screen nor the host's burned-in words show
      them. Revisit if a host would notice.
- [ ] `KaraFunMediaProvider`'s search/download path is only tested against a fake `HttpMessageHandler`
      with hand-written XML fixtures (two real shapes: `search/song` and `song/info`, both taken
      from live-captured samples). Nothing exercises pagination,
      multi-page search, or the various KaraFun error codes (`INVALID_SESSION`, `WRONG_PASSWORD`,
      etc.) beyond the one `WRONG_PASSWORD`-shaped test.
- [ ] **`device_id` is still not sent.** The app's `session/open` parameter list carries one
      ("registered device identifier"), and it is what device-slot enforcement keys on — a server
      at the account's cap answers with a `missingDevice` payload listing what is registered.
      Deliberately left alone: the format is undocumented, `session/open` works without it, and
      inventing an id risks burning a slot on a real account. Worth doing once its shape is known,
      and it is a candidate explanation if sessions keep dying after the renewal work.

- [ ] **Renewal is only triggered once a launch.** `_validated` latches, so a session that dies
      mid-shift is not noticed until something asks — the same shape as the old bug, one level up.
      A `session/info` that answers `INVALID_SESSION` during a search should renew inline rather
      than surfacing the error; right now only the once-per-launch check does.
