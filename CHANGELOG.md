# Changelog

## [0.4.0] - 2026-09-13

### Added

- `[Terrain]`: dug, raised, levelled and paved ground slowly returns to the land's own shape once
  the area has been left alone (`IdleHours`, then `AfterHours` after the zone's ground was first
  seen modified, then a step every `StepHours`). Each step divides the remaining height change
  by `Divider` and drops it below `MinDelta`; paint goes back to nothing once the height under it
  is back, paved and cultivated ground excepted. Ground around player base objects (the game's
  own PlayerBase areas: workbench, forge, beds, fires ...) and wards is left alone by the object's
  own radius, so a base that creatures broke protects nothing any more. The data is written in
  the game's format (`TerrainComp.Save`); verified on a local 1.0.12 server: all 38 modified
  zones of a copied live world round-trip byte-identically, and a step reduced e.g. 242 to 230
  modified vertices with 587 to 230 painted ones, 60 vertices near a base left untouched.
