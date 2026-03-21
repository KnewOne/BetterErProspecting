using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BetterErProspecting.Item;
public partial class ItemBetterErProspectingPick {

	#region Helpers

	// These are assholes
	public static Dictionary<string, string> specialOreCodeConversion = new Dictionary<string, string>() {
		// These have items different from the code used for the material. Funnily enough, both of them are child deposits
		{"nativegold", "gold" },
		{"nativesilver", "silver" },
		{"lapislazuli", "lapis" }
	};
	// For now a few cases. The conversion is a public method, can extend from there.
	// I will assume basegame's logic of "_" meaning childnode
	private static string ConvertChildRocks(string code) {
		if (code == null)
			return null;

		var span = code.AsSpan();
		int idx = code.LastIndexOf('_');
		ReadOnlySpan<char> suffixSpan = idx >= 0 ? span[(idx + 1)..] : span;

		string suffix = suffixSpan.ToString();

		return specialOreCodeConversion.GetValueOrDefault(suffix, suffix);
	}

	public void WalkBlocksCylinder(BlockPos startPos, int radius, Action<Block, int, int, int> onBlock) {
		var ba = sapi.World.BlockAccessor;
		int startX = startPos.X;
		int startY = startPos.InternalY;
		int startZ = startPos.Z;

		for (int y = startY; y > 0; y--) {
			for (int x = startX - radius; x <= startX + radius; x++) {
				for (int z = startZ - radius; z <= startZ + radius; z++) {
					int dx = x - startX;
					int dz = z - startZ;
					if (dx * dx + dz * dz > radius * radius) continue;
					var block = ba.GetBlock(new BlockPos(x, y, z));
					onBlock(block, x, y, z);
				}
			}
		}
	}

	public void WalkBlocksSphere(BlockPos startPos, int radius, Action<Block, int, int, int> onBlock) {
		var ba = sapi.World.BlockAccessor;
		int startX = startPos.X;
		int startY = startPos.InternalY;
		int startZ = startPos.Z;

		for (int y = startY - radius; y <= startY + radius; y++) {
			for (int x = startX - radius; x <= startX + radius; x++) {
				for (int z = startZ - radius; z <= startZ + radius; z++) {
					int dx = x - startX;
					int dy = y - startY;
					int dz = z - startZ;

					if (dx * dx + dy * dy + dz * dz > radius * radius) continue;
					var block = ba.GetBlock(new BlockPos(x, y, z));
					onBlock(block, x, y, z);
				}
			}
		}
	}

	public static string getHandbookLinkOrName(IWorldAccessor world, IServerPlayer serverPlayer, string key, string itemName = null, string handbookUrl = null) {
		return getHandbookLinkOrName(world, serverPlayer.LanguageCode, key, itemName, handbookUrl);
	}

	public static string getHandbookLinkOrName(IWorldAccessor world, string languageCode, string key, string itemName = null, string handbookUrl = null) {
		itemName ??= Lang.GetL(languageCode, key);
		if (handbookUrl != null) return $"<a href=\"handbook://{handbookUrl}\">{itemName}</a>";

		if (world.GetBlock(key) is { } block) {
			handbookUrl = GuiHandbookItemStackPage.PageCodeForStack(new ItemStack(block));
		} else if (world.GetItem(key) is { } item) {
			handbookUrl = GuiHandbookItemStackPage.PageCodeForStack(new ItemStack(item));
		}

		return handbookUrl != null ? $"<a href=\"handbook://{handbookUrl}\">{itemName}</a>" : itemName;
	}
	public static bool IsOre(Block block, Dictionary<string, string> cache, out string key, out string typeKey) {
		key = null;
		typeKey = null;

		if (block.BlockMaterial != EnumBlockMaterial.Ore || block.Variant == null)
			return false;
		if (!block.Variant.TryGetValue("type", out string oreKey))
			return false;

		if (!cache.TryGetValue(oreKey, out typeKey)) {
			typeKey = ConvertChildRocks(oreKey);
			cache[oreKey] = typeKey;
		}

		key = "ore-" + typeKey;
		return true;
	}
	public static bool IsOre(Block block, Dictionary<string, string> cache, out string key) {
		return IsOre(block, cache, out key, out _);
	}
	public static bool IsRock(Block block, Dictionary<string, string> cache, out string key, out string rockKey) {
		key = null;
		rockKey = null;
		if (block.Variant == null || !block.Variant.TryGetValue("rock", out rockKey))
			return false;

		if (!cache.TryGetValue(rockKey, out var cached)) {
			cached = "rock-" + rockKey;
			cache[rockKey] = cached;
		}

		key = cache[rockKey];
		return true;
	}
	public static bool IsRock(Block block, Dictionary<string, string> cache, out string key) {
		return IsRock(block, cache, out key, out _);
	}

    private static bool breakIsPropickable(IWorldAccessor world, BlockSelection blockSel, ref float dropMultiplier) {
		Block block = world.BlockAccessor.GetBlock(blockSel.Position);

		if (!block?.Attributes?["propickable"].AsBool() == true) {
			return false;
		}

        dropMultiplier = 0;
		return true;
	}
	#endregion
}
