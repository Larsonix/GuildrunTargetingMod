# Changelog

Downloads for every release are on the
[releases page](https://github.com/Larsonix/GuildrunTargetingMod/releases).

## 2.1.0

* **A button in the main menu turns the mod on and off.**
* **Your scores can reach the leaderboards again.** A run counts only if the mod was off for the
  whole of it. A Red Rift win streak containing such a run stays out of the leaderboards until the
  streak restarts.

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
