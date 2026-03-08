using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;

/// <summary>
/// S# Validator — auto-runs on .ss file save.
/// Manual trigger: S# > Validate All Scripts
///
/// Now validates:
///  - variable script references (checks C# type exists via reflection)
///  - cross-script field/method/property access (checks member exists on C# type)
///  - lifecycle hooks: on Start { } / on Update { }
///  - lists, string ops, all existing commands
/// </summary>
public class SSValidator : AssetPostprocessor
{
    private static readonly HashSet<string> KEYWORDS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "say", "variable", "hidden", "string", "bool", "int", "float",
        "gameobject", "list", "script",
        "if", "else", "loop", "while", "for", "to", "step",
        "action", "return", "yes", "no", "null",
        "wait", "seconds", "enable", "disable", "destroy",
        "move", "change", "by", "isActive",
        "on", "Start", "Update",
        "add", "remove", "from",
        "contains", "upper", "lower", "length",
    };

    private static readonly HashSet<string> VALID_TYPES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "string", "int", "float", "bool", "gameobject", "list", "script" };

    private static readonly HashSet<string> LIFECYCLE_HOOKS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Start", "Update" };

    // -----------------------------------------------------------------------
    // Auto-run on asset save
    // -----------------------------------------------------------------------

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string path in imported)
        {
            if (!path.EndsWith(".ss") && !path.EndsWith(".ss.txt")) continue;
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) continue;
            ValidateScript(asset.text, path);
        }
    }

    // -----------------------------------------------------------------------
    // Menu item
    // -----------------------------------------------------------------------

    [MenuItem("S#/Validate All Scripts")]
    public static void ValidateAll()
    {
        string[] guids = AssetDatabase.FindAssets("t:TextAsset");
        int count = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".ss") && !path.EndsWith(".ss.txt")) continue;
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) continue;
            ValidateScript(asset.text, path);
            count++;
        }
        Debug.Log(count == 0 ? "[SS Validator] No .ss files found." : $"[SS Validator] Validated {count} script(s).");
    }

    // -----------------------------------------------------------------------
    // Core validator
    // -----------------------------------------------------------------------

    public static void ValidateScript(string source, string scriptName)
    {
        string[] lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // First pass — collect declared variable names, types, and script type mappings
        var declaredVars    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // varName -> ssType
        var scriptTypeMap   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // varName -> C# type name
        var declaredActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasErrors      = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            int varOffset = 0;
            if (parts.Length > 0 && parts[0].Equals("hidden", StringComparison.OrdinalIgnoreCase)) varOffset = 1;

            if (parts.Length > varOffset && parts[varOffset].Equals("variable", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length > varOffset + 2)
                {
                    string varType = parts[varOffset + 1].ToLower();
                    string varName = parts[varOffset + 2];
                    declaredVars[varName] = varType;

                    // script type: variable script varName CSharpTypeName
                    if (varType == "script" && parts.Length > varOffset + 3)
                        scriptTypeMap[varName] = parts[varOffset + 3];
                }
            }

            if (parts.Length >= 2 && parts[0].Equals("action", StringComparison.OrdinalIgnoreCase))
                declaredActions.Add(parts[1].Split('(')[0]);
        }

        // Validate script type references — check C# types exist via reflection
        foreach (var kvp in scriptTypeMap)
        {
            Type csType = FindTypeInAssemblies(kvp.Value);
            if (csType == null)
            {
                Debug.LogError($"[SS Validator] ({scriptName}) Script type '{kvp.Value}' for variable '{kvp.Key}' does not exist. Script will not run.");
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            Debug.LogError($"[SS Validator] ({scriptName}) Validation failed — fix errors above before running.");
            return;
        }

        // Second pass — line-by-line validation
        int blockDepth = 0;
        for (int lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            string line = lines[lineNum].Trim();
            int ln      = lineNum + 1;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            foreach (char c in line) { if (c == '{') blockDepth++; else if (c == '}') blockDepth--; }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            ValidateTokens(parts, ln, scriptName, declaredVars, scriptTypeMap, declaredActions, ref hasErrors);
        }

        if (blockDepth != 0)
            Debug.LogWarning($"[SS] ({scriptName}) Mismatched braces — check your {{ }} blocks");

        if (hasErrors)
            Debug.LogError($"[SS Validator] ({scriptName}) Validation failed — script will not run.");
    }

    // -----------------------------------------------------------------------
    // Per-line validation
    // -----------------------------------------------------------------------

    private static void ValidateTokens(
        string[] parts, int ln, string scriptName,
        Dictionary<string, string> declaredVars,
        Dictionary<string, string> scriptTypeMap,
        HashSet<string> declaredActions,
        ref bool hasErrors)
    {
        if (parts.Length == 0) return;
        string first = parts[0].ToLower();

        // ---- closing brace / opening brace ----
        if (parts[0] == "}" || parts[0] == "{") return;

        // ---- action definition ----
        if (first == "action") return;

        // ---- on Start / on Update ----
        if (first == "on")
        {
            if (parts.Length < 2)
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'on' expects a hook name: on Start {{ }} or on Update {{ }}"); return; }
            if (!LIFECYCLE_HOOKS.Contains(parts[1]))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) Unknown lifecycle hook '{parts[1]}' — use 'Start' or 'Update'");
            return;
        }

        // ---- else ----
        if (first == "else") return;

        // ---- return ----
        if (first == "return") return;

        // ---- say ----
        if (first == "say")
        {
            if (parts.Length < 2)
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'say' expects a value");
            return;
        }

        // ---- variable / hidden variable ----
        if (first == "variable" || (first == "hidden" && parts.Length > 1 &&
            parts[1].Equals("variable", StringComparison.OrdinalIgnoreCase)))
        {
            string[] vparts = first == "hidden" ? SubArray(parts, 1) : parts;
            if (vparts.Length < 3)
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'variable' expects: variable type name"); return; }

            string type = vparts[1].ToLower();
            string name = vparts[2];

            if (!VALID_TYPES.Contains(type))
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) Unknown type '{type}' — use: string, int, float, bool, gameobject, list, script"); return; }

            if (type == "script")
            {
                if (vparts.Length < 4)
                { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'variable script' expects: variable script varName CSharpTypeName"); return; }
                // Type existence already checked in first pass
                return;
            }

            if (type == "gameobject" && vparts.Length >= 5 && vparts[3] == "=")
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) gameobject variables must be assigned in the Inspector"); return; }

            if (type == "list") return;

            if (vparts.Length >= 5 && vparts[3] == "=")
            {
                string raw = vparts[4];
                bool isExpression = raw.StartsWith("(") || raw.StartsWith("-") ||
                    (!raw.StartsWith("\"") && !IsLiteralValue(raw));
                if (!isExpression)
                {
                    bool mismatch = false;
                    if (type == "string"  && !raw.StartsWith("\""))  mismatch = true;
                    if ((type == "int" || type == "float") &&
                        !float.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _)) mismatch = true;
                    if (type == "bool" &&
                        !raw.Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                        !raw.Equals("no",  StringComparison.OrdinalIgnoreCase)) mismatch = true;
                    if (mismatch)
                        Debug.LogWarning($"[SS] ({scriptName}:L{ln}) Type mismatch: '{name}' is {type} but value looks wrong");
                }
            }
            return;
        }

        // ---- if ----
        if (first == "if") { if (parts.Length < 2) Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'if' expects a condition"); return; }

        // ---- loop while ----
        if (first == "loop")
        {
            if (parts.Length < 3 || !parts[1].Equals("while", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'loop' expects: loop while condition {{ }}");
            return;
        }

        // ---- for ----
        if (first == "for")
        {
            if (parts.Length < 5) Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'for' expects: for i = 0 to 10 {{ }}");
            return;
        }

        // ---- wait ----
        if (first == "wait")
        {
            if (parts.Length < 3 || !parts[2].Equals("seconds", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'wait' expects: wait 2 seconds");
            return;
        }

        // ---- enable / disable / destroy ----
        if (first == "enable" || first == "disable")
        { if (parts.Length < 2) Debug.LogWarning($"[SS] ({scriptName}:L{ln}) '{first}' expects a GameObject name"); return; }
        if (first == "destroy")
        { if (parts.Length < 2) Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'destroy' expects a GameObject name"); return; }

        // ---- move ----
        if (first == "move")
        {
            if (parts.Length < 6 || !parts[2].Equals("to", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'move' expects: move obj to x y z");
            return;
        }

        // ---- change ----
        if (first == "change")
        {
            if (parts.Length < 4 || !parts[1].Contains(".") || !parts[2].Equals("by", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'change' expects: change obj.x by 5");
            return;
        }

        // ---- add / remove ----
        if (first == "add")
        {
            if (parts.Length < 4 || !parts[2].Equals("to", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'add' expects: add value to listName");
            return;
        }
        if (first == "remove")
        {
            if (parts.Length < 4 || !parts[2].Equals("from", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) 'remove' expects: remove 1 from listName");
            return;
        }

        // ---- cross-script method call: varName.Method(args) ----
        if (parts[0].Contains(".") && parts[0].Contains("("))
        {
            int dot = parts[0].IndexOf('.');
            int paren = parts[0].IndexOf('(');
            string varName = parts[0].Substring(0, dot);
            string methodName = parts[0].Substring(dot + 1, paren - dot - 1);

            if (!declaredVars.ContainsKey(varName))
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) '{varName}' is not declared"); return; }

            if (declaredVars[varName] != "script")
            { Debug.LogWarning($"[SS] ({scriptName}:L{ln}) '{varName}' is not a script variable"); return; }

            if (scriptTypeMap.TryGetValue(varName, out string csTypeName))
            {
                Type csType = FindTypeInAssemblies(csTypeName);
                if (csType != null)
                {
                    // Check if it's an SSScript first (actions checked at runtime)
                    bool isSSScript = typeof(SSScript).IsAssignableFrom(csType) ||
                                      csTypeName.Equals("SSScript", StringComparison.OrdinalIgnoreCase);
                    if (!isSSScript)
                    {
                        MethodInfo method = csType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                        if (method == null)
                        {
                            Debug.LogError($"[SS Validator] ({scriptName}:L{ln}) Method '{methodName}' does not exist on '{csTypeName}'. Script will not run.");
                            hasErrors = true;
                        }
                    }
                }
            }
            return;
        }

        // ---- dot assignment:  varName.field = value ----
        if (parts[0].Contains(".") && parts.Length >= 3 && parts[1] == "=")
        {
            int dot = parts[0].IndexOf('.');
            string varName  = parts[0].Substring(0, dot);
            string propName = parts[0].Substring(dot + 1);

            if (declaredVars.TryGetValue(varName, out string varType) && varType == "script" &&
                scriptTypeMap.TryGetValue(varName, out string csTypeName))
            {
                Type csType = FindTypeInAssemblies(csTypeName);
                if (csType != null)
                {
                    bool isSSScript = typeof(SSScript).IsAssignableFrom(csType) ||
                                      csTypeName.Equals("SSScript", StringComparison.OrdinalIgnoreCase);
                    if (!isSSScript)
                    {
                        FieldInfo    field = csType.GetField(propName, BindingFlags.Public | BindingFlags.Instance);
                        PropertyInfo prop  = csType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                        if (field == null && prop == null)
                        {
                            Debug.LogError($"[SS Validator] ({scriptName}:L{ln}) Field/property '{propName}' does not exist on '{csTypeName}'. Script will not run.");
                            hasErrors = true;
                        }
                    }
                }
            }
            return;
        }

        // ---- GO dot assignment  obj.x = value ----
        if (parts[0].Contains(".") && parts.Length >= 3 && parts[1] == "=") return;

        // ---- list index assignment: list[1] = value ----
        if (parts[0].Contains("[") && parts.Length >= 3 && parts[1] == "=") return;

        // ---- dot property read (say obj.field, variable x = obj.field) ----
        if (parts[0].Contains(".")) return;

        // ---- variable reassignment: name = value ----
        if (parts.Length >= 3 && parts[1] == "=")
        {
            if (!declaredVars.ContainsKey(parts[0]))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) '{parts[0]}' assigned but never declared");
            return;
        }

        // ---- action call: Name(...) ----
        if (parts[0].Contains("("))
        {
            string actionName = parts[0].Split('(')[0];
            if (!declaredActions.Contains(actionName))
                Debug.LogWarning($"[SS] ({scriptName}:L{ln}) Unknown action '{actionName}'");
            return;
        }

        // ---- unknown ----
        if (!KEYWORDS.Contains(parts[0]))
            Debug.LogWarning($"[SS] ({scriptName}:L{ln}) Unknown command '{parts[0]}'");
    }

    // -----------------------------------------------------------------------
    // Reflection type finder
    // -----------------------------------------------------------------------

    private static Type FindTypeInAssemblies(string typeName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType(typeName, false, true);
            if (t != null) return t;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool IsLiteralValue(string s)
    {
        if (s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("no",  StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("\"")) return true;
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
        return false;
    }

    private static string[] SubArray(string[] arr, int start)
    {
        if (start >= arr.Length) return new string[0];
        string[] r = new string[arr.Length - start];
        Array.Copy(arr, start, r, 0, r.Length);
        return r;
    }
}
#endif