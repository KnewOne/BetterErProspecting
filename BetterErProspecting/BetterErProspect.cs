using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterErProspecting.Config;
using BetterErProspecting.Item;
using BetterErProspecting.Patches;
using ConfigLib;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace BetterErProspecting;

public class BetterErProspect : ModSystem {
	public static ILogger Logger { get; private set; }
	public static ICoreAPI Api { get; private set; }
	public static ModConfig Config => ModConfig.Instance;
    public static Harmony harmony { get; private set; }
    public static string ModId { get; private set; }

    public override void Start(ICoreAPI api) {
		base.Start(api);
        ModId = Mod.Info.ModID;

        harmony = new Harmony(ModId);
		Api = api;
		Logger = Mod.Logger;

		try {
            LoadFileConfig();
            api.StoreModConfig(Config, ModConfig.ConfigName);
		} catch (Exception) { ModConfig.Instance = new ModConfig(); }

        ConfigManager.handle();
        PatchManager.handle();
        api.RegisterItemClass("ItemProspectingPick", typeof(ItemBetterErProspectingPick));
        Logger.Debug("ItemProspectingPick item re-registered to mod's implementation");
	}

    public static void LoadFileConfig() {
        ModConfig.Instance = Api.LoadModConfig<ModConfig>(ModConfig.ConfigName);
        if (ModConfig.Instance != null) return;
        ModConfig.Instance = new ModConfig();
        Logger.Notification("Failed to load config from file. Creating a default config");
    }



	public override void Dispose() {
        harmony?.UnpatchAll(ModId);
		ModConfig.Instance = null;
		harmony = null;
		Logger = null;
		Api = null;
		base.Dispose();
	}
}

