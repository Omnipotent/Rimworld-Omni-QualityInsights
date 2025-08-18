# Quality Insights

Quality log + live quality odds + optional dev cheat (≥ threshold) for RimWorld 1.4/1.5/1.6.

## Build

1. Open `Source/QualityInsights/QualityInsights.csproj` in Visual Studio or `dotnet build`.
2. Set environment variable `RimWorldManaged` to your `RimWorld*Data/Managed` path (or edit the csproj `RimWorldManaged` property).
3. Build. The DLL is copied to `../Assemblies/QualityInsights.dll`.

## Install

Drop the entire mod folder into `RimWorld/Mods/QualityInsights`. Ensure `Assemblies/QualityInsights.dll` exists. Enable the mod.

## Use

- A new **Quality log** main button appears in the top bar.
- Select any **work table** to see a **Quality odds** gizmo; click it to choose a recipe and pawn to view Excellent/Masterwork/Legendary estimates.
- In **Mod Settings**, you can enable the dev cheat and adjust the probability threshold and sample count.

## Notes

- Odds are estimated by sampling the **actual game method** that rolls quality, so they automatically stay correct with most quality-altering mods.
- Legendary is only allowed if the pawn has Inspired Creativity or the Production Specialist role; the cheat respects this rule.
