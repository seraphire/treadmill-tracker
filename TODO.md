# Treadmill Tracker — TODO

A living list of features and polish items. Roughly ordered by priority.
Tick items as we ship them; commit the updated list alongside the change.

## Up next

- [x] Flip window behavior — X closes the app, Minimize button sends to tray
- [x] Sleep / wake handling — finalize active walk on suspend, suppress the
      LostConnection toast for sleep-induced disconnects, auto-reconnect on
      resume with longer retry window (`Microsoft.Win32.SystemEvents`).
- [ ] **Dark theme** — recolor main window + Settings dialog to match the
      toast palette (`#1E1E2E` background, `#3A3A5C` borders, light text on
      dark surfaces). Restyle `GroupBox` / `ListView` / `ListBox` / buttons.
  - **Tooling options to explore before diving in**:
    - **Figma** (web, free) — mock up the dark main window visually, then
      hand screenshots / inspector CSS values to Claude to translate to
      XAML. Best for "I want a custom look but iterate without WPF builds."
    - **Realtime Colors** (https://realtimecolors.com/) — paste in the
      toast palette and see it applied to a sample UI live; great for
      refining the exact shades before committing.
    - **MahApps.Metro** or **Material Design In XAML Toolkit** (NuGet) —
      drop-in modern WPF theming. ~30 min to get a polished dark theme
      across the whole app; trades custom feel for instant cohesion.
      Worth at least *trying* with our existing chibi-character toasts
      to see if the combination clicks before committing to hand-rolled
      styles. If yes, layer custom touches on top.
    - **v0.dev / Galileo AI / UIzard** — AI mockup generators if we want
      design-direction help. Output isn't WPF-native but the visual
      decisions translate.
    - **Blend for Visual Studio** — native WPF style/template editor,
      ships with VS. Good for fine-tuning control templates but Figma
      is friendlier for the broader composition work.
- [x] **Stats window** v1 — opened from `File → Stats…` (Ctrl+T):
      Today (distance/steps progress bars, kcal, walking time, walk count),
      This Week (Mon–Sun bar chart + weekly km total), Streak (current +
      longest), All-time (total walks · km · kcal · time, longest single
      walk, fastest avg pace, first walked date). Light theme for now;
      will pick up the dark theme alongside the rest of the app.
- [ ] **Stats window v2 polish**:
  - Calendar heatmap of the last 30 days, click a day to drill in
  - Walk / Jog / Run breakdown (pie or stacked bar)
  - Personal Records section (longest week, biggest single day, etc.)
  - Apply dark theme when the rest of the app converts

## Soon

- [ ] **Challenge Tracker** for The Conqueror Mandalorian (and future events)
  - Settings → Goals adds `Challenge Name` + `Target Km` fields
  - Stats window gets a Challenge panel with big progress bar, total km
    vs target, ETA based on last-30-days pace
  - Milestone toasts (25 / 50 / 75 / 100 %) using `winning.png`

- [ ] **Pull walks from Strava** — re-OAuth to add `activity:read`
      scope alongside the existing `activity:write`, then on every app
      launch (and on demand from a Settings button) fetch recent
      Walk / Run / Hike activities and merge them into local session
      history. Two vacation-prep use cases this unlocks:
  - **Streak survival on the road** — walks logged via the Strava
    mobile app on vacation count toward the daily streak. Sessions
    imported from Strava arrive with totals only (no live speed
    samples), so the existing `ClassifyWalk` runs on the activity's
    average speed.
  - **Healing false-negative uploads** — when our app shows
    *"Couldn't Upload"* but the activity actually made it (timeout /
    dropped-connection case), the next-launch pull recovers the real
    activity ID, updates the local record's `StravaActivityId`, and
    the *"view on Strava"* link comes back.
  - Match local ↔ Strava by `start_date_local` within a ~5 min
    window. Strava-only activities (no local match) become new
    local session records. Out of scope for v1: bidirectional edits,
    deleting from our app, conflict resolution.
  - Settings → Strava gets a *"Reconnect (additional permissions)"*
    button for users who already authorized with write-only scope.

- [ ] **Travel mode** — explicit "I'm away" status, distinct from a
      Streak Freeze. Two interaction patterns, **both required**:
  - **Forward-looking**: before a trip, set a start + end date in
    Settings → Goals. While inside the range, the streak is preserved
    without requiring a walk; the daily-totals display reads
    *"On vacation"* instead of counting toward the goal.
  - **Retroactive**: flag past dates as vacation *after the fact*,
    for the realistic case of *"got home, realized I forgot to set it,
    streak is now broken."* Surfaced (a) in Settings → Goals as a
    list of ranges with add / edit / delete, and (b) once the Stats
    calendar heatmap lands, right-click → *"Mark as vacation"* on
    any missed day or range. Retroactive flags must heal the streak
    counter and the daily totals view immediately.
  - Edge cases worth noting in design: overlapping ranges merge;
    real walks logged during a vacation range still count toward
    stats and Strava (vacation just suppresses the *requirement*);
    ranges may extend into the future. Storage: list of `{Start,
    End, Note}` in `flags.json`, or a dedicated `vacations.json` if
    it grows.
  - *(Inspired by Fito's Streak Shield and Gentler Streak's rest
    status — neither supports retroactive flagging, which is the
    feature that makes this actually useful.)*

- [ ] **Treadmill remote control** (opt-in per connection)
  - Discovery: probe for FTMS Control Point characteristic (`0x2AD9`)
    on connect, log whether it's supported. If not present, hide the
    feature entirely on this connection.
  - **Opt-in model**: do not auto-enable. After connecting to a
    supporting treadmill, show an "Enable Remote Control" button
    somewhere prominent. Clicking it sends FTMS `Request Control` (op
    `0x00`) and reveals the control panel. Opt-in resets every
    connection — no persisted preference, every session is a deliberate
    choice (so the belt can't surprise you on the next launch).
  - Control panel UI (hidden until opt-in):
    - **▼ Slower** / **▲ Faster** — adjust target speed by 0.1 mph
      (≈ 16 units of 0.01 km/h via FTMS Set Target Speed `0x02`).
    - **▶ Start** — FTMS `0x07`. Visually distinct (green) — clicking
      starts the belt physically. No confirm dialog (annoying), just
      treat the button as the deliberate action.
    - **⏸ Pause** — FTMS `0x08 0x02`.
    - **⏹ Stop** — FTMS `0x08 0x01`.
  - State tracking:
    - Buttons disabled when not connected
    - Speed buttons disabled when belt is stopped (no speed to adjust)
    - Start disabled when belt is already running, etc.
  - Failure handling: if `Request Control` is rejected (some devices
    require physical button-press first), show a toast and don't
    reveal the panel — user can retry by clicking the opt-in button
    again.

## Maybe later

- [ ] **Custom chrome** (borderless main window with handcrafted title-bar
      buttons). Only if the default Windows chrome still looks wrong after
      the dark theme lands.
- [ ] **Smarter daily nag** — afternoon prompt if no walk has been logged
      by, say, 4 pm; current logic only fires once at app launch.
- [ ] **Activity edit / delete** in the Today's Activity list — right-click
      menu to remove a misclassified walk locally (and optionally delete
      from Strava if `activity:write` was used to upload it).

## Backlog / brainstorm

Ideas surfaced from looking at adjacent fitness apps (Strava, Garmin
Connect, Peloton, iFit, Couch-to-5k, RunGo, Fitbit, Duolingo's streak
mechanics). Not committed — promote to "Up next / Soon / Maybe later"
when one feels right.

### Audio / music integration

- [ ] **Music play/pause sync** — when a walk starts, resume the user's
      current Spotify / Windows-media playback; pause it when the walk
      pauses; resume on resume; pause again on session end. *(Like
      Peloton, iFit, Spotify Running.)*
- [ ] **Voice cues at milestones** — TTS or pre-recorded *"One
      kilometer!"*, *"Halfway there!"*, *"30 minutes!"* at user-set
      intervals. *(Like RunGo, C25K apps.)*
- [ ] **Goal-hit chime** — short distinctive sound alongside the
      Winning toast when a daily goal is crossed. Configurable / mutable.

### Engagement & gamification

- [ ] **Personal records** in the Stats window — longest single walk,
      fastest avg pace, most steps in a day, longest streak, biggest
      week. *(Like Strava PR lists, Garmin "records".)*
- [ ] **Achievement / badge system** — milestones like 10/100/1000 km
      total, first jog, first hour-long walk, etc. *(Like Apple
      Fitness rings, Garmin badges.)*
- [ ] **Streak freeze** — let a missed day not break the streak (one
      "skip" per N days). *(Like Duolingo.)*
- [ ] **Weekly + monthly goals** alongside the existing daily goal,
      each with its own progress display. *(Like Apple Fitness, Garmin.)*

### Workout programs

- [ ] **Saved interval workouts** — e.g. "1 min fast / 1 min slow ×10".
      Fires audio cues as it runs. Far more interesting once Treadmill
      Remote Control lands so the program can drive the speed itself.
      *(Like iFit, Peloton intervals, fartlek timers.)*
- [ ] **Couch-to-5k-style programs** — multi-week structured
      progression with audio coaching. *(Like C25K, 5K Runner.)*
- [ ] **Warm-up / cool-down timers** — gentle ramp at session start
      and a recommended slow-down at the end of a long walk.

### Hardware integrations

- [ ] **BLE heart rate strap support** — Polar H10, Wahoo TICKR, generic
      HRMs. Subscribe to Heart Rate Service (`0x180D`), show live BPM
      in the metrics tile, compute time-in-zone, push HR samples in
      the Strava upload. Unlocks zone breakdowns and VO2max estimates.
- [ ] **Multiple treadmill profiles** — if you ever use a different
      machine (gym / travel / second home), each saved with its own
      name and stride calibration. Currently there's only one
      "last device".

### Data portability

- [ ] **Export session history** — CSV / JSON / TCX. Useful for moving
      to a new computer or running your own analysis.
- [ ] **Import sessions** — restore from a previously-exported file.
- [ ] **Manual session entry** — log a walk that happened off-app (a
      different treadmill, an outdoor walk). Counts toward streak /
      totals locally; optional Strava push.

### Treadmill care

- [ ] **Belt mileage tracker** — accumulate total distance per
      treadmill profile. Reminder toast every X km recommending
      lubrication / maintenance. *(Like NordicTrack's iFit
      maintenance section.)*

### Polish / UX

- [ ] **Distance unit preference** — global km/miles toggle. The
      treadmill displays mph; you currently log km. Pick one or let
      the user switch.
- [ ] **Time format** — 12h vs 24h for session timestamps.
- [ ] **Live pace graph** — small line chart of speed over time during
      the active walk, in the metrics panel.
- [ ] **Keyboard shortcuts** — Ctrl+Q exit, Ctrl+, settings, Ctrl+Shift+S
      stats. Surfaced in the menu via `InputGestureText`.
- [ ] **About dialog improvements** — version number, GitHub repo link,
      license, "check for updates" stub.
- [ ] **First-run welcome / tutorial** — first launch with no saved
      device walks the user through scan → connect → (later) Strava.
      Could reuse `firstrun.png`.
- [ ] **Stats screenshot share** — "Share this week's totals" generates
      a card image of the weekly stats panel for social posting.
      *(Like Strava's share card.)*

### Mobile / phone

Two distinct shapes — pick one, or do both eventually.

- [ ] **Companion phone app** — phone is a small dashboard / remote for
      the desktop app. Sees the live walk metrics, current goals,
      streak, daily totals, recent toasts; can trigger remote-control
      buttons (once that lands) without leaning over to the laptop.
      Requires a small local-network API on the desktop side (e.g. a
      lightweight HTTP+SSE listener on a localhost-or-LAN port,
      mDNS-discoverable). Pairing model: scan a QR code shown by the
      desktop app, phone remembers the desktop's address. Probably
      the smallest-effort path because the desktop app stays the
      source of truth and the phone is a thin client. *(Like Peloton's
      paired phone screen, NordicTrack iFit on a tablet alongside the
      treadmill.)*

- [ ] **Standalone phone app** — phone connects directly to the
      treadmill over BLE and does everything the desktop does, on its
      own. Best for "I left my laptop in the other room" or "I'm
      traveling with just my phone". Big effort: would mean either
      porting to **.NET MAUI** (shared codebase with the WPF app —
      most code-reuse, but mobile UX needs hand-tuning), **Flutter**
      (different language, better mobile-native feel), or two native
      apps (Swift / Kotlin — most polished, least reuse). Same Strava
      account on both means walks dedupe naturally on Strava's side.

- [ ] *(Cheap interim)* — **Strava already provides mobile**. Your
      walks land on Strava and Strava's app shows them on the phone,
      including The Conqueror integration. That covers post-walk
      review with zero code from us. Things Strava doesn't show:
      live-during-walk metrics, our streak counter, daily-goal
      progress, future Conqueror challenge tracker — that's the gap a
      first-party mobile companion would fill.

### Reminders / smart prompts

- [ ] **Scheduled walk reminders** — *"remind me at 9 am Mon / Wed / Fri"*.
      Native Windows notification + in-app toast. *(Like Apple Fitness
      reminders.)*
- [ ] **Pattern-based nudges** — if you usually walk 7–9 am and it's
      8:30 am with no walk yet, prompt. Extends the existing
      ghost-toast logic. *(Like Fitbit's "you usually walk now"
      notices.)*

## Tech debt / verification

- [ ] Confirm watchdog quieting works in practice — should not see
      `Vendor notifications silent — re-subscribing...` spam during idle
      after the latest build (commit fixing `_lastVendorDataTime` updates).
- [ ] Confirm Strava 90 s timeout catches the slow-upload false-negative
      case (the 1.89 km walk that succeeded but showed "Couldn't Upload").
- [ ] Confirm retry-sweep "Confirmed on Strava" toast renders correctly
      on the next launch when a sentinel-id session is reconciled via 409.
- [ ] Verify sleep / wake flow end-to-end:
      - Active walk in progress → close laptop lid → walk is saved locally
      - Open lid → "Welcome back!" toast → reconnect succeeds within ~30 s
      - On next launch, Strava upload retry sweep picks up the
        sleep-finalized walk
      - No spurious "Connection Lost" toast appears on the way to sleep
