# Changelog

All notable changes to **Quality Insights** will be documented in this file.
This project aims to follow [Keep a Changelog](https://keepachangelog.com/) conventions and semantic versioning.

## [Unreleased]

### Added

- **TBD**

---

## [1.0.2] – Fix For Logging Bug After Stripped Item Fix – 2025-08-24

### Fixed

- Stopped **stripped/equipped world-spawned apparel** from being logged as player creations by gating `SetQuality` to real creation contexts (recipe/construction) and preferring the bound worker.
- Restored **construction logging** that regressed in 1.0.1 by deferring roll-state cleanup so the builder/skill persist until `AfterSetQuality`.
- Fixed occasional **“Lvl 0” / wrong skill** on crafted items by keeping the roll skill alive through `SetQuality` (no more fallback to heuristics).
- Minor debug-log polish for construction: clearer “prefix” and “mats bound” traces.

---

## [1.0.1] – Fix For Stripped Items – 2025-08-24

### Fixed

- Fixed false-positive log entries when pawns **strip/equip** items or when items **spawn with preset quality** (e.g., from world pawns). These were previously recorded as new crafts/constructions.
- Added strict **creation-context verification** around `SetQuality` (only log during a real vanilla roll, an active construction run, or a recipe product with a bound worker).
- Prefer the **bound worker** (from `PostProcessProduct` / construction) to avoid misattributing quality to the wrong pawn; consume the binding once used.
- Hardened **materials capture** for construction frames and recipe products, and improved cache cleanup to prevent cross-item leakage.
- Scoped and cleared roll state to prevent stale inspiration/skill data from bleeding into unrelated items.
- Minor debug-log polish. No gameplay changes; safe to update mid-save.


---

## [1.0.0] – Initial release – 2025-08-24

### Added
- **Quality Log (table & log views)**
  - Searchable, filterable history of quality roll outcomes.
  - **Pop out / Dock** toggle (only one visible at a time).
  - **Floating window polish**: smaller top margin and a larger draggable strip.
  - **Resizable table UI**: sortable columns, drag splitters with persisted layout, **Columns menu** to show/hide columns (with hidden-by-default **Item ID** and **Stuff ID** raw columns), zebra striping/hover highlights, dynamic last column fill.
  - **Responsive header/footer (no overlap)**: buttons auto-size; search field shrinks first; low-priority actions flow into a **“⋯ More”** overflow when space is tight. (Scoped only to the Quality Log, both docked and floating.)
  - **Search & filters**:
    - Inline clear (×), **Ctrl/Cmd+F** to focus.
    - **Quality** and **Skill** dropdowns.
    - **Reset filters** button clears Search/Quality/Skill and persists the cleared state.
    - **Materials-aware search**: matches **Stuff** and per-ingredient **materials** for multi-mat items by **raw defNames** and **friendly labels**.
    - **Persistent filters**: Search/Quality/Skill are remembered between sessions (docked or floating).
  - **Copy helpers (row context menu)**: Copy row (friendly/raw), Item defName, Stuff defName(s).
  - **Row count status**: “**X of Y shown**”.
  - **Time columns**: in-game **Time** and **RL** (real-life play time since entry; ignores pause; updates while unpaused).
  - **CSV export** with **PlayTime** column + **Open folder** button; export folder auto-prunes by count/size (configurable).
  - **Duplicate suppression**: one entry per thing even if multiple `SetQuality` calls land.
- **Live Quality Odds – Work tables & Construction**
  - Work tables: select recipe + pawn for full **Awful→Legendary** odds.
  - **Construction**: gizmo on frames and blueprints; pick constructor pawn for build quality odds.
  - **Accurate skill resolver**: `recipe.workSkill` when present; otherwise Construction (buildings), Artistic (CompArt/sculptures), Crafting (general).
- **Optional Dev Cheat (threshold-based, safe)**
  - “Always roll at least the highest tier whose probability ≥ threshold.”
  - Respects Legendary requirements; never biases the odds UI.
- **Notifications (optional silencing)**
  - Mod Settings toggles: **Silence Masterwork**, **Silence Legendary**.
  - Suppresses both bottom-left toasts and right-side Letters for the specific crafted/built thing; robust for minified items and same-tick events.
- **Diagnostics & Dev QoL**
  - **Enable debug logs** (Dev Mode) for rich `[QI]` traces (flags, tier shifts, baseline vs. shifted distributions, context, suppression traces).
  - **Validate 100k** buttons in both odds windows: run large-N baseline, compare vs UI, copy report to clipboard, toast completion.
- **Settings**
  - Toggle logging & live chances widgets; enable cheat + **Min Cheat Chance** slider; **Estimation samples** slider.
  - **Quality Log UI**: font (Tiny/Small/Medium), row height scale, **Reset column widths**, **Open quality log**, **Reset ALL settings**.
  - **Retention**: prune by age and/or entry count.
  - **Exports**: cap by file count and/or MB.
- **Delete row** action in the Quality Log (right-click context menu).
  - **Hold Shift** while clicking to **skip the confirmation** dialog.
- **Ingredient tracking for construction**: log entries for constructed structures now capture **additional ingredients** consumed by the build (beyond Stuff). These appear in the **Materials** field, participate in **search**, and are included in **CSV exports**.

### Changed
- **Cheat isolation & safety**
  - Cheat evaluation uses a true **baseline** (cheat force-disabled, inspirations stripped, side-effects suppressed), then applies a **single-hop** bump only when the target tier meets the threshold; Legendary rules still apply.
  - Ensures `CompArt` is initialized before bumping to prevent null art data.
- **Deterministic, faster odds**
  - Stable seeding and caching keyed by pawn, recipe/**builtDef**, boost mask, and cheat flag.
- **Skill resolution & materials/worker tracking**
  - Unified skill inference and improved capture during `MakeRecipeProducts/PostProcessProduct` and construction completion (handles minified furniture).
- **CSV & table**
  - CSV includes `PlayTime`; export UI includes **Open folder** and auto-pruning configuration.
- **Menu option priority**: use `MenuOptionPriority.High` for the destructive **Delete row** option (replaces the older `DangerOption` value for broader compatibility).
- **Shift detection** for delete: uses IMGUI’s `Event.current` modifiers (no dependency on `UnityEngine.Input`), improving compatibility with player builds and compile targets.

### Fixed
- **UI overlap prevention** in the Quality Log header/footer on small windows or large UI scales via responsive sizing and **“⋯ More”** overflow.
- **Search clear** consistently clears text even while the field has focus.
- **Robustness with dev cheat enabled** in rare re-entrancy/recursion paths:
  - Re-entrancy shield, `_inCheatEval` gate, and inspiration side-effect suppression during sampling/evaluation.
- **Duplicate log entries** further reduced via tighter suppression window.
- Additional null/edge guards around parent things, minified inners, and delayed art initialization.
- Minor null-safety and UI consistency around the new row-deletion flow.

### Performance
- Odds sampling remains deterministic and cached; construction shares the same fast path.
- RL play-time tracking is lightweight (accumulator only while unpaused; UI renders a simple diff).
- Weak references + periodic cleanup avoid retaining built things and stale dedupe entries.

### Developer Notes
- **Debug logs** require **both** RimWorld **Dev Mode** and **Enable debug logs** in Mod Settings.
- Validation output (100k) provides per-tier comparisons and a max absolute delta summary; copied to clipboard for easy sharing.
- Confirmed to work on RimWorld 1.6. Should also work on 1.4 and 1.5, but is untested.

---

*Tip: when cutting a release, replace **[Unreleased]** with a version/date (e.g., `## [1.2.0] – 2025-08-25`) and start a fresh **[Unreleased]** section above it.*
