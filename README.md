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

### Live Quality Odds – Work Tables **and Construction**

* **Work tables:** choose a recipe + pawn and see **full Awful → Legendary odds**.
* **Construction:** odds are available for **frames** *and* **blueprints** via a gizmo.
  Pick a constructor pawn and view construction quality odds.
* **Accurate skill resolver**
  * Uses recipe `workSkill` when present; otherwise infers: Construction (buildings), Artistic (CompArt/sculptures), Crafting (general).
* **Boost-aware odds**
  * **Inspired Creativity**: +2 tiers (caps at Legendary).
  * **Production Specialist** (Ideology): +1 tier.
  * Legendary is **capped post-shift** if not allowed; overflow mass goes to Masterwork.
* **Deterministic, cached sampling**
  * Sampling uses the real vanilla roll via Harmony; **cheat is always disabled during sampling** and inspiration side-effects are **suppressed** to keep the baseline clean.
  * Results are cached using a stable key (pawn, recipe/builtDef, boost mask, cheat flag) for snappy UI.
* **Dev-only validation**: “**Validate 100k**” button runs a large sample, compares vs. the UI, copies details to clipboard, and toasts a completion message.

### Construction Path Support

* Binds frame → completed building and attributes quality to the correct **builder pawn** (handles minified furniture).

### Optional Dev Cheat (threshold-based, safe)

* Mod setting: “always roll at least the highest tier whose probability ≥ threshold.”
* **Never biases the odds UI** (cheat is force-disabled during sampling).
* **Respects Legendary rules** (still requires inspiration/role to reach Legendary).
* Implemented as a **safe single-hop bump** via `SetQuality`, with a reentrancy guard and art init safety (ensures `CompArt` is initialized when needed).

### Diagnostics & Developer Quality-of-Life

* **Diagnostics** section in Mod Settings:
  * **Enable debug logs** (Dev Mode only) to emit detailed `[QI]` traces:
    * Flags (Inspired/ProdSpec, tier boost, mask).
    * Raw vs. final shifted distributions.
    * Context (pawn, skill, recipe/builtDef).
* Dev-only “**Validate 100k**” buttons in both odds windows (work tables & construction), with results copied to clipboard for easy sharing.

## How It Works (accuracy & safety)

* The odds UI samples the real `QualityUtility.GenerateQualityCreatedByPawn` via Harmony. During sampling we:
  * **Disable cheat**, **suppress inspiration side-effects**, and **strip inspiration** per roll to compute a true baseline.
  * Apply tier shifts once (+2 inspiration, +1 role) with a correct Legendary cap.
* Cheat uses a separate baseline estimation (also with cheat off / insp suppressed) and **only bumps one tier** safely if the target tier meets your threshold.
* **Real-life play time** is tracked by a lightweight component that accumulates seconds **only while the game is unpaused**. Each log entry stores a play-time snapshot; the UI shows *(currentAccum − snapshot)*.

## Settings

* **Enable quality logging** (table/log feature).
* **Enable live chances widget** (gizmo on worktables, frames & blueprints).
* **Enable dev cheat** + **Min Cheat Chance** slider (0%–20%).
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
* Select a **frame** (or **blueprint**) to use the **construction odds** gizmo; pick a constructor pawn to view tier odds.
* Configure options in **Mod Settings** (cheat threshold/samples, diagnostics, UI preferences).

## Compatibility & Notes

* Odds remain accurate with most quality-altering mods because we sample the actual roll.
* Legendary requires Inspired Creativity or the Production Specialist role; the cheat respects this.
* Construction quality is attributed to the builder pawn reliably, including minified furniture.
* Performance: sampled odds are cached; the log’s RL time is cheap (accumulator + UI diff). Column resizing persists and can be reset.

## Troubleshooting

* **Accented/garbled labels** → language keys weren’t found. Copy `Languages/English/Keyed/QualityInsights.xml` into the active version folder (per `About/LoadFolders.xml`) and use Dev Mode → *Reload language files*.
* **Columns won’t resize or revert unexpectedly** → click **Reset column widths** in settings to rebuild from defaults.

## Changelog (recent highlights)

* **New**: **Construction quality odds** gizmo on **frames & blueprints** (choose a pawn, see full Awful→Legendary odds).
* **New**: Dev-only **Validate 100k** buttons in both odds windows; results are copied to clipboard.
* **Improved**: Cheat is **isolated from sampling** (no bias); a **single-hop safe bump** upgrades results when threshold criteria are met, with reentrancy guards and art init safety.
* **Improved**: Deterministic seeding + smarter caching (keyed by pawn, recipe/builtDef, boost mask, cheat flag) for very fast UI refresh.
* **Improved**: Materials and worker attribution are more robust across minified items and construction completion.
* **Fixed**: Rare crash when dev cheat was enabled (now guarded by recursion shields, inspiration side-effect suppression, and clean sampling separation).
