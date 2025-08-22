# Changelog

All notable changes to **Quality Insights** will be documented in this file.
This project aims to follow [Keep a Changelog](https://keepachangelog.com/) conventions and semantic versioning.

## [Unreleased]

### Added
- **Construction quality odds**:
  - New gizmo on **Frames** *and* **Blueprints** to preview full Awful→Legendary odds before/while building.
  - Pawn picker for construction odds, mirroring the worktable window UX.
- **Dev validation tools**:
  - **Validate 100k** button in both odds windows (worktable + construction). Runs a large-N baseline, compares vs. UI, writes a detailed table to the log, and copies results to the clipboard with a toast.
- **Quality Log – Columns menu**:
  - Show/hide any column; two new raw columns **Item ID** and **Stuff ID** (default hidden).
- **Quality Log – Search niceties**:
  - **Inline clear (×)** button when the box is non-empty.
  - **Ctrl/Cmd+F** focuses the search box.
- **Quality Log – Copy helpers (context menu)**:
  - Right-click a row → **Copy row (friendly)**, **Copy row (raw)**, **Copy Item defName**, **Copy Stuff defName(s)**.
- **Quality Log – Row count**:
  - Bottom-left **“X of Y shown”** indicator that updates with filters/search.
- **Quality Log – Footer reset**:
  - **Reset widths** button that calls the same reset as Mod Settings.
- **Floating window polish**:
  - Smaller top margin and a larger draggable strip.

### Changed
- **Cheat isolation & safety**:
  - Cheat evaluation samples a **true baseline** (cheat force-disabled, inspirations stripped, side-effects suppressed), then applies a **single-hop safe bump** if the chosen tier meets your threshold.
  - Legendary rules still apply (requires inspiration or Production Specialist).
  - `CompArt` is initialized when needed before bumping to avoid null art data.
- **Deterministic, faster odds**:
  - Stable seeding and **smarter caching** keyed by pawn, recipe/**builtDef**, boost mask, and the **cheat flag** for consistently snappy UI.
- **Skill resolution** is unified:
  - Uses `recipe.workSkill` when present; otherwise: **Construction** (buildings), **Artistic** (CompArt/sculptures), else **Crafting**. Applied consistently in odds and logging.
- **Materials/worker tracking**:
  - Improved capture during `MakeRecipeProducts/PostProcessProduct` and construction completion, including **minified** items and furniture.
- **CSV & table**:
  - Header includes `PlayTime`; export path has **Open folder** and auto-prunes by count/size (settings).

### Fixed
- **Search clear button** now correctly clears the text (click was previously intercepted by the text field).
- **Crash with dev cheat enabled** in rare re-entrancy/recursion paths:
  - Guarded with an internal **re-entrancy shield**, an **_inCheatEval** recursion gate, and **inspiration side-effect suppression** during sampling/cheat evaluation.
- **Duplicate log entries**:
  - Tightened duplicate suppression window to avoid spurious repeats when multiple `SetQuality` calls land quickly.
- Additional null/edge guards around parent things, minified inner things, and delayed art initialization.

### Performance
- Odds sampling remains **deterministic** and cached; construction odds share the same fast path.
- Log “Real-life Play Time” remains lightweight (accumulator advances **only while unpaused**; rows render a simple `(now − stamp)` diff).
- Weak references avoid retaining built things longer than needed; periodic cleanup trims old dedupe entries.

### Developer Notes
- **Debug logs** require **both** RimWorld **Dev Mode** and the mod’s **Enable debug logs** setting.
- Diagnostic traces include:
  - Boost flags (Inspired/ProdSpec), computed tier boost, and mask.
  - Raw baseline vs. final (boost-shifted) distributions.
  - Context (pawn, skill, recipe/builtDef).
- Validation output (100k) provides per-tier comparisons and a max absolute delta summary, copied to the clipboard for easy sharing.

### Migration Notes
- If your saved column layout predates the newer columns/UI options, use **Reset column widths** (footer or Mod Settings) once.
- Assets (`Languages/...`, textures) are not embedded in the DLL—keep them in your mod folder tree.
- No data migration is required for existing saves; cheat/odds logic changes are internal and backward-compatible.

---

## [1.0.0] – Initial release (summary)
- Quality Log with CSV export.
- Live Quality Odds gizmo.
- Optional dev cheat (≥ threshold) that respects Legendary rules.
- Construction path support and duplicate log suppression.

---

*Tip: when cutting a release, replace **[Unreleased]** with a version/date (e.g., `## [1.2.0] – 2025-08-21`) and start a fresh **[Unreleased]** section above it.*
