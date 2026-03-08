using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// S# (Simple Script) Language Processor — place on ONE manager GameObject.
///
/// Script variable syntax:
///   variable script myPlayer PlayerController     — drag &amp; drop in Inspector
///   hidden variable script enemy EnemyAI          — finds by type name at runtime
///
/// Cross-script calls:
///   myPlayer.TakeDamage(10)       — calls S# action OR C# method
///   say myPlayer.health           — reads S# variable OR C# public field/property
///   myPlayer.speed = 10           — writes S# variable OR C# public field/property
///
/// C# Bridge (from C# code):
///   ss.GetVariable("health")
///   ss.SetVariable("health", 100)
///   ss.CallAction("TakeDamage", 10)
///   ss.OnVariableChanged += (name, val) => { }
///   ss.OnActionCalled    += (name, args) => { }
/// </summary>
public class SSProcessor : MonoBehaviour
{
    [Tooltip("Log every token during parsing")]
    public bool verboseLogging = false;

    // -----------------------------------------------------------------------
    // Internal classes
    // -----------------------------------------------------------------------

    private class IntRef { public int Value; public IntRef(int v) { Value = v; } }

    private class SSVariable
    {
        public string Type;
        public bool   Hidden;
        public object Value;
    }

    private class SSAction
    {
        public string       Name;
        public List<string> Parameters;
        public List<Token>  Body;
    }

    private class ScriptState
    {
        public Dictionary<string, SSVariable> Variables =
            new Dictionary<string, SSVariable>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SSAction> Actions =
            new Dictionary<string, SSAction>(StringComparer.OrdinalIgnoreCase);
        public List<Token>  UpdateBody    = null;
        public bool         UpdateRunning = false;
        public GameObject   Owner;
        public SSScript     Script;
    }

    private Dictionary<SSScript, ScriptState> _states = new Dictionary<SSScript, ScriptState>();
    private ScriptState _current;

    // -----------------------------------------------------------------------
    // Keywords
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // Token
    // -----------------------------------------------------------------------

    public enum TokenType
    {
        Keyword, Identifier, StringLiteral, NumberLiteral, BoolLiteral,
        Operator, Punctuation, Comment, EndOfLine, Unknown
    }

    public struct Token
    {
        public TokenType Type;
        public string    Value;
        public int       Line;
        public override string ToString() => $"[{Type}|'{Value}'|L{Line}]";
    }

    // -----------------------------------------------------------------------
    // C# Bridge — called by SSScript public methods
    // -----------------------------------------------------------------------

    public object GetVariable(SSScript ss, string varName)
    {
        if (!_states.TryGetValue(ss, out ScriptState state)) return null;
        return state.Variables.TryGetValue(varName, out SSVariable v) ? v.Value : null;
    }

    public void SetVariable(SSScript ss, string varName, object value)
    {
        if (!_states.TryGetValue(ss, out ScriptState state)) return;
        if (state.Variables.ContainsKey(varName))
            state.Variables[varName].Value = value;
        else
            state.Variables[varName] = new SSVariable { Type = "var", Hidden = false, Value = value };

        ss.VariableChangedCallback?.Invoke(varName, value);
    }

    public void CallActionFromCSharp(SSScript ss, string actionName, object[] args)
    {
        if (!_states.TryGetValue(ss, out ScriptState state)) return;
        if (!state.Actions.TryGetValue(actionName, out SSAction action))
        { Debug.LogWarning($"[SS] CallAction: '{actionName}' not found in '{ss.scriptFile.name}'"); return; }

        ss.ActionCalledCallback?.Invoke(actionName, args);

        StartCoroutine(RunActionFromCSharp(state, action, args));
    }

    private IEnumerator RunActionFromCSharp(ScriptState state, SSAction action, object[] args)
    {
        ScriptState saved = _current;
        _current = state;

        var savedVars = new Dictionary<string, SSVariable>(state.Variables, StringComparer.OrdinalIgnoreCase);
        state.Variables.Remove("__returning__");

        for (int p = 0; p < action.Parameters.Count && p < args.Length; p++)
            state.Variables[action.Parameters[p]] = new SSVariable { Type = "var", Hidden = true, Value = args[p] };

        IntRef bi = new IntRef(0);
        yield return StartCoroutine(Execute(action.Body, bi));

        state.Variables = savedVars;
        _current = saved;
    }

    // -----------------------------------------------------------------------
    // Public entry points
    // -----------------------------------------------------------------------

    public void RunScript(SSScript ss)
    {
        if (ss == null || ss.scriptFile == null) return;
        Debug.Log($"[SS] Running '{ss.scriptFile.name}' on '{ss.gameObject.name}'");
        StartCoroutine(RunSource(ss.scriptFile.text, ss.scriptFile.name, ss.gameObject, ss));
    }

    public void RunUpdate(SSScript ss)
    {
        if (!_states.TryGetValue(ss, out ScriptState state)) return;
        if (state.UpdateBody == null || state.UpdateRunning) return;
        state.UpdateRunning = true;
        StartCoroutine(RunUpdateBody(state));
    }

    public void RunAll()
    {
        SSScript[] scripts = FindObjectsOfType<SSScript>();
        if (scripts.Length == 0) { Debug.LogWarning("[SS] No SSScript components found."); return; }
        foreach (SSScript ss in scripts)
            if (ss.enabled) RunScript(ss);
    }

    // -----------------------------------------------------------------------
    // Runner
    // -----------------------------------------------------------------------

    private IEnumerator RunSource(string source, string scriptName, GameObject owner, SSScript ss)
    {
        ScriptState state = new ScriptState { Owner = owner, Script = ss };
        _states[ss] = state;

        // Load inspector values
        foreach (SSExposedVariable ev in ss.exposedVariables)
        {
            object val = ev.GetValue();

            // For script type: if inspector value is null and it's hidden, find by type at runtime
            if (ev.type.ToLower() == "script" && val == null && !string.IsNullOrEmpty(ev.stringValue))
                val = FindScriptByTypeName(ev.stringValue);

            state.Variables[ev.name] = new SSVariable { Type = ev.type, Hidden = false, Value = val };
        }

        List<Token> tokens = Tokenize(source);

        if (verboseLogging)
            foreach (Token t in tokens) Debug.Log($"[SS] Token: {t}");

        RegisterActions(tokens, state);
        ExtractLifecycleHooks(tokens, state);

        // Resolve hidden script variables
        ResolveHiddenScriptVars(source, state);

        _current = state;
        IntRef idx = new IntRef(0);
        yield return StartCoroutine(Execute(tokens, idx));
    }

    private IEnumerator RunUpdateBody(ScriptState state)
    {
        ScriptState saved = _current;
        _current = state;
        IntRef idx = new IntRef(0);
        yield return StartCoroutine(Execute(state.UpdateBody, idx));
        state.UpdateRunning = false;
        _current = saved;
    }

    // -----------------------------------------------------------------------
    // Hidden script variable resolver — finds by C# type name at runtime
    // -----------------------------------------------------------------------

    private void ResolveHiddenScriptVars(string source, ScriptState state)
    {
        string[] lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("hidden", StringComparison.OrdinalIgnoreCase)) continue;

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // hidden variable script varName TypeName
            if (parts.Length < 5) continue;
            if (!parts[1].Equals("variable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[2].Equals("script",   StringComparison.OrdinalIgnoreCase)) continue;

            string varName   = parts[3];
            string typeName  = parts[4];

            Component found = FindScriptByTypeName(typeName);
            if (found == null)
                Debug.LogWarning($"[SS] Could not find script of type '{typeName}' in scene");

            state.Variables[varName] = new SSVariable { Type = "script", Hidden = true, Value = found };
        }
    }

    private Component FindScriptByTypeName(string typeName)
    {
        // Search all assemblies for the type
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType(typeName, false, true);
            if (t == null) continue;
            Component c = FindObjectOfType(t) as Component;
            if (c != null) return c;
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // LIFECYCLE HOOK EXTRACTOR
    // -----------------------------------------------------------------------

    private void ExtractLifecycleHooks(List<Token> tokens, ScriptState state)
    {
        int i = 0;
        while (i < tokens.Count)
        {
            if (!(tokens[i].Type == TokenType.Keyword &&
                  tokens[i].Value.Equals("on", StringComparison.OrdinalIgnoreCase)))
            { i++; continue; }

            i++;
            if (i >= tokens.Count) break;
            string hookName = tokens[i].Value; i++;

            while (i < tokens.Count && tokens[i].Type == TokenType.EndOfLine) i++;
            if (i >= tokens.Count || tokens[i].Value != "{") continue;

            i++; // skip {
            var body = new List<Token>();
            int depth = 1;
            while (i < tokens.Count)
            {
                if (tokens[i].Value == "{") depth++;
                if (tokens[i].Value == "}") { depth--; if (depth <= 0) { i++; break; } }
                body.Add(tokens[i++]);
            }

            if (hookName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                state.UpdateBody = body;
            else if (hookName.Equals("Start", StringComparison.OrdinalIgnoreCase))
                StartCoroutine(RunStartHook(body, state));
        }
    }

    private IEnumerator RunStartHook(List<Token> body, ScriptState state)
    {
        ScriptState saved = _current;
        _current = state;
        IntRef idx = new IntRef(0);
        yield return StartCoroutine(Execute(body, idx));
        _current = saved;
    }

    // -----------------------------------------------------------------------
    // TOKENIZER
    // -----------------------------------------------------------------------

    private List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        string[] lines = source.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        for (int lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            string line = lines[lineNum].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            int col = 0;
            while (col < line.Length)
            {
                if (char.IsWhiteSpace(line[col])) { col++; continue; }

                // Comment
                if (col + 1 < line.Length && line[col] == '/' && line[col + 1] == '/')
                { tokens.Add(T(TokenType.Comment, line.Substring(col + 2).Trim(), lineNum + 1)); break; }

                // String literal
                if (line[col] == '"')
                {
                    int s = col + 1; col++;
                    while (col < line.Length && line[col] != '"') col++;
                    tokens.Add(T(TokenType.StringLiteral,
                        col < line.Length ? line.Substring(s, col - s) : line.Substring(s), lineNum + 1));
                    col++; continue;
                }

                // Number
                if (char.IsDigit(line[col]) ||
                    (line[col] == '-' && col + 1 < line.Length && char.IsDigit(line[col + 1])))
                {
                    int s = col;
                    if (line[col] == '-') col++;
                    while (col < line.Length && (char.IsDigit(line[col]) || line[col] == '.')) col++;
                    tokens.Add(T(TokenType.NumberLiteral, line.Substring(s, col - s), lineNum + 1));
                    continue;
                }

                // Word
                if (char.IsLetter(line[col]) || line[col] == '_')
                {
                    int s = col;
                    while (col < line.Length && (char.IsLetterOrDigit(line[col]) || line[col] == '_')) col++;
                    string word = line.Substring(s, col - s);

                    // dot chain: word.prop  or  word.method(
                    if (col < line.Length && line[col] == '.')
                    {
                        col++; // skip dot
                        int ps = col;
                        while (col < line.Length && (char.IsLetterOrDigit(line[col]) || line[col] == '_')) col++;
                        string prop = line.Substring(ps, col - ps);

                        // method call: word.method(args)  — keep as  word.method(args)
                        if (col < line.Length && line[col] == '(')
                        {
                            col++; // skip (
                            int as_ = col;
                            int depth = 1;
                            while (col < line.Length && depth > 0)
                            {
                                if (line[col] == '(') depth++;
                                if (line[col] == ')') depth--;
                                col++;
                            }
                            string argStr = line.Substring(as_, col - as_ - 1);
                            tokens.Add(T(TokenType.Identifier, $"{word}.{prop}({argStr})", lineNum + 1));
                            continue;
                        }

                        tokens.Add(T(TokenType.Identifier, word + "." + prop, lineNum + 1));
                        continue;
                    }

                    // bracket index: word[N]
                    if (col < line.Length && line[col] == '[')
                    {
                        col++;
                        int bs = col;
                        while (col < line.Length && line[col] != ']') col++;
                        string idx = col < line.Length ? line.Substring(bs, col - bs).Trim() : "";
                        if (col < line.Length) col++;
                        tokens.Add(T(TokenType.Identifier, word + "[" + idx + "]", lineNum + 1));
                        continue;
                    }

                    TokenType tt;
                    if (word.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                        word.Equals("no",  StringComparison.OrdinalIgnoreCase))
                        tt = TokenType.BoolLiteral;
                    else
                        tt = KEYWORDS.Contains(word) ? TokenType.Keyword : TokenType.Identifier;
                    tokens.Add(T(tt, word, lineNum + 1));
                    continue;
                }

                if (line[col] == '[') { tokens.Add(T(TokenType.Punctuation, "[", lineNum + 1)); col++; continue; }
                if (line[col] == ']') { tokens.Add(T(TokenType.Punctuation, "]", lineNum + 1)); col++; continue; }

                // Two-char operators
                if (col + 1 < line.Length)
                {
                    string two = line.Substring(col, 2);
                    if (two == "==" || two == "!=" || two == "<=" || two == ">=" || two == "&&" || two == "||")
                    { tokens.Add(T(TokenType.Operator, two, lineNum + 1)); col += 2; continue; }
                }

                if ("=+-*/!<>%".IndexOf(line[col]) >= 0)
                { tokens.Add(T(TokenType.Operator, line[col].ToString(), lineNum + 1)); col++; continue; }

                if ("(){},;".IndexOf(line[col]) >= 0)
                { tokens.Add(T(TokenType.Punctuation, line[col].ToString(), lineNum + 1)); col++; continue; }

                tokens.Add(T(TokenType.Unknown, line[col].ToString(), lineNum + 1));
                col++;
            }

            tokens.Add(T(TokenType.EndOfLine, "\n", lineNum + 1));
        }

        return tokens;
    }

    private static Token T(TokenType type, string value, int line)
        => new Token { Type = type, Value = value, Line = line };

    // -----------------------------------------------------------------------
    // ACTION REGISTRATION
    // -----------------------------------------------------------------------

    private void RegisterActions(List<Token> tokens, ScriptState state)
    {
        int i = 0;
        while (i < tokens.Count)
        {
            if (!IsKeyword(tokens[i], "action")) { i++; continue; }
            i++;
            if (i >= tokens.Count || tokens[i].Type != TokenType.Identifier) continue;
            string name = tokens[i++].Value;

            var parameters = new List<string>();
            if (i < tokens.Count && tokens[i].Value == "(")
            {
                i++;
                while (i < tokens.Count && tokens[i].Value != ")")
                { if (tokens[i].Type == TokenType.Identifier) parameters.Add(tokens[i].Value); i++; }
                if (i < tokens.Count) i++;
            }

            var body = new List<Token>();
            int depth = 0; bool started = false;
            while (i < tokens.Count)
            {
                if (tokens[i].Value == "{") { depth++; started = true; i++; continue; }
                if (tokens[i].Value == "}") { depth--; if (depth <= 0) { i++; break; } }
                if (started) body.Add(tokens[i]);
                i++;
            }

            state.Actions[name] = new SSAction { Name = name, Parameters = parameters, Body = body };
        }
    }

    // -----------------------------------------------------------------------
    // EXECUTOR
    // -----------------------------------------------------------------------

    private IEnumerator Execute(List<Token> tokens, IntRef i)
    {
        while (i.Value < tokens.Count && !_current.Variables.ContainsKey("__returning__"))
        {
            Token t = tokens[i.Value];

            if (t.Type == TokenType.Comment || t.Type == TokenType.EndOfLine)
            { i.Value++; continue; }

            if (IsKeyword(t, "say"))     { i.Value++; i.Value = ExecSay(tokens, i.Value);     continue; }
            if (IsKeyword(t, "variable")){ i.Value++; i.Value = ExecVarDecl(tokens, i.Value, false); continue; }
            if (IsKeyword(t, "hidden"))  { i.Value++; i.Value = ExecVarDecl(tokens, i.Value, true);  continue; }
            if (IsKeyword(t, "if"))      { yield return StartCoroutine(ExecIf(tokens, i));     continue; }
            if (IsKeyword(t, "loop"))    { yield return StartCoroutine(ExecLoopWhile(tokens, i)); continue; }
            if (IsKeyword(t, "for"))     { yield return StartCoroutine(ExecFor(tokens, i));    continue; }
            if (IsKeyword(t, "on"))      { i.Value++; i.Value = SkipOnBlock(tokens, i.Value);  continue; }
            if (IsKeyword(t, "action"))  { i.Value++; i.Value = SkipActionDef(tokens, i.Value); continue; }
            if (IsKeyword(t, "return"))  { i.Value++; i.Value = ExecReturn(tokens, i.Value);   continue; }
            if (IsKeyword(t, "enable"))  { i.Value++; i.Value = ExecEnableDisable(tokens, i.Value, true);  continue; }
            if (IsKeyword(t, "disable")) { i.Value++; i.Value = ExecEnableDisable(tokens, i.Value, false); continue; }
            if (IsKeyword(t, "destroy")) { i.Value++; i.Value = ExecDestroy(tokens, i.Value);  continue; }
            if (IsKeyword(t, "move"))    { i.Value++; i.Value = ExecMove(tokens, i.Value);     continue; }
            if (IsKeyword(t, "change"))  { i.Value++; i.Value = ExecChange(tokens, i.Value);   continue; }
            if (IsKeyword(t, "add"))     { i.Value++; i.Value = ExecAdd(tokens, i.Value);      continue; }
            if (IsKeyword(t, "remove"))  { i.Value++; i.Value = ExecRemove(tokens, i.Value);   continue; }

            if (IsKeyword(t, "wait"))
            {
                i.Value++;
                int ni = i.Value;
                float secs = ToFloat(EvalExpr(tokens, ref ni));
                if (ni < tokens.Count && IsKeyword(tokens[ni], "seconds")) ni++;
                i.Value = SkipToNextLine(tokens, ni);
                yield return new WaitForSeconds(secs);
                continue;
            }

            // cross-script method call:  varName.MethodOrAction(args)
            if (t.Type == TokenType.Identifier && IsScriptMethodCall(t.Value))
            {
                yield return StartCoroutine(ExecScriptMethodCall(t, i));
                continue;
            }

            // action call:  Name(
            if (t.Type == TokenType.Identifier && !t.Value.Contains(".") && !t.Value.Contains("[")
                && Peek(tokens, i.Value + 1) == "(")
            { yield return StartCoroutine(ExecActionCall(tokens, i)); continue; }

            // list index assignment: list[1] = value
            if (t.Type == TokenType.Identifier && t.Value.Contains("[") && Peek(tokens, i.Value + 1) == "=")
            {
                int ni = i.Value + 2;
                object val = EvalExpr(tokens, ref ni);
                SetListIndex(t.Value, val, t.Line);
                i.Value = SkipToNextLine(tokens, ni);
                continue;
            }

            // dot assignment: varName.prop = value  (script field/SS var/GO position)
            if (t.Type == TokenType.Identifier && t.Value.Contains(".") && Peek(tokens, i.Value + 1) == "=")
            {
                int ni = i.Value + 2;
                object val = EvalExpr(tokens, ref ni);
                SetDotProp(t.Value, val, t.Line);
                i.Value = SkipToNextLine(tokens, ni);
                continue;
            }

            // variable reassignment
            if (t.Type == TokenType.Identifier && Peek(tokens, i.Value + 1) == "=")
            {
                string varName = t.Value;
                int ni = i.Value + 2;
                object val = EvalExpr(tokens, ref ni);
                if (_current.Variables.ContainsKey(varName))
                {
                    _current.Variables[varName].Value = val;
                    _current.Script.VariableChangedCallback?.Invoke(varName, val);
                }
                else
                    Debug.LogWarning($"[SS] ({ScriptName()}:L{t.Line}) Undeclared variable '{varName}'");
                i.Value = SkipToNextLine(tokens, ni);
                continue;
            }

            if (t.Type == TokenType.Punctuation && t.Value == "}")
            { i.Value++; continue; }

            Debug.LogWarning($"[SS] ({ScriptName()}:L{t.Line}) Unknown token: '{t.Value}'");
            i.Value++;
        }
    }

    // -----------------------------------------------------------------------
    // CROSS-SCRIPT METHOD CALL  varName.Method(args)
    // -----------------------------------------------------------------------

    // Returns true if token looks like  identifier.Method(...)
    private bool IsScriptMethodCall(string tokenVal)
    {
        int dot = tokenVal.IndexOf('.');
        if (dot < 0) return false;
        string prop = tokenVal.Substring(dot + 1);
        return prop.EndsWith(")") && prop.Contains("(");
    }

    private IEnumerator ExecScriptMethodCall(Token t, IntRef i)
    {
        i.Value++;
        ParseDotMethod(t.Value, out string varName, out string methodName, out string[] rawArgs);

        // Evaluate argument expressions
        var args = new List<object>();
        foreach (string raw in rawArgs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            List<Token> argTokens = Tokenize(raw);
            int ai = 0;
            args.Add(EvalExpr(argTokens, ref ai));
        }

        object target = ResolveScriptTarget(varName, t.Line);
        if (target == null) yield break;

        if (target is SSScript targetSS)
        {
            // Try S# action first
            if (_states.TryGetValue(targetSS, out ScriptState targetState) &&
                targetState.Actions.ContainsKey(methodName))
            {
                targetSS.ActionCalledCallback?.Invoke(methodName, args.ToArray());
                ScriptState saved = _current;
                _current = targetState;
                yield return StartCoroutine(RunActionFromCSharp(targetState, targetState.Actions[methodName], args.ToArray()));
                _current = saved;
            }
            else
                Debug.LogWarning($"[SS] ({ScriptName()}:L{t.Line}) Action '{methodName}' not found on '{targetSS.scriptFile?.name}'");
        }
        else if (target is Component comp)
        {
            // Call C# method via reflection
            MethodInfo method = comp.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
            { Debug.LogWarning($"[SS] ({ScriptName()}:L{t.Line}) Method '{methodName}' not found on '{comp.GetType().Name}'"); yield break; }

            object[] convertedArgs = ConvertArgsForMethod(method, args);
            method.Invoke(comp, convertedArgs);
        }

        i.Value = SkipToNextLine(Tokenize(""), 0); // already advanced
    }

    // -----------------------------------------------------------------------
    // SAY
    // -----------------------------------------------------------------------

    private int ExecSay(List<Token> tokens, int i)
    {
        if (i >= tokens.Count || tokens[i].Type == TokenType.EndOfLine)
        { Debug.LogError($"[SS] 'say' expects a value"); return SkipToNextLine(tokens, i); }

        if (tokens[i].Type == TokenType.StringLiteral)
        { Debug.Log($"[{_current.Owner.name}] {tokens[i].Value}"); return SkipToNextLine(tokens, i + 1); }

        int ni = i;
        object result = EvalExpr(tokens, ref ni);
        Debug.Log($"[{_current.Owner.name}] {result}");
        return SkipToNextLine(tokens, ni);
    }

    // -----------------------------------------------------------------------
    // VARIABLE DECLARATION
    // -----------------------------------------------------------------------

    private int ExecVarDecl(List<Token> tokens, int i, bool hidden)
    {
        if (hidden)
        {
            if (i >= tokens.Count || !IsKeyword(tokens[i], "variable"))
            { Debug.LogError($"[SS] Expected 'variable' after 'hidden'"); return SkipToNextLine(tokens, i); }
            i++;
        }

        if (i >= tokens.Count) return SkipToNextLine(tokens, i);
        string type = tokens[i++].Value.ToLower();

        if (i >= tokens.Count || tokens[i].Type != TokenType.Identifier)
        { Debug.LogError($"[SS] Expected variable name after '{type}'"); return SkipToNextLine(tokens, i); }

        // script type:  variable script varName TypeName
        if (type == "script")
        {
            string varName  = tokens[i++].Value;
            string typeName = (i < tokens.Count && tokens[i].Type == TokenType.Identifier) ? tokens[i++].Value : "";

            // Don't overwrite inspector value
            if (!hidden && _current.Variables.ContainsKey(varName))
                return SkipToNextLine(tokens, i);

            if (hidden)
            {
                Component found = FindScriptByTypeName(typeName);
                _current.Variables[varName] = new SSVariable { Type = "script", Hidden = true, Value = found };
            }
            return SkipToNextLine(tokens, i);
        }

        string name = tokens[i++].Value;

        if (!hidden && _current.Variables.ContainsKey(name))
            return SkipToNextLine(tokens, i);

        object value = DefaultValue(type);
        if (i < tokens.Count && tokens[i].Value == "=")
        {
            i++;
            value = type == "list" ? (object)ParseListLiteral(tokens, ref i) : EvalExpr(tokens, ref i);
        }

        _current.Variables[name] = new SSVariable { Type = type, Hidden = hidden, Value = value };
        return SkipToNextLine(tokens, i);
    }

    // -----------------------------------------------------------------------
    // LIST helpers
    // -----------------------------------------------------------------------

    private List<object> ParseListLiteral(List<Token> tokens, ref int i)
    {
        var list = new List<object>();
        if (i >= tokens.Count || tokens[i].Value != "[") return list;
        i++;
        while (i < tokens.Count && tokens[i].Value != "]" && tokens[i].Type != TokenType.EndOfLine)
        {
            if (tokens[i].Value == ",") { i++; continue; }
            list.Add(EvalExpr(tokens, ref i));
        }
        if (i < tokens.Count && tokens[i].Value == "]") i++;
        return list;
    }

    private int ExecAdd(List<Token> tokens, int i)
    {
        int ni = i; object value = EvalExpr(tokens, ref ni); i = ni;
        if (i >= tokens.Count || !IsKeyword(tokens[i], "to"))
        { Debug.LogError($"[SS] 'add' expects: add value to listName"); return SkipToNextLine(tokens, i); }
        i++;
        if (i >= tokens.Count) return SkipToNextLine(tokens, i);
        string listName = tokens[i++].Value;
        if (_current.Variables.TryGetValue(listName, out SSVariable v) && v.Value is List<object> list)
            list.Add(value);
        else
            Debug.LogError($"[SS] '{listName}' is not a list");
        return SkipToNextLine(tokens, i);
    }

    private int ExecRemove(List<Token> tokens, int i)
    {
        int ni = i;
        int pos = (int)ToFloat(EvalExpr(tokens, ref ni)) - 1; i = ni;
        if (i >= tokens.Count || !IsKeyword(tokens[i], "from"))
        { Debug.LogError($"[SS] 'remove' expects: remove 1 from listName"); return SkipToNextLine(tokens, i); }
        i++;
        string listName = tokens[i++].Value;
        if (_current.Variables.TryGetValue(listName, out SSVariable v) && v.Value is List<object> list)
        {
            if (pos >= 0 && pos < list.Count) list.RemoveAt(pos);
            else Debug.LogWarning($"[SS] remove: index {pos + 1} out of range");
        }
        else Debug.LogError($"[SS] '{listName}' is not a list");
        return SkipToNextLine(tokens, i);
    }

    private void SetListIndex(string token, object value, int line)
    {
        ParseIndexToken(token, out string listName, out string indexStr);
        if (!_current.Variables.TryGetValue(listName, out SSVariable v) || !(v.Value is List<object> list))
        { Debug.LogError($"[SS] '{listName}' is not a list"); return; }
        int pos = ResolveIndex(indexStr) - 1;
        if (pos < 0 || pos >= list.Count)
        { Debug.LogWarning($"[SS] L{line}: index {pos + 1} out of range"); return; }
        list[pos] = value;
    }

    private object GetListIndex(string token, int line)
    {
        ParseIndexToken(token, out string listName, out string indexStr);
        if (!_current.Variables.TryGetValue(listName, out SSVariable v) || !(v.Value is List<object> list))
        { Debug.LogError($"[SS] L{line}: '{listName}' is not a list"); return null; }
        int pos = ResolveIndex(indexStr) - 1;
        if (pos < 0 || pos >= list.Count)
        { Debug.LogWarning($"[SS] L{line}: index {pos + 1} out of range"); return null; }
        return list[pos];
    }

    private void ParseIndexToken(string token, out string listName, out string indexStr)
    {
        int b = token.IndexOf('[');
        listName = token.Substring(0, b);
        indexStr = token.Substring(b + 1, token.Length - b - 2);
    }

    private int ResolveIndex(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        if (_current.Variables.TryGetValue(s, out SSVariable v)) return (int)ToFloat(v.Value);
        return 1;
    }

    // -----------------------------------------------------------------------
    // IF / ELSE IF / ELSE
    // -----------------------------------------------------------------------

    private IEnumerator ExecIf(List<Token> tokens, IntRef i)
    {
        i.Value++;
        int ci = i.Value;
        bool cond = ToBool(EvalExpr(tokens, ref ci));
        i.Value = ci;
        List<Token> ifBody = ReadBlock(tokens, i);

        if (cond)
        {
            IntRef bi = new IntRef(0);
            yield return StartCoroutine(Execute(ifBody, bi));
            SkipElseChain(tokens, i);
        }
        else
        {
            SkipEolWhitespace(tokens, i);
            if (i.Value < tokens.Count && IsKeyword(tokens[i.Value], "else"))
            {
                i.Value++;
                SkipEolWhitespace(tokens, i);
                if (i.Value < tokens.Count && IsKeyword(tokens[i.Value], "if"))
                    yield return StartCoroutine(ExecIf(tokens, i));
                else
                {
                    List<Token> elseBody = ReadBlock(tokens, i);
                    IntRef bi = new IntRef(0);
                    yield return StartCoroutine(Execute(elseBody, bi));
                }
            }
        }
    }

    private void SkipElseChain(List<Token> tokens, IntRef i)
    {
        while (true)
        {
            SkipEolWhitespace(tokens, i);
            if (i.Value >= tokens.Count || !IsKeyword(tokens[i.Value], "else")) break;
            i.Value++;
            SkipEolWhitespace(tokens, i);
            if (i.Value < tokens.Count && IsKeyword(tokens[i.Value], "if"))
            {
                i.Value++;
                while (i.Value < tokens.Count && tokens[i.Value].Value != "{" &&
                       tokens[i.Value].Type != TokenType.EndOfLine) i.Value++;
            }
            SkipBlockRaw(tokens, i);
        }
    }

    // -----------------------------------------------------------------------
    // LOOP WHILE
    // -----------------------------------------------------------------------

    private IEnumerator ExecLoopWhile(List<Token> tokens, IntRef i)
    {
        i.Value++;
        if (i.Value >= tokens.Count || !IsKeyword(tokens[i.Value], "while"))
        { Debug.LogError($"[SS] Expected 'while' after 'loop'"); i.Value = SkipToNextLine(tokens, i.Value); yield break; }
        i.Value++;

        int condStart = i.Value;
        int ci = condStart; EvalExpr(tokens, ref ci);
        int blockStart = ci;

        int safety = 100000, count = 0;
        while (true)
        {
            ci = condStart;
            if (!ToBool(EvalExpr(tokens, ref ci))) break;
            IntRef bi = new IntRef(blockStart);
            List<Token> body = ReadBlock(tokens, bi);
            IntRef bodyIdx = new IntRef(0);
            yield return StartCoroutine(Execute(body, bodyIdx));
            if (++count >= safety) { Debug.LogError($"[SS] loop while safety limit hit"); break; }
        }

        i.Value = blockStart;
        SkipBlockRaw(tokens, i);
    }

    // -----------------------------------------------------------------------
    // FOR
    // -----------------------------------------------------------------------

    private IEnumerator ExecFor(List<Token> tokens, IntRef i)
    {
        i.Value++;
        if (i.Value >= tokens.Count || tokens[i.Value].Type != TokenType.Identifier)
        { Debug.LogError($"[SS] 'for' expects: for i = 0 to 10"); i.Value = SkipToNextLine(tokens, i.Value); yield break; }
        string varName = tokens[i.Value++].Value;

        if (i.Value >= tokens.Count || tokens[i.Value].Value != "=")
        { Debug.LogError($"[SS] 'for': expected '='"); i.Value = SkipToNextLine(tokens, i.Value); yield break; }
        i.Value++;

        int ni = i.Value; float from = ToFloat(EvalExpr(tokens, ref ni)); i.Value = ni;
        if (!IsKeyword(tokens[i.Value], "to"))
        { Debug.LogError($"[SS] 'for': expected 'to'"); i.Value = SkipToNextLine(tokens, i.Value); yield break; }
        i.Value++;
        ni = i.Value; float to = ToFloat(EvalExpr(tokens, ref ni)); i.Value = ni;

        float step = 1f;
        if (i.Value < tokens.Count && IsKeyword(tokens[i.Value], "step"))
        { i.Value++; ni = i.Value; step = ToFloat(EvalExpr(tokens, ref ni)); i.Value = ni; }

        List<Token> body = ReadBlock(tokens, i);
        _current.Variables[varName] = new SSVariable { Type = "float", Hidden = true, Value = from };

        int safety = 100000, count = 0;
        float cur = from;
        while ((step > 0 && cur <= to) || (step < 0 && cur >= to))
        {
            _current.Variables[varName].Value = cur;
            IntRef bi = new IntRef(0);
            yield return StartCoroutine(Execute(body, bi));
            cur += step;
            if (++count >= safety) { Debug.LogError($"[SS] for loop safety limit hit"); break; }
        }
    }

    // -----------------------------------------------------------------------
    // RETURN
    // -----------------------------------------------------------------------

    private int ExecReturn(List<Token> tokens, int i)
    {
        object retVal = null;
        if (i < tokens.Count && tokens[i].Type != TokenType.EndOfLine)
        { int ni = i; retVal = EvalExpr(tokens, ref ni); i = ni; }
        _current.Variables["__returning__"] = new SSVariable { Type = "var", Hidden = true, Value = retVal };
        return SkipToNextLine(tokens, i);
    }

    // -----------------------------------------------------------------------
    // ENABLE / DISABLE / DESTROY / MOVE / CHANGE
    // -----------------------------------------------------------------------

    private int ExecEnableDisable(List<Token> tokens, int i, bool enable)
    {
        if (i >= tokens.Count) return SkipToNextLine(tokens, i);
        GameObject go = ResolveGO(tokens[i], tokens[i].Line); i++;
        if (go != null) go.SetActive(enable);
        return SkipToNextLine(tokens, i);
    }

    private int ExecDestroy(List<Token> tokens, int i)
    {
        if (i >= tokens.Count) return SkipToNextLine(tokens, i);
        GameObject go = ResolveGO(tokens[i], tokens[i].Line); i++;
        if (go != null) Destroy(go);
        return SkipToNextLine(tokens, i);
    }

    private int ExecMove(List<Token> tokens, int i)
    {
        if (i >= tokens.Count) return SkipToNextLine(tokens, i);
        GameObject go = ResolveGO(tokens[i], tokens[i].Line); i++;
        if (i >= tokens.Count || !IsKeyword(tokens[i], "to"))
        { Debug.LogError($"[SS] 'move' expects 'to'"); return SkipToNextLine(tokens, i); }
        i++;
        float x = ToFloat(EvalExpr(tokens, ref i));
        float y = ToFloat(EvalExpr(tokens, ref i));
        float z = ToFloat(EvalExpr(tokens, ref i));
        if (go != null) go.transform.position = new Vector3(x, y, z);
        return SkipToNextLine(tokens, i);
    }

    private int ExecChange(List<Token> tokens, int i)
    {
        if (i >= tokens.Count || !tokens[i].Value.Contains("."))
        { Debug.LogError($"[SS] 'change' expects: change cube.x by 5"); return SkipToNextLine(tokens, i); }
        string dot = tokens[i++].Value;
        if (i >= tokens.Count || !IsKeyword(tokens[i], "by"))
        { Debug.LogError($"[SS] 'change' expects 'by'"); return SkipToNextLine(tokens, i); }
        i++;
        float amount  = ToFloat(EvalExpr(tokens, ref i));
        float current = ToFloat(GetDotProp(dot, PeekLine(tokens, i)));
        SetDotProp(dot, current + amount, PeekLine(tokens, i));
        return SkipToNextLine(tokens, i);
    }

    // -----------------------------------------------------------------------
    // ACTION CALL (coroutine)
    // -----------------------------------------------------------------------

    private IEnumerator ExecActionCall(List<Token> tokens, IntRef i)
    {
        string name = tokens[i.Value++].Value;
        i.Value++; // skip (

        var args = new List<object>();
        while (i.Value < tokens.Count && tokens[i.Value].Value != ")")
        {
            if (tokens[i.Value].Value == ",") { i.Value++; continue; }
            int ni = i.Value;
            args.Add(EvalExpr(tokens, ref ni));
            i.Value = ni;
        }
        if (i.Value < tokens.Count) i.Value++;
        i.Value = SkipToNextLine(tokens, i.Value);

        if (!_current.Actions.TryGetValue(name, out SSAction action))
        { Debug.LogError($"[SS] Undefined action '{name}'"); yield break; }

        _current.Script.ActionCalledCallback?.Invoke(name, args.ToArray());

        var savedVars = new Dictionary<string, SSVariable>(_current.Variables, StringComparer.OrdinalIgnoreCase);
        _current.Variables.Remove("__returning__");

        for (int p = 0; p < action.Parameters.Count && p < args.Count; p++)
            _current.Variables[action.Parameters[p]] = new SSVariable { Type = "var", Hidden = true, Value = args[p] };

        IntRef bi = new IntRef(0);
        yield return StartCoroutine(Execute(action.Body, bi));

        _current.Variables = savedVars;
    }

    private int SkipActionDef(List<Token> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Value != "{") i++;
        int depth = 0;
        while (i < tokens.Count)
        {
            if (tokens[i].Value == "{") depth++;
            if (tokens[i].Value == "}") { depth--; if (depth <= 0) { i++; break; } }
            i++;
        }
        return i;
    }

    private int SkipOnBlock(List<Token> tokens, int i)
    {
        if (i < tokens.Count) i++;
        while (i < tokens.Count && tokens[i].Type == TokenType.EndOfLine) i++;
        if (i < tokens.Count && tokens[i].Value == "{")
        {
            int depth = 0;
            while (i < tokens.Count)
            {
                if (tokens[i].Value == "{") depth++;
                if (tokens[i].Value == "}") { depth--; if (depth <= 0) { i++; break; } }
                i++;
            }
        }
        return i;
    }

    // -----------------------------------------------------------------------
    // DOT PROPERTY — handles SS vars, C# fields/properties, GO position, strings, lists
    // -----------------------------------------------------------------------

    private object GetDotProp(string dot, int line)
    {
        int dotIdx = dot.IndexOf('.');
        string objName = dot.Substring(0, dotIdx);
        string prop    = dot.Substring(dotIdx + 1);

        // String properties
        if (_current.Variables.TryGetValue(objName, out SSVariable sv) && sv.Value is string strVal)
        {
            switch (prop.ToLower())
            {
                case "length":   return (float)strVal.Length;
                case "upper":    return strVal.ToUpper();
                case "lower":    return strVal.ToLower();
            }
        }

        // List properties
        if (_current.Variables.TryGetValue(objName, out SSVariable lv) && lv.Value is List<object> lst)
        {
            if (prop.ToLower() == "length") return (float)lst.Count;
        }

        // Script variable — try SS variable first, then C# reflection
        if (_current.Variables.TryGetValue(objName, out SSVariable scriptVar) && scriptVar.Value is Component comp)
        {
            // Is target an SSScript? Check its SS variables
            if (comp is SSScript targetSS && _states.TryGetValue(targetSS, out ScriptState targetState))
            {
                if (targetState.Variables.TryGetValue(prop, out SSVariable ssv))
                    return ssv.Value;
            }

            // Fall through to C# reflection
            return GetCSharpFieldOrProp(comp, prop, line);
        }

        // GameObject position
        GameObject go = ResolveGOByName(objName, line);
        if (go != null)
        {
            switch (prop.ToLower())
            {
                case "x": return go.transform.position.x;
                case "y": return go.transform.position.y;
                case "z": return go.transform.position.z;
            }
        }

        Debug.LogError($"[SS] L{line}: Unknown property '{prop}' on '{objName}'");
        return null;
    }

    private void SetDotProp(string dot, object value, int line)
    {
        int dotIdx = dot.IndexOf('.');
        string objName = dot.Substring(0, dotIdx);
        string prop    = dot.Substring(dotIdx + 1);

        // Script variable
        if (_current.Variables.TryGetValue(objName, out SSVariable scriptVar) && scriptVar.Value is Component comp)
        {
            if (comp is SSScript targetSS && _states.TryGetValue(targetSS, out ScriptState targetState))
            {
                if (targetState.Variables.ContainsKey(prop))
                {
                    targetState.Variables[prop].Value = value;
                    targetSS.VariableChangedCallback?.Invoke(prop, value);
                    return;
                }
            }
            SetCSharpFieldOrProp(comp, prop, value, line);
            return;
        }

        // GameObject position
        GameObject go = ResolveGOByName(objName, line);
        if (go != null)
        {
            float val = ToFloat(value);
            Vector3 pos = go.transform.position;
            switch (prop.ToLower())
            {
                case "x": pos.x = val; break;
                case "y": pos.y = val; break;
                case "z": pos.z = val; break;
                default: Debug.LogError($"[SS] L{line}: Unknown property '{prop}'"); return;
            }
            go.transform.position = pos;
        }
    }

    // -----------------------------------------------------------------------
    // C# REFLECTION HELPERS
    // -----------------------------------------------------------------------

    private object GetCSharpFieldOrProp(Component comp, string memberName, int line)
    {
        Type type = comp.GetType();

        FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null) return NormalizeCSharpValue(field.GetValue(comp));

        PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) return NormalizeCSharpValue(prop.GetValue(comp));

        Debug.LogWarning($"[SS] L{line}: Field/property '{memberName}' not found on '{type.Name}'");
        return null;
    }

    private void SetCSharpFieldOrProp(Component comp, string memberName, object value, int line)
    {
        Type type = comp.GetType();

        FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(comp, ConvertToType(value, field.FieldType));
            return;
        }

        PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(comp, ConvertToType(value, prop.PropertyType));
            return;
        }

        Debug.LogWarning($"[SS] L{line}: Field/property '{memberName}' not found or not writable on '{type.Name}'");
    }

    private object NormalizeCSharpValue(object val)
    {
        if (val is int i)    return (float)i;
        if (val is double d) return (float)d;
        if (val is bool b)   return b;
        if (val is string s) return s;
        if (val is float f)  return f;
        return val;
    }

    private object ConvertToType(object value, Type targetType)
    {
        if (targetType == typeof(float))  return ToFloat(value);
        if (targetType == typeof(int))    return (int)ToFloat(value);
        if (targetType == typeof(double)) return (double)ToFloat(value);
        if (targetType == typeof(bool))   return ToBool(value);
        if (targetType == typeof(string)) return value?.ToString() ?? "";
        return value;
    }

    private object[] ConvertArgsForMethod(MethodInfo method, List<object> args)
    {
        ParameterInfo[] parms = method.GetParameters();
        object[] result = new object[parms.Length];
        for (int p = 0; p < parms.Length; p++)
        {
            object raw = p < args.Count ? args[p] : null;
            result[p] = ConvertToType(raw, parms[p].ParameterType);
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // DOT METHOD CALL PARSER
    // -----------------------------------------------------------------------

    private void ParseDotMethod(string token, out string objName, out string methodName, out string[] args)
    {
        // format: objName.method(arg1, arg2)
        int dot    = token.IndexOf('.');
        int paren  = token.IndexOf('(');
        objName    = token.Substring(0, dot);
        methodName = token.Substring(dot + 1, paren - dot - 1);
        string argStr = token.Substring(paren + 1, token.Length - paren - 2);

        // Split args respecting nested parens/quotes
        var argList = new List<string>();
        int depth = 0; bool inStr = false; int start = 0;
        for (int c = 0; c < argStr.Length; c++)
        {
            if (argStr[c] == '"') inStr = !inStr;
            if (!inStr)
            {
                if (argStr[c] == '(') depth++;
                if (argStr[c] == ')') depth--;
                if (argStr[c] == ',' && depth == 0)
                { argList.Add(argStr.Substring(start, c - start).Trim()); start = c + 1; }
            }
        }
        if (start < argStr.Length) argList.Add(argStr.Substring(start).Trim());
        args = argList.ToArray();
    }

    // -----------------------------------------------------------------------
    // SCRIPT TARGET RESOLVER
    // -----------------------------------------------------------------------

    private object ResolveScriptTarget(string varName, int line)
    {
        if (_current.Variables.TryGetValue(varName, out SSVariable v))
        {
            if (v.Value is SSScript ss) return ss;
            if (v.Value is Component c) return c;
            Debug.LogWarning($"[SS] L{line}: '{varName}' is not a script reference");
            return null;
        }
        Debug.LogWarning($"[SS] L{line}: '{varName}' not declared");
        return null;
    }

    // -----------------------------------------------------------------------
    // GAMEOBJECT RESOLVERS
    // -----------------------------------------------------------------------

    private GameObject ResolveGO(Token t, int line)
    {
        if (t.Type == TokenType.Identifier && _current.Variables.TryGetValue(t.Value, out SSVariable v))
        {
            if (v.Value is GameObject go) return go;
            Debug.LogError($"[SS] L{line}: '{t.Value}' is not a gameObject"); return null;
        }
        if (t.Type == TokenType.StringLiteral || t.Type == TokenType.Identifier)
        {
            GameObject found = GameObject.Find(t.Value);
            if (found == null) Debug.LogWarning($"[SS] L{line}: GameObject '{t.Value}' not found");
            return found;
        }
        Debug.LogError($"[SS] L{line}: Expected a GameObject"); return null;
    }

    private GameObject ResolveGOByName(string name, int line)
    {
        if (_current.Variables.TryGetValue(name, out SSVariable v) && v.Value is GameObject go) return go;
        GameObject found = GameObject.Find(name);
        if (found == null) Debug.LogWarning($"[SS] L{line}: GameObject '{name}' not found");
        return found;
    }

    // -----------------------------------------------------------------------
    // EXPRESSION EVALUATOR
    // -----------------------------------------------------------------------

    private object EvalExpr(List<Token> tokens, ref int i) => ParseOr(tokens, ref i);

    private object ParseOr(List<Token> tokens, ref int i)
    {
        object left = ParseAnd(tokens, ref i);
        while (i < tokens.Count && tokens[i].Value == "||")
        { i++; left = ToBool(left) || ToBool(ParseAnd(tokens, ref i)); }
        return left;
    }

    private object ParseAnd(List<Token> tokens, ref int i)
    {
        object left = ParseEq(tokens, ref i);
        while (i < tokens.Count && tokens[i].Value == "&&")
        { i++; left = ToBool(left) && ToBool(ParseEq(tokens, ref i)); }
        return left;
    }

    private object ParseEq(List<Token> tokens, ref int i)
    {
        object left = ParseCmp(tokens, ref i);
        while (i < tokens.Count && (tokens[i].Value == "==" || tokens[i].Value == "!="))
        {
            string op = tokens[i++].Value;
            object right = ParseCmp(tokens, ref i);
            left = op == "==" ? ObjEq(left, right) : !ObjEq(left, right);
        }
        return left;
    }

    private object ParseCmp(List<Token> tokens, ref int i)
    {
        object left = ParseAddSub(tokens, ref i);
        while (i < tokens.Count && (tokens[i].Value == "<" || tokens[i].Value == ">" ||
               tokens[i].Value == "<=" || tokens[i].Value == ">="))
        {
            string op = tokens[i++].Value;
            float l = ToFloat(left), r = ToFloat(ParseAddSub(tokens, ref i));
            left = op switch { "<" => l < r, ">" => l > r, "<=" => l <= r, ">=" => l >= r, _ => (object)false };
        }
        return left;
    }

    private object ParseAddSub(List<Token> tokens, ref int i)
    {
        object left = ParseMulDiv(tokens, ref i);
        while (i < tokens.Count && (tokens[i].Value == "+" || tokens[i].Value == "-"))
        {
            string op = tokens[i++].Value;
            object right = ParseMulDiv(tokens, ref i);
            if (left is string || right is string) left = $"{left}{right}";
            else { float l = ToFloat(left), r = ToFloat(right); left = op == "+" ? l + r : l - r; }
        }
        return left;
    }

    private object ParseMulDiv(List<Token> tokens, ref int i)
    {
        object left = ParseUnary(tokens, ref i);
        while (i < tokens.Count && (tokens[i].Value == "*" || tokens[i].Value == "/" || tokens[i].Value == "%"))
        {
            string op = tokens[i++].Value;
            float l = ToFloat(left), r = ToFloat(ParseUnary(tokens, ref i));
            if (op == "/" && r == 0f) { Debug.LogError($"[SS] Division by zero"); left = 0f; continue; }
            left = op switch { "*" => l * r, "/" => l / r, "%" => l % r, _ => (object)0f };
        }
        return left;
    }

    private object ParseUnary(List<Token> tokens, ref int i)
    {
        if (i < tokens.Count && tokens[i].Value == "!") { i++; return !ToBool(ParsePrimary(tokens, ref i)); }
        if (i < tokens.Count && tokens[i].Value == "-") { i++; return -ToFloat(ParsePrimary(tokens, ref i)); }
        return ParsePrimary(tokens, ref i);
    }

    private object ParsePrimary(List<Token> tokens, ref int i)
    {
        if (i >= tokens.Count) return null;
        Token t = tokens[i];

        if (t.Value == "(")
        {
            i++;
            object val = EvalExpr(tokens, ref i);
            if (i < tokens.Count && tokens[i].Value == ")") i++;
            return val;
        }

        if (IsKeyword(t, "isActive"))
        {
            i++;
            if (i >= tokens.Count) return false;
            GameObject go = ResolveGO(tokens[i], tokens[i].Line);
            i++;
            return go != null && go.activeSelf;
        }

        // cross-script method call returning a value: myPlayer.GetHealth()
        if (t.Type == TokenType.Identifier && IsScriptMethodCall(t.Value))
        {
            i++;
            ParseDotMethod(t.Value, out string objName, out string methodName, out string[] rawArgs);

            var args = new List<object>();
            foreach (string raw in rawArgs)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                List<Token> argTokens = Tokenize(raw);
                int ai = 0;
                args.Add(EvalExpr(argTokens, ref ai));
            }

            object target = ResolveScriptTarget(objName, t.Line);
            if (target == null) return null;

            if (target is SSScript targetSS)
            {
                // Try S# action with return value
                if (_states.TryGetValue(targetSS, out ScriptState targetState) &&
                    targetState.Actions.ContainsKey(methodName))
                {
                    targetSS.ActionCalledCallback?.Invoke(methodName, args.ToArray());
                    // Run synchronously by executing action body inline (no coroutine in expression context)
                    var savedVars = new Dictionary<string, SSVariable>(targetState.Variables, StringComparer.OrdinalIgnoreCase);
                    ScriptState savedCurrent = _current;
                    _current = targetState;
                    targetState.Variables.Remove("__returning__");

                    SSAction action = targetState.Actions[methodName];
                    for (int p = 0; p < action.Parameters.Count && p < args.Count; p++)
                        targetState.Variables[action.Parameters[p]] = new SSVariable { Type = "var", Hidden = true, Value = args[p] };

                    // Execute body synchronously (no waits in expression context)
                    IntRef bi = new IntRef(0);
                    ExecuteSync(action.Body, bi);

                    object retVal = targetState.Variables.TryGetValue("__returning__", out SSVariable rv) ? rv.Value : null;
                    targetState.Variables = savedVars;
                    _current = savedCurrent;
                    return NormalizeCSharpValue(retVal);
                }
            }
            else if (target is Component comp)
            {
                MethodInfo method = comp.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                if (method == null)
                { Debug.LogWarning($"[SS] L{t.Line}: Method '{methodName}' not found on '{comp.GetType().Name}'"); return null; }
                object[] converted = ConvertArgsForMethod(method, args);
                object result = method.Invoke(comp, converted);
                return NormalizeCSharpValue(result);
            }

            return null;
        }

        // dot property (field, SS var, GO position, string/list prop)
        if (t.Type == TokenType.Identifier && t.Value.Contains("."))
        {
            int dotIdx = t.Value.IndexOf('.');
            string prop = t.Value.Substring(dotIdx + 1).ToLower();
            string objName = t.Value.Substring(0, dotIdx);

            if (prop == "contains")
            {
                i++;
                if (i < tokens.Count && tokens[i].Value == "(")
                {
                    i++;
                    string search = "";
                    if (i < tokens.Count && tokens[i].Type == TokenType.StringLiteral)
                    { search = tokens[i].Value; i++; }
                    if (i < tokens.Count && tokens[i].Value == ")") i++;
                    if (_current.Variables.TryGetValue(objName, out SSVariable csv) && csv.Value is string sv)
                        return sv.Contains(search);
                    return false;
                }
            }

            i++;
            return GetDotProp(t.Value, t.Line);
        }

        // list index
        if (t.Type == TokenType.Identifier && t.Value.Contains("["))
        { i++; return GetListIndex(t.Value, t.Line); }

        i++;
        switch (t.Type)
        {
            case TokenType.StringLiteral: return t.Value;
            case TokenType.NumberLiteral:
                return float.TryParse(t.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f) ? (object)f : t.Value;
            case TokenType.BoolLiteral:
                return t.Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            case TokenType.Keyword when t.Value.Equals("null", StringComparison.OrdinalIgnoreCase):
                return null;
            case TokenType.Identifier:
                if (_current.Variables.TryGetValue(t.Value, out SSVariable v)) return v.Value;
                Debug.LogWarning($"[SS] L{t.Line}: Undefined variable '{t.Value}'");
                return null;
            default: return t.Value;
        }
    }

    // Synchronous executor for expression contexts (no coroutine/wait support)
    private void ExecuteSync(List<Token> tokens, IntRef i)
    {
        while (i.Value < tokens.Count && !_current.Variables.ContainsKey("__returning__"))
        {
            Token t = tokens[i.Value];
            if (t.Type == TokenType.Comment || t.Type == TokenType.EndOfLine) { i.Value++; continue; }
            if (IsKeyword(t, "return")) { i.Value++; i.Value = ExecReturn(tokens, i.Value); continue; }
            if (IsKeyword(t, "say"))    { i.Value++; i.Value = ExecSay(tokens, i.Value);    continue; }
            if (IsKeyword(t, "variable")) { i.Value++; i.Value = ExecVarDecl(tokens, i.Value, false); continue; }
            if (IsKeyword(t, "hidden"))   { i.Value++; i.Value = ExecVarDecl(tokens, i.Value, true);  continue; }
            if (t.Type == TokenType.Identifier && Peek(tokens, i.Value + 1) == "=")
            {
                string varName = t.Value; int ni = i.Value + 2;
                object val = EvalExpr(tokens, ref ni);
                if (_current.Variables.ContainsKey(varName))
                { _current.Variables[varName].Value = val; _current.Script.VariableChangedCallback?.Invoke(varName, val); }
                i.Value = SkipToNextLine(tokens, ni); continue;
            }
            if (t.Type == TokenType.Punctuation && t.Value == "}") { i.Value++; continue; }
            i.Value++;
        }
    }

    // -----------------------------------------------------------------------
    // BLOCK READER
    // -----------------------------------------------------------------------

    private List<Token> ReadBlock(List<Token> tokens, IntRef i)
    {
        SkipEolWhitespace(tokens, i);
        if (i.Value >= tokens.Count || tokens[i.Value].Value != "{")
        {
            var single = new List<Token>();
            while (i.Value < tokens.Count && tokens[i.Value].Type != TokenType.EndOfLine)
                single.Add(tokens[i.Value++]);
            single.Add(T(TokenType.EndOfLine, "\n", 0));
            if (i.Value < tokens.Count) i.Value++;
            return single;
        }
        i.Value++;
        var body = new List<Token>(); int depth = 1;
        while (i.Value < tokens.Count)
        {
            if (tokens[i.Value].Value == "{") depth++;
            if (tokens[i.Value].Value == "}") { depth--; if (depth <= 0) { i.Value++; break; } }
            body.Add(tokens[i.Value++]);
        }
        return body;
    }

    private void SkipBlockRaw(List<Token> tokens, IntRef i)
    {
        SkipEolWhitespace(tokens, i);
        if (i.Value >= tokens.Count || tokens[i.Value].Value != "{")
        {
            while (i.Value < tokens.Count && tokens[i.Value].Type != TokenType.EndOfLine) i.Value++;
            if (i.Value < tokens.Count) i.Value++; return;
        }
        i.Value++; int depth = 1;
        while (i.Value < tokens.Count)
        {
            if (tokens[i.Value].Value == "{") depth++;
            if (tokens[i.Value].Value == "}") { depth--; if (depth <= 0) { i.Value++; break; } }
            i.Value++;
        }
    }

    // -----------------------------------------------------------------------
    // HELPERS
    // -----------------------------------------------------------------------

    private object DefaultValue(string type)
    {
        switch (type)
        {
            case "int": case "float": return 0f;
            case "bool":       return false;
            case "string":     return "";
            case "gameobject": return null;
            case "list":       return new List<object>();
            case "script":     return null;
            default:           return null;
        }
    }

    private float ToFloat(object v)
    {
        if (v is float f)  return f;
        if (v is bool b)   return b ? 1f : 0f;
        if (v is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float pf)) return pf;
        return 0f;
    }

    private bool ToBool(object v)
    {
        if (v is bool b)   return b;
        if (v is float f)  return f != 0f;
        if (v is string s) return !string.IsNullOrEmpty(s);
        return v != null;
    }

    private bool ObjEq(object a, object b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is float fa && b is float fb) return MathF.Abs(fa - fb) < 0.0001f;
        return a.ToString().Equals(b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsKeyword(Token t, string kw)
        => t.Type == TokenType.Keyword && t.Value.Equals(kw, StringComparison.OrdinalIgnoreCase);

    private string Peek(List<Token> tokens, int i)
        => i < tokens.Count ? tokens[i].Value : "";

    private int PeekLine(List<Token> tokens, int i)
        => i < tokens.Count ? tokens[i].Line : -1;

    private void SkipEolWhitespace(List<Token> tokens, IntRef i)
    {
        while (i.Value < tokens.Count && tokens[i.Value].Type == TokenType.EndOfLine) i.Value++;
    }

    private int SkipToNextLine(List<Token> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type != TokenType.EndOfLine) i++;
        return i + 1 < tokens.Count ? i + 1 : i;
    }

    private string ScriptName()
        => _current?.Script?.scriptFile?.name ?? "unknown";
}

// ---------------------------------------------------------------------------
// Inspector button
// ---------------------------------------------------------------------------
#if UNITY_EDITOR
[CustomEditor(typeof(SSProcessor))]
public class SSProcessorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GUILayout.Space(8);
        if (GUILayout.Button("▶  Run S# Scripts Now", GUILayout.Height(30)))
            ((SSProcessor)target).RunAll();
    }
}
#endif