// Source/QualityInsights/Construction/ConstructionMatsCapture.cs
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace QualityInsights.Construction
{
    /// <summary>
    /// Captures materials from a Frame (Stuff + resourceContainer contents) at completion,
    /// then associates them with the newly-built thing so the SetQuality logger can attach them.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ConstructionMatsCapture
    {
        // thingIDNumber -> distinct defNames of materials used
        private static readonly Dictionary<int, List<string>> _matsByThingId = new();
        private static readonly object _lock = new();

        public static List<string>? ConsumeFor(Thing t)
        {
            if (t == null) return null;
            lock (_lock)
            {
                if (_matsByThingId.TryGetValue(t.thingIDNumber, out var list))
                {
                    _matsByThingId.Remove(t.thingIDNumber);
                    return list;
                }
                return null;
            }
        }

        private static void Remember(Thing built, IEnumerable<string> mats)
        {
            if (built == null) return;
            var distinct = mats?.Where(s => !string.IsNullOrEmpty(s))
                                .Distinct(StringComparer.Ordinal)
                                .ToList() ?? new List<string>();
            lock (_lock) _matsByThingId[built.thingIDNumber] = distinct;
        }

        [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
        public static class Patch_Frame_CompleteConstruction
        {
            // Grab the materials BEFORE the frame clears/destroys its container.
            public static void Prefix(Frame __instance, Pawn worker, out List<string> __state)
            {
                var tmp = new List<string>();
                try
                {
                    // Include chosen Stuff (e.g., Wood)
                    if (__instance.Stuff != null)
                        tmp.Add(__instance.Stuff.defName);

                    // Include extra costList items (Gold, Steel, Components, etc.)
                    // Frame.resourceContainer is a private field.
                    var rcField = AccessTools.Field(typeof(Frame), "resourceContainer");
                    var rc = rcField?.GetValue(__instance) as ThingOwner<Thing>;
                    if (rc != null)
                    {
                        foreach (var t in rc)
                            if (t?.def != null)
                                tmp.Add(t.def.defName);
                    }
                }
                catch { /* best-effort capture */ }

                __state = tmp;
            }

            // After completion, find the built thing at the same cell and attach the mats list.
            public static void Postfix(Frame __instance, Pawn worker, List<string> __state)
            {
                if (__state == null || __state.Count == 0) return;
                try
                {
                    var map = __instance.Map;
                    if (map == null) return;

                    // Prefer the thing with a quality comp that appeared at the frame's position.
                    Thing built = __instance.Position.GetThingList(map)
                        .FirstOrDefault(t => t != null && (t.TryGetComp<CompQuality>() != null));

                    // Fallback: any new building occupying the cell.
                    built ??= __instance.Position.GetThingList(map)
                        .FirstOrDefault(t => t is Building);

                    if (built != null)
                        Remember(built, __state);
                }
                catch { /* non-fatal */ }
            }
        }
    }
}
