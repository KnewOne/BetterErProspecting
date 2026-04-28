using BetterErProspecting.Tracking;
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
	private ICoreServerAPI sapi;
    private PptTracker pptTracker;

    CancellationTokenSource _cts = new();

	public override void StartPre(ICoreAPI api) {
		base.StartPre(api);
		sapi = api as ICoreServerAPI;
	}

	public override void StartServerSide(ICoreServerAPI api) {
		base.StartServerSide(api);
		sapi = api;
        pptTracker = api.ModLoader.GetModSystem<PptTracker>();
		api.ChatCommands.GetOrCreate("btrpr")
			.RequiresPrivilege(Privilege.controlserver)
            .BeginSub("reprospectcancell")
            .WithExamples("/btrpr reprospectcancell")
            .HandleWith(CancelReprospect)
            .EndSub()
			.BeginSub("reprospect")
            .WithDesc("Regenerates prospecting data for all players ( including offline ). Optionally only for one player. Expensive operation.")
            .WithExamples("/btrpr reprospect", "/btrpr reprospect KnewOne")
            .WithArgs(new OnlinePlayerArgParser("player", api, isMandatoryArg: false))
            .HandleWith(Reprospect)
			.EndSub();
	}

    private TextCommandResult CancelReprospect(TextCommandCallingArgs args) {
        var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
        isReprospecting = false;
        return TextCommandResult.Success("Done");
    }

	private TextCommandResult Reprospect(TextCommandCallingArgs args) {
		if (isReprospecting) {
			return TextCommandResult.Error("Please wait before the previous command ends");
		}

		var caller = args.Caller.Player as IServerPlayer;
		var targetPlayer = args.Parsers[0].GetValue() as IServerPlayer;

		// Background
		Task.Run(() => { _ = ReprospectTask(caller, targetPlayer); });

        return TextCommandResult.Success("[BetterEr Prospecting] Began reprospecting");
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

    private bool isReprospecting;
    private readonly Lock notifyLock = new();

	// This might create lag or memory issues. Need more feedback on large world/many players
	public async Task ReprospectTask(IServerPlayer caller, IServerPlayer targetPlayer) {
        int processed = 0;
        int lastPercentNotified = -1;
        DateTime lastNotifyTime = DateTime.UtcNow;

        try {
            logger.Notification("Reprospecting started by {0} on {1}", caller == null ? "console" : caller.PlayerName, targetPlayer == null ? "all" : $"{targetPlayer} player");

			var oml = sapi.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is OreMapLayer) as OreMapLayer;

			if (oml == null) {
				return;
			}

			if (isReprospecting)
				return;
			isReprospecting = true;
			chunkLoadTasks.Clear(); // Reruns

            int allPlayerReadingsCount = PptTracker.getAllPlayerReadings(sapi).Count;

            void NotifyCallerProgressIfNeeded() {
                int current = Volatile.Read(ref processed);
                int percent = current / allPlayerReadingsCount * 100;

                lock (notifyLock) {
                    // throttle time
                    if ((DateTime.UtcNow - lastNotifyTime).Seconds < 20)
                        return;

                    // threshold step (e.g. every 10%)
                    if (percent / 10 == lastPercentNotified / 10)
                        return;

                    lastPercentNotified = percent;
                    lastNotifyTime = DateTime.UtcNow;
                }

                var message = $"[BetterEr Prospecting] Reprospect progress: {percent}% ({current}/{allPlayerReadingsCount})";

                caller?.SendMessage(GlobalConstants.AllChatGroups, message, EnumChatType.Notification);
                logger.Notification(message);
            }

            var localLists = new ConcurrentQueue<(string oreCode, double ppt)>();

            // We could process all readings at the same time, but that might cause a lot of ram usage. Let's stick to oml per player ( huge cope )
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
                var updatedReadings = new PropickReading[readings.Count];
                int index = 0;

                await Parallel.ForEachAsync(
                    readings,
                    new ParallelOptions {
                        MaxDegreeOfParallelism = BetterErProspect.Config.ReprospectParellelism > 0 ? BetterErProspect.Config.ReprospectParellelism : Environment.ProcessorCount / 4,
                        CancellationToken = _cts.Token
                    },
                    async (reading, ct) => {
                        int i = Interlocked.Increment(ref index) - 1;
                        var result = reading;
                        var readingBlock = reading.Position.AsBlockPos;
                        var readingChunk = sapi.WorldManager.GetChunk(readingBlock);

                        if (readingChunk != null) {
                            generateReadigs(
                                sapi,
                                readingBlock,
                                GenerateBlockData(sapi, readingBlock),
                                out PropickReading newReading,
                                out List<(string oreCode, double ppt)> updatePairs
                            );
                            updatePairs.ForEach(localLists.Enqueue);
                            result = newReading;
                        }

                        Interlocked.Increment(ref processed);
                        NotifyCallerProgressIfNeeded();
                        updatedReadings[i] = result;
                    }
                );

				// Step 4: Replace readings safely
				readings.Clear();
				readings.AddRange(updatedReadings);
			}

            pptTracker?.UpdatePpt(localLists.ToList());

			// Step 5: Serialize per player in parallel
			var savegame = sapi.WorldManager.SaveGame;
			var serializeTasks = oml.PropickReadingsByPlayer.Select(val => Task.Run(() => {
				using var ms = new FastMemoryStream();
				savegame.StoreData("oreMapMarkers-" + val.Key, SerializerUtil.Serialize(val.Value, ms));
			})).ToArray();

			await Task.WhenAll(serializeTasks);

			// Step 6: Clear offline players
			var onlineUids = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToHashSet();
            oml.PropickReadingsByPlayer.RemoveAllByKey(onlineUids.Contains);

			logger.Notification("[BetterEr Prospecting] Reprospecting finished");
		} catch (Exception ex) {
			logger.Error("[BetterEr Prospecting] Error during reprospecting: {0}", ex);
		} finally {
			isReprospecting = false;
			caller?.SendMessage(GlobalConstants.AllChatGroups,
                $"[BetterEr Prospecting] Finished reprospecting. Processed {processed} readings.",
				EnumChatType.Notification);
		}
	}


	public static Dictionary<string, int> GenerateBlockData(ICoreServerAPI api, BlockPos blockPos, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= [];
		const int radius = ItemBetterErProspectingPick.densityRadius;

		int mapHeight = api.World.BlockAccessor.GetTerrainMapheightAt(blockPos);
        var blacklistedCodes = BetterErProspect.Config.DensityBlackListedOres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToHashSet();

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
				if (blacklistedCodes.Contains(key))
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

    /// Note: The readings aren't updated, that must be done manually from the update pairs.
    /// <returns>Bool stating if readings have been generated successfully</returns>
    public static bool generateReadigs(ICoreServerAPI sapi, BlockPos blockPos, Dictionary<string, int> codeToFoundOre, out PropickReading readings,
        out List<(string oreCode, double ppt)> updatePairs, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= [];

		var world = sapi.World;
		var deposits = sapi.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(world.Api, "propickworkspace");
		if (deposits == null) {
			readings = null;
            updatePairs = [];
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

        updatePairs = new List<(string oreCode, double ppt)>();

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
