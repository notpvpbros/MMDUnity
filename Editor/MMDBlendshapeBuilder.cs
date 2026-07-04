using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

// MMD Blendshape Builder v2 — full Xoriu chart coverage (55 shapes) + List 2 L/R brows.
// Builds every MMD morph a VRChat MMD dance world can drive, from whatever shapes the avatar has:
//   - Direct mapping (weighted, multi-source per target)
//   - Recipe synthesis: derives missing shapes from mapped ones (あ２ from あ, ワ from あ+い, winks from blink via L/R split, ...)
//   - L/R half-splitting with a smooth center falloff (winks from blink, 左眉上/右眉上 from combined brow shapes)
//   - Optional katakana spelling-variant duplicates for the wink family (dance motion data is inconsistent)
// Prefill remains conservative: at most ONE source per target, exact-token matches only.
public class MMDBlendshapeBuilder : EditorWindow
{
    private SkinnedMeshRenderer smr;
    private Vector2 scroll;
    private bool fillExisting = false;
    private string lastPrefillSummary;
    private bool synthesize = true;
    private bool spellingVariants = true;
    private bool emptyPlaceholders = false;
    private bool flipSplit = false;
    private int mergeMode = 0; // 0 = Weighted Max (overshoot-safe), 1 = Weighted Sum
    private List<TargetRow> rows;
    private Dictionary<string, bool> foldouts = new Dictionary<string, bool>();

    private class Source { public string name; public float weight = 1f; }
    private class TargetRow
    {
        public string key; public string label; public string mmd; public string cat; public bool custom;
        public List<Source> sources = new List<Source>();
    }
    private class Deltas { public Vector3[] dV, dN, dT; }
    private class FrameData { public float weight; public Vector3[] dV, dN, dT; }
    private class ShapeData { public string name; public List<FrameData> frames; }

    private const int SIDE_NONE = 0, SIDE_LEFT = 1, SIDE_RIGHT = 2; // character's left = local +X on standard humanoid imports
    private struct Ing
    {
        public string row; public float w; public int side;
        public Ing(string row, float w, int side = SIDE_NONE) { this.row = row; this.w = w; this.side = side; }
    }

    // ---------------------------------------------------------------- TARGETS (full PDF chart)
    private static List<TargetRow> DefaultRows()
    {
        var list = new List<TargetRow>();
        System.Action<string, string, string, string> Add = (key, label, mmd, cat) =>
            list.Add(new TargetRow { key = key, label = label, mmd = mmd, cat = cat });

        // MOUTH — a/i/u/e/o are the lip-sync visemes MMD dances drive constantly.
        Add("m_a", "a (viseme)", "あ", "Mouth");
        Add("m_i", "i (viseme)", "い", "Mouth");
        Add("m_u", "u (viseme)", "う", "Mouth");
        Add("m_e", "e (viseme)", "え", "Mouth");
        Add("m_o", "o (viseme)", "お", "Mouth");
        Add("m_a2", "a 2 (wider)", "あ２", "Mouth");
        Add("m_n", "n (closed hum)", "ん", "Mouth");
        Add("m_tri", "Mouse_1 ▲", "▲", "Mouth");
        Add("m_hat", "Mouse_2 ∧", "∧", "Mouth");
        Add("m_sq", "□ (square)", "□", "Mouth");
        Add("m_wa", "Wa", "ワ", "Mouth");
        Add("m_omega", "Omega :3", "ω", "Mouth");
        Add("m_omega_sq", "ω□ (open :3)", "ω□", "Mouth");
        Add("m_niyari", "Niyari (smirk)", "にやり", "Mouth");
        Add("m_niyari2", "Niyari2", "にやり２", "Mouth");
        Add("m_smile", "Smile (closed)", "にっこり", "Mouth");
        Add("m_pero", "Pero (tongue)", "ぺろっ", "Mouth");
        Add("m_tehe", "Bero-tehe", "てへぺろ", "Mouth");
        Add("m_tehe2", "Bero-tehe2", "てへぺろ２", "Mouth");
        Add("m_up", "MouseUP (corners up)", "口角上げ", "Mouth");
        Add("m_down", "MouseDW (corners down)", "口角下げ", "Mouth");
        Add("m_wide", "MouseWD (widen)", "口横広げ", "Mouth");
        Add("m_tooth_up", "ToothAnon (hide upper)", "歯無し上", "Mouth");
        Add("m_tooth_dn", "ToothBnon (hide lower)", "歯無し下", "Mouth");

        // EYES
        Add("e_blink", "Blink", "まばたき", "Eyes");
        Add("e_smile", "Smile ^ ^", "笑い", "Eyes");
        Add("e_wink", "Wink (char. left)", "ウィンク", "Eyes");
        Add("e_wink_r", "Wink-a (char. right)", "ウィンク右", "Eyes");
        Add("e_wink2", "Wink-b (happy L)", "ウィンク２", "Eyes");
        Add("e_wink2_r", "Wink-c (happy R)", "ｳｨﾝｸ２右", "Eyes");
        Add("e_nagomi", "Howawa (calm = =)", "なごみ", "Eyes");
        Add("e_hau", "> <", "はぅ", "Eyes");
        Add("e_ha", "Ha!!! (surprise)", "びっくり", "Eyes");
        Add("e_jito", "Jito-eye (doubt)", "じと目", "Eyes");
        Add("e_kiri", "Kiri-eye", "ｷﾘｯ", "Eyes");
        Add("e_hachu", "O O (round pupils)", "はちゅ目", "Eyes");
        Add("e_star", "EyeStar", "星目", "Eyes");
        Add("e_heart", "EyeHeart", "はぁと", "Eyes");
        Add("e_small", "EyeSmall (pupil shrink)", "瞳小", "Eyes");
        Add("e_small_v", "EyeSmall-v", "瞳縦潰れ", "Eyes");
        Add("e_underli", "EyeUnderli", "光下", "Eyes");
        Add("e_funky", "EyeFunky", "恐ろしい子！", "Eyes");
        Add("e_hioff", "EyeHi-off (highlight off)", "ハイライト消", "Eyes");
        Add("e_refoff", "EyeRef-off (reflection off)", "映り込み消", "Eyes");
        Add("e_joy", "Joy", "喜び", "Eyes");
        Add("e_wao", "Wao?!", "わぉ?!", "Eyes");
        Add("e_nagomi_o", "Howawa ω", "なごみω", "Eyes");
        Add("e_wail", "Wail (sad)", "悲しむ", "Eyes");
        Add("e_hostility", "Hostility", "敵意", "Eyes");

        // BROWS (Chart 1 six + List 2 per-side)
        Add("b_serious", "Serious", "真面目", "Brows");
        Add("b_trouble", "Trouble (sad)", "困る", "Brows");
        Add("b_smily", "Smily (cheerful)", "にこり", "Brows");
        Add("b_anger", "Get angry", "怒り", "Brows");
        Add("b_up", "UP", "上", "Brows");
        Add("b_down", "Down", "下", "Brows");
        Add("b_l_up", "Left brow up", "左眉上", "Brows");
        Add("b_l_down", "Left brow down", "左眉下", "Brows");
        Add("b_r_up", "Right brow up", "右眉上", "Brows");
        Add("b_r_down", "Right brow down", "右眉下", "Brows");
        return list;
    }

    // Extra spelling duplicates for the wink family — motion data disagrees about small ィ vs big イ and half/full width.
    private static readonly Dictionary<string, string[]> SpellingVariantsMap = new Dictionary<string, string[]>
    {
        { "e_wink_r",  new[] { "ウインク右" } },
        { "e_wink2",   new[] { "ウインク２" } },
        { "e_wink2_r", new[] { "ウィンク２右", "ウインク２右" } },
    };

    // ---------------------------------------------------------------- PREFILL (conservative, one per target)
    // Matched against the shape name with junk/prefix tokens stripped (joinedCore) and unstripped (joinedAll). Exact matches only.
    private static readonly Dictionary<string, string[]> PrefillTokens = new Dictionary<string, string[]>
    {
        { "m_a", new[]{ "a", "ah", "aa" } },
        { "m_i", new[]{ "i", "ch", "ih" } },
        { "m_u", new[]{ "u", "ou", "oo", "uu" } },
        { "m_e", new[]{ "e", "eh", "ee" } },
        { "m_o", new[]{ "o", "oh" } },
        { "m_n", new[]{ "n", "nn", "mm" } },
        { "m_pero", new[]{ "pero", "tongue", "tongueout" } },
        { "m_up", new[]{ "mouseup", "mouthup", "mouthcornerup", "cornerup", "mouthcornersup" } },
        { "m_down", new[]{ "mousedw", "mousedown", "mouthdown", "mouthcornerdown", "mouthcornersdown", "frown" } },
        { "m_wide", new[]{ "mousewd", "mouthwide", "mouthwiden" } },
        { "m_smile", new[]{ "smile", "mouthsmile", "nikkori", "fun" } },
        { "m_niyari", new[]{ "niyari", "grin", "smirk" } },
        { "m_sq", new[]{ "square", "mouthsquare" } },
        { "m_wa", new[]{ "wa" } },
        { "m_omega", new[]{ "omega", "catmouth", "w3" } },
        { "m_tooth_up", new[]{ "toothanon", "hideupperteeth", "upperteethoff" } },
        { "m_tooth_dn", new[]{ "toothbnon", "hidelowerteeth", "lowerteethoff" } },
        { "e_blink", new[]{ "blink", "blinkboth", "eyesclosed", "eyeclose", "eyesclose", "eyeclosed", "close", "closed" } },
        { "e_smile", new[]{ "blinkhappy", "happyblink", "eyeshappy", "eyesmile", "eyessmile", "happy" } },
        { "e_hau", new[]{ "closex", "eyesclosex" } },
        { "e_nagomi", new[]{ "calm", "nagomi" } },
        { "e_jito", new[]{ "stare", "jito", "jitoeye", "doubt" } },
        { "e_kiri", new[]{ "kiri", "kirieye" } },
        { "e_ha", new[]{ "surprise", "surprised", "eyeswide", "eyewide", "shock" } },
        { "e_small", new[]{ "pupilsmall", "eyesmall", "pupilshrink", "pupilssmall" } },
        { "e_star", new[]{ "eyestar", "stareyes", "stareye" } },
        { "e_heart", new[]{ "eyeheart", "hearteyes", "hearteye" } },
        { "e_joy", new[]{ "joy" } },
        { "e_wail", new[]{ "wail", "cry", "crying", "teary" } },
        { "e_hostility", new[]{ "hostility" } },
        { "b_serious", new[]{ "serious", "browserious" } },
        { "b_trouble", new[]{ "trouble", "sadness", "sad", "sorrow", "browsad", "browssad", "browtrouble" } },
        { "b_smily", new[]{ "cheerful", "smily", "fun", "browhappy", "browshappy", "browsmile" } },
        { "b_anger", new[]{ "anger", "angry", "browangry", "browsangry", "browanger" } },
        { "b_up", new[]{ "upper", "browup", "browsup", "eyebrowup", "eyebrowsup" } },
        { "b_down", new[]{ "lower", "browdown", "browsdown", "eyebrowdown", "eyebrowsdown" } },
    };

    private static readonly HashSet<string> Junk = new HashSet<string>
    { "vrc", "v", "viseme", "vis", "fcl", "mth", "eye", "brw", "bs", "blendshape", "blend", "key", "exp", "face", "facial", "shape", "mmd" };

    // ---------------------------------------------------------------- RECIPES (synthesis of missing shapes)
    // Each target maps to alternatives, tried in order; the first whose ingredients are all resolved wins.
    // Ingredients reference other rows (direct or previously synthesized). Multi-pass, so recipes can chain.
    // Eye recipes stay eye-only and brow recipes brow-only: MMD motion data drives those channels
    // separately, and mixing them causes double-driven overshoot during dances.
    private static readonly Dictionary<string, Ing[][]> Recipes = new Dictionary<string, Ing[][]>
    {
        // Mouth
        { "m_a2", new[]{ new[]{ new Ing("m_a", 1.25f) } } },
        { "m_n", new[]{ new[]{ new Ing("m_u", 0.2f) } } },
        { "m_tri", new[]{ new[]{ new Ing("m_o", 0.45f), new Ing("m_u", 0.35f) } } },
        { "m_hat", new[]{ new[]{ new Ing("m_down", 0.7f), new Ing("m_u", 0.3f) },
                          new[]{ new Ing("m_u", 0.55f) } } },
        { "m_sq", new[]{ new[]{ new Ing("m_a", 0.5f), new Ing("m_e", 0.45f) } } },
        { "m_wa", new[]{ new[]{ new Ing("m_a", 0.65f), new Ing("m_i", 0.35f) } } },
        { "m_omega", new[]{ new[]{ new Ing("m_smile", 0.55f), new Ing("m_u", 0.4f) },
                            new[]{ new Ing("m_u", 0.45f), new Ing("m_i", 0.2f) } } },
        { "m_omega_sq", new[]{ new[]{ new Ing("m_omega", 0.8f), new Ing("m_a", 0.35f) },
                               new[]{ new Ing("m_u", 0.4f), new Ing("m_a", 0.35f) } } },
        { "m_niyari", new[]{ new[]{ new Ing("m_smile", 0.6f), new Ing("m_wide", 0.3f) },
                             new[]{ new Ing("m_smile", 0.7f) },
                             new[]{ new Ing("m_i", 0.35f) } } },
        { "m_niyari2", new[]{ new[]{ new Ing("m_up", 0.6f), new Ing("m_smile", 0.3f) },
                              new[]{ new Ing("m_smile", 0.5f) } } },
        { "m_smile", new[]{ new[]{ new Ing("m_up", 0.8f) },
                            new[]{ new Ing("m_i", 0.25f) } } },
        { "m_tehe", new[]{ new[]{ new Ing("m_pero", 1f), new Ing("m_smile", 0.35f) },
                           new[]{ new Ing("m_pero", 1f), new Ing("m_i", 0.2f) } } },
        { "m_tehe2", new[]{ new[]{ new Ing("m_pero", 1f), new Ing("m_a", 0.3f) } } },
        { "m_up", new[]{ new[]{ new Ing("m_smile", 0.6f) } } },
        { "m_wide", new[]{ new[]{ new Ing("m_i", 0.5f) } } },
        // Eyes — winks are split out of full-eye shapes per half.
        { "e_wink", new[]{ new[]{ new Ing("e_blink", 1f, SIDE_LEFT) } } },
        { "e_wink_r", new[]{ new[]{ new Ing("e_blink", 1f, SIDE_RIGHT) } } },
        { "e_wink2", new[]{ new[]{ new Ing("e_smile", 1f, SIDE_LEFT) },
                            new[]{ new Ing("e_blink", 1f, SIDE_LEFT) } } },
        { "e_wink2_r", new[]{ new[]{ new Ing("e_smile", 1f, SIDE_RIGHT) },
                              new[]{ new Ing("e_blink", 1f, SIDE_RIGHT) } } },
        { "e_smile", new[]{ new[]{ new Ing("e_blink", 0.9f) } } },
        { "e_nagomi", new[]{ new[]{ new Ing("e_blink", 0.85f) } } },
        { "e_nagomi_o", new[]{ new[]{ new Ing("e_nagomi", 1f) } } },
        { "e_hau", new[]{ new[]{ new Ing("e_blink", 1f) } } },
        { "e_joy", new[]{ new[]{ new Ing("e_smile", 1f) } } },
        { "e_jito", new[]{ new[]{ new Ing("e_blink", 0.45f) } } },
        { "e_kiri", new[]{ new[]{ new Ing("e_jito", 0.5f) } } },
        { "e_wail", new[]{ new[]{ new Ing("e_blink", 0.55f) } } },
        { "e_hostility", new[]{ new[]{ new Ing("e_jito", 0.8f) } } },
        // Brows — per-side splits from combined shapes.
        { "b_l_up", new[]{ new[]{ new Ing("b_up", 1f, SIDE_LEFT) } } },
        { "b_l_down", new[]{ new[]{ new Ing("b_down", 1f, SIDE_LEFT) } } },
        { "b_r_up", new[]{ new[]{ new Ing("b_up", 1f, SIDE_RIGHT) } } },
        { "b_r_down", new[]{ new[]{ new Ing("b_down", 1f, SIDE_RIGHT) } } },
    };

    // Shapes that genuinely cannot be synthesized from typical avatar shapes (texture swaps / pupil meshes / teeth toggles).
    private static readonly HashSet<string> UnsynthesizableKeys = new HashSet<string>
    { "m_pero", "m_tooth_up", "m_tooth_dn", "e_ha", "e_hachu", "e_star", "e_heart", "e_small", "e_small_v",
      "e_underli", "e_funky", "e_hioff", "e_refoff", "e_wao" };

    [MenuItem("Tools/MMD Blendshape Builder")]
    public static void ShowWindow() => GetWindow<MMDBlendshapeBuilder>("MMD Blendshapes");

    private void OnEnable() { if (rows == null || rows.Count == 0) rows = DefaultRows(); }

    // ---------------------------------------------------------------- GUI
    private void OnGUI()
    {
        GUILayout.Label("MMD Shape Keys — full chart (55 + L/R brows)", EditorStyles.boldLabel);
        smr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Mesh", smr, typeof(SkinnedMeshRenderer), true);
        if (smr == null || smr.sharedMesh == null) { EditorGUILayout.HelpBox("Assign a Skinned Mesh Renderer (usually 'Body').", MessageType.Warning); return; }

        Mesh mesh = smr.sharedMesh;
        string[] names = new string[mesh.blendShapeCount];
        for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
        string[] addOptions = new string[names.Length + 1];
        addOptions[0] = "+ add shape";
        for (int i = 0; i < names.Length; i++) addOptions[i + 1] = names[i];

        fillExisting = EditorGUILayout.ToggleLeft("Self-map targets that already exist on the mesh (only for exporting mappings — hides real prefill suggestions)", fillExisting);
        if (!string.IsNullOrEmpty(lastPrefillSummary)) EditorGUILayout.HelpBox(lastPrefillSummary, MessageType.None);
        synthesize = EditorGUILayout.ToggleLeft("Synthesize missing shapes from recipes (expressive mode)", synthesize);
        spellingVariants = EditorGUILayout.ToggleLeft("Also write katakana spelling variants for winks (compatibility)", spellingVariants);
        emptyPlaceholders = EditorGUILayout.ToggleLeft("Create zero-delta placeholders for anything still missing (does nothing visually)", emptyPlaceholders);
        flipSplit = EditorGUILayout.ToggleLeft("Flip L/R for split shapes (use if winks come out mirrored)", flipSplit);
        mergeMode = EditorGUILayout.Popup("Multi-source merge", mergeMode, new[] { "Weighted Max (overshoot-safe)", "Weighted Sum (additive)" });

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Prefill Values")) Prefill(mesh);
            if (GUILayout.Button("Build MMD Blendshapes", GUILayout.Height(20))) BuildAll(mesh);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Import From Clipboard")) ImportJson(mesh);
            if (GUILayout.Button("Export Settings To Clipboard")) ExportJson();
            if (GUILayout.Button("Clear All")) foreach (var r in rows) r.sources.Clear();
        }

        EditorGUILayout.HelpBox(
            "Prefill assigns at most one shape per target. Weights next to each source scale its contribution (1 = as-is).\n" +
            "Recipes only fill targets you left empty, from shapes you mapped — nothing is invented for texture-based shapes " +
            "(EyeStar, EyeHeart, highlight/teeth toggles): map real sources for those or they are skipped.",
            MessageType.Info);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        string lastCat = null;
        bool open = true;
        for (int idx = 0; idx < rows.Count; idx++)
        {
            var row = rows[idx];
            if (row.cat != lastCat)
            {
                lastCat = row.cat;
                if (!foldouts.ContainsKey(lastCat)) foldouts[lastCat] = true;
                int mapped = 0, total = 0, exist = 0;
                foreach (var r in rows) if (r.cat == lastCat)
                { total++; if (r.sources.Count > 0) mapped++; if (!r.custom && System.Array.IndexOf(names, r.mmd) >= 0) exist++; }
                foldouts[lastCat] = EditorGUILayout.Foldout(foldouts[lastCat], $"{lastCat}  ({mapped}/{total} mapped, {exist} already on mesh)", true);
                open = foldouts[lastCat];
            }
            if (open) DrawRow(row, names, addOptions);
        }
        EditorGUILayout.Space();
        if (GUILayout.Button("+ Add custom target"))
            rows.Add(new TargetRow { key = "custom", label = "NewShape", mmd = "NewShape", cat = "Custom", custom = true });
        EditorGUILayout.EndScrollView();
    }

    private void DrawRow(TargetRow row, string[] names, string[] addOptions)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (row.custom)
            {
                row.label = EditorGUILayout.TextField(row.label, GUILayout.Width(150));
                row.mmd = row.label;
                if (GUILayout.Button("−", GUILayout.Width(20))) { row.sources.Clear(); row.label = ""; }
            }
            else
            {
                bool onMesh = System.Array.IndexOf(names, row.mmd) >= 0;
                string hint = onMesh ? "  ✓" : (row.sources.Count == 0 && Recipes.ContainsKey(row.key) ? "  (auto)" : "");
                EditorGUILayout.LabelField($"{row.mmd}  {row.label}{hint}", GUILayout.Width(210));
            }

            int add = EditorGUILayout.Popup(0, addOptions, GUILayout.Width(90));
            if (add > 0)
            {
                string nm = names[add - 1];
                if (row.sources.Find(s => s.name == nm) == null) row.sources.Add(new Source { name = nm });
            }

            for (int j = row.sources.Count - 1; j >= 0; j--)
            {
                var src = row.sources[j];
                bool missing = System.Array.IndexOf(names, src.name) < 0;
                EditorGUILayout.LabelField((missing ? "(?) " : "") + src.name, EditorStyles.miniLabel, GUILayout.Width(120));
                src.weight = EditorGUILayout.FloatField(src.weight, GUILayout.Width(35));
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(20))) row.sources.RemoveAt(j);
            }
        }
    }

    // ---------------------------------------------------------------- PREFILL
    private void Prefill(Mesh mesh)
    {
        var picks = new List<string>();

        if (fillExisting)
            foreach (var row in rows)
                if (!row.custom && row.sources.Count == 0 && mesh.GetBlendShapeIndex(row.mmd) >= 0)
                    row.sources.Add(new Source { name = row.mmd });

        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            var a = Analyze(name);
            string joinedCore = string.Concat(a.core);
            string joinedAll = string.Concat(a.all);

            // Category hints keep shapes on the right rows (brow beats eye: "EyeBrow" splits into both).
            bool browHint = a.all.Contains("brow") || a.all.Contains("brows") || a.all.Contains("brw") || a.all.Contains("eyebrow") || a.all.Contains("eyebrows");
            bool eyeHint = !browHint && (a.all.Contains("eye") || a.all.Contains("eyes") || joinedAll.StartsWith("eye"));
            bool mouthHint = !browHint && !eyeHint && (a.all.Contains("mouth") || a.all.Contains("mth") || a.all.Contains("mouse") || a.all.Contains("lip") || a.all.Contains("lips"));
            char catGuard = browHint ? 'b' : eyeHint ? 'e' : mouthHint ? 'm' : '\0';

            // Wink family — two/right detection.
            if (a.core.Contains("wink"))
            {
                bool two = a.core.Contains("2") || name.Contains("2") || a.core.Contains("happy");
                string pk = two ? (a.right ? "e_wink2_r" : "e_wink2") : (a.right ? "e_wink_r" : "e_wink");
                if (AssignIfEmpty(pk, name)) picks.Add($"{RowMmd(pk)} ← {name}");
                continue;
            }
            // Per-eye close = wink geometry (better than splitting a blink in half later).
            if (eyeHint && (a.left || a.right) && (a.core.Contains("close") || a.core.Contains("closed")))
            {
                string pk = a.right ? "e_wink_r" : "e_wink";
                if (AssignIfEmpty(pk, name)) { picks.Add($"{RowMmd(pk)} ← {name}"); continue; }
            }
            // Per-side brows.
            if ((a.left || a.right) && browHint)
            {
                string pk = null;
                if (a.core.Contains("up") || a.core.Contains("upper")) pk = a.left ? "b_l_up" : "b_r_up";
                else if (a.core.Contains("down") || a.core.Contains("lower")) pk = a.left ? "b_l_down" : "b_r_down";
                if (pk != null && AssignIfEmpty(pk, name)) { picks.Add($"{RowMmd(pk)} ← {name}"); continue; }
            }

            // Pass 1: full name (most specific). Pass 2: junk-stripped core. Category hints filter both.
            string hitKey = null;
            foreach (var kv in PrefillTokens)
                if ((catGuard == '\0' || kv.Key[0] == catGuard) && System.Array.IndexOf(kv.Value, joinedAll) >= 0) { hitKey = kv.Key; break; }
            if (hitKey == null)
                foreach (var kv in PrefillTokens)
                    if ((catGuard == '\0' || kv.Key[0] == catGuard) && System.Array.IndexOf(kv.Value, joinedCore) >= 0) { hitKey = kv.Key; break; }
            if (hitKey != null && AssignIfEmpty(hitKey, name)) picks.Add($"{RowMmd(hitKey)} ← {name}");
        }

        int onMesh = 0, unmatched = 0;
        foreach (var row in rows)
        {
            if (row.custom) continue;
            if (mesh.GetBlendShapeIndex(row.mmd) >= 0) onMesh++;
            else if (row.sources.Count == 0) unmatched++;
        }
        lastPrefillSummary = $"Prefill proposed {picks.Count} mappings (shown in the rows below — nothing is written until you hit Build). " +
                             $"{onMesh} targets already exist on the mesh and were left alone. {unmatched} targets have no source yet" +
                             (unmatched > 0 && synthesize ? " (recipes will cover the ones marked (auto))." : ".");
        Debug.Log($"Prefill proposals:\n  {(picks.Count > 0 ? string.Join("\n  ", picks) : "(none — every recognizable shape is either already on the mesh or already assigned)")}\n{lastPrefillSummary}");
    }

    private string RowMmd(string key) { var r = rows.Find(x => x.key == key); return r != null ? r.mmd : key; }

    private bool AssignIfEmpty(string key, string sourceName)
    {
        var row = rows.Find(r => r.key == key && !r.custom);
        if (row == null || row.sources.Count > 0) return false;
        if (sourceName == row.mmd) return false; // already correctly named
        // A shape may prefill at most one row.
        foreach (var r in rows) foreach (var s in r.sources) if (s.name == sourceName) return false;
        row.sources.Add(new Source { name = sourceName });
        return true;
    }

    private static (List<string> core, List<string> all, bool left, bool right) Analyze(string name)
    {
        string spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = Regex.Replace(spaced, "(?<=[A-Za-z])(?=[0-9])", " ");
        var parts = Regex.Split(spaced, "[^A-Za-z0-9]+");
        var core = new List<string>(); var all = new List<string>();
        bool left = false, right = false;
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            string t = p.ToLowerInvariant();
            if (t == "left" || t == "l") { left = true; continue; }
            if (t == "right" || t == "r") { right = true; continue; }
            all.Add(t);
            if (Junk.Contains(t)) continue;
            core.Add(t);
        }
        return (core, all, left, right);
    }

    // ---------------------------------------------------------------- BUILD
    private void BuildAll(Mesh ignored)
    {
        if (!EnsureReadable(smr.sharedMesh)) return;
        Mesh mesh = smr.sharedMesh;
        int vc = mesh.vertexCount;

        var shapes = Snapshot(mesh);
        var resolved = new Dictionary<string, Deltas>();
        var output = new Dictionary<string, Deltas>(); // final MMD-name -> deltas
        int direct = 0, synth = 0;

        // Pass 1 — rows with directly mapped sources.
        foreach (var row in rows)
        {
            string outName = (row.custom ? row.label : row.mmd).Trim();
            if (string.IsNullOrEmpty(outName)) continue;

            var valid = new List<Source>();
            foreach (var s in row.sources) if (mesh.GetBlendShapeIndex(s.name) >= 0) valid.Add(s);
            if (valid.Count == 0) continue;

            var d = Accumulate(mesh, vc, valid, mergeMode);
            if (!row.custom) resolved[row.key] = d;
            if (!(valid.Count == 1 && valid[0].name == outName && NearOne(valid[0].weight)))
            { output[outName] = d; direct++; }
        }

        // Pass 2 — recipe synthesis for empty rows (multi-pass so recipes can chain, e.g. jito -> kiri).
        if (synthesize)
        {
            // Shapes the mesh already ships count as available ingredients even when their rows are empty.
            foreach (var row in rows)
            {
                if (row.custom || resolved.ContainsKey(row.key)) continue;
                if (mesh.GetBlendShapeIndex(row.mmd) < 0) continue;
                resolved[row.key] = Accumulate(mesh, vc, new List<Source> { new Source { name = row.mmd } }, mergeMode);
            }

            Vector3[] verts = null; float falloff = 0f;
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                foreach (var row in rows)
                {
                    if (row.custom || resolved.ContainsKey(row.key)) continue;
                    if (!Recipes.TryGetValue(row.key, out var alternatives)) continue;
                    if (mesh.GetBlendShapeIndex(row.mmd) >= 0) continue; // model already ships this morph — don't overwrite with an approximation

                    foreach (var alt in alternatives)
                    {
                        bool ok = true;
                        foreach (var ing in alt) if (!resolved.ContainsKey(ing.row)) { ok = false; break; }
                        if (!ok) continue;

                        if (verts == null) { verts = mesh.vertices; falloff = Mathf.Max(mesh.bounds.extents.x * 0.02f, 1e-4f); }
                        var d = NewDeltas(vc);
                        foreach (var ing in alt) AddWeighted(d, resolved[ing.row], ing.w, ing.side, verts, falloff, flipSplit);
                        resolved[row.key] = d;
                        output[row.mmd] = d;
                        synth++; changed = true;
                        break;
                    }
                }
                if (!changed) break;
            }
        }

        // Pass 3 — wink spelling variants.
        int variants = 0;
        if (spellingVariants)
        {
            foreach (var kv in SpellingVariantsMap)
            {
                var row = rows.Find(r => r.key == kv.Key);
                if (row == null || !resolved.ContainsKey(kv.Key)) continue;
                foreach (var alias in kv.Value)
                {
                    if (output.ContainsKey(alias) || mesh.GetBlendShapeIndex(alias) >= 0) continue;
                    output[alias] = resolved[kv.Key];
                    variants++;
                }
            }
        }

        // Pass 4 — optional zero-delta placeholders so the complete chart exists by name.
        int placeholders = 0;
        if (emptyPlaceholders)
        {
            foreach (var row in rows)
            {
                if (row.custom || output.ContainsKey(row.mmd) || mesh.GetBlendShapeIndex(row.mmd) >= 0) continue;
                output[row.mmd] = NewDeltas(vc);
                placeholders++;
            }
        }

        if (output.Count == 0) { Debug.LogError("Nothing to build — map at least one source shape (hit Prefill first)."); return; }

        foreach (var kv in output)
        {
            shapes.RemoveAll(sd => sd.name == kv.Key);
            shapes.Add(new ShapeData { name = kv.Key, frames = new List<FrameData> { new FrameData { weight = 100f, dV = kv.Value.dV, dN = kv.Value.dN, dT = kv.Value.dT } } });
        }

        WriteShapes(mesh, shapes, "Build MMD Blendshapes");

        var missing = new List<string>();
        foreach (var row in rows)
            if (!row.custom && !output.ContainsKey(row.mmd) && mesh.GetBlendShapeIndex(row.mmd) < 0)
                missing.Add(row.mmd + (UnsynthesizableKeys.Contains(row.key) ? " (needs a real source: texture/pupil/teeth)" : ""));

        Debug.Log($"Built {direct} mapped + {synth} synthesized + {variants} spelling variants + {placeholders} placeholders = {output.Count} MMD blendshapes.");
        if (missing.Count > 0)
            Debug.LogWarning($"{missing.Count} chart shapes not created (no source, no recipe ingredients): {string.Join(", ", missing)}");
    }

    private static bool NearOne(float w) => Mathf.Abs(w - 1f) < 0.0001f;

    private static Deltas NewDeltas(int vc) => new Deltas { dV = new Vector3[vc], dN = new Vector3[vc], dT = new Vector3[vc] };

    private static Deltas Accumulate(Mesh mesh, int vc, List<Source> sources, int mode)
    {
        var d = NewDeltas(vc);
        var tV = new Vector3[vc]; var tN = new Vector3[vc]; var tT = new Vector3[vc];
        var bestMag = mode == 0 ? new float[vc] : null;

        foreach (var src in sources)
        {
            int s = mesh.GetBlendShapeIndex(src.name);
            if (s < 0) continue;
            int fi = mesh.GetBlendShapeFrameCount(s) - 1;
            mesh.GetBlendShapeFrameVertices(s, fi, tV, tN, tT);
            float w = src.weight;

            if (mode == 1) // Weighted Sum
            {
                for (int v = 0; v < vc; v++) { d.dV[v] += tV[v] * w; d.dN[v] += tN[v] * w; d.dT[v] += tT[v] * w; }
            }
            else // Weighted Max per-vertex (overshoot-safe)
            {
                for (int v = 0; v < vc; v++)
                {
                    float m = tV[v].sqrMagnitude * w * w;
                    if (m > bestMag[v]) { bestMag[v] = m; d.dV[v] = tV[v] * w; d.dN[v] = tN[v] * w; d.dT[v] = tT[v] * w; }
                }
            }
        }
        return d;
    }

    // Adds src * w into dst; if side != NONE, masks to the character's left (+X) or right (−X) half
    // with a linear falloff band across the center line so there is no visible seam.
    private static void AddWeighted(Deltas dst, Deltas src, float w, int side, Vector3[] verts, float falloff, bool flip)
    {
        int vc = dst.dV.Length;
        if (side == SIDE_NONE)
        {
            for (int v = 0; v < vc; v++) { dst.dV[v] += src.dV[v] * w; dst.dN[v] += src.dN[v] * w; dst.dT[v] += src.dT[v] * w; }
            return;
        }
        bool wantLeft = (side == SIDE_LEFT) != flip;
        for (int v = 0; v < vc; v++)
        {
            float leftMask = Mathf.Clamp01((verts[v].x + falloff) / (2f * falloff));
            float m = (wantLeft ? leftMask : 1f - leftMask) * w;
            if (m <= 0f) continue;
            dst.dV[v] += src.dV[v] * m; dst.dN[v] += src.dN[v] * m; dst.dT[v] += src.dT[v] * m;
        }
    }

    private void WriteShapes(Mesh mesh, List<ShapeData> shapes, string undoLabel)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        bool ownAsset = !string.IsNullOrEmpty(path) && path.EndsWith(".asset") && AssetDatabase.IsMainAsset(mesh);
        Mesh working = ownAsset ? mesh : Instantiate(mesh);
        if (!ownAsset) working.name = mesh.name + "_MMD";

        working.ClearBlendShapes();
        foreach (var sd in shapes)
            foreach (var fr in sd.frames)
                working.AddBlendShapeFrame(sd.name, fr.weight, fr.dV, fr.dN, fr.dT);

        if (ownAsset) { EditorUtility.SetDirty(working); AssetDatabase.SaveAssets(); }
        else
        {
            string dir = string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path);
            string np = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{working.name}.asset");
            AssetDatabase.CreateAsset(working, np);
            AssetDatabase.SaveAssets();
            Undo.RecordObject(smr, undoLabel);
            smr.sharedMesh = working;
            PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
            EditorUtility.SetDirty(smr);
            Debug.Log($"Forked mesh to editable asset: {np}");
        }
    }

    private static bool EnsureReadable(Mesh m)
    {
        if (m == null) return false;
        if (m.isReadable) return true;
        string path = AssetDatabase.GetAssetPath(m);
        if (AssetImporter.GetAtPath(path) is ModelImporter mi)
        {
            Debug.Log($"Enabling Read/Write on '{path}' and reimporting...");
            mi.isReadable = true; mi.SaveAndReimport();
            return true;
        }
        Debug.LogError($"'{m.name}' isn't readable and can't be auto-fixed. Enable Read/Write manually.");
        return false;
    }

    private static List<ShapeData> Snapshot(Mesh mesh)
    {
        int vc = mesh.vertexCount;
        var list = new List<ShapeData>(mesh.blendShapeCount);
        for (int s = 0; s < mesh.blendShapeCount; s++)
        {
            var sd = new ShapeData { name = mesh.GetBlendShapeName(s), frames = new List<FrameData>() };
            int frames = mesh.GetBlendShapeFrameCount(s);
            for (int f = 0; f < frames; f++)
            {
                var dV = new Vector3[vc]; var dN = new Vector3[vc]; var dT = new Vector3[vc];
                mesh.GetBlendShapeFrameVertices(s, f, dV, dN, dT);
                sd.frames.Add(new FrameData { weight = mesh.GetBlendShapeFrameWeight(s, f), dV = dV, dN = dN, dT = dT });
            }
            list.Add(sd);
        }
        return list;
    }

    // ---------------------------------------------------------------- CLIPBOARD JSON
    // Format: { "m_a": ["ShapeName", "Other@0.5"], ... } — "@weight" is optional (defaults to 1).
    // Also accepts the Blender plugin's export (legacy keys, single-string values).
    private static readonly Dictionary<string, string> LegacyKeys = new Dictionary<string, string>
    {
        { "ah", "m_a" }, { "ch", "m_i" }, { "u", "m_u" }, { "e", "m_e" }, { "oh", "m_o" },
        { "blink_happy", "e_smile" }, { "blink", "e_blink" }, { "close_X", "e_hau" }, { "calm", "e_nagomi" },
        { "stare", "e_jito" }, { "wink", "e_wink" }, { "wink_right", "e_wink_r" }, { "wink_2", "e_wink2" },
        { "wink_2_right", "e_wink2_r" }, { "cheerful", "b_smily" }, { "serious", "b_serious" },
        { "upper", "b_up" }, { "lower", "b_down" }, { "anger", "b_anger" }, { "sadness", "b_trouble" },
    };

    private void ExportJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        bool first = true;
        foreach (var row in rows)
        {
            if (row.sources.Count == 0) continue;
            string key = row.custom ? row.label : row.key;
            if (!first) sb.Append(",\n"); first = false;
            sb.Append($"  \"{Esc(key)}\": [");
            for (int j = 0; j < row.sources.Count; j++)
            {
                if (j > 0) sb.Append(", ");
                var s = row.sources[j];
                sb.Append(NearOne(s.weight) ? $"\"{Esc(s.name)}\"" : $"\"{Esc(s.name)}@{s.weight:0.###}\"");
            }
            sb.Append("]");
        }
        sb.Append("\n}");
        EditorGUIUtility.systemCopyBuffer = sb.ToString();
        Debug.Log("Settings copied to clipboard as JSON.");
    }

    private void ImportJson(Mesh mesh)
    {
        string data = EditorGUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(data)) { Debug.LogError("Clipboard is empty."); return; }
        int applied = 0;
        var seen = new HashSet<string>();

        foreach (Match m in Regex.Matches(data, "\"([^\"]+)\"\\s*:\\s*\\[([^\\]]*)\\]"))
        {
            string key = m.Groups[1].Value;
            var vals = new List<string>();
            foreach (Match q in Regex.Matches(m.Groups[2].Value, "\"([^\"]*)\"")) vals.Add(q.Groups[1].Value);
            if (ApplyRow(key, vals, mesh)) applied++;
            seen.Add(key);
        }
        foreach (Match m in Regex.Matches(data, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\""))
        {
            string key = m.Groups[1].Value;
            if (seen.Contains(key)) continue;
            if (ApplyRow(key, new List<string> { m.Groups[2].Value }, mesh)) applied++;
        }
        Debug.Log($"Imported mapping for {applied} rows.");
    }

    private bool ApplyRow(string key, List<string> entries, Mesh mesh)
    {
        if (LegacyKeys.TryGetValue(key, out string mappedKey)) key = mappedKey;
        var row = rows.Find(r => string.Equals(r.custom ? r.label : r.key, key, System.StringComparison.OrdinalIgnoreCase));
        if (row == null) { row = new TargetRow { key = key, label = key, mmd = key, cat = "Custom", custom = true }; rows.Add(row); }
        row.sources.Clear();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            string nm = entry; float w = 1f;
            int at = entry.LastIndexOf('@');
            if (at > 0 && float.TryParse(entry.Substring(at + 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            { nm = entry.Substring(0, at); w = parsed; }
            if (mesh != null && mesh.GetBlendShapeIndex(nm) < 0) continue;
            row.sources.Add(new Source { name = nm, weight = w });
        }
        return true;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
