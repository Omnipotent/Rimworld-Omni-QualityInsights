# Changelog

All notable changes to **Quality Insights** will be documented in this file.
This project aims to follow [Keep a Changelog](https://keepachangelog.com/) conventions and semantic versioning.

## \[Unreleased]

### Added

* **Diagnostics** section in Mod Settings.

  * **Enable debug logs** toggle. When enabled (and RimWorld **Dev Mode** is ON), emits detailed `[QI]` traces for odds sampling, tier boosts, and context.
* **Settings UI localization** keys for:

  * Diagnostics header, Debug Logs toggle and tooltip.
  * Quality Log UI header, **Font**, **Row height scale**, **Reset column widths**, **Open quality log**.
  * Localized font menu items: Tiny / Small / Medium.
* **Helper** to open settings from code: `QualityInsightsMod.OpenSettings()`.

### Changed

* **Live Quality Odds** now computes a **baseline distribution** (sampling the real game method with inspiration suppressed), then applies tier shifts:

  * **Inspired Creativity** = +2 tiers (cap at Legendary).
  * **Production Specialist** = +1 tier.
  * Legendary mass is capped if not allowed and overflow moves into Masterwork.
* **Role detection** for **Production Specialist** made robust via null-safe reflection across Ideology variants; works reliably in UI and logs.
* **Caching** improved: odds cache now keys on pawn, recipe, **boost mask** (inspiration/role), and cheat flag—reducing unnecessary recalcs.

### Fixed

* Production Specialist not being recognized in some setups (now logs `ProdSpec=True` and shows “(+1 tier)” line in the odds window).
* Crash when opening the odds window on pawns with certain role trackers (guarded reflection, safer null handling).
* Potential inspiration side effects during sampling (temporarily clear inspiration & suppress vanilla messages while sampling).

### Performance

* Deterministic RNG seeding and tighter cache checks keep the odds window smooth while dragging/refreshing.

### Developer Notes

* **Debug output** is gated by **both** the mod’s *Enable debug logs* setting **and** RimWorld Dev Mode.
* Keys live in: `Languages/English/Keyed/QualityInsights.xml`.

  * If you use versioned `About/LoadFolders.xml`, ensure the updated XML is copied to the active folder (e.g., `1.6/Languages/...`).
  * Garbled/“accented” labels mean the keys weren’t found—copy the XML and use Dev Mode → *Reload language files*.

### Migration Notes

* Assets like `Languages/...` and textures are **not** in the DLL—remember to copy them into the mod folder you load in-game.
* Column widths can be reset via **Reset column widths** in settings if your stored layout looks off after updating.

---

## \[1.0.0] – Initial release (summary)

* Quality Log with CSV export.
* Live Quality Odds gizmo.
* Optional dev cheat (≥ threshold) that respects Legendary rules.
* Construction path support and duplicate log suppression.

---

*Tip: when we cut a release, replace **\[Unreleased]** with a version and date (e.g., `## [1.2.0] – 2025-08-20`) and start a fresh **\[Unreleased]** section above it.*
