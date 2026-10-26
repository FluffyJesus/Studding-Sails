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
        public float TopOffset; // height of the top of the yard above its axis (flattened yards are lower)
        public Vector3 YardScale; // yard lossy scale at measure time (ShipyardExpansion rescales yards)
        public float EndUp, EndN; // half-height and half-depth of the yard at its ends (flattened yards are lower)
        public Color Tone = new Color(0.30f, 0.22f, 0.17f); // average colour of the yard as textured
        public Vector2[] EndUv;   // the yard's texture coordinates around its end, by angle (StuddingMeshes.SparSeg bins)
        public Vector2[] EndUv2;  // the same for its second UV set (the paint/detail maps may use it), or null

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
            try { return sail.GetScaleY() * 7.31f + sail.GetScaleZ() * 3.17f + sail.GetScaledHeight() * 1.13f; }
            catch (Exception) { return 0f; }
        }

        internal static YardInfo Get(Sail sail)
        {
            float key = KeyOf(sail);
            YardInfo info;
            if (cache.TryGetValue(sail, out info))
            {
                if (info.Valid && info.Yard != null && Mathf.Abs(info.Key - key) < 0.0005f &&
                    (info.Yard.lossyScale - info.YardScale).sqrMagnitude < 1e-6f) return info;
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

        /// <summary>Average colour of the yard: its texture sampled at its mesh UVs, times the material colour.</summary>
        /// <summary>Main texture set through the renderer's property block, if any (for the log).</summary>
        /// <summary>A texture the yard is drawn with: property block main texture, main texture, detail albedo.</summary>
        internal static Texture AnyTexture(Material m, Renderer r)
        {
            try
            {
                if (r != null)
                {
                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);
                    if (!mpb.isEmpty)
                    {
                        Texture bt = mpb.GetTexture("_MainTex");
                        if (bt != null) return bt;
                    }
                }
                if (m == null) return null;
                if (m.mainTexture != null) return m.mainTexture;
                if (m.HasProperty("_DetailAlbedoMap") && m.GetTexture("_DetailAlbedoMap") != null) return m.GetTexture("_DetailAlbedoMap");
            }
            catch (Exception) { }
            return null;
        }

        private static readonly Dictionary<Texture, Vector2> dominantUv = new Dictionary<Texture, Vector2>();

        /// <summary>
        /// Texture coordinates of the most common colour of a texture (a plain paint filling most of an atlas).
        /// The texture need not be readable: it is drawn into a small render texture and read back from there,
        /// once per texture. The point taken is the one deepest inside a patch of that colour.
        /// </summary>
        internal static bool DominantUv(Texture tex, out Vector2 uv)
        {
            uv = new Vector2(0.5f, 0.5f);
            if (tex == null) return false;
            if (dominantUv.TryGetValue(tex, out uv)) return true;

            const int n = 64;
            RenderTexture rt = null;
            RenderTexture prev = RenderTexture.active;
            Color32[] px;
            try
            {
                rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var t2 = new Texture2D(n, n, TextureFormat.RGBA32, false);
                t2.ReadPixels(new Rect(0, 0, n, n), 0, 0);
                t2.Apply();
                px = t2.GetPixels32();
                UnityEngine.Object.Destroy(t2);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }

            // most common colour, 4 bits per channel
            var keys = new int[px.Length];
            var hist = new int[4096];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                keys[i] = ((c.r >> 4) << 8) | ((c.g >> 4) << 4) | (c.b >> 4);
                hist[keys[i]]++;
            }
            int best = 0;
            for (int k = 1; k < hist.Length; k++) if (hist[k] > hist[best]) best = k;

            // the pixel of that colour with the most same-coloured pixels around it (3 px radius)
            int bestI = -1, bestN = -1;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (keys[y * n + x] != best) continue;
                    int cnt = 0;
                    for (int dy = -3; dy <= 3; dy++)
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                            if (keys[yy * n + xx] == best) cnt++;
                        }
                    if (cnt > bestN) { bestN = cnt; bestI = y * n + x; }
                }
            if (bestI < 0) return false;

            uv = new Vector2((bestI % n + 0.5f) / n, (bestI / n + 0.5f) / n);
            dominantUv[tex] = uv;
            return true;
        }

        internal static string BlockTex(Renderer r)
        {
            try
            {
                if (r == null) return "none";
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                if (mpb.isEmpty) return "none";
                Texture t = mpb.GetTexture("_MainTex");
                return t != null ? t.name : "none";
            }
            catch (Exception) { return "?"; }
        }

        private static Color YardTone(Renderer r, Mesh mesh, Color fallback)
        {
            try
            {
                Material m = r != null ? r.sharedMaterial : null;
                if (m == null) return fallback;
                Color tint = m.HasProperty("_Color") ? m.color : Color.white;
                var tex = m.mainTexture as Texture2D;
                Vector2[] uv = mesh.isReadable ? mesh.uv : null;
                if (tex == null || !tex.isReadable || uv == null || uv.Length == 0) return fallback;

                Vector2 sc = m.mainTextureScale, of = m.mainTextureOffset;
                Color sum = Color.clear;
                int n = 0, step = Mathf.Max(1, uv.Length / 400);
                for (int i = 0; i < uv.Length; i += step)
                {
                    Vector2 u = new Vector2(uv[i].x * sc.x + of.x, uv[i].y * sc.y + of.y);
                    sum += tex.GetPixelBilinear(u.x, u.y);
                    n++;
                }
                if (n == 0) return fallback;
                Color c = sum / n;
                return new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b, 1f);
            }
            catch (Exception)
            {
                return fallback;
            }
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
                float endUp = -1f, endN = -1f;
                Vector2[] endUv = null, endUv2 = null;
                Color tone = info.Tone;

                // fallback for the top of the yard: half of the bounds extent along "up" (non-long axes)
                float topOffset = 0f;
                for (int a2 = 0; a2 < 3; a2++)
                {
                    if (a2 == axis) continue;
                    Vector3 av = a2 == 0 ? Vector3.right : (a2 == 1 ? Vector3.up : Vector3.forward);
                    Vector3 v3 = st.InverseTransformVector(yard.TransformVector(av * b.extents[a2]));
                    topOffset += Mathf.Abs(Vector3.Dot(v3, up));
                }

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

                            float top = 0f;
                            for (int i = 0; i < perps.Count; i++) top = Mathf.Max(top, Vector3.Dot(perps[i] - off, up));
                            topOffset = top;

                            // cross-section at the very ends (the yardarms): our yardarm extensions continue it
                            float eu = 0f, en = 0f;
                            int ec = 0;
                            Vector3 nrm = Vector3.Cross(along, up).normalized;
                            Vector2[] yuv = null;
                            Vector3[] ynm = null;
                            try { yuv = mesh.uv; ynm = mesh.normals; } catch (Exception) { }
                            if (yuv != null && yuv.Length != yv.Length) yuv = null;
                            if (ynm != null && ynm.Length != yv.Length) ynm = null;
                            int seg = StuddingMeshes.SparSeg;
                            // per angle bin, all side UVs; the bin takes the average of its biggest cluster (a
                            // plain average is right in tone but at a UV seam lands between the two islands,
                            // on some unrelated, lighter part of the texture)
                            var uvBins = new List<Vector2>[seg];
                            var uv2Bins = new List<Vector2>[seg];
                            for (int q = 0; q < seg; q++) { uvBins[q] = new List<Vector2>(); uv2Bins[q] = new List<Vector2>(); }
                            Vector2[] yuv2 = null;
                            try { yuv2 = mesh.uv2; } catch (Exception) { }
                            if (yuv2 != null && yuv2.Length != yv.Length) yuv2 = null;
                            for (int i = 0; i < yv.Length; i++)
                            {
                                Vector3 l = st.InverseTransformPoint(yard.TransformPoint(yv[i])) - center;
                                float a = Vector3.Dot(l, along);
                                if (Mathf.Abs(a) < half * 0.9f) continue;
                                Vector3 perp = l - along * a; // centre already moved onto the axis
                                float pu = Vector3.Dot(perp, up), pn = Vector3.Dot(perp, nrm);
                                eu = Mathf.Max(eu, Mathf.Abs(pu));
                                en = Mathf.Max(en, Mathf.Abs(pn));
                                ec++;
                                if (yuv != null && Mathf.Abs(a) > half * 0.75f && Mathf.Abs(a) < half * 0.97f)
                                {
                                    // side faces only: end caps carry end-grain UVs
                                    if (ynm != null)
                                    {
                                        Vector3 nw = st.InverseTransformDirection(yard.TransformDirection(ynm[i])).normalized;
                                        if (Mathf.Abs(Vector3.Dot(nw, along)) > 0.5f) continue;
                                    }
                                    // angle as our spar mesh measures it: cos -> up, sin -> canvas normal
                                    float ang = Mathf.Atan2(pn, pu);
                                    if (ang < 0f) ang += Mathf.PI * 2f;
                                    int bin = Mathf.RoundToInt(ang / (Mathf.PI * 2f) * seg) % seg;
                                    uvBins[bin].Add(yuv[i]);
                                    if (yuv2 != null) uv2Bins[bin].Add(yuv2[i]);
                                }
                            }
                            if (ec >= 4 && eu > 0.005f && en > 0.005f) { endUp = eu; endN = en; }
                            // One texture coordinate for the whole spar: the average of all the yard's side UVs at
                            // its end. Averaging gave the right tone; averaging per angle striped the spars, because
                            // at a UV seam the two sides of the seam averaged to some unrelated part of the texture.
                            endUv = StuddingMeshes.UniformRing(uvBins);
                            if (yuv2 != null) endUv2 = StuddingMeshes.UniformRing(uv2Bins);
                        }
                    }
                }
                catch (Exception)
                {
                    // mesh not readable: keep the bounds centre
                }

                // the yard's colour as it is textured: its texture averaged at its own UVs
                // (a shared atlas looks patchy on our spars, so they get their own wood in this tone)
                tone = YardTone(yardRend, mesh, tone);

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
                info.TopOffset = topOffset > 0.005f ? Mathf.Clamp(topOffset, 0.005f, 0.2f) : info.Radius;
                info.YardScale = yard.lossyScale;
                info.EndUp = endUp > 0f ? Mathf.Clamp(endUp, 0.01f, 0.2f) : info.TopOffset;
                info.EndN = endN > 0f ? Mathf.Clamp(endN, 0.01f, 0.2f) : info.Radius;
                info.Tone = tone;
                info.EndUv = endUv;
                info.EndUv2 = endUv2;
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

                // which renderer is taken as the yard and what our spars copy from it (once per sail and size)
                {
                    Material ym = yardRend != null ? yardRend.sharedMaterial : null;
                    Texture yt = ym != null ? ym.mainTexture : null;
                    Plugin.Log.LogInfo("[Studding] " + sail.name + ": yard '" + yard.name + "' mesh '" + mesh.name +
                        "' material '" + (ym != null ? ym.name : "none") + "' texture '" + (yt != null ? yt.name : "none") +
                        "' readable=" + (yt is Texture2D && ((Texture2D)yt).isReadable) + ", mesh readable=" + mesh.isReadable +
                        ", ring UVs " + (endUv != null ? "found" : "not found") + (endUv2 != null ? " (+uv2)" : "") +
                        ", keywords [" + (ym != null ? string.Join(",", ym.shaderKeywords) : "") + "]" +
                        ", block texture '" + BlockTex(yardRend) + "', detail '" +
                        (ym != null && ym.HasProperty("_DetailAlbedoMap") && ym.GetTexture("_DetailAlbedoMap") != null ? ym.GetTexture("_DetailAlbedoMap").name : "none") +
                        "', tone " + tone);
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
        internal const int SparSeg = 10;

        /// <summary>Average of the largest group of points lying within 0.06 of each other.</summary>
        /// <summary>All the points of all bins averaged, the same value for every bin. Null if there are none.</summary>
        internal static Vector2[] UniformRing(List<Vector2>[] bins)
        {
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (var b in bins) foreach (var p in b) { sum += p; n++; }
            if (n == 0) return null;
            var r = new Vector2[bins.Length];
            for (int i = 0; i < r.Length; i++) r[i] = sum / n;
            return r;
        }

        internal static bool MainCluster(List<Vector2> pts, out Vector2 centre)
        {
            centre = Vector2.zero;
            if (pts == null || pts.Count == 0) return false;
            const float r2 = 0.06f * 0.06f;
            int bestN = -1;
            Vector2 bestC = pts[0];
            for (int i = 0; i < pts.Count; i++)
            {
                int n = 0;
                Vector2 sum = Vector2.zero;
                for (int j = 0; j < pts.Count; j++)
                    if ((pts[j] - pts[i]).sqrMagnitude <= r2) { n++; sum += pts[j]; }
                if (n > bestN) { bestN = n; bestC = sum / n; }
            }
            centre = bestC;
            return true;
        }

        /// <summary>Average texture coordinates per angle bin; empty bins take the nearest filled one. Null if none.</summary>
        internal static Vector2[] FillRing(Vector2[] sum, int[] cnt)
        {
            int n = sum.Length, filled = 0;
            var r = new Vector2[n];
            var ok = new bool[n];
            for (int i = 0; i < n; i++) if (cnt[i] > 0) { r[i] = sum[i] / cnt[i]; ok[i] = true; filled++; }
            if (filled == 0) return null;
            for (int i = 0; i < n; i++)
            {
                if (ok[i]) continue;
                for (int d = 1; d < n; d++)
                {
                    int a = (i + d) % n, b = (i - d + n) % n;
                    if (ok[a]) { r[i] = r[a]; break; }
                    if (ok[b]) { r[i] = r[b]; break; }
                }
            }
            return r;
        }

        /// <summary>
        /// Spar textured like a given yard: every ring takes the yard's own texture coordinates at the same
        /// angle around it (measured at the yard's end), so the spar shows exactly the yard's wood.
        /// </summary>
        internal static Mesh Spar(Vector2[] ringUv, Vector2[] ringUv2 = null)
        {
            if (ringUv == null || ringUv.Length != SparSeg) return Boom();
            Mesh m = BuildSpar(ringUv);
            if (ringUv2 != null && ringUv2.Length == SparSeg)
            {
                // second UV set, same layout as the first: every vertex takes its angle bin's value
                var uv = new List<Vector2>();
                m.GetUVs(0, uv);
                var uv2 = new List<Vector2>(uv.Count);
                for (int i = 0; i < uv.Count; i++)
                {
                    int bin = 0;
                    float bd = float.MaxValue;
                    for (int q = 0; q < SparSeg; q++)
                    {
                        float d = (ringUv[q] - uv[i]).sqrMagnitude;
                        if (d < bd) { bd = d; bin = q; }
                    }
                    uv2.Add(ringUv2[bin]);
                }
                m.SetUVs(1, uv2);
            }
            return m;
        }

        internal static Mesh Boom()
        {
            if (boom != null) return boom;
            boom = BuildSpar(null);
            return boom;
        }

        private static Mesh BuildSpar(Vector2[] ringUv)
        {
            const int seg = SparSeg, rings = 6;
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
                    uv.Add(ringUv != null ? ringUv[j % seg] : new Vector2(k, j / (float)seg));
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
                Vector2 mid = new Vector2(end, 0.5f);
                if (ringUv != null) { mid = Vector2.zero; foreach (var q in ringUv) mid += q; mid /= seg; }
                v.Add(new Vector3(x, 0f, 0f)); uv.Add(mid);
                int ring0 = v.Count;
                for (int j = 0; j <= seg; j++)
                {
                    float a = j / (float)seg * Mathf.PI * 2f;
                    v.Add(new Vector3(x, r * Mathf.Cos(a), r * Mathf.Sin(a)));
                    uv.Add(ringUv != null ? ringUv[j % seg] : new Vector2(end, j / (float)seg));
                }
                for (int j = 0; j < seg; j++)
                {
                    if (end == 1) { t.Add(center); t.Add(ring0 + j); t.Add(ring0 + j + 1); }
                    else { t.Add(center); t.Add(ring0 + j + 1); t.Add(ring0 + j); }
                }
            }

            var mesh = new Mesh();
            mesh.name = ringUv != null ? "studding_spar" : "studding_boom";
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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
            float yTop, float yBot, bool tri, int nu, int nv, float bulge, float belly = 0f, float headZ = 0f)
        {
            // Slack on a free edge: at rest it is bowed into an arc longer than
            // the distance between the corners by "slack" (for an arc: length ~ chord * (1 + 8/3*(depth/chord)^2)).
            // The foot of a four-sided sail is cut as an upward arc (roach, like real sails):
            // it carries HemSlack more cloth than the chord, and sags to roughly straight under its weight.
            float hemK = tri ? 0f : Mathf.Sqrt(3f * Mathf.Max(0f, Studding.HemSlack.Value) / 8f);
            float hemDepth = (xOutBot - xInBot) * hemK;

            // Side edges bow outwards by SideSlack (the inner one towards the parent sail, the outer one outboard;
            // on the triangle the outer edge is the diagonal from the boom end down to the apex).
            float sideK = Mathf.Sqrt(3f * Mathf.Max(0f, Studding.SideSlack.Value * (tri ? Studding.TriangleSlack.Value : 1f)) / 8f);
            float sideDepth = (yTop - yBot) * sideK;
            // no slack on the inner edge of a four-sided studding sail: folding back it cut through its sail
            float innerDepth = tri ? Mathf.Min(sideDepth, Studding.PanelGap.Value * 0.8f) : 0f;

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
                    // the head hangs from the studding sail yard, off the sail plane; the foot is on the boom below
                    z += headZ * (1f - fv);
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

            public GameObject Roll;       // furled studding sail under its own yard
            public Transform Slider;      // the boom on our yard (the base for the studding sail above); moved inboard when run in
            public Transform Canvas;      // the studding sail itself: its yard, canvas and corners (does not slide)
            public Transform Head;        // everything hanging on the halyard: swings about the yardarm block
            public float Ang, AngVel;     // swing of the head, degrees (a damped pendulum driven by the wind)
            public Transform HeadYard;    // the studding sail's own small yard along its head
            public Transform HeadMid;     // middle of that yard: the halyard is made fast here
            public Transform Arm;         // yardarm extension of our yard (studding sail yards are longer)
            public Transform ArmTip;      // block at the yardarm: the halyard leads up to it
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
        private Mesh sparMesh;
        private Material[] blockMats;

        /// <summary>Blocks: the yard's shader with our wood texture, darker than the spars.</summary>
        private Material[] BlockMats()
        {
            if (blockMats != null) return blockMats;
            YardInfo info = curInfo != null ? curInfo : StuddingGeom.Get(sail);
            Material src = info != null && info.YardRenderer != null ? info.YardRenderer.sharedMaterial : null;
            Color tone = info != null ? info.Tone : new Color(0.3f, 0.22f, 0.17f);
            Material m = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
            m.name = "StuddingBlockWood";
            m.mainTexture = SparTexture(tone * 0.7f);
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_Color")) m.color = Color.white;
            if (m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", null); m.DisableKeyword("_NORMALMAP"); }
            if (m.HasProperty("_DetailAlbedoMap")) m.SetTexture("_DetailAlbedoMap", null);
            blockMats = new[] { m };
            return blockMats;
        }
        private bool woodTextured; // woodMats is the yard's own material: the spars carry the yard's UVs
        private Material clothMat;
        private Renderer parentRenderer;
        private Renderer furledRend;
        private bool useSailTexture;
        private Material srcMat;
        private Texture srcTex;
        private YardInfo curInfo;
        private bool uvFitted;
        private bool uvProvisional; // fitted on the rest-pose mesh while the sail was furled; refined once it is set
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
        private float gRetract;
        private float gHeadZ, gPivotX, gDrop;
        private readonly bool[] headStowed = new bool[2];
        private float laidArm = -1f, laidAboveWidth, laidFoot, laidAboveFoot;
        private readonly float[] sideK = new float[2];     // set fraction of each side's canvas
        private bool geomOk;

        private bool needBuild = true;
        private bool panelsShown;                           // either side shown
        private readonly bool[] sideShown = new bool[2];
        private float builtK = -1f;
        private bool builtTri;
        private float lastK = -1f, stableTime;
        private float laidWidth = -1f;

        private Sail above, below;
        private float nextNeighbourCheck;

        /// <summary>Studding sail canvas area, both sides together (used for thrust).</summary>
        internal float Area;

        /// <summary>
        /// Set area of one side (0 port, 1 starboard) for thrust: the canvas area times how far it is set.
        /// The first 30% of the winch only runs the boom out, the canvas (and so the area) starts after it.
        /// </summary>
        internal float SideArea(int s)
        {
            if (!Visible || !sideShown[s]) return 0f;
            return Area * 0.5f * sideK[s];
        }

        /// <summary>Centre of one side's studding sail (fully set shape), world space.</summary>
        internal Vector3 SideCenter(int s)
        {
            if (root == null) return sail.transform.position;
            float sign = s == 0 ? -1f : 1f;
            return root.transform.TransformPoint(new Vector3(sign * (gXInTop + gXOutTop + gXInBot + gXOutBot) * 0.25f,
                                                             (gYTop + gYBot) * 0.5f, 0f));
        }

        /// <summary>Rig shown (booms in place); the canvas may be furled.</summary>
        internal bool Visible;
        internal bool PanelsShown { get { return panelsShown; } }

        internal StuddingRig(Sail sail)
        {
            this.sail = sail;
            rigs[sail] = this;
            if (Studding.VerboseLog.Value) Plugin.Log.LogInfo("[Studding] rig created for " + Studding.Describe(sail));
        }

        // ------------------------------------------------------------ lifecycle

        /// <summary>
        /// deploy: 0 = booms run in on the yard, 0..BoomPhase = booms run out, then the canvas is lowered.
        /// </summary>
        internal void Tick(float deploy) { Tick(deploy, deploy); }

        /// <summary>Each side (0 port, 1 starboard) set by its own value.</summary>
        internal void Tick(float deployL, float deployR)
        {
            YardInfo info = StuddingGeom.Get(sail);
            if (!info.Valid)
            {
                Hide();
                return;
            }

            curInfo = info;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (root == null) { Build(info); Slow.Report("build", sw, sail.sailName); }
            if (root == null) return;

            if (Time.time >= nextNeighbourCheck)
            {
                nextNeighbourCheck = Time.time + 0.5f;
                above = FindNeighbour(true);
                below = FindNeighbour(false);

                // the studding sail above changed width (or appeared / went): our boom is its base
                float aw = above != null && Studding.IsOn(above) ? Studding.PanelWidth(above) : 0f;
                if (Mathf.Abs(aw - laidAboveWidth) > 0.01f) laidOutKey = -9999f;
                float af = above != null && Studding.IsOn(above) ? Studding.GetFootShift(above) : 0f;
                if (Mathf.Abs(af - laidAboveFoot) > 0.001f) laidOutKey = -9999f;

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

            sw = System.Diagnostics.Stopwatch.StartNew();
            TryBakeGeometry();
            Slow.Report("bake geometry", sw, sail.sailName);
            if (laidVersion != LayoutVersion || Mathf.Abs(laidOutKey - info.Key) > 0.0001f ||
                Mathf.Abs(laidWidth - Studding.GetWidth(sail)) > 0.001f ||
                Mathf.Abs(laidArm - Studding.GetArm(sail)) > 0.001f ||
                (!triangle && Mathf.Abs(laidFoot - Studding.GetFootShift(sail)) > 0.001f))
            {
                sw = System.Diagnostics.Stopwatch.StartNew();
                Layout(info);
                Slow.Report("layout", sw, sail.sailName);
            }

            sw = System.Diagnostics.Stopwatch.StartNew();
            SyncColor();
            Slow.Report("colour", sw, sail.sailName);
            tickDeploy[0] = deployL;
            tickDeploy[1] = deployR;
            for (int s = 0; s < 2; s++)
            {
                // our boom is only the base for the studding sail above (its foot is sheeted to it): it runs
                // out with that sail's winch; our own studding sail hangs from the yardarm instead
                float boom = BoomDemandFromAbove(s) * BoomPhase;
                boomDeploy[s] = boom;
                float slide = Mathf.Clamp01(boom / BoomPhase);
                tickCanvas[s] = Mathf.Clamp01((tickDeploy[s] - BoomPhase) / (1f - BoomPhase));
                if (sides[s] == null || sides[s].Slider == null) continue;
                float sign = s == 0 ? -1f : 1f;
                sides[s].Slider.localPosition = new Vector3(-sign * gRetract * (1f - slide), 0f, 0f);
            }

            bool boomNeeded = above != null && Studding.IsOn(above);
            for (int s = 0; s < 2; s++)
                if (sides[s] != null && sides[s].Boom != null && sides[s].Boom.gameObject.activeSelf != boomNeeded)
                    sides[s].Boom.gameObject.SetActive(boomNeeded);

            sw = System.Diagnostics.Stopwatch.StartNew();
            UpdatePanels(tickCanvas, tickDeploy);
            Slow.Report("panels", sw, sail.sailName);
            for (int s = 0; s < 2; s++) UpdateSwing(s);

            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                if (sd.CoeffPending) ApplyCoefficients(sd);
                if (!wasShown && sd.Cloth != null) sd.Cloth.ClearTransformMotion();
                UpdateClothWind(sd, s);
            }
            wasShown = true;

            UpdateRopes();
        }

        private const float BoomPhase = 0.3f; // first part of the winch: the boom runs out, no canvas yet
        private readonly float[] tickDeploy = new float[2], tickCanvas = new float[2], boomDeploy = new float[2];

        /// <summary>
        /// How far the four-sided studding sail above needs our boom on side s (0..1): its foot is sheeted to
        /// our boom end, so our boom runs out together with its boom.
        /// </summary>
        private float BoomDemandFromAbove(int s)
        {
            if (above == null || !Studding.IsOn(above)) return 0f;
            StuddingRig r = Of(above);
            if (r == null || !r.Visible || r.triangle || r.below != sail) return 0f;
            return Mathf.Clamp01(r.tickDeploy[s] / BoomPhase);
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
            if (woodMats != null && !woodTextured) // the yard's own material is shared, never ours to destroy
                foreach (var m in woodMats) if (m != null) UnityEngine.Object.Destroy(m);
            if (blockMats != null)
                foreach (var m in blockMats) if (m != null) UnityEngine.Object.Destroy(m);
            blockMats = null;
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

            woodMats = MakeWoodMaterials(info);
            sparMesh = info.EndUv != null && woodTextured ? StuddingMeshes.Spar(info.EndUv, info.EndUv2) : StuddingMeshes.Boom();

            parentRenderer = sail.cloth != null ? sail.cloth.GetComponent<SkinnedMeshRenderer>() : null;
            // built-in canvas until the sail material is taken (or if the sail UVs cannot be read)
            useSailTexture = Studding.UseSailTexture.Value;
            var reef0 = sail.GetComponent<ReefEffectAnimUniversal>();
            furledRend = reef0 != null ? reef0.furledSail : null;
            Color c0;
            bool h0;
            clothMat = StuddingCanvas.Make(SourceMat(out c0, out h0));
            SyncColor();

            var reef = sail.GetComponent<ReefEffectAnimUniversal>();
            Renderer furled = reef != null ? reef.furledSail : null;

            for (int s = 0; s < 2; s++)
            {
                var sd = new Side();
                string tag = s == 0 ? "L" : "R";

                // everything on this side lives under a slider: the boom runs out along the yard when set
                sd.Slider = NewChild("Slide_" + tag, root.transform);

                sd.Boom = NewChild("Boom_" + tag, sd.Slider);
                AttachWood(sd.Boom, info);

                sd.BoomTip = NewChild("BoomTip_" + tag, sd.Slider);
                sd.BoomInner = NewChild("BoomInner_" + tag, sd.Slider);
                sd.SheetInner = NewChild("SheetInner_" + tag, sd.Slider);
                // the studding sail: hoisted on its own yard by a halyard from the yardarm
                sd.Canvas = NewChild("Canvas_" + tag, root.transform);
                sd.Head = NewChild("Head_" + tag, sd.Canvas);
                sd.TopIn = NewChild("TopIn_" + tag, sd.Head);
                sd.TopOut = NewChild("TopOut_" + tag, sd.Head);
                sd.BotIn = NewChild("BotIn_" + tag, sd.Canvas);
                sd.BotOut = NewChild("BotOut_" + tag, sd.Canvas);
                sd.HeadYard = Spar("HeadYard_" + tag, sd.Head, info.YardRenderer);
                sd.HeadMid = NewChild("HeadMid_" + tag, sd.Head);

                // yardarm extension: yards carrying studding sails are longer than plain ones
                sd.Arm = Spar("YardArm_" + tag, root.transform, info.YardRenderer);
                sd.ArmTip = NewChild("ArmTip_" + tag, root.transform);

                sd.Roll = MakeRoll(furled, "Roll_" + tag, sd.Head);

                sides[s] = sd;
            }

            laidOutKey = -9999f;
        }

        /// <summary>A wooden spar (boom mesh, yard wood), placed by Layout.</summary>
        private Transform Spar(string name, Transform parent, Renderer yardRend)
        {
            Transform t = NewChild(name, parent);
            AttachWood(t, curInfo != null ? curInfo : StuddingGeom.Get(sail));
            return t;
        }

        /// <summary>
        /// Wood for a spar placed by Layout as a unit spar along X: our spar mesh, carrying the yard's texture
        /// coordinates, with the yard's own material and property block. (The yard mesh itself cannot be used:
        /// on some sails it has the yardarm cleats built in, which then turned up on every spar.)
        /// </summary>
        private void AttachWood(Transform t, YardInfo info)
        {
            Renderer yr = info != null ? info.YardRenderer : null;
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = sparMesh != null ? sparMesh : StuddingMeshes.Boom();
            var fr = t.gameObject.AddComponent<MeshRenderer>();
            fr.sharedMaterials = woodMats;
            fr.shadowCastingMode = ShadowCastingMode.On;
            fr.receiveShadows = true;
            if (woodTextured) CopyPropertyBlock(yr, fr);
        }

        /// <summary>Clone of the vanilla furled sail (furledSail), shown under the studding sail yard when it is furled.</summary>
        private GameObject MakeRoll(Renderer furled, string name, Transform parent)
        {
            if (furled == null) return null;
            Mesh mesh = StuddingGeom.MeshOf(furled.transform);
            if (mesh == null) return null;

            Transform t = NewChild(name, parent);
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = t.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterials = furled.sharedMaterials; // shared material: picks up the sail colour automatically
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            t.gameObject.SetActive(false);
            return t.gameObject;
        }

        /// <summary>
        /// Size of the furled studding sail bundle: the same for every sail, RollSize times the bundle of
        /// the parent's prefab (scaling it with the parent's height made it bulky on standard and big
        /// sails). The furled transform may already be scaled with the sail height, so that is divided out.
        /// </summary>
        private float RollHeightFactor(Renderer furled)
        {
            float size = Mathf.Clamp(Studding.RollSize.Value, 0.1f, 2f);
            try
            {
                var prefabGo = PrefabsDirectory.instance.sails[sail.prefabIndex];
                Sail prefab = prefabGo != null ? prefabGo.GetComponent<Sail>() : null;
                if (prefab == null) return size;

                var prefabReef = prefab.GetComponent<ReefEffectAnimUniversal>();
                float already = 1f;
                if (prefabReef != null && prefabReef.furledSail != null)
                {
                    Vector3 a = furled.transform.lossyScale, p = prefabReef.furledSail.transform.lossyScale;
                    // ship/sail placement scale is the same for both; compare the cross-section size
                    float pm = Mathf.Abs(p.x) + Mathf.Abs(p.y) + Mathf.Abs(p.z);
                    if (pm > 1e-4f) already = Mathf.Clamp((Mathf.Abs(a.x) + Mathf.Abs(a.y) + Mathf.Abs(a.z)) / pm, 0.2f, 3f);
                }
                return Mathf.Clamp(size / already, 0.05f, 3f);
            }
            catch (Exception)
            {
                return size;
            }
        }

        private void LayoutRoll(Side sd, float sign, float xCenter, float length, float yUnder /* spar centre */, float z)
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

            // thinner than the parent's bundle, one size for all sails (RollSize)
            float thin = 0.75f * RollHeightFactor(furled);
            Vector3 scale = new Vector3(Mathf.Abs(lossy.x) * thin, Mathf.Abs(lossy.y) * thin, Mathf.Abs(lossy.z) * thin);
            float meshLen = axis == 0 ? sz.x : axis == 1 ? sz.y : sz.z;
            float along = length / Mathf.Max(0.01f, meshLen);
            if (axis == 0) scale.x = along; else if (axis == 1) scale.y = along; else scale.z = along;

            float cross = 0f;
            for (int i = 0; i < 3; i++) if (i != axis) cross = Mathf.Max(cross, dims[i] * thin);

            // tucked under the boom, like the vanilla furled sail under its yard
            Vector3 target = new Vector3(sign * xCenter, yUnder - cross * 0.3f, z);
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
            if (clothMat == null || parentRenderer == null) return;

            Color c;
            bool hasColor;
            Material pm = SourceMat(out c, out hasColor);
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

            if (!hasColor || !clothMat.HasProperty("_Color")) return;
            if (c != clothMat.color) clothMat.color = c;
        }

        /// <summary>
        /// Wood for our spars (booms, studding sail yards, yardarm extensions, blocks): the yard's own shader
        /// with our own wood texture in the yard's colour. The yard texture itself is often a shared atlas laid
        /// out for the yard's UVs, and on our spars it came out patchy.
        /// </summary>
        private Material[] MakeWoodMaterials(YardInfo info)
        {
            Renderer yardRend = info.YardRenderer;
            Material src = yardRend != null ? yardRend.sharedMaterial : null;

            // best: the yard's own material, our spars carry the yard's texture coordinates (Spar mesh)
            // the yard's own material when our spars can look the same with it: either we carry the yard's
            // texture coordinates, or the yard has no texture at all (a plain painted colour, like the grey brig
            // yards: their mesh has nothing to copy, and our own wood came out brown)
            bool plain = src != null && src.mainTexture == null && StuddingGeom.BlockTex(yardRend) == "none";

            // No texture coordinates to copy from the yard but a texture after all (the boat's big shared atlas:
            // planking, fittings... mostly one plain colour): point the whole spar at the atlas's most common
            // colour, which is the plain paint those yards use
            if (src != null && info.EndUv == null)
            {
                Texture atlas = StuddingGeom.AnyTexture(src, yardRend);
                Vector2 at;
                if (atlas != null && StuddingGeom.DominantUv(atlas, out at))
                {
                    info.EndUv = new Vector2[StuddingMeshes.SparSeg];
                    for (int i = 0; i < info.EndUv.Length; i++) info.EndUv[i] = at;
                    info.EndUv2 = info.EndUv;
                    Plugin.Log.LogInfo("[Studding] " + sail.name + ": spars use the most common colour of '" + atlas.name + "' at uv " + at);
                }
            }
            woodTextured = src != null && (info.EndUv != null || plain);
            // shared, not copied: the yard's material is a per-boat instance (the boat paint, e.g.
            // "medi_medium_paint (Instance)"), and anything that later repaints it repaints our spars too
            if (woodTextured) return new[] { src };

            // otherwise: the yard's shader with our own wood texture in the yard's colour
            Material m = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
            m.name = "StuddingSparWood";
            m.mainTexture = SparTexture(info.Tone);
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_Color")) m.color = Color.white;
            if (m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", null); m.DisableKeyword("_NORMALMAP"); }
            if (m.HasProperty("_DetailAlbedoMap")) m.SetTexture("_DetailAlbedoMap", null);
            return new[] { m };
        }

        private static readonly Dictionary<int, Texture2D> sparTex = new Dictionary<int, Texture2D>();

        /// <summary>
        /// Spar wood: the tone with fine grain running along the spar. The spar mesh has u along its length
        /// and v around it, so the grain varies with v only.
        /// </summary>
        private static Texture2D SparTexture(Color tone)
        {
            int key = Mathf.RoundToInt(tone.r * 255f) | (Mathf.RoundToInt(tone.g * 255f) << 8) | (Mathf.RoundToInt(tone.b * 255f) << 16);
            Texture2D tex;
            if (sparTex.TryGetValue(key, out tex) && tex != null) return tex;

            const int size = 64;
            var rnd = new System.Random(11);
            var grain = new float[size];
            float phase = (float)rnd.NextDouble() * 6.28f;
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size * Mathf.PI * 2f;
                grain[y] = 0.07f * Mathf.Sin(v * 4f + phase) + 0.04f * Mathf.Sin(v * 11f + phase * 1.7f) +
                           0.035f * ((float)rnd.NextDouble() - 0.5f);
            }

            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float along = 0.02f * Mathf.Sin(x / (float)size * Mathf.PI * 2f * 2f);
                    float k = 1f + grain[y] + along + 0.015f * ((float)rnd.NextDouble() - 0.5f);
                    px[y * size + x] = new Color(tone.r * k, tone.g * k, tone.b * k, 1f);
                }

            tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "StuddingSparWood" };
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(px);
            tex.Apply(true, true);
            sparTex[key] = tex;
            return tex;
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
            return c.y + b.TopOffset;
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
            float boomY = info.TopOffset + boomRadius * 0.9f; // lies on top of the main yard (also when flattened)
            float boomZ = Studding.BoomOffsetZ.Value;

            // Our boom: the base for the studding sail ABOVE (its foot is sheeted to it), laid out as before:
            // from inside the yard end out past where that sail's foot reaches
            float footIn = info.Half + gap;
            float aIn = info.Half - overlap;
            laidAboveWidth = above != null && Studding.IsOn(above) ? Studding.PanelWidth(above) : 0f;
            laidAboveFoot = above != null && Studding.IsOn(above) ? Studding.GetFootShift(above) : 0f;
            float footW = laidAboveWidth > 0.2f ? laidAboveWidth : panelWidth; // as wide as the studding sail above
            float aOut = footIn + footW + outerMargin;

            // Our own studding sail hangs from its own small yard, hoisted by a halyard from its middle to our
            // yardarm. The yard is lengthened so that the studding sail laps over the sail by HeadOverlap of
            // its width only: yardarm = yard end + width * (0.5 - overlap)
            laidArm = Studding.GetArm(sail);
            float ext = laidArm >= 0f ? laidArm : panelWidth * (0.5f - Mathf.Clamp(Studding.HeadOverlap.Value, 0f, 0.5f));
            float xHead = info.Half + ext;                          // studding sail yard centre, under the yardarm
            float headR = Mathf.Max(0.03f, info.Radius * 0.45f);
            float headZ = Studding.HeadOffsetZ.Value;               // in front of / behind the sail, clear of it
            float yTop = -(info.Radius + Mathf.Max(0.2f, Studding.HalyardLength.Value)); // hangs on the halyard under the yard
            float xInTop = xHead - panelWidth * 0.5f;
            float xOutTop = xHead + panelWidth * 0.5f;
            gHeadZ = headZ;
            float boomLen = aOut - aIn;
            gRetract = Mathf.Max(0f, aOut - info.Half - 0.05f); // run in until the boom end reaches the yard end

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
                    // the boom below is laid out by its own rig the same way (footIn); the inner foot corner is
                    // moved along it by this sail's FootShift (negative = in, lapping over the sail below)
                    laidFoot = Studding.GetFootShift(sail);
                    float bInner = b.Half + gap + laidFoot;
                    float bOuter = b.Half + gap + panelWidth; // the boom below is laid out as wide as this studding sail
                    bInner = Mathf.Min(bInner, bOuter - 0.3f);

                    Vector3 pIn = BelowPoint(b, bInner, b.TopOffset + bBoomR * 1.9f);
                    Vector3 pOut = BelowPoint(b, bOuter, b.TopOffset + bBoomR * 1.9f);
                    xInBot = Mathf.Abs(pIn.x);
                    xOutBot = Mathf.Abs(pOut.x);
                    // the foot is sheeted down to the boom below on short ropes, clear of the sail below
                    yBot = (pIn.y + pOut.y) * 0.5f + Mathf.Max(0.1f, Studding.FootLift.Value);
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
            gDrop = Mathf.Max(0f, yTop - yBot - 0.25f); // lowered, the yard lies just above the foot
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
                // the inner sheet of the studding sail above, at its (shifted) inner foot corner
                float sheetIn = footIn + (above != null && Studding.IsOn(above) ? Studding.GetFootShift(above) : 0f);
                sd.SheetInner.localPosition = new Vector3(sign * sheetIn, boomY, boomZ);

                sd.TopIn.localPosition = new Vector3(sign * xInTop, yTop, headZ);
                sd.TopOut.localPosition = new Vector3(sign * xOutTop, yTop, headZ);

                // the studding sail's own yard along its head, the halyard from its middle
                float headLen = panelWidth + 0.15f;
                sd.HeadYard.localPosition = new Vector3(sign * xHead, yTop + headR, headZ);
                sd.HeadYard.localRotation = s == 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                sd.HeadYard.localScale = new Vector3(headLen, headR, headR);
                sd.HeadMid.localPosition = new Vector3(sign * xHead, yTop + headR * 2f, headZ);

                // yardarm extension, coaxial with the yard, from a little inside its end out to the new yardarm
                float armFrom = info.Half - 0.1f, armTo = info.Half + ext;
                bool arm = ext > 0.05f;
                sd.Arm.gameObject.SetActive(arm);
                sd.Arm.localPosition = new Vector3(sign * (armFrom + armTo) * 0.5f, 0f, 0f);
                sd.Arm.localRotation = s == 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                // the yard's own cross-section at its end (ShipyardExpansion flattens yards), tapering outwards
                sd.Arm.localScale = new Vector3(armTo - armFrom, info.EndUp, info.EndN);
                sd.ArmTip.localPosition = new Vector3(sign * (armTo - 0.08f), 0f, 0f);
                gPivotX = armTo - 0.08f;

                // furled bundle under the studding sail yard
                LayoutRoll(sd, sign, xHead, panelWidth, yTop, headZ);
            }

            if (Studding.VerboseLog.Value)
            {
                Plugin.Log.LogInfo(string.Format(
                    "[Studding] {0}: {1} (below: {2}, above: {10}), boom={3:F2} m, panel top x[{4:F2}..{5:F2}] bottom x[{6:F2}..{7:F2}] y[{8:F2}..{9:F2}]",
                    sail.name, triangle ? "triangle" : "trapezoid", below != null ? below.name : "none",
                    boomLen, xInTop, xOutTop, xInBot, xOutBot, yTop, yBot,
                    above != null ? above.name + " [" + above.category + "]" : "none"));
            }
        }

        // ------------------------------------------------------------ canvas and furling

        /// <summary>
        /// The canvas is built once per layout and skinned to two bones: the head (boom) and the foot (clews).
        /// Furling smoothly raises the foot bone, like vanilla (where an armature animation furls the sail).
        /// The apex of the lowest triangular studding sail always follows the parent sail's clew.
        /// </summary>
        private void UpdatePanels(float[] canvas, float[] deploy)
        {
            float parentSet = sail.currentUnroll < 0.04f ? 0f : Mathf.Clamp01(sail.currentUnroll);
            float kMax = 0f;
            for (int s = 0; s < 2; s++)
            {
                float c = Mathf.Clamp01(canvas[s]);
                float k;
                if (!Studding.UseWinch.Value) k = parentSet * c;
                else if (triangle) k = Mathf.Min(parentSet, c);
                else k = c;
                sideK[s] = k;
                kMax = Mathf.Max(kMax, k);
            }
            if (Mathf.Abs(kMax - lastK) > 0.002f) { lastK = kMax; stableTime = 0f; }
            else stableTime += Time.deltaTime;

            // the canvas is always full size: reefing pulls it up with bones instead of rebuilding
            if (geomOk && kMax >= 0.06f && needBuild)
            {
                needBuild = false;
                BuildPanels(1f);
            }

            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                float k = sideK[s];
                bool show = geomOk && !needBuild && k >= 0.06f;

                // before the hoist (the boom phase of the winch) the studding sail lies stowed: no yard, no
                // bundle, no halyard; after that the furled bundle hangs under its yard until the canvas opens
                bool stowed = deploy[s] < 0.28f && k <= 0f && GameState.currentShipyard == null;
                headStowed[s] = stowed;
                if (sd.Head != null && sd.Head.gameObject.activeSelf == stowed) sd.Head.gameObject.SetActive(!stowed);
                bool roll = !show && !stowed;
                if (sd.Roll != null && sd.Roll.activeSelf != roll) sd.Roll.SetActive(roll);

                if (show != sideShown[s])
                {
                    sideShown[s] = show;
                    if (sd.PanelGo != null) sd.PanelGo.SetActive(show);
                }
                if (!show || sd.BotBone == null) continue;

                // the foot stays at the boom below (the triangle at the clew); the head is hoisted (UpdateSwing).
                // The boom below belongs to another yard, braced on its own: follow where its inner sheet point
                // actually is (along and fore/aft), otherwise the foot hangs in the old plane and the sheets
                // run off forward or aft
                Vector3 target = BottomAt(s, 1f);
                if (!builtTri && below != null)
                {
                    StuddingRig br = Of(below);
                    Side bs = br != null && br.Visible ? br.sides[s] : null;
                    if (bs != null && bs.SheetInner != null && bs.Boom != null && bs.Boom.gameObject.activeInHierarchy)
                    {
                        // only along the yard: pulling the foot fore/aft into the plane of the yard below
                        // flattened the studding sail against its sail (it no longer blew out past it)
                        Vector3 p = root.transform.InverseTransformPoint(bs.SheetInner.position);
                        target = new Vector3(p.x, target.y, target.z);
                    }

                    // the foot hangs on its sheets above the boom: the wind blows it out the same way as the
                    // yard above (the head swing), as far as the sheets let it - computed, not read back from
                    // the cloth, so it costs nothing
                    float sheet = Mathf.Max(0.1f, Studding.FootLift.Value);
                    Vector3 d = Quaternion.AngleAxis(sd.Ang, Vector3.right) * Vector3.down;
                    target += new Vector3(0f, -(1f + d.y) * sheet, d.z * sheet);
                }
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
                bool settled = Mathf.Abs(k - builtK) <= 0.02f;
                bool clothOn = settled || Studding.ClothWhileReefing.Value;
                if (sd.Cloth != null && sd.Cloth.enabled != clothOn)
                {
                    sd.Cloth.enabled = clothOn;
                    if (clothOn) sd.CoeffPending = true;
                }
            }
            panelsShown = sideShown[0] || sideShown[1];
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
        private void ApplyUvToPanels()
        {
            for (int s = 0; s < 2; s++)
            {
                Side sd = sides[s];
                if (sd == null || sd.PanelMesh == null || sd.RestVerts == null) continue;
                sd.PanelMesh.uv = SailUv(sd.RestVerts, s == 0 ? -1f : 1f);
            }
        }

        /// <summary>
        /// The sail's cloth material: its own while it is set. While it is furled the game gives the canvas an
        /// empty material, so then the sail type's prefab canvas material is used (texture), tinted with the
        /// colour of the furled bundle (the sail colour).
        /// </summary>
        private Material SourceMat(out Color color, out bool hasColor)
        {
            color = Color.white;
            hasColor = false;
            Material pm = parentRenderer != null ? parentRenderer.sharedMaterial : null;
            if (pm != null && pm != clothMat && sail.currentUnroll >= 0.05f && HasTex(pm))
            {
                hasColor = pm.HasProperty("_Color");
                if (hasColor) color = pm.color;
                return pm;
            }

            // Furled: the game keeps the sail's real canvas in ReefEffectAnimUniversal.unfurledMaterial and shows a
            // placeholder. Up to 1.3.1 the studding sails took the prefab's canvas then, so a sail with another texture
            // (Shipyard Expansion's, none, Sail Emblems' paint) showed the standard one on its studding sails while
            // furled (Кэп). Take the saved canvas.
            Material saved = SavedCanvas();
            if (saved != null && saved != clothMat && HasTex(saved))
            {
                hasColor = saved.HasProperty("_Color");
                if (hasColor) color = saved.color;
                return saved;
            }

            Material prefab = PrefabCloth();
            if (furledRend != null && furledRend.sharedMaterial != null && furledRend.sharedMaterial.HasProperty("_Color"))
            {
                color = furledRend.sharedMaterial.color;
                hasColor = true;
            }
            else if (prefab != null && prefab.HasProperty("_Color"))
            {
                color = prefab.color;
                hasColor = true;
            }
            return prefab;
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<ReefEffectAnimUniversal, Material> UnfurledMaterial =
            HarmonyLib.AccessTools.FieldRefAccess<ReefEffectAnimUniversal, Material>("unfurledMaterial");

        // The canvas the game saved away while the sail is furled (null when set, or not a real canvas).
        private Material SavedCanvas()
        {
            var reef = sail != null ? sail.GetComponent<ReefEffectAnimUniversal>() : null;
            if (reef == null) return null;
            Material m = UnfurledMaterial(reef);
            Material empty = Refs.emptyMaterial;
            if (m == null || empty != null && (m == empty || m.name.StartsWith(empty.name, StringComparison.Ordinal))) return null;
            return m;
        }

        private static bool HasTex(Material m)
        {
            return m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null;
        }

        private Material prefabCloth;
        private bool prefabClothTried;

        private Material PrefabCloth()
        {
            if (prefabClothTried) return prefabCloth;
            prefabClothTried = true;
            try
            {
                var go = PrefabsDirectory.instance.sails[sail.prefabIndex];
                Sail ps = go != null ? go.GetComponent<Sail>() : null;
                var smr = ps != null && ps.cloth != null ? ps.cloth.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr != null && HasTex(smr.sharedMaterial)) prefabCloth = smr.sharedMaterial;
            }
            catch (Exception) { }
            return prefabCloth;
        }

        private void TryFitUv()
        {
            if ((uvFitted && !uvProvisional) || !useSailTexture || curInfo == null) return;
            // the sail has not been set yet (e.g. fresh from the shipyard): its rest-pose UV layout came out
            // turned across the canvas, so until it is set the studding sails wear the built-in canvas in the
            // sail's colour (see Build / SyncColor), and the real cloth layout is fitted once the sail is set
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
                uvProvisional = false;
                uvNote = string.Format("fitted on baked mesh: span={0:F2} edge={1:F2}", span, uvEdge);

                // apply the new UVs to existing canvases (no cloth rebuild)
                ApplyUvToPanels();
            }
            catch (Exception e)
            {
                uvNote = "fit error: " + e.Message;
                useSailTexture = false;
            }
        }

        /// <summary>
        /// The studding sail yard hangs on its halyard from the yardarm block: a damped pendulum swinging
        /// across the sail, blown out by the wind across the canvas (more when the canvas is set) and swinging
        /// back with inertia when it eases or the boat turns. Visual only: the boat's forces are unchanged.
        /// </summary>
        private void UpdateSwing(int s)
        {
            Side sd = sides[s];
            if (sd == null || sd.Head == null || root == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;

            // hoist: the yard runs up the halyard from just above the foot to its full height under the yardarm
            float hoist = sideShown[s] ? sideK[s] : 0f;
            float lower = gDrop * (1f - hoist);

            // wind across the canvas blows the hanging yard out: in proportion to how much canvas is set
            // (a furled bundle catches little), and never further out than the set canvas lets it
            Vector3 wind = root.transform.InverseTransformDirection(sail.apparentWind);
            // a bare yard with its bundle catches next to nothing; the canvas takes over as it opens
            float push = wind.z * sail.GetCurrentShadowMult() * (0.03f + 0.97f * hoist * hoist);
            float target = push * Mathf.Max(0f, Studding.SwingStrength.Value);

            float len = Mathf.Max(0.2f, Studding.HalyardLength.Value) + lower;
            float maxOut = 0.05f + 1.6f * hoist; // m sideways at the yard
            float maxAng = Mathf.Min(50f, Mathf.Asin(Mathf.Clamp01(maxOut / len)) * Mathf.Rad2Deg);
            target = Mathf.Clamp(target, -maxAng, maxAng) * SwingSign;
            float omega = Mathf.Sqrt(9.81f / len);
            sd.AngVel += (omega * omega * (target - sd.Ang) - 2f * 0.35f * omega * sd.AngVel) * dt;
            sd.Ang += sd.AngVel * dt;
            if (float.IsNaN(sd.Ang) || Mathf.Abs(sd.Ang) > 80f) { sd.Ang = Mathf.Clamp(target, -50f, 50f); sd.AngVel = 0f; }

            ApplySwing(sd, s == 0 ? -1f : 1f, sd.Ang, lower);
        }

        /// <summary>Head lowered by "lower" on its halyard and swung by ang degrees about the yardarm block.</summary>
        private void ApplySwing(Side sd, float sign, float ang, float lower)
        {
            Quaternion q = Quaternion.AngleAxis(ang, Vector3.right);
            Vector3 pivot = new Vector3(sign * gPivotX, 0f, 0f);
            sd.Head.localRotation = q;
            sd.Head.localPosition = pivot - q * pivot + q * new Vector3(0f, -lower, 0f);
        }

        /// <summary>+1 if a positive angle swings the hanging head towards +Z (the canvas normal).</summary>
        private static readonly float SwingSign =
            Mathf.Sign((Quaternion.AngleAxis(1f, Vector3.right) * Vector3.down).z);

        private void BuildPanels(float k)
        {
            // build with the head at rest: the bind poses are taken from the current bone positions
            for (int s = 0; s < 2; s++)
                if (sides[s] != null && sides[s].Head != null) ApplySwing(sides[s], s == 0 ? -1f : 1f, 0f, 0f);

            Area = 0f;
            panelsShown = false;
            sideShown[0] = sideShown[1] = false;
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
                    sd.BotIn.SetParent(sd.Canvas, false);
                    sd.BotOut.SetParent(sd.Canvas, false);
                    UnityEngine.Object.Destroy(sd.BotBone.gameObject);
                }
                sd.PanelGo = null;
                sd.Cloth = null;
                sd.TopBone = null;
                sd.BotBone = null;
                sd.CoeffPending = false;

                // roll visibility is decided in UpdatePanels
                if (!geomOk) continue;

                bool tri = builtTri;
                float yBot = Mathf.Lerp(gYTop, gYBot, k);
                float xInBot = Mathf.Lerp(gXInTop, gXInBot, k);
                float xOutBot = Mathf.Lerp(gXOutTop, gXOutBot, k);
                Vector3 pinIn = new Vector3(sign * xInBot, yBot, 0f);
                Vector3 pinOut = new Vector3(sign * (tri ? xInBot : xOutBot), yBot, 0f);

                // bones
                sd.TopBone = NewChild("TopBone", sd.Head);
                sd.TopBone.localPosition = new Vector3(0f, gYTop, 0f);
                sd.BotBone = NewChild("BotBone", sd.Canvas);
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
                go.transform.SetParent(sd.Canvas, false);
                go.SetActive(false);

                float height = gYTop - yBot;
                // a sine arc is longer than its chord by (pi^2/4)*(depth/chord)^2 -> depth for the requested slack
                // the triangle gets the same slack as the four-sided ones (it has no foot, so no roach)
                // the triangle gets less (it rolled up untidily in a cross wind): TriangleSlack of the slack
                float triK = tri ? Studding.TriangleSlack.Value : 1f;
                float bulge = height * (2f / Mathf.PI) * Mathf.Sqrt(Mathf.Max(0f, Studding.Fullness.Value * triK));
                float belly = gWidth * (2f / Mathf.PI) * Mathf.Sqrt(Mathf.Max(0f, Studding.Belly.Value * triK));
                // grid density like vanilla sails (~0.6 m per cell): on a finer grid
                // Unity cloth is noticeably stiffer and barely moves
                float cell = Mathf.Max(0.2f, Studding.ClothCell.Value);
                int nu = Mathf.Clamp(Mathf.RoundToInt(gWidth / cell), 3, 12);
                int nvFull = Mathf.Clamp(Mathf.RoundToInt((gYTop - gYBot) / cell), 4, 20);
                int nv = Mathf.Max(3, Mathf.RoundToInt(nvFull * Mathf.Clamp01(k + 0.15f)));

                sd.PanelMesh = new Mesh();
                sd.PanelMesh.name = "studding_panel";
                Area += StuddingMeshes.BuildGrid(sd.PanelMesh, sign, gXInTop, gXOutTop, xInBot, xOutBot,
                    gYTop, yBot, tri, nu, nv, bulge, belly, gHeadZ);

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
        private void UpdateClothWind(Side sd, int s)
        {
            if (sd.Cloth == null) return;

            // Same formula as vanilla WindCloth (staticMultiplier 2 on these sails), but with our own set
            // fraction instead of the parent's, so a studding sail set under a furled sail still fills.
            Cloth pc = sail.cloth;
            Vector3 rnd = pc != null ? pc.randomAcceleration : Vector3.zero;
            Vector3 ext = sail.apparentWind * 2f * sideK[s] * sail.GetCurrentShadowMult();

            // vanilla flutter comes from WindCloth: damping follows the captured wind fraction every frame;
            // while the parent is furled its damping is pinned to max, so use a neutral value then
            if (pc != null) sd.Cloth.damping = sail.currentUnroll >= 0.1f ? pc.damping : 0.5f;

            float mul = Studding.ClothWind.Value;
            sd.Cloth.externalAcceleration = ext * mul;
            // optional random jitter on top of the vanilla wind
            float flutter = Studding.Flutter.Value;
            Vector3 e = ext * mul;
            sd.Cloth.randomAcceleration = rnd * mul + new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)) * flutter;
        }

        // ------------------------------------------------------------ neighbours in the stack

        private sealed class SailList { public float Until; public Sail[] Sails; }
        private static readonly Dictionary<GameObject, SailList> shipSails = new Dictionary<GameObject, SailList>();

        /// <summary>
        /// The boat's sails, shared by all rigs and refreshed at most every 2 s (at the shipyard every 0.5 s,
        /// sails come and go there): one search per boat instead of one per rig every half second.
        /// </summary>
        private static Sail[] ShipSails(GameObject ship)
        {
            SailList l;
            if (!shipSails.TryGetValue(ship, out l) || l == null)
            {
                l = new SailList();
                shipSails[ship] = l;
            }
            if (l.Sails == null || Time.time >= l.Until)
            {
                l.Sails = ship.GetComponentsInChildren<Sail>(false);
                l.Until = Time.time + (GameState.currentShipyard != null ? 0.5f : 2f);
            }
            return l.Sails;
        }

        /// <summary>Nearest square sail above/below in the same stack (mast + topmast).</summary>
        private Sail FindNeighbour(bool up)
        {
            GameObject shipGo = Studding.ShipOf(sail);
            if (shipGo == null) return null;

            Transform ship = shipGo.transform;
            Vector3 me = ship.InverseTransformPoint(sail.transform.position);
            Mast myMast = sail.transform.parent != null ? sail.transform.parent.GetComponent<Mast>() : null;

            Sail best = null;
            float bestDy = float.MaxValue;

            foreach (var other in ShipSails(shipGo))
            {
                if (other == null || other == sail || !other.gameObject.activeInHierarchy) continue;
                if (!Studding.IsSquare(other) || Plugin.IsExcludedSail(other)) continue;

                // the same mast or one stacked on it (topmast), not a separate mast standing close by
                Mast om = other.transform.parent != null ? other.transform.parent.GetComponent<Mast>() : null;
                if (myMast != null && om != null && !StuddingWinch.SameColumn(myMast, om)) continue;

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

                // Halyard: from the middle of the studding sail yard up to the block at our yardarm
                if (!headStowed[s]) EnsureRope("halyard" + k, sd.HeadMid, sd.ArmTip, false, 0.95f, true, true, 0.8f);
                else EnsureRope("halyard" + k, null, null, false, 0f, false, false, 0f);

                // Lower corners sheeted to the boom below (the lowest triangle is tacked to the clew)
                if (sideShown[s] && !triangle && below != null)
                {
                    EnsureRope("sheetIn" + k, sd.BotIn, BoomTipOf(below, s, true), true, 0.97f, true, false, 0.8f); // no block on the yard: vanilla already has one
                    EnsureRope("sheetOut" + k, sd.BotOut, BoomTipOf(below, s, false), true, 0.97f, true, false, 0.8f); // no block on the yard: vanilla already has one
                }
                else
                {
                    EnsureRope("sheetIn" + k, null, null, true, 0f, false, false, 0f);
                    EnsureRope("sheetOut" + k, null, null, true, 0f, false, false, 0f);
                }
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
            mr.sharedMaterials = BlockMats();
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