using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches.Stone;

[HarmonyPatch(typeof(OreMapLayer), "ShouldDisplayReading")]
// Always because stone readings are still in save files
[HarmonyPatchCategory(nameof(BetterErProspect.PatchCategory.Always))]
public class OreMapRebuildShouldDisplayReadingPatch {
    private static readonly MethodInfo GetTotalFactorMethod = AccessTools.Method(typeof(PropickReading), "GetTotalFactor");

    private static double GetTotalFactor(PropickReading reading, string code) =>
        (double)GetTotalFactorMethod.Invoke(reading, new object[] { code });

    static bool Prefix(OreMapLayer __instance, PropickReading reading, ref bool __result) {
        var filterByOreCode = (string)AccessTools.Field(typeof(OreMapLayer), "filterByOreCode").GetValue(__instance);

        // Hide rock-only readings if stone readings are disabled
        if (!BetterErProspect.Config.StoneSearchCreatesReadings && reading.OreReadings.Keys.All(k => k.StartsWith("rock-"))) {
            __result = false;
            return false;
        }

        switch (filterByOreCode) {
            case "ore-":
                __result = reading.OreReadings.Keys.Any(k => !k.StartsWith("rock-") && GetTotalFactor(reading, k) > PropickReading.MentionThreshold);
                return false;
            case "rock-":
                __result = reading.OreReadings.Keys.Any(k => k.StartsWith("rock-") && GetTotalFactor(reading, k) > PropickReading.MentionThreshold);
                return false;
            default:
                return true; // Let base handle null and exact key matches
        }
    }
}
