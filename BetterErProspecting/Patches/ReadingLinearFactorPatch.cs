using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BetterErProspecting.Config;
using HarmonyLib;
using Vintagestory.GameContent;

namespace BetterErProspecting.Patches;

[HarmonyPatch(typeof(PropickReading), nameof(PropickReading.ToHumanReadable))]
public class ReadingLinearFactorPatch {
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var matcher = new CodeMatcher(instructions);

        matcher.Start().MatchStartForward(new CodeMatch(OpCodes.Ldc_R8, 7.5));

		if (matcher.IsInvalid) {
			BetterErProspect.Logger?.Error("[PropickReadingPatch] Failed to find the 7.5 multiplication pattern");
			return instructions;
		}

		// Replace 7.5 with 5.0 to make it linear (0-1 range * 5.0 = 0-5 index range for 6 elements)
		matcher.SetOperandAndAdvance(Constants.LinearFactorValue);

		return matcher.InstructionEnumeration();
	}
}
