# Quality Insights

Quality log + live quality odds + optional dev cheat (≥ threshold) for RimWorld 1.4/1.5/1.6.

## Features

* **Quality Log**

  * New top-bar button opens a log of all quality roll outcomes.
  * Tracks who created the item, with what skill, and the final quality.
  * Duplicate suppression ensures one entry per item, even if multiple late SetQuality calls occur.

* **Live Quality Odds Gizmo**

  * Appears on any work table with quality-producing recipes.
  * Lets you pick a recipe and pawn to see Excellent/Masterwork/Legendary chances.
  * Resolves the correct skill dynamically:

    * Construction for buildings/furniture.
    * Artistic for sculptures and CompArt items.
    * Crafting for weapons, apparel, and general bills.
    * Uses recipe’s explicit workSkill if defined.
  * Accounts for inspirations and roles:

    * **Inspired Creativity** guarantees Masterwork (Legendary if any chance > 0).
    * **Production Specialist** (Ideology) shifts results +1 tier.
  * Probabilities sampled from the **actual RimWorld quality roll function**, so they stay accurate with mods.

* **Construction Path Support**

  * Hooks into building frames → completed buildings.
  * Correctly attributes the builder pawn and Construction skill.
  * Handles minified furniture correctly.

* **Cheat Mode (optional)**

  * Configurable in Mod Settings.
  * Forces final quality ≥ the lowest tier with probability ≥ threshold.
  * Respects vanilla rules: Legendary only with inspiration/role.

* **Robust Modded Recipe Handling**

  * Gracefully infers skill when recipes don’t define one.
  * Heuristics ensure modded quality items are included (e.g., modded weapons, statues).

* **Debug & Logging Enhancements**

  * Clear `[QI]` log lines for prefix/postfix, SetQuality, and duplicate suppression.
  * Helps modders verify which pawn/skill was used.

## Build

1. Open `Source/QualityInsights/QualityInsights.csproj` in Visual Studio or `dotnet build`.
2. Set environment variable `RimWorldManaged` to your `RimWorld*Data/Managed` path (or edit the csproj `RimWorldManaged` property).
3. Build. The DLL is copied to `../Assemblies/QualityInsights.dll`.

## Install

Drop the entire mod folder into `RimWorld/Mods/QualityInsights`. Ensure `Assemblies/QualityInsights.dll` exists. Enable the mod.

## Use

* A new **Quality log** main button appears in the top bar.
* Select any **work table** to see a **Quality odds** gizmo; click it to choose a recipe and pawn to view Excellent/Masterwork/Legendary estimates.
* In **Mod Settings**, you can enable the dev cheat and adjust the probability threshold and sample count.

## Notes

* Odds are estimated by sampling the **actual game method** that rolls quality, so they automatically stay correct with most quality-altering mods.
* Legendary is only allowed if the pawn has Inspired Creativity or the Production Specialist role; the cheat respects this rule.
* Handles both crafting and construction quality reliably, with proper pawn/skill attribution.
