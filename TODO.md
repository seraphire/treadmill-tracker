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
- [ ] **Stats window** opened from `File → Stats…`, dark-themed to match:
  - Today panel — distance/steps progress bars (color shift at goal), kcal,
    walking time, walk count
  - This Week panel — Mon–Sun mini bar chart + weekly km total
  - Streak panel — current + longest streak, fire icon
  - All-time panel — total walks · km · kcal · time, longest single walk,
    fastest avg pace, first walked date
  - *(v2)* Calendar heatmap of the last 30 days, click a day to drill in
  - *(v2)* Walk / Jog / Run breakdown (pie or stacked bar)

## Soon

- [ ] **Challenge Tracker** for The Conqueror Mandalorian (and future events)
  - Settings → Goals adds `Challenge Name` + `Target Km` fields
  - Stats window gets a Challenge panel with big progress bar, total km
    vs target, ETA based on last-30-days pace
  - Milestone toasts (25 / 50 / 75 / 100 %) using `winning.png`

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
- [ ] **`activity:read` Strava scope** — re-OAuth required. Lets us verify
      timeout-induced false-negatives by querying the activity directly,
      and pull historical data for streak / Conqueror cross-validation.
- [ ] **Strava-inclusive streak** — count outdoor walks/runs/rides from
      the Strava feed, not only sessions logged through this app.
- [ ] **Smarter daily nag** — afternoon prompt if no walk has been logged
      by, say, 4 pm; current logic only fires once at app launch.
- [ ] **Activity edit / delete** in the Today's Activity list — right-click
      menu to remove a misclassified walk locally (and optionally delete
      from Strava if `activity:write` was used to upload it).

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
