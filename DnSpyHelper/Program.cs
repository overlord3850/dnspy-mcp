using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// Safe stdout — only set encoding when NOT piped (avoids SetConsoleOutputCP crash)
if (!Console.IsOutputRedirected)
    Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DnSpyHelper <command> <assembly> [args...]");
    Console.Error.WriteLine("Commands: list-types, decompile-type, decompile-type-il,");
    Console.Error.WriteLine("          decompile-method, decompile-method-il, list-strings,");
    Console.Error.WriteLine("          find-callers, get-token, list-attributes");
    return 1;
}

string command = args[0].ToLowerInvariant();
string asmPath = args[1];

try
{
    return command switch
    {
        "list-types"          => ListTypes(asmPath),
        "decompile-type"      => DecompileType(asmPath, RequireArg(args, 2, "type"), il: false),
        "decompile-type-il"   => DecompileType(asmPath, RequireArg(args, 2, "type"), il: true),
        "decompile-method"    => DecompileMethod(asmPath, RequireArg(args, 2, "type"), RequireArg(args, 3, "method"), il: false),
        "decompile-method-il" => DecompileMethod(asmPath, RequireArg(args, 2, "type"), RequireArg(args, 3, "method"), il: true),
        "list-strings"        => ListStrings(asmPath, args.Length > 2 ? args[2] : ""),
        "find-callers"        => FindCallers(asmPath, RequireArg(args, 2, "type"), RequireArg(args, 3, "method")),
        "get-token"           => GetToken(asmPath, RequireArg(args, 2, "type"), args.Length > 3 ? args[3] : ""),
        "list-attributes"     => ListAttributes(asmPath, RequireArg(args, 2, "type")),
        _                     => Fail($"Unknown command: {command}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ── Utility ────────────────────────────────────────────────────────────────────

static string RequireArg(string[] a, int i, string name)
{
    if (i < a.Length) return a[i];
    throw new ArgumentException($"Missing required argument: <{name}>");
}

static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

static ModuleDefMD OpenModule(string path) =>
    ModuleDefMD.Load(path, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });

static CSharpDecompiler MakeDecompiler(string path) =>
    new CSharpDecompiler(path, new DecompilerSettings
    {
        ThrowOnAssemblyResolveErrors = false,
        AlwaysUseBraces = true,
        ShowXmlDocumentation = false,
    });

// Find a type in dnlib module — accepts short name or fully-qualified name
static TypeDef? FindTypeDnlib(ModuleDef mod, string name)
{
    foreach (var t in mod.GetTypes())
    {
        string full = (string)t.FullName;
        string simple = (string)t.Name;
        if (full == name || simple == name) return t;
    }
    // Suffix match: "Login" -> "DVTA.Login"
    foreach (var t in mod.GetTypes())
    {
        string full = (string)t.FullName;
        if (full.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) return t;
    }
    return null;
}

// Find a type in ILSpy type system — accepts short name or fully-qualified name
static ITypeDefinition? FindTypeILSpy(CSharpDecompiler dec, string name)
{
    // Try fully-qualified first
    try
    {
        var t = dec.TypeSystem.MainModule.Compilation
            .FindType(new FullTypeName(name)).GetDefinition();
        if (t != null) return t;
    }
    catch { /* FullTypeName ctor may throw on bad input */ }

    // Scan all top-level types
    foreach (var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions)
    {
        if (t.FullName == name || t.Name == name) return t;
        foreach (var nested in t.NestedTypes)
            if (nested.FullName == name || nested.Name == name) return nested;
    }

    // Case-insensitive short-name match
    foreach (var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions)
    {
        if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var nested in t.NestedTypes)
            if (string.Equals(nested.Name, name, StringComparison.OrdinalIgnoreCase)) return nested;
    }
    return null;
}

// ── Commands ───────────────────────────────────────────────────────────────────

static int ListTypes(string path)
{
    using var mod = OpenModule(path);
    foreach (var t in mod.GetTypes().OrderBy(t => (string)t.FullName))
        Console.WriteLine((string)t.FullName);
    return 0;
}

static int DecompileType(string path, string typeName, bool il)
{
    if (il)
    {
        using var mod = OpenModule(path);
        var td = FindTypeDnlib(mod, typeName);
        if (td == null) return Fail($"Type '{typeName}' not found.");
        PrintTypeIL(td);
        return 0;
    }

    var dec = MakeDecompiler(path);
    var t = FindTypeILSpy(dec, typeName);
    if (t == null) return Fail($"Type '{typeName}' not found.");
    Console.WriteLine(dec.DecompileAsString(t.MetadataToken));
    return 0;
}

static void PrintTypeIL(TypeDef td)
{
    Console.WriteLine($".class {(string)td.FullName}");
    Console.WriteLine("{");
    foreach (var m in td.Methods)
        PrintMethodIL(m, "  ");
    Console.WriteLine("}");
}

static void PrintMethodIL(MethodDef method, string indent = "")
{
    Console.WriteLine($"{indent}.method {(string)method.FullName}");
    Console.WriteLine($"{indent}{{");
    if (method.HasBody)
    {
        Console.WriteLine($"{indent}  .maxstack {method.Body.MaxStack}");
        if (method.Body.HasVariables)
        {
            var locals = method.Body.Variables
                .Select((v, i) => $"[{i}] {(string)v.Type.FullName} V_{i}");
            Console.WriteLine($"{indent}  .locals init ({string.Join(", ", locals)})");
        }
        foreach (var instr in method.Body.Instructions)
            Console.WriteLine($"{indent}  IL_{instr.Offset:X4}: {instr}");
    }
    Console.WriteLine($"{indent}}}");
}

static int DecompileMethod(string path, string typeName, string methodName, bool il)
{
    if (il)
    {
        using var mod = OpenModule(path);
        var td = FindTypeDnlib(mod, typeName);
        if (td == null) return Fail($"Type '{typeName}' not found.");
        var md = td.Methods.FirstOrDefault(m =>
            string.Equals((string)m.Name, methodName, StringComparison.OrdinalIgnoreCase));
        if (md == null) return Fail($"Method '{methodName}' not found in '{typeName}'.");
        PrintMethodIL(md);
        return 0;
    }

    // C# decompilation via ICSharpCode.Decompiler
    var dec = MakeDecompiler(path);
    var t = FindTypeILSpy(dec, typeName);
    if (t == null) return Fail($"Type '{typeName}' not found.");

    var methods = t.Methods
        .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (methods.Count == 0) return Fail($"Method '{methodName}' not found in '{typeName}'.");

    foreach (var m in methods)
    {
        try { Console.WriteLine(dec.DecompileAsString(m.MetadataToken)); }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: {ex.Message}"); }
    }
    return 0;
}

static int ListStrings(string path, string filter)
{
    using var mod = OpenModule(path);
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var t in mod.GetTypes())
    {
        foreach (var method in t.Methods)
        {
            if (!method.HasBody) continue;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode.Code != Code.Ldstr) continue;
                var s = instr.Operand as string ?? "";
                if (!seen.Add(s)) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    !s.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var display = s.Replace("\\", "\\\\")
                               .Replace("\n", "\\n")
                               .Replace("\r", "\\r")
                               .Replace("\t", "\\t");
                Console.WriteLine($"IL_{instr.Offset:X4}\t{(string)t.FullName}::{(string)method.Name}\t{display}");
            }
        }
    }
    return 0;
}

static int FindCallers(string path, string typeName, string methodName)
{
    using var mod = OpenModule(path);

    // Derive match strings from the user-supplied typeName — supports cross-assembly search.
    // typeName may be "DBAccess.DBAccessClass" (full) or just "DBAccessClass" (short).
    string typeShort = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

    bool found = false;

    foreach (var t in mod.GetTypes())
    {
        foreach (var caller in t.Methods)
        {
            if (!caller.HasBody) continue;
            bool emitted = false;
            foreach (var instr in caller.Body.Instructions)
            {
                if (instr.OpCode.Code != Code.Call &&
                    instr.OpCode.Code != Code.Callvirt &&
                    instr.OpCode.Code != Code.Newobj) continue;

                if (instr.Operand is not IMethodDefOrRef callee) continue;
                if (!string.Equals((string)callee.Name, methodName, StringComparison.OrdinalIgnoreCase)) continue;

                string? declTypeFull = callee.DeclaringType != null ? (string)callee.DeclaringType.FullName : null;
                string? declTypeName = callee.DeclaringType != null ? (string)callee.DeclaringType.Name : null;

                bool typeMatch = string.Equals(declTypeFull, typeName, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(declTypeName, typeShort, StringComparison.OrdinalIgnoreCase);
                if (!typeMatch) continue;

                if (!emitted)
                {
                    Console.WriteLine($"{(string)t.FullName}::{(string)caller.Name}");
                    emitted = true;
                    found = true;
                }
            }
        }
    }

    if (!found)
        Console.WriteLine($"(no callers found for {typeName}::{methodName})");
    return 0;
}

static int GetToken(string path, string typeName, string methodName)
{
    using var mod = OpenModule(path);
    var td = FindTypeDnlib(mod, typeName);
    if (td == null) return Fail($"Type '{typeName}' not found.");

    Console.WriteLine($"Type   : 0x{td.MDToken.Raw:X8}  {(string)td.FullName}");

    if (!string.IsNullOrEmpty(methodName))
    {
        var md = td.Methods.FirstOrDefault(m =>
            string.Equals((string)m.Name, methodName, StringComparison.OrdinalIgnoreCase));
        if (md == null) return Fail($"Method '{methodName}' not found in '{typeName}'.");
        Console.WriteLine($"Method : 0x{md.MDToken.Raw:X8}  {(string)md.Name}");
        Console.WriteLine($"RVA    : 0x{(uint)md.RVA:X8}");
    }
    return 0;
}

static int ListAttributes(string path, string typeName)
{
    using var mod = OpenModule(path);
    var td = FindTypeDnlib(mod, typeName);
    if (td == null) return Fail($"Type '{typeName}' not found.");

    if (!td.HasCustomAttributes)
    {
        Console.WriteLine($"(no custom attributes on {typeName})");
        return 0;
    }

    foreach (var attr in td.CustomAttributes)
    {
        var sb = new StringBuilder("[");
        sb.Append(attr.TypeFullName);
        if (attr.HasConstructorArguments || attr.HasNamedArguments)
        {
            sb.Append('(');
            var parts = new List<string>();
            foreach (var ca in attr.ConstructorArguments)
                parts.Add(FmtValue(ca.Value));
            foreach (var na in attr.NamedArguments)
                parts.Add($"{na.Name} = {FmtValue(na.Argument.Value)}");
            sb.Append(string.Join(", ", parts));
            sb.Append(')');
        }
        sb.Append(']');
        Console.WriteLine(sb);
    }
    return 0;
}

static string FmtValue(object? v) => v switch
{
    string s => $"\"{s}\"",
    null     => "null",
    _        => v.ToString() ?? "?"
};
