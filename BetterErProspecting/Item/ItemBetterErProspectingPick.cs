using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BetterErProspecting.Item.Data;
using BetterErProspecting.Prospecting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ModConfig = BetterErProspecting.Config.ModConfig;

namespace BetterErProspecting.Item;
public sealed partial class ItemBetterErProspectingPick : ItemProspectingPick {
	ICoreServerAPI sapi;
	SkillItem[] toolModes;

	public const int densityRadius = GlobalConstants.ChunkSize;
    public static ModConfig config => ModConfig.Instance;


    /// <summary>
    ///  Register an outside mod's mode to the propick
    /// </summary>
    /// <param name="modeData">Mode definition with the corresponding execution method</param>
    /// <param name="regenerateModes">Whether to immediately regenerate tool modes. Useful when adding multiple modes. Still need to call manually after</param>
    public void RegisterMode(ModeData modeData, bool regenerateModes = true) {
        modeDataStorage[modeData.Skill.Code.Path] = modeData;
        if (regenerateModes)
            RegenerateToolModes();
    }

    private readonly OrderedDictionary<string, ModeData> modeDataStorage = new() {
        { "density", new ModeData("density", "textures/icons/heatmap.svg") },
        { "node", new ModeData("node", "textures/icons/rocks.svg", "bettererprospecting:node") },
        { "proximity", new ModeData("proximity", "textures/icons/worldmap/spiral.svg", "bettererprospecting:proximity") },
        { "stone", new ModeData("stone", "textures/icons/probe_stone.svg", "bettererprospecting:stone", "bettererprospecting") },
        { "borehole", new ModeData("borehole", "textures/icons/probe_borehole.svg", "bettererprospecting:borehole", "bettererprospecting") }
	};

	public override void OnLoaded(ICoreAPI Api) {
		sapi = Api as ICoreServerAPI;
		base.OnLoaded(Api);

        // Need to define here since doing in dict definition would require static methods. Other mods can use RegisterMode
        modeDataStorage["density"].Execute = ProbeDensity;
        modeDataStorage["node"].Execute = ProbeNode;
        modeDataStorage["proximity"].Execute = ProbeProximity;
        modeDataStorage["stone"].Execute = ProbeStone;
        modeDataStorage["borehole"].Execute = ProbeBorehole;

        RegenerateToolModes();
        BetterErProspect.ReloadTools += RegenerateToolModes;
	}

    public void RegenerateToolModes() {
        ObjectCacheUtil.Delete(api, "proPickToolModes");
        toolModes = ObjectCacheUtil.GetOrCreate(api, "proPickToolModes", () => {
            var density = modeDataStorage["density"];
            density.Enabled = config.EnableDensityMode;
            density.Skill.Name = config.NewDensityMode ? Lang.Get("bettererprospecting:density-block-based") : Lang.Get("Density Search Mode (Long range, chance based search)");

            modeDataStorage["node"].Enabled = api.World.Config.GetAsInt("propickNodeSearchRadius") > 0;
            modeDataStorage["proximity"].Enabled = config.AddProximityMode;
            modeDataStorage["borehole"].Enabled = config.AddBoreHoleMode;
            modeDataStorage["stone"].Enabled = config.AddStoneMode;

            return modeDataStorage.Values.Where(m => m.Enabled).Select(m => m.Skill).ToArray();
		});
	}

	public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1) {
		IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;
		int tm = GetToolMode(itemslot, byPlayer, blockSel);
        int damage = 1;

        // Order here matters. If no tool modes are enabled, mult is still 1. If we swap these it would be zero.
        if (tm >= 0 && breakIsPropickable(world, blockSel, ref dropQuantityMultiplier)) {
            string skillItemCode = toolModes[tm].Code.Path;

            if (modeDataStorage.TryGetValue(skillItemCode, out var modeData) && modeData.Execute != null) {
                damage = modeData.Execute(world, byPlayer, itemslot, blockSel);
            } else {
                throw new ArgumentException($"Declared skill item code not handled for propick: {skillItemCode}");
            }
		}

        world.BlockAccessor.GetBlock(blockSel.Position).OnBlockBroken(world, blockSel.Position, byPlayer, dropQuantityMultiplier);

		if (DamagedBy != null && DamagedBy.Contains(EnumItemDamageSource.BlockBreaking)) {
            DamageItem(world, byEntity, itemslot, damage);
		}

		return true;
	}


    private int ProbeNode(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel) {
        ProbeBlockNodeMode(world, byPlayer.Entity, itemslot, blockSel, api.World.Config.GetAsInt("propickNodeSearchRadius"));
        return 1;
    }

    private int ProbeDensity(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel) {
        if (config.NewDensityMode) {
            return ProbeBlockDensityMode(byPlayer, blockSel);
        } else {
            if (config.OneShotDensity) {
                if (byPlayer is IServerPlayer severPlayer)
                    PrintProbeResults(world, severPlayer, itemslot, blockSel.Position);
                return 3;
            } else {
                base.ProbeBlockDensityMode(world, byPlayer.Entity, itemslot, blockSel);
                return 1;
            }
        }
    }

	// Modded Density amount-based search. Square with chunkSize radius around current block. Whole mapheight
    private int ProbeBlockDensityMode(IPlayer byPlayer, BlockSelection blockSel) {
		if (byPlayer is not IServerPlayer serverPlayer)
            return config.NewDensityDmg;

		List<DelayedMessage> delayedMessages = [];

		Dictionary<string, int> codeToFoundCount = ProspectingSystem.GenerateBlockData(sapi, blockSel.Position, delayedMessages);

        if (!ProspectingSystem.generateReadigs(sapi, blockSel.Position, codeToFoundCount, out PropickReading readings, delayedMessages)) {
            return config.NewDensityDmg;
        }

        ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		var textResults = readings.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);

		if (config.DebugMode) { delayedMessages.ForEach(msg => msg.Send(serverPlayer)); }

		sapi.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(readings, serverPlayer);

        return config.NewDensityDmg;
	}

	// Sphere search
    private int ProbeProximity(IWorldAccessor world, IPlayer byPlayer, ItemSlot _, BlockSelection blockSel) {
		int radius = config.ProximitySearchRadius;

		if (byPlayer is not IServerPlayer serverPlayer)
            return config.ProximityDmg;

		BlockPos pos = blockSel.Position.Copy();
		int closestOre = -1;
		var cache = new Dictionary<string, string>();

		WalkBlocksSphere(pos, radius, (walkBlock, x, y, z) => {
			if (!IsOre(walkBlock, cache, out var key)) return;
			if (key.Contains("quartz")) return;

			var distanceTo = (int)Math.Round(pos.DistanceTo(x, y, z));

			if (closestOre == -1 || closestOre > distanceTo) {
				closestOre = distanceTo;
			}
		});

		string messageKey;

		if (!config.ProximityVagueDescriptors) {
			messageKey = closestOre != -1
				? "bettererprospecting:closest-ore-is"
				: "bettererprospecting:closest-ore-not-found";
			object[] messageArgs = closestOre != -1 ? [closestOre] : [radius];
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, messageKey, messageArgs), EnumChatType.Notification);
		} else {
			messageKey = closestOre != -1
				? "bettererprospecting:promimity-vague-ore-nearby"
				: "bettererprospecting:proximity-vague-ore-not-found";
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, messageKey), EnumChatType.Notification);
		}

        return config.ProximityDmg;
	}

	// Square radius-based search
    private int ProbeStone(IWorldAccessor world, IPlayer byPlayer, ItemSlot __, BlockSelection blockSel) {
		int walkRadius = config.StoneSearchRadius;

		if (byPlayer is not IServerPlayer serverPlayer)
            return config.StoneDmg;

		StringBuilder sb = new StringBuilder();

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:area-sample", walkRadius));

		Dictionary<string, (int Distance, int Count)> rockInfo = new();

		BlockPos blockPos = blockSel.Position.Copy();
		var blockEnd = blockPos.AddCopy(-walkRadius, 0, -walkRadius);
		blockEnd.Y = 1;
		var cache = new Dictionary<string, string>();
		api.World.BlockAccessor.WalkBlocks(blockPos.AddCopy(walkRadius, walkRadius, walkRadius), blockEnd,
			(walkBlock, x, y, z) => {

				if (IsRock(walkBlock, cache, out string key)) {
					int distance = (int)blockSel.Position.DistanceTo(new BlockPos(x, y, z));

					if (rockInfo.TryGetValue(key, out var existing)) {
						rockInfo[key] = (Math.Min(existing.Distance, distance), existing.Count + 1);
					} else {
						rockInfo[key] = (distance, 1);
					}
				}

			});


		if (rockInfo.Count == 0) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:no-rocks-near"), EnumChatType.Notification);
            return config.StoneDmg;
		}

		rockInfo.Remove("rock-meteorite-iron");
		rockInfo.Remove("rock-suevite");

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:found-rocks"));

		int totalRocks = rockInfo.Values.Sum(v => v.Count);

		var output = config.StonePercentSearch
			? rockInfo.OrderByDescending(kvp => kvp.Value.Count).ToList()
			: rockInfo.OrderBy(kvp => kvp.Value.Distance).ToList();

        PropickReading propickReading = new PropickReading {
            Position = blockPos.ToVec3d()
        };

        foreach (var (key, (distance, count)) in output) {
            var rockReading = new OreReading {
                DepositCode = key // should be rock-{andesite|granite|etc}
            };

            double percent = (double)count / totalRocks; // 0-1
			double percentScaled = Math.Max(percent * 100.0, 0.01); // 0.01-100

			// totalfactor is used by ToHumanReadable for sorting, but for display we will use PPT, which holds 0-100 percentage
			rockReading.TotalFactor = Math.Max(percent, 0.026);
			rockReading.PartsPerThousand = percentScaled; // will use a percentage instead

			propickReading.OreReadings[key] = rockReading;

			string itemLink = getHandbookLinkOrName(world, serverPlayer, key);

			if (config.StonePercentSearch) {
				sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, $"{itemLink}: {percentScaled:0.##} %"));
			} else {
				sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:stone-mode-blocks-away", itemLink, distance));
			}
		}

		if (config.StoneSearchCreatesReadings) {
			world.Api.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(propickReading, serverPlayer);
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);
        return config.StoneDmg;
	}

	// Cylinder Search
    private int ProbeBorehole(IWorldAccessor world, IPlayer byPlayer, ItemSlot __, BlockSelection blockSel) {
		int radius = config.BoreholeRadius;

		if (byPlayer is not IServerPlayer serverPlayer)
            return config.BoreholeDmg;

		BlockFacing face = blockSel.Face;

		if (!config.BoreholeScansOre && !config.BoreholeScansStone) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-no-filter"), EnumChatType.Notification);
            return config.BoreholeDmg;
		}

		// It's MY mod. And I get to decide what's important for immersion:tm:
		if (face != BlockFacing.UP) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-upside"), EnumChatType.Notification);
            return config.BoreholeDmg;
		}

		StringBuilder sb = new StringBuilder();
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		sb.Append(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-taken"));

		// Need to hold unique insertion order. OrderedHashSet where art thou ?
		var blockKeys = new Vintagestory.API.Datastructures.OrderedDictionary<string, string>();
		var cache = new Dictionary<string, string>();

		BlockPos blockPos = blockSel.Position.Copy();

		WalkBlocksCylinder(blockPos, radius, (walkBlock, _, _, _) => {
			if (config.BoreholeScansOre && IsOre(walkBlock, cache, out string fullKey, out string oreKey)) {
				var oreHandbook = ppws.depositsByCode.GetValueOrDefault(oreKey, null)?.HandbookPageCode;
				blockKeys.TryAdd(fullKey, oreHandbook);
			} else
			if (config.BoreholeScansStone && IsRock(walkBlock, cache, out fullKey, out _)) {
				blockKeys.TryAdd(fullKey, null);
			}

		});

		if (blockKeys.Count == 0) {
			sb.AppendLine();
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-not-found"));
		} else {
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-found"));
			var linkedNames = blockKeys.Select(kv => getHandbookLinkOrName(world, serverPlayer, kv.Key, handbookUrl: blockKeys[kv.Key])).ToList();
			sb.AppendLine(string.Join(", ", linkedNames));
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);
        return config.BoreholeDmg;
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) {
		if (api is not ICoreClientAPI capi) {
			return null;
		}

		foreach (var modeSkill in toolModes) {
            var data = modeDataStorage[modeSkill.Code.Path];

			if (data.Texture == null) {
				data.Texture = capi.Gui.LoadSvgWithPadding(data.Asset, 48, 48, 5, ColorUtil.WhiteArgb);
			}

			modeSkill.WithIcon(capi, data.Texture);
			modeSkill.TexturePremultipliedAlpha = false;
		}

		return toolModes;
	}
	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) {
        return Math.Min(toolModes.Length - 1, slot.Itemstack!.Attributes.GetInt("toolMode"));
	}

    // public override float OnBlockBreaking(IPlayer player, BlockSelection blockSel, ItemSlot, float remainingResistance, float dt, int counter) {
    // 	float remain = base.OnBlockBreaking(player, blockSel, itemslot, remainingResistance, dt, counter);
    //
    // 	remain = (remain + remainingResistance) / 2.2f;
    // 	return remain;
    // }

	public override void OnUnloaded(ICoreAPI coreApi) {
		foreach (var item in modeDataStorage?.Values!) { item?.Skill?.Dispose(); }

		int num = 0;
		while (toolModes != null && num < toolModes.Length) {
			toolModes[num]?.Dispose();
			num++;
		}

		base.OnUnloaded(coreApi);
	}

}
