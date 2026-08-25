# Changelog

Player-facing changes to **Smarter Taxiway Traffic**
([Workshop item 3788247984](https://steamcommunity.com/sharedfiles/filedetails/?id=3788247984)).

The engineering account of each change — including the wrong turns — lives in the
game's `MODDING-NOTES.md`, not here.

Versions are the `version` field in `package/ModData.json`, which is what Steam
shows. From 1.0.2 the `[BepInPlugin]` attribute carries the same number, so the
line BepInEx writes to `Player.log` on load identifies the build too. Before 1.0.2
that attribute read `0.1.0` for every release and was no help at all.

## 1.0.2 — 2026-08-25

Fixed:

- **Aircraft freezing nose to nose part-way down a long single-lane taxiway.**
  Two of the mod's own rules were disagreeing about right of way. The single-lane
  block rule picked a direction, and the junction head-on rule immediately wrote
  the opposite order across the same taxiway — several times a second, forever.
  Neither aircraft moved, and if a second pair met inside the corridor while this
  was going on they were stuck for good, since the game has no tug and nothing can
  reverse. The block rule now has the final say over its own stretch of taxiway.

  Most likely to have affected long taxiways with no passing places; a 41-node
  corridor is what exposed it. Short segments could hit it too, with less to go
  wrong. A four-figure "overtakes per aircraft-hour" in the summary line is the
  signature.

## 1.0.1 — 2026-08-22 — staged but never published

Built and tested locally; the Workshop item stayed on 1.0.0, so **no subscriber
ever received this**. Its contents ship in 1.0.2.

- Taxiway distances are measured rather than assumed to be 4 m per node. A
  diagonal step is 4√2 ≈ 5.66 m, so a diagonal run is nearly 30% longer than
  counting nodes suggests — which meant a taxiway segment could be handed to
  oncoming traffic while an aircraft was still traversing it.
- The small-aircraft tail default is now 5 nodes, matching the game's own
  look-back, rather than 4. Too short puts two aeroplanes in the same place, which
  is visible; too long only costs an occasional missed promotion, which is not.
- Bug reports moved to
  [GitHub issues](https://github.com/Rhamnos/AirportCEO-smarter_taxiway_traffic/issues),
  because Steam comments cannot carry a log file attachment.

## 1.0.0 — 2026-08-22

First release.

- Aircraft claim taxiway ground when they start rolling, not when their route is
  first plotted. A departure waiting up to three minutes for a pushback truck no
  longer holds its whole route against everyone else.
- An aircraft blocked by one that is nowhere near the node they are contending
  over can take its turn back.
- Single-lane stretches are derived from your own layout and cleared one direction
  at a time, the way a controller clears a segment to a hold-short point. Opposing
  traffic waits outside rather than meeting half way.
- Departures are held at the stand when the only way out is contested, instead of
  reversing into it.
- Precedence follows the ladder the game already uses for its runway queue:
  emergency first, then anything still clearing a runway, general aviation last.
- Any deadlock that still forms is detected and broken.
