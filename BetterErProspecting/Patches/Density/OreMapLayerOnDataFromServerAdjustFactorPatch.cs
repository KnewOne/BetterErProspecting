using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BetterErProspecting.Tracking;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches.Density;

[HarmonyPatch(typeof(OreMapLayer), nameof(OreMapLayer.OnDataFromServer))]
[HarmonyPatchCategory(nameof(PatchManager.PatchCategory.NewDensity))]
public class OreMapLayerOnDataFromServerAdjustFactorPatch {

    [HarmonyPriority(Priority.First)]
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var adjust = AccessTools.Method(
            typeof(OreMapLayerOnDataFromServerAdjustFactorPatch),
            nameof(AdjustReadings));

        var matcher = new CodeMatcher(instructions);

        // call SerializerUtil.Deserialize<List<PropickReading>>(byte[])
        matcher.MatchEndForward(
            new CodeMatch(OpCodes.Ldarg_0),
            new CodeMatch(OpCodes.Ldfld),
            new CodeMatch(Code.Ldarg_1),
            new CodeMatch(OpCodes.Call));

        if (matcher.IsInvalid) {
            BetterErProspect.Api.Logger.Error("[BetterErProspect] OreMapLayer.OnDataFromServer: Deserialize call not found, patch skipped");
            return instructions;
        }

        // Stack: [list] -> call Adjust(list) -> [list]
        matcher.Advance().Insert(new CodeInstruction(OpCodes.Call, adjust));

        return matcher.InstructionEnumeration();
    }

    public static List<PropickReading> AdjustReadings(List<PropickReading> readings) {
        if (readings == null || BetterErProspect.Api is not ICoreClientAPI capi)
            return readings;

        var pptTracker = capi.ModLoader.GetModSystem<PptTracker>();
        pptTracker?.AdjustFactor(readings);

        return readings;
    }
}
