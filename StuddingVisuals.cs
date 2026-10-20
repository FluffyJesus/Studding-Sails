using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StuddingSails
{
    // ====================================================================================
    //  Geometry of the sail's head yard (everything in sail-local space)
    // ====================================================================================

    internal sealed class YardInfo
    {
        public bool Valid;
        public float Key;
        public float NextTry;

        public Transform Yard;
        public Renderer YardRenderer;

        public Vector3 Center;  // yard centre
        public Vector3 Along;   // along the yard, towards starboard
        public Vector3 Up;      // up along the mast
        public Vector3 Normal;  // normal to the canvas

        public float Half;      // half the yard length
        public float Radius;    // yard radius

        public float TopU;      // canvas top relative to the yard centre (along Up)
        public float BottomU;   // canvas bottom (negative)
        public float TopHalf;   // canvas half-width at the head
        public float BottomHalf;// canvas half-width at the foot

        // parent canvas UV layout: uv = A*(along, up) + B (relative to the yard centre)
        public bool UvOk;
        public bool Measured;
        public string UvNote = "";
        public float UvUw, UvUh, UvU0, UvVw, UvVh, UvV0;
    }

    /// <summary>Anchor at the yard end of a sail without studding sails: ropes from the studding sails below lead here.</summary>
    internal sealed class StuddingTips : MonoBehaviour
    {
        public Transform[] Tip = new Transform[2];
        public float Key = -9999f;
    }

    internal static class StuddingGeom
    {
        private static readonly Dictionary<Sail, YardInfo> cache = new Dictionary<Sail, YardInfo>();

        internal static void Forget(Sail sail)
        {
            if (sail != null) cache.Remove(sail);
        }

        private static float KeyOf(Sail sail)
        {
            try { return sail.GetScaleY() * 7.31f + sail.GetScaleZ() * 3.17f; }
            catch (Exception) { return 0f; }
        }

        internal static YardInfo Get(Sail sail)
        {
            float key = KeyOf(sail);
            YardInfo info;
            if (cache.TryGetValue(sail, out info))
            {
                if (info.Valid && info.Yard != null && Mathf.Abs(info.Key - key) < 0.0005f) return info;
                if (!info.Valid && Time.time < info.NextTry) return info;
            }

            info = Measure(sail, key);
            cache[sail] = info;
            return info;
        }

        /// <summary>Anchor at the yard end (0 = port, 1 = starboard). Created on demand.</summary>
        internal static Transform GetTip(Sail sail, int side)
        {
            YardInfo info = Get(sail);
            if (!info.Valid) return null;

            var tips = sail.GetComponent<StuddingTips>();
            if (tips == null) tips = sail.gameObject.AddComponent<StuddingTips>();

            for (int s = 0; s < 2; s++)
            {
                if (tips.Tip[s] != null) continue;
                var go = new GameObject("StuddingYardTip" + s);
                go.transform.SetParent(sail.transform, false);
                go.layer = sail.gameObject.layer;
                tips.Tip[s] = go.transform;
                tips.Key = -9999f;
            }

            if (Mathf.Abs(tips.Key - info.Key) > 0.0001f)
            {
                tips.Key = info.Key;
                tips.Tip[0].localPosition = info.Center - info.Along * info.Half;
                tips.Tip[1].localPosition = info.Center + info.Along * info.Half;
            }

            return tips.Tip[side];
        }

        private static YardInfo Measure(Sail sail, float key)
        {
            var info = new YardInfo();
            info.Key = key;
            info.NextTry = Time.time + 1f;

            try
            {
                Renderer yardRend;
                Transform yard = FindTopYard(sail, out yardRend);
                if (yard == null || yard == sail.transform) return info;

                Mesh mesh = MeshOf(yard);
                if (mesh == null) return info;

                Transform st = sail.transform;
                Bounds b = mesh.bounds;
                Vector3 lossy = yard.lossyScale;
                float[] dims = new float[]
                {
                    b.size.x * Mathf.Abs(lossy.x),
                    b.size.y * Mathf.Abs(lossy.y),
                    b.size.z * Mathf.Abs(lossy.z)
                };

                int axis = 0;
                if (dims[1] > dims[axis]) axis = 1;
                if (dims[2] > dims[axis]) axis = 2;
                Vector3 axisVec = axis == 0 ? Vector3.right : (axis == 1 ? Vector3.up : Vector3.forward);
                float ext = b.extents[axis];

                Vector3 e1 = st.InverseTransformPoint(yard.TransformPoint(b.center + axisVec * ext));
                Vector3 e0 = st.InverseTransformPoint(yard.TransformPoint(b.center - axisVec * ext));
                Vector3 center = st.InverseTransformPoint(yard.TransformPoint(b.center));

                Vector3 along = e1 - e0;
                float half = along.magnitude * 0.5f;
                if (half < 0.3f) return info;
                along = along / (half * 2f);

                GameObject ship = Studding.ShipOf(sail);
                Vector3 upW = ship != null ? ship.transform.up : Vector3.up;
                Vector3 rightW = ship != null ? ship.transform.right : Vector3.right;

                Vector3 up = st.InverseTransformDirection(upW).normalized;
                if (Vector3.Dot(along, st.InverseTransformDirection(rightW)) < 0f) along = -along;
                along = (along - up * Vector3.Dot(along, up)).normalized;
                Vector3 normal = Vector3.Cross(along, up).normalized;

                float thick = (dims[(axis + 1) % 3] + dims[(axis + 2) % 3]) * 0.25f;

                // Refine the yard axis from the vertices of its ends: the parrel and fittings shift the bounds centre
                try
                {
                    Vector3[] yv = mesh.vertices;
                    if (yv != null && yv.Length > 0)
                    {
                        Vector3 sum = Vector3.zero;
                        int cnt = 0;
                        var perps = new List<Vector3>();
                        for (int i = 0; i < yv.Length; i++)
                        {
                            Vector3 l = st.InverseTransformPoint(yard.TransformPoint(yv[i])) - center;
                            float a = Vector3.Dot(l, along);
                            if (Mathf.Abs(a) < half * 0.55f) continue;
                            Vector3 perp = l - along * a;
                            perps.Add(perp);
                            sum += perp;
                            cnt++;
                        }

                        if (cnt >= 8)
                        {
                            Vector3 off = sum / cnt;
                            float rSum = 0f;
                            for (int i = 0; i < perps.Count; i++) rSum += (perps[i] - off).magnitude;
                            center += off;
                            thick = rSum / cnt;
                        }
                    }
                }
                catch (Exception)
                {
                    // mesh not readable: keep the bounds centre
                }

                // Canvas size from the rest-pose vertices of the sail mesh
                float height = sail.GetScaledHeight() + 0.28f;
                float topU = -0.05f, bottomU = -height, topHalf = half, bottomHalf = half;
                bool measured = false;

                var smr = sail.cloth != null ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr != null && smr.sharedMesh != null)
                {
                    Vector3[] verts = smr.sharedMesh.vertices;
                    Transform ct = smr.transform;
                    float[] us = new float[verts.Length];
                    float[] ws = new float[verts.Length];
                    float minU = float.MaxValue, maxU = float.MinValue;

                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 l = st.InverseTransformPoint(ct.TransformPoint(verts[i])) - center;
                        ws[i] = Vector3.Dot(l, along);
                        us[i] = Vector3.Dot(l, up);
                        if (us[i] < minU) minU = us[i];
                        if (us[i] > maxU) maxU = us[i];
                    }

                    float span = maxU - minU;
                    if (span > 0.5f && span > height * 0.3f && span < height * 3f && Mathf.Abs(maxU) < 1.5f)
                    {
                        float band = span * 0.08f;
                        float tH = 0f, bH = 0f;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            if (us[i] > maxU - band) tH = Mathf.Max(tH, Mathf.Abs(ws[i]));
                            if (us[i] < minU + band) bH = Mathf.Max(bH, Mathf.Abs(ws[i]));
                        }

                        topU = maxU;
                        bottomU = minU;
                        if (tH > 0.3f) topHalf = tH;
                        if (bH > 0.3f) bottomHalf = bH;
                        measured = true;
                    }

                    // UV layout: linear fit of uv against vertex position (handles rotated/mirrored UVs)
                    try
                    {
                        Vector2[] muv = smr.sharedMesh.uv;
                        info.UvNote = "uv=" + (muv != null ? muv.Length : -1) + "/verts=" + verts.Length;
                        if (muv != null && muv.Length == verts.Length && verts.Length >= 8)
                        {
                            float[] cu, cv;
                            if (FitPlane(ws, us, muv, true, out cu) && FitPlane(ws, us, muv, false, out cv))
                            {
                                info.UvUw = cu[0]; info.UvUh = cu[1]; info.UvU0 = cu[2];
                                info.UvVw = cv[0]; info.UvVh = cv[1]; info.UvV0 = cv[2];
                                info.UvOk = true;
                                info.UvNote += " fitted";
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        info.UvNote = "uv read failed: " + e.Message;
                    }
                }

                info.Yard = yard;
                info.YardRenderer = yardRend;
                info.Center = center;
                info.Along = along;
                info.Up = up;
                info.Normal = normal;
                info.Half = half;
                info.Radius = Mathf.Clamp(thick * 0.95f, 0.03f, 0.12f);
                info.TopU = topU;
                info.BottomU = bottomU;
                info.TopHalf = topHalf;
                info.BottomHalf = bottomHalf;
                info.Valid = true;
                info.Measured = measured;

                // UVs not read: assume the standard layout, u across the sail 0..1, v bottom to top 0..1
                if (!info.UvOk)
                {
                    float hh = Mathf.Max(0.5f, topU - bottomU);
                    info.UvUw = 1f / (2f * Mathf.Max(0.3f, topHalf)); info.UvUh = 0f; info.UvU0 = 0.5f;
                    info.UvVw = 0f; info.UvVh = 1f / hh; info.UvV0 = 1f - topU / hh;
                    info.UvOk = true;
                    info.UvNote += " (assumed standard layout)";
                }

                if (Studding.VerboseLog.Value)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "[Studding] {0}: yard '{1}' half={2:F2} radius={3:F3} | cloth {4}: top={5:F2}/{6:F2} bottom={7:F2}/{8:F2} (u/half)",
                        sail.name, yard.name, half, info.Radius, measured ? "measured" : "fallback",
                        topU, topHalf, bottomU, bottomHalf));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Studding] geometry measure failed for " + sail.name + ": " + e);
            }

            return info;
        }

        /// <summary>Least squares: t ~ a*w + b*h + c (t = u or v).</summary>
        internal static bool FitPlane(float[] w, float[] h, Vector2[] uv, bool takeU, out float[] coef)
        {
            coef = null;
            double sww = 0, swh = 0, shh = 0, sw = 0, sh = 0, n = w.Length;
            double swt = 0, sht = 0, st = 0;
            for (int i = 0; i < w.Length; i++)
            {
                double t = takeU ? uv[i].x : uv[i].y;
                sww += w[i] * w[i]; swh += w[i] * h[i]; shh += h[i] * h[i];
                sw += w[i]; sh += h[i];
                swt += w[i] * t; sht += h[i] * t; st += t;
            }

            // | sww swh sw | |a|   |swt|
            // | swh shh sh | |b| = |sht|
            // | sw  sh  n  | |c|   |st |
            double det = sww * (shh * n - sh * sh) - swh * (swh * n - sh * sw) + sw * (swh * sh - shh * sw);
            if (Math.Abs(det) < 1e-9) return false;

            double da = swt * (shh * n - sh * sh) - swh * (sht * n - sh * st) + sw * (sht * sh - shh * st);
            double db = sww * (sht * n - st * sh) - swt * (swh * n - sh * sw) + sw * (swh * st - sht * sw);
            double dc = sww * (shh * st - sh * sht) - swh * (swh * st - sht * sw) + swt * (swh * sh - shh * sw);

            coef = new float[] { (float)(da / det), (float)(db / det), (float)(dc / det) };
            return true;
        }

        internal static Mesh MeshOf(Transform t)
        {
            if (t == null) return null;
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            return smr != null ? smr.sharedMesh : null;
        }

        private static bool UnderRig(Transform t, Transform stop)
        {
            while (t != null && t != stop)
            {
                if (t.name == StuddingRig.RootName) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>The head yard: the highest "long" renderer that is not canvas, rope or collider.</summary>
        private static Transform FindTopYard(Sail sail, out Renderer topRend)
        {
            topRend = null;

            Renderer clothRend = sail.cloth != null ? sail.cloth.GetComponent<Renderer>() : null;
            var reefAnim = sail.GetComponent<ReefEffectAnimUniversal>();
            Renderer furledRend = reefAnim != null ? reefAnim.furledSail : null;

            GameObject ship = Studding.ShipOf(sail);
            Vector3 up = ship != null ? ship.transform.up : Vector3.up;

            Renderer best = null;
            float bestH = 0f, bestDim = 0f;
            bool bestNamed = false;
            bool have = false;

            foreach (var r in sail.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r == clothRend || r == furledRend) continue;
                if (UnderRig(r.transform, sail.transform)) continue;
                if (!r.enabled) continue;

                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) mesh = smr.sharedMesh;
                else
                {
                    var mfr = r.GetComponent<MeshFilter>();
                    if (mfr != null) mesh = mfr.sharedMesh;
                }
                if (mesh == null) continue;

                string nm = (r.name + " " + (r.transform.parent != null ? r.transform.parent.name : "")).ToLowerInvariant();
                if (nm.Contains("col_checker") || nm.Contains("shadow") || nm.Contains("rope") ||
                    nm.Contains("cloth") || nm.Contains("canvas") || nm.Contains("furled") ||
                    nm.Contains("footyard") || nm.Contains("studding"))
                    continue;

                Vector3 s = mesh.bounds.size;
                Vector3 lossy = r.transform.lossyScale;
                float dim = Mathf.Max(s.x * Mathf.Abs(lossy.x), Mathf.Max(s.y * Mathf.Abs(lossy.y), s.z * Mathf.Abs(lossy.z)));
                if (dim <= 0.8f) continue;

                Vector3 centerWorld = r.transform.TransformPoint(mesh.bounds.center);
                float h = Vector3.Dot(centerWorld - sail.transform.position, up);
                bool named = nm.Contains("yard") || nm.Contains("spar") || nm.Contains("boom");

                if (!have)
                {
                    best = r; bestH = h; bestDim = dim; bestNamed = named; have = true;
                    continue;
                }

                if (h > bestH + 0.25f)
                {
                    best = r; bestH = h; bestDim = dim; bestNamed = named;
                }
                else if (h > bestH - 0.25f)
                {
                    if ((named && !bestNamed) || (named == bestNamed && dim > bestDim))
                    {
                        best = r; bestH = Mathf.Max(h, bestH); bestDim = dim; bestNamed = named;
                    }
                }
            }

            if (best == null) return null;
            topRend = best;
            return best.transform;
        }
    }

    // ====================================================================================
    //  Meshes
    // ====================================================================================

    internal static class StuddingMeshes
    {
        private static Mesh boom;

        /// <summary>Cylinder along X: length 1 (-0.5..0.5), radius 1, tapering towards +X.</summary>
        internal static Mesh Boom()
        {
            if (boom != null) return boom;

            const int seg = 10, rings = 6;
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();

            for (int i = 0; i <= rings; i++)
            {
                float k = i / (float)rings;
                float x = k - 0.5f;
                float r = 1f - 0.3f * k;
                for (int j = 0; j <= seg; j++)
                {
                    float a = j / (float)seg * Mathf.PI * 2f;
                    v.Add(new Vector3(x, r * Mathf.Cos(a), r * Mathf.Sin(a)));
                    uv.Add(new Vector2(k, j / (float)seg));
                }
            }

            int stride = seg + 1;
            for (int i = 0; i < rings; i++)
            {
                for (int j = 0; j < seg; j++)
                {
                    int a0 = i * stride + j, b0 = a0 + 1, c0 = a0 + stride, d0 = c0 + 1;
                    t.Add(a0); t.Add(b0); t.Add(c0);
                    t.Add(b0); t.Add(d0); t.Add(c0);
                }
            }

            for (int end = 0; end < 2; end++)
            {
                float x = end == 0 ? -0.5f : 0.5f;
                float r = end == 0 ? 1f : 0.7f;
                int center = v.Count;
                v.Add(new Vector3(x, 0f, 0f)); uv.Add(new Vector2(end, 0.5f));
                int ring0 = v.Count;
                for (int j = 0; j <= seg; j++)
                {
                    float a = j / (float)seg * Mathf.PI * 2f;
                    v.Add(new Vector3(x, r * Mathf.Cos(a), r * Mathf.Sin(a)));
                    uv.Add(new Vector2(end, j / (float)seg));
                }
                for (int j = 0; j < seg; j++)
                {
                    if (end == 1) { t.Add(center); t.Add(ring0 + j); t.Add(ring0 + j + 1); }
                    else { t.Add(center); t.Add(ring0 + j + 1); t.Add(ring0 + j); }
                }
            }

            boom = new Mesh();
            boom.name = "studding_boom";
            boom.SetVertices(v);
            boom.SetUVs(0, uv);
            boom.SetTriangles(t, 0);
            boom.RecalculateNormals();
            boom.RecalculateBounds();
            return boom;
        }

        /// <summary>
        /// Canvas grid for Cloth (single-sided; the sail material is double-sided).
        /// Coordinates are "outboard positive"; sign = -1 mirrors to port.
        /// For the triangle the bottom row converges almost to a point (the apex at the sail's clew).
        /// Returns the area.
        /// </summary>
        internal const float SeamSpacing = 0.55f; // m, width of one cloth panel on the built-in texture

        internal static float BuildGrid(Mesh m, float sign,
            float xInTop, float xOutTop, float xInBot, float xOutBot,
            float yTop, float yBot, bool tri, int nu, int nv, float bulge, float belly = 0f)
        {
            // Slack on a free edge: at rest it is bowed into an arc longer than
            // the distance between the corners by "slack" (for an arc: length ~ chord * (1 + 8/3*(depth/chord)^2)).
            // The foot of a four-sided sail is cut as an upward arc (roach, like real sails):
            // it carries HemSlack more cloth than the chord, and sags to roughly straight under its weight.
            float hemK = tri ? 0f : Mathf.Sqrt(3f * Mathf.Max(0f, Studding.HemSlack.Value) / 8f);
            float hemDepth = (xOutBot - xInBot) * hemK;

            // Side edges bow outwards by SideSlack (the inner one towards the parent sail, the outer one outboard).
            float sideK = tri ? 0f : Mathf.Sqrt(3f * Mathf.Max(0f, Studding.SideSlack.Value) / 8f); // no slack on the triangle
            float sideDepth = (yTop - yBot) * sideK;
            float innerDepth = Mathf.Min(sideDepth, Studding.PanelGap.Value * 0.8f); // stay clear of the parent sail

            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();

            for (int j = 0; j <= nv; j++)
            {
                float fv = j / (float)nv;
                float y = Mathf.Lerp(yTop, yBot, fv);
                float xIn = Mathf.Lerp(xInTop, xInBot, fv);
                float xOut = tri ? Mathf.Lerp(xOutTop, xInBot + 0.06f, fv) : Mathf.Lerp(xOutTop, xOutBot, fv);

                for (int i = 0; i <= nu; i++)
                {
                    float fu = i / (float)nu;
                    float x = Mathf.Lerp(xIn, xOut, fu);
                    // slack: the canvas is already curved at rest, otherwise Cloth keeps it drum-tight
                    // height slack: the canvas is longer than the yard-to-yard distance, the extra length is stored as an arc
                    // (nearly uniform across, otherwise the slack across would be huge)
                    float z = bulge * Mathf.Sin(Mathf.PI * fv) * (0.7f + 0.3f * Mathf.Sin(Mathf.PI * fu));
                    // belly: slack across the width (fades out towards the head)
                    z += belly * Mathf.Sin(Mathf.PI * fu) * Mathf.Sqrt(fv);
                    float xx = x, yy = y;

                    // foot roach: upward arc at the foot, fading out above
                    yy += hemDepth * Mathf.Sin(Mathf.PI * fu) * fv * fv * fv;

                    // side slack: outward bow at the edge, fading towards the middle
                    float bow = Mathf.Sin(Mathf.PI * fv);
                    xx += sideDepth * bow * fu * fu * fu;
                    xx -= innerDepth * bow * (1f - fu) * (1f - fu) * (1f - fu);
                    v.Add(new Vector3(sign * xx, yy, z));
                    // u in metres from the inner edge: same seam spacing on every studding sail
                    uv.Add(new Vector2((x - xInTop) / SeamSpacing, 1f - fv));
                }
            }

            int stride = nu + 1;
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++)
                {
                    int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                    if (sign > 0f)
                    {
                        t.Add(a); t.Add(b); t.Add(c);
                        t.Add(b); t.Add(d); t.Add(c);
                    }
                    else
                    {
                        t.Add(a); t.Add(c); t.Add(b);
                        t.Add(b); t.Add(c); t.Add(d);
                    }
                }
            }

            m.Clear();
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();

            float wTop = xOutTop - xInTop;
            float wBot = tri ? 0f : (xOutBot - xInBot);
            return 0.5f * (wTop + wBot) * (yTop - yBot);
        }
    }

    // ====================================================================================
    //  Fallback canvas material: built-in canvas texture tinted like the parent sail
    // ====================================================================================

    internal static class StuddingCanvas
    {
        private static readonly Dictionary<Texture, Color> avgCache = new Dictionary<Texture, Color>();
        private static readonly Dictionary<int, Texture2D> texCache = new Dictionary<int, Texture2D>();
        private static readonly Color DefaultTint = new Color(0.93f, 0.9f, 0.82f);

        /// <summary>Copy of the sail material with our texture. _Color is synced with the sail later.</summary>
        internal static Material Make(Material parent)
        {
            Material m;
            Texture parentTex = null;

            if (parent != null)
            {
                m = new Material(parent);
                if (parent.HasProperty("_MainTex")) parentTex = parent.GetTexture("_MainTex");
            }
            else
            {
                m = new Material(Shader.Find("Standard"));
            }

            Texture2D tex = CanvasTexture(AverageColor(parentTex));
            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", Vector2.one);
                m.SetTextureOffset("_MainTex", Vector2.zero);
            }

            // normal/detail maps are laid out for the parent sail: drop them
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
            if (m.HasProperty("_DetailAlbedoMap")) m.SetTexture("_DetailAlbedoMap", null);
            if (m.HasProperty("_DetailNormalMap")) m.SetTexture("_DetailNormalMap", null);

            m.name = "StuddingCanvas";
            return m;
        }

        /// <summary>Average colour of the sail texture (downsampled on the GPU: game textures are not CPU-readable).</summary>
        private static Color AverageColor(Texture tex)
        {
            if (tex == null) return DefaultTint;

            Color cached;
            if (avgCache.TryGetValue(tex, out cached)) return cached;

            Color result = DefaultTint;
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tmp = null;
            try
            {
                const int n = 32;
                rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(n, n, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, n, n), 0, 0);
                tmp.Apply();

                Color[] px = tmp.GetPixels();
                float r = 0f, g = 0f, b = 0f;
                for (int i = 0; i < px.Length; i++) { r += px[i].r; g += px[i].g; b += px[i].b; }
                result = new Color(r / px.Length, g / px.Length, b / px.Length, 1f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Studding] could not sample sail texture colour: " + e.Message);
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }

            avgCache[tex] = result;
            return result;
        }

        /// <summary>Canvas: flat tone, a darker seam at each panel edge and slight grain.</summary>
        private static Texture2D CanvasTexture(Color tint)
        {
            int key = Mathf.RoundToInt(tint.r * 255f) << 16 | Mathf.RoundToInt(tint.g * 255f) << 8 | Mathf.RoundToInt(tint.b * 255f);
            Texture2D tex;
            if (texCache.TryGetValue(key, out tex) && tex != null) return tex;

            const int w = 64, h = 64;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.name = "studding_canvas";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            var px = new Color[w * h];
            var rnd = new System.Random(1234);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float k = 1f;

                    // seam: two stitch lines at the panel edges
                    int dx = Mathf.Min(x, w - 1 - x);
                    if (dx == 0) k *= 0.82f;
                    else if (dx == 1) k *= 0.9f;
                    else if (dx == 3) k *= 0.96f;

                    // slight grain, a little darker towards the panel middle
                    k *= 0.985f + 0.03f * (float)rnd.NextDouble();
                    k *= 1f - 0.03f * Mathf.Sin(Mathf.PI * x / (w - 1f));

                    px[y * w + x] = new Color(tint.r * k, tint.g * k, tint.b * k, 1f);
                }
            }

            tex.SetPixels(px);
            tex.Apply(true);
            texCache[key] = tex;
            return tex;
        }
    }

    // ====================================================================================
    //  Studding sails of one sail
    // ====================================================================================

    internal sealed class StuddingRig
    {
        internal const string RootName = "StuddingRig";

        private const int GridU = 8;
        private const int GridV = 14;

        private static readonly Dictionary<Sail, StuddingRig> rigs = new Dictionary<Sail, StuddingRig>();

        /// <summary>Bumped when settings change in the window: all studding sails are rebuilt.</summary>
        internal static int LayoutVersion;

        /// <summary>Cloth state of all studding sails (debug dump).</summary>
        internal static void DumpAll(System.Text.StringBuilder sb)
        {
            sb.AppendLine("[Studding] ===== studding sails =====");
            foreach (var kv in rigs)
            {
                StuddingRig r = kv.Value;
                if (kv.Key == null) continue;
                sb.AppendLine("--- " + kv.Key.name + ": visible=" + r.Visible + " panels=" + r.panelsShown +
                              " builtK=" + r.builtK.ToString("F2") + " lastK=" + r.lastK.ToString("F2") +
                              " tri=" + r.triangle + " area=" + r.Area.ToString("F1"));
                sb.AppendLine("  geometry: " + r.bakeNote);
                sb.AppendLine("  texture: useSail=" + r.useSailTexture + " fitted=" + r.uvFitted + " mat=" +
                              (r.clothMat != null ? r.clothMat.name : "-") + " tex=" + (r.srcTex != null ? r.srcTex.name : "-") +
                              " | " + r.uvNote +
                              string.Format(" u=({0:F3},{1:F3},{2:F3}) v=({3:F3},{4:F3},{5:F3})",
                                  r.uUw, r.uUh, r.uU0, r.uVw, r.uVh, r.uV0));
                for (int s = 0; s < 2; s++)
                {
                    Side sd = r.sides[s];
                    if (sd == null) continue;
                    Cloth c = sd.Cloth;
                    if (c == null) { sb.AppendLine("  side " + s + ": no cloth, panel=" + (sd.PanelGo != null && sd.PanelGo.activeInHierarchy)); continue; }

                    int pinned = 0, free = 0, limited = 0;
                    try
                    {
                        foreach (var k in c.coefficients)
                        {
                            if (k.maxDistance <= 0.0001f) pinned++;
                            else if (k.maxDistance > 1e6f) free++;
                            else limited++;
                        }
                    }
                    catch (Exception) { }

                    sb.AppendLine("  side " + s + ": enabled=" + c.enabled + " active=" + c.gameObject.activeInHierarchy +
                                  " verts=" + c.vertices.Length + " pinned/free/limited=" + pinned + "/" + free + "/" + limited +
                                  " coeffPending=" + sd.CoeffPending);
                    sb.AppendLine("    damping=" + c.damping + " stretch=" + c.stretchingStiffness + " bend=" + c.bendingStiffness +
                                  " sleep=" + c.sleepThreshold + " gravity=" + c.useGravity +
                                  " ext=" + c.externalAcceleration + " rnd=" + c.randomAcceleration);
                }
            }
            sb.AppendLine("Physics.clothGravity=" + Physics.clothGravity);
        }

        internal static StuddingRig Of(Sail sail)
        {
            StuddingRig r;
            return sail != null && rigs.TryGetValue(sail, out r) ? r : null;
        }

        private sealed class Side
        {
            public Transform Boom;
            public Transform BoomTip;    // outer end of the studding boom (halyard, sheets from above)
            public Transform BoomInner;  // inner head corner lashing: above the main yard end
            public Transform SheetInner; // where the inner sheet from the studding sail above is made fast

            public Transform TopIn, TopOut, BotIn, BotOut; // canvas corners

            public GameObject PanelGo;
            public Mesh PanelMesh;
            public Cloth Cloth;
            public bool CoeffPending;
            public Vector3 PinIn, PinOut; // pinned lower corners (rest pose, local)
            public Vector3[] RestVerts;
            public Transform TopBone, BotBone;
            public Vector3 BotRest;

            public GameObject Roll;       // furled studding sail on the boom
        }

        private sealed class RopeLink
        {
            public GameObject Go;
            public RopeEffect Rope;
            public Transform Holder;
            public Transform From;
            public Transform Target;
        }

        private readonly Sail sail;
        private readonly Side[] sides = new Side[2];
        private readonly Dictionary<string, RopeLink> ropes = new Dictionary<string, RopeLink>();

        private GameObject root;
        private Material[] woodMats;
        private Material clothMat;
        private Renderer parentRenderer;
        private bool useSailTexture;
        private Material srcMat;
        private Texture srcTex;
        private YardInfo curInfo;
        private bool uvFitted;
        private bool geomBaked;
        private float bakedBottomHalf, bakedBottomU;
        private string bakeNote = "not baked yet";
        private float uUw, uUh, uU0, uVw, uVh, uV0, uvTop, uvBot, uvEdge;
        private string uvNote = "not fitted yet";

        private float laidOutKey = -9999f;
        private int laidVersion = -1;
        private float laidYBot = float.NaN;
        private float laidBelowWidth = 1f;
        private bool triangle;
        private bool wasShown;

        // canvas geometry with the sail fully set ("outboard positive" coordinates)
        private float gXInTop, gXOutTop, gXInBot, gXOutBot, gYTop, gYBot, gWidth;
        private bool geomOk;

        private bool needBuild = true;
        private bool panelsShown;
        private float builtK = -1f;
        private bool builtTri;
        private float lastK = -1f, stableTime;
        private float laidWidth = -1f;

        private Sail above, below;
        private float nextNeighbourCheck;

        /// <summary>Studding sail canvas area (used for thrust).</summary>
        internal float Area;

        /// <summary>Rig shown (booms in place); the canvas may be furled.</summary>
        internal bool Visible;
        internal bool PanelsShown { get { return panelsShown; } }

        internal StuddingRig(Sail sail)
        {
            this.sail = sail;
            rigs[sail] = this;
        }

        // ------------------------------------------------------------ lifecycle

        internal void Tick(bool deployed)
        {
            YardInfo info = StuddingGeom.Get(sail);
            if (!info.Valid)
            {
                Hide();
                return;
            }

            curInfo = info;
            if (root == null) Build(info);
            if (root == null) return;

            if (Time.time >= nextNeighbourCheck)
            {
                nextNeighbourCheck = Time.time + 0.5f;
                above = FindNeighbour(true);
                below = FindNeighbour(false);

                bool tri = below == null || !Studding.IsOn(below); // no studding sails below: triangle
                if (tri != triangle)
                {
                    triangle = tri;
                    laidOutKey = -9999f;
                }
                else if (!triangle && root.activeSelf)
                {
                    // the yard below may have been moved or rescaled at the shipyard
                    float yb = BelowYardY();
                    bool had = !float.IsNaN(laidYBot), has = !float.IsNaN(yb);
                    if (had != has || (has && Mathf.Abs(yb - laidYBot) > 0.05f)) laidOutKey = -9999f;
                    if (below != null && Mathf.Abs(Studding.GetWidth(below) - laidBelowWidth) > 0.001f) laidOutKey = -9999f;
                }
            }

            if (!root.activeSelf) root.SetActive(true);
            Visible = true;

            TryBakeGeometry();
            if (laidVersion != LayoutVersion || Mathf.Abs(laidOutKey - info.Key) > 0.0001f ||
                Mathf.Abs(laidWidth - Studding.GetWidth(sail)) > 0.001f) Layout(info);

            SyncColor();
            UpdatePanels(deployed);

            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                if (sd.CoeffPending) ApplyCoefficients(sd);
                if (!wasShown && sd.Cloth != null) sd.Cloth.ClearTransformMotion();
                UpdateClothWind(sd);
            }
            wasShown = true;

            UpdateRopes();
        }

        internal void Hide()
        {
            Visible = false;
            wasShown = false;
            KillAllRopes();
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        internal void Dispose()
        {
            Hide();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            if (clothMat != null) UnityEngine.Object.Destroy(clothMat);
            if (woodMats != null)
                foreach (var m in woodMats) if (m != null) UnityEngine.Object.Destroy(m);
            rigs.Remove(sail);
            StuddingGeom.Forget(sail);
        }

        // ------------------------------------------------------------ building

        private Transform NewChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.layer = sail.gameObject.layer;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private void Build(YardInfo info)
        {
            root = new GameObject(RootName);
            root.layer = sail.gameObject.layer;
            root.transform.SetParent(sail.transform, false);

            below = FindNeighbour(false);
            above = FindNeighbour(true);
            triangle = below == null || !Studding.IsOn(below); // no studding sails below: triangle
            nextNeighbourCheck = Time.time + 0.5f;

            woodMats = MakeWoodMaterials(info.YardRenderer);

            parentRenderer = sail.cloth != null ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : null;
            // built-in canvas until the sail material is taken (or if the sail UVs cannot be read)
            useSailTexture = Studding.UseSailTexture.Value;
            clothMat = StuddingCanvas.Make(parentRenderer != null ? parentRenderer.sharedMaterial : null);
            SyncColor();

            var reef = sail.GetComponent<ReefEffectAnimUniversal>();
            Renderer furled = reef != null ? reef.furledSail : null;

            for (int s = 0; s < 2; s++)
            {
                var sd = new Side();
                string tag = s == 0 ? "L" : "R";

                sd.Boom = NewChild("Boom_" + tag, root.transform);
                sd.Boom.gameObject.AddComponent<MeshFilter>().sharedMesh = StuddingMeshes.Boom();
                var br = sd.Boom.gameObject.AddComponent<MeshRenderer>();
                br.sharedMaterials = woodMats;
                br.shadowCastingMode = ShadowCastingMode.On;
                br.receiveShadows = true;
                CopyPropertyBlock(info.YardRenderer, br);

                sd.BoomTip = NewChild("BoomTip_" + tag, root.transform);
                sd.BoomInner = NewChild("BoomInner_" + tag, root.transform);
                sd.SheetInner = NewChild("SheetInner_" + tag, root.transform);
                sd.TopIn = NewChild("TopIn_" + tag, root.transform);
                sd.TopOut = NewChild("TopOut_" + tag, root.transform);
                sd.BotIn = NewChild("BotIn_" + tag, root.transform);
                sd.BotOut = NewChild("BotOut_" + tag, root.transform);

                sd.Roll = MakeRoll(furled, "Roll_" + tag);

                sides[s] = sd;
            }

            laidOutKey = -9999f;
        }

        /// <summary>Clone of the vanilla furled sail (furledSail), shown on the boom when the studding sail is furled.</summary>
        private GameObject MakeRoll(Renderer furled, string name)
        {
            if (furled == null) return null;
            Mesh mesh = StuddingGeom.MeshOf(furled.transform);
            if (mesh == null) return null;

            Transform t = NewChild(name, root.transform);
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterials = furled.sharedMaterials; // shared material: picks up the sail colour automatically
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            t.gameObject.SetActive(false);
            return t.gameObject;
        }

        private void LayoutRoll(Side sd, float sign, float xCenter, float length, float yUnder /* boom centre */)
        {
            if (sd.Roll == null) return;

            var reef = sail.GetComponent<ReefEffectAnimUniversal>();
            Renderer furled = reef != null ? reef.furledSail : null;
            Mesh mesh = sd.Roll.GetComponent<MeshFilter>().sharedMesh;
            if (furled == null || mesh == null) return;

            Bounds b = mesh.bounds;
            Vector3 lossy = furled.transform.lossyScale;
            Vector3 sz = b.size;
            float[] dims = { sz.x * Mathf.Abs(lossy.x), sz.y * Mathf.Abs(lossy.y), sz.z * Mathf.Abs(lossy.z) };
            int axis = 0;
            if (dims[1] > dims[axis]) axis = 1;
            if (dims[2] > dims[axis]) axis = 2;

            // the long mesh axis goes along the boom (root X)
            Quaternion rot = axis == 0 ? Quaternion.identity
                           : axis == 1 ? Quaternion.Euler(0f, 0f, -90f)
                           : Quaternion.Euler(0f, 90f, 0f);

            const float thin = 0.75f; // thinner than the parent sail
            Vector3 scale = new Vector3(Mathf.Abs(lossy.x) * thin, Mathf.Abs(lossy.y) * thin, Mathf.Abs(lossy.z) * thin);
            float meshLen = axis == 0 ? sz.x : axis == 1 ? sz.y : sz.z;
            float along = length / Mathf.Max(0.01f, meshLen);
            if (axis == 0) scale.x = along; else if (axis == 1) scale.y = along; else scale.z = along;

            float cross = 0f;
            for (int i = 0; i < 3; i++) if (i != axis) cross = Mathf.Max(cross, dims[i] * thin);

            // tucked under the boom, like the vanilla furled sail under its yard
            Vector3 target = new Vector3(sign * xCenter, yUnder - cross * 0.3f, Studding.BoomOffsetZ.Value);
            Transform t = sd.Roll.transform;
            t.localRotation = rot;
            t.localScale = scale;
            t.localPosition = target - rot * Vector3.Scale(scale, b.center);
        }

        /// <summary>
        /// Canvas material: a copy of the parent sail material (texture, normal map, shader),
        /// recreated when the sail material or texture changes (e.g. via ShipyardExpansion).
        /// Plus colour (ChangeSailColor writes it into _Color of the parent canvas material).
        /// </summary>
        private void SyncColor()
        {
            if (clothMat == null || parentRenderer == null || sail.currentUnroll < 0.05f) return;

            Material pm = parentRenderer.sharedMaterial; // an empty material while the sail is furled
            if (pm == null || pm == clothMat) return;

            TryFitUv();
            if (useSailTexture && uvFitted)
            {
                Texture tex = pm.HasProperty("_MainTex") ? pm.GetTexture("_MainTex") : null;
                if (pm != srcMat || tex != srcTex)
                {
                    srcMat = pm;
                    srcTex = tex;
                    Material old = clothMat;
                    clothMat = new Material(pm);
                    clothMat.name = "StuddingSailCloth";
                    for (int s = 0; s < 2; s++)
                    {
                        Side sd = sides[s];
                        if (sd == null || sd.PanelGo == null) continue;
                        var r = sd.PanelGo.GetComponent<Renderer>();
                        if (r != null) r.sharedMaterial = clothMat;
                    }
                    if (old != null) UnityEngine.Object.Destroy(old);
                }
            }

            if (!pm.HasProperty("_Color") || !clothMat.HasProperty("_Color")) return;

            Color c = pm.color;
            if (c != clothMat.color) clothMat.color = c;
        }

        private static Material[] MakeWoodMaterials(Renderer yardRend)
        {
            if (yardRend != null && yardRend.sharedMaterials != null && yardRend.sharedMaterials.Length > 0)
            {
                var src = yardRend.sharedMaterials;
                var mats = new Material[src.Length];
                for (int i = 0; i < mats.Length; i++)
                    mats[i] = src[i] != null ? new Material(src[i]) : null;
                return mats;
            }

            var m = new Material(Shader.Find("Standard"));
            m.color = new Color(0.243f, 0.173f, 0.125f);
            return new Material[] { m };
        }

        private static void CopyPropertyBlock(Renderer from, Renderer to)
        {
            if (from == null || to == null) return;
            var mpb = new MaterialPropertyBlock();
            from.GetPropertyBlock(mpb);
            to.SetPropertyBlock(mpb);
        }

        // ------------------------------------------------------------ layout

        /// <summary>Height of the top of the yard below, in root space (NaN if none).</summary>
        private float BelowYardY()
        {
            if (below == null || root == null) return float.NaN;
            YardInfo b = StuddingGeom.Get(below);
            if (!b.Valid) return float.NaN;
            Vector3 c = root.transform.InverseTransformPoint(below.transform.TransformPoint(b.Center));
            return c.y + b.Radius;
        }

        /// <summary>Point on the yard below (starboard side), in root space.</summary>
        private Vector3 BelowPoint(YardInfo b, float along, float up)
        {
            Vector3 local = b.Center + b.Along * along + b.Up * up;
            return root.transform.InverseTransformPoint(below.transform.TransformPoint(local));
        }

        private void Layout(YardInfo info)
        {
            laidOutKey = info.Key;
            laidVersion = LayoutVersion;
            needBuild = true;
            KillAllRopes();

            // Root: X = along the yard (outboard to starboard), Y = up the mast, Z = canvas normal
            root.transform.localPosition = info.Center;
            root.transform.localRotation = Quaternion.LookRotation(info.Normal, info.Up);
            root.transform.localScale = Vector3.one;

            float yardLen = info.Half * 2f;
            float gap = Studding.PanelGap.Value;
            laidWidth = Studding.GetWidth(sail);
            float panelWidth = Mathf.Max(0.3f, yardLen * Studding.PanelWidthRatio.Value * laidWidth);
            float overlap = Mathf.Max(0.2f, yardLen * Studding.BoomOverlapRatio.Value);
            const float outerMargin = 0.15f;

            float boomRadius = info.Radius * 0.75f;
            float boomY = info.Radius + boomRadius * 0.9f; // lies on top of the main yard
            float boomZ = Studding.BoomOffsetZ.Value;

            // Inner edge: where the boom leaves the main yard end (overlap not counted), plus the gap
            float yTop = -0.25f;
            float xInTop = info.Half + gap;
            float xOutTop = xInTop + panelWidth;
            float aIn = info.Half - overlap;
            float aOut = xOutTop + outerMargin;
            float boomLen = aOut - aIn;

            float xInBot, xOutBot, yBot;
            laidYBot = float.NaN;
            laidBelowWidth = below != null ? Studding.GetWidth(below) : 1f;

            if (triangle)
            {
                // Lowest studding sail: right triangle, apex at the parent sail's clew
                float bh = geomBaked ? bakedBottomHalf : info.BottomHalf;
                xInBot = Mathf.Max(bh, info.Half * 0.9f) + 0.05f;
                xOutBot = xInBot;
                yBot = (geomBaked ? bakedBottomU : info.BottomU) + 0.05f;
            }
            else
            {
                // Upper studding sails: corners at the ends of their own boom (head) and of the studding boom below (foot)
                YardInfo b = below != null ? StuddingGeom.Get(below) : null;
                float yb = BelowYardY();
                if (b != null && b.Valid && !float.IsNaN(yb) && yb < yTop - 0.5f)
                {
                    laidYBot = yb;
                    float bLen = b.Half * 2f;
                    float bBoomR = b.Radius * 0.75f;
                    float bInner = b.Half + gap;
                    float bOuter = bInner + Mathf.Max(0.3f, bLen * Studding.PanelWidthRatio.Value * Studding.GetWidth(below));

                    Vector3 pIn = BelowPoint(b, bInner, b.Radius + bBoomR * 1.9f);
                    Vector3 pOut = BelowPoint(b, bOuter, b.Radius + bBoomR * 1.9f);
                    xInBot = Mathf.Abs(pIn.x);
                    xOutBot = Mathf.Abs(pOut.x);
                    yBot = (pIn.y + pOut.y) * 0.5f + 0.2f;
                }
                else
                {
                    xInBot = Mathf.Max(xInTop, info.BottomHalf + gap);
                    xOutBot = xInBot + panelWidth;
                    yBot = yTop + (info.BottomU - yTop) * Mathf.Clamp(Studding.PanelHeightRatio.Value, 0.3f, 1f);
                }
            }

            gXInTop = xInTop; gXOutTop = xOutTop; gXInBot = xInBot; gXOutBot = xOutBot;
            gYTop = yTop; gYBot = yBot; gWidth = panelWidth;
            geomOk = panelWidth > 0.2f && (yTop - yBot) > 0.3f && xOutBot > xInBot - 0.001f;

            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? -1f : 1f;
                Side sd = sides[s];

                sd.Boom.localPosition = new Vector3(sign * (aIn + aOut) * 0.5f, boomY, boomZ);
                sd.Boom.localRotation = s == 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                sd.Boom.localScale = new Vector3(boomLen, boomRadius, boomRadius);

                sd.BoomTip.localPosition = new Vector3(sign * (aOut - 0.05f), boomY, boomZ);
                sd.BoomInner.localPosition = new Vector3(sign * info.Half, boomY, boomZ);
                sd.SheetInner.localPosition = new Vector3(sign * xInTop, boomY, boomZ);

                sd.TopIn.localPosition = new Vector3(sign * xInTop, yTop, 0f);
                sd.TopOut.localPosition = new Vector3(sign * xOutTop, yTop, 0f);

                // furled bundle along the visible boom length (from the main yard end to the boom end)
                float rollFrom = info.Half + 0.05f, rollTo = aOut - 0.08f;
                LayoutRoll(sd, sign, (rollFrom + rollTo) * 0.5f, rollTo - rollFrom, boomY);
            }

            if (Studding.VerboseLog.Value)
            {
                Plugin.Log.LogInfo(string.Format(
                    "[Studding] {0}: {1} (below: {2}), boom={3:F2} m, panel top x[{4:F2}..{5:F2}] bottom x[{6:F2}..{7:F2}] y[{8:F2}..{9:F2}]",
                    sail.name, triangle ? "triangle" : "trapezoid", below != null ? below.name : "none",
                    boomLen, xInTop, xOutTop, xInBot, xOutBot, yTop, yBot));
            }
        }

        // ------------------------------------------------------------ canvas and furling

        /// <summary>
        /// The canvas is built once per layout and skinned to two bones: the head (boom) and the foot (clews).
        /// Furling smoothly raises the foot bone, like vanilla (where an armature animation furls the sail).
        /// The apex of the lowest triangular studding sail always follows the parent sail's clew.
        /// </summary>
        private void UpdatePanels(bool deployed)
        {
            float k = deployed ? Mathf.Clamp01(sail.currentUnroll) : 0f;
            if (Mathf.Abs(k - lastK) > 0.002f) { lastK = k; stableTime = 0f; }
            else stableTime += Time.deltaTime;

            bool show = geomOk && k >= 0.06f;

            // the canvas is always full size: reefing pulls it up with bones instead of rebuilding
            if (show && needBuild)
            {
                needBuild = false;
                BuildPanels(1f);
            }

            if (show != panelsShown)
            {
                panelsShown = show;
                for (int s = 0; s < 2; s++)
                {
                    Side sd = sides[s];
                    if (sd.Roll != null) sd.Roll.SetActive(!show);
                    if (sd.PanelGo != null) sd.PanelGo.SetActive(show);
                }
            }

            if (!show) return;

            // sail fully set: cloth always on; partly furled: per ClothWhileReefing
            bool settled = Mathf.Abs(k - builtK) <= 0.02f;

            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                if (sd.BotBone == null) continue;

                Vector3 target = BottomAt(s, k);
                if (builtTri)
                {
                    Transform clew = FindClew(s);
                    if (clew != null)
                    {
                        Vector3 c = root.transform.InverseTransformPoint(clew.position);
                        // accept the clew only if it is near the expected apex:
                        // on some sails (cog) the controller attachment is a point at the rail, not the clew
                        if (Mathf.Abs(c.x - target.x) < 0.6f && Mathf.Abs(c.y - target.y) < 1.2f) target = c;
                    }
                }
                sd.BotBone.localPosition = target;

                // like vanilla: cloth keeps running while furling, bones only move the pinned vertices
                bool clothOn = settled || Studding.ClothWhileReefing.Value;
                if (sd.Cloth != null && sd.Cloth.enabled != clothOn)
                {
                    sd.Cloth.enabled = clothOn;
                    if (clothOn) sd.CoeffPending = true;
                }
            }
        }

        /// <summary>Inner lower corner at set fraction k (along the side edge).</summary>
        private Vector3 BottomAt(int s, float k)
        {
            float sign = s == 0 ? -1f : 1f;
            return new Vector3(sign * Mathf.Lerp(gXInTop, gXInBot, k), Mathf.Lerp(gYTop, gYBot, k), 0f);
        }

        /// <summary>Clew of the parent sail on side s: the point the vanilla sheet leads to.</summary>
        private Transform FindClew(int s)
        {
            var sc = sail.GetComponent<SailConnections>();
            if (sc == null) return null;

            Transform a = ClewOf(sc.angleControllerLeft);
            Transform b = ClewOf(sc.angleControllerRight);
            if (a == null || b == null) return a ?? b;

            float ax = root.transform.InverseTransformPoint(a.position).x;
            float bx = root.transform.InverseTransformPoint(b.position).x;
            bool aIsLeft = ax < bx;
            if (s == 0) return aIsLeft ? a : b;
            return aIsLeft ? b : a;
        }

        private static Transform ClewOf(RopeController rc)
        {
            if (rc == null) return null;
            var re = rc.GetComponent<RopeEffect>();
            return re != null ? re.attachment : null;
        }

        /// <summary>
        /// Studding sail UVs from the parent layout: across in metres (same seam spacing),
        /// measured inwards from the outer edge;
        /// in height proportionally from the head to the foot of the parent canvas (seams are vertical, so the stretch is invisible).
        /// </summary>
        private Vector2[] SailUv(Vector3[] verts, float sign)
        {
            float edge = Mathf.Max(uvEdge, 0.3f);
            float h = Mathf.Max(0.01f, gYTop - gYBot);
            var uv = new Vector2[verts.Length];
            for (int n = 0; n < verts.Length; n++)
            {
                Vector3 p = verts[n];
                float f = Mathf.Clamp01((gYTop - p.y) / h);
                float w;
                if (builtTri)
                {
                    // triangle: a strip from the sail edge, vertical seams
                    float d = Mathf.Clamp(gXOutTop - Mathf.Abs(p.x), 0f, edge * 2f);
                    w = sign * (edge - d);
                }
                else
                {
                    // trapezoid: distance from the outer edge in the same row, so seams run parallel to it;
                    // the texture edge has no seams, so take a strip from the sail middle outwards:
                    // the outer edge sits one studding sail width from the middle
                    float xOutRow = Mathf.Lerp(gXOutTop, gXOutBot, f);
                    float d = Mathf.Max(0f, xOutRow - Mathf.Abs(p.x));
                    w = sign * (gWidth - d);
                }
                float hp = Mathf.Lerp(uvTop, uvBot, f);
                uv[n] = new Vector2(uUw * w + uUh * hp + uU0, uVw * w + uVh * hp + uV0);
            }
            return uv;
        }

        /// <summary>
        /// Actual size of the parent canvas (foot height and half-width) from its current shape: BakeMesh
        /// includes bones. On some sails (cog, standard square) the rest-pose mesh lies differently and the
        /// triangle apex drifted sideways. Done once, when the sail is fully set.
        /// </summary>
        private void TryBakeGeometry()
        {
            if (geomBaked || curInfo == null || sail.currentUnroll < 0.97f) return;
            var smr = parentRenderer as SkinnedMeshRenderer;
            if (smr == null) { geomBaked = true; return; }

            try
            {
                var baked = new Mesh();
                smr.BakeMesh(baked);
                Vector3[] verts = baked.vertices;
                UnityEngine.Object.Destroy(baked);
                if (verts == null || verts.Length < 8) { geomBaked = true; return; }

                Transform st = sail.transform, ct = smr.transform;
                YardInfo i = curInfo;
                float minU = float.MaxValue, maxU = float.MinValue;
                var ws = new float[verts.Length];
                var us = new float[verts.Length];
                for (int n = 0; n < verts.Length; n++)
                {
                    Vector3 l = st.InverseTransformPoint(ct.position + ct.rotation * verts[n]) /* BakeMesh already applied the renderer scale */ - i.Center;
                    ws[n] = Vector3.Dot(l, i.Along);
                    us[n] = Vector3.Dot(l, i.Up);
                    if (us[n] < minU) minU = us[n];
                    if (us[n] > maxU) maxU = us[n];
                }

                float span = maxU - minU;
                float bh = 0f;
                for (int n = 0; n < verts.Length; n++)
                    if (us[n] < minU + span * 0.08f) bh = Mathf.Max(bh, Mathf.Abs(ws[n]));

                geomBaked = true;
                float expectH = sail.GetScaledHeight() + 0.28f;
                bool sane = span > 0.5f && bh > 0.3f &&
                            bh < i.Half * 1.5f &&                              // foot no wider than 1.5 half-yards
                            span > expectH * 0.5f && span < expectH * 1.6f;    // height close to the sail height
                bakeNote = string.Format("baked: bottomHalf={0:F2} (yard half {1:F2}) span={2:F2} (expected {3:F2}) {4}",
                    bh, i.Half, span, expectH, sane ? "used" : "rejected");
                if (sane)
                {
                    bakedBottomHalf = bh;
                    bakedBottomU = minU;
                    laidOutKey = -9999f; // rebuild with the real size
                }
                else
                {
                    bakedBottomHalf = i.BottomHalf;
                    bakedBottomU = i.BottomU;
                }
            }
            catch (Exception)
            {
                geomBaked = true;
                bakedBottomHalf = curInfo.BottomHalf;
                bakedBottomU = curInfo.BottomU;
            }
        }

        /// <summary>
        /// Fits the parent sail's UV layout against its ACTUAL current shape (BakeMesh includes bones and
        /// animation) rather than the rest-pose mesh, which differs between sails. Done once, when the sail
        /// is fully set; until then the studding sails use the built-in canvas.
        /// </summary>
        private void TryFitUv()
        {
            if (uvFitted || !useSailTexture || curInfo == null) return;
            if (sail.currentUnroll < 0.97f) return;

            var smr = parentRenderer as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null) { useSailTexture = false; return; }

            try
            {
                Vector2[] muv = smr.sharedMesh.uv;
                var baked = new Mesh();
                smr.BakeMesh(baked);
                Vector3[] verts = baked.vertices;
                UnityEngine.Object.Destroy(baked);

                if (muv == null || verts == null || muv.Length != verts.Length || verts.Length < 8)
                {
                    uvNote = "uv/verts mismatch " + (muv != null ? muv.Length : -1) + "/" + (verts != null ? verts.Length : -1);
                    useSailTexture = false;
                    return;
                }

                Transform st = sail.transform, ct = smr.transform;
                YardInfo i = curInfo;
                var ws = new float[verts.Length];
                var us = new float[verts.Length];
                float minU = float.MaxValue, maxU = float.MinValue;
                for (int n = 0; n < verts.Length; n++)
                {
                    Vector3 l = st.InverseTransformPoint(ct.position + ct.rotation * verts[n]) /* BakeMesh already applied the renderer scale */ - i.Center;
                    ws[n] = Vector3.Dot(l, i.Along);
                    us[n] = Vector3.Dot(l, i.Up);
                    if (us[n] < minU) minU = us[n];
                    if (us[n] > maxU) maxU = us[n];
                }

                float span = Mathf.Max(0.01f, maxU - minU);
                float edge = 0f;
                for (int n = 0; n < verts.Length; n++)
                    if (us[n] > maxU - span * 0.1f) edge = Mathf.Max(edge, Mathf.Abs(ws[n]));

                float[] cu, cv;
                if (!StuddingGeom.FitPlane(ws, us, muv, true, out cu) || !StuddingGeom.FitPlane(ws, us, muv, false, out cv))
                {
                    uvNote = "fit failed";
                    useSailTexture = false;
                    return;
                }

                uUw = cu[0]; uUh = cu[1]; uU0 = cu[2];
                uVw = cv[0]; uVh = cv[1]; uV0 = cv[2];
                uvTop = maxU; uvBot = minU; uvEdge = edge > 0.3f ? edge : i.Half;
                uvFitted = true;
                uvNote = string.Format("fitted on baked mesh: span={0:F2} edge={1:F2}", span, uvEdge);

                // apply the new UVs to existing canvases (no cloth rebuild)
                for (int s = 0; s < 2; s++)
                {
                    Side sd = sides[s];
                    if (sd == null || sd.PanelMesh == null || sd.RestVerts == null) continue;
                    sd.PanelMesh.uv = SailUv(sd.RestVerts, s == 0 ? -1f : 1f);
                }
            }
            catch (Exception e)
            {
                uvNote = "fit error: " + e.Message;
                useSailTexture = false;
            }
        }

        private void BuildPanels(float k)
        {
            Area = 0f;
            panelsShown = false;
            builtK = k;
            builtTri = triangle; // the triangle stays a triangle while furling, its apex follows the clew

            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? -1f : 1f;
                Side sd = sides[s];

                if (sd.PanelGo != null) UnityEngine.Object.Destroy(sd.PanelGo);
                if (sd.TopBone != null) UnityEngine.Object.Destroy(sd.TopBone.gameObject);
                if (sd.BotBone != null)
                {
                    // corner anchors live on the foot bone: move them to the root, delete the bone
                    sd.BotIn.SetParent(root.transform, false);
                    sd.BotOut.SetParent(root.transform, false);
                    UnityEngine.Object.Destroy(sd.BotBone.gameObject);
                }
                sd.PanelGo = null;
                sd.Cloth = null;
                sd.TopBone = null;
                sd.BotBone = null;
                sd.CoeffPending = false;

                if (sd.Roll != null) sd.Roll.SetActive(true);
                if (!geomOk) continue;

                bool tri = builtTri;
                float yBot = Mathf.Lerp(gYTop, gYBot, k);
                float xInBot = Mathf.Lerp(gXInTop, gXInBot, k);
                float xOutBot = Mathf.Lerp(gXOutTop, gXOutBot, k);
                Vector3 pinIn = new Vector3(sign * xInBot, yBot, 0f);
                Vector3 pinOut = new Vector3(sign * (tri ? xInBot : xOutBot), yBot, 0f);

                // bones
                sd.TopBone = NewChild("TopBone", root.transform);
                sd.TopBone.localPosition = new Vector3(0f, gYTop, 0f);
                sd.BotBone = NewChild("BotBone", root.transform);
                sd.BotBone.localPosition = pinIn;
                sd.BotRest = pinIn;

                sd.BotIn.SetParent(sd.BotBone, false);
                sd.BotIn.localPosition = Vector3.zero;
                sd.BotOut.SetParent(sd.BotBone, false);
                sd.BotOut.localPosition = pinOut - pinIn;

                sd.PinIn = pinIn;
                sd.PinOut = pinOut;

                var go = new GameObject("Panel_" + (s == 0 ? "L" : "R"));
                go.layer = sail.gameObject.layer;
                go.transform.SetParent(root.transform, false);
                go.SetActive(false);

                float height = gYTop - yBot;
                // a sine arc is longer than its chord by (pi^2/4)*(depth/chord)^2 -> depth for the requested slack
                float bulge = tri ? 0f : height * (2f / Mathf.PI) * Mathf.Sqrt(Mathf.Max(0f, Studding.Fullness.Value));
                float belly = tri ? 0f : gWidth * (2f / Mathf.PI) * Mathf.Sqrt(Mathf.Max(0f, Studding.Belly.Value));
                // grid density like vanilla sails (~0.6 m per cell): on a finer grid
                // Unity cloth is noticeably stiffer and barely moves
                float cell = Mathf.Max(0.2f, Studding.ClothCell.Value);
                int nu = Mathf.Clamp(Mathf.RoundToInt(gWidth / cell), 3, 12);
                int nvFull = Mathf.Clamp(Mathf.RoundToInt((gYTop - gYBot) / cell), 4, 20);
                int nv = Mathf.Max(3, Mathf.RoundToInt(nvFull * Mathf.Clamp01(k + 0.15f)));

                sd.PanelMesh = new Mesh();
                sd.PanelMesh.name = "studding_panel";
                Area += StuddingMeshes.BuildGrid(sd.PanelMesh, sign, gXInTop, gXOutTop, xInBot, xOutBot,
                    gYTop, yBot, tri, nu, nv, bulge, belly);

                // weights: head on the top bone, blending to the foot bone
                Vector3[] mv = sd.PanelMesh.vertices;
                var bw = new BoneWeight[mv.Length];
                for (int i = 0; i < mv.Length; i++)
                {
                    float v = Mathf.Clamp01((gYTop - mv[i].y) / Mathf.Max(0.01f, height));
                    bw[i].boneIndex0 = 0;
                    bw[i].weight0 = 1f - v;
                    bw[i].boneIndex1 = 1;
                    bw[i].weight1 = v;
                }
                sd.PanelMesh.boneWeights = bw;
                sd.RestVerts = mv;
                if (useSailTexture && uvFitted)
                    sd.PanelMesh.uv = SailUv(mv, sign);

                Matrix4x4 meshToWorld = go.transform.localToWorldMatrix;
                sd.PanelMesh.bindposes = new Matrix4x4[]
                {
                    sd.TopBone.worldToLocalMatrix * meshToWorld,
                    sd.BotBone.worldToLocalMatrix * meshToWorld
                };

                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.sharedMesh = sd.PanelMesh;
                r.bones = new Transform[] { sd.TopBone, sd.BotBone };
                r.rootBone = sd.TopBone;
                r.sharedMaterial = clothMat;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
                r.updateWhenOffscreen = true;

                if (Studding.ClothPhysics.Value)
                {
                    var c = go.AddComponent<Cloth>();
                    CopyClothSettings(sail.cloth, c);
                    sd.Cloth = c;
                    sd.CoeffPending = true;
                }

                sd.PanelGo = go; // activated in UpdatePanels
                go.SetActive(false);
            }
        }

        // ------------------------------------------------------------ cloth

        /// <summary>Cloth settings copied from the parent sail (vanilla: stretching 0.99, bending 1, damping ~0.63, world* = 0).</summary>
        private static void CopyClothSettings(Cloth from, Cloth to)
        {
            if (from == null)
            {
                to.stretchingStiffness = 0.99f;
                to.bendingStiffness = 1f;
                to.damping = 0.63f;
                to.worldVelocityScale = 0f;
                to.worldAccelerationScale = 0f;
                to.useVirtualParticles = 1f;
                return;
            }

            to.stretchingStiffness = from.stretchingStiffness;
            to.bendingStiffness = from.bendingStiffness;
            to.damping = from.damping;
            to.useGravity = from.useGravity;
            to.friction = from.friction;
            to.worldVelocityScale = from.worldVelocityScale;
            to.worldAccelerationScale = from.worldAccelerationScale;
            to.sleepThreshold = 0f; // never sleep: small cloths fall asleep quickly in Unity

            // Bending: Unity keeps the angles between triangles as in the rest mesh.
            // Our rest mesh carries the belly, so at stiffness 1 the cloth would freeze in it; hence softer.
            to.bendingStiffness = Mathf.Clamp01(Studding.ClothBending.Value);
            to.useVirtualParticles = from.useVirtualParticles;
        }

        /// <summary>
        /// Head and lower corners pinned, the rest free, like vanilla sails
        /// (BillowLimit below 3 m limits it).
        /// </summary>
        private void ApplyCoefficients(Side sd)
        {
            if (sd.Cloth == null || sd.PanelGo == null || !sd.PanelGo.activeInHierarchy) return;

            Vector3[] cv = sd.Cloth.vertices;
            if (cv == null || cv.Length == 0) return; // not initialised yet: retry next frame

            // pinning uses the rest shape (particle order matches vertices: the grid has no duplicates)
            Vector3[] pts = (sd.RestVerts != null && sd.RestVerts.Length == cv.Length) ? sd.RestVerts : cv;

            float limit = Studding.MaxBillow.Value;
            bool unlimited = limit >= 2.99f;

            var coeffs = new ClothSkinningCoefficient[cv.Length];
            for (int i = 0; i < cv.Length; i++)
            {
                Vector3 p = pts[i];
                float dTop = Mathf.Max(0f, gYTop - p.y);
                float dIn = Vector2.Distance(new Vector2(p.x, p.y), new Vector2(sd.PinIn.x, sd.PinIn.y));
                float dOut = Vector2.Distance(new Vector2(p.x, p.y), new Vector2(sd.PinOut.x, sd.PinOut.y));

                bool pinned = dTop < 0.02f || dIn < 0.08f || dOut < 0.08f;
                float free = unlimited ? float.MaxValue : Mathf.Min(limit, Mathf.Min(dTop, Mathf.Min(dIn, dOut)) + 0.1f);
                coeffs[i].maxDistance = pinned ? 0f : free;
                coeffs[i].collisionSphereDistance = float.MaxValue;
            }

            sd.Cloth.coefficients = coeffs;
            sd.CoeffPending = false;
        }

        /// <summary>Wind like vanilla (WindCloth: externalAcceleration = 2 x apparent wind), with a multiplier.</summary>
        private void UpdateClothWind(Side sd)
        {
            if (sd.Cloth == null) return;

            Cloth pc = sail.cloth;
            Vector3 ext = pc != null ? pc.externalAcceleration : Vector3.zero;
            Vector3 rnd = pc != null ? pc.randomAcceleration : Vector3.zero;
            if (ext.sqrMagnitude < 0.0001f) ext = sail.apparentWind * 2f;

            // vanilla flutter comes from WindCloth: damping follows the captured wind fraction every frame
            if (pc != null) sd.Cloth.damping = pc.damping;

            float mul = Studding.ClothWind.Value;
            sd.Cloth.externalAcceleration = ext * mul;
            // optional random jitter on top of the vanilla wind
            float flutter = Studding.Flutter.Value;
            Vector3 e = ext * mul;
            sd.Cloth.randomAcceleration = rnd * mul + new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * flutter;
        }

        // ------------------------------------------------------------ neighbours in the stack

        /// <summary>Nearest square sail above/below in the same stack (mast + topmast).</summary>
        private Sail FindNeighbour(bool up)
        {
            GameObject shipGo = Studding.ShipOf(sail);
            if (shipGo == null) return null;

            Transform ship = shipGo.transform;
            Vector3 me = ship.InverseTransformPoint(sail.transform.position);

            Sail best = null;
            float bestDy = float.MaxValue;

            foreach (var other in ship.GetComponentsInChildren<Sail>(false))
            {
                if (other == null || other == sail) continue;
                if (!other.squareSail || Plugin.IsExcludedSail(other)) continue;

                Vector3 p = ship.InverseTransformPoint(other.transform.position);
                float dx = p.x - me.x, dz = p.z - me.z;
                if (dx * dx + dz * dz > 1.5f * 1.5f) continue;

                float dy = up ? p.y - me.y : me.y - p.y;
                if (dy < 0.25f) continue;

                if (dy < bestDy)
                {
                    bestDy = dy;
                    best = other;
                }
            }
            return best;
        }

        // ------------------------------------------------------------ ropes

        /// <summary>Point on a neighbouring sail: its studding boom if shown, otherwise its main yard end.</summary>
        private Transform BoomTipOf(Sail other, int s, bool inner)
        {
            StuddingRig r = Of(other);
            if (r != null && r.Visible && r.root != null && r.root.activeInHierarchy && r.sides[s] != null)
                return inner ? r.sides[s].SheetInner : r.sides[s].BoomTip;
            return StuddingGeom.GetTip(other, s);
        }

        private void UpdateRopes()
        {
            if (!Studding.ShowRopes.Value)
            {
                KillAllRopes();
                return;
            }

            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                string k = s.ToString();

                if (panelsShown)
                {
                    // Head corners to their own boom: inner above the main yard end, outer to the boom end
                    EnsureRope("lashIn" + k, sd.TopIn, sd.BoomInner, true, 0.97f, true, false, 0.7f);
                    EnsureRope("lashOut" + k, sd.TopOut, sd.BoomTip, true, 0.97f, true, false, 0.7f);
                }
                else
                {
                    EnsureRope("lashIn" + k, null, null, true, 0f, false, false, 0f);
                    EnsureRope("lashOut" + k, null, null, true, 0f, false, false, 0f);
                }

                // Lower corners sheeted to the boom below (the lowest triangle has no sheet)
                if (panelsShown && !triangle && below != null)
                {
                    EnsureRope("sheetIn" + k, sd.BotIn, BoomTipOf(below, s, true), true, 0.97f, true, false, 0.8f); // no block on the yard: vanilla already has one
                    EnsureRope("sheetOut" + k, sd.BotOut, BoomTipOf(below, s, false), true, 0.97f, true, false, 0.8f); // no block on the yard: vanilla already has one
                }
                else
                {
                    EnsureRope("sheetIn" + k, null, null, true, 0f, false, false, 0f);
                    EnsureRope("sheetOut" + k, null, null, true, 0f, false, false, 0f);
                }

                // The halyard always holds the boom: to the studding boom (or main yard) end above
                // for the topmost studding sails, to the block at the masthead
                Transform up = above != null ? BoomTipOf(above, s, false) : MastTopBlock();
                EnsureRope("halyard" + k, sd.BoomTip, up, false, 0.95f, false, false, 0.8f);
            }
        }

        /// <summary>
        /// A block on the same mast above the boom: among the mast attachment points (mastReefAtt, extensions, midRopeAtt)
        /// take those on the mast axis (within 1.5 m in plan) and above the boom end, the highest one.
        /// Some midRopeAtt points are on deck; without this check the rope went down or to another mast.
        /// </summary>
        private Transform MastTopBlock()
        {
            Mast mast = sail.transform.parent != null ? sail.transform.parent.GetComponent<Mast>() : null;
            GameObject shipGo = Studding.ShipOf(sail);
            if (mast == null || shipGo == null || sides[1] == null) return null;

            Transform ship = shipGo.transform;
            Vector3 axis = ship.InverseTransformPoint(mast.transform.position);
            float tipY = ship.InverseTransformPoint(sides[1].BoomTip.position).y;

            Transform best = null;
            float bestY = tipY + 0.5f;
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

        private void EnsureRope(string key, Transform from, Transform target, bool sheet, float total,
                                bool blockFrom, bool blockTo, float widthMul)
        {
            RopeLink l;
            ropes.TryGetValue(key, out l);

            if (from == null || target == null)
            {
                if (l != null)
                {
                    KillRope(l);
                    ropes.Remove(key);
                }
                return;
            }

            if (l != null && l.Rope != null && l.Holder != null && l.From == from && l.Target == target)
            {
                l.Rope.currentRopeLength = 0f;
                return;
            }

            if (l != null) KillRope(l);
            ropes[key] = CreateRope(from, target, sheet, total, blockFrom, blockTo, widthMul);
        }

        private RopeEffect FindRopeTemplate()
        {
            var sc = sail.GetComponent<SailConnections>();
            if (sc != null && sc.angleControllerLeft != null)
            {
                var r = sc.angleControllerLeft.GetComponentInChildren<RopeEffect>();
                if (r != null) return r;
            }
            return null;
        }

        private RopeLink CreateRope(Transform from, Transform target, bool sheet, float total,
                                    bool blockFrom, bool blockTo, float widthMul)
        {
            int layer = sail.gameObject.layer;

            // Both ends are dedicated empties: RopeEffect rotates them (LookAt), blocks rotate with them
            Transform holder = NewChild("StuddingRopeEnd", target);
            holder.localPosition = Vector3.zero;

            var go = new GameObject("StuddingRope");
            go.layer = layer;
            go.transform.SetParent(from, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.SetActive(false);

            RopeEffect template = FindRopeTemplate();

            var lr = go.AddComponent<LineRenderer>();
            if (template != null)
            {
                var srcLr = template.GetComponent<LineRenderer>();
                if (srcLr != null)
                {
                    lr.sharedMaterial = srcLr.sharedMaterial;
                    lr.shadowCastingMode = srcLr.shadowCastingMode;
                    lr.receiveShadows = srcLr.receiveShadows;
                }
            }

            var rope = go.AddComponent<RopeEffect>();
            rope.attachment = holder;
            rope.sheet = sheet;
            rope.ropeWidth = (template != null ? template.ropeWidth : 0.038f) * widthMul;
            rope.totalRopeLength = total;
            rope.currentRopeLength = 0f;
            rope.ReceiveSailArea(Area * 0.5f);

            if (blockFrom) AddBlock(go.transform);
            if (blockTo) AddBlock(holder);

            go.SetActive(true);

            var l = new RopeLink();
            l.Go = go;
            l.Rope = rope;
            l.Holder = holder;
            l.From = from;
            l.Target = target;
            return l;
        }

        /// <summary>Block: a short thick barrel oriented along the rope (forward of the rope end).</summary>
        private void AddBlock(Transform end)
        {
            Transform b = NewChild("StuddingBlock", end);
            b.localPosition = new Vector3(0f, 0f, 0.07f);
            b.localRotation = Quaternion.Euler(0f, -90f, 0f); // mesh X axis -> along the rope
            b.localScale = new Vector3(0.14f, 0.045f, 0.035f);
            b.gameObject.AddComponent<MeshFilter>().sharedMesh = StuddingMeshes.Boom();
            var mr = b.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterials = woodMats;
            mr.shadowCastingMode = ShadowCastingMode.On;
        }

        private static void KillRope(RopeLink l)
        {
            // RopeEffect.OnDestroy removes its ClothRope, so ropes are destroyed rather than hidden
            if (l.Go != null) UnityEngine.Object.Destroy(l.Go);
            if (l.Holder != null) UnityEngine.Object.Destroy(l.Holder.gameObject);
        }

        private void KillAllRopes()
        {
            foreach (var l in ropes.Values) KillRope(l);
            ropes.Clear();
        }
    }
}
