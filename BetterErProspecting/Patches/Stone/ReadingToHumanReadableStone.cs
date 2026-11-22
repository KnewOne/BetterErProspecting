using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using BetterErProspecting.Item;
using HarmonyLib;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches.Stone;

// Processes "rock-" keys in PropickReading.ToHumanReadable (vanilla treats every key as an ore).
// Lines starting with ">" in the comments below are the code we inject, the rest is vanilla.
[HarmonyPatch(typeof(PropickReading), nameof(PropickReading.ToHumanReadable))]
[HarmonyPatchCategory(nameof(PatchManager.PatchCategory.Always))]
public class ReadingToHumanReadableStone {
    private const string LogTag = "[ReadingToHumanReadableStone]";
    private const string RockPrefix = "rock-";

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var original = instructions.ToList();
        var matcher = new CodeMatcher(original);

        if (!PatchMainList(matcher) || !PatchTraceList(matcher)) {
            BetterErProspect.Logger?.Error($"{LogTag} Patch not applied, vanilla output will be used.");
            return original;
        }

        return matcher.InstructionEnumeration();
    }

    // above 2.5%
    // - str2 = oreReading1.Key;
    // > str2 = ProcessStoneReading(languageCode, oreReading1);
    private static bool PatchMainList(CodeMatcher matcher) {
        matcher.Start().MatchStartForward(
            new CodeMatch(i => i.opcode == OpCodes.Ldloca_S), // oreReading1
            new CodeMatch(OpCodes.Call, AccessTools.PropertyGetter(typeof(KeyValuePair<string, OreReading>), "Key")),
            new CodeMatch(OpCodes.Stloc_S)); // str2

        if (matcher.IsInvalid) {
            BetterErProspect.Logger?.Error($"{LogTag} `str2 = oreReading1.Key` not found.");
            return false;
        }

        var oreReading1 = matcher.Operand; // must be read before advancing

        matcher
            .SetAndAdvance(OpCodes.Ldarg_1, null) // languageCode
            .SetAndAdvance(OpCodes.Ldloc_S, oreReading1)
            .Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ReadingToHumanReadableStone), nameof(ProcessStoneReading))));

        return true;
    }

    // "Miniscule amounts of ..." list:
    //   str4 = $"<a href=\"handbook://{str3}\">{Lang.GetL(languageCode, "ore-" + key)}</a>";
    // > str4 = ProcessTraceReading(languageCode, key, str4);
    //   stringBuilder2.Append(str4);
    private static bool PatchTraceList(CodeMatcher matcher) {
        // Walk forward once: first the `key` local...
        matcher.Start().MatchStartForward(
            new CodeMatch(OpCodes.Ldstr, "ore-"),
            new CodeMatch(OpCodes.Ldloc_S), // key
            new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) })));

        if (matcher.IsInvalid) {
            BetterErProspect.Logger?.Error($"{LogTag} `\"ore-\" + key` not found.");
            return false;
        }

        var key = matcher.InstructionAt(1).operand;

        // ...then the str4 assignment
        matcher.MatchStartForward(
            new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object), typeof(object) })),
            new CodeMatch(OpCodes.Stloc_S), // str4
            new CodeMatch(OpCodes.Ldloc_S), // stringBuilder2
            new CodeMatch(OpCodes.Ldloc_S), // str4
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(StringBuilder), nameof(StringBuilder.Append), new[] { typeof(string) })));

        if (matcher.IsInvalid) {
            BetterErProspect.Logger?.Error($"{LogTag} str4 assignment not found.");
            return false;
        }

        var str4 = matcher.InstructionAt(1).operand;

        matcher.Advance(2); // after `stloc str4`
        matcher.Insert(
            new CodeInstruction(OpCodes.Ldarg_1), // languageCode
            new CodeInstruction(OpCodes.Ldloc_S, key),
            new CodeInstruction(OpCodes.Ldloc_S, str4),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ReadingToHumanReadableStone), nameof(ProcessTraceReading))),
            new CodeInstruction(OpCodes.Stloc_S, str4));

        return true;
    }

    // "12.3% Granite" for rocks, the key itself otherwise
    private static string ProcessStoneReading(string languageCode, KeyValuePair<string, OreReading> entry) {
        if (!entry.Key.StartsWith(RockPrefix))
            return entry.Key;

        var reading = entry.Value;
        return $"{reading.TotalFactor.ToString("0.##%")} {Lang.GetL(languageCode, reading.DepositCode)}";
    }

    // Handbook link (or plain name) for the rock
    private static string ProcessTraceReading(string languageCode, string key, string vanillaLink) {
        if (!key.StartsWith(RockPrefix))
            return vanillaLink;

        return ItemBetterErProspectingPick.getHandbookLinkOrName(BetterErProspect.Api.World, languageCode, key);
    }
}
