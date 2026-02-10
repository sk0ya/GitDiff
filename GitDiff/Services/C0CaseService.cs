using System.Text.RegularExpressions;
using GitDiff.Models;

namespace GitDiff.Services;

public class C0CaseService : IC0CaseService
{
    private static readonly Regex ClassRegex = new(@"(?:^|\s)(?:class|struct|record)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex MethodRegex = new(
        @"^(?:\s*)(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|partial|new|readonly|extern|unsafe|\s)*" +
        @"[\w<>\[\]?,\s]+\s+(\w+)\s*\(",
        RegexOptions.Compiled);
    private static readonly string[] ControlFlowKeywords = ["if", "else", "for", "foreach", "while", "do", "switch", "catch", "using", "lock", "fixed"];
    private static readonly string[] NonMethodKeywords = ["if", "else", "for", "foreach", "while", "do", "switch", "catch", "using", "lock", "fixed", "return", "throw", "yield", "var", "new", "await", "nameof", "typeof", "sizeof", "default", "checked", "unchecked"];

    public IReadOnlyList<C0TestCase> GenerateC0Cases(string fileContent, IReadOnlyList<int> changedLines)
    {
        var lines = fileContent.Split('\n');
        var changedLineSet = new HashSet<int>(changedLines);

        // Step 1: Parse the file to build line-to-context mapping
        var lineContexts = ParseFileStructure(lines);

        // Step 2: Find methods that contain changed lines
        var changedMethods = new HashSet<string>();
        foreach (var lineNum in changedLines)
        {
            if (lineNum >= 1 && lineNum <= lineContexts.Length)
            {
                var ctx = lineContexts[lineNum - 1];
                if (ctx.ClassName != null && ctx.MethodName != null)
                {
                    changedMethods.Add($"{ctx.ClassName}.{ctx.MethodName}");
                }
            }
        }

        // Step 3: Collect branch conditions in changed methods where the branch
        //         itself or its block contains changed lines
        var results = new List<C0TestCase>();
        var seen = new HashSet<string>();

        foreach (var branch in CollectBranches(lines, lineContexts, changedLineSet, changedMethods))
        {
            var key = $"{branch.ClassName}\t{branch.MethodName}\t{branch.BranchCondition}";
            if (seen.Add(key))
            {
                results.Add(branch);
            }
        }

        return results;
    }

    private record LineContext(string? ClassName, string? MethodName);

    private LineContext[] ParseFileStructure(string[] lines)
    {
        var contexts = new LineContext[lines.Length];
        var scopeStack = new Stack<ScopeInfo>();
        string? currentClass = null;
        string? currentMethod = null;
        var braceDepth = 0;
        var inString = false;
        var inVerbatimString = false;
        var inSingleLineComment = false;
        var inMultiLineComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Skip preprocessor directives
            if (trimmed.StartsWith('#'))
            {
                contexts[i] = new LineContext(currentClass, currentMethod);
                continue;
            }

            // Analyze line for declarations (using the raw trimmed line before brace counting)
            var strippedLine = StripCommentsAndStrings(trimmed);

            // Check for class/struct/record declaration
            var classMatch = ClassRegex.Match(strippedLine);
            if (classMatch.Success && !IsInsideString(trimmed))
            {
                var className = classMatch.Groups[1].Value;
                scopeStack.Push(new ScopeInfo("class", className, braceDepth, currentClass, currentMethod));
                currentClass = className;
                currentMethod = null;
            }
            else
            {
                // Check for method declaration
                var methodName = TryParseMethodName(strippedLine);
                if (methodName != null && currentClass != null)
                {
                    scopeStack.Push(new ScopeInfo("method", methodName, braceDepth, currentClass, currentMethod));
                    currentMethod = methodName;
                }
            }

            // Count braces to track scope
            var depthBefore = braceDepth;
            CountBraces(line, ref braceDepth, ref inString, ref inVerbatimString, ref inSingleLineComment, ref inMultiLineComment);

            // Check if any scopes closed
            while (scopeStack.Count > 0 && braceDepth <= scopeStack.Peek().BraceDepth && braceDepth < depthBefore)
            {
                var closed = scopeStack.Pop();
                if (closed.Type == "method")
                {
                    currentMethod = closed.PreviousMethod;
                }
                else if (closed.Type == "class")
                {
                    currentClass = closed.PreviousClass;
                    currentMethod = closed.PreviousMethod;
                }
            }

            // Reset single-line comment flag
            inSingleLineComment = false;

            contexts[i] = new LineContext(currentClass, currentMethod);
        }

        return contexts;
    }

    private record ScopeInfo(string Type, string Name, int BraceDepth, string? PreviousClass, string? PreviousMethod);

    private IEnumerable<C0TestCase> CollectBranches(
        string[] lines, LineContext[] lineContexts, HashSet<int> changedLineSet, HashSet<string> changedMethods)
    {
        var branchStack = new Stack<BranchInfo>();
        var braceDepth = 0;
        var inString = false;
        var inVerbatimString = false;
        var inSingleLineComment = false;
        var inMultiLineComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNum = i + 1;
            var ctx = lineContexts[i];
            if (ctx.ClassName == null || ctx.MethodName == null)
            {
                CountBraces(lines[i], ref braceDepth, ref inString, ref inVerbatimString, ref inSingleLineComment, ref inMultiLineComment);
                inSingleLineComment = false;
                continue;
            }

            var methodKey = $"{ctx.ClassName}.{ctx.MethodName}";
            if (!changedMethods.Contains(methodKey))
            {
                CountBraces(lines[i], ref braceDepth, ref inString, ref inVerbatimString, ref inSingleLineComment, ref inMultiLineComment);
                inSingleLineComment = false;
                continue;
            }

            var trimmed = lines[i].TrimStart();
            var strippedLine = StripCommentsAndStrings(trimmed);

            var branchCondition = TryParseBranchCondition(strippedLine, trimmed);
            if (branchCondition != null)
            {
                branchStack.Push(new BranchInfo(ctx.ClassName, ctx.MethodName, branchCondition, lineNum, braceDepth));
            }

            var depthBefore = braceDepth;
            CountBraces(lines[i], ref braceDepth, ref inString, ref inVerbatimString, ref inSingleLineComment, ref inMultiLineComment);
            inSingleLineComment = false;

            // Pop closed branches
            while (branchStack.Count > 0 && braceDepth <= branchStack.Peek().BraceDepth && braceDepth < depthBefore)
            {
                var closedBranch = branchStack.Pop();
                var hasChangedLine = false;
                for (var line = closedBranch.StartLine; line <= lineNum; line++)
                {
                    if (changedLineSet.Contains(line))
                    {
                        hasChangedLine = true;
                        break;
                    }
                }

                if (hasChangedLine)
                {
                    foreach (var c in ExpandBranchToC0Cases(closedBranch))
                        yield return c;
                }
            }

            // Branch condition line itself is changed
            if (branchCondition != null && changedLineSet.Contains(lineNum))
            {
                foreach (var c in ExpandBranchToC0Cases(
                    new BranchInfo(ctx.ClassName, ctx.MethodName, branchCondition, lineNum, braceDepth)))
                    yield return c;
            }
        }

        // Handle remaining open branches
        while (branchStack.Count > 0)
        {
            var openBranch = branchStack.Pop();
            var hasChangedLine = false;
            for (var line = openBranch.StartLine; line <= lines.Length; line++)
            {
                if (changedLineSet.Contains(line))
                {
                    hasChangedLine = true;
                    break;
                }
            }

            if (hasChangedLine)
            {
                foreach (var c in ExpandBranchToC0Cases(openBranch))
                    yield return c;
            }
        }
    }

    /// <summary>
    /// if / else if → true行 + false行の2行を生成
    /// else → 前のifのfalseケースに該当するためスキップ
    /// switch / case / default → そのまま1行
    /// </summary>
    private static IEnumerable<C0TestCase> ExpandBranchToC0Cases(BranchInfo branch)
    {
        var cond = branch.Condition;

        if (cond.StartsWith("if (") || cond.StartsWith("if(")
            || cond.StartsWith("else if (") || cond.StartsWith("else if("))
        {
            yield return new C0TestCase(branch.ClassName, branch.MethodName, $"{cond} → true");
            yield return new C0TestCase(branch.ClassName, branch.MethodName, $"{cond} → false");
        }
        else if (cond == "else")
        {
            // else is the false path of the preceding if — skip to avoid duplication
        }
        else
        {
            // switch, case, default
            yield return new C0TestCase(branch.ClassName, branch.MethodName, cond);
        }
    }

    private record BranchInfo(string ClassName, string MethodName, string Condition, int StartLine, int BraceDepth);

    private static string? TryParseBranchCondition(string strippedLine, string originalTrimmed)
    {
        // else if (...) — must check before "else" and "if" individually
        if (Regex.IsMatch(strippedLine, @"^else\s+if\s*\("))
        {
            var condition = ExtractCondition(originalTrimmed, @"else\s+if\s*\(");
            return condition != null ? $"else if ({condition})" : "else if (...)";
        }

        // if (...)
        if (Regex.IsMatch(strippedLine, @"^if\s*\("))
        {
            var condition = ExtractCondition(originalTrimmed, @"if\s*\(");
            return condition != null ? $"if ({condition})" : "if (...)";
        }

        // else (standalone)
        if (Regex.IsMatch(strippedLine, @"^else\s*\{?$") || strippedLine == "else")
        {
            return "else";
        }

        // switch (...)
        if (Regex.IsMatch(strippedLine, @"^switch\s*\("))
        {
            var condition = ExtractCondition(originalTrimmed, @"switch\s*\(");
            return condition != null ? $"switch ({condition})" : "switch (...)";
        }

        // case ...:
        var caseMatch = Regex.Match(strippedLine, @"^case\s+(.+?):");
        if (caseMatch.Success)
        {
            return $"case {caseMatch.Groups[1].Value.Trim()}:";
        }

        // default:
        if (Regex.IsMatch(strippedLine, @"^default\s*:"))
        {
            return "default:";
        }

        return null;
    }

    private static string? ExtractCondition(string line, string prefix)
    {
        var match = Regex.Match(line, prefix);
        if (!match.Success) return null;

        var startIdx = match.Index + match.Length;
        var depth = 1;
        var end = startIdx;

        while (end < line.Length && depth > 0)
        {
            if (line[end] == '(') depth++;
            else if (line[end] == ')') depth--;
            if (depth > 0) end++;
        }

        if (depth == 0)
        {
            return line[startIdx..end].Trim();
        }

        return null;
    }

    private static string? TryParseMethodName(string strippedLine)
    {
        // Skip lines that are clearly not method declarations
        if (string.IsNullOrWhiteSpace(strippedLine)) return null;
        if (strippedLine.StartsWith("//") || strippedLine.StartsWith("/*")) return null;
        if (strippedLine.StartsWith("using ") || strippedLine.StartsWith("namespace ")) return null;

        // Must not be a control flow keyword
        foreach (var keyword in NonMethodKeywords)
        {
            if (strippedLine.StartsWith(keyword + " ") || strippedLine.StartsWith(keyword + "("))
                return null;
        }

        // Skip property/field/event-like lines
        if (strippedLine.Contains("=>") && !strippedLine.Contains("(")) return null;

        var match = MethodRegex.Match(strippedLine);
        if (!match.Success) return null;

        var name = match.Groups[1].Value;

        // Exclude class/struct/record/enum/interface declarations
        if (Regex.IsMatch(strippedLine, @"\b(?:class|struct|record|enum|interface)\s+" + Regex.Escape(name)))
            return null;

        // Exclude known non-method patterns
        if (name is "get" or "set" or "init" or "add" or "remove")
            return null;

        return name;
    }

    private static string StripCommentsAndStrings(string line)
    {
        // Remove string literals and comments for analysis
        var result = Regex.Replace(line, @"""[^""\\]*(?:\\.[^""\\]*)*""", "\"\"");   // regular strings
        result = Regex.Replace(result, @"@""[^""]*(?:""""[^""]*)*""", "\"\"");       // verbatim strings
        result = Regex.Replace(result, @"'[^'\\]*(?:\\.[^'\\]*)*'", "''");           // char literals
        result = Regex.Replace(result, @"//.*$", "");                                // single-line comment
        result = Regex.Replace(result, @"/\*.*?\*/", "");                            // inline multi-line comment
        return result;
    }

    private static bool IsInsideString(string line)
    {
        // Simple heuristic: check if "class"/"struct"/"record" appears inside a string
        return false;
    }

    private static void CountBraces(string line, ref int depth,
        ref bool inString, ref bool inVerbatimString,
        ref bool inSingleLineComment, ref bool inMultiLineComment)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inMultiLineComment)
            {
                if (c == '*' && next == '/')
                {
                    inMultiLineComment = false;
                    i++;
                }
                continue;
            }

            if (inSingleLineComment) continue;

            if (inVerbatimString)
            {
                if (c == '"')
                {
                    if (next == '"')
                        i++; // escaped quote in verbatim string
                    else
                        inVerbatimString = false;
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                    i++; // skip escaped char
                else if (c == '"')
                    inString = false;
                continue;
            }

            // Not in any string or comment
            if (c == '/' && next == '/')
            {
                inSingleLineComment = true;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inMultiLineComment = true;
                i++;
                continue;
            }
            if (c == '@' && next == '"')
            {
                inVerbatimString = true;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            if (c == '\'')
            {
                // Char literal — skip next char(s)
                if (i + 2 < line.Length && line[i + 1] == '\\' && i + 3 < line.Length && line[i + 3] == '\'')
                    i += 3;
                else if (i + 2 < line.Length && line[i + 2] == '\'')
                    i += 2;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
    }
}
