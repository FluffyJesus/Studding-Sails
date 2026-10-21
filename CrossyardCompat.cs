using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StuddingSails
{
    /// <summary>
    /// Compatibility with Topsail Crossyard (TopsailYards.FootYard).
    /// FootYard clones the sail's head yard for the yard under the sail. It picks it among the sail's
    /// renderers: the highest ones, preferring names with "yard"/"spar"/"boom", then the longest. Our
    /// studding booms lie on top of the yard and are called "Boom_...", so it picked one of them and the
    /// crossyard came out as a copy of a studding boom. Here, when its choice is one of our objects, the
    /// same choice is made again with our objects left out, so it clones the vanilla yard.
    /// Patched only if Topsail Crossyard is installed.
    /// </summary>
    [HarmonyPatch]
    internal static class CrossyardTopYardPatch
    {
        private static MethodBase target;
        private static FieldInfo YardField;

        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("TopsailYards.FootYard");
            target = t != null ? AccessTools.Method(t, "FindTopYard") : null;
            YardField = t != null ? AccessTools.Field(t, "yard") : null;
            if (target != null) Plugin.Log.LogInfo("[Studding] Topsail Crossyard found: its yard lookup skips studding booms");
            return target != null;
        }

        private static MethodBase TargetMethod() { return target; }

        private static void Postfix(Component __instance, ref Transform __result, ref Renderer topRend, ref float calculatedWidth)
        {
            if (__instance == null || topRend == null || !IsOurs(topRend.transform, __instance.transform)) return;

            Renderer best = PickYard(__instance);
            if (best == null) return;

            __result = best.transform;
            topRend = best;
            calculatedWidth = Dim(best);
        }

        /// <summary>Anything under our rig root or created by this mod (names start with "Studding").</summary>
        internal static bool IsOurs(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
                if (t.name == StuddingRig.RootName || t.name.StartsWith("Studding")) return true;
            return false;
        }

        private static float Dim(Renderer r)
        {
            Mesh m = StuddingGeom.MeshOf(r.transform);
            if (m == null) return 0f;
            Vector3 s = m.bounds.size;
            Vector3 l = r.transform.lossyScale;
            return Mathf.Max(s.x * Mathf.Abs(l.x), Mathf.Max(s.y * Mathf.Abs(l.y), s.z * Mathf.Abs(l.z)));
        }

        /// <summary>FootYard.FindTopYard's own rule, without our objects.</summary>
        private static Renderer PickYard(Component foot)
        {
            Sail sail = foot.GetComponent<Sail>();
            if (sail == null) return null;

            Renderer clothRend = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
            var reefAnim = sail.GetComponent<ReefEffectAnimUniversal>();
            Renderer furledRend = reefAnim != null ? reefAnim.furledSail : null;
            Vector3 up = sail.shipRigidbody != null ? sail.shipRigidbody.transform.up : Vector3.up;
            Transform me = foot.transform;
            Transform ownYard = YardField != null ? YardField.GetValue(foot) as Transform : null;

            Renderer best = null;
            float bestH = float.MinValue, bestDim = 0f;
            bool bestNamed = false;

            // first pass: the highest height; second: the best candidate within 0.25 m of it
            var cands = new System.Collections.Generic.List<Candidate>();
            foreach (var r in me.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r == clothRend || r == furledRend || !r.enabled) continue;
                if (IsOurs(r.transform, me)) continue;
                if (ownYard != null && r.transform.IsChildOf(ownYard)) continue; // FootYard's own clone
                Mesh mesh = StuddingGeom.MeshOf(r.transform);
                if (mesh == null) continue;

                string nm = (r.name + " " + (r.transform.parent != null ? r.transform.parent.name : "")).ToLowerInvariant();
                if (nm.Contains("col_checker") || nm.Contains("shadow") || nm.Contains("rope") ||
                    nm.Contains("cloth") || nm.Contains("canvas") || nm.Contains("furled"))
                    continue;

                float dim = Dim(r);
                if (dim <= 0.8f) continue;

                float h = Vector3.Dot(r.transform.TransformPoint(mesh.bounds.center) - me.position, up);
                cands.Add(new Candidate { r = r, dim = dim, h = h, named = nm.Contains("yard") || nm.Contains("spar") || nm.Contains("boom") });
                if (h > bestH) bestH = h;
            }

            foreach (var c in cands)
            {
                if (c.h < bestH - 0.25f) continue;
                if (best == null || (c.named && !bestNamed) || (c.named == bestNamed && c.dim > bestDim))
                {
                    best = c.r; bestDim = c.dim; bestNamed = c.named;
                }
            }
            return best;
        }

        private struct Candidate
        {
            public Renderer r;
            public float dim, h;
            public bool named;
        }
    }

    /// <summary>
    /// Topsail Crossyard also copies a "bracket" for its yard: a small renderer of the sail near the head
    /// yard's height (0.15-0.85 m). Our yardarm extensions, blocks and studding sail yards fit that, so it copied
    /// a yardarm extension under the sail. Ours are never brackets.
    /// </summary>
    [HarmonyPatch]
    internal static class CrossyardBracketPatch
    {
        private static MethodBase target;

        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("TopsailYards.FootYard");
            target = t != null ? AccessTools.Method(t, "IsLikelyBracket") : null;
            return target != null;
        }

        private static MethodBase TargetMethod() { return target; }

        private static void Postfix(Component __instance, Renderer r, ref bool __result)
        {
            if (__result && r != null && __instance != null && CrossyardTopYardPatch.IsOurs(r.transform, __instance.transform))
                __result = false;
        }
    }
}