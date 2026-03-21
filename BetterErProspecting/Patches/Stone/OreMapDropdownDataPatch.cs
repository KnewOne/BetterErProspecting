using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches.Stone;

// Always because we need to handle previously created stone readings. Otherwise they will have ore-rock-{} format and not have a proper dropdown option
// Adds dropdowns for ore and rock group/ per rock specific dropdowns
// Sorts readings by locale name
// Filters out rock readings from WPs if not enabled
[HarmonyPatch(typeof(OreMapLayer), "GetOreFilterDropdownData")]
[HarmonyPatchCategory(nameof(BetterErProspect.PatchCategory.Always))]
public class OreMapDropdownDataPatch {
    static void Postfix(ref DropDownEntry[] __result) {
        var stoneReadingsEnabled = BetterErProspect.Config.StoneSearchCreatesReadings;

        var headers = new List<DropDownEntry>();
        var ores = new List<DropDownEntry>();
        var rocks = new List<DropDownEntry>();

        foreach (var entry in __result) {
            // Keep the "everything" header (null value)
            if (entry.Value == null) {
                headers.Add(entry);
                continue;
            }

            // ore-rock-* were stored with ore- prefix; strip it to rock-*
            if (entry.Value.StartsWith("ore-rock-")) {
                if (!stoneReadingsEnabled) continue;
                var rockCode = entry.Value.Substring("ore-".Length); // "rock-*"
                rocks.Add(new DropDownEntry(rockCode, $"[{Lang.Get("bettererprospecting:R")}]{Lang.Get(rockCode)}"));
            } else if (entry.Value.StartsWith("rock-")) {
                if (!stoneReadingsEnabled) continue;
                rocks.Add(new DropDownEntry(entry.Value, $"[{Lang.Get("bettererprospecting:R")}]{Lang.Get(entry.Value)}"));
            } else {
                ores.Add(entry);
            }
        }

        if (stoneReadingsEnabled) {
            headers.Add(new DropDownEntry("ore-", Lang.Get("bettererprospecting:worldmap-ores-only")));
            headers.Add(new DropDownEntry("rock-", Lang.Get("bettererprospecting:worldmap-rocks-only")));
        }

        ores.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.CurrentCulture));
        rocks.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.CurrentCulture));

        __result = [..headers, ..ores, ..rocks];
    }
}
