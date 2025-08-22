# Changelog

All notable changes to **Quality Insights** will be documented in this file.
This project aims to follow [Keep a Changelog](https://keepachangelog.com/) conventions and semantic versioning.

## [Unreleased]

### Added
- **Quality Log – Responsive header/footer + overflow (“⋯ More”)**
  - Buttons **auto-size** to fit labels and avoid overlap on small windows or large UI scales.
  - The **search field shrinks first**; lower-priority actions flow into a **“⋯ More”** menu.
  - Header overflow may include: **Reset filters**, **Columns**, **Pop out/Dock**.  
    Footer overflow may include: **Open folder**, **Reset widths**, **Export CSV**, **Settings** (as needed).
  - Scoped **only** to the Quality Log (docked and popped out).
- **Quality Log – Reset filters button**:
  - One-click clear of **Search**, **Quality**, and **Skill**. Persists the cleared state and focuses the search box.
- **Quality Log – Materials-aware search**:
  - Search now matches **Stuff** *and* per-ingredient **materials** for multi-material items.
  - Matches both **raw defNames** and **friendly labels** (e.g., “Steel + Plasteel”).
- **Notification silencing (Masterwork / Legendary)**:
  - New Mod Settings toggles: **Silence Masterwork**, **Silence Legendary**.
  - Suppresses **bottom-left toasts** (`Messages.Message`) **and** **right-side Letters** (blue mail) for the specific produced/built thing.
  - Robust matching via same-tick product IDs, including **minified** inner things.
  - Dev logs (when enabled) report both the “mark for suppression” and the actual message/letter blocks.
- **Persistent filters for the Quality Log**:
  - **Search**, **Quality**, and **Skill** filters are **saved** to settings and **restored** when reopening the log (works in both docked and floating windows).
  - Updates persist in-memory immediately; settings are flushed on window close.
- **Reset ALL settings** button in Mod Settings (restores every QI setting).
- **Construction quality odds**:
  - Gizmo on **Frames** *and* **Blueprints** to preview full Awful→Legendary odds before/while building.
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
  - Bottom-left **“X of Y shown”** indicator.
- **Quality Log – Footer reset**:
  - **Reset widths** button that calls the same reset as Mod Settings.
- **Floating window polish**:
  - Smaller top margin and a larger draggable strip.

### Changed
- **Quality Log buttons are now adaptive**:
  - Button widths are measured from labels at runtime; overlap is prevented via responsive layout and overflow.
  - Preference is given to keep **Settings** and **Export CSV** visible in the footer when possible.
- **Cheat isolation & safety**:
  - Cheat evaluation samples a **true baseline** (cheat force-disabled, inspirations stripped, side-effects suppressed), then applies a **single-hop safe bump** if the chosen tier meets your threshold.
  - Legendary rules still apply (requires Inspired Creativity or Production Specialist).
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
- **UI overlap** in the Quality Log header/footer when shrinking the window or using larger fonts — controls now adapt or overflow to **“⋯ More”**.
- **Notification silencing** now correctly targets both Messages and Letters via patched overloads. Debug traces identify what was blocked.
- **Search clear button** now reliably clears the text even when the text field had focus.
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
  - **Suppressed MESSAGE/LETTER** lines when notification silencing is active.
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
