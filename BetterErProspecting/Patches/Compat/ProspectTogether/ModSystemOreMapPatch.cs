using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterErProspecting.Tracking;
using HarmonyLib;
using ProspectTogether;
using ProspectTogether.Shared;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Constants = BetterErProspecting.Config.Constants;

namespace BetterErProspecting.Patches.Compat.ProspectTogether;

// Changes scaling
// Prefix because cba
[HarmonyPatch(typeof(OreMapLayer), nameof(OreMapLayer.OnDataFromServer))]
[HarmonyPatchCategory(nameof(BetterErProspect.PatchCategory.ProspectTogetherCompat))]
public class OreMapLayerPatch {
    [HarmonyPrefix]
    [MethodImpl(MethodImplOptions.NoInlining)] // !!!!!
    static void Prefix(OreMapLayer __instance, byte[] data) {
        ICoreClientAPI capi = (ICoreClientAPI)typeof(OreMapLayer).GetField("capi", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);

        // Obtain pageCodes
        var pageCodes = capi.ModLoader.GetModSystem<ModSystemOreMap>().prospectingMetaData.PageCodes;

        if (pageCodes is null) {
            capi.World.Logger.Error("pageCodes is null");
            return;
        }

        ProspectTogetherModSystem mod = capi.ModLoader.GetModSystem<ProspectTogetherModSystem>();
        if (mod is null) {
            return;
        }

        var results = SerializerUtil.Deserialize<List<PropickReading>>(data);

        Dictionary<ChunkCoordinate, ProspectInfo> infos = new();

        foreach (var result in results) {
            // Convert results to ProspectTogether format
            var occurences = new List<OreOccurence>();
            // For now filter out non-ores. Maybe add rock- support in the future ? Lang might be annoying
            foreach (var reading in result.OreReadings) {
                string pageCode = reading.Key;
                if (pageCodes.ContainsKey(reading.Key)) {
                    pageCode = pageCodes.Get(reading.Key);
                }

                // MODIFIED 7.5
                if (reading.Value.TotalFactor > 0.025) {
                    // +2 to offset for our Enum
                    occurences.Add(new OreOccurence("game:ore-" + reading.Key, pageCode, (RelativeDensity)((int)GameMath.Clamp(reading.Value.TotalFactor * Constants.LinearFactorValue, 0, 5) + 2), reading.Value.PartsPerThousand));
                } else if (reading.Value.TotalFactor > PropickReading.MentionThreshold) {
                    occurences.Add(new OreOccurence("game:ore-" + reading.Key, pageCode, RelativeDensity.Miniscule, reading.Value.PartsPerThousand));
                }
            }

            var pos = result.Position;

            int chunksize = GlobalConstants.ChunkSize;
            var coords = new ChunkCoordinate(pos.XInt / chunksize, pos.ZInt / chunksize);
            ProspectInfo info = new(coords, occurences);
            infos[coords] = info;
        }

        // Send data to mod
        mod.ClientStorage.PlayerProspected(infos.Values.ToList());
    }
}
