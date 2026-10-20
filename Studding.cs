using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace StuddingSails
{
    /// <summary>
    /// Marker on a square sail: "studding sails ordered". Also owns the visuals (StuddingRig).
    /// Thrust is added by StuddingPowerPatch.
    /// </summary>
    internal sealed class StuddingSailMarker : MonoBehaviour
    {
        public bool On;
        public float Width = 1f; // studding sail width multiplier (shipyard buttons)

        private Sail sail;
        private StuddingRig rig;

        private void Awake()
        {
            sail = GetComponent<Sail>();
        }

        private void LateUpdate()
        {
            if (sail == null) return;

            // Booms stay while the option is owned and set; the canvas only while the sail is unfurled
            bool want = On && Studding.Enabled.Value && Studding.ShowRig(sail);
            if (!want)
            {
                if (rig != null) rig.Hide();
                return;
            }

            if (rig == null) rig = new StuddingRig(sail);
            rig.Tick(Studding.IsDeployed(sail));
        }

        private void OnDisable()
        {
            // RopeEffect spawns separate objects that do not hide by themselves
            if (rig != null) rig.Hide();
        }

        private void OnDestroy()
        {
            if (rig != null) rig.Dispose();
            rig = null;
        }
    }

    /// <summary>Per-boat state: are studding sails set (toggled by hotkey).</summary>
    internal sealed class StuddingShip : MonoBehaviour
    {
        public bool Deployed = true; // set by default: they follow their sail when it is set or furled
    }

    /// <summary>A former debug config value, now hard-coded.</summary>
    internal sealed class Fixed<T>
    {
        public readonly T Value;
        public Fixed(T value) { Value = value; }
    }

    internal static class Studding
    {
        // SaveSailData.health: vanilla always writes 100 and never reads it, so we use it as a flag.
        internal const float SaveBase = 100f;
        internal const float SaveOn = 101f;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> PriceMultiplier;
        internal static readonly Fixed<float> ButtonOffsetX = new Fixed<float>(0f);
        internal static readonly Fixed<float> ButtonOffsetY = new Fixed<float>(0f);
        internal static readonly Fixed<float> ButtonOffsetZ = new Fixed<float>(0f);

        /// <summary>Set during Mast.LoadSail when the saved sail has studding sails.</summary>
        internal static bool PendingFromSave;
        internal static float PendingWidth = 1f;

        internal const float MinWidth = 0.5f, MaxWidth = 2f, WidthStep = 0.1f;

        /// <summary>Saved health: 101 = old format (width 1), 200 + width*100 = current format.</summary>
        internal static float EncodeSave(float width) { return 200f + Mathf.Round(width * 100f); }

        internal static bool DecodeSave(float health, out float width)
        {
            width = 1f;
            if (health > 150f)
            {
                width = Mathf.Clamp((health - 200f) / 100f, MinWidth, MaxWidth);
                return true;
            }
            return health > (SaveBase + SaveOn) * 0.5f;
        }

        internal static float GetWidth(Sail sail)
        {
            if (sail == null) return 1f;
            var m = sail.GetComponent<StuddingSailMarker>();
            return m != null ? m.Width : 1f;
        }

        internal static void SetWidth(Sail sail, float width)
        {
            if (sail == null) return;
            var m = sail.GetComponent<StuddingSailMarker>();
            if (m == null) return;
            m.Width = Mathf.Clamp(Mathf.Round(width * 10f) / 10f, MinWidth, MaxWidth);
        }

        internal static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Studding", "Enabled", true,
                "Offer studding sails for square sails at the shipyard.");
            PriceMultiplier = cfg.Bind("Studding", "PriceMultiplier", 0.35f,
                "Studding sail surcharge as a fraction of the sail price (scaled by studding sail width).");

            Cfg = cfg;
            cfg.SaveOnConfigSet = false; // the tuning window changes values every frame while dragging; saved by its button

            WindowKey = cfg.Bind("Studding", "WindowKey", KeyCode.F8,
                "Key: studding sail tuning window.");
            ToggleKey = cfg.Bind("Studding", "ToggleKey", KeyCode.L,
                "Key: set / stow studding sails (and their booms) on the boat you are on.");
            BoomOverlapRatio = cfg.Bind("Visuals", "BoomOverlapOnYard", 0.2f,
                "How far the studding sail boom lies along the main yard, as a fraction of the yard length.");
            PanelGap = cfg.Bind("Visuals", "PanelGap", 0.25f,
                "Gap (m) between the end of the main yard and the inner edge of the studding sail.");
            PanelHeightRatio = cfg.Bind("Visuals", "PanelHeightRatio", 0.9f,
                "Height of a four-sided studding sail as a fraction of the sail height, used when there is no studding boom below (the triangular one always reaches the clew).");
            HemSlack = cfg.Bind("Visuals", "HemSlack", 0.1f,
                "Foot roach of four-sided studding sails: extra cloth along the foot, as a fraction of its length (cut as an upward arc).");
            SideSlack = cfg.Bind("Visuals", "SideSlack", 0.02f,
                "Extra cloth along the side edges, as a fraction of their length.");
            ThrustMultiplier = cfg.Bind("Studding", "ThrustMultiplier", 1f,
                "Studding sail thrust: their area is added to the parent sail with this multiplier (0 = looks only).");
            UseSailTexture = cfg.Bind("Visuals", "UseSailTexture", true,
                "Use the parent sail's material and texture (off = plain built-in canvas).");
            Belly = cfg.Bind("Visuals", "Belly", 0.005f,
                "Belly: extra cloth across the width, as a fraction of the width.");
            Fullness = cfg.Bind("Visuals", "LengthSlack", 0.15f,
                "Extra cloth in height: the canvas is longer than the distance between the yards by this fraction, so it sags and fills.");
            BoomOffsetZ = cfg.Bind("Visuals", "BoomOffsetZ", 0f,
                "Manual fore/aft offset of the studding sail booms, m.");
            ShowRopes = cfg.Bind("Visuals", "ShowRopes", true,
                "Ropes: head lashings, sheets and halyards.");
        }

        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<KeyCode> WindowKey;
        internal static ConfigFile Cfg;
        internal static readonly Fixed<float> PanelWidthRatio = new Fixed<float>(0.24f);
        internal static readonly Fixed<bool> ClothPhysics = new Fixed<bool>(true);
        internal static readonly Fixed<float> MaxBillow = new Fixed<float>(3f);
        internal static readonly Fixed<float> ClothWind = new Fixed<float>(1.01f);
        internal static ConfigEntry<float> Fullness;
        internal static ConfigEntry<float> Belly;
        internal static ConfigEntry<bool> UseSailTexture;
        internal static ConfigEntry<float> ThrustMultiplier;
        internal static readonly Fixed<float> Flutter = new Fixed<float>(0f);
        internal static readonly Fixed<float> ClothCell = new Fixed<float>(0.6f);
        internal static ConfigEntry<float> HemSlack;
        internal static ConfigEntry<float> SideSlack;
        internal static readonly Fixed<bool> ClothWhileReefing = new Fixed<bool>(true);
        internal static readonly Fixed<float> ClothBending = new Fixed<float>(0.4f);
        internal static ConfigEntry<float> BoomOffsetZ;
        internal static ConfigEntry<float> BoomOverlapRatio;
        internal static ConfigEntry<float> PanelGap;
        internal static ConfigEntry<float> PanelHeightRatio;
        internal static ConfigEntry<bool> ShowRopes;
        internal static readonly Fixed<bool> VerboseLog = new Fixed<bool>(false);

        private static readonly FieldInfo CurrentBoatField = AccessTools.Field(typeof(GameState), "currentBoat");
        private static readonly FieldInfo LastBoatField = AccessTools.Field(typeof(GameState), "lastBoat");

        /// <summary>Player's boat: the object with BoatRefs (same as Mast.shipRigidbody).</summary>
        internal static GameObject GetPlayerShip()
        {
            Transform t = null;
            if (CurrentBoatField != null) t = CurrentBoatField.GetValue(null) as Transform;
            if (t == null && LastBoatField != null) t = LastBoatField.GetValue(null) as Transform;
            if (t == null) return null;

            var refs = t.GetComponent<BoatRefs>();
            if (refs == null) refs = t.GetComponentInChildren<BoatRefs>();
            if (refs == null) refs = t.GetComponentInParent<BoatRefs>();
            return refs != null ? refs.gameObject : null;
        }

        internal static GameObject ShipOf(Sail sail)
        {
            if (sail == null) return null;
            if (sail.shipRigidbody != null) return sail.shipRigidbody.gameObject;

            var mast = sail.transform.parent != null ? sail.transform.parent.GetComponent<Mast>() : null;
            if (mast != null && mast.shipRigidbody != null) return mast.shipRigidbody.gameObject;
            return null;
        }

        internal static void ToggleDeployed()
        {
            GameObject ship = GetPlayerShip();
            if (ship == null)
            {
                Plugin.Log.LogInfo("Studding: no current boat, nothing to toggle.");
                return;
            }

            var st = ship.GetComponent<StuddingShip>();
            if (st == null) st = ship.AddComponent<StuddingShip>();
            st.Deployed = !st.Deployed;
            Plugin.Log.LogInfo("Studding: studding sails " + (st.Deployed ? "set" : "stowed") + " on " + ship.name);
        }

        /// <summary>
        /// Booms and everything else: always at the shipyard (preview); at sea on an installed sail
        /// if studding sails are set by the hotkey (yes by default).
        /// </summary>
        internal static bool ShowRig(Sail sail)
        {
            if (sail == null) return false;
            if (GameState.currentShipyard != null) return true;
            if (!sail.IsInstalled()) return false;

            GameObject ship = ShipOf(sail);
            var st = ship != null ? ship.GetComponent<StuddingShip>() : null;
            return st == null || st.Deployed;
        }

        /// <summary>Canvas is set: always at the shipyard; at sea together with the parent sail.</summary>
        internal static bool IsDeployed(Sail sail)
        {
            if (sail == null) return false;
            if (GameState.currentShipyard != null) return true;
            return sail.currentUnroll >= 0.04f;
        }

        /// <summary>Studding sails are only offered for regular square sails.</summary>
        internal static bool CanHave(Sail sail)
        {
            return Enabled.Value && sail != null && sail.squareSail && !Plugin.IsExcludedSail(sail);
        }

        /// <summary>Raw marker state (independent of the config so no data is lost).</summary>
        internal static bool IsOn(Sail sail)
        {
            if (sail == null) return false;
            var m = sail.GetComponent<StuddingSailMarker>();
            return m != null && m.On;
        }

        internal static void Set(Sail sail, bool on)
        {
            if (sail == null) return;

            var m = sail.GetComponent<StuddingSailMarker>();
            if (m == null)
            {
                if (!on) return;
                m = sail.gameObject.AddComponent<StuddingSailMarker>();
            }
            m.On = on;
        }
    }

    // ---------------------------------------------------------------- price

    /// <summary>
    /// The sail price feeds both the order (Shipyard.UpdateOrder) and the refund (DestroySail/DestroySelectedSail),
    /// so patching GetSailPrice is enough.
    /// </summary>
    [HarmonyPatch(typeof(Sail), "GetSailPrice")]
    internal static class StuddingPricePatch
    {
        private static void Postfix(Sail __instance, ref float __result)
        {
            if (Studding.Enabled.Value && Studding.IsOn(__instance))
                __result *= 1f + Studding.PriceMultiplier.Value * Studding.GetWidth(__instance);
        }
    }

    // ---------------------------------------------------------------- thrust

    /// <summary>
    /// The area of set studding sails is simply added to the sail power. The vanilla force already accounts for
    /// wind angle, shadowing and reefing, so studding sails work under the same conditions as their sail.
    /// One dictionary lookup per call, no extra math.
    /// </summary>
    [HarmonyPatch(typeof(Sail), "GetRealSailPower")]
    internal static class StuddingPowerPatch
    {
        private static void Postfix(Sail __instance, ref float __result)
        {
            StuddingRig rig = StuddingRig.Of(__instance);
            if (rig == null || !rig.Visible || !rig.PanelsShown) return;
            __result += rig.Area * Debugger.instance.debugSailAreaMult * Studding.ThrustMultiplier.Value;
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Writes the flag into health. Mast/sail iteration mirrors vanilla GetData.</summary>
    [HarmonyPatch(typeof(SaveableBoatCustomization), "GetData")]
    internal static class StuddingSaveGetPatch
    {
        private static readonly FieldInfo RefsField = AccessTools.Field(typeof(SaveableBoatCustomization), "refs");

        private static void Postfix(SaveableBoatCustomization __instance, SaveBoatCustomizationData __result)
        {
            try
            {
                if (RefsField == null || __result == null) return;

                var refs = RefsField.GetValue(__instance) as BoatRefs;
                if (refs == null || refs.masts == null) return;

                int i = 0;
                foreach (var mast in refs.masts)
                {
                    if (!mast || !mast.gameObject.activeSelf) continue;

                    foreach (var go in mast.sails)
                    {
                        if (i >= __result.sails.Count) return;

                        Sail sl = go.GetComponent<Sail>();
                        if (Studding.IsOn(sl))
                            __result.sails[i].health = Studding.EncodeSave(Studding.GetWidth(sl));

                        i++;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Studding: save failed: " + e);
            }
        }
    }

    /// <summary>When a sail is loaded, remember the flag until AttachSailToMast runs.</summary>
    [HarmonyPatch(typeof(Mast), "LoadSail")]
    internal static class StuddingLoadSailPatch
    {
        private static void Prefix(SaveSailData sailData)
        {
            float w = 1f;
            Studding.PendingFromSave = sailData != null && Studding.DecodeSave(sailData.health, out w);
            Studding.PendingWidth = Studding.PendingFromSave ? w : 1f;
        }

        private static void Postfix()
        {
            Studding.PendingFromSave = false;
        }
    }

    /// <summary>AttachSailToMast receives the sail object: attach the marker to it.</summary>
    [HarmonyPatch(typeof(Mast), "AttachSailToMast")]
    internal static class StuddingAttachPatch
    {
        private static void Postfix(GameObject sailObject)
        {
            if (!Studding.PendingFromSave) return;
            Studding.PendingFromSave = false;

            var sail = sailObject != null ? sailObject.GetComponent<Sail>() : null;
            if (sail != null && sail.squareSail)
            {
                Studding.Set(sail, true);
                Studding.SetWidth(sail, Studding.PendingWidth);
            }
        }
    }

    // ---------------------------------------------------------------- shipyard

    /// <summary>Shipyard button: a clone of the width button with our action.</summary>
    internal sealed class StuddingToggleButton : ShipyardButton
    {
        /// <summary>0 = add/remove studding sails, +1 = wider, -1 = narrower.</summary>
        public int Action;

        public override void OnActivate()
        {
            UISoundPlayer.instance.PlayUISound(UISounds.buttonHover, 0.33f, 3f);

            ShipyardUI ui = ShipyardUI.instance;
            ui.HideNewSailButtons();
            ui.CloseSailCategoryMenu();

            if (GameState.currentShipyard != null)
            {
                Sail sail = GameState.currentShipyard.sailInstaller.GetCurrentSail();
                if (sail != null && !sail.IsInstalled() && Studding.CanHave(sail))
                {
                    if (Action == 0) Studding.Set(sail, !Studding.IsOn(sail));
                    else if (Studding.IsOn(sail))
                        Studding.SetWidth(sail, Studding.GetWidth(sail) + Action * Studding.WidthStep);
                    GameState.currentShipyard.UpdateOrder();
                    ui.UpdateDescriptionText();
                }
            }

            ui.RefreshButtons();
        }
    }

    /// <summary>
    /// The buttons are shown together with the vanilla move/scale buttons:
    /// for the selected, not yet installed square sail.
    /// </summary>
    [HarmonyPatch(typeof(ShipyardUI), "UpdateMoveButtons")]
    internal static class StuddingUiPatch
    {
        private static readonly FieldInfo WidthUp = AccessTools.Field(typeof(ShipyardUI), "widthUpButton");
        private static readonly FieldInfo WidthDown = AccessTools.Field(typeof(ShipyardUI), "widthDownButton");
        private static readonly FieldInfo ScaleUp = AccessTools.Field(typeof(ShipyardUI), "scaleUpButton");
        private static readonly FieldInfo ScaleDown = AccessTools.Field(typeof(ShipyardUI), "scaleDownButton");

        private static readonly GameObject[] buttons = new GameObject[3]; // 0 on/off, 1 wider, 2 narrower
        private static readonly int[] actions = { 0, 1, -1 };

        private static void Postfix(ShipyardUI __instance)
        {
            try
            {
                if (!Studding.Enabled.Value || GameState.currentShipyard == null)
                {
                    foreach (var b in buttons) if (b != null) b.SetActive(false);
                    return;
                }

                Sail sail = GameState.currentShipyard.sailInstaller.GetCurrentSail();
                bool show = sail != null && !sail.IsInstalled() && Studding.CanHave(sail);
                bool on = show && Studding.IsOn(sail);

                for (int i = 0; i < buttons.Length; i++)
                {
                    bool want = i == 0 ? show : on;
                    if (buttons[i] == null)
                    {
                        if (!want) continue;
                        buttons[i] = CreateButton(__instance, i);
                        if (buttons[i] == null) continue;
                    }

                    buttons[i].SetActive(want);
                    if (!want) continue;

                    string text;
                    if (i == 0) text = on ? "Studding: On" : "Studding: Off";
                    else if (i == 1) text = "Studding +";
                    else text = "Studding -";
                    buttons[i].GetComponent<ShipyardButton>().SetText(text);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Studding: shipyard button failed: " + e);
            }
        }

        private static GameObject CreateButton(ShipyardUI ui, int index)
        {
            if (WidthUp == null || WidthDown == null) return null;

            var up = WidthUp.GetValue(ui) as GameObject;
            var down = WidthDown.GetValue(ui) as GameObject;
            if (up == null || down == null) return null;

            var go = UnityEngine.Object.Instantiate(up, up.transform.parent);
            go.name = "StuddingSailButton" + index;

            // Strip the vanilla behaviour from the clone: ShipyardButton and any outline
            var old = go.GetComponent<ShipyardButton>();
            if (old != null) UnityEngine.Object.DestroyImmediate(old);

            foreach (var c in go.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "Outline")
                    UnityEngine.Object.DestroyImmediate(c);
            }

            go.AddComponent<StuddingToggleButton>().Action = actions[index];

            // Column left of "- Width": "Studding: On/Off" in the Width row, "-" above it, "+" on top.
            Vector3 hStep = down.transform.localPosition - up.transform.localPosition;
            Vector3 vStep = Vector3.zero;
            var sUp = ScaleUp != null ? ScaleUp.GetValue(ui) as GameObject : null;
            var sDown = ScaleDown != null ? ScaleDown.GetValue(ui) as GameObject : null;
            if (sUp != null && sDown != null) vStep = sDown.transform.localPosition - sUp.transform.localPosition;
            else vStep = new Vector3(-hStep.y, hStep.x, hStep.z); // fallback: rotate the step by 90 degrees

            Vector3 basePos = down.transform.localPosition + hStep;
            int rowsUp = index == 0 ? 0 : (index == 1 ? 2 : 1); // 1 = "+" on top, 2 = "-" in the middle
            go.transform.localPosition = basePos - vStep * rowsUp + new Vector3(
                Studding.ButtonOffsetX.Value,
                Studding.ButtonOffsetY.Value,
                Studding.ButtonOffsetZ.Value);

            return go;
        }
    }

    /// <summary>Line in the selected sail description.</summary>
    [HarmonyPatch(typeof(ShipyardUI), "UpdateDescriptionText")]
    internal static class StuddingDescriptionPatch
    {
        private static readonly FieldInfo DescText = AccessTools.Field(typeof(ShipyardUI), "descText");

        private static void Postfix(ShipyardUI __instance)
        {
            try
            {
                if (!Studding.Enabled.Value || DescText == null) return;
                if (GameState.currentShipyard == null) return;

                Sail sail = GameState.currentShipyard.sailInstaller.GetCurrentSail();
                if (sail == null || !Studding.IsOn(sail)) return;

                var tm = DescText.GetValue(__instance) as TextMesh;
                if (tm != null)
                    tm.text += "\nstudding sails: yes (" + Mathf.RoundToInt(Studding.GetWidth(sail) * 100f) + "%)";
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Studding: description failed: " + e);
            }
        }
    }
}
