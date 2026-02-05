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
using Vintagestory.API.Datastructures;
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

	public enum Mode {
		density,
		node,
		proximity,
		stone,
		borehole
	}

	private readonly Dictionary<Mode, ModeData> modeDataStorage = new Dictionary<Mode, ModeData>() {
		{ Mode.density, new ModeData(Mode.density, "textures/icons/heatmap.svg") },
		{ Mode.node, new ModeData(Mode.node, "textures/icons/rocks.svg") },
		{ Mode.proximity, new ModeData(Mode.proximity, "textures/icons/worldmap/spiral.svg") },
		{ Mode.stone, new ModeData(Mode.stone, "textures/icons/probe_stone.svg", "bettererprospecting") },
		{ Mode.borehole, new ModeData(Mode.borehole, "textures/icons/probe_borehole.svg", "bettererprospecting") }
	};

	public static ModConfig config => ModConfig.Instance;
	public override void OnLoaded(ICoreAPI Api) {
		sapi = Api as ICoreServerAPI;

		GenerateToolModes(Api);
		BetterErProspect.ReloadTools += () => { GenerateToolModes(Api); };
		Api.ModLoader.GetModSystem<ProspectingSystem>();
		base.OnLoaded(Api);
	}

	private void GenerateToolModes(ICoreAPI Api) {
		ObjectCacheUtil.Delete(Api, "proPickToolModes");
		toolModes = ObjectCacheUtil.GetOrCreate(Api, "proPickToolModes", () => {
			List<SkillItem> modes = [];

			// Density mode (two possible names, same SkillItem)
			if (config.EnableDensityMode) {
				if (config.NewDensityMode) {
					modeDataStorage[Mode.density].Skill.Name = Lang.Get("bettererprospecting:density-block-based");
				} else {
					modeDataStorage[Mode.density].Skill.Name = Lang.Get("Density Search Mode (Long range, chance based search)"); // This is a real vanilla lang string lmao
				}
				modes.Add(modeDataStorage[Mode.density].Skill);
			}


			// Node mode
			if (Api.World.Config.GetAsInt("propickNodeSearchRadius") > 0) {
				modeDataStorage[Mode.node].Skill.Name = Lang.Get("bettererprospecting:node");
				modes.Add(modeDataStorage[Mode.node].Skill);
			}

			// Proximity mode
			if (config.AddProximityMode) {
				modeDataStorage[Mode.proximity].Skill.Name = Lang.Get("bettererprospecting:proximity");
				modes.Add(modeDataStorage[Mode.proximity].Skill);
			}

			// Borehole mode
			if (config.AddBoreHoleMode) {
				modeDataStorage[Mode.borehole].Skill.Name = Lang.Get("bettererprospecting:borehole");
				modes.Add(modeDataStorage[Mode.borehole].Skill);
			}

			// Stone mode
			if (config.AddStoneMode) {
				modeDataStorage[Mode.stone].Skill.Name = Lang.Get("bettererprospecting:stone");
				modes.Add(modeDataStorage[Mode.stone].Skill);
			}

			return modes.ToArray();
		});
	}

	public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1) {
		IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;
		int tm = GetToolMode(itemslot, byPlayer, blockSel);
		int damage = 1;

		if (tm >= 0) {
			SkillItem skillItem = toolModes[tm];
			Mode toolMode = (Mode)Enum.Parse(typeof(Mode), skillItem.Code.Path, true);


			switch (toolMode) {
				case Mode.density:
					if (config.NewDensityMode) {
						ProbeBlockDensityMode(world, byPlayer, itemslot, blockSel, out damage);
					} else {
						ProbeVanillaDensity(world, byEntity, itemslot, blockSel, ref damage);
					}
					break;
				case Mode.node:
					ProbeBlockNodeMode(world, byEntity, itemslot, blockSel, api.World.Config.GetAsInt("propickNodeSearchRadius"));
					break;
				case Mode.proximity:
					ProbeProximity(world, byPlayer, itemslot, blockSel, out damage);
					break;
				case Mode.stone:
					ProbeStone(world, byPlayer, blockSel, out damage);
					break;
				case Mode.borehole:
					ProbeBorehole(world, byPlayer, blockSel, out damage);
					break;
			}

		} else {
			// All modes disabled
			// Propickn't
			world.BlockAccessor.GetBlock(blockSel.Position).OnBlockBroken(world, blockSel.Position, byPlayer);
		}


		if (DamagedBy != null && DamagedBy.Contains(EnumItemDamageSource.BlockBreaking)) {
			DamageItem(world, byEntity, itemslot, damage);
		}

		return true;
	}

	// Handle oneshot here too
	private void ProbeVanillaDensity(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		if (config.OneShotDensity) {
			damage = 3;
			IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;

			if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
				return;

			if (byPlayer is IServerPlayer severPlayer)
				PrintProbeResults(world, severPlayer, itemslot, blockSel.Position);

		} else {
			base.ProbeBlockDensityMode(world, byEntity, itemslot, blockSel);
		}
	}

	// Modded Density amount-based search. Square with chunkSize radius around current block. Whole mapheight
	private void ProbeBlockDensityMode(IWorldAccessor world, IPlayer byPlayer, ItemSlot _, BlockSelection blockSel, out int damage) {
		damage = config.NewDensityDmg;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		if (byPlayer is not IServerPlayer serverPlayer)
			return;

		List<DelayedMessage> delayedMessages = [];

		Dictionary<string, int> codeToFoundCount = ProspectingSystem.GenerateBlockData(sapi, blockSel.Position, delayedMessages);

		if (!ProspectingSystem.generateReadigs(sapi, blockSel.Position, codeToFoundCount, out PropickReading readings, delayedMessages)) { return; }

		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(world.Api, "propickworkspace");

		var textResults = readings.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);

		if (config.DebugMode) { delayedMessages.ForEach(msg => msg.Send(serverPlayer)); }

		sapi.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(readings, serverPlayer);

	}

	// Sphere search
	private void ProbeProximity(IWorldAccessor world, IPlayer byPlayer, ItemSlot _, BlockSelection blockSel, out int damage) {
		damage = config.ProximityDmg;
		int radius = config.ProximitySearchRadius;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		if (byPlayer is not IServerPlayer serverPlayer)
			return;

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


	}

	// Square radius-based search
	private void ProbeStone(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out int damage) {
		damage = config.StoneDmg;
		int walkRadius = config.StoneSearchRadius;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		if (byPlayer is not IServerPlayer serverPlayer)
			return;

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
			return;
		}

		rockInfo.Remove("rock-meteorite-iron");
		rockInfo.Remove("rock-suevite");

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:found-rocks"));

		int totalRocks = rockInfo.Values.Sum(v => v.Count);

		var output = config.StonePercentSearch
			? rockInfo.OrderByDescending(kvp => kvp.Value.Count).ToList()
			: rockInfo.OrderBy(kvp => kvp.Value.Distance).ToList();

		PropickReading propickReading = new PropickReading();
		propickReading.Position = blockPos.ToVec3d();

		foreach (var (key, (distance, count)) in output) {
			var rockReading = new OreReading();
			rockReading.DepositCode = key; // should be rock-{andesite|granite|etc}

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
	}

	// Cylinder Search
	private void ProbeBorehole(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, out int damage) {
		damage = config.BoreholeDmg;
		int radius = config.BoreholeRadius;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		if (byPlayer is not IServerPlayer serverPlayer)
			return;

		BlockFacing face = blockSel.Face;


		if (!config.BoreholeScansOre && !config.BoreholeScansStone) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-no-filter"), EnumChatType.Notification);
			return;
		}

		// It's MY mod. And I get to decide what's important for immersion:tm:
		if (face != BlockFacing.UP) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-upside"), EnumChatType.Notification);
			return;
		}

		StringBuilder sb = new StringBuilder();
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		sb.Append(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-taken"));

		// Need to hold unique insertion order. OrderedHashSet where art thou ?
		var blockKeys = new OrderedDictionary<string, string>();
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
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) {
		if (api is not ICoreClientAPI capi) {
			return null;
		}

		foreach (var modeSkill in toolModes) {
			Mode modeEnum = (Mode)Enum.Parse(typeof(Mode), modeSkill.Code.Path, true);
			var data = modeDataStorage[modeEnum];

			if (data.Texture == null) {
				data.Texture = capi.Gui.LoadSvgWithPadding(data.Asset, 48, 48, 5, ColorUtil.WhiteArgb);
			}

			modeSkill.WithIcon(capi, data.Texture);
			modeSkill.TexturePremultipliedAlpha = false;
		}

		return toolModes;
	}
	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) {
		return Math.Min(toolModes.Length - 1, slot.Itemstack.Attributes.GetInt("toolMode"));
	}
	public override float OnBlockBreaking(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float dt, int counter) {
		float remain = base.OnBlockBreaking(player, blockSel, itemslot, remainingResistance, dt, counter);

		remain = (remain + remainingResistance) / 2.2f;
		return remain;
	}
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
