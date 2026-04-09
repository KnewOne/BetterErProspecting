using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterErProspecting.Prospecting;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace BetterErProspecting.Tracking;

[ProtoContract]
public class PptData {
	[ProtoMember(1)]
	public double MinPpt = 1000.0;

    [ProtoMember(2)] public double MaxPpt;

    [ProtoIgnore] private readonly object _lock = new();

	public void Update(double ppt) {
		lock (_lock) {
			if (ppt < MinPpt) MinPpt = ppt;
			if (ppt > MaxPpt) MaxPpt = ppt;
		}
	}
}

[ProtoContract]
public class PptDataPacket(Dictionary<string, PptData> data) {
    [ProtoMember(1)] public Dictionary<string, PptData> codeToData { get; set; } = data;

    public PptDataPacket() : this(new Dictionary<string, PptData>()) {
    }
}

public class PptTracker : ModSystem {
	public static readonly ConcurrentDictionary<string, PptData> oreData = new();
	private const string SaveKey = "betterErProspectingPptData";
	private const string ChannelName = "bettererprospecting_ppt";

	private IServerNetworkChannel serverChannel;
	private IClientNetworkChannel clientChannel;
	private ICoreServerAPI sapi;
	private ICoreClientAPI capi;


	public override void StartServerSide(ICoreServerAPI api) {
		base.StartServerSide(api);
		sapi = api;

		sapi.ChatCommands.GetOrCreate("btrpr")
			.RequiresPrivilege(Privilege.controlserver)
			.BeginSub("oreData")
				.RequiresPlayer()
				.WithDesc("Dumps all ores data from memory and file storage and rewrites from all existing ore readings. Best to reprospect first. May cause lags.")
				.WithExamples("/btrpr oreData")
				.HandleWith(DumpAndReload)
			.EndSub();

		serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<PptDataPacket>();

		api.Event.PlayerJoin += OnPlayerJoin;
		api.Event.SaveGameLoaded += OnSaveGameLoaded;
		api.Event.GameWorldSave += OnSaveGameGettingSaved;
	}

	public override void StartClientSide(ICoreClientAPI api) {
		base.StartClientSide(api);
		capi = api;
        oreData.Clear();

		clientChannel = api.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<PptDataPacket>()
            .SetMessageHandler<PptDataPacket>(OnClientReceivePacket);
	}

	private void OnSaveGameLoaded() {
		oreData.Clear();

		byte[] savedData = sapi.WorldManager.SaveGame.GetData(SaveKey);
		if (savedData != null) {
			var loaded = SerializerUtil.Deserialize<Dictionary<string, PptData>>(savedData);
			if (loaded == null) return;
			foreach (var kvp in loaded) {
				oreData[kvp.Key] = kvp.Value;
			}
			Mod.Logger.Debug($"[BetterErProspecting] Loaded ppt data for {loaded.Count} ore codes from save");
		} else {
			// Absolute cold start. Lets normalize all readings
			sapi.ModLoader.GetModSystem<ProspectingSystem>().ReprospectTask(null, null).Wait();
			// We need the page codes and ppws delays to async RunGame phase action
			sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => { ScheduleBackfillWhenReady(); });
		}
	}

	private void OnSaveGameGettingSaved() {
		if (oreData.IsEmpty) {
			Mod.Logger.Debug("[BetterErProspecting] No data to save");
			return;
		}

		using var ms = new FastMemoryStream();
		var dataToSave = new Dictionary<string, PptData>(oreData);
		sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(dataToSave, ms));

		Mod.Logger.Debug($"[BetterErProspecting] Saved ppt data for {dataToSave.Count} ore codes");
	}

	private void OnPlayerJoin(IServerPlayer byPlayer) {
		if (oreData.IsEmpty)
			return;

		var packet = new PptDataPacket(new Dictionary<string, PptData>(oreData));
		serverChannel?.SendPacket(packet, byPlayer);
	}

    private void OnClientReceivePacket(PptDataPacket packet) {
        if (packet?.codeToData == null)
            return;

        foreach (var kvp in packet.codeToData) {
            oreData[kvp.Key] = kvp.Value;
        }

        Mod.Logger.Debug($"[BetterErProspecting] Client received ppt data for ({string.Join(", ", packet.codeToData.Keys)}) ore codes");
    }

    public void UpdatePpt(List<(string oreCode, double ppt)> updatePairs) {
        var updatePacket = new PptDataPacket(new Dictionary<string, PptData>());

        foreach (var pair in updatePairs) {
            if (string.IsNullOrEmpty(pair.oreCode))
                return;

            var data = oreData.GetOrAdd(pair.oreCode, _ => new PptData());
            data.Update(pair.ppt);

            updatePacket.codeToData[pair.oreCode] = data;
        }

        serverChannel?.BroadcastPacket(updatePacket);
	}

	public void AdjustFactor(PropickReading readings) {
		foreach (var reading in readings.OreReadings) {
			reading.Value.DepositCode = reading.Key;
			AdjustFactor(reading.Value);
		}
	}

	public void AdjustFactor(Dictionary<string, OreReading> OreReadings) {
		foreach (var reading in OreReadings) {
			reading.Value.DepositCode = reading.Key;
			AdjustFactor(reading.Value);
		}
	}

	public void AdjustFactor(List<PropickReading> readings) {
		foreach (var reading in readings) {
			AdjustFactor(reading);
		}
	}


	public void AdjustFactor(OreReading reading) {
		reading.TotalFactor = GetAdjustedFactor(reading);
	}

	public double GetAdjustedFactor(OreReading reading) {
		if (reading?.DepositCode == null || reading.DepositCode.StartsWith("rock-")) {
			return reading?.TotalFactor ?? 0.0;
		}

		if (!oreData.TryGetValue(reading.DepositCode, out var data)) {
			return reading.TotalFactor;
		}

		if (Math.Abs(data.MaxPpt - data.MinPpt) < 0.0001) {
			// First reading or no variance
			return 1.0;
		}

		double normalizedValue = (reading.PartsPerThousand - data.MinPpt) / (data.MaxPpt - data.MinPpt);
		double adjustedFactor = 0.04 + (normalizedValue * 0.96);
		return Math.Clamp(adjustedFactor, 0.04, 1.0);
	}

	private TextCommandResult DumpAndReload(TextCommandCallingArgs args) {
		Mod.Logger.Notification($"[BetterErProspecting] Starting dump and reload of all ore readings data. Initiated by {args.Caller.Player.PlayerName}...");
		oreData.Clear();

		using (var ms = new FastMemoryStream()) {
			var emptyDict = new Dictionary<string, PptData>();
			sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(emptyDict, ms));
		}

		FillOreDataFromReadings();

		Mod.Logger.Notification($"[BetterErProspecting] Dump and reload complete. Tracked {oreData.Count} ore codes.");
		return TextCommandResult.Success($"Successfully reloaded ore readings data. Now tracking {oreData.Count} ore codes.");
	}

	private void ScheduleBackfillWhenReady(int attemptCount = 0) {
		const int maxAttempts = 30;

		sapi.Event.RegisterCallback((dt) =>
		{
			var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(sapi, "propickworkspace");
			if (ppws?.pageCodes is { Count: > 0 }) {
				FillOreDataFromReadings();
			} else if (attemptCount < maxAttempts) {
				ScheduleBackfillWhenReady(attemptCount + 1);
			} else {
                Mod.Logger.Error("[BetterErProspecting] Timed out waiting for ProPickWorkSpace pageCodes to be populated");
			}
        }, 1000);
	}

	private void FillOreDataFromReadings() {
		var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(sapi, "propickworkspace");
		if (ppws?.pageCodes == null) {
			Mod.Logger.Error("[BetterErProspecting] ProPickWorkSpace not available, couldn't perform backfill");
			return;
		}

		Mod.Logger.Notification($"[BetterErProspecting] Backfilling {ppws.pageCodes.Keys.Count} ore codes: {string.Join(", ", ppws.pageCodes.Keys)}");

		var allReadings = getAllPlayerReadings(sapi);

		// Update from each reading server-side
		foreach (var reading in allReadings) {
			foreach (var oreReading in reading.OreReadings) {
				var data = oreData.GetOrAdd(oreReading.Key, _ => new PptData());
				data.Update(oreReading.Value.PartsPerThousand);
			}
		}

        var updatePacket = new PptDataPacket(new Dictionary<string, PptData>());
		foreach (var data in oreData) {
            updatePacket.codeToData[data.Key] = data.Value;
		}

        serverChannel?.BroadcastPacket(updatePacket);

        Mod.Logger.Debug($"[BetterErProspecting] Backfilled from {allReadings.Count} readings");
	}

	// Fills per-player data in oml as well as returns list of all readings
	public static List<PropickReading> getAllPlayerReadings(ICoreServerAPI sapi) {
		var result = new List<PropickReading>();
		var oml = sapi.ModLoader?.GetModSystem<WorldMapManager>()?.MapLayers?.FirstOrDefault(ml => ml is OreMapLayer) as OreMapLayer;
		if (oml == null) {
			BetterErProspect.Logger.Warning("[BetterErProspecting] OreMapLayer not available for backfill");
			return result;
		}

		foreach (var playerUid in sapi.PlayerData.PlayerDataByUid.Keys) {
			result.AddRange(getOrLoadReadings(playerUid, oml, sapi));
		}

		return result;
	}

	public static List<PropickReading> getOrLoadReadings(string playeruid, OreMapLayer oml, ICoreServerAPI sapi) {
		if (oml.PropickReadingsByPlayer.TryGetValue(playeruid, out var orLoadReadings))
			return orLoadReadings;
		byte[] data = sapi.WorldManager.SaveGame.GetData("oreMapMarkers-" + playeruid);
		return data != null ? (oml.PropickReadingsByPlayer[playeruid] = SerializerUtil.Deserialize<List<PropickReading>>(data)) : (oml.PropickReadingsByPlayer[playeruid] = []);
	}

    public override void Dispose() {
        oreData.Clear();
        base.Dispose();
    }
}
