using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches.Stone;

[HarmonyPatch(typeof(OreMapComponent), "GetComponentColor")]
[HarmonyPatchCategory(nameof(BetterErProspect.PatchCategory.StoneReadings))]
// Adds rock- and ore- grouped readings dropdown option handling
public class OreMapComponentColorPatch {
    static bool Prefix(OreMapComponent __instance) {
        var filterByOreCode = __instance.filterByOreCode;

        // Only handle special prefix filters - let original run for normal cases
        if (filterByOreCode != "ore-" && filterByOreCode != "rock-")
            return true;

        // Calculate the highest reading for the prefix filter
        double highestFactor = 0;
        foreach (var kvp in __instance.Reading.OreReadings) {
            bool matches = filterByOreCode == "rock-"
                ? kvp.Key.StartsWith("rock-")
                : !kvp.Key.StartsWith("rock-");

            if (matches && kvp.Value.TotalFactor > highestFactor)
                highestFactor = kvp.Value.TotalFactor;
        }

        var colorVec = new Vec4f();
        int color = GuiStyle.DamageColorGradient[(int)Math.Min(99.0, highestFactor * 150.0)];
        ColorUtil.ToRGBAVec4f(color, ref colorVec);
        colorVec.W = 1f;
        AccessTools.Field(typeof(OreMapComponent), "color").SetValue(__instance, colorVec);

        return false; // Skip original
    }
}
