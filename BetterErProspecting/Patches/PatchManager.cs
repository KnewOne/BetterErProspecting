using System.Linq;
using BetterErProspecting.Config;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches;

public static class PatchManager {
    private static Harmony harmony => BetterErProspect.harmony;
    private static ICoreAPI Api => BetterErProspect.Api;
    private static string modId => BetterErProspect.ModId;

    public static void handle() {
        PatchUnpatch();
    }


    private static void PatchUnpatch() {
        harmony.UnpatchAll(modId);
        harmony.PatchAllUncategorized();
        handleProspectTogether();

        if (ModConfig.Instance.NewDensityMode) {
            harmony.PatchCategory(nameof(PatchCategory.NewDensity));
        }

        if (ModConfig.Instance.StoneSearchCreatesReadings) {
            harmony.PatchCategory(nameof(PatchCategory.StoneReadings));
        }
    }

    private static void handleProspectTogether() {
        if (!Api.ModLoader.IsModEnabled("prospecttogether")) return;

        var original = AccessTools.Method(typeof(OreMapLayer), nameof(OreMapLayer.OnDataFromServer));

        var info = Harmony.GetPatchInfo(original);
        if (info?.Prefixes != null) {
            foreach (var patch in info.Prefixes.ToList()) {
                if (patch.owner == "prospecttogether") {
                    harmony.Unpatch(original, patch.PatchMethod);
                }
            }
        }

        harmony.PatchCategory(nameof(PatchCategory.ProspectTogetherCompat));
    }

    public enum PatchCategory {
        NewDensity,
        StoneReadings,
        ProspectTogetherCompat
    }
}
