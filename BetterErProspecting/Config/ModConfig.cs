namespace BetterErProspecting.Config;

public class ModConfig {
	public static string ConfigName = "BetterErProspecting.json";
	public static ModConfig Instance { get; set; } = new ModConfig();

    public static string[] SettingsForceLoad = [nameof(EnableDensityMode), nameof(NewDensityMode), nameof(AddBoreHoleMode), nameof(AddStoneMode), nameof(AddProximityMode)];
    public static string[] SettingsPatch = [nameof(NewDensityMode), nameof(StoneSearchCreatesReadings)];

	public bool EnableDensityMode = true;
	public bool OneShotDensity = false;


	public bool NewDensityMode = true;
	public int NewDensityDmg = 3;

    public string DensityBlackListedOres = "flint, quartz";

	public bool AddProximityMode = true;
	public bool ProximityVagueDescriptors = false;
	public int ProximitySearchRadius = 5;
	public int ProximityDmg = 2;

	public bool AddStoneMode = true;
	public bool StoneSearchCreatesReadings = false;
	public bool StonePercentSearch = true;
	public int StoneSearchRadius = 64;
	public int StoneDmg = 4;

	public bool AddBoreHoleMode = true;
	public int BoreholeRadius = 8;
	public int BoreholeDmg = 2;
	public bool BoreholeScansOre = true;
	public bool BoreholeScansStone = false;

	public bool DebugMode = false;
    public int ReprospectParellelism = 0;
}
