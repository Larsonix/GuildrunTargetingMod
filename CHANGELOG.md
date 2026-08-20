# Changelog

Downloads for every release are on the
[releases page](https://github.com/Larsonix/GuildrunTargetingMod/releases).

## 2.5.1

Everything here makes something that already shipped do what it always said it did.

* **A hero's ability now gets the red border when its placement rule is not being met.** The border
  has existed since 2.0.0 for items and relics, and never once appeared on an ability. Two separate
  faults had to be cleared: the mod was looking at the wrong part of the interface, and the answer it
  did work out was being filed under a name nothing could look up.
* **Placement rules about OTHER heroes are now judged correctly.** Rules like Sal's The Lover, which
  pays out to heroes standing in front of her, were being asked whether Sal was in front of herself.
  The answer to that is always no, so those parts reported "not working" on every board and no amount
  of moving anyone could clear it. Fifteen entries were affected: four always claimed to be off, and
  eleven more could never report being off at all.
* **The mark under a hero is on the hex you put her on.** It used to appear on the hex she walks to
  as well, or instead, which is the one hex you cannot do anything about.
* **Ability areas that were being hidden are drawn again.** A rule meant to hide areas nobody can
  step out of was measuring how far away a shape's furthest point is, which says nothing useful about
  a long thin one. Requiem Barrage was hidden on every board it appeared on. The Dragons' breath is
  still hidden, which is what that rule is for.
* **Targeting no longer stops until a hero is moved.** The preview could get stuck holding a picture
  it had already thrown away, and only moving a hero a whole hex would bring it back. Picking a hero
  up and putting her down could not, because the mod could not tell that apart from doing nothing.
* **Moving a hero hides the old hex's arrows straight away.** They used to stay for a moment first,
  describing a hex the hero had already left, then blank, then return. One change instead of two.
* An ability the mod cannot read no longer stays unreadable for the rest of the session. A single
  badly timed moment while a battle was loading used to retire that ability's area until you
  restarted the game.
* The placement marks are worked out when the board changes rather than on every frame.

## 2.5.0

* **Attacks that hit more than one unit now show every unit they hit.** Funke's fireball catches
  every enemy in his range, Ming's burst catches everything next to him, and Tilly's the same. Until
  now all three drew a single arrow, exactly like a hero who hits one enemy.
* **The Dragons' cleave is finally on the board.** Their ordinary attacks also strike every Hero
  standing next to the one they are aimed at. That is the most important thing to know when placing
  against a Final Boss, and the mod could not show it because it lives on a passive. The extra hits
  fan out from the Hero being struck, because that is where the splash comes from.
* **The huge circle around the Dragons is gone.** It was their breath, which hits every enemy
  wherever they stand, so a ring around the Dragon suggested a dodge that does not exist. Areas you
  cannot step out of are no longer drawn. Every real area, including the mushroom mages' storms, is
  untouched.
* Range is read live, so a rank modifier or a specialization that grants Attack Range widens the
  picture straight away.

## 2.4.0

* **The mod tells you when a new version is out.** A message in the main menu names the version,
  says which one you are on, and offers to open the download for you. Accepting opens the mod-only
  zip and your game folder, so the file has somewhere to go the moment it lands.
* Each version is offered once. Decline it and it is not mentioned again for that version.
* It always points at the newest release, and it reads the real file from the release rather than
  guessing its name, so the link cannot rot.
* The mod contacts GitHub once per launch to ask what the newest version is, and asks nothing else
  and sends nothing about you. Set `CheckForUpdates` to false in the settings file to stop it
  contacting the internet at all.
* The check runs whether the mod is switched on or off, since being current has nothing to do with
  whether you want your runs to count.

## 2.3.0

* **A fourth button, and a shortcut, for the ability areas** (**G**). It decides on its own whether
  the dashed outlines are drawn, for enemies as well as heroes, and it applies to every way the
  board shows you anything : hovering, the opening preview, and while you are holding a hero. Turn
  it off for a board with no outlines on it, and everything else stays exactly as it was.
* Switching the areas off also stops the mod working them out, so it costs nothing while it is off.
* The new button sits on the left of the other two and remembers how you left it, like they do.
* **The arrow origin button is gone, and attack arrows now always start where units will be
  standing.** That is the picture the mod is for : an arrow starting where a unit is standing right
  now describes a moment that never happens, because the unit has left before it swings. The old
  behaviour is still there as `ArrowsFromGhosts` in the settings file.
* **If you had that button turned off, this update turns it back on, once.** Losing the button
  would otherwise have left you switched off with no way back that did not start with finding a
  settings file. If you really do want the old behaviour, set `ArrowsFromGhosts = false` in that
  file after updating and it will stay off from then on. The update says so in the log when it
  changes anything.
* The `ArrowOriginKey` line is now orphaned and inert, and **F** no longer does anything.

## 2.2.1

* **The preview button now remembers, like the other two.** Leave it on and the next battle opens
  with it on, this session and every session after. Leave it off and it stays off. The other two
  buttons have always worked this way ; the preview was the odd one out, and the only way to change
  it was a line in a settings file. A fresh install still opens its first battle with the preview
  off.

## 2.2.0

* **Playing with the mod on can no longer cost you your Red Rift streak.** It is frozen for that
  run : a win does not advance it, and a loss or an abandon does not reset it. Whatever your streak
  was, it is still there when you switch the mod off.
* Existing `ModdedChallengeStreak` preference lines are now orphaned and inert.
* **While dragging a hero, the preview no longer shows the answer for the hex you have just left.**
  If the new hex takes long enough to work out that you would notice, the arrows clear until it is
  ready. On a fast machine nothing changes.

## 2.1.0

* **A button in the main menu turns the mod on and off.**
* **Your scores can reach the leaderboards again.** A run counts only if the mod was off for the
  whole of it.

## 2.0.2

* **Faster.** Moving a hero updates the preview in about a thirtieth of a second, where it could
  take up to half a second.
* Smaller freeze on the first placement of a session, and no stall on boards where units keep dying.
* The mod behaves the same at any frame rate.

## 2.0.1

* **Faster, most of all on machines that were struggling.** The pause when a placement begins is
  largely gone, hovering and dragging cost a fraction of what they did, and a hitch that came back
  about once a second is gone.
* On a machine that cannot keep up, the preview takes a moment longer to appear instead of the game
  stuttering.

## 2.0.0

* **Items, relics, rank modifiers and specializations that need a position are marked.** Front row,
  back row, next to an ally, alone in a row. When the hero is standing where one of these pays, the
  hex under them lights up. When it does not pay, the hex is dimmed with a red line across it, and
  the item in that hero's row or the relic in the bar gets a red animated border and a line too, so
  you can see which one is the problem.
* **Hero cards show which ability or rank modifier is switched off**, with the same red border and
  line, and only on the hero that is out of position.
* **A wasted Rift Seal gets marked.** In a Red Rift run, if winning this fight would charge nothing
  and moving the Seal would fix that, the Seal's icon gets a red animated border and a line across
  it. That covers the Seal sitting in your bag, and the Seal worn by a hero whose classes are
  already charged.
* **A dashed outline shows the ground an area attack will cover.** Enemies get one too.
* **See-through units became its own button** (**T**), instead of being tied to a mode.
* New setting `MarkLapSeconds` sets the speed of the lights that travel a mark, or `0` stops them.
* **Scores stopped going to the Steam leaderboards while the mod was installed.** Replaced in 2.1.0
  by the main menu switch.
