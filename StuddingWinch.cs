using System;
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
        private GameObject coil;            // the rope coil shown in place of the winch (UseRopeCoil), or null
        private float standOff = StandOff;  // winch centre from the mast surface (less for a flat coil)
        private float spacing = 0.35f;      // between neighbouring winches on the mast
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

        private float nextTry;
        private bool reportedFail;

        private void Ensure()
        {
            if (winch != null || mast == null || Time.time < nextTry) return;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BuildWinch();
                Slow.Report("winch", sw, mast.name);
            }
            catch (Exception e)
            {
                // never leave a half-built winch behind; try again a bit later
                if (!reportedFail) Plugin.Log.LogError("[Studding] studding sail winch for mast " + mast.name + " failed: " + e);
                reportedFail = true;
                if (winch != null) Destroy(winch.gameObject);
                if (controller != null) Destroy(controller.gameObject);
                if (peg != null) Destroy(peg.gameObject);
                if (coil != null) Destroy(coil);
                ShowFall(false);
                winch = null;
                controller = null;
                peg = null;
                coil = null;
                nextTry = Time.time + 5f;
            }
        }

        private void BuildWinch()
        {

            Transform ship = mast.shipRigidbody != null ? mast.shipRigidbody.transform : null;
            baseMast = ship != null ? BaseMast(mast, ship) : mast;
            column = ship != null ? ColumnMasts(baseMast, ship) : new List<Mast> { mast };
            slot = Mathf.Max(0, column.IndexOf(mast));

            // template: a winch of the lower mast first (where every peg of the column goes), then the others
            GPButtonRopeWinch template = null;
            foreach (var m in column) { template = PickTemplate(m); if (template != null) break; }
            if (template == null) template = PickTemplate(mast);
            if (template == null)
            {
                if (!reportedFail) Plugin.Log.LogWarning("[Studding] mast " + mast.name + ": no vanilla winch to copy for the studding sail winch");
                reportedFail = true;
                nextTry = Time.time + 5f;
                return;
            }

            // where: on the mast itself, on a peg, just above the vanilla winches (MastMountPose);
            // if the mast line looks wrong - stacked above the vanilla winches (WinchPose)
            // what it looks like on a modded boat's peg: a coil of rope (the mooring rope's model), flat against
            // the mast, so it sits closer to it and needs more room beside its neighbours
            CoilModel coilModel = Studding.UseRopeCoil.Value && !IsVanillaBoat(mast) ? FindCoilModel() : null;
            float coilScale = 0f;
            if (coilModel != null)
            {
                float d = Mathf.Clamp(Studding.RopeCoilSize.Value, 0.1f, 1f);
                coilScale = d / coilModel.Diameter;
                float depth = coilModel.Depth * coilScale;
                standOff = depth * 0.5f + 0.03f;
                spacing = Mathf.Max(0.35f, d + 0.05f);
                pegFront = depth * 0.5f + 0.01f;
                fallRadius = d * 0.45f;
            }

            Transform parent = null;
            Vector3 pos = Vector3.zero;
            Quaternion rot = Quaternion.identity;
            bool onMast = MastMountPose(template, out parent, out pos, out rot);
            if (!onMast)
            {
                WinchPose(template, out parent, out pos, out rot);
                coilModel = null; // stacked with the mast's winches: the same as its neighbours
                fallRadius = DrumRadius;
            }

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

            // the template may be an unused, switched-off winch slot: the copy must be on, or it never wakes
            // up (no boat found) and stays invisible
            if (!clone.activeSelf) clone.SetActive(true);
            ResetHighlight(clone);
            // The vanilla winch adds its own highlight outline in Start; the template's one was copied along
            // with it and stayed behind as a second, stale outline that the outline renderer later picked up
            // in red (after a teleport). Remove the copies: our winch adds a fresh one in its own Start.
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "Outline") DestroyImmediate(c);
            if (coilModel != null && !DressAsCoil(clone, coilModel, coilScale))
            {
                coilModel = null;
                fallRadius = DrumRadius;
            }

            winch = clone.GetComponent<GPButtonRopeWinch>();
            winch.enableRedOutline = false;
            winch.forceDisableRedOutline = true; // never red: nothing about our winch is ever "wrong" to look at
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
            Plugin.Log.LogInfo("[Studding] " + (side == 0 ? "port" : "starboard") + " studding sail winch for mast " + mast.name +
                               (onMast ? " pinned to the mast" : " stacked with the mast's winches") +
                               ", copied from '" + template.name + "' (active " + template.gameObject.activeInHierarchy + ")" +
                               (coil != null ? ", shown as a rope coil from '" + coilModel.Source + "'" : ""));
        }

        // ------------------------------------------------------------ the rope coil

        /// <summary>The mooring rope coil's model: mesh, materials and its size in metres along the mesh axes.</summary>
        private sealed class CoilModel
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 Scale;   // world scale of the source (mesh units -> metres)
            public Vector3 Size;    // mesh bounds, metres
            public Vector3 Centre;  // mesh bounds centre, metres along the mesh axes
            public int Axis;        // the coil's own axis: its thinnest side
            public string Source;

            public float Depth { get { return Size[Axis]; } }
            public float Diameter { get { return Mathf.Max(Size[(Axis + 1) % 3], Size[(Axis + 2) % 3]); } }
        }

        private static CoilModel coilCache;
        private static float nextCoilSearch;
        private static Mesh emptyMesh;
        private cakeslice.Outline coilOutline, winchOutline;

        /// <summary>
        /// The model of a mooring rope coil (PickupableBoatMooringRope: every boat carries some), a vanilla boat's
        /// one first. Only its mesh and materials are used: the coil itself is a saved, pickable item tied to its boat.
        /// </summary>
        private static CoilModel FindCoilModel()
        {
            if (coilCache != null && coilCache.Mesh != null) return coilCache;
            if (Time.time < nextCoilSearch) return null;
            nextCoilSearch = Time.time + 5f;

            string list = Studding.VanillaBoats.Value;
            bool haveList = !string.IsNullOrEmpty(list) && list.Trim().Length > 0;
            MeshFilter best = null;
            bool bestVanilla = false;
            float bestSize = 0f;
            foreach (var rope in FindObjectsOfType<PickupableBoatMooringRope>())
            {
                if (rope == null) continue;
                bool vanilla = haveList && InList(rope.transform, list);
                foreach (var mf in rope.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || mr.sharedMaterial == null) continue;
                    float size = Vector3.Scale(mf.sharedMesh.bounds.size, Abs(mf.transform.lossyScale)).magnitude;
                    bool better = best == null || (vanilla && !bestVanilla) || (vanilla == bestVanilla && size > bestSize);
                    if (!better) continue;
                    best = mf;
                    bestVanilla = vanilla;
                    bestSize = size;
                }
            }
            if (best == null)
            {
                Plugin.Log.LogWarning("[Studding] no mooring rope coil found to copy, the studding sail winch keeps the winch look");
                return null;
            }

            Vector3 s = Abs(best.transform.lossyScale);
            Bounds b = best.sharedMesh.bounds;
            var m = new CoilModel
            {
                Mesh = best.sharedMesh,
                Materials = best.GetComponent<MeshRenderer>().sharedMaterials,
                Scale = s,
                Size = Vector3.Scale(b.size, s),
                Centre = Vector3.Scale(b.center, s),
                Source = best.name + "' on '" + best.transform.root.name,
            };
            m.Axis = m.Size.x <= m.Size.y && m.Size.x <= m.Size.z ? 0 : (m.Size.y <= m.Size.z ? 1 : 2);
            if (m.Diameter < 0.01f) return null;
            Plugin.Log.LogInfo("[Studding] rope coil model '" + m.Source + "': " + m.Size.ToString("F2") + " m, axis " + "XYZ"[m.Axis]);
            coilCache = m;
            return m;
        }

        /// <summary>
        /// Show the winch as a rope coil. The winch itself stays (it turns when worked, which is how it measures
        /// the rope) but draws nothing and loses its own parts; the coil hangs on it and turns with it, centred on the
        /// spindle with its axis along it, and lights up together with the winch (LateUpdate). The winch's
        /// collider is fitted to the coil.
        /// </summary>
        private bool DressAsCoil(GameObject clone, CoilModel model, float scale)
        {
            var mf = clone.GetComponent<MeshFilter>();
            if (mf == null) return false;
            if (emptyMesh == null) emptyMesh = new Mesh { name = "StuddingNoWinch" };
            mf.sharedMesh = emptyMesh;
            Transform wt = clone.transform;
            for (int i = wt.childCount - 1; i >= 0; i--) DestroyImmediate(wt.GetChild(i).gameObject);

            var go = new GameObject("StuddingRopeCoil");
            go.layer = clone.layer;
            Transform t = go.transform;
            t.SetParent(wt.parent, false);
            Vector3 axis = model.Axis == 0 ? Vector3.right : model.Axis == 1 ? Vector3.up : Vector3.forward;
            t.rotation = wt.rotation * Quaternion.FromToRotation(axis, Vector3.forward);
            Vector3 p = wt.parent != null ? Abs(wt.parent.lossyScale) : Vector3.one;
            float pu = Mathf.Max(1e-4f, (p.x + p.y + p.z) / 3f);
            t.localScale = model.Scale * (scale / pu);
            t.position = wt.position - t.rotation * (model.Centre * scale);
            go.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = model.Materials;
            coilOutline = go.AddComponent<cakeslice.Outline>();
            coilOutline.enabled = false;
            // on the winch: the coil turns with it when worked
            t.SetParent(wt, true);
            coil = go;

            FitCollider(clone, model.Diameter * scale, model.Depth * scale);
            return true;
        }

        /// <summary>Winch colliders around the coil: a disc of diameter d and thickness depth on the spindle.</summary>
        private static void FitCollider(GameObject go, float d, float depth)
        {
            Vector3 s = Abs(go.transform.lossyScale);
            var size = new Vector3(d / Mathf.Max(1e-4f, s.x), d / Mathf.Max(1e-4f, s.y), depth / Mathf.Max(1e-4f, s.z));
            float r = 0.5f * Mathf.Max(size.x, size.y);
            foreach (var c in go.GetComponents<Collider>())
            {
                var box = c as BoxCollider;
                if (box != null) { box.center = Vector3.zero; box.size = size; continue; }
                var sphere = c as SphereCollider;
                if (sphere != null) { sphere.center = Vector3.zero; sphere.radius = r; continue; }
                var capsule = c as CapsuleCollider;
                if (capsule != null) { capsule.center = Vector3.zero; capsule.direction = 2; capsule.radius = r; capsule.height = 2f * r; continue; }
                var mc = c as MeshCollider;
                if (mc != null) { mc.sharedMesh = BoxMesh(size); mc.convex = true; }
            }
        }

        private static Mesh BoxMesh(Vector3 size)
        {
            Vector3 h = size * 0.5f;
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
            int[] t =
            {
                0, 2, 1, 1, 2, 3,  4, 5, 6, 5, 7, 6, // -z, +z
                0, 1, 4, 1, 5, 4,  2, 6, 3, 3, 6, 7, // -y, +y
                0, 4, 2, 2, 4, 6,  1, 3, 5, 3, 7, 5, // -x, +x
            };
            var mesh = new Mesh { name = "StuddingCoilCollider", vertices = v, triangles = t };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 Abs(Vector3 v) { return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z)); }

        /// <summary>The coil lights up exactly when the winch it stands for does.</summary>
        private void LateUpdate()
        {
            if (coilOutline == null || winch == null) return;
            if (winchOutline == null)
            {
                winchOutline = winch.GetComponent<cakeslice.Outline>(); // added in the winch's Start
                if (winchOutline == null) return;
            }
            if (coilOutline.color != winchOutline.color) coilOutline.color = winchOutline.color;
            bool lit = winchOutline.enabled && coil.activeInHierarchy;
            if (coilOutline.enabled != lit) coilOutline.enabled = lit;
        }

        // ------------------------------------------------------------ vanilla or modded boat

        /// <summary>
        /// Boats listed in the VanillaBoats config (comma-separated parts of the boat object name): there the
        /// mast line must pass through vanilla winches, or the winch is stacked the old way instead.
        /// An empty list treats every boat as vanilla.
        /// </summary>
        private static bool IsVanillaBoat(Mast mast)
        {
            string list = Studding.VanillaBoats.Value;
            if (string.IsNullOrEmpty(list) || list.Trim().Length == 0) return true;
            return InList(mast.transform, list);
        }

        /// <summary>Is t on a boat named in the comma-separated list (any part of an ancestor's name)?</summary>
        private static bool InList(Transform from, string list)
        {
            // the boat name can sit on any ancestor (PurchasableBoat, the rigidbody, the root), so check them all
            var parts = new List<string>();
            foreach (var part in list.Split(','))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p.Length > 0) parts.Add(p);
            }

            for (Transform t = from; t != null; t = t.parent)
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

        /// <summary>
        /// Two masts form one column (lower mast and topmast) when the upper one stands on the lower one: its
        /// foot is well up the lower mast (at least 40% of its height) and on its line (within 1 m). Two masts
        /// side by side - both feet down at the deck - are separate, however close they stand.
        /// </summary>
        internal static bool SameColumn(Mast a, Mast b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            // one stands under the other in the hierarchy (vanilla topmasts are children of their lower mast)
            if (IsAncestor(a, b) || IsAncestor(b, a)) return true;
            Transform ship = a.shipRigidbody != null ? a.shipRigidbody.transform : (b.shipRigidbody != null ? b.shipRigidbody.transform : null);
            Vector3 up = ship != null ? ship.up : Vector3.up;
            // only upright masts (within ~25 degrees) stack: the line of a bowsprit or a slanted mast (ShipyardExpansion
            // adds some at 30-40 degrees), run on far enough, can pass any topmast's foot
            if (Vector3.Dot(Axis(a), up) < 0.9f || Vector3.Dot(Axis(b), up) < 0.9f) return false;
            Vector3 fa = Foot(a), fb = Foot(b);
            Mast lo = Vector3.Dot(fa, up) <= Vector3.Dot(fb, up) ? a : b;
            Mast hi = lo == a ? b : a;
            Vector3 flo = lo == a ? fa : fb, fhi = lo == a ? fb : fa;
            float rise = Vector3.Dot(fhi - flo, up);
            if (rise < 0.4f * Mathf.Max(1f, lo.mastHeight)) return false;
            return DistToLine(fhi, flo, Axis(lo)) <= 1f;
        }

        /// <summary>The nearest Mast above this one in the transform hierarchy.</summary>
        private static Mast ParentMast(Mast m)
        {
            Transform t = m != null ? m.transform.parent : null;
            return t != null ? t.GetComponentInParent<Mast>() : null;
        }

        private static bool IsAncestor(Mast anc, Mast m)
        {
            for (Mast p = ParentMast(m); p != null; p = ParentMast(p)) if (p == anc) return true;
            return false;
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
            // the game's own hierarchy first: a topmast sits under its lower mast
            Mast root = mast;
            for (Mast p = ParentMast(root); p != null && p != mast; p = ParentMast(p)) root = p;
            if (root != mast) return root;

            Vector3 up = ship.up;
            Mast best = mast;
            Vector3 myFoot = Foot(mast);
            float bestY = Vector3.Dot(myFoot, up);
            foreach (Mast m in ship.GetComponentsInChildren<Mast>(true))
            {
                if (m == null || m == mast) continue;
                float y = Vector3.Dot(Foot(m), up);
                if (y >= bestY) continue;
                if (!SameColumn(mast, m)) continue;
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
                if (SameColumn(baseMast, m)) list.Add(m);
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
            // studding sail winch of the column, they stand side by side). Every vanilla slot counts, shown or
            // not (a hidden one may appear when a sail is added), except winches far up in the rig (some masts
            // keep a slot for another mast spot there): only those within 2 m of the lowest one at this mast.
            var hs = new List<float>();
            foreach (var t in AxisWinches(ship, foot, dir, false)) hs.Add(Vector3.Dot(t.position, up));
            int near = hs.Count;
            float lowH = float.MaxValue, maxH = float.MinValue;
            foreach (float y in hs) lowH = Mathf.Min(lowH, y);
            foreach (float y in hs) if (y <= lowH + 2f) maxH = Mathf.Max(maxH, y);
            if (IsVanillaBoat(mast) && near == 0)
            {
                // the mast line misses the vanilla winches: something is off with this mast, use the old way
                Plugin.Log.LogWarning("[Studding] mast " + baseMast.name + ": no vanilla winches on its line, stacking the winch");
                return false;
            }
            if (maxH == float.MinValue) maxH = Vector3.Dot(template.transform.position, up);
            float h = maxH + 0.3f;
            Vector3 axisPt = foot + dir * ((h - footY) / dirUp);

            int unreadable;
            List<MeshData> meshes = TallMeshes(ship, up, out unreadable);
            Vector3 centre;
            float radius;
            string src = PoleSection(axisPt, dir, meshes, out centre, out radius);
            // the peg goes in down to the mast axis: no gap even if the mast is thinner or off-centre there
            pegBack = standOff + Mathf.Clamp(radius, 0.08f, 0.4f);

            // on the aft side of the mast, port winches to port and starboard ones to starboard, about 0.35 m
            // apart: lower mast pair closest to aft (+-half a step), then the topmast pair, and so on
            Vector3 aft = Vector3.ProjectOnPlane(-ship.forward, dir).normalized;
            float step = Mathf.Clamp(spacing / (radius + standOff) * Mathf.Rad2Deg, 20f, 60f);
            float ang = step * (0.5f + slot);
            Vector3 outDir = Quaternion.AngleAxis(ang, dir) * aft;
            bool toStarboard = Vector3.Dot(outDir, ship.right) > 0f;
            if (toStarboard != (side == 1))
            {
                ang = -ang;
                outDir = Quaternion.AngleAxis(ang, dir) * aft;
            }
            Vector3 c = centre + outDir * (radius + standOff);

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
        private float pegFront = 0.015f;          // from the winch centre to the cap (snug against the drum or the coil)
        private static readonly Dictionary<int, Mesh> pegMeshes = new Dictionary<int, Mesh>();

        /// <summary>
        /// Belaying-pin style peg along +Z, in metres: a round shaft from z = 0 (inside the mast) to the
        /// winch and past it, ending in a mushroom cap (flat underside, rim, low dome) that would keep the
        /// winch from sliding off. Low-poly, faceted like the game's own pins.
        /// </summary>
        private static Mesh PegMesh(float back, float front)
        {
            // one mesh per centimetre of length and millimetre of the part in front of the winch
            int key = Mathf.RoundToInt(back * 100f) * 1000 + Mathf.RoundToInt(front * 1000f);
            Mesh pegMesh;
            if (pegMeshes.TryGetValue(key, out pegMesh) && pegMesh != null) return pegMesh;

            float L = Mathf.RoundToInt(back * 100f) / 100f + Mathf.RoundToInt(front * 1000f) / 1000f;
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

            go.AddComponent<MeshFilter>().sharedMesh = PegMesh(pegBack, pegFront);
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
        private float fallRadius = DrumRadius;   // where it leaves the winch: the drum, or the rim of the coil

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
            fallStart.position = fallFrom.position + side * fallRadius;

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

        /// <summary>
        /// The copy takes over the template's highlight exactly as it was at that moment: if the template was
        /// lit (looked at, or red while the boat was being moved), the copy stays lit for good, because it never
        /// gets the "look away" that would switch it off. Switch every outline/highlight component of the copy off;
        /// the game switches it on again when the winch is looked at.
        /// </summary>
        private static void ResetHighlight(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name.ToLowerInvariant();
                if (n.Contains("outline") || n.Contains("highlight")) c.enabled = false;
            }
        }

        /// <summary>A vanilla winch of the mast to copy: preferably one in use (switched on and shown).</summary>
        private static GPButtonRopeWinch PickTemplate(Mast mast)
        {
            GPButtonRopeWinch[][] sets = { mast.reefWinch, mast.midAngleWinch, mast.leftAngleWinch, mast.rightAngleWinch };
            GPButtonRopeWinch any = null;
            foreach (var set in sets)
            {
                if (set == null) continue;
                foreach (var w in set)
                {
                    if (w == null) continue;
                    if (any == null) any = w;
                    var r = w.GetComponent<Renderer>();
                    if (w.gameObject.activeInHierarchy && r != null && r.enabled) return w;
                }
            }
            return any;
        }

        // ------------------------------------------------------------ stacking (vanilla boats)

        /// <summary>
        /// Stack the studding sail winch above the highest winch of the mast column. A topmast is a separate
        /// Mast, but its winches stand at the foot of the lower mast, so every mast standing in the same spot
        /// (within 1.5 m in plan) counts, including all their vanilla slots (used or not, since a slot can
        /// get a sail later). The new winch copies the orientation of that highest winch, so it faces the
        /// mast the same way.
        /// </summary>
        private void WinchPose(GPButtonRopeWinch template, out Transform parent, out Vector3 pos, out Quaternion rot)
        {
            Mast col = baseMast != null ? baseMast : mast;
            Transform ship = col.shipRigidbody != null ? col.shipRigidbody.transform : null;
            Vector3 up = ship != null ? ship.up : Vector3.up;

            // The shown vanilla winches of the column (ours do not count: they share one row). Hidden slots are
            // skipped, and so is anything well above the lowest winch: some masts carry winches up in the rig
            // (another mast spot, a mod), and stacking on those hung ours in the air.
            var shown = new List<Transform>();
            var all = ColumnWinches(col, ship, false);
            foreach (Transform t in all)
            {
                var tr = t.GetComponent<Renderer>();
                if (t.gameObject.activeInHierarchy && tr != null && tr.enabled) shown.Add(t);
            }
            if (shown.Count == 0) shown.AddRange(all);
            float low = float.MaxValue;
            foreach (Transform t in shown) low = Mathf.Min(low, Vector3.Dot(t.position, up));
            var kept = new List<Transform>();
            var dropped = new System.Text.StringBuilder();
            foreach (Transform t in shown)
            {
                float h = Vector3.Dot(t.position, up) - low;
                if (h <= 2f) kept.Add(t);
                else dropped.Append(" '" + t.name + "' (" + (t.parent != null ? t.parent.name : "-") + ", +" + h.ToString("0.0") + " m)");
            }
            Transform top = template.transform;
            float topH = float.MinValue;
            foreach (Transform t in kept)
            {
                float h = Vector3.Dot(t.position, up);
                if (h > topH) { topH = h; top = t; }
            }
            Plugin.Log.LogInfo("[Studding] winch row for mast " + mast.name + " (column of " + col.name + ", slot " + slot + "): " +
                all.Count + " vanilla winches near the mast, " + shown.Count + " shown, " + kept.Count + " used" +
                (dropped.Length > 0 ? "; ignored high up:" + dropped : ""));

            // one winch height higher (world bounds of the winch along "up", with a little gap)
            float spacing = 0.35f;
            var r = top.GetComponent<Renderer>();
            if (r != null)
            {
                Vector3 sz = r.bounds.size;
                float along = Mathf.Abs(Vector3.Dot(sz, new Vector3(Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z))));
                if (along > 0.05f && along < 1.5f) spacing = along * 1.15f;
            }

            // Spread around the mast instead of a tall column. No guessing where the mast's axis is: the shown
            // vanilla winches already sit on the mast's surface all around it, so ours take their spots (and
            // their facing), lifted into the row above them. Port winches take the most-port spots, starboard
            // ones the most-starboard; lower mast pair outermost, then the topmast pairs; when spots run out,
            // a new row starts.
            var spots = new List<Transform>();
            foreach (Transform t in kept)
            {
                bool dup = false;
                foreach (var o in spots)
                {
                    Vector3 d = t.position - o.position;
                    d -= up * Vector3.Dot(d, up);
                    if (d.sqrMagnitude < 0.15f * 0.15f) { dup = true; break; }
                }
                if (!dup) spots.Add(t);
            }
            if (spots.Count == 0) spots.Add(top);
            Vector3 right = ship != null ? ship.right : Vector3.right;
            spots.Sort((x, y) => Vector3.Dot(x.position, right).CompareTo(Vector3.Dot(y.position, right)));

            int n = spots.Count;
            int perSide = Mathf.Max(1, n / 2);
            int idx = slot % perSide;
            int row = slot / perSide;
            if (n == 1) row = slot * 2 + side; // one spot only: port and starboard take turns upwards
            Transform spot = side == 0 ? spots[idx] : spots[n - 1 - idx];

            Vector3 world = spot.position + up * (Vector3.Dot(top.position - spot.position, up) + spacing * (1 + row));
            parent = spot.parent;
            pos = parent != null ? parent.InverseTransformPoint(world) : world;
            rot = spot.localRotation;
            pos += new Vector3(Studding.WinchOffsetX.Value, Studding.WinchOffsetY.Value, Studding.WinchOffsetZ.Value);
        }

        /// <summary>All winches (vanilla slots and ours) of the masts standing where this mast stands.</summary>
        private static List<Transform> ColumnWinches(Mast mast, Transform ship, bool ours = true)
        {
            var list = new List<Transform>();
            Vector3 me = ship != null ? ship.InverseTransformPoint(mast.transform.position) : mast.transform.position;

            IEnumerable<Mast> masts = ship != null ? (IEnumerable<Mast>)ship.GetComponentsInChildren<Mast>(true) : new Mast[] { mast };
            foreach (Mast m in masts)
            {
                if (m == null) continue;
                // only this mast and the masts stacked on it (topmasts): a separate mast standing right next to
                // it (ShipyardExpansion puts some close together) has its own winches and its own stack
                if (!SameColumn(mast, m)) continue;

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

                if (ours) foreach (var sw in OursOn(m)) list.Add(sw.winch.transform);
            }
            return list;
        }

        private Collider winchCol;
        private bool colOn = true;

        private void Update()
        {
            if (winch == null) return;

            // The winch needs its collider to be cranked, but the shipyard checks every collider on the boat
            // against the sails ("a sail is obstructed", red outline on our winches): no collider at the shipyard
            if (winchCol == null) winchCol = winch.GetComponent<Collider>();
            bool wantCol = needed && GameState.currentShipyard == null;
            if (winchCol != null && (winchCol.enabled != wantCol || colOn != wantCol))
            {
                winchCol.enabled = wantCol;
                colOn = wantCol;
            }

            if (Time.time < nextCheck) return;
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
                colOn = !colOn; // ShowWinch switched the collider too: let the check above set it again
                if (peg != null) peg.gameObject.SetActive(any);
                if (coil != null) coil.SetActive(any);
                ShowFall(any);
            }
        }

        private void OnDestroy()
        {
            StuddingWinch[] pair;
            if (mast != null && winches.TryGetValue(mast, out pair) && pair != null && pair[side] == this) pair[side] = null;
            if (winch != null) Destroy(winch.gameObject);
            if (peg != null) Destroy(peg.gameObject);
            if (coil != null) Destroy(coil);
            ShowFall(false);
            if (controller != null) Destroy(controller.gameObject);
        }
    }
}
