# Internals

Engineering notes : what the mod attaches to in the game, why each choice was made, and the
in-game pass to run before a release. For what the mod does and how to install it, see the
top-level README.

The mod is placement-only. It mirrors the current board into the game's own simulation, draws
hover arrows and an optional whole-board preview, and removes every visual before the fight is on
screen. The live battle is read by the self-check and by nothing else. No Harmony patch, no game
state written.

## Build and install

```powershell
dotnet build src/GuildrunTargetingMod.csproj -c Release
```

Output is `src/bin/Release/GuildrunTargetingMod.dll`. Installing is deliberately manual : the
project never copies itself into `Mods/`. Rebuild against regenerated `MelonLoader/Il2CppAssemblies`
after every game update. `packaging/package.ps1` builds both release zips.

## What the player sees

- **One picture, one moment.** Everything drawn describes the instant every unit has arrived at
  the hex it will fight from. Everything before that instant is fully simulated, including
  damage, but a unit that would die is made damage-immune at that instant and survives. The
  opening picks exist only as evidence for the self-check and must never reach the renderer.

  Hover used to take its arrows from the opening picks while drawing the ghosts where units end
  up, which composed a board the fight never passes through : a unit standing at the hex it
  finishes on, aiming at the enemy it only chose on the first frame. A hero picks again on every
  hex it crosses and an enemy does not, so a walking hero routinely abandons that first choice
  before arriving. Across 280 boards and 840 hero units, 32% ended up fighting someone other than
  their first pick, and 14% did so with no tie anywhere in the comparison to excuse it. The
  preview toggle had been right the whole time, which is what made the two modes disagree.

### Why the preview prevents deaths

A death is not just one unit disappearing. It frees the dead unit's hex, removes everything that
unit contributed to its team, and makes every unit targeting it acquire another target. Showing
that death would reveal information the player is not meant to have and would change the
positions and pairings the preview exists to show. Stopping before the lethal tick would omit
real movement and damage that happened before arrival, while letting the death stand would leak
the future. The playout instead replays the lethal tick with that unit damage-immune, then keeps
simulating until everyone has arrived.

The mirrored battle duration is only a generous backstop for the simulation's timed end
condition. It is not the duration a normal live battle receives, and it does not need to match:
the playout stops at arrival or at its own 600-tick ceiling before that duration matters.

- **Hover.** The preview picture, narrowed to the units this hover is about : the unit under the
  pointer, the one it fights once the board settles, and everyone fighting it there. Each gets its
  ghost, its white movement line and its destination tile, with attack arcs coloured for the
  attacker and anchored ghost to ghost.

- **The combat-start jump.** A Lizard's passive throws it to the player's back line before the
  fight starts, and it walks on from there. Drawn as one line from its placement hex that reads as
  a unit that walked the whole way, so the jump gets its own fainter, thinner segment, a grey tile
  marks the hex it lands on, and the ordinary movement line continues from there. The tile is the
  same one the team colours use, at the same weight, so the landing hex is anchored exactly like
  every other end of a line ; only its colour says "passed through" rather than "stopped here".
  The ghost still belongs to the settled hex alone. A unit that lands where it stops takes the
  team-coloured tile instead, since one hex only ever carries one tile.

  This one cannot be caught by watching the playout tick. The game raises its combat-start
  triggers inside the battle frame's constructor, which the simulation runs while it is still
  being built, so the jump has already happened before the mod ticks anything. A check asking
  whether a unit's hex changed on a given tick would compile, ship and never fire. What the mod
  compares instead is the unit's hex on the real board, which the player has not started yet,
  against its hex in the very first frame of the playout. The test is a distance of about two
  hexes rather than an equality, because the board and the simulation only have to agree to within
  a hex, and nothing that walks can be two hexes from its placement before the fight begins.

  Consequence worth knowing : this sees teleports that happen as the battle is built, and not ones
  that happen mid-fight from an ability that has to charge first. Those are already accounted for
  in where the unit settles ; only the path drawn to get there is a straight line.

- **Opening preview toggle.** The same picture for the whole board. While it is on, the game's
  overhead unit panels are hidden so the board stays readable. They come back on toggle-off, on
  release into the fight, on leaving placement, and on every error path. Remembered between
  battles and between sessions, in `PreviewStartsOn`, which the button writes on every deliberate
  flip. Until 2.2.1 it was read-only to the mod and the preview restarted off in every battle.

- **The update notice (2.4.0).** One request per launch to the GitHub releases API, on a thread pool
  thread, parked in one volatile field and collected by `MenuUI` on the timer it already runs. No
  marshalling and no lock : the object is built complete and never mutated, and a reference
  assignment is atomic. A network call on Unity's thread would freeze the game for as long as the
  connection takes to fail, and a Unity API touched off-thread is undefined behaviour, so the split
  is not stylistic.

  **Everything that can go wrong is silence.** Offline, DNS gone, rate limited, captive portal
  serving a login page, a runtime missing a piece, a malformed answer. At most one log line. An
  error dialog raised because someone is on a train is worth less than nothing.

  Three decisions worth not re-litigating :

  - **The releases API, not the download redirect.** One request answers both whether something is
    newer AND the real asset URL. Building the URL from the tag would bake today's file-naming
    convention into every copy already installed, so the day the naming changed, every older build
    would send its players to a 404 that nothing could fix from our side.
  - **It runs whether the mod is switched on or off**, and it is started BEFORE the leaderboard
    guard. A guard that cannot be applied means a game update moved something, which is the single
    moment a player most needs to hear a newer version exists ; `UnavailableBody` already tells them
    to check for one, and this is the mod doing the checking. It is gated only on `CheckForUpdates`,
    which is the one consent that is actually about contacting the internet.
  - **The dialog names no button.** `DialogPanel` is a singleton shared with the game's own quit,
    GDPR and Steam-survey dialogs, and its buttons carry the game's words. Writing a body around
    "press OK" would describe a label this mod neither owns nor can keep, and relabeling the shared
    panel would leak our words into the game's dialogs. The body says what happens instead.

  `ReleaseManifest` is deliberately free of MelonLoader, Unity and networking, because the version
  comparison is correct or silent and never loud : a mistake in it presents as an updater that never
  fires, which is indistinguishable from one with nothing to say. Pure means it can be run outside a
  game launch, and it has been, against the live API answer and every malformed shape.

- **Arrow origin**, on by default, and since 2.3.0 a file setting with NO button and NO shortcut.
  Off re-anchors every attack arc at the units' current hexes, in hover and preview alike. Set
  through `ArrowsFromGhosts`.

  It had a button until 2.3.0 took it away, on the same trade as the direction marks and for a
  sharper reason : on is not a preference, it is the picture the mod exists to draw. An arc that
  starts where a unit will be standing describes the fight that is going to happen ; one that starts
  where it is standing now describes a moment that never occurs, since the unit has left before it
  swings. Off is the shape the mod first shipped with, kept for anyone who attributes an arrow
  faster that way, and that is not a question worth a button in a row where every button costs the
  ones beside it.

  **`OverlayRenderer.ArrowsFromGhosts` is still pushed every frame and still compared in
  `PictureUnchanged`.** Do not "simplify" either away. The value is only fixed for a session, not
  forever, and the comparison is what the cache contract requires of every visual input regardless
  of how it got there.

  **Upgrading forces it back on, once** (`Mod.ApplySettingsMigrations`, migration 1). Removing the
  button would otherwise strand anyone who had switched it off in a state they most likely never
  meant to keep, with no way back that did not begin with knowing the settings file exists. The
  once is the whole design : `SettingsMigration` records the last migration applied, so a value set
  by hand AFTER the update is never touched again. Forcing it every launch would not be a
  migration, it would be the mod overruling the player forever, and it would make the setting a lie
  with nothing on screen to explain it. The log line is printed only when a value actually moved,
  so it can answer the one question it exists for.

- **Attacks that land on more than one unit (2.5.0).** `SettledEntity` carries `ExtraTargets`
  alongside its single `TargetPairing`, and the renderer draws an arc to each. Two mechanisms feed
  it, and they are not variants of one another :

  - **Reach**, measured from the UNIT. Three ability actions in the whole game loop an attack, and
    `MultiHit` keys on the action class rather than the hero, so a respec or a rename cannot break
    it. `FunkeAbilityAction` and `TillyAction` take every enemy within the caster's **live** Attack
    Range ; `MingAbilityAction` takes distance exactly 1. Live, so a range rank modifier or a
    specialization widens the picture with no code change.
  - **Splash**, measured from the unit being ATTACKED, at distance exactly **1**. Authored as
    `IsAdjacentToTriggerTargetCondition`, whose body is literally
    `HexGridUtils.Distance(target, triggerTarget) == 1` : exactly one, not within one, because the
    thing being struck is already struck by the ordinary attack. Five effects use it; three are the
    Fire, Frost and Poison Dragons' auto attacks. **It lives on a PASSIVE**, which is why it was
    invisible for four versions : nothing in this mod had ever read one. The path is
    `PassiveAbilities`, then `GetAllEffects()`, then `ModularEffect.Condition`, read by native class name.

  Every distance test calls the game's own `HexGridUtils.Distance`. Hex distance on an offset grid is
  not the obvious formula, the game ships the answer, and a second implementation is a second thing
  to drift. Splash arcs originate at the struck unit rather than the attacker, which is both the real
  geometry and the thing that tells a player which neighbour to move.

  **Interop trap** : the wrapper for the game's read-only list exposes **only an indexer**, with no
  `Count` and no `GetEnumerator`, so neither a `for` nor a `foreach` compiles against it. Cast to the
  concrete `List` first.

- **Areas nobody can step out of are not drawn (2.5.0).** The Final Boss dragons' breath is authored
  as a circle of radius 12 because what it means is "every enemy"; painting that as a ring around the
  dragon invited a dodge that does not exist. An outline is suppressed when its reach is at least
  half the board's corner-to-corner span, measured from **the board** (`PredictionResult.BoardWidth`
  / `BoardHeight`, carried off the playout's own `BattleConfig`) and never from where the units
  happen to stand. A huddled team is not playing on a smaller board, and measuring the huddle would
  shrink the threshold until an ordinary zone was suppressed as unavoidable.

  On the shipped 7x8 board the threshold is **11.55** world units : the dragons' 12.0 is suppressed
  and the next largest real area is 6.0, so there is no near miss. Comparing a simulation radius
  against a Unity-grid span is sound because the sim's cell-to-world table is itself computed from
  Unity's Grid : they differ by about one fifteen-thousandth of a hex (see `CellWorldTable`).

- **Ability areas toggle** (2.3.0), leftmost of the four, on by default. Decides whether the dashed
  area outlines are drawn at all, for heroes and enemies alike. It is a plain visibility switch over
  what each mode already shows rather than a mode of its own : the hovered unit's area in hover,
  every unit's with the preview on, following a hero while dragging, and none of them when it is
  off. Remembered in `AbilityAreas`, written by the button.

  Two gates, for two jobs, and both are needed. `OverlayRenderer.ShowAreas` is checked at the top
  of `DrawAoe`, which covers both places areas are asked for and means a third caller added later
  inherits the switch rather than having to remember it ; **and it is in `PictureUnchanged` and
  `RememberPicture` beside the other display switches**, without which the button would flip and
  leave the outlines on screen until something unrelated forced a redraw. `AoeShapes.Update(bool)`
  takes the same answer so the twice-a-second scan stops as well as the drawing, since that scan is
  the entire cost of the feature. It goes quiet once on the way down rather than clearing every
  frame, because `Clear` bumps the version and the renderer redraws whenever the version moves.

  Kept apart from `Capabilities.AoeOutline` on purpose. That one means the feature broke and said so
  in the log ; this one means the player asked for it to stop. Both must be true to draw, and
  neither writes the other, so a fault can never flip a button the player owns and a deliberate
  choice never reads as a defect.

- **Direction marks**, off by default, and the only display option with NO button and NO shortcut.
  Adds a smaller chevron at the middle of every arc and movement line, skipped on lines shorter
  than about one and a half hexes. Set through `MidlineArrowheads` in the settings file.

  It had a button until playtest feedback took it away : once see-through units made the board
  readable, this was the least useful of the four, and a button nobody presses is a button in the
  way of the ones that do get pressed. The feature is cheap to keep and the row is not, which is
  the general shape of the trade whenever another control is proposed.

  That trade was taken the other way in 2.3.0 for the ability areas, and the difference is the
  whole test : the direction marks were a control nobody had asked for, sitting next to three that
  were being used, while the areas button was asked for by players who wanted the outlines gone and
  had no way to do it. A row grows for a question people are actually asking.

- **Keyboard shortcuts, placement only.** P for the preview, T for see-through units, G for the
  ability areas, each rebindable to any `UnityEngine.InputSystem.Key` name. They set the toggle's
  own value, so a key and a click take one path and cannot drift apart. A shortcut exists only
  where a button does : F went with the arrow origin button in 2.3.0, and `ArrowOriginKey` with it.

  Two things chose those three, neither of them taste. The game already owns Space, Enter, Tab, H,
  Shift and Escape, and its own translated text is the only list of that there is, since there is
  no keybinding screen and its input actions are mouse-only. And a key is a physical position, so
  A, Q, Z, W and M all land under a different finger on common non-QWERTY layouts, while P, T
  and G do not move. That test is also why the areas are not on A for area or Z for zone, which
  were the two obvious letters and are both disqualified by it. The label on the tooltip comes from the keyboard the player is actually using, so
  an unusual layout shows the character its own key produces.

- **Shortcut display follows the game's own shape.** The game writes a control's key in round
  brackets after its name : "Hero Panel (Tab)", "Open Shop (Space)", "Feedback (Enter)". Square
  brackets are its other shape, kept for a key named in the middle of a sentence, as in "Hold
  [Shift] for more detail". A button's own name takes the round ones. The label is read from the
  live keyboard rather than from a name in code.

- **No tooltip on a unit.** It named the hovered unit's target in words, next to arrows already
  saying it, and read as leftover debugging. `NativeUI.Update` consequently takes neither the
  prediction nor the views.

- **Hero drag.** While a board hero is held, the mod predicts the board as if it were released on
  the hex under it. With the preview off that is the dragged hero's own hover story at its release
  hex ; with it on, the whole opening recomputed live. It recomputes only when the candidate hex
  changes, on a larger per-frame budget, with a cache per layout, so a hex crossed twice and the
  drop itself both render instantly. Setting `DragLivePreview` to false restores the old
  hide-while-dragging behaviour, and any fault in the drag path degrades to that same behaviour.
  The four buttons stay put, and all four persist.

  - **The drag is read from the game's state, never from input.** A hero on the board sits exactly
    on its tile centre, and a hero in hand is moved to the cursor's ground point and lifted, every
    render frame. So a hero away from its own tile is being dragged, with no assumption about the
    lift height, the board's height, or any input event arriving.

    The first implementation watched for the frame the mouse button goes down and lost the whole
    drag whenever that one frame was hidden from the mod, by the click that restores window focus
    after an alt tab, or by a press starting over a piece of UI. The preview then either froze on
    the picture from before the drag or hid everything. Both were reported from play. An event you
    can miss is not a state source.

  - **Release is mirrored exactly.** A hex is a valid destination when it is in range and free,
    and free means free of an enemy, so a hex holding one of your own heroes is valid and the
    release swaps the two. The prediction exchanges both tiles and both heroes are drawn where
    they will really stand. Modelling it as a one-way move put two heroes on one hex, which
    shipped once. An enemy hex or an out-of-range hex really is a no-op, so the mod predicts
    the board unchanged ; a hero held over the UI is parked off the board and a benched hero has
    no board hex to move from, so both hide the visuals rather than guess.

- **The fight.** Every visual and every object the mod owns is destroyed immediately, and the unit
  panels are restored first. The self-check keeps sampling invisibly.

## What the mod binds to

- **Camera** : the board controller's own render camera, the same one the game hands to its input
  handling. Never the main camera.
- **Views** : the board controller's own hero dictionary, plus a scene sweep for enemies validated
  against the game registry.
- **Team colours** : read live from the health bar view's own serialized colours.
- **Tiles** : the board controller's ally and enemy tiles, falling back to the open tile only when
  a team tile has no sprite.
- **Ghost shader** : `_BaseColor`, then `_Color`, then a live unlit shader as fallback. Ghosts
  copy render data only ; no live object is ever cloned wholesale.
- **UI** : the live placement parent, three clones of the game's speed toggle, its own font
  assets, and its tooltip controller driven explicitly. A runtime failure falls back to a plain
  text chip styled from the live UI. The origin and direction-mark icons are drawn in code.
- **Unit panels** : one parent holds every health bar view in the battle, heroes and enemies
  alike, so the preview hides the layer by switching off that one parent. A panel created during
  placement, when a hero is swapped in from the bench, lands under it already hidden, and the game
  only ever flips the individual panels, never the parent.
- **Drag** : the game's drag controller is not a scene object, so the drag is read from the
  dragged hero's own position instead. The mouse-based signal survives only as a backstop for when
  that tracker is disabled or faulted.
- **Main menu** : the menu controller's own button references, cloned the same way the placement
  toggles are, and the game's own `DialogPanel` for the confirmation. The mod finds the menu from a
  scene-load event rather than by polling, learns the menu scene's name the first time it succeeds,
  and ignores every other scene after that, so a battle load never pays for a search. It destroys
  the clone's `LocalizeStringEvent` before writing the label, or the game would put its own text
  back on the next language change.
- **The leaderboard gate** : `RunSessionDataReader.RunId`, compared at submission time against the
  run the mod recorded itself running in. Not cached, not ticked, and not read at all when no run
  has ever been marked, so a player who never switches the mod on can never be blocked by a read
  that failed. Everything else about the menu is presentation and cannot change the answer.
- **Cell positions** : the battle scope's own `SimulationDebugConfig`, whose baked cell-to-world
  table is what `CreateBattleConfig` reads. **Not `HexGridUtils.GetCellCenterWorldPosition`,**
  which is the game's hex formula and is a different number : the table was baked from Unity's
  floating point grid in the editor, and a hex's width carries a square root of three that float
  and fixed point round differently. The mod computed it for months and the self-check failed on
  the difference. Optional binding : without it the formula answers and the gate's tolerance
  absorbs the difference.

### Tooltips, and three traps paid for in full

The mod drives the game's own tooltip explicitly : one source, one target, an explicit show, and
a raycast freeze while it is up.

1. **Use the battle's controller.** The run's tooltip controller is a kind of application tooltip
   controller, so both are alive at once and a plain search returns whichever comes back first.
   The wrong one drives a tooltip nothing in this scene has a position for.

2. **A runtime target must carry its own anchor.** For as long as anything is showing a tooltip,
   the controller re-reads that thing's anchor every frame, whatever else is happening. With no
   usable anchor it resolves to the origin of the world, which is the middle of the screen, and
   the freeze then stops the game from clearing it : an empty black panel stranded mid screen. So
   the mod creates its own anchor, parented under the tooltip view's own parent so the coordinates
   match, and moves it to the cursor each frame. That turns the same per-frame work into a tooltip
   that follows the pointer.

3. **A call that returns is not a tooltip that appeared.** Clearing the tooltip raises a flag, and
   showing it returns immediately while that flag is up, so the whole sequence can finish quietly
   having drawn nothing. The mod reads the view back and logs when nothing rendered.

4. **Never touch the shared tooltip unless the mod put something in it.** Hiding it unconditionally
   made the game's own tooltips flicker and disappear, because on every frame the mod had nothing
   to show it was still hiding the shared view. It is gated on a flag set only by a successful
   show, and it releases the freeze before hiding, so a failure cannot strand a panel.

Which button the pointer is over comes from the hover raycast, which already runs every frame, so
no second raycast is needed. That report needs its own full scan of the hit list : the blocking
pass stops at the first control it finds, and the mod's buttons are clones of one, sitting among
the game's panels, so folding detection into that ordered loop found nothing and the tooltips
never appeared. A loop that legitimately stops early cannot double as a search.

Do not instead attach a raycast target to the cloned buttons and let the game find them. The
controller only inspects the topmost hit, so it depends on which child graphic happens to be on
top, and it asks for an anchor every frame, which logs an engine-level error every frame for any
target with no anchor above it.

## Player text and typography

Every player-visible string lives in one block at the top of `NativeUI`. Code that computes a
result never writes prose : the parity gate and the mod publish a notice state, and the UI decides
the words. The shipped readmes are additionally gated in `packaging/package.ps1`, which fails the
build on an em dash, an en dash or a curly quote, in the readmes and across the source tree alike.

With `DevLog` set to true, the first usable placement writes the complete resolved census to
`UserData/GuildrunTargetingMod/ui_census.json` : hierarchy and canvas ordering, fonts, the button
path, cameras, a sample collider, the ghost shader property and the runtime colour source. The
prediction and parity logs carry the current encounter's id.

## Releasing an update

1. Bump `Bindings.ModVersion`. It is the single source : `packaging/package.ps1` reads it for the
   zip names and stamps it into both readmes.
2. `pwsh -File packaging/package.ps1`. It runs the typography gate over every source and
   documentation file first, then builds, then writes and checks both archives.
3. Copy `packaging/stage-mod-only/Mods/GuildrunTargetingMod.dll` over the one in the game, so what
   is installed is byte-identical to what ships. A rebuild is not reliably byte-identical, so copy
   the packaged file rather than the build output.
4. Publish. A new version means a new tag and release ; re-releasing the same version means
   `gh release upload <tag> <zips> --clobber`.

**A stuck preview clears itself, and there are three levers behind that.** Two fights in a row
disagreeing writes `<version>#r<rules revision>` into `ParityFailureVersion` and the preview stays
off across launches. Any later fight that does not disagree clears it, which is the normal
recovery and needs no release. A game update clears it too, because a verdict about a build that
is no longer installed is not evidence. And bumping `ParityRulesRevision` in `Bindings.cs` clears
every stored failure everywhere, which is the lever to pull whenever the gate's own comparison
rules change : a verdict is only as good as the rules that produced it, and revision 1 failed
every battle it ever judged.

**Triaging a report starts in `MelonLoader/Logs/`, not in the code.** Both bugs found on release
day were found by counting lines, not by reading source. Three counts answer most of it :
`visuals resolved` against the number of placements entered (they must be equal, and a stuck 1 is
the teardown bug), `PARITY PASS` against the number of fights, and anything at `[ERROR]` or
starting `tearing down`.

## In-game acceptance pass

1. Hover heroes and enemies with the preview off. Check the arrow directions, and that nothing is
   drawn over the interface or over empty board.

2. **Hover and preview must agree, on a board where the first pick is not the settled one.** Do
   not drop this step : it is cheap and it is the whole class of defect. Use `snake-act-1` layout
   1 with Nyx at (6,0), Yuuna at (5,0) and Dragomir at (4,3). Nyx's first pick is a genuine
   coin toss between two identical snakes at equal distance, and her settled target is the other
   one either way. Hover Nyx with the preview off, note the enemy, then turn the preview on and
   read the same arc. They must name the same enemy, and it must be the snake Nyx is standing next
   to at her settled hex, never the one Dragomir is fighting at (5,4). Any board with heroes in the
   back row and two equidistant enemies works ; this one is the reported case.

3. Turn the preview on. Inspect ghost opacity, movement lines, tile sprites, and hover isolation
   on a crowded board.

4. **The combat-start jump**, on any `lizard-act-1` board. Each Lizard must show a faint thin line
   from the hex it is standing on to a hex on the player's back line, a grey tile on that landing
   hex, and a normal movement line onward from there to where it settles. The grey tile must read
   at the same strength as the team-coloured ones, not fainter. The ghost and the attack arrow
   belong to the settled hex, never to the landing hex. Check it in hover and with the preview
   on ; both go through the same code. Every other unit on the board must be unchanged, with one
   ordinary line, one team-coloured tile and no grey anywhere.

5. Drag a hero across several hexes with the preview off, then on. The arrows and ghosts must
   track the hex under the hero with no stutter and no flicker ; an enemy-side or out-of-range hex
   must show the board unchanged ; holding over one of your own heroes must show both swapped ;
   hovering the bottom panel must hide everything ; the drop must render instantly. Then check
   that `DragLivePreview=false` restores the old disappear-on-drag behaviour.

5b. **The stale picture clears itself, and only where there is one (2.2.0).** Rest on a hex the
   hero has not stood on before and watch the screen while the new playout runs. On a machine that
   answers promptly there must be NO blank at all : that is the flicker check above and it has not
   been relaxed. On a machine slow enough for the wait to be visible, the arcs, ghost copies, tiles
   and footprints must go out and stay out until the new hex is answered, rather than the previous
   hex's arrows standing in for them. Three things must NOT go out with them : the units stay
   see-through, the game's overhead panels stay hidden, and the placement marks stay lit, because
   none of those waits on a playout. The placement report grades this and prints the threshold it
   used, so take the number from there rather than from this page : `drag response: N drag
   preview(s) answered, mean X ms, worst Y ms, Z cleared after T ms`. Z at 0 on a machine that
   visibly waits means the clear is not firing ; Z climbing on a machine with no visible wait means
   `StalePictureSeconds` is set too low.

6. Drag detection, both past defects. Alt-tab away mid-placement, click back into the window and
   drag immediately : the preview must track the hero, not freeze on the picture from before. Then
   repeat drags and clicks in varied spots : the preview must never be left stuck on a stale
   layout, or blank while a hero is in hand.

7. Start the fight. The entire player-facing layer must be gone before the fight is visible.

8. Use a board where the prediction prevents at least one death. Confirm the preview keeps that
   unit in the arrival picture, then finish the real fight and confirm `parity_log.jsonl` records
   `preventedDeaths` above zero and a `partial-pass`, with a `not-comparable` settled-state note.

9. Review `ui_census.json` and `parity_log.jsonl` under `UserData/GuildrunTargetingMod/`, and the
   colour and discovery lines in MelonLoader's log.

10. Play on a board that runs out of ticks. Check the English tooltips and the "Still moving"
   notice.

11. Shortcuts. Press P, T and G during placement and confirm each flips the matching button with
   the same feedback a click gives. Confirm each tooltip reads `Name (Key)`. Confirm they do
   nothing outside placement. Set `PreviewKey` to a nonsense value and confirm the mod falls back
   to P, logs one line, and leaves the other two working. There must be exactly THREE of the mod's
   buttons on the row, left to right : ability areas, opening preview, see-through units. **Two
   keys must now do nothing** : D, whose direction-marks button went in 2.0.0, and **F, whose arrow
   origin button went in 2.3.0**. Both features are still there and still set from the file, so
   confirm `MidlineArrowheads=true` still draws the chevrons and `ArrowsFromGhosts=false` still
   re-anchors every arc at the units' current hexes.

11b. **The one-time settings migration (2.3.0).** Three launches, and the second is the one that
   can fail silently. It needs the settings file edited between each.

   1. **It fires.** Set `ArrowsFromGhosts = false` and `SettingsMigration = 0`, launch. The file
      must come back with `ArrowsFromGhosts = true` and `SettingsMigration = 1`, the log must carry
      the `settings brought forward` line, and the arcs must anchor at the settled hexes.
   2. **It does not fire again.** Now set `ArrowsFromGhosts = false` and leave `SettingsMigration`
      at `1`. Launch. The value must **stay false**, there must be **no** `settings brought forward`
      line, and the arcs must anchor at the current hexes. This is the whole test. Step 1 passing
      on its own is equally consistent with a mod that overrules the player on every launch, which
      is the defect, and it would look identical to anyone who only ever ran step 1.
   3. **It is quiet on a file that had nothing to change.** Delete the whole `[GuildrunTargetingMod]`
      section, launch. `SettingsMigration` must be written as `1`, `ArrowsFromGhosts` as `true`, and
      there must be **no** log line, since nothing moved and a line here would claim a change that
      never happened on every fresh install.

11c. **The update notice (2.4.0).** The parsing and the version comparison are already proved
   outside the game : `ReleaseManifest` is pure, and the harness in the session scratchpad runs the
   real source against the live API answer plus every malformed shape, including the `2.10.0` versus
   `2.9.0` case that a string comparison gets wrong. Re-run that before touching it. What a launch
   has to prove is the half a harness cannot :

   1. **It appears at all.** Set `LastUpdateNoticeVersion` to empty and `ModVersion` lower than the
      published release (or publish something newer), launch, and reach the main menu. The dialog
      must name the newer version and your current one, and must arrive AFTER the intro rather than
      on top of it.
   2. **Accepting opens both.** The browser must land on the mod-only zip for the newest release,
      not the release page and not an older asset. The game folder must open beside it. Under Proton
      the folder may not open at all, which is expected and why the path is logged and the dialog
      says where the file goes ; the download must still work.
   3. **Once per version.** Relaunch. There must be no dialog, and the log must say the version has
      already been offered. This is the half that fails quietly, because a second dialog looks like
      the feature working rather than the feature repeating.
   4. **It never fights the leaderboard notice.** On a fresh install with both due, the leaderboard
      notice must come first and the update notice must wait for a later tick. Two modals in one
      frame is the case the game logs its own error for.
   5. **Offline is silent.** Disconnect, launch, reach the menu. No dialog, no error, one log line.
   6. **The setting is obeyed.** `CheckForUpdates=false`, launch, and confirm no request is made and
      nothing is shown.

12. **See-through units, and all three buttons remembering (2.2.1, 2.3.0).** T must fade every unit for the
   whole placement, hovering or not, preview on or off, and restore them the instant it is switched
   back.

   Then the memory, with `UserData/MelonPreferences.cfg` open beside the game. Starting from the
   preview off, switch it on during a placement : `PreviewStartsOn` must turn `true` on the click.
   Start the fight, and enter the next placement : the preview must be on and its button lit, and
   the line must **still** read `true` even though the mod itself switched the preview off in
   between. That last half is the whole test. The mod moves this button twice a battle, and if
   either of those movements reached the settings file the player's own choice would be overwritten
   by the teardown within one fight, which is a defect no single placement can show. Switch it off
   in-game and confirm the line returns to `false` and stays there. Same round trip for T and G,
   and the same again through the P, T and G shortcuts rather than the buttons, since a key press
   goes through the button and must write identically.

13. **The placement marks, both halves at once.** Equip a hero with Sentinel's Plate or Deadeye Hood
   and take a run carrying one of Frontline Defender, Frontline Embiggener or Frontline Barricader.
   The one log line to look for is `positional glow active: N hero(es), M item(s), K relic(s);
   scanned X, Y positional, Z unattributed`. **N, M and K must all be above zero.** Z counts effects
   belonging to nobody on the board, an act boss relic or an enemy's, and may legitimately be above
   zero. Then, on the board :
   * moving that hero out of the front row must flip its hex from lit to drained and struck through,
     and the mark must APPEAR on its item in the same breath. Moving it back must take the mark off
     again and leave the item looking exactly like vanilla ;
   * the relics in the bar must be marked independently of any hero ;
   * **a working item carries nothing at all.** No border, no strike, no ring, no tint. This is the
     rule the whole mark rests on, and the fastest way to check it is to compare a hero whose items
     are all paying against the same row with the mod switched off : the two must be identical ;
   * nothing on an unaffected hero, item or relic must be marked at all ;
   * leaving placement must remove every mark. Check the item row afterwards : the game's own
     colours must be exactly as they were, because the mod only ever adds its own objects and never
     writes to one of the game's.

13b. **The marks under a dragged hero.** Pick that hero up and move it between a hex that satisfies
   its rule and one that does not. The hex mark must flip, and the marks on its items and on the
   relics must appear and disappear, as you cross and before you let go, settling to the same answer
   once you drop. Look for
   `positional glow follows the drag: N hero(es), M item(s), K relic(s) on the board being dragged
   to`. Holding the hero over the interface, or over a hex where dropping would do nothing, must
   show the real board's answer rather than a stale one.
   **Then check the self-check still passes.** This evaluation runs against the same frame the
   prediction is about to be played out from, so if it ever wrote to that frame it would change the
   fight being predicted and `parity_log.jsonl` would start disagreeing. It is handed a switched-off
   sacrificial writer precisely so it cannot, and a clean parity row after a drag-heavy placement is
   what proves it.

14. **The area outline.** Look for `ability areas: N unit(s) with a footprint to draw, M ability(ies)
   with none, K left undrawn as ambiguous`. On a board with a unit that has one, the outline must be
   a broken ring, not a solid one, and must sit on the settled hex with `ArrowsFromGhosts` on and on
   the current hex with it off. That is now two launches rather than one keypress, and it is still
   worth both : the outline must be anchored by the same rule the arrows are, and a version where
   they disagreed would look right in every screenshot taken with the default.
   It must vanish while dragging, like everything else, and it must be gone before the fight is
   visible. A circle must read as a circle from every camera angle : if it looks like an egg, the
   per-ring camera nudge has regressed to a per-point one.

14a. **The areas button (2.3.0), and the frame it is pressed on.** The frame is the whole test.
   Hover a unit that has an area and hold the pointer perfectly still, then press **G**. The outline
   must go **on that frame**, with nothing else moving and the pointer not moved. If it only clears
   once you jog the mouse or hover something else, `ShowAreas` is missing from `PictureUnchanged`
   and the renderer is holding a stale picture ; that is the one defect this feature can ship with
   and look fine in every other test, because every other test moves something.

   Then the rest of it. Off, no outline in hover, none with the preview on, none while dragging,
   on enemies as well as heroes. `AbilityAreas` must follow the click in
   `UserData/MelonPreferences.cfg`, and the button must come back the way it was left in the next
   battle and the next session. Press **G** again and the outlines must return on the next frame,
   not up to half a second later, which is what `Clear` leaving the scan due immediately buys.
   Finally, with the areas off, `Perf` must show the area slot at effectively zero : the scan is the
   cost of this feature and switching it off is supposed to stop paying it.

14b. **Hover after leaving the window.** Alt-tab away mid-placement, click back in, and hover a
   unit WITHOUT touching a hero first. It must respond immediately. Before, it stayed dead until a
   hero was picked up, because that is what rebuilt the model-to-unit map. With `DevLog` on, a
   rebuild logs `unit view map rebuilt: the pointer was over a model it did not know`.

14c. **Multi-hit attacks and the suppressed boss circles (2.5.0).** Four checks, and the boss one
   needs a Final Boss floor.

   1. **Funke.** Place him where two or more enemies sit within his Attack Range and hover him. One
      arc per enemy in range, all in team colour, not one. Give him a range rank modifier or the
      range specialization and confirm the picture WIDENS on the next placement, since range is read
      live rather than from the authored stat. Ming is the same test at distance exactly one.
   2. **The Dragon cleave.** On a Final Boss floor, put two Heroes side by side within reach. The
      Dragon must draw its ordinary arc to the Hero it is aimed at, **plus** an arc to each Hero on
      an adjacent hex, and those extra arcs must start at the STRUCK Hero rather than at the Dragon.
      Move the neighbour one hex away and its arc must disappear. That last half is the test : if the
      arc survives the move, the splash is being measured from the wrong unit.
   3. **The boss circle is gone.** The same floor must show NO huge ring around the Dragon. On any
      mushroom-mage board the storm circle must still be drawn, and Gustav's Blizzard too. If either
      of those vanished, the suppression threshold is being measured from the units instead of from
      the board.
   4. **Everything else is unchanged.** Any ordinary board: one arc per unit, exactly as before.

15. **The Rift Seal mark, which needs a Red Rift run.** Three states, one indicator, and no text on
   screen in any of them.
   * Seal equipped on a hero whose classes are **already charged**, while another fielded hero
     could still charge it : the Seal's icon in that hero's item row must be struck through in red.
     This is the case a player reported as "a weird text" and it is the reason this round exists.
   * Seal **in the bag** while a fielded hero could charge it : same red mark on its icon.
   * Winning **would** charge it, or **no** fielded hero could charge it either : no mark at all,
     and still no sentence anywhere.

16. **Nothing is withheld, and the numbers say so.** The boot log must read
   `parity state: knownBuild=... persistedFailure=False mismatchStreak=0/2` and
   `capabilities: CoreRead=True, Prediction=True`, and the preview must work on the first placement
   of a fresh install. Then read `UserData/GuildrunTargetingMod/parity_log.jsonl` after a fight :
   `mismatches` should be empty, and any opening-position line should now be absent entirely
   (because the mod reads the game's own table) or sitting in `notes` prefixed `within tolerance`.
   The boot log should also carry `cell positions read from the game's own baked table` once per
   placement scene ; if it says the table could not be read, the mod is on the formula fallback and
   the tolerance is what is keeping it honest.

17. **The marks keep up, and the marks ARRIVE.** Two different things, and the second is the one
   that was reported twice.
   * Drag a hero across several hexes : the item and relic marks must change on the hex you are
     over, not a beat later.
   * Open a panel that was not already on screen. Its marks must appear **with the panel**. This is
     the one that used to wait out a scan timer, and it is what "the red line takes half a second
     to arrive" meant.
   With `DevLog=true`, leaving placement prints two lines:
   `placement marks: N refresh(es), first F ms, worst after that W ms, mean after that M ms` and
   `icon marks: N icon(s) marked from S slot(s) in the scene; K scene search(es), worst X ms;
   N frame(s), first F ms, worst after that W ms, mean after that M ms`.
   **Read "after that", never "first".** The first frame of each pays for every scene search and
   every classification and is expected to be milliseconds; the settled ones are what decide
   whether per-frame was affordable. The mod warns on its own, with no DevLog, if a settled frame
   of either exceeds 3 ms. There must be no `shadow replay budget ... spent` lines on ordinary
   boards ; one on a lethal board is the honest fallback working and must still leave the marks
   updating.

18. **When the Seal says nothing, the log says why.** `[TargetingMod] Rift Seal: ...` is printed
   whenever the answer changes and never while it holds, so one line covers a whole session. It
   names the state and the count behind it, e.g.
   `worn by a Hero whose groups are all charged, 2 fielded Hero(es) would charge it : MARKED`.
   If the mark is missing, that line is the first thing to read : it separates "decided not to
   mark" from "marked something that was not on screen" from "never ran", which is three play
   sessions of guessing otherwise.

19. **The mark itself, on a blocked item.** This is the picture, and it is worth looking at closely
   once rather than glancing at it every session.
   * The border must sit flush INSIDE the frame's own band, following the centre notch on the gold,
     red and dark blue frames and staying straight on the two plain ones. It is traced off the
     sprite at runtime, so a frame the game adds later gets a correct border with no code change.
   * **It must be the same red on all five rarities.** If it reads green, brown or gold on some of
     them, the mark is being tinted rather than drawn, which is exactly the defect it replaced :
     the interface shader multiplies a tint into the sprite, so borrowing the game's frame and
     colouring it produced rarity colour times our colour, and four of the five came out green.
   * Two lights travel the border, half a lap apart, one lap every `MarkLapSeconds` seconds. Set it
     to 0 : the border stays and the lights stop. Set it to 1 : they race. This is the dial most
     likely to want changing after a real play session.
   * The first time each rarity is marked, the log prints
     `traced the mark for '<sprite>': 222x228 band at x A..B, y C..D, V depth E`. A `(FLAT FALLBACK)`
     on that line means the sprite could not be read and the border is a plain rectangle : it still
     marks, it just does not follow the notch. Please report that line with the log.
   * **Relics get the same border, on a diamond.** Their frames are squares standing on a corner,
     so the mod reads them at 45 degrees and the graphics hardware turns the finished mark back.
     Check the border sits flush inside the diamond's band all the way round, and that it does NOT
     detour into the four corner studs on the red and dark blue frames : the studs are decoration
     sitting on the band, not the band bending, and a small red curl at each of them is the defect
     this is checked for. The log names it, `read at 45 degrees (a turned frame)`.
     **An item frame must NEVER report a turn, and a relic frame must always report one.** An
     item reading turned means the turned path has begun accepting shapes the border cannot
     describe ; a relic reading square means the gate has started passing a diamond as four straight
     edges, which is what once drew a red rectangle floating in the middle of a diamond.
   * **Hero abilities, rank modifiers and the specialization, on the Hero card.** This is the
     LARGEST of the three surfaces : eighteen of the thirty-one entries the feature can ever light
     up are hero-owned, and until now the only thing that could show them was the tile under the
     Hero, which says something is switched off without saying which of up to a dozen things. Their
     plate is a diamond like a relic's, so it takes the same traced border.
     Open a Hero card during placement with a hero out of position and check that **only the guilty
     modifier is marked**, not every icon on the card. Two Heroes carrying the SAME rank modifier in
     different positions must show it marked on one card and clean on the other : the state is keyed
     by Hero and entry together, and a mark that spreads to the other card means that key has been
     flattened back to the entry alone.
     The placement exit line names anything skipped :
     `ABILITY SLOTS SKIPPED: N whose icon did not match the entry the walk expected`. That is the
     safety check reporting, and it means the game repacked the card's icon array. **A non-zero
     count is a report, not a dial** : the mod deliberately marks nothing rather than risk marking
     the wrong ability. `hero-owned effect(s) not traced to their entry` on the glow line is the
     other half, effects whose origin could not be read at all.
   * A frame whose shape the border still cannot follow says
     `'<sprite>' is not a shape the border can follow, so it is marked with the strike alone`, and
     the placement exit line counts them. That is the honest outcome, not a defect.
   * A `nine sliced` warning means the frame art changed shape under us and the border will not line
     up. That one is a report, not a dial.
   * Frame time for this rides in the icon marks line as `worst trace X ms, worst lap step Y ms`.
     The trace is once per rarity per session and is allowed to be milliseconds. The lap step runs
     only while the lights move and should be well under one.

20. **The main menu switch, and the leaderboard rule behind it.** Nine checks, and the last four are
   the ones nothing outside the game can prove.
   * The boot log reads `[TargetingMod] leaderboards: SubmitScore, SubmitChallengeScore,
     IncrementChallengeWinstreak and ResetChallengeWinstreak patched; marked run=...`. All four
     names must be there. Anything else means the mod is inert and the button below will say so
     instead of working, because from 2.2.0 the four patches are one guarantee: if any of them
     cannot be registered and verified, none of the mod runs.
   * The button is in the main menu, reads `Targeting Mod: On`, and sits with the game's own
     buttons rather than floating over them. `main menu button placement:` in the log says whether
     the menu turned out to be a layout group or was measured by hand; read it either way, and read
     `main menu button resolved:` for the clone's path, the scene name and the label count.
   * Press it. The game's own confirmation dialog appears, with the game's art and its OK and
     Cancel. Cancel changes nothing. OK flips the label to `Targeting Mod: Off` immediately, and
     `MelonPreferences.cfg` reads `Enabled = false`.
   * Play a battle with it off. No preview, no marks, no overlay, no arrows, and the log stays
     silent past the boot block. Then switch it on and confirm everything works as it did in 2.0.2.
   * Language acceptance step removed: the mod now ships English only.
   * **The cost with the mod off, measured rather than assumed.** Set `DevLog=true`, play one
     placement with the mod on and read `Perf.Report`. Then switch the mod off and confirm no
     report is printed at all, because no frame is opened. That absence is the evidence.
   * **A battle scene load must not search for the menu.** After the first time the menu button is
     built, the log must never again show a main-menu resolve during a battle load. The mod learns
     the menu's scene name on the first build and ignores every other scene; without that it would
     walk every loaded object several times on every battle load.
   * **The submission that is allowed.** With the mod switched off and a run that was never played
     with it on, lose an Endless fight. The log must read `Endless score submitted: this run was
     played without the mod`, and Steam must show the score. **This is the one path no previous
     version of the mod has ever exercised**: every release until now returned false from that
     prefix unconditionally, so "the game's own submission still runs when we allow it" is proven
     here and nowhere else.
   * **The submission that is blocked, and the reason it gives.** Play a run with the mod on, quit
     to the main menu, switch the mod off, continue that same run and lose in Endless. The log must
     read `blocked an Endless leaderboard submission: this run was played with the mod on`. Then
     abandon it, start a new run with the mod still off, and confirm that one submits.

21. **The Red Rift win-streak freeze (2.2.0). Verification status, stated by coverage rather than
   by intent.** This entry exists because the freeze is the first thing the mod does that
   SUPPRESSES a write to the player's profile rather than merely declining to send something, and
   the strength of the evidence behind each half of it differs.

   **MEASURED at boot against the installed build, 2026-08-19.** The log reads
   `[TargetingMod] leaderboard suppression and Red Rift win-streak freeze applied` followed by
   `leaderboards: SubmitScore, SubmitChallengeScore, IncrementChallengeWinstreak and
   ResetChallengeWinstreak patched; marked run=...`. `FindStreakMutator` resolves both members by
   live reflection over `Il2CppEmber.Scopes.Application.Progression.Services.ProgressionService`,
   `VerifyNoUnknownStreakMutation` fails the boot if an unaccounted void `*ChallengeWinstreak`
   member appears, and `PatchAndVerify` interrogates `Harmony.GetPatchInfo` rather than trusting
   that `Patch` did not throw. Any failure sets `Applied = false` and the whole mod goes inert with
   the reason printed.

   **Two defects were found by that run and by nothing before it, which is why it is worth
   repeating after every game patch.**
   * `IncrementChallengeWinstreak` takes a `Guid` on the installed build; the decompiled tree the
     design was written against shows it parameterless. The lookup asked for the parameterless
     overload, threw, and switched the whole mod off. Signatures come from the interop assembly or
     the dummy DLLs, never from the decompile, which runs several game patches behind.
   * `UpdateChallengeWinstreak` is PRIVATE in Leyline's source and PUBLIC on the Il2CppInterop
     wrapper, so the boot check saw a third mutator and correctly refused to boot. It is the load
     path, it appends no win and records no loss, and freezing it would stop a leaderboard-version
     season reset from ever reaching an existing profile. It is therefore listed in
     `StreakMethodsDeliberatelyNotFrozen`, kept separate from the patched list so that "looked at
     and left alone" is never read as "guarded".

   **MEASURED: the method bodies really are detoured, and the answer really is obeyed.** A
   throwaway probe mod put POSTFIXES carrying Harmony's injected `bool __runOriginal` on both
   members, captured the live `ProgressionService` (via postfixes taking `__instance` on the
   constructor and on `UpdateChallengeWinstreak`, since the service is not reachable through
   `DataReaders`), and drove three cases by flipping the `ModdedRunId` preference between calls.
   Result, 2026-08-19 on build `58e3a0f2`:

   | case | mark | expected `__runOriginal` | observed |
   |---|---|---|---|
   | suppressed reset | `unidentified` | false | **false** |
   | allowed reset | empty | true | **true** |
   | suppressed increment | `unidentified` | false | **false** |

   The two cases with OPPOSITE expectations differ, which is what makes this a measurement rather
   than a blind instrument reporting one constant. `__runOriginal=false` means the game's own body
   did not execute, not merely that the mod logged something. The probe read the streak first and
   ran the allowed case only because it was 0, where appending a reset marker is arithmetically
   inert; it also set the mark before the increment case so the submission prefix would block
   independently if the freeze were broken, and it restored the original mark in a `finally`.

   **What remains an inference, narrowly.** The probe invoked the members itself, so it proves the
   BODY is detoured. That the game's own call sites reach that body rather than an inlined copy
   rests on both sites holding the service as `IProgressionService` (`ChallengeService`,
   `PersistenceService`), making the calls interface dispatches that cannot be inlined at the call
   site, plus the 2.0.0 measurement of exactly that shape on `SteamPlatformService.SubmitScore`.
   Strong, and still an inference. Do not restate it as a measurement.

   Note the trap recorded against the 2.0.0 probe and still applicable: patch the member the game
   dispatches to, never something it calls directly, or the detour never fires and every case
   reports nothing at all.

   **The measurement above was then REPEATED against the shipping bytes, and that repeat was not
   ceremony.** `package.ps1` rebuilds the DLL, and .NET builds here are not byte-reproducible: the
   first pass proved `612227b8...` while the zip contained `26f8b462...`. Same source, different
   object, and the 2.0.0 cycle has already recorded one probe that passed against a DLL nobody
   would receive. The DLL was therefore extracted back out of
   `GuildrunTargetingMod-v2.2.0-mod-only.zip`, installed, and rerun: boot line correct, all three
   cases PASS, `VERDICT: detour proven`. **The verified artifact is the one with SHA-256 beginning
   `26f8b462`. Do not rebuild between verifying and uploading - a rebuild produces a different
   object and voids this record.**

   **Player-facing text, checkable by reading the dialog.** The turn-on dialog and the first-run
   notice must both read `A run played with the mod on cannot submit a score and does not move the
   Red Rift win streak in either direction.` Text that still describes a streak "containing" a
   modded run means a pre-2.2.0 build is installed.
   * **The increment side, opportunistically.** It needs a full Red Rift win, so take it when one
     happens rather than farming it. Win a Red Rift run with the mod on: the log must print
     `suppressed a Red Rift win-streak increment`, the streak must not rise, and nothing must reach
     the Steam board.
   * **What a player is told.** The turn-on dialog and the first-run notice must both read `A run
     played with the mod on cannot submit a score and does not move the Red Rift win streak in
     either direction.` If the text still talks about a streak "containing" a modded run, an old
     build is installed.

A build that compiles proves the shapes the mod binds to and nothing about frame time. Steps 13,
14, 17 and 19 are where frame time is actually observed : 13 and 14 add throttled scans that walk
loaded objects, 17 reports the one thing that now runs every frame, and 19 reports the only thing
that uploads a texture while it does. Step 20 is where the leaderboard rule is observed, and its
last two checks are the only proof that exists that a score can still get out. **Step 21 is the
same kind of proof for the streak freeze, and until someone runs both of its halves the freeze is
an argument from a dispatch shape rather than an observation.**
