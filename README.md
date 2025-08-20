# Quality Insights

Quality log + live quality odds + optional dev cheat (≥ threshold) for RimWorld 1.4 / 1.5 / 1.6.

## Features

### Quality Log (Table View)

* Top-bar button opens a searchable, filterable log of all quality roll outcomes.
* **Resizable table UI**

  * **Sortable columns** (click header).
  * **Resizable columns** (drag splitters) with **persisted layout** in mod settings.
  * **Zebra striping & hover highlights** for readability.
  * **Dynamic last column** auto-fills the remaining width.
* Records: item, maker pawn, skill used, final quality, inspiration/role flags, and materials (where available).
* **Duplicate suppression** (only one entry per thing even if multiple SetQuality calls land).
* **CSV export** and a quick summary distribution.

### Live Quality Odds Gizmo

* Appears on any work table with quality-producing recipes.
* Choose a recipe + pawn and see **full Awful → Legendary odds**.
* **Accurate skill resolver**

  * Uses recipe `workSkill` when present, otherwise infers: Construction (buildings), Artistic (CompArt/sculptures), Crafting (general).
* **Boost-aware odds**

  * **Inspired Creativity**: +2 tiers (caps at Legendary).
  * **Production Specialist** (Ideology): +1 tier.
  * Legendary is **capped post-shift** if not allowed; excess mass flows into Masterwork.
* **Sampling is from the real game roll** for mod compatibility.
* **Deterministic caching** keyed by pawn/recipe/boost mask/sample count for snappy UI.

### Construction Path Support

* Hooks frames → completed buildings and binds the created thing back to the builder.
* Attributes Construction correctly.
* Handles minified furniture.

### Optional Cheat Mode

* Mod setting: “always roll at least the lowest tier whose probability ≥ threshold.”
* **Respects Legendary rules** (still requires inspiration/role to reach Legendary).
* Never affects the sampling that powers the odds UI.

### Diagnostics & Developer Quality-of-Life

* New **Diagnostics** section in Mod Settings:

  * **Enable debug logs** toggle. When on (and Dev Mode is enabled), emits detailed `[QI]` traces:

    * Flags line (Inspired/ProdSpec, tier boost, mask).
    * Raw vs. final shifted distributions.
    * Context lines (pawn, skill, recipe).
* Localized settings UI (English keys provided).

## How It Works (accuracy & safety)

* The odds UI samples the real `QualityUtility.GenerateQualityCreatedByPawn` via Harmony hooks.
* During sampling we **suppress inspirations & their side effects** and **disable the cheat**, so the baseline is clean. Afterwards we apply the **exact tier shifts** (+2 inspiration, +1 Production Specialist) once, in a controlled way.
* Legendary mass is capped and redirected if not permitted by pawn state.

## Settings

* **Enable quality logging** (table feature).
* **Enable live chances widget** (gizmo on worktables).
* **Enable dev cheat** + threshold slider.
* **Estimation samples** slider (performance/precision trade-off).
* **Diagnostics → Enable debug logs** (verbose `[QI]` logs, Dev Mode only).
* **Quality Log UI**

  * Font: Tiny / Small / Medium.
  * Row height scale.
  * Reset column widths.
  * Open quality log.

> **Localization**: strings live in `Languages/English/Keyed/QualityInsights.xml`.
> If you use versioned load folders (e.g. `1.5/` or `1.6/` in `About/LoadFolders.xml`), make sure the updated XML is copied to the **active** folder (`<modroot>/<version>/Languages/...`). “Garbled/accented” labels mean the keys weren’t found.

## Build

1. Open `Source/QualityInsights/QualityInsights.csproj` or run `dotnet build`.
2. Set `RimWorldManaged` to your `RimWorld*_Data/Managed` path (or edit the csproj property).
3. Build. The DLL is copied to `Assemblies/QualityInsights.dll`.

> **Note**: assets like `Languages/…` and textures are **not** compiled into the DLL. Copy/update them in the mod folder you load in-game.

## Install

Copy the mod folder to `RimWorld/Mods/QualityInsights` (ensure `Assemblies/QualityInsights.dll` exists). Enable in the mod list.

## Use

* Click the **Quality log** main button to open the table (sort, resize, export).
* Select a **work table**, click the **Quality odds** gizmo, pick a recipe + pawn to view odds across all tiers.
* Configure options in **Mod Settings** (cheat threshold/samples, diagnostics, UI preferences).

## Compatibility & Notes

* Odds remain accurate with most quality-altering mods because we sample the actual roll.
* Legendary requires Inspired Creativity or the Production Specialist role; the cheat respects this.
* Construction quality is attributed to the builder pawn reliably.
* Performance: sampled odds are cached per pawn/recipe/boost state; sliders and window dragging stay smooth.

## Troubleshooting

* **Accented/garbled labels** → language keys weren’t found. Copy `Languages/English/Keyed/QualityInsights.xml` into the folder listed in `About/LoadFolders.xml` (e.g. `1.6/…`) and use Dev Mode → *Reload language files*.
* **Too many log lines** → disable “Enable debug logs” (or turn off Dev Mode). Debug output is gated by both.

## Changelog (recent)

* **Production Specialist support fixed & reliable** (role detection via robust reflection; UI shows “(+1 tier)” and logs show `ProdSpec=True`).
* **No-side-effects sampling** (temporarily clears inspiration during sampling, suppresses vanilla inspiration start/end).
* **Tier-shift pipeline**: compute baseline, then apply +2 (inspiration) and/or +1 (role), then Legendary cap → Masterwork spillover.
* **Diagnostics setting** added (`Enable debug logs`), with localized strings and a dedicated section header.
* **Localized settings UI** (`Font`, `Row height scale`, `Reset column widths`, `Open quality log`, etc.).
* **Deterministic caching** keyed by pawn/recipe/boost mask/cheat flag to prevent stale UI.
* **Crash guard improvements** in role detection (null-safe reflection across Ideology variants).
