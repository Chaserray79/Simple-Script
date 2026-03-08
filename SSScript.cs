using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Attach this to any GameObject and drag your .ss file into the slot.
/// Supports on Start { } and on Update { } lifecycle hooks.
/// Provides C# bridge: GetVariable, SetVariable, CallAction, OnVariableChanged, OnActionCalled.
/// </summary>
public class SSScript : MonoBehaviour
{
    [Tooltip("Drag your .ss file here")]
    public TextAsset scriptFile;

    public List<SSExposedVariable> exposedVariables = new List<SSExposedVariable>();

    // -----------------------------------------------------------------------
    // C# Bridge events
    // -----------------------------------------------------------------------

    /// <summary>Fired whenever any S# variable changes value (from S# code or C# SetVariable).</summary>
    public event Action<string, object> OnVariableChanged;

    /// <summary>Fired whenever an S# action is called.</summary>
    public event Action<string, object[]> OnActionCalled;

    // Internal — SSProcessor reads/writes these
    internal Action<string, object> VariableChangedCallback  => OnVariableChanged;
    internal Action<string, object[]> ActionCalledCallback   => OnActionCalled;

    // -----------------------------------------------------------------------
    // C# Bridge methods
    // -----------------------------------------------------------------------

    private SSProcessor _processor;

    /// <summary>Get the current value of an S# variable by name.</summary>
    public object GetVariable(string varName)
    {
        if (_processor == null) { Debug.LogWarning($"[SSScript] GetVariable called before script started"); return null; }
        return _processor.GetVariable(this, varName);
    }

    /// <summary>Set an S# variable by name. Fires OnVariableChanged.</summary>
    public void SetVariable(string varName, object value)
    {
        if (_processor == null) { Debug.LogWarning($"[SSScript] SetVariable called before script started"); return; }
        _processor.SetVariable(this, varName, value);
    }

    /// <summary>Call an S# action by name with optional arguments.</summary>
    public void CallAction(string actionName, params object[] args)
    {
        if (_processor == null) { Debug.LogWarning($"[SSScript] CallAction called before script started"); return; }
        _processor.CallActionFromCSharp(this, actionName, args);
    }

    // -----------------------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------------------

    private void Start()
    {
        if (scriptFile == null)
        {
            Debug.LogWarning($"[SSScript] No .ss file assigned on '{gameObject.name}'", this);
            return;
        }

        _processor = FindObjectOfType<SSProcessor>();
        if (_processor == null)
        {
            Debug.LogError("[SSScript] No SSProcessor found in the scene!", this);
            return;
        }

        _processor.RunScript(this);
    }

    private void Update()
    {
        if (_processor != null)
            _processor.RunUpdate(this);
    }

    public SSExposedVariable GetExposedVariable(string name)
        => exposedVariables.Find(v => v.name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------------------
// SSExposedVariable
// ---------------------------------------------------------------------------

[Serializable]
public class SSExposedVariable
{
    public string name;
    public string type;
    public string stringValue;
    public float  numberValue;
    public bool   boolValue;
    public GameObject gameObjectValue;
    public Component  scriptValue;          // for "script" type
    public List<string> listItems = new List<string>();

    public object GetValue()
    {
        switch (type.ToLower())
        {
            case "string":     return stringValue;
            case "int":
            case "float":      return numberValue;
            case "bool":       return boolValue;
            case "gameobject": return gameObjectValue;
            case "script":     return scriptValue;
            case "list":
                var list = new List<object>();
                foreach (string item in listItems)
                {
                    if (float.TryParse(item, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f))
                        list.Add(f);
                    else if (item.Equals("yes", StringComparison.OrdinalIgnoreCase)) list.Add(true);
                    else if (item.Equals("no",  StringComparison.OrdinalIgnoreCase)) list.Add(false);
                    else list.Add(item);
                }
                return list;
            default: return stringValue;
        }
    }
}

// ---------------------------------------------------------------------------
// Custom Inspector
// ---------------------------------------------------------------------------

#if UNITY_EDITOR
[CustomEditor(typeof(SSScript))]
public class SSScriptEditor : Editor
{
    private Dictionary<string, bool> _listFoldouts = new Dictionary<string, bool>();

    public override void OnInspectorGUI()
    {
        SSScript ss = (SSScript)target;

        EditorGUI.BeginChangeCheck();
        ss.scriptFile = (TextAsset)EditorGUILayout.ObjectField("Script File", ss.scriptFile, typeof(TextAsset), false);
        if (EditorGUI.EndChangeCheck())
        {
            ScanForVariables(ss);
            EditorUtility.SetDirty(ss);
        }

        if (ss.scriptFile == null)
        {
            EditorGUILayout.HelpBox("Drag a .ss file into the slot above.", MessageType.Info);
            return;
        }

        if (GUILayout.Button("↺  Rescan Variables"))
        {
            ScanForVariables(ss);
            EditorUtility.SetDirty(ss);
        }

        if (ss.exposedVariables.Count == 0)
        {
            EditorGUILayout.HelpBox("No exposed variables found.\nUse 'variable type name' to expose one.", MessageType.None);
            return;
        }

        GUILayout.Space(4);
        GUILayout.Label("Script Variables", EditorStyles.boldLabel);

        foreach (SSExposedVariable v in ss.exposedVariables)
        {
            EditorGUI.BeginChangeCheck();

            switch (v.type.ToLower())
            {
                case "string":
                    v.stringValue = EditorGUILayout.TextField(v.name, v.stringValue);
                    break;
                case "int":
                    v.numberValue = EditorGUILayout.IntField(v.name, (int)v.numberValue);
                    break;
                case "float":
                    v.numberValue = EditorGUILayout.FloatField(v.name, v.numberValue);
                    break;
                case "bool":
                    v.boolValue = EditorGUILayout.Toggle(v.name, v.boolValue);
                    break;
                case "gameobject":
                    v.gameObjectValue = (GameObject)EditorGUILayout.ObjectField(
                        v.name, v.gameObjectValue, typeof(GameObject), true);
                    break;
                case "script":
                    // Show a Component object field — label includes the expected type name
                    v.scriptValue = (Component)EditorGUILayout.ObjectField(
                        $"{v.name}  ({v.stringValue})", v.scriptValue, typeof(Component), true);
                    break;
                case "list":
                    DrawListVariable(v, ss);
                    break;
            }

            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(ss);
        }
    }

    // -----------------------------------------------------------------------
    // List drawer
    // -----------------------------------------------------------------------

    private void DrawListVariable(SSExposedVariable v, SSScript ss)
    {
        if (!_listFoldouts.ContainsKey(v.name))
            _listFoldouts[v.name] = true;

        string header = $"{v.name}   [{v.listItems.Count} item{(v.listItems.Count == 1 ? "" : "s")}]";
        _listFoldouts[v.name] = EditorGUILayout.Foldout(_listFoldouts[v.name], header, true, EditorStyles.foldoutHeader);
        if (!_listFoldouts[v.name]) return;

        EditorGUI.indentLevel++;
        int removeAt = -1;

        for (int i = 0; i < v.listItems.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"[{i + 1}]", GUILayout.Width(32));
            v.listItems[i] = EditorGUILayout.TextField(v.listItems[i]);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("✕", GUILayout.Width(24))) removeAt = i;
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();
        }

        if (removeAt >= 0) { v.listItems.RemoveAt(removeAt); EditorUtility.SetDirty(ss); }

        GUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(EditorGUI.indentLevel * 15);
        Color pc = GUI.color;
        GUI.color = new Color(0.4f, 1f, 0.6f);
        if (GUILayout.Button("＋  Add Item")) { v.listItems.Add(""); EditorUtility.SetDirty(ss); }
        GUI.color = pc;
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(4);
        EditorGUI.indentLevel--;
    }

    // -----------------------------------------------------------------------
    // Variable scanner
    // -----------------------------------------------------------------------

    private void ScanForVariables(SSScript ss)
    {
        if (ss.scriptFile == null) { ss.exposedVariables.Clear(); return; }

        string[] lines = ss.scriptFile.text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var found = new List<SSExposedVariable>();
        var validTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "string", "int", "float", "bool", "gameobject", "list", "script" };

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            if (parts[0].Equals("hidden", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("variable", StringComparison.OrdinalIgnoreCase)) continue;

            if (!parts[0].Equals("variable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!validTypes.Contains(parts[1])) continue;

            string varType = parts[1].ToLower();
            string varName = parts[2];

            // script type: variable script myPlayer PlayerController
            // parts[2] = varName, parts[3] = C# type name
            if (varType == "script")
            {
                string csTypeName = parts.Length >= 4 ? parts[3] : "";
                SSExposedVariable existing = ss.exposedVariables.Find(v => v.name == varName && v.type == "script");
                if (existing != null)
                {
                    existing.stringValue = csTypeName; // keep type name up to date
                    found.Add(existing);
                }
                else
                    found.Add(new SSExposedVariable { name = varName, type = "script", stringValue = csTypeName });
                continue;
            }

            if (varType == "gameobject")
            {
                SSExposedVariable existing = ss.exposedVariables.Find(v => v.name == varName && v.type == varType);
                found.Add(existing ?? new SSExposedVariable { name = varName, type = varType });
                continue;
            }

            if (varType == "list")
            {
                SSExposedVariable existing = ss.exposedVariables.Find(v => v.name == varName && v.type == varType);
                if (existing != null) { found.Add(existing); continue; }
                var newList = new SSExposedVariable { name = varName, type = varType };
                int eqIdx = Array.IndexOf(parts, "=");
                if (eqIdx >= 0)
                {
                    string rest = string.Join(" ", parts, eqIdx + 1, parts.Length - eqIdx - 1)
                        .Trim().TrimStart('[').TrimEnd(']');
                    foreach (string item in rest.Split(','))
                    {
                        string t = item.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(t)) newList.listItems.Add(t);
                    }
                }
                found.Add(newList);
                continue;
            }

            SSExposedVariable existingVar = ss.exposedVariables.Find(v => v.name == varName && v.type == varType);
            if (existingVar != null) { found.Add(existingVar); continue; }

            var ev = new SSExposedVariable { name = varName, type = varType };
            if (parts.Length >= 5 && parts[3] == "=")
            {
                string raw = parts[4];
                switch (varType)
                {
                    case "string": ev.stringValue = raw.Trim('"'); break;
                    case "int": case "float":
                        float.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out ev.numberValue);
                        break;
                    case "bool":
                        ev.boolValue = raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
            found.Add(ev);
        }

        ss.exposedVariables = found;
    }
}
#endif