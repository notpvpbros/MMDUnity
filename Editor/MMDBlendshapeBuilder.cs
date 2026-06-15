using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

public class MMDBlendshapeBuilder : EditorWindow
{
    private SkinnedMeshRenderer smr;
    private Vector2 scroll;
    private bool fillExisting = true;
    private int mergeMode = 0; // 0 = Max (overshoot-safe), 1 = Sum
    private List<TargetRow> rows;

    private class TargetRow { public string propKey; public string label; public string mmd; public string cat; public bool custom; public List<string> sources = new List<string>(); }
    private class FrameData { public float weight; public Vector3[] dV, dN, dT; }
    private class ShapeData { public string name; public List<FrameData> frames; }
    private struct ShapeInfo { public string raw; public List<string> core; public string joined; public bool left, right; }

    // Mirrors the Blender plugin's name targets verbatim (note: it uses katakana ウインク for the right/2 variants)
    private static List<TargetRow> DefaultRows()
    {
        var list = new List<TargetRow>();
        void Add(string pk, string label, string mmd, string cat) => list.Add(new TargetRow { propKey = pk, label = label, mmd = mmd, cat = cat });
        Add("ah", "ah", "あ", "Visemes"); Add("ch", "ch", "い", "Visemes"); Add("u", "u", "う", "Visemes");
        Add("e", "e", "え", "Visemes"); Add("oh", "oh", "お", "Visemes");
        Add("blink_happy", "blink happy", "笑い", "Other Shape Keys");
        Add("blink", "blink", "まばたき", "Other Shape Keys");
        Add("close_X", "close><", "はぅ", "Other Shape Keys");
        Add("calm", "calm", "なごみ", "Other Shape Keys");
        Add("stare", "stare", "じと目", "Other Shape Keys");
        Add("wink", "wink", "ウィンク", "Other Shape Keys");
        Add("wink_right", "wink right", "ウインク右", "Other Shape Keys");
        Add("wink_2", "wink 2", "ウインク２", "Other Shape Keys");
        Add("wink_2_right", "wink 2 right", "ウインク２右", "Other Shape Keys");
        Add("cheerful", "cheerful", "にこり", "Other Shape Keys");
        Add("serious", "serious", "真面目", "Other Shape Keys");
        Add("upper", "upper", "上", "Other Shape Keys");
        Add("lower", "lower", "下", "Other Shape Keys");
        Add("anger", "anger", "怒り", "Other Shape Keys");
        Add("sadness", "sadness", "困る", "Other Shape Keys");
        return list;
    }

    // Conservative prefill tokens (exact/alias only). Wink is handled separately.
    private static readonly Dictionary<string, string[]> PrefillTokens = new Dictionary<string, string[]>
    {
        {"ah", new[]{"ah","aa","a"}}, {"ch", new[]{"ch","i","ih"}}, {"u", new[]{"u","ou","oo","uu"}},
        {"e", new[]{"e","eh","ee"}}, {"oh", new[]{"oh","o"}},
        {"blink_happy", new[]{"blinkhappy","happy","joy"}}, {"blink", new[]{"blink"}},
        {"close_X", new[]{"closex"}}, {"calm", new[]{"calm"}}, {"stare", new[]{"stare","jito"}},
        {"cheerful", new[]{"cheerful"}}, {"serious", new[]{"serious"}},
        {"upper", new[]{"upper","browup","browsup"}}, {"lower", new[]{"lower","browdown","browsdown"}},
        {"anger", new[]{"anger","angry"}}, {"sadness", new[]{"sadness","sad"}},
    };

    private static readonly HashSet<string> Junk = new HashSet<string>
    { "vrc","v","viseme","vis","fcl","mth","bs","blendshape","blend","key","exp","mouth","face","facial","shape","mmd" };

    [MenuItem("Tools/MMD Blendshape Builder")]
    public static void ShowWindow() => GetWindow<MMDBlendshapeBuilder>("MMD Blendshapes");

    private void OnEnable() { if (rows == null || rows.Count == 0) rows = DefaultRows(); }

    private void OnGUI()
    {
        GUILayout.Label("MMD Shape Keys", EditorStyles.boldLabel);
        smr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Mesh", smr, typeof(SkinnedMeshRenderer), true);
        if (smr == null || smr.sharedMesh == null) { EditorGUILayout.HelpBox("Assign a Skinned Mesh Renderer.", MessageType.Warning); return; }

        Mesh mesh = smr.sharedMesh;
        string[] names = new string[mesh.blendShapeCount];
        for (int i = 0; i < names.Length; i++) names[i] = mesh.GetBlendShapeName(i);
        string[] addOptions = new string[names.Length + 1];
        addOptions[0] = "+ add shape";
        for (int i = 0; i < names.Length; i++) addOptions[i + 1] = names[i];

        fillExisting = EditorGUILayout.ToggleLeft("Fill existing target shape keys as placeholder", fillExisting);
        mergeMode = EditorGUILayout.Popup("If a row has multiple shapes", mergeMode, new[] { "Max (overshoot-safe)", "Sum (additive)" });

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Prefill Values")) Prefill(mesh);
            if (GUILayout.Button("Duplicate Shape Keys With MMD Names", GUILayout.Height(20))) BuildAll(mesh);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Import From Clipboard")) ImportJson(mesh);
            if (GUILayout.Button("Export Settings To Clipboard")) ExportJson();
            if (GUILayout.Button("Clear All")) foreach (var r in rows) r.sources.Clear();
        }

        EditorGUILayout.HelpBox("Prefill assigns at most one shape per target (conservative, no overshoot). You can add more by hand; with multiple, 'Max' keeps each vertex at its strongest single shape so it won't double up.", MessageType.Info);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        string lastCat = null;
        for (int idx = 0; idx < rows.Count; idx++)
        {
            var row = rows[idx];
            if (row.cat != lastCat) { EditorGUILayout.LabelField(row.cat + ":", EditorStyles.boldLabel); lastCat = row.cat; }
            DrawRow(row, names, addOptions);
        }
        EditorGUILayout.Space();
        if (GUILayout.Button("+ Add custom target"))
            rows.Add(new TargetRow { propKey = "custom", label = "NewShape", mmd = "NewShape", cat = "Custom", custom = true });
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
            else EditorGUILayout.LabelField($"{row.label}  →  {row.mmd}", GUILayout.Width(170));

            int add = EditorGUILayout.Popup(0, addOptions, GUILayout.Width(95));
            if (add > 0) { string nm = names[add - 1]; if (!row.sources.Contains(nm)) row.sources.Add(nm); }

            for (int j = row.sources.Count - 1; j >= 0; j--)
            {
                string nm = row.sources[j];
                bool missing = System.Array.IndexOf(names, nm) < 0;
                if (GUILayout.Button((missing ? "(?) " : "") + nm + "  ✕", EditorStyles.miniButton)) row.sources.RemoveAt(j);
            }
        }
    }

    // ---------- PREFILL (conservative: one per target) ----------
    private void Prefill(Mesh mesh)
    {
        var info = AnalyzeAll(mesh);
        int filled = 0;

        if (fillExisting)
            foreach (var row in rows)
                if (row.sources.Count == 0 && mesh.GetBlendShapeIndex(row.mmd) >= 0) { row.sources.Add(row.mmd); filled++; }

        for (int i = 0; i < info.Length; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            var core = info[i].core; string joined = info[i].joined;

            if (core.Contains("wink"))
            {
                bool two = core.Contains("2") || info[i].raw.Contains("2");
                bool right = info[i].right;
                string pk = two ? (right ? "wink_2_right" : "wink_2") : (right ? "wink_right" : "wink");
                if (AssignIfEmpty(pk, name)) filled++;
                continue;
            }

            foreach (var kv in PrefillTokens)
            {
                bool hit = System.Array.IndexOf(kv.Value, joined) >= 0;
                if (hit) { if (AssignIfEmpty(kv.Key, name)) filled++; break; }
            }
        }
        Debug.Log($"Prefilled {filled} rows (one shape each). Review and edit, then hit Duplicate.");
    }

    private bool AssignIfEmpty(string propKey, string sourceName)
    {
        var row = rows.Find(r => r.propKey == propKey && !r.custom);
        if (row == null || row.sources.Count > 0) return false;
        if (sourceName == row.mmd) return false; // already correctly named — nothing to duplicate
        row.sources.Add(sourceName);
        return true;
    }

    private static ShapeInfo[] AnalyzeAll(Mesh mesh)
    {
        int n = mesh.blendShapeCount;
        var arr = new ShapeInfo[n];
        for (int i = 0; i < n; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            var a = Analyze(name);
            arr[i] = new ShapeInfo { raw = name.Trim(), core = a.core, joined = string.Concat(a.core), left = a.left, right = a.right };
        }
        return arr;
    }

    private static (List<string> core, bool left, bool right) Analyze(string name)
    {
        string spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = Regex.Replace(spaced, "(?<=[A-Za-z])(?=[0-9])", " ");
        var parts = Regex.Split(spaced, "[^A-Za-z0-9]+");
        var core = new List<string>();
        bool left = false, right = false;
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            string t = p.ToLowerInvariant();
            if (t == "left" || t == "l") { left = true; continue; }
            if (t == "right" || t == "r") { right = true; continue; }
            if (Junk.Contains(t)) continue;
            core.Add(t);
        }
        return (core, left, right);
    }

    // ---------- BUILD ----------
    private void BuildAll(Mesh mesh)
    {
        if (!EnsureReadable(smr.sharedMesh)) return;
        mesh = smr.sharedMesh;
        int vc = mesh.vertexCount;

        var shapes = Snapshot(mesh);
        int built = 0, skipped = 0;
        foreach (var row in rows)
        {
            string outName = (row.custom ? row.label : row.mmd).Trim();
            if (string.IsNullOrEmpty(outName)) continue;

            var valid = new List<string>();
            foreach (var nm in row.sources) if (mesh.GetBlendShapeIndex(nm) >= 0) valid.Add(nm);
            if (valid.Count == 0) continue;

            if (valid.Count == 1 && valid[0] == outName) { skipped++; continue; } // already correct

            Accumulate(mesh, vc, valid, mergeMode, out var aV, out var aN, out var aT);
            shapes.RemoveAll(sd => sd.name == outName);
            shapes.Add(new ShapeData { name = outName, frames = new List<FrameData> { new FrameData { weight = 100f, dV = aV, dN = aN, dT = aT } } });
            built++;
        }
        if (built == 0) { Debug.LogError("No rows have valid source shapes to build."); return; }

        WriteShapes(mesh, shapes, "Duplicate Shape Keys With MMD Names");
        Debug.Log($"Created/updated {built} MMD blendshapes" + (skipped > 0 ? $", skipped {skipped} already-correct." : "."));
    }

    private static void Accumulate(Mesh mesh, int vc, List<string> sourceNames, int mode, out Vector3[] aV, out Vector3[] aN, out Vector3[] aT)
    {
        aV = new Vector3[vc]; aN = new Vector3[vc]; aT = new Vector3[vc];
        var tV = new Vector3[vc]; var tN = new Vector3[vc]; var tT = new Vector3[vc];
        var bestMag = mode == 0 ? new float[vc] : null;

        foreach (var nm in sourceNames)
        {
            int s = mesh.GetBlendShapeIndex(nm);
            if (s < 0) continue;
            int fi = mesh.GetBlendShapeFrameCount(s) - 1;
            mesh.GetBlendShapeFrameVertices(s, fi, tV, tN, tT);

            if (mode == 1) // Sum
            {
                for (int v = 0; v < vc; v++) { aV[v] += tV[v]; aN[v] += tN[v]; aT[v] += tT[v]; }
            }
            else // Max per-vertex magnitude (overshoot-safe)
            {
                for (int v = 0; v < vc; v++)
                {
                    float m = tV[v].sqrMagnitude;
                    if (m > bestMag[v]) { bestMag[v] = m; aV[v] = tV[v]; aN[v] = tN[v]; aT[v] = tT[v]; }
                }
            }
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

    // ---------- CLIPBOARD (JSON; reads the Blender plugin's exports too) ----------
    private void ExportJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        bool first = true;
        foreach (var row in rows)
        {
            if (row.sources.Count == 0) continue;
            string key = row.custom ? row.label : row.propKey;
            if (!first) sb.Append(",\n"); first = false;
            sb.Append($"  \"{Esc(key)}\": [");
            for (int j = 0; j < row.sources.Count; j++) { if (j > 0) sb.Append(", "); sb.Append($"\"{Esc(row.sources[j])}\""); }
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

    private bool ApplyRow(string key, List<string> sourceNames, Mesh mesh)
    {
        var row = rows.Find(r => string.Equals(r.custom ? r.label : r.propKey, key, System.StringComparison.OrdinalIgnoreCase));
        if (row == null) { row = new TargetRow { propKey = key, label = key, mmd = key, cat = "Custom", custom = true }; rows.Add(row); }
        row.sources.Clear();
        foreach (var nm in sourceNames)
        {
            if (string.IsNullOrEmpty(nm)) continue;
            if (mesh != null && mesh.GetBlendShapeIndex(nm) < 0) continue; // drop names not on this mesh
            row.sources.Add(nm);
        }
        return true;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
