# Changelog

All notable changes to **Quality Insights** will be documented in this file.
This project aims to follow [Keep a Changelog](https://keepachangelog.com/) conventions and semantic versioning.

## [Unreleased]

### Added
- **Real-life Play Time**
  - New **RL** column in the Quality Log table and RL time inline in log view (e.g., `1h 12m`).
  - Ignores paused time; updates live while unpaused.
  - **CSV** now includes a `PlayTime` column matching the RL display.
- **Pop out / Dock toggle** for the Quality Log.
  - Clicking toggles between docked main-tab and floating window; the other view auto-closes.
  - Floating window has a lightweight custom drag bar and stays clamped on-screen.
- **Open folder** button on exports panel (cross-platform: Windows/macOS/Linux).
- **Materials list** rendering in the table (up to 3 material icons + tooltip); falls back to Stuff if no mats list is present.

### Changed
- **Column layout persistence**:
  - Dragging splitters saves fractions without global “layout bumps,” preventing snap-back.
  - Reset column widths now broadcasts a layout refresh so all instances redraw with defaults.
- **Sorting**:
  - RL column sorts by “stamped first” then by play-time snapshot for intuitive ordering.
- **CSV**:
  - Header updated to include `PlayTime`.
  - Export folder pruning by max file count and total size (settings-driven).

### Fixed
- Column widths sometimes not resetting or “snapping back” after drag — resolved by deferring layout regeneration and tracking applied fractions.
- Misc compile guards around game loading/long events and safer nullability across UI flows.
- Stable pop-out/dock behavior—no duplicate windows, no focus tug-of-war.

### Performance
- RL time uses a tiny accumulator that only advances while unpaused; rows show a cheap `(now − stamp)` diff.
- Odds sampling remains deterministic and cached by pawn/recipe/boost/cheat state.

### Developer Notes
- Debug output is gated by **both** the mod’s *Enable debug logs* and RimWorld **Dev Mode**.
- Localization keys live in `Languages/English/Keyed/QualityInsights.xml`. With versioned load folders, ensure keys are copied to the active version.

### Migration Notes
- After updating, you can **Reset column widths** in Mod Settings if your stored layout predates the new columns.
- Assets (`Languages/...`, textures) are not embedded in the DLL—keep them in your mod folder tree.

---

## [1.0.0] – Initial release (summary)
- Quality Log with CSV export.
- Live Quality Odds gizmo.
- Optional dev cheat (≥ threshold) that respects Legendary rules.
- Construction path support and duplicate log suppression.

---

*Tip: when we cut a release, replace **[Unreleased]** with a version and date (e.g., `## [1.2.0] – 2025-08-21`) and start a fresh **[Unreleased]** section above it.*
