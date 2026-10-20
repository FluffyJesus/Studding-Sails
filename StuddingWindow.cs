using System;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace StuddingSails
{
    /// <summary>
    /// In-game studding sail tuning window (IMGUI), opened with WindowKey.
    /// Values go to the config; written to disk by "Save" or on close.
    /// </summary>
    internal static class StuddingWindow
    {
        private const int WindowId = 0x57D5;

        internal static bool Open;
        private static Rect rect = new Rect(40f, 80f, 380f, 10f);
        private static bool dirty;

        internal static void Update()
        {
            if (Studding.WindowKey != null && Input.GetKeyDown(Studding.WindowKey.Value))
            {
                if (Open) Close();
                else Open = true;
            }

            if (Open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private static void Close()
        {
            Open = false;
            if (dirty) Save();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static void Save()
        {
            if (Studding.Cfg != null) Studding.Cfg.Save();
            dirty = false;
        }

        internal static void OnGUI()
        {
            if (!Open) return;
            rect = GUILayout.Window(WindowId, rect, Draw, "Studding Sails");
        }

        private static void Draw(int id)
        {
            GUILayout.Label("Size");
            Slider("Boom overlap on the yard (fraction of yard)", Studding.BoomOverlapRatio, 0.02f, 0.3f);
            Slider("Gap from the sail, m", Studding.PanelGap, 0f, 1f);
            Slider("Height without a boom below (fraction of sail)", Studding.PanelHeightRatio, 0.3f, 1f);
            Slider("Boom fore/aft offset, m", Studding.BoomOffsetZ, -0.5f, 0.5f);

            GUILayout.Space(6f);
            GUILayout.Label("Cloth");
            Slider("Extra cloth in height (between yards)", Studding.Fullness, 0f, 0.3f);
            Slider("Belly (extra cloth across)", Studding.Belly, 0f, 0.3f);
            Slider("Foot roach (extra cloth on the foot)", Studding.HemSlack, 0f, 0.4f);
            Slider("Extra cloth on the side edges", Studding.SideSlack, 0f, 0.15f);

            bool ropes = GUILayout.Toggle(Studding.ShowRopes.Value, " Ropes");
            if (ropes != Studding.ShowRopes.Value) { Studding.ShowRopes.Value = ropes; Changed(); }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save")) Save();
            if (GUILayout.Button("Close")) Close();
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private static void Slider(string label, ConfigEntry<float> e, float min, float max)
        {
            GUILayout.Label(label + ": " + e.Value.ToString("F3"));
            float v = GUILayout.HorizontalSlider(e.Value, min, max);
            if (Mathf.Abs(v - e.Value) > 0.0005f)
            {
                e.Value = (float)Math.Round(v, 3);
                Changed();
            }
        }

        private static void Changed()
        {
            dirty = true;
            StuddingRig.LayoutVersion++;
        }

        /// <summary>Cloth parameters of the vanilla sails on the current boat (debug).</summary>
        internal static void DumpCloth()
        {
            GameObject ship = Studding.GetPlayerShip();
            Sail[] sails = ship != null
                ? ship.GetComponentsInChildren<Sail>(true)
                : UnityEngine.Object.FindObjectsOfType<Sail>();

            var sb = new StringBuilder();
            sb.AppendLine("[Studding] ===== sail cloth dump (" + (ship != null ? ship.name : "all sails") + ") =====");

            foreach (var sail in sails)
            {
                if (sail == null) continue;
                Cloth c = sail.cloth;
                sb.AppendLine("--- " + sail.name + " (" + sail.sailName + "), unroll=" + sail.currentUnroll.ToString("F2") +
                              ", apparentWind=" + sail.apparentWind + " |" + sail.apparentWind.magnitude.ToString("F1") + "|");
                if (c == null) { sb.AppendLine("  no cloth"); continue; }

                sb.AppendLine("  stretching=" + c.stretchingStiffness + " bending=" + c.bendingStiffness +
                              " damping=" + c.damping + " friction=" + c.friction + " gravity=" + c.useGravity);
                sb.AppendLine("  worldVel=" + c.worldVelocityScale + " worldAcc=" + c.worldAccelerationScale +
                              " sleep=" + c.sleepThreshold + " virtualParticles=" + c.useVirtualParticles +
                              " tethers=" + c.useTethers);
                sb.AppendLine("  external=" + c.externalAcceleration + " random=" + c.randomAcceleration);

                try
                {
                    var co = c.coefficients;
                    if (co != null && co.Length > 0)
                    {
                        float mn = float.MaxValue, mx = 0f, sum = 0f;
                        int pinned = 0;
                        foreach (var k in co)
                        {
                            if (k.maxDistance <= 0.0001f) pinned++;
                            mn = Mathf.Min(mn, k.maxDistance);
                            if (k.maxDistance < 1e6f) { mx = Mathf.Max(mx, k.maxDistance); sum += k.maxDistance; }
                        }
                        sb.AppendLine("  verts=" + co.Length + " pinned=" + pinned + " maxDistance min/avg/max=" +
                                      mn.ToString("F3") + "/" + (sum / co.Length).ToString("F3") + "/" + mx.ToString("F3"));
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine("  coefficients: " + e.Message);
                }

                sb.AppendLine("  cloth object: " + c.gameObject.name + ", scale " + c.transform.lossyScale +
                              ", components: " + string.Join(", ", c.GetComponents<Component>().Select(x => x.GetType().Name).ToArray()));
                sb.AppendLine("  sail components: " + string.Join(", ", sail.GetComponents<Component>()
                    .Select(x => x.GetType().FullName + " [" + x.GetType().Assembly.GetName().Name + "]").ToArray()));
            }

            StuddingRig.DumpAll(sb);
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
