# Quality Insights

Quality log + live quality odds + optional dev cheat (≥ threshold) for RimWorld 1.4 / 1.5 / 1.6.

## Features

### Quality Log (Table & Log Views)

* Top-bar button opens a searchable, filterable log of all quality roll outcomes.
* **Pop out / Dock toggle**  
  One button switches between a docked main-tab window and a floating window. Opening one closes the other, so only one is ever visible.
* **Resizable table UI**
  * **Sortable columns** (click header).
  * **Resizable columns** (drag splitters) with **persisted layout** in mod settings.
  * **Reset column widths** in Mod Settings (works for both docked & popped windows).
  * **Zebra striping & hover highlights** for readability.
  * **Dynamic last column** auto-fills remaining width.
* **Time columns (2):**
  * **Time** — in-game “time ago” (e.g. `2d 4h`).
  * **RL** — **real-life play time elapsed** since the log entry (e.g. `1h 12m`).  
    *Ignores time spent paused; updates live while unpaused.*
* Records: item, maker pawn, skill used, final quality, inspiration/role flags, and **materials** (distinct list with icons where available; otherwise shows Stuff).
* **Duplicate suppression** (only one entry per thing even if multiple `SetQuality` calls land).
* **CSV export** (+ **Open folder** button) with a **PlayTime** column matching the RL display.
* Export folder **auto-prunes** by count/size (configurable).

### Live Quality Odds Gizmo

* Appears on any work table with quality-producing recipes.
* Choose a recipe + pawn and see **full Awful → Legendary odds**.
* **Accurate skill resolver**
  * Uses recipe `workSkill` when present; otherwise infers: Construction (buildings), Artistic (CompArt/sculptures), Crafting (general).
* **Boost-aware odds**
  * **Inspired Creativity**: +2 tiers (caps at Legendary).
  * **Production Specialist** (Ideology): +1 tier.
  * Legendary is **capped post-shift** if not allowed; overflow mass goes to Masterwork.
* **Sampling uses the real game roll** (Harmony) for compatibility.
* **Deterministic caching** keyed by pawn/recipe/boost mask/cheat flag for snappy UI.

### Construction Path Support
* Hooks frames → completed buildings and attributes quality to the builder (handles minified furniture).

### Optional Cheat Mode
* Mod setting: “always roll at least the lowest tier whose probability ≥ threshold.”
* **Respects Legendary rules** (still requires inspiration/role to reach Legendary).
* Never affects the sampling that powers the odds UI.

### Diagnostics & Developer Quality-of-Life
* **Diagnostics** section in Mod Settings:
  * **Enable debug logs** toggle (Dev Mode only) to emit detailed `[QI]` traces:
    * Flags (Inspired/ProdSpec, tier boost, mask).
    * Raw vs. final shifted distributions.
    * Context lines (pawn, skill, recipe).

## How It Works (accuracy & safety)

* The odds UI samples the real `QualityUtility.GenerateQualityCreatedByPawn` via Harmony, with inspirations suppressed during sampling and the cheat disabled—then applies the **exact** tier shifts (+2 inspiration, +1 role) once, with a proper Legendary cap.
* **Real-life play time** is tracked by a lightweight component that accumulates seconds **only while the game is unpaused**. Each log entry stores a play-time snapshot; the UI simply shows *(currentAccum − snapshot)*.  
  Zero per-row work; the value is computed once per repaint.

## Settings

* **Enable quality logging** (table/log feature).
* **Enable live chances widget** (gizmo on worktables).
* **Enable dev cheat** + threshold slider.
* **Estimation samples** slider (performance/precision trade-off).
* **Diagnostics → Enable debug logs** (verbose `[QI]` logs; Dev Mode only).
* **Quality Log UI**
  * Font: Tiny / Small / Medium.
  * Row height scale.
  * **Reset column widths** (reverts to sensible defaults).
  * **Open quality log** (quick access).

> **Localization**: strings live in `Languages/English/Keyed/QualityInsights.xml`.  
> If you use versioned load folders (e.g. `1.5/` or `1.6/` in `About/LoadFolders.xml`), ensure the updated XML is copied to the **active** folder (`<modroot>/<version>/Languages/...`).

## Build

1. Open `Source/QualityInsights/QualityInsights.csproj` or run `dotnet build`.
2. Set `RimWorldManaged` to your `RimWorld*_Data/Managed` path (or edit the csproj property).
3. Build. The DLL is copied to `Assemblies/QualityInsights.dll`.

> **Note**: assets like `Languages/…` and textures are **not** compiled into the DLL; keep them in the mod folder you load in-game.

## Install

Copy the mod folder to `RimWorld/Mods/QualityInsights` (ensure `Assemblies/QualityInsights.dll` exists). Enable in the mod list.

## Use

* Click the **Quality Log** button to open the table (sort, resize, export). Use **Pop out/Dock** to switch window mode.
* Select a **work table**, click the **Quality odds** gizmo, pick a recipe + pawn to view tier odds.
* Configure options in **Mod Settings** (cheat threshold/samples, diagnostics, UI preferences).

## Compatibility & Notes

* Odds remain accurate with most quality-altering mods because we sample the actual roll.
* Legendary requires Inspired Creativity or the Production Specialist role; the cheat respects this.
* Construction quality is attributed to the builder pawn reliably.
* Performance: sampled odds are cached; the log’s RL time is cheap (accumulator + UI diff). Column resizing persists without “snap-back” and can be reset.

## Troubleshooting

* **Accented/garbled labels** → language keys weren’t found. Copy `Languages/English/Keyed/QualityInsights.xml` into the active version folder (per `About/LoadFolders.xml`) and use Dev Mode → *Reload language files*.
* **Columns won’t resize or revert unexpectedly** → click **Reset column widths** in settings to rebuild from defaults.

## Changelog (recent highlights)

* **New**: **Real-life Play Time (RL)** column in the table, RL time shown inline in Log view, and **PlayTime** in CSV export. Ignores paused time.
* **New**: **Pop out / Dock** toggle with single-instance behavior; floating window has a dedicated drag bar and clamps on-screen.
* **Improved**: Column layout persistence—no snap-back on mouse-up; resetting now reliably updates all instances.
* **Improved**: Materials display shows up to 3 material icons + full list tooltip; falls back to Stuff when no mats exist.
* **Fixed**: Assorted compile guards & null-safety around game events and role detection.
