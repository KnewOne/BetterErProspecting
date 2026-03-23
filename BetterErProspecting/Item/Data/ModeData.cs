using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BetterErProspecting.Item.Data;

public class ModeData {
    // Hold texture here in case we want to dispose it and reload in future mod versions
    public AssetLocation TextureAssetLocation;
	public SkillItem Skill;

    // Controlls whether the mode should appear in gui in case you want to controll it after config changes.
    public bool Enabled = true;

    /// <summary>
    /// Executes the probe mode. Returns damage dealt to the propick.
    /// </summary>
    public System.Func<IWorldAccessor, IServerPlayer, ItemSlot, BlockSelection, int> Execute;

    public ModeData(
        string mode,
        string assetPath,
        System.Func<IWorldAccessor, IServerPlayer, ItemSlot, BlockSelection, int> execute = null,
        string nameKey = null,
        string domain = "game") {
        TextureAssetLocation = $"{domain}:{assetPath}";
        Skill = new SkillItem { Code = new AssetLocation($"{domain}:{mode}") };
        Execute = execute;

        if (nameKey != null)
            Skill.Name = Lang.Get(nameKey);
    }
}

