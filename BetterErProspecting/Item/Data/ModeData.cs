using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace BetterErProspecting.Item.Data;

public class ModeData {
	public AssetLocation Asset;
	public LoadedTexture Texture;
	public SkillItem Skill;

    // Controlls whether the mode should appear in gui in case you want to controll it after config changes.
    public bool Enabled = true;

    /// <summary>
    /// Executes the probe mode. Returns damage dealt to the propick.
    /// </summary>
    public System.Func<IWorldAccessor, IPlayer, ItemSlot, BlockSelection, int> Execute;

    public ModeData(string mode, string assetPath, string nameKey = null, string domain = null) {
		Asset = domain == null ? new AssetLocation(assetPath) : new AssetLocation(domain, assetPath);
        Skill = new SkillItem { Code = new AssetLocation(mode) };
        if (nameKey != null) Skill.Name = Lang.Get(nameKey);
	}
}

