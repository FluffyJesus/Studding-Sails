using System.Collections.Generic;
using UnityEngine;

namespace StuddingSails
{
    /// <summary>
    /// Rope controller for the studding sail winch. The vanilla winch only changes
    /// RopeController.currentLength (0..1); we read it as "how far the studding sails are set".
    /// </summary>
    internal sealed class StuddingRopeController : RopeController
    {
        public override bool CanPull() { return true; }
    }

    /// <summary>
    /// Two extra winches per mast that carries studding sails: one for the port studding sails, one for the
    /// starboard ones (set one side only to help the boat turn). Clones of a vanilla winch, but not part of
    /// the mast winch arrays, so mastOrder and the vanilla slots are untouched and a mast full of sails still
    /// gets its own. On modded boats every winch of a mast column (lower mast, topmasts) is pegged to the
    /// lower mast at the same height, port ones on its port side, starboard ones on its starboard side.
    /// </summary>
    internal sealed class StuddingWinch : MonoBehaviour
    {
        // two winches per mast: [0] port, [1] starboard
        private static readonly Dictionary<Mast, StuddingWinch[]> winches = new Dictionary<Mast, StuddingWinch[]>();

        private const float StandOff = 0.13f; // winch centre from the mast surface

        private Mast mast;          // the mast whose studding sails this winch works
        private Mast baseMast;      // the lowest mast of its column (the pole the peg goes into)
        private List<Mast> column;  // every mast standing on that line, lower mast first
        private int slot;           // place of this mast in the column: 0 = lower mast
        private int side;           // 0 = port studding sails, 1 = starboard
        private GPButtonRopeWinch winch;
        private StuddingRopeController controller;
        private Transform peg;
        private GameObject rope;
        private float nextCheck;
        private bool needed;

        internal float Value { get { return controller != null ? controller.currentLength : 0f; } }

        internal static StuddingWinch Of(Mast mast, int side)
        {
            if (mast == null) return null;
            side = side == 0 ? 0 : 1;
            StuddingWinch[] pair;
            if (!winches.TryGetValue(mast, out pair) || pair == null)
            {
                pair = new StuddingWinch[2];
                winches[mast] = pair;
            }
            if (pair[side] != null) return pair[side];

            var w = mast.gameObject.AddComponent<StuddingWinch>();
            w.mast = mast;
            w.side = side;
            pair[side] = w;
            return w;
        }

        /// <summary>How far the studding sails of this sail on one side (0 port, 1 starboard) are set by the winch (0..1).</summary>
        internal static float ValueFor(Sail sail, int side)
        {
            Mast mast = sail != null && sail.transform.parent != null ? sail.transform.parent.GetComponent<Mast>() : null;
            if (mast == null) return 0f;
            StuddingWinch w = Of(mast, side);
            if (w == null) return 0f;
            w.Ensure();
            return w.Value;
        }

        /// <summary>Every studding sail winch placed on these masts.</summary>
        private static IEnumerable<StuddingWinch> OursOn(Mast m)
        {
            StuddingWinch[] pair;
            if (m == null || !winches.TryGetValue(m, out pair) || pair == null) yield break;
            foreach (var w in pair) if (w != null && w.winch != null) yield return w;
        }

        private void Ensure()
        {
            if (winch != null || mast == null) return;

            Transform ship = mast.shipRigidbody != null ? mast.shipRigidbody.transform : null;
            baseMast = ship != null ? BaseMast(mast, ship) : mast;
            column = ship != null ? ColumnMasts(baseMast, ship) : new List<Mast> { mast };
            slot = Mathf.Max(0, column.IndexOf(mast));

            // template: a winch of the lower mast first (where every peg of the column goes), then the others
            GPButtonRopeWinch template = null;
            foreach (var m in column) { template = PickTemplate(m); if (template != null) break; }
            if (template == null) template = PickTemplate(mast);
            if (template == null) return;

            // where: vanilla boats - above the highest winch of this mast column;
            // modded boats - on the mast itself, on a peg, at the first free spot around it
            Transform parent = null;
            Vector3 pos = Vector3.zero;
            Quaternion rot = Quaternion.identity;
            bool onMast = !IsVanillaBoat(mast) && MastMountPose(template, out parent, out pos, out rot);
            if (!onMast) WinchPose(mast, template, out parent, out pos, out rot);

            // controller first: the winch talks to it
            var cgo = new GameObject("StuddingRopeController");
            cgo.transform.SetParent(parent, false);
            controller = cgo.AddComponent<StuddingRopeController>();
            controller.currentLength = 0f; // booms run in after loading; crank to set

            // clone with the right parent so its Awake finds the PurchasableBoat up the hierarchy
            GameObject clone = Instantiate(template.gameObject, parent);
            clone.name = "StuddingSailWinch";
            clone.transform.localPosition = pos;
            clone.transform.localRotation = rot;
            clone.transform.localScale = template.transform.localScale;

            winch = clone.GetComponent<GPButtonRopeWinch>();
            winch.rotHandle = null; // the VR handle reference was copied from the template; do not share it
            // studding sails are heavier work than a plain rope: a higher gear ratio means more turns of the
            // winch for the same travel (the vanilla winch moves the rope by rotation / 360 / gearRatio), so
            // setting them takes longer while the winch stays as responsive as any other
            winch.gearRatio = template.gearRatio * Mathf.Max(1f, Studding.WinchGear.Value);
            winch.AttachToController(controller);
            winch.ShowWinch(true);
            needed = true;

            if (onMast) AddPeg(clone.transform, template);
            AddRope(clone.transform, ship);
        }

        // ------------------------------------------------------------ vanilla or modded boat

        /// <summary>
        /// Boats listed in the VanillaBoats config (comma-separated parts of the boat object name) use the
        /// stacking placement; any other boat (from mods) gets the winch mounted on the mast.
        /// An empty list treats every boat as vanilla.
        /// </summary>
        private static bool IsVanillaBoat(Mast mast)
        {
            string list = Studding.VanillaBoats.Value;
            if (string.IsNullOrEmpty(list) || list.Trim().Length == 0) return true;

            // the boat name can sit on any ancestor (PurchasableBoat, the rigidbody, the root), so check them all
            var parts = new List<string>();
            foreach (var part in list.Split(','))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p.Length > 0) parts.Add(p);
            }

            for (Transform t = mast.transform; t != null; t = t.parent)
            {
                string name = t.name.ToLowerInvariant();
                foreach (var p in parts) if (name.Contains(p)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ the mast line, as the shipyard sees it

        // The shipyard slides sails along the mast's local Z: a sail's install height is
        // mast.InverseTransformPoint(sail).z + mast.mastHeight. So the mast transform sits at the
        // masthead (z = 0) and the foot is at z = -mastHeight (sails with extended height go down a
        // further extraBottomHeight). That line is the mast, whatever the model or its rake.

        private static Vector3 Head(Mast m) { return m.transform.position; }

        private static Vector3 Foot(Mast m)
        {
            return m.transform.TransformPoint(new Vector3(0f, 0f, -(m.mastHeight + Mathf.Max(0f, m.extraBottomHeight))));
        }

        /// <summary>Unit direction foot -> head (falls back to local Z turned upward).</summary>
        private static Vector3 Axis(Mast m)
        {
            Vector3 d = Head(m) - Foot(m);
            if (d.sqrMagnitude > 0.01f) return d.normalized;
            Vector3 f = m.transform.forward;
            Transform ship = m.shipRigidbody != null ? m.shipRigidbody.transform : null;
            if (ship != null && Vector3.Dot(f, ship.up) < 0f) f = -f;
            return f;
        }

        private static float DistToLine(Vector3 p, Vector3 origin, Vector3 dir)
        {
            Vector3 d = p - origin;
            return (d - dir * Vector3.Dot(d, dir)).magnitude;
        }

        /// <summary>
        /// The lowest mast whose line this mast stands on: a topmast's foot lies on the line of its
        /// lower mast (within 1 m), lower foot wins.
        /// </summary>
        private static Mast BaseMast(Mast mast, Transform ship)
        {
            Vector3 up = ship.up;
            Mast best = mast;
            Vector3 myFoot = Foot(mast);
            float bestY = Vector3.Dot(myFoot, up);
            foreach (Mast m in ship.GetComponentsInChildren<Mast>(true))
            {
                if (m == null || m == mast) continue;
                float y = Vector3.Dot(Foot(m), up);
                if (y >= bestY) continue;
                if (DistToLine(myFoot, Foot(m), Axis(m)) > 1f) continue;
                bestY = y;
                best = m;
            }
            return best;
        }

        /// <summary>The base mast plus every mast whose foot stands on its line above its foot.</summary>
        private static List<Mast> ColumnMasts(Mast baseMast, Transform ship)
        {
            var list = new List<Mast> { baseMast };
            Vector3 up = ship.up;
            Vector3 foot = Foot(baseMast), dir = Axis(baseMast);
            float footY = Vector3.Dot(foot, up);
            foreach (Mast m in ship.GetComponentsInChildren<Mast>(true))
            {
                if (m == null || m == baseMast) continue;
                Vector3 f = Foot(m);
                if (Vector3.Dot(f, up) <= footY) continue;
                if (DistToLine(f, foot, dir) <= 1f) list.Add(m);
            }
            list.Sort((a, b) => a == b ? 0 : a == baseMast ? -1 : b == baseMast ? 1 : Vector3.Dot(Foot(a), up).CompareTo(Vector3.Dot(Foot(b), up)));
            return list;
        }

        // ------------------------------------------------------------ mast mount (modded boats)

        /// <summary>A readable mesh of the boat, vertices in world space.</summary>
        private sealed class MeshData
        {
            public Renderer r;
            public Bounds b;
            public Vector3[] w;
            public int[] t;
        }

        /// <summary>Tall (2 m and more along the ship's up) readable meshes: masts, not rails or bands.</summary>
        private static List<MeshData> TallMeshes(Transform ship, Vector3 up, out int unreadable)
        {
            unreadable = 0;
            Vector3 upAbs = new Vector3(Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z));
            var list = new List<MeshData>();
            foreach (var r in ship.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (r == null || !r.enabled) continue;
                if (r.GetComponentInParent<Sail>() != null || r.GetComponentInParent<GPButtonRopeWinch>() != null) continue;
                if (Vector3.Dot(r.bounds.size, upAbs) < 2f) continue;
                Mesh m = StuddingGeom.MeshOf(r.transform);
                if (m == null) continue;
                if (!m.isReadable || m.vertexCount > 200000) { unreadable++; continue; }

                Vector3[] v = m.vertices;
                Matrix4x4 l2w = r.transform.localToWorldMatrix;
                var w = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++) w[i] = l2w.MultiplyPoint3x4(v[i]);
                list.Add(new MeshData { r = r, b = r.bounds, w = w, t = m.triangles });
            }
            return list;
        }

        /// <summary>
        /// Winch on the mast, no clearance checks: on the mast line a little above the highest winch
        /// standing at this mast, on its aft side. The pole itself is found at that height from the meshes:
        /// of the tall meshes cut by the plane across the line there (within 1.5 m of it), the one closest to
        /// the line; its cross-section gives the pole centre and radius (the shipyard line runs where the sails hang,
        /// which need not be the middle of the pole). The winch spindle (local Z) and the peg point straight
        /// out of the mast, perpendicular to its line. Runs once per mast column.
        /// </summary>
        private bool MastMountPose(GPButtonRopeWinch template, out Transform parent, out Vector3 pos, out Quaternion rot)
        {
            parent = template.transform.parent;
            pos = Vector3.zero;
            rot = Quaternion.identity;

            Transform ship = mast.shipRigidbody != null ? mast.shipRigidbody.transform : null;
            if (ship == null || parent == null) return false;
            Vector3 up = ship.up;

            Vector3 foot = Foot(baseMast);
            Vector3 dir = Axis(baseMast);
            float dirUp = Vector3.Dot(dir, up);
            if (dirUp < 0.3f)
            {
                Plugin.Log.LogWarning("[Studding] mast " + baseMast.name + " is not upright (" + dirUp.ToString("F2") + "), stacking the winch");
                return false;
            }
            float footY = Vector3.Dot(foot, up);

            // height: a little above the highest vanilla winch standing at this mast (the same for every
            // studding sail winch of the column, they stand side by side)
            float maxH = float.MinValue;
            int near = 0;
            foreach (var t in AxisWinches(ship, foot, dir, false)) { maxH = Mathf.Max(maxH, Vector3.Dot(t.position, up)); near++; }
            if (maxH == float.MinValue) maxH = Vector3.Dot(template.transform.position, up);
            float h = maxH + 0.3f;
            Vector3 axisPt = foot + dir * ((h - footY) / dirUp);

            int unreadable;
            List<MeshData> meshes = TallMeshes(ship, up, out unreadable);
            Vector3 centre;
            float radius;
            string src = PoleSection(axisPt, dir, meshes, out centre, out radius);
            // the peg goes in down to the mast axis: no gap even if the mast is thinner or off-centre there
            pegBack = StandOff + Mathf.Clamp(radius, 0.08f, 0.4f);

            // on the aft side of the mast, port winches to port and starboard ones to starboard, about 0.35 m
            // apart: lower mast pair closest to aft (+-half a step), then the topmast pair, and so on
            Vector3 aft = Vector3.ProjectOnPlane(-ship.forward, dir).normalized;
            float step = Mathf.Clamp(0.35f / (radius + StandOff) * Mathf.Rad2Deg, 20f, 60f);
            float ang = step * (0.5f + slot);
            Vector3 outDir = Quaternion.AngleAxis(ang, dir) * aft;
            bool toStarboard = Vector3.Dot(outDir, ship.right) > 0f;
            if (toStarboard != (side == 1))
            {
                ang = -ang;
                outDir = Quaternion.AngleAxis(ang, dir) * aft;
            }
            Vector3 c = centre + outDir * (radius + StandOff);

            pos = parent.InverseTransformPoint(c);
            rot = Quaternion.Inverse(parent.rotation) * Quaternion.LookRotation(outDir, dir);

            Vector3 shipFoot = ship.InverseTransformPoint(foot), shipHead = ship.InverseTransformPoint(Head(baseMast));
            Plugin.Log.LogInfo("[Studding] " + (side == 0 ? "port" : "starboard") + " winch for mast " + mast.name + " on " + baseMast.name + " (slot " + slot + ", angle " +
                               ang.ToString("F0") + "): mastHeight=" + baseMast.mastHeight.ToString("F2") +
                               ", extraBottom=" + baseMast.extraBottomHeight.ToString("F2") +
                               ", foot " + shipFoot.ToString("F2") + ", head " + shipHead.ToString("F2") +
                               ", winches at mast " + near + ", winch height " + ship.InverseTransformPoint(c).y.ToString("F2") +
                               ", pole offset from line " + (centre - axisPt).magnitude.ToString("F2") +
                               ", radius " + radius.ToString("F3") + " (" + src + "), unreadable tall meshes " + unreadable);
            return true;
        }

        /// <summary>
        /// Cross-section of a mesh by the plane through p perpendicular to dir: the points where triangle
        /// edges cross the plane, within maxR of p (relative to p). Works for low-poly poles too, whose
        /// vertices may all be metres away from the plane.
        /// </summary>
        private static List<Vector3> Slice(MeshData md, Vector3 p, Vector3 dir, float maxR)
        {
            var pts = new List<Vector3>();
            Vector3[] w = md.w;
            int[] t = md.t;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = w[t[i + e]], b = w[t[i + (e + 1) % 3]];
                    float sa = Vector3.Dot(a - p, dir), sb = Vector3.Dot(b - p, dir);
                    if ((sa > 0f) == (sb > 0f)) continue;
                    Vector3 q = a + (b - a) * (sa / (sa - sb)) - p;
                    q -= dir * Vector3.Dot(q, dir);
                    if (q.magnitude <= maxR) pts.Add(q);
                }
            }
            return pts;
        }

        /// <summary>Pole centre and radius at a point of the mast line (see MastMountPose).</summary>
        private string PoleSection(Vector3 axisPt, Vector3 dir, List<MeshData> meshes, out Vector3 centre, out float radius)
        {
            centre = axisPt;
            radius = 0.2f;

            MeshData best = null;
            float bestMean = float.MaxValue;
            Vector3 bestCen = Vector3.zero;
            float bestRad = 0.2f;
            int bestN = 0;
            foreach (var md in meshes)
            {
                Bounds b = md.b;
                b.Expand(3f);
                if (!b.Contains(axisPt)) continue;

                List<Vector3> pts = Slice(md, axisPt, dir, 1.5f);
                if (pts.Count < 3) continue;

                Vector3 sum = Vector3.zero;
                float dsum = 0f;
                foreach (var q in pts) { sum += q; dsum += q.magnitude; }
                float mean = dsum / pts.Count;
                if (mean >= bestMean) continue;

                Vector3 cen = sum / pts.Count;
                float rs = 0f;
                foreach (var q in pts) rs += (q - cen).magnitude;

                bestMean = mean; best = md; bestCen = cen; bestRad = rs / pts.Count; bestN = pts.Count;
            }

            if (best != null)
            {
                centre = axisPt + bestCen;
                radius = Mathf.Clamp(bestRad, 0.04f, 0.6f);
                return "mesh '" + best.r.name + "', " + bestN + " section points";
            }

            // no readable pole: the column's own long meshes, cross size in their local space
            float bestR = float.MaxValue;
            foreach (var m in column)
            {
                foreach (var r in m.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (r == null || r.GetComponentInParent<Sail>() != null || r.GetComponentInParent<GPButtonRopeWinch>() != null) continue;
                    Mesh mesh = StuddingGeom.MeshOf(r.transform);
                    if (mesh == null) continue;
                    Vector3 sz = Vector3.Scale(mesh.bounds.size, r.transform.lossyScale);
                    float[] d = { Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z) };
                    System.Array.Sort(d);
                    if (d[2] < 3f) continue;
                    float rad = (d[0] + d[1]) * 0.25f;
                    if (rad > 0.03f && rad < 0.6f && rad < bestR) bestR = rad;
                }
            }
            if (bestR != float.MaxValue) { radius = bestR; return "bounds, on the line"; }
            return "default, on the line";
        }

        /// <summary>Winches (vanilla slots of every mast, and ours) standing within 1.2 m of the mast line.</summary>
        private static List<Transform> AxisWinches(Transform ship, Vector3 foot, Vector3 dir, bool ours)
        {
            var list = new List<Transform>();
            foreach (Mast m in ship.GetComponentsInChildren<Mast>(true))
            {
                if (m == null) continue;
                GPButtonRopeWinch[][] sets = { m.reefWinch, m.midAngleWinch, m.leftAngleWinch, m.rightAngleWinch };
                foreach (var set in sets)
                {
                    if (set == null) continue;
                    foreach (var w in set)
                        if (w != null && DistToLine(w.transform.position, foot, dir) <= 1.2f) list.Add(w.transform);
                }
                if (ours)
                    foreach (var sw in OursOn(m))
                        if (DistToLine(sw.winch.transform.position, foot, dir) <= 1.2f) list.Add(sw.winch.transform);
            }
            return list;
        }

        private float pegBack = StandOff + 0.2f; // from the winch centre into the mast: down to its axis (set by MastMountPose)
        private const float PegFront = 0.015f;          // from the winch centre to the cap (snug against the drum)
        private static readonly Dictionary<int, Mesh> pegMeshes = new Dictionary<int, Mesh>();

        /// <summary>
        /// Belaying-pin style peg along +Z, in metres: a round shaft from z = 0 (inside the mast) to the
        /// winch and past it, ending in a mushroom cap (flat underside, rim, low dome) that would keep the
        /// winch from sliding off. Low-poly, faceted like the game's own pins.
        /// </summary>
        private static Mesh PegMesh(float back)
        {
            int key = Mathf.RoundToInt(back * 100f); // one mesh per centimetre of length
            Mesh pegMesh;
            if (pegMeshes.TryGetValue(key, out pegMesh) && pegMesh != null) return pegMesh;

            float L = key / 100f + PegFront;
            // profile (radius, z), tip last; every pair is its own strip, so edges between them stay sharp
            Vector2[] prof =
            {
                new Vector2(0.032f, 0f), new Vector2(0.028f, L),          // shaft, slightly tapered
                new Vector2(0.028f, L), new Vector2(0.068f, L),           // flat underside of the cap
                new Vector2(0.068f, L), new Vector2(0.072f, L + 0.02f),   // rim
                new Vector2(0.072f, L + 0.02f), new Vector2(0.058f, L + 0.042f), // dome
                new Vector2(0.058f, L + 0.042f), new Vector2(0.032f, L + 0.058f),
                new Vector2(0.032f, L + 0.058f), new Vector2(0f, L + 0.064f),
            };

            const int seg = 12;
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            for (int s = 0; s + 1 < prof.Length; s += 2)
            {
                int start = v.Count;
                for (int k = 0; k < 2; k++)
                {
                    Vector2 pr = prof[s + k];
                    for (int j = 0; j <= seg; j++)
                    {
                        float ang = j / (float)seg * Mathf.PI * 2f;
                        v.Add(new Vector3(pr.x * Mathf.Cos(ang), pr.x * Mathf.Sin(ang), pr.y));
                        // shaft: around x along (grain runs along the peg); cap faces: radius x around
                        // (the grain shows as rings, like end grain)
                        bool cap = s >= 2;
                        uv.Add(cap ? new Vector2(pr.x * 9f, j / (float)seg) : new Vector2(j / (float)seg, pr.y * 1.5f));
                    }
                }
                int stride = seg + 1;
                for (int j = 0; j < seg; j++)
                {
                    int a0 = start + j, a1 = a0 + 1, b0 = a0 + stride, b1 = b0 + 1;
                    // (ring k, next angle, next ring) faces outward
                    t.Add(a0); t.Add(a1); t.Add(b0);
                    t.Add(a1); t.Add(b1); t.Add(b0);
                }
            }

            pegMesh = new Mesh { name = "StuddingWinchPeg" };
            pegMesh.SetVertices(v);
            pegMesh.SetUVs(0, uv);
            pegMesh.SetTriangles(t, 0);
            pegMesh.RecalculateNormals();
            pegMesh.RecalculateBounds();
            pegMeshes[key] = pegMesh;
            return pegMesh;
        }

        /// <summary>Wooden mushroom peg from inside the mast surface through the winch, along the winch spindle.</summary>
        private void AddPeg(Transform winchT, GPButtonRopeWinch template)
        {
            var go = new GameObject("StuddingWinchPeg");
            go.transform.SetParent(winchT.parent, false);
            peg = go.transform;

            // mesh +Z along the spindle, starting a little inside the mast; metres regardless of parent scale
            Vector3 fwd = winchT.forward;
            float back = Mathf.RoundToInt(pegBack * 100f) / 100f;
            go.transform.position = winchT.position - fwd * back;
            go.transform.rotation = Quaternion.LookRotation(fwd, winchT.up);
            Vector3 ps = winchT.parent != null ? winchT.parent.lossyScale : Vector3.one;
            go.transform.localScale = new Vector3(1f / Mathf.Max(1e-4f, Mathf.Abs(ps.x)),
                                                  1f / Mathf.Max(1e-4f, Mathf.Abs(ps.y)),
                                                  1f / Mathf.Max(1e-4f, Mathf.Abs(ps.z)));

            go.AddComponent<MeshFilter>().sharedMesh = PegMesh(pegBack);
            var mr = go.AddComponent<MeshRenderer>();

            // the game's spar wood: the yard of a square sail on this mast column (the studding booms use
            // it too); otherwise the mast, otherwise the winch
            Renderer wood = YardWood();
            if (wood == null)
            {
                Mast pole = baseMast != null ? baseMast : mast;
                foreach (var r in pole.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (r.GetComponentInParent<Sail>() != null || r.GetComponentInParent<GPButtonRopeWinch>() != null) continue;
                    if (r.GetComponentInParent<Mast>() != pole) continue;
                    wood = r;
                    break;
                }
            }
            if (wood == null) wood = template.GetComponent<Renderer>();
            if (wood != null && wood.sharedMaterial != null) mr.sharedMaterial = PegMaterial(wood.sharedMaterial);
        }

        private static readonly Dictionary<Material, Material> pegMats = new Dictionary<Material, Material>();
        private static Texture2D pegTex;

        /// <summary>
        /// The game's wood shader with our own small wood texture: the spar textures are laid out for the
        /// yard's UVs, on a peg they came out as random streaks. Light brown with fine lengthwise grain,
        /// in the flat style of the game's belaying pins.
        /// </summary>
        private static Material PegMaterial(Material src)
        {
            Material m;
            if (pegMats.TryGetValue(src, out m) && m != null) return m;

            m = new Material(src) { name = "StuddingPegWood" };
            m.mainTexture = PegTexture();
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_Color")) m.color = Color.white;
            if (m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", null); m.DisableKeyword("_NORMALMAP"); }
            if (m.HasProperty("_DetailAlbedoMap")) m.SetTexture("_DetailAlbedoMap", null);
            pegMats[src] = m;
            return m;
        }

        private static Texture2D PegTexture()
        {
            if (pegTex != null) return pegTex;

            const int size = 64;
            var baseCol = new Color(0.43f, 0.31f, 0.22f);
            var rnd = new System.Random(7);
            // per-column grain: a few soft lines and some noise, varying across u only
            var grain = new float[size];
            float phase = (float)rnd.NextDouble() * 6.28f;
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size * Mathf.PI * 2f;
                grain[x] = 0.06f * Mathf.Sin(u * 5f + phase) + 0.035f * Mathf.Sin(u * 13f + phase * 2f) +
                           0.03f * ((float)rnd.NextDouble() - 0.5f);
            }

            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float along = 0.015f * Mathf.Sin(y / (float)size * Mathf.PI * 2f * 3f);
                for (int x = 0; x < size; x++)
                {
                    float k = 1f + grain[x] + along + 0.012f * ((float)rnd.NextDouble() - 0.5f);
                    px[y * size + x] = new Color(baseCol.r * k, baseCol.g * k, baseCol.b * k, 1f);
                }
            }

            pegTex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "StuddingPegWood" };
            pegTex.wrapMode = TextureWrapMode.Repeat;
            pegTex.filterMode = FilterMode.Bilinear;
            pegTex.SetPixels(px);
            pegTex.Apply(true, true);
            return pegTex;
        }

        /// <summary>Yard renderer of a square sail on this mast (or its column).</summary>
        private Renderer YardWood()
        {
            var masts = new List<Mast> { mast };
            if (column != null) masts.AddRange(column);
            foreach (var m in masts)
            {
                if (m == null || m.sails == null) continue;
                foreach (var go in m.sails)
                {
                    Sail s = go != null ? go.GetComponent<Sail>() : null;
                    if (!Studding.IsSquare(s)) continue;
                    YardInfo info = StuddingGeom.Get(s);
                    if (info != null && info.Valid && info.YardRenderer != null) return info.YardRenderer;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ fall from the winch to the masthead

        /// <summary>
        /// The block the studding booms of this mast are hoisted to: the highest of the mast's attachment
        /// points (mastReefAtt, extensions, midRopeAtt) standing on its axis (within 1.5 m in plan).
        /// Same choice as the halyards of the topmost studding sails (StuddingRig.MastTopBlock).
        /// </summary>
        private static Transform MastTopBlock(Mast mast, Transform ship)
        {
            Vector3 axis = ship.InverseTransformPoint(mast.transform.position);
            Transform best = null;
            float bestY = float.MinValue;
            Transform[][] sets = { mast.mastReefAtt, mast.mastReefAttExtension, mast.midRopeAtt };
            foreach (var set in sets)
            {
                if (set == null) continue;
                foreach (var t in set)
                {
                    if (t == null) continue;
                    Vector3 p = ship.InverseTransformPoint(t.position);
                    float dx = p.x - axis.x, dz = p.z - axis.z;
                    if (dx * dx + dz * dz > 1.5f * 1.5f) continue;
                    if (p.y > bestY) { bestY = p.y; best = t; }
                }
            }
            return best;
        }

        private Transform fallFrom, fallTo;
        private Transform fallHolder, fallStart;
        private const float DrumRadius = 0.068f; // where the rope leaves the drum (? the cap underside radius)

        /// <summary>Remembers the ends of the fall (winch drum -> masthead block); it is built when shown.</summary>
        private void AddRope(Transform winchT, Transform ship)
        {
            if (ship == null) return;
            fallFrom = winchT;
            fallTo = MastTopBlock(mast, ship);
            ShowFall(true);
        }

        /// <summary>
        /// The fall is a vanilla RopeEffect, the same kind of rope as the game's halyards and our rig ropes, so
        /// it looks the same. RopeEffect spawns its rope as a separate object that does not hide with ours,
        /// so the rope is destroyed when hidden and built again when shown.
        /// </summary>
        private void ShowFall(bool show)
        {
            if (!show || fallFrom == null || fallTo == null)
            {
                if (rope != null) Destroy(rope);
                if (fallHolder != null) Destroy(fallHolder.gameObject);
                if (fallStart != null) Destroy(fallStart.gameObject);
                rope = null;
                fallHolder = null;
                fallStart = null;
                return;
            }
            if (rope != null) return;

            fallHolder = new GameObject("StuddingWinchFallEnd").transform;
            fallHolder.SetParent(fallTo, false);

            // the rope leaves the drum at its rim, on the side facing the block (in the drum's plane). The
            // start is not a child of the winch: the winch object turns when cranked, the rope should not
            fallStart = new GameObject("StuddingWinchFallStart").transform;
            fallStart.SetParent(fallFrom.parent, false);
            Vector3 toTop = Vector3.ProjectOnPlane(fallTo.position - fallFrom.position, fallFrom.forward);
            Vector3 side = toTop.sqrMagnitude > 1e-6f ? toTop.normalized : fallFrom.up;
            fallStart.position = fallFrom.position + side * DrumRadius;

            rope = new GameObject("StuddingWinchFall");
            rope.layer = fallFrom.gameObject.layer;
            rope.transform.SetParent(fallStart, false);
            rope.SetActive(false);

            Transform ship = mast.shipRigidbody != null ? mast.shipRigidbody.transform : null;
            RopeEffect src = ship != null ? ship.GetComponentInChildren<RopeEffect>(true) : null;

            var lr = rope.AddComponent<LineRenderer>();
            LineRenderer srcLr = src != null ? src.GetComponent<LineRenderer>() : null;
            if (srcLr != null)
            {
                lr.sharedMaterial = srcLr.sharedMaterial;
                lr.shadowCastingMode = srcLr.shadowCastingMode;
                lr.receiveShadows = srcLr.receiveShadows;
            }

            var re = rope.AddComponent<RopeEffect>();
            re.attachment = fallHolder;
            re.sheet = false;
            re.ropeWidth = src != null ? src.ropeWidth : 0.038f;
            re.totalRopeLength = 0.95f;
            re.currentRopeLength = 0f;
            re.ReceiveSailArea(20f);

            rope.SetActive(true);
        }

        private static GPButtonRopeWinch PickTemplate(Mast mast)
        {
            GPButtonRopeWinch[][] sets = { mast.reefWinch, mast.midAngleWinch, mast.leftAngleWinch, mast.rightAngleWinch };
            foreach (var set in sets)
            {
                if (set == null) continue;
                foreach (var w in set) if (w != null) return w;
            }
            return null;
        }

        // ------------------------------------------------------------ stacking (vanilla boats)

        /// <summary>
        /// Stack the studding sail winch above the highest winch of the mast column. A topmast is a separate
        /// Mast, but its winches stand at the foot of the lower mast, so every mast standing in the same spot
        /// (within 1.5 m in plan) counts, including all their vanilla slots (used or not, since a slot can
        /// get a sail later). The new winch copies the orientation of that highest winch, so it faces the
        /// mast the same way.
        /// </summary>
        private static void WinchPose(Mast mast, GPButtonRopeWinch template, out Transform parent, out Vector3 pos, out Quaternion rot)
        {
            Transform ship = mast.shipRigidbody != null ? mast.shipRigidbody.transform : null;
            Vector3 up = ship != null ? ship.up : Vector3.up;

            Transform top = template.transform;
            float topH = float.MinValue;

            foreach (Transform t in ColumnWinches(mast, ship))
            {
                float h = Vector3.Dot(t.position, up);
                if (h > topH) { topH = h; top = t; }
            }

            // one winch height higher (world bounds of the winch along "up", with a little gap)
            float spacing = 0.35f;
            var r = top.GetComponent<Renderer>();
            if (r != null)
            {
                Vector3 sz = r.bounds.size;
                float along = Mathf.Abs(Vector3.Dot(sz, new Vector3(Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z))));
                if (along > 0.05f && along < 1.5f) spacing = along * 1.15f;
            }

            parent = top.parent;
            Vector3 world = top.position + up * spacing;
            pos = parent != null ? parent.InverseTransformPoint(world) : world;
            rot = top.localRotation;
            pos += new Vector3(Studding.WinchOffsetX.Value, Studding.WinchOffsetY.Value, Studding.WinchOffsetZ.Value);
        }

        /// <summary>All winches (vanilla slots and ours) of the masts standing where this mast stands.</summary>
        private static List<Transform> ColumnWinches(Mast mast, Transform ship)
        {
            var list = new List<Transform>();
            Vector3 me = ship != null ? ship.InverseTransformPoint(mast.transform.position) : mast.transform.position;

            IEnumerable<Mast> masts = ship != null ? (IEnumerable<Mast>)ship.GetComponentsInChildren<Mast>(true) : new Mast[] { mast };
            foreach (Mast m in masts)
            {
                if (m == null) continue;
                Vector3 p = ship != null ? ship.InverseTransformPoint(m.transform.position) : m.transform.position;
                float dx = p.x - me.x, dz = p.z - me.z;
                if (dx * dx + dz * dz > 1.5f * 1.5f) continue;

                GPButtonRopeWinch[][] sets = { m.reefWinch, m.midAngleWinch, m.leftAngleWinch, m.rightAngleWinch };
                foreach (var set in sets)
                {
                    if (set == null) continue;
                    foreach (var w in set)
                    {
                        if (w == null) continue;
                        // only winches on the mast itself: brace winches often stand at the rails
                        Vector3 wp = ship != null ? ship.InverseTransformPoint(w.transform.position) : w.transform.position;
                        float wx = wp.x - me.x, wz = wp.z - me.z;
                        if (wx * wx + wz * wz > 1.2f * 1.2f) continue;
                        list.Add(w.transform);
                    }
                }

                foreach (var sw in OursOn(m)) list.Add(sw.winch.transform);
            }
            return list;
        }

        private void Update()
        {
            if (winch == null || Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;

            // show the winch only while some sail of this mast has studding sails
            bool any = false;
            if (mast != null && mast.sails != null)
            {
                foreach (var go in mast.sails)
                {
                    Sail s = go != null ? go.GetComponent<Sail>() : null;
                    if (s != null && s.IsInstalled() && Studding.IsOn(s)) { any = true; break; }
                }
            }

            if (any != needed)
            {
                needed = any;
                winch.ShowWinch(any);
                if (peg != null) peg.gameObject.SetActive(any);
                ShowFall(any);
            }
        }

        private void OnDestroy()
        {
            StuddingWinch[] pair;
            if (mast != null && winches.TryGetValue(mast, out pair) && pair != null && pair[side] == this) pair[side] = null;
            if (winch != null) Destroy(winch.gameObject);
            if (peg != null) Destroy(peg.gameObject);
            ShowFall(false);
            if (controller != null) Destroy(controller.gameObject);
        }
    }
}