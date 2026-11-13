using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace PaperPals
{
    internal enum Pose { Idle = 0, StepA = 1, StepB = 2, Crouch = 3, CrouchA = 4, CrouchB = 5, RunA = 6, RunB = 7 }

    /// One PaperPal's frames: index = (view * 8 + pose) * 2 + mouth. Views: front, back, left, right.
    internal sealed class SpriteSet
    {
        public static readonly string[] Views = { "front", "back", "left", "right" };
        public static readonly string[] Poses = { "idle", "step-a", "step-b", "crouch", "crouch-a", "crouch-b", "run-a", "run-b" };
        public static readonly string[] Mouths = { "quiet", "talking" };
        public static int Index(int view, Pose pose, int mouth) => (view * 8 + (int)pose) * 2 + mouth;

        private readonly Texture2D[] textures = new Texture2D[64];
        private Material[] materials;
        private bool materialsFailed;
        public bool Ok, HasCrouch, HasSneak, HasRun;
        public float Aspect = 0.52f; // width / height of the (padded) canvas
        public Mesh Quad;
        public int Loaded;

        /// Frames are looked up under the folder of this pal's npc.json only, skipping sub-folders that hold
        /// their own npc.json, so mod-manager flattening can't mix two pals.
        public static SpriteSet Load(NpcDefinition def)
        {
            var set = new SpriteSet();
            var files = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            CollectPngs(def.Dir, def.Dir, files);

            bool Has(Pose p) => Views.All(v => Mouths.All(m => files.ContainsKey($"{v}_{Poses[(int)p]}_{m}.png")));
            bool required = Has(Pose.Idle) && Has(Pose.StepA) && Has(Pose.StepB);
            if (!required)
            {
                var missing = Views.SelectMany(v => new[] { Pose.Idle, Pose.StepA, Pose.StepB }.SelectMany(p =>
                    Mouths.Select(m => $"{v}_{Poses[(int)p]}_{m}.png"))).Where(n => !files.ContainsKey(n)).Take(6);
                Plugin.Log.LogError($"{def.Id}: missing required frames ({string.Join(", ", missing)}...) under {def.Dir}. Not loaded.");
                return set;
            }
            set.HasCrouch = Has(Pose.Crouch);
            set.HasSneak = Has(Pose.CrouchA) && Has(Pose.CrouchB);
            set.HasRun = Has(Pose.RunA) && Has(Pose.RunB);

            int canvasW = 0, canvasH = 0;
            for (int v = 0; v < 4; v++)
            for (int p = 0; p < 8; p++)
            {
                var pose = (Pose)p;
                if ((pose == Pose.Crouch && !set.HasCrouch) || ((pose == Pose.CrouchA || pose == Pose.CrouchB) && !set.HasSneak)
                    || ((pose == Pose.RunA || pose == Pose.RunB) && !set.HasRun)) continue;
                for (int m = 0; m < 2; m++)
                {
                    string name = $"{Views[v]}_{Poses[p]}_{Mouths[m]}.png";
                    var tex = LoadFrame(files[name], def.Id, name, ref canvasW, ref canvasH);
                    if (tex == null) continue;
                    set.textures[Index(v, pose, m)] = tex;
                    set.Loaded++;
                }
            }
            if (set.textures[Index(0, Pose.Idle, 0)] == null) { Plugin.Log.LogError($"{def.Id}: front_idle_quiet.png failed to load."); return set; }
            set.Aspect = (float)set.textures[Index(0, Pose.Idle, 0)].width / set.textures[Index(0, Pose.Idle, 0)].height;
            set.Quad = BuildQuad(set.Aspect);
            set.Ok = true;
            Plugin.Log.LogInfo($"{def.Id}: {set.Loaded} frames ({canvasW}x{canvasH}); crouch={set.HasCrouch} sneak={set.HasSneak} run={set.HasRun}.");
            return set;
        }

        private static void CollectPngs(string root, string dir, Dictionary<string, string> files)
        {
            if (dir != root && File.Exists(Path.Combine(dir, "npc.json"))) return; // another pal's folder
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                string n = Path.GetFileName(f);
                if (!files.ContainsKey(n)) files[n] = f;
            }
            foreach (var d in Directory.GetDirectories(dir)) CollectPngs(root, d, files);
        }

        /// Loads a PNG, pads it to a multiple of 4 (keeping the feet bottom-centre) so it can be DXT-compressed.
        private static Texture2D LoadFrame(string path, string id, string name, ref int canvasW, ref int canvasH)
        {
            var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!raw.LoadImage(File.ReadAllBytes(path))) { Plugin.Log.LogError($"{id}: bad PNG {path}"); Object.Destroy(raw); return null; }
            int w = raw.width, h = raw.height;
            if (canvasW == 0) { canvasW = w; canvasH = h; }
            else if (w != canvasW || h != canvasH)
                Plugin.Log.LogWarning($"{id}: {name} is {w}x{h}, other frames are {canvasW}x{canvasH}; all frames should share one canvas.");
            int w4 = (w + 3) / 4 * 4, h4 = (h + 3) / 4 * 4;
            var tex = new Texture2D(w4, h4, TextureFormat.RGBA32, true);
            if (w4 != w || h4 != h)
            {
                var clear = new Color32[w4 * h4];
                tex.SetPixels32(clear);
                tex.SetPixels32((w4 - w) / 2, 0, w, h, raw.GetPixels32()); // y=0 is the bottom row: feet stay on it
            }
            else tex.SetPixels32(raw.GetPixels32());
            Object.Destroy(raw);
            tex.Apply(true);
            tex.name = $"PaperPal_{id}_{Path.GetFileNameWithoutExtension(name)}";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.Compress(true);
            tex.Apply(false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        public static Mesh BuildQuad(float aspect)
        {
            float w = aspect, h = 1f; // unit height, scaled by the AI
            var mesh = new Mesh { name = "PaperPalQuad" };
            mesh.vertices = new[] { new Vector3(-w / 2, 0, 0), new Vector3(w / 2, 0, 0), new Vector3(-w / 2, h, 0), new Vector3(w / 2, h, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 3, 0, 3, 1 };
            mesh.normals = Enumerable.Repeat(new Vector3(0, 0, -1), 4).ToArray();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.HideAndDontSave;
            return mesh;
        }

        /// Material for a frame; falls back to idle when a pose frame is missing.
        public Material Get(int view, Pose pose, int mouth)
        {
            if (materials == null && !materialsFailed) BuildMaterials();
            if (materials == null) return null;
            return materials[Index(view, pose, mouth)] ?? materials[Index(view, Pose.Idle, mouth)];
        }

        private static readonly string[] ExtraKeywords =
        {
            "_SURFACE_TYPE_TRANSPARENT", "_NORMALMAP", "_NORMALMAP_TANGENT_SPACE", "_MASKMAP", "_EMISSIVE_COLOR_MAP",
            "_DETAIL_MAP", "_HEIGHTMAP", "_BENTNORMALMAP", "_SPECULARCOLORMAP", "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING",
            "_MATERIAL_FEATURE_TRANSMISSION", "_MATERIAL_FEATURE_IRIDESCENCE", "_PIXEL_DISPLACEMENT", "_VERTEX_DISPLACEMENT"
        };
        private static Material template;
        private static bool templateSearched;

        private void BuildMaterials()
        {
            try
            {
                if (!templateSearched)
                {
                    templateSearched = true;
                    template = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m =>
                        m != null && m.shader != null && m.shader.name == "HDRP/Lit" && m.IsKeywordEnabled("_ALPHATEST_ON")
                        && !ExtraKeywords.Any(m.IsKeywordEnabled));
                }
                Shader lit = template != null ? template.shader : Shader.Find("HDRP/Lit");
                if (lit == null) { materialsFailed = true; Plugin.Log.LogError("HDRP/Lit not found; PaperPals will be invisible."); return; }
                var built = new Material[64];
                for (int i = 0; i < 64; i++)
                {
                    if (textures[i] == null) continue;
                    var mat = template != null ? new Material(template) : new Material(lit);
                    mat.name = textures[i].name;
                    mat.SetTexture("_BaseColorMap", textures[i]);
                    mat.SetTextureScale("_BaseColorMap", Vector2.one);
                    mat.SetTextureOffset("_BaseColorMap", Vector2.zero);
                    mat.SetColor("_BaseColor", Color.white);
                    if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", Color.black);
                    if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                    if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
                    if (mat.HasProperty("_AlphaCutoff")) mat.SetFloat("_AlphaCutoff", 0.5f);
                    if (template == null)
                    {
                        HDMaterial.SetAlphaClipping(mat, true);
                        HDMaterial.SetAlphaCutoff(mat, 0.5f);
                        HDMaterial.ValidateMaterial(mat);
                    }
                    mat.hideFlags = HideFlags.HideAndDontSave;
                    built[i] = mat;
                }
                materials = built;
            }
            catch (System.Exception e) { materialsFailed = true; Plugin.Log.LogError("PaperPals materials failed: " + e); }
        }
    }
}
