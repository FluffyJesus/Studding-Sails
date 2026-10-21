using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StuddingSails
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("fluffyjesus.sailwind.topsailyards", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "fluffyjesus.sailwind.studdingsails";
        public const string NAME = "Studding Sails";
        public const string VERSION = "1.2.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Studding.Init(Config);

            new Harmony(GUID).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{NAME} {VERSION} loaded successfully!");
        }

        private void Update()
        {
            if (Studding.ToggleKey != null && Input.GetKeyDown(Studding.ToggleKey.Value) && !StuddingWindow.Open)
                Studding.ToggleDeployed();

            StuddingWindow.Update();
        }

        private void OnGUI()
        {
            StuddingWindow.OnGUI();
        }

        /// <summary>Sails that never get studding sails.</summary>
        internal static bool IsExcludedSail(Sail sail)
        {
            if (sail == null) return true;
            if (sail.junkType) return true;

            string n = (sail.name + " " + sail.sailName).ToLowerInvariant();

            if (n.Contains("wide") && n.Contains("square")) return true;
            if (n.Contains("sprit")) return true;
            if (n.Contains("junk")) return true;

            return false;
        }
    }
}
