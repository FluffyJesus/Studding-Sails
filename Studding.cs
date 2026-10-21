using System;
using System.Collections.Generic;
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
        public float Arm = -1f;  // yardarm extension, m (shipyard buttons); negative = automatic
        public float Foot;       // inner foot corner moved along the boom below, m (negative = in, over the sail below)

        private Sail sail;
        private StuddingRig rig;

        private void Awake()
        {
            sail = GetComponent<Sail>();
            // a marker that wakes up already On was copied along with its sail object (Instantiate)
            if (On) Plugin.Log.LogWarning("[Studding] marker woke up ON on " + Studding.Describe(sail) + " (copied object?)");
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
            rig.Tick(Studding.Deploy(sail, 0), Studding.Deploy(sail, 1));
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
        internal static float PendingArm = -1f;
        internal static float PendingFoot;

        internal const float MinWidth = 0.5f, MaxWidth = 2f, WidthStep = 0.1f;

        internal const float ArmStep = 0.1f, MaxArm = 3f;
        internal const float FootStep = 0.1f, MaxFoot = 2f;

        /// <summary>
        /// Saved health: 101 = old format (width 1); 200 + width*100 + 1000*a + 100000*f = current format:
        /// a = 0 for an automatic yardarm, otherwise the yardarm extension in 10 cm steps plus one (up to 31);
        /// f = 0 for no foot shift, otherwise the foot shift in 10 cm steps plus 21 (1..41).
        /// Saves of 1.0-1.2 (a = f = 0) load as before. All values are whole numbers well inside float precision.
        /// </summary>
        internal static float EncodeSave(float width, float arm, float foot)
        {
            int a = arm < 0f ? 0 : Mathf.RoundToInt(arm / ArmStep) + 1;
            int fs = Mathf.RoundToInt(foot / FootStep);
            int f = fs == 0 ? 0 : fs + 21;
            return 200f + Mathf.Round(width * 100f) + 1000f * a + 100000f * f;
        }

        internal static bool DecodeSave(float health, out float width, out float arm, out float foot)
        {
            width = 1f;
            arm = -1f;
            foot = 0f;
            if (health > 150f)
            {
                float rest = Mathf.Round(health - 200f);
                int f = Mathf.FloorToInt(rest / 100000f);
                rest -= 100000f * f;
                int a = Mathf.FloorToInt(rest / 1000f);
                width = Mathf.Clamp((rest - 1000f * a) / 100f, MinWidth, MaxWidth);
                if (a > 0) arm = Mathf.Clamp((a - 1) * ArmStep, 0f, MaxArm);
                if (f > 0) foot = Mathf.Clamp((f - 21) * FootStep, -MaxFoot, MaxFoot);
                return true;
            }
            return health > (SaveBase + SaveOn) * 0.5f;
        }

        /// <summary>Inner foot corner of the four-sided studding sail moved along the boom below, m.</summary>
        internal static float GetFootShift(Sail sail)
        {
            if (sail == null) return 0f;
            var m = sail.GetComponent<StuddingSailMarker>();
            return m != null ? m.Foot : 0f;
        }

        internal static void SetFootShift(Sail sail, float foot)
        {
            if (sail == null) return;
            var m = sail.GetComponent<StuddingSailMarker>();
            if (m == null) return;
            m.Foot = Mathf.Clamp(Mathf.Round(foot / FootStep) * FootStep, -MaxFoot, MaxFoot);
        }

        /// <summary>Yardarm extension set at the shipyard, m; negative = automatic (see AutoArm).</summary>
        internal static float GetArm(Sail sail)
        {
            if (sail == null) return -1f;
            var m = sail.GetComponent<StuddingSailMarker>();
            return m != null ? m.Arm : -1f;
        }

        internal static void SetArm(Sail sail, float arm)
        {
            if (sail == null) return;
            var m = sail.GetComponent<StuddingSailMarker>();
            if (m == null) return;
            m.Arm = arm < 0f ? -1f : Mathf.Clamp(Mathf.Round(arm / ArmStep) * ArmStep, 0f, MaxArm);
        }

        /// <summary>Studding sail width at the head, m (the same rule the rig lays it out with).</summary>
        internal static float PanelWidth(Sail sail)
        {
            YardInfo info = StuddingGeom.Get(sail);
            if (info == null || !info.Valid) return 0f;
            return Mathf.Max(0.3f, info.Half * 2f * PanelWidthRatio.Value * GetWidth(sail));
        }

        /// <summary>
        /// Automatic yardarm extension: the studding sail yard hangs from the yardarm by its middle, so this
        /// makes it lap over the sail by HeadOverlap of its width.
        /// </summary>
        internal static float AutoArm(Sail sail)
        {
            return PanelWidth(sail) * (0.5f - Mathf.Clamp(HeadOverlap.Value, 0f, 0.5f));
        }

        /// <summary>The yardarm extension in use, m.</summary>
        internal static float ArmOf(Sail sail)
        {
            float a = GetArm(sail);
            return a >= 0f ? a : AutoArm(sail);
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
                "Key: rig / unrig studding sails (hide booms and canvas) on the boat you are on.");
            UseWinch = cfg.Bind("Studding", "UseWinch", true,
                "Set studding sails with a winch at the mast (off = they follow their sail, the key sets/stows them).");
            VanillaBoats = cfg.Bind("Studding", "VanillaBoats",
                "BOAT dhow small (10),BOAT dhow medium (20),BOAT dhow large (30),BOAT medi small (40)," +
                "BOAT medi medium (50),BOAT junk large (70),BOAT junk medium (80),BOAT junk small singleroof(90)",
                "Comma-separated parts of vanilla boat object names. Their studding sail winch is stacked above the " +
                "mast's own winches; on any other (modded) boat it is mounted on the mast on a peg. Empty = all boats vanilla.");
            WinchOffsetX = cfg.Bind("Studding", "WinchOffsetX", 0f,
                "Extra offset of the studding sail winch, local X (if it overlaps something).");
            WinchOffsetY = cfg.Bind("Studding", "WinchOffsetY", 0f,
                "Extra offset of the studding sail winch, local Y.");
            WinchOffsetZ = cfg.Bind("Studding", "WinchOffsetZ", 0f,
                "Extra offset of the studding sail winch, local Z.");
            BoomOverlapRatio = cfg.Bind("Visuals", "BoomOverlapOnYard", 0.2f,
                "How far the studding sail boom lies along the main yard, as a fraction of the yard length.");
            PanelGap = cfg.Bind("Visuals", "PanelGap", 0.25f,
                "Gap (m) between the end of the main yard and the inner edge of the studding sail.");
            PanelHeightRatio = cfg.Bind("Visuals", "PanelHeightRatio", 0.9f,
                "Height of a four-sided studding sail as a fraction of the sail height, used when there is no studding boom below (the triangular one always reaches the clew).");
            HemSlack = cfg.Bind("Visuals", "HemSlack", 0.1f,
                "Foot roach of four-sided studding sails: extra cloth along the foot, as a fraction of its length (cut as an upward arc).");
            SideSlack = cfg.Bind("Visuals", "SideSlack", 0.01f,
                "Extra cloth along the side edges, as a fraction of their length.");
            ThrustMultiplier = cfg.Bind("Studding", "ThrustMultiplier", 1f,
                "Studding sail thrust: the force per square metre of their sail, times this (0 = looks only).");
            TurnMultiplier = cfg.Bind("Studding", "TurnMultiplier", 1f,
                "Turning effect of studding sails set on one side only: their force is applied this many times " +
                "farther out from the sail centre than they really are (0 = no turning, only thrust).");
            UseSailTexture = cfg.Bind("Visuals", "UseSailTexture", true,
                "Use the parent sail's material and texture (off = plain built-in canvas).");
            Belly = cfg.Bind("Visuals", "Belly", 0.005f,
                "Belly: extra cloth across the width, as a fraction of the width.");
            Fullness = cfg.Bind("Visuals", "LengthSlack", 0.14f,
                "Extra cloth in height: the canvas is longer than the distance between the yards by this fraction, so it sags and fills.");
            BoomOffsetZ = cfg.Bind("Visuals", "BoomOffsetZ", 0f,
                "Manual fore/aft offset of the studding sail booms, m.");
            HeadOverlap = cfg.Bind("Visuals", "HeadOverlap", 0.2f,
                "How much of the studding sail's width laps over its sail at the head (0..0.5). The sail's yard is " +
                "lengthened so the studding sail yard, hung from the yardarm by its middle, laps over this much.");
            HalyardLength = cfg.Bind("Visuals", "HalyardLength", 0.3f,
                "Length (m) of the halyard the studding sail yard hangs on below the yardarm: the pendulum length.");
            SwingStrength = cfg.Bind("Visuals", "SwingStrength", 5f,
                "How far the wind blows the hanging studding sail yard out, degrees per m/s of wind across the sail (0 = it hangs still).");
            FootLift = cfg.Bind("Visuals", "FootLift", 0.5f,
                "Height (m) of the foot of a four-sided studding sail above the boom below; the sheets span the gap.");
            HeadOffsetZ = cfg.Bind("Visuals", "HeadOffsetZ", 0f,
                "Fore/aft offset (m) of the studding sail head from the sail, so the lapping part does not cut " +
                "through it. Flip the sign if it ends up on the wrong side.");
            ShowRopes = cfg.Bind("Visuals", "ShowRopes", true,
                "Ropes: head lashings, sheets and halyards.");
        }

        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<bool> UseWinch;
        internal static ConfigEntry<string> VanillaBoats;
        internal static ConfigEntry<float> WinchOffsetX;
        internal static ConfigEntry<float> WinchOffsetY;
        internal static ConfigEntry<float> WinchOffsetZ;
        internal static ConfigEntry<KeyCode> WindowKey;
        internal static ConfigFile Cfg;
        internal static readonly Fixed<float> PanelWidthRatio = new Fixed<float>(0.2f);
        internal static readonly Fixed<bool> ClothPhysics = new Fixed<bool>(true);
        internal static readonly Fixed<float> MaxBillow = new Fixed<float>(3f);
        internal static readonly Fixed<float> ClothWind = new Fixed<float>(1f);
        internal static ConfigEntry<float> Fullness;
        internal static ConfigEntry<float> Belly;
        internal static ConfigEntry<bool> UseSailTexture;
        internal static ConfigEntry<float> ThrustMultiplier;
        internal static ConfigEntry<float> TurnMultiplier;
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
        internal static ConfigEntry<float> HeadOverlap;
        internal static ConfigEntry<float> HeadOffsetZ;
        internal static ConfigEntry<float> FootLift;
        internal static ConfigEntry<float> HalyardLength;
        internal static ConfigEntry<float> SwingStrength;
        internal static readonly Fixed<float> RollSize = new Fixed<float>(0.3f); // furled bundle size, see RollHeightFactor
        internal static readonly Fixed<float> TriangleSlack = new Fixed<float>(0.4f); // share of the cloth slack the triangle gets
        internal static readonly Fixed<float> WinchGear = new Fixed<float>(2f); // studding sail winch gear ratio, times the vanilla one: twice the turns
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

            // the key shows / hides the whole rig (booms and canvas); the winches keep their positions
            Plugin.Log.LogInfo("Studding: studding sails " + (st.Deployed ? "rigged" : "unrigged") + " on " + ship.name);
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

        /// <summary>
        /// How far the studding sails are set, 0..1 (0 = booms run in, ~0.3 = booms out, 1 = canvas fully lowered;
        /// the canvas also follows the parent sail). Shipyard: always 1 (preview). Winch mode: the mast's
        /// studding sail winch. Otherwise: the hotkey state.
        /// </summary>
        internal static float Deploy(Sail sail, int side)
        {
            if (sail == null) return 0f;
            if (GameState.currentShipyard != null) return 1f;
            if (UseWinch.Value) return StuddingWinch.ValueFor(sail, side);

            GameObject ship = ShipOf(sail);
            var st = ship != null ? ship.GetComponent<StuddingShip>() : null;
            return (st == null || st.Deployed) ? 1f : 0f;
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
            return Enabled.Value && IsSquare(sail) && !Plugin.IsExcludedSail(sail);
        }

        /// <summary>Raw marker state (independent of the config so no data is lost).</summary>
        /// <summary>A square sail (studding sails are offered for these, minus the excluded types).</summary>
        internal static bool IsSquare(Sail sail)
        {
            return sail != null && sail.squareSail;
        }

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
            if (m.On != on && VerboseLog.Value)
                Plugin.Log.LogInfo("[Studding] studding sails " + (on ? "ON" : "off") + " for " + Describe(sail) +
                                   "\n" + ShortStack());
            m.On = on;
        }

        /// <summary>"sail name (sailName) on mast X" for the log.</summary>
        internal static string Describe(Sail sail)
        {
            if (sail == null) return "null";
            Transform p = sail.transform.parent;
            return sail.name + " (" + sail.sailName + ") on " + (p != null ? p.name : "nothing") +
                   ", installed=" + sail.IsInstalled() + ", id " + sail.GetInstanceID();
        }

        /// <summary>A few frames of the call stack, without the logging frames.</summary>
        internal static string ShortStack()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 6; i++)
            {
                var mth = st.GetFrame(i).GetMethod();
                if (mth == null) continue;
                sb.Append("    at ").Append(mth.DeclaringType != null ? mth.DeclaringType.Name : "?").Append('.').Append(mth.Name).Append('\n');
            }
            return sb.ToString();
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
    /// Studding sail force, per side. Vanilla Sail.ApplyForce pushes the boat at the sail's wind centre with
    /// forward * unamplifiedForwardForce * power * 50 and right * unamplifiedSidewayForce * power * 50 * 1.5
    /// (power = sail area). unamplified*Force come from Sail.UpdateWindForceOnSail and are scaled by
    /// max(debugYardMult, currentUnroll), i.e. by how far the sail itself is set. Studding sails have their
    /// own set, so the same per-square-metre force is computed here without that factor (same wind, angle,
    /// height, shadow and bracing as the sail), times each side's set area, and applied at that side's
    /// studding sail centre: both sides set = thrust, one side = thrust and a turn.
    /// </summary>
    [HarmonyPatch(typeof(Sail), "ApplyForce")]
    internal static class StuddingForcePatch
    {
        private static readonly AccessTools.FieldRef<Sail, float> OutFwd = AccessTools.FieldRefAccess<Sail, float>("outFinalForwardForce");
        private static readonly Func<Sail, Vector3, float> WindAngle =
            AccessTools.MethodDelegate<Func<Sail, Vector3, float>>(AccessTools.Method(typeof(Sail), "GetWindAngle"));
        private static readonly Func<Sail, float, float> HeightMult =
            AccessTools.MethodDelegate<Func<Sail, float, float>>(AccessTools.Method(typeof(Sail), "GetWindHeightMult"));
        private static readonly Func<Sail, Vector3> ForceDir =
            AccessTools.MethodDelegate<Func<Sail, Vector3>>(AccessTools.Method(typeof(Sail), "GetSailForceDirection"));

        /// <summary>Sail.UpdateWindForceOnSail for a fully set sail: forward and sideway force per unit power.</summary>
        private static void UnitForce(Sail sl, out float fwd, out float side)
        {
            fwd = side = 0f;
            Rigidbody rb = sl.shipRigidbody;
            Vector3 wind = sl.apparentWind;
            Transform wc = sl.windcenter;
            if (rb == null || wc == null) return;
            if (sl.damage && sl.damage.sunk) return;

            float rel = WindAngle(sl, wind);
            float total = wind.magnitude * HeightMult(sl, wc.position.y) * sl.GetCurrentShadowMult();
            float applied = Mathf.Lerp(total * sl.upwindEfficiency, total, rel / 90f) * Mathf.InverseLerp(13f, 16f, rel);
            if (applied <= 0f) return;

            Vector3 force = ForceDir(sl) * applied;
            float a3 = Vector3.Angle(force, rb.transform.forward);
            float back = a3 > 90f ? -0.33f : 1f;
            float a5 = Vector3.Angle(force, rb.transform.right);
            float sgn = a5 > 90f ? -1f : 1f;
            if (a3 > 90f) a3 = 180f - a3;
            float fShare = Mathf.Lerp((90f - a3) / 90f, 1f, sl.upwindEfficiency);
            float sShare = a3 / 90f;
            fwd = back * applied * fShare;
            side = sgn * applied * sShare;
        }

        private static void Postfix(Sail __instance)
        {
            StuddingRig rig = StuddingRig.Of(__instance);
            if (rig == null || !rig.Visible) return;
            Rigidbody rb = __instance.shipRigidbody;
            if (rb == null || Debugger.instance == null) return;

            float a0 = rig.SideArea(0), a1 = rig.SideArea(1);
            if (a0 <= 0f && a1 <= 0f) return; // booms only (first 30% of the winch) or furled: no wind force

            float mult = Debugger.instance.debugSailAreaMult * Studding.ThrustMultiplier.Value;
            if (mult <= 0f) return;

            float uF, uS;
            UnitForce(__instance, out uF, out uS);
            if (uF == 0f && uS == 0f) return;

            Vector3 c = __instance.windcenter != null ? __instance.windcenter.position : __instance.transform.position;
            float turn = Mathf.Max(0f, Studding.TurnMultiplier.Value);
            Vector3 fwd = rb.transform.forward, right = rb.transform.right;

            float total = 0f;
            for (int s = 0; s < 2; s++)
            {
                float p = (s == 0 ? a0 : a1) * mult;
                if (p <= 0f) continue;
                total += p;
                Vector3 at = c + (rig.SideCenter(s) - c) * turn;
                rb.AddForceAtPosition(fwd * uF * p * 50f, at, ForceMode.Force);
                rb.AddForceAtPosition(right * uS * p * 50f * 1.5f, at, ForceMode.Force);
            }
            OutFwd(__instance) += uF * 50f * total;
        }
    }

    /// <summary>
    /// Boat mass: vanilla counts square sails as 30 x their power (BoatMass.GetSailMass); the set studding
    /// sail area counts the same way, as it did when it was added to the sail power.
    /// </summary>
    [HarmonyPatch(typeof(BoatMass), "GetSailMass")]
    internal static class StuddingMassPatch
    {
        private static void Postfix(Sail sail, ref float __result)
        {
            StuddingRig rig = StuddingRig.Of(sail);
            if (rig == null || !rig.Visible || Debugger.instance == null) return;
            float p = (rig.SideArea(0) + rig.SideArea(1)) * Debugger.instance.debugSailAreaMult * Studding.ThrustMultiplier.Value;
            if (p > 0f) __result += p * 30f;
        }
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// Writes the flag into health. Each sail's record is found by its content (mast index, prefab,
    /// install height, scale), exactly the values vanilla GetData copies from it, so the flag cannot land
    /// on another sail if some mod adds, removes or reorders records. The vanilla order is only a hint.
    /// </summary>
    [HarmonyPatch(typeof(SaveableBoatCustomization), "GetData")]
    internal static class StuddingSaveGetPatch
    {
        private static readonly FieldInfo RefsField = AccessTools.Field(typeof(SaveableBoatCustomization), "refs");

        private static void Postfix(SaveableBoatCustomization __instance, SaveBoatCustomizationData __result)
        {
            try
            {
                if (RefsField == null || __result == null || __result.sails == null) return;

                var refs = RefsField.GetValue(__instance) as BoatRefs;
                if (refs == null || refs.masts == null) return;

                var records = __result.sails;
                var used = new bool[records.Count];
                int i = 0;
                foreach (var mast in refs.masts)
                {
                    if (!mast || !mast.gameObject.activeSelf) continue;

                    foreach (var go in mast.sails)
                    {
                        int guess = i++;
                        Sail sl = go != null ? go.GetComponent<Sail>() : null;
                        if (!Studding.IsOn(sl)) continue;

                        int k = Find(records, used, guess, mast, sl);
                        if (k < 0)
                        {
                            Plugin.Log.LogWarning("[Studding] save: no record for " + Studding.Describe(sl) + ", its studding sails are not saved");
                            continue;
                        }
                        if (k != guess)
                            Plugin.Log.LogWarning("[Studding] save: record of " + Studding.Describe(sl) + " is #" + k +
                                                  ", not #" + guess + " (another mod changed the sail list)");

                        used[k] = true;
                        records[k].health = Studding.EncodeSave(Studding.GetWidth(sl), Studding.GetArm(sl), Studding.GetFootShift(sl));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Studding: save failed: " + e);
            }
        }

        private static bool Same(SaveSailData d, Mast mast, Sail sl)
        {
            return d != null &&
                   d.mastIndex == mast.orderIndex &&
                   d.prefabIndex == sl.prefabIndex &&
                   Mathf.Abs(d.installHeight - sl.GetCurrentInstallHeight()) < 0.01f &&
                   Mathf.Abs(d.scaleY - sl.GetScaleY()) < 0.001f &&
                   Mathf.Abs(d.scaleZ - sl.GetScaleZ()) < 0.001f;
        }

        private static int Find(List<SaveSailData> records, bool[] used, int guess, Mast mast, Sail sl)
        {
            if (guess < records.Count && !used[guess] && Same(records[guess], mast, sl)) return guess;
            for (int k = 0; k < records.Count; k++)
                if (!used[k] && Same(records[k], mast, sl)) return k;
            return -1;
        }
    }

    /// <summary>When a sail is loaded, remember the flag until AttachSailToMast runs.</summary>
    [HarmonyPatch(typeof(Mast), "LoadSail")]
    internal static class StuddingLoadSailPatch
    {
        private static void Prefix(SaveSailData sailData)
        {
            float w = 1f, arm = -1f, foot = 0f;
            Studding.PendingFromSave = sailData != null && Studding.DecodeSave(sailData.health, out w, out arm, out foot);
            Studding.PendingWidth = Studding.PendingFromSave ? w : 1f;
            Studding.PendingArm = Studding.PendingFromSave ? arm : -1f;
            Studding.PendingFoot = Studding.PendingFromSave ? foot : 0f;
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
            if (Studding.IsSquare(sail))
            {
                Studding.Set(sail, true);
                Studding.SetWidth(sail, Studding.PendingWidth);
                Studding.SetArm(sail, Studding.PendingArm);
                Studding.SetFootShift(sail, Studding.PendingFoot);
            }
        }
    }

    // ---------------------------------------------------------------- shipyard

    /// <summary>Shipyard button: a clone of the width button with our action.</summary>
    internal sealed class StuddingToggleButton : ShipyardButton
    {
        /// <summary>0 = add/remove studding sails, +1 = wider, -1 = narrower, +2 / -2 = longer / shorter yardarm,
        /// +3 / -3 = inner foot corner out / in along the boom below.</summary>
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
                    else if (Studding.IsOn(sail) && (Action == 1 || Action == -1))
                        Studding.SetWidth(sail, Studding.GetWidth(sail) + Action * Studding.WidthStep);
                    else if (Studding.IsOn(sail) && (Action == 2 || Action == -2))
                        // the first press starts from the automatic length
                        Studding.SetArm(sail, Studding.ArmOf(sail) + Mathf.Sign(Action) * Studding.ArmStep);
                    else if (Studding.IsOn(sail))
                        Studding.SetFootShift(sail, Studding.GetFootShift(sail) + Mathf.Sign(Action) * Studding.FootStep);
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

        // 0 on/off, 1 wider, 2 narrower, 3 longer yardarm, 4 shorter, 5 foot corner out, 6 foot corner in
        private static readonly GameObject[] buttons = new GameObject[7];
        private static readonly int[] actions = { 0, 1, -1, 2, -2, 3, -3 };

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
                    else if (i == 2) text = "Studding -";
                    else if (i == 3) text = "Yardarm +";
                    else if (i == 4) text = "Yardarm -";
                    else if (i == 5) text = "Foot out";
                    else text = "Foot in";
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

            // second column further left: "Yardarm +" on top, "Yardarm -" below it
            // columns further left: yardarm, then the foot corner
            float col = index >= 5 ? 3f : (index >= 3 ? 2f : 1f);
            Vector3 basePos = down.transform.localPosition + hStep * col;
            int rowsUp = index == 0 ? 0 : (index == 1 || index == 3 || index == 5 ? 2 : 1); // "+" on top, "-" in the middle
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
                    tm.text += "\nstudding sails: yes (" + Mathf.RoundToInt(Studding.GetWidth(sail) * 100f) + "%), yardarm +" +
                               Studding.ArmOf(sail).ToString("F1") + " m" + (Studding.GetArm(sail) < 0f ? " (auto)" : "") +
                               ", foot " + (Studding.GetFootShift(sail) >= 0f ? "+" : "") + Studding.GetFootShift(sail).ToString("F1") + " m";
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Studding: description failed: " + e);
            }
        }
    }
}