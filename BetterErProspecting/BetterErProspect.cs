using System;
using System.Linq;
using System.Reflection;
using BetterErProspecting.Config;
using BetterErProspecting.Item;
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
	private static Harmony harmony { get; set; }

	public static event Action ReloadTools;

	public override void Start(ICoreAPI api) {
		api.Logger.Debug("[BetterErProspecting] Starting...");
		base.Start(api);

		harmony = new Harmony(Mod.Info.ModID);
		Api = api;
		Logger = Mod.Logger;

		try {
			ModConfig.Instance = api.LoadModConfig<ModConfig>(ModConfig.ConfigName) ?? new ModConfig();
            api.StoreModConfig(Config, ModConfig.ConfigName);
		} catch (Exception) { ModConfig.Instance = new ModConfig(); }

		if (api.ModLoader.IsModEnabled("configlib")) {
            SubscribeToConfigChange();
		}

		PatchUnpatch();
		api.RegisterItemClass("ItemBetterErProspectingPick", typeof(ItemBetterErProspectingPick));
	}


    private void SubscribeToConfigChange() {
        ConfigLibModSystem system = Api.ModLoader.GetModSystem<ConfigLibModSystem>();
        // Server deals with
		system.SettingChanged += (domain, _, setting) => {
            if (domain != "bettererprospecting") return;
            setting.AssignSettingValue(Config);

            if (ModConfig.SettingsForceLoad.Contains(setting.YamlCode)) {
				ReloadTools?.Invoke();
			}

            if (ModConfig.SettingsPatch.Contains(setting.YamlCode)) {
				PatchUnpatch();
			}
		};

        if (Api.Side == EnumAppSide.Server || (Api as ICoreClientAPI)?.IsSinglePlayer == true) return;

        // When a client connects to a MP server, it might have outdated values
        system.ConfigsLoaded += () => {
            system.GetConfig("bettererprospecting")?.AssignSettingsValues(Config);
            PatchUnpatch();
            ReloadTools?.Invoke();

            // When a client modifies settings from his side in MP, we need to reload ~ SettingChanged doesn't seem to capture him
            // TODO: nag maltiez to (?) fix this
            (system.GetConfig("bettererprospecting") as ConfigLib.Config)!.ConfigSaved += cfg => {
                cfg.AssignSettingsValues(Config);
                ReloadTools?.Invoke();
                PatchUnpatch();
            };
        };
    }

	public override void Dispose() {
		harmony?.UnpatchAll(Mod.Info.ModID);
		ModConfig.Instance = null;
		harmony = null;
		Logger = null;
		Api = null;
		base.Dispose();
	}

	private void PatchUnpatch() {
		harmony.UnpatchAll(Mod.Info.ModID);
		harmony.PatchCategory(nameof(PatchCategory.Always));

		if (ModConfig.Instance.NewDensityMode) {
			harmony.PatchCategory(nameof(PatchCategory.NewDensity));
		}

		if (ModConfig.Instance.StoneSearchCreatesReadings) {
			harmony.PatchCategory(nameof(PatchCategory.StoneReadings));
		}

		if (Api.ModLoader.IsModEnabled("prospecttogether")) {
			UnpatchProspectTogether();
			harmony.PatchCategory(nameof(PatchCategory.ProspectTogetherCompat));
		}
	}

	// Maybe some better place ?
	private void UnpatchProspectTogether() {
		var original = AccessTools.Method(typeof(OreMapLayer), nameof(OreMapLayer.OnDataFromServer));
		var ptAssembly = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault(a => a.GetName().Name == "ProspectTogether");
		if (ptAssembly == null)
			return;

		var patchType = ptAssembly.GetType("ProspectTogether.Client.OreMapLayerPatch");
		var theirPrefix = patchType?.GetMethod("OnDataFromServer", BindingFlags.Static | BindingFlags.NonPublic);
		if (theirPrefix != null) {
			harmony.Unpatch(original, theirPrefix);
		}
	}

	public enum PatchCategory {
		Always,
		NewDensity,
		StoneReadings,
		ProspectTogetherCompat
	}
}

