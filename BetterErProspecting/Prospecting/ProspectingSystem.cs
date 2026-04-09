using BetterErProspecting.Tracking;
using InterestingOreGen;
using Vintagestory.API.Client;
using Vintagestory.API.Common.CommandAbbr;

namespace BetterErProspecting.Prospecting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Item;
using Item.Data;
using HydrateOrDiedrate;
using HydrateOrDiedrate.Wells.Aquifer;
using HydrateOrDiedrate.Wells.Aquifer.ModData;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

public class ProspectingSystem : ModSystem {
	private static ILogger logger => BetterErProspect.Logger;
	private bool isReprospecting;
	private ICoreServerAPI sapi;

	public override void StartPre(ICoreAPI api) {
		base.StartPre(api);
		sapi = api as ICoreServerAPI;
	}

	public override void StartServerSide(ICoreServerAPI api) {
		base.StartServerSide(api);
		sapi = api;
		api.ChatCommands.GetOrCreate("btrpr")
			.RequiresPrivilege(Privilege.controlserver)
			.BeginSub("reprospect")
				.WithDesc("Regenerates prospecting data for all players ( including offline ). Optionally only for one player. Expensive operation.")
				.WithExamples("/btrpr reprospect", "/btrpr reprospect KnewOne")
				.WithArgs(new OnlinePlayerArgParser("player", api, isMandatoryArg: false))
				.HandleWith(Reprospect)
			.EndSub();
	}

	private TextCommandResult Reprospect(TextCommandCallingArgs args) {
		if (isReprospecting) {
			return TextCommandResult.Error("Please wait before the previous command ends");
		}

		var caller = args.Caller.Player as IServerPlayer;
		var targetPlayer = args.Parsers[0].GetValue() as IServerPlayer;

		// Background
		Task.Run(() => { _ = ReprospectTask(caller, targetPlayer); });

		return TextCommandResult.Success("[BetterEr Prospect] Began reprospecting");
	}
	private readonly ConcurrentDictionary<(int, int), Task> chunkLoadTasks = new();

	private Task EnsureChunkLoaded(int cx, int cz) {
		return chunkLoadTasks.GetOrAdd((cx, cz), _ => {
			var tcs = new TaskCompletionSource();
			var chunk = sapi.WorldManager.GetMapChunk(cx, cz);
			if (chunk != null) {
				tcs.SetResult();
				return tcs.Task;
			}

			var opts = new ChunkLoadOptions();
			opts.OnLoaded += () => tcs.TrySetResult();
			sapi.WorldManager.LoadChunkColumnPriority(cx, cz, opts);
			return tcs.Task;
		});
	}

	// This might create lag or memory issues. Need more feedback on large world/many players
	public async Task ReprospectTask(IServerPlayer caller, IServerPlayer targetPlayer) {

		int countSucc = 0;
		int countUnload = 0;

		try {
			logger.Notification("[BetterEr Prospecting] Reprospecting started by {0} on player? {1}",
				caller == null ? "console" : caller.PlayerName,
				targetPlayer == null ? "all" : targetPlayer);

			var oml = sapi.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is OreMapLayer) as OreMapLayer;

			if (oml == null) {
				return;
			}

			if (isReprospecting)
				return;
			isReprospecting = true;
			chunkLoadTasks.Clear(); // Reruns


			var allReadings = PptTracker.getAllPlayerReadings(sapi);

			// We could process all readings at the same time, but that might cause a lot of ram usage. Lets stick to oml per player ( huge cope )
			foreach (var (_, readings) in oml.PropickReadingsByPlayer) {

				// Step 1: Collect all unique chunks for this player's readings
				var chunksToLoad = new HashSet<(int cx, int cz)>();
				foreach (var reading in readings) {
					int bx = reading.Position.XInt;
					int bz = reading.Position.ZInt;

					foreach (var dx in new[] { -32, 0, 32 })
						foreach (var dz in new[] { -32, 0, 32 }) {
							int cx = (bx + dx) / GlobalConstants.ChunkSize;
							int cz = (bz + dz) / GlobalConstants.ChunkSize;
							chunksToLoad.Add((cx, cz));
						}
				}

				// Step 2: Ensure all chunks are loaded in parallel
				await Task.WhenAll(chunksToLoad.Select(c => EnsureChunkLoaded(c.cx, c.cz)));

				// Step 3: Process readings in parallel
				var updatedReadings = await Task.WhenAll(readings.Select(reading => {
					var readingBlock = reading.Position.AsBlockPos;
					var readingChunk = sapi.WorldManager.GetChunk(readingBlock);

					if (readingChunk == null) {
						Interlocked.Increment(ref countUnload);
						return Task.FromResult(reading);
					}

					generateReadigs(sapi, readingBlock, GenerateBlockData(sapi, readingBlock), out PropickReading newReading);
					Interlocked.Increment(ref countSucc);
					return Task.FromResult(newReading);
				}));

				// Step 4: Replace readings safely
				readings.Clear();
				readings.AddRange(updatedReadings);
			}

			// Step 5: Serialize per player in parallel
			var savegame = sapi.WorldManager.SaveGame;
			var serializeTasks = oml.PropickReadingsByPlayer.Select(val => Task.Run(() => {
				using var ms = new FastMemoryStream();
				savegame.StoreData("oreMapMarkers-" + val.Key, SerializerUtil.Serialize(val.Value, ms));
			})).ToArray();

			await Task.WhenAll(serializeTasks);

			// Step 6: Clear offline players
			var onlineUids = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToHashSet();
			oml.PropickReadingsByPlayer.RemoveAllByKey(uid => onlineUids.Contains(uid));

			logger.Notification("[BetterEr Prospecting] Reprospecting finished");
		} catch (Exception ex) {
			logger.Error("[BetterEr Prospecting] Error during reprospecting: {0}", ex);
		} finally {
			isReprospecting = false;
			caller?.SendMessage(GlobalConstants.AllChatGroups,
				$"[BetterEr Prospecting] Finished reprospecting. Changed {countSucc} readings. Kept unchanged {countUnload} unloaded chunk readings",
				EnumChatType.Notification);
		}
	}


	public static Dictionary<string, int> GenerateBlockData(ICoreServerAPI api, BlockPos blockPos, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= [];
		const int radius = ItemBetterErProspectingPick.densityRadius;

		int mapHeight = api.World.BlockAccessor.GetTerrainMapheightAt(blockPos);
		string[] knownBlacklistedCodes = ["flint", "quartz"];

		var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		Dictionary<string, int> codeToFoundCount = new();
		var nopageVariant = new HashSet<string>();
		var depositKeys = new HashSet<string>(ppws.depositsByCode.Keys);

		var blockCache = new Dictionary<string, string>();

		api.World.BlockAccessor.WalkBlocks(new BlockPos(blockPos.X - radius, mapHeight, blockPos.Z - radius), new BlockPos(blockPos.X + radius, 0, blockPos.Z + radius),
			(walkBlock, x, y, z) => {
				if (walkBlock.Variant == null)
					return;

				bool isOre = ItemBetterErProspectingPick.IsOre(walkBlock, blockCache, out _, out var key);
				bool isRock = !isOre && ItemBetterErProspectingPick.IsRock(walkBlock, blockCache, out _, out key);

				if (!isOre && !isRock) return;
				if (knownBlacklistedCodes.Contains(key))
					return;

				if (depositKeys.Contains(key)) {
					codeToFoundCount[key] = codeToFoundCount.GetValueOrDefault(key, 0) + 1;
				} else if (isOre) {
					nopageVariant.Add(key);
				}
			});

		if (nopageVariant.Count <= 0) return codeToFoundCount;
		delayedMessages.Add(new DelayedMessage(Lang.Get("bettererprospecting:debug-bad-ppws-key", string.Join(", ", nopageVariant))));
		delayedMessages.Add(new DelayedMessage(Lang.Get("bettererprospecting:debug-bad-ppws-key-expected", string.Join(", ", ppws.depositsByCode.Keys))));

		return codeToFoundCount;
	}

    /// <returns>Bool stating if readings have been generated successfully</returns>
	public static bool generateReadigs(ICoreServerAPI sapi, BlockPos blockPos, Dictionary<string, int> codeToFoundOre, out PropickReading readings, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= [];

		var world = sapi.World;
		var deposits = sapi.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(world.Api, "propickworkspace");
		if (deposits == null) {
			readings = null;
			return false;
		}

		const int radius = ItemBetterErProspectingPick.densityRadius;

		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(blockPos);
		const int zoneDiameter = 2 * radius;
		int zoneBlocks = zoneDiameter * zoneDiameter * mapHeight;

		readings = new PropickReading
		{
			Position = blockPos.ToVec3d()
		};

        var updatePairs = new List<(string oreCode, double ppt)>();

		foreach (var (oreCode, empiricalAmount) in codeToFoundOre) {
			var reading = new OreReading
			{
				PartsPerThousand = (double)empiricalAmount / zoneBlocks * 1000
			};

            // This is basically vanilla logic
            IBlockAccessor blockAccess = world.BlockAccessor;
            int regsize = blockAccess.RegionSize;
            IMapRegion reg = world.BlockAccessor.GetMapRegion(blockPos.X / regsize, blockPos.Z / regsize);
            int lx = blockPos.X % regsize;
            int lz = blockPos.Z % regsize;
            IntDataMap2D map = reg.OreMaps[oreCode];
            int noiseSize = map.InnerSize;
            float posXInRegionOre = (float)lx / regsize * noiseSize;
            float posZInRegionOre = (float)lz / regsize * noiseSize;
            int oreDist = map.GetUnpaddedColorLerped(posXInRegionOre, posZInRegionOre);
            int[] blockColumn = ppws.GetRockColumn(blockPos.X, blockPos.Z);
            ppws.depositsByCode[oreCode].GeneratorInst.GetPropickReading(blockPos, oreDist, blockColumn, out _, out double imaginationLandFactor);

            // 0.15 to allow ppt visibility. We will be overwriting this with the patch anyway
            reading.TotalFactor = Math.Clamp(imaginationLandFactor, 0.15, 1.0);


			readings.OreReadings[oreCode] = reading;
            updatePairs.Add((oreCode, reading.PartsPerThousand));
		}


        var pptTracker = sapi.ModLoader.GetModSystem<PptTracker>();
        pptTracker?.UpdatePpt(updatePairs);

		addMiscReadings(sapi, readings, blockPos, delayedMessages);
		return true;
	}

	#region Compat

    private static void addMiscReadings(ICoreServerAPI sapi, PropickReading readings, BlockPos blockPos, List<DelayedMessage> delayedMessages) {
        if (sapi.ModLoader.IsModEnabled("hydrateordiedrate")) addHoDReadingsIfCan(sapi, readings, blockPos, delayedMessages);
    }

    private static void addHoDReadingsIfCan(ICoreServerAPI sapi, PropickReading readings, BlockPos pos, List<DelayedMessage> delayedMessages) {
        HydrateOrDiedrateModSystem system = sapi.ModLoader.GetModSystem<HydrateOrDiedrateModSystem>();
        // Latest bump to 2.2.13 due to modified namespace
        var minVer = new Version("2.2.13");
        if (new Version(system.Mod.Info.Version) <= minVer) {
            delayedMessages.Add(new DelayedMessage($"[BetterEr Prospecting] Please update HydrateOrDietrade to at least {minVer.ToString()} for aquifer support"));
            return;
        }

        var world = sapi.World;
        var chnData = AquiferManager.GetAquiferChunkData(world, pos)?.Data;
        if (chnData == null) {
            return;
        }

        var hydrateConfig = HydrateOrDiedrate.Config.ModConfig.Instance;

        if (hydrateConfig.GroundWater.ShowAquiferProspectingDataOnMap) {
            readings.OreReadings.Add(AquiferData.OreReadingKey, chnData);
        }

        delayedMessages.Add(new DelayedMessage(AquiferManager.GetAquiferDirectionHint(world, pos)));
    }
	#endregion

	public override void Dispose() {
		sapi = null;
		base.Dispose();
	}
}
