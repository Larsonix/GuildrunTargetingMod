GUILDRUN TARGETING PREVIEW  v{VERSION}
A mod for the Guildrun Demo, made by Larsonix.


WHAT IT DOES

See how the fight is going to start, while you are still placing your
heroes.

Hover a unit to see where it ends up standing and who it ends up
fighting. Hold a hero and the whole board answers for the hex under
your cursor, so you see the result before you drop it. A toggle shows
the whole board at once. Items, relics and abilities that only pay
from a certain place are marked when they are not paying.

The mod does not change the fight. It only shows what was already
going to happen.


SCORES AND LEADERBOARDS

  A run counts for the Steam leaderboards only if the mod was
  switched off for the whole of it.

While the mod is switched on, your scores do not go to the Steam
leaderboards. Not the Endless score, and not the Red Rift win streak.
A Red Rift win streak containing such a run stays out of the
leaderboards until the streak restarts.

You still see the leaderboards. Your standing is never lowered, and
your runs, your unlocks and your progress are saved the way they
always were.


INSTALL (once, about a minute)

1. In Steam : right click Guildrun Demo > Manage > Browse local files.
2. Drag everything from this zip into that folder.
   If Windows asks to replace files, say yes.
3. Launch the game normally.

This zip already contains MelonLoader, the loader the game needs to
run mods. You have nothing else to install.

The first launch takes one or two minutes and needs internet : a black
console window opens while MelonLoader sets itself up. This is normal.
Every launch after that is instant. The preview works from your very
first placement.


TURNING IT ON AND OFF

There is a button in the main menu that says TARGETING MOD: ON or
TARGETING MOD: OFF. Press it and confirm. You never have to close the
game and you never have to delete anything.

Switched off, the mod does nothing at all while you play, and your
scores go to the leaderboards again under the rule above.


LINUX AND STEAM DECK

The game only ships a Windows build, so it runs through Proton and you
use this same zip. One extra step: in Steam, right click Guildrun Demo
> Properties > General > Launch options, and put in

  WINEDLLOVERRIDES="version=n,b" %command%

Without it, Wine ignores the file the loader arrives through and the
mod never starts. This is the route the MelonLoader community uses for
Windows games under Proton. It has not been tested here, so please say
if it gives you trouble.

There is no macOS route today, because there is no macOS build of the
game to run.


REMOVE IT

Delete the file version.dll in the game folder. That turns MelonLoader
off completely and the game is exactly like before.
To remove only this mod and keep your other ones, delete
Mods\GuildrunTargetingMod.dll instead.
To remove every trace, also delete the MelonLoader, Mods, Plugins and
UserData folders.

To play for the leaderboards, switching the mod off in the main menu
is enough.


NOTES

Some antivirus flag mod loaders as unknown software. The one used here
is MelonLoader, an open source project used by mods for a lot of Unity
games : https://github.com/LavaGang/MelonLoader

Every setting, every keyboard shortcut and the full description are on
the mod's page :
https://github.com/Larsonix/GuildrunTargetingMod

Questions or bugs : find me on the Guildrun Discord, or on
https://guildrun.wiki
