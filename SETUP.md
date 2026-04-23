# dnSpy MCP — Setup Guide

A Claude Code MCP that provides dnSpy-equivalent .NET analysis through two libraries:
- **dnlib** — metadata reading, IL inspection, string/token extraction
- **ICSharpCode.Decompiler** — C# decompilation (same engine dnSpy uses)

The MCP exposes 10 tools as `mcp__dnspy__*` inside Claude Code sessions.

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 9.0+ | `dotnet --version` to check |
| Python | 3.10+ | `python --version` to check |
| `mcp` Python package | >=1.2.0,<2 | Installed below |
| Windows | x64 | The helper EXE targets win-x64; can analyze both x86 and x64 .NET assemblies |

---

## Directory layout

After setup, the project looks like this:

```
dnspy-mcp/
  bridge_mcp_dnspy.py          <- Python MCP bridge (FastMCP)
  SETUP.md                     <- this file
  DnSpyHelper/
    DnSpyHelper.csproj
    Program.cs
    binpublish/
      DnSpyHelper.exe          <- compiled helper (produced by dotnet publish)
```

---

## Step 1 — Create the project folder

```
mkdir dnspy-mcp
cd dnspy-mcp
mkdir DnSpyHelper
```

---

## Step 2 — Create the .NET helper project

### `DnSpyHelper/DnSpyHelper.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>DnSpyHelper</AssemblyName>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ICSharpCode.Decompiler" Version="9.0.0.7889" />
    <PackageReference Include="dnlib" Version="4.4.0" />
  </ItemGroup>
</Project>
```

### `DnSpyHelper/Program.cs`

```csharp
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

// ── Utility ─────────────────────────────────────────────────────────────────────

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
    try
    {
        var t = dec.TypeSystem.MainModule.Compilation
            .FindType(new FullTypeName(name)).GetDefinition();
        if (t != null) return t;
    }
    catch { }

    foreach (var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions)
    {
        if (t.FullName == name || t.Name == name) return t;
        foreach (var nested in t.NestedTypes)
            if (nested.FullName == name || nested.Name == name) return nested;
    }

    foreach (var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions)
    {
        if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var nested in t.NestedTypes)
            if (string.Equals(nested.Name, name, StringComparison.OrdinalIgnoreCase)) return nested;
    }
    return null;
}

// ── Commands ────────────────────────────────────────────────────────────────────

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

    // Supports cross-assembly search: typeName may be defined in a different DLL
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
```

---

## Step 3 — Create the Python bridge

### `bridge_mcp_dnspy.py`

```python
# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "mcp>=1.2.0,<2",
# ]
# ///

import subprocess
import argparse
import logging

from mcp.server.fastmcp import FastMCP

logger = logging.getLogger(__name__)
mcp = FastMCP("dnspy-mcp")

helper_exe = ""
default_assembly = ""


def run_helper(cmd_args: list[str], timeout: int = 60) -> str:
    try:
        result = subprocess.run(
            [helper_exe] + cmd_args,
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
        )
        output = result.stdout.strip()
        if result.returncode != 0:
            err = result.stderr.strip()
            return f"Error: {err}" if err else f"Error: helper exited with code {result.returncode}"
        return output
    except subprocess.TimeoutExpired:
        return f"Error: DnSpyHelper timed out after {timeout}s"
    except FileNotFoundError:
        return f"Error: DnSpyHelper not found at '{helper_exe}'"
    except Exception as e:
        return f"Error: {str(e)}"


def resolve_assembly(assembly_path: str) -> str:
    return assembly_path if assembly_path else default_assembly


@mcp.tool()
def list_types(assembly_path: str = "") -> list[str]:
    """List all fully-qualified type names defined in a .NET assembly."""
    path = resolve_assembly(assembly_path)
    output = run_helper(["list-types", path])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def decompile_type(type_name: str, assembly_path: str = "") -> str:
    """Decompile a .NET type to C# source using ICSharpCode.Decompiler (same engine as dnSpy)."""
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-type", path, type_name])


@mcp.tool()
def decompile_type_il(type_name: str, assembly_path: str = "") -> str:
    """Decompile a .NET type to raw IL bytecode. Shows opcodes, offsets, locals for every method."""
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-type-il", path, type_name])


@mcp.tool()
def decompile_method(type_name: str, method_name: str, assembly_path: str = "") -> str:
    """Decompile a single method to C# source. method_name is case-insensitive."""
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-method", path, type_name, method_name])


@mcp.tool()
def decompile_method_il(type_name: str, method_name: str, assembly_path: str = "") -> str:
    """Decompile a single method to raw IL bytecode. Essential for IL patching and offset analysis."""
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-method-il", path, type_name, method_name])


@mcp.tool()
def list_strings(assembly_path: str = "", filter: str = "") -> list[str]:
    """
    Extract all string literals from a .NET assembly's IL.
    Each line: '<IL_offset>  <Type>::<Method>  <string_value>'
    Use filter to restrict results (e.g. 'password', 'SELECT', 'http').
    """
    path = resolve_assembly(assembly_path)
    args = ["list-strings", path]
    if filter:
        args.append(filter)
    output = run_helper(args, timeout=120)
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def find_callers(type_name: str, method_name: str, assembly_path: str = "") -> list[str]:
    """
    Find all call sites of a method in the assembly (dnSpy Analyzer equivalent).
    Works cross-assembly: type_name may be defined in a different DLL than assembly_path.
    Returns lines like 'DVTA.Login::btnLogin_Click'.
    """
    path = resolve_assembly(assembly_path)
    output = run_helper(["find-callers", path, type_name, method_name])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def get_metadata_token(type_name: str, method_name: str = "", assembly_path: str = "") -> str:
    """
    Get the raw metadata token (hex) and RVA for a type/method.
    Required for IL patching and binary offset identification.
    """
    path = resolve_assembly(assembly_path)
    args = ["get-token", path, type_name]
    if method_name:
        args.append(method_name)
    return run_helper(args)


@mcp.tool()
def list_attributes(type_name: str, assembly_path: str = "") -> list[str]:
    """List all custom attributes applied to a .NET type."""
    path = resolve_assembly(assembly_path)
    output = run_helper(["list-attributes", path, type_name])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def search_in_assembly(search_term: str, assembly_path: str = "", case_sensitive: bool = False) -> list[str]:
    """Search for a term across all string literals in the assembly."""
    path = resolve_assembly(assembly_path)
    filter_term = search_term if case_sensitive else search_term.lower()
    output = run_helper(["list-strings", path, filter_term], timeout=120)
    if output.startswith("Error"):
        return [output]
    lines = output.splitlines()
    if not case_sensitive:
        lines = [l for l in lines if search_term.lower() in l.lower()]
    else:
        lines = [l for l in lines if search_term in l]
    return lines if lines else [f"No matches for '{search_term}'"]


def main():
    parser = argparse.ArgumentParser(description="dnspy-mcp bridge")
    parser.add_argument("--assembly", default="", help="Default .NET assembly path")
    parser.add_argument("--helper", default="DnSpyHelper.exe", help="Path to DnSpyHelper.exe")
    args = parser.parse_args()

    global default_assembly, helper_exe
    default_assembly = args.assembly
    helper_exe = args.helper

    mcp.run()


if __name__ == "__main__":
    main()
```

---

## Step 4 — Install the Python MCP package

```
pip install "mcp>=1.2.0,<2"
```

Verify:

```
python -c "from mcp.server.fastmcp import FastMCP; print('ok')"
```

---

## Step 5 — Build DnSpyHelper.exe

From inside the `DnSpyHelper/` directory:

```
cd DnSpyHelper
dotnet publish -c Release -r win-x64 --no-self-contained -o binpublish
```

Expected output ends with:

```
DnSpyHelper -> ...\DnSpyHelper\binpublish\
```

> **Important — Git Bash path separator bug:** If using Git Bash on Windows,
> `-o bin\publish` treats the backslash as an escape and produces a folder named
> `binpublish` instead of `bin\publish`. Use `-o binpublish` (no backslash) or
> use PowerShell/CMD if you need a sub-folder.

Verify the EXE exists:

```
ls binpublish/DnSpyHelper.exe
```

---

## Step 6 — Smoke-test the helper directly

Replace `C:\path\to\YourAssembly.exe` with your target .NET binary.

```
# List all types
.\binpublish\DnSpyHelper.exe list-types "C:\path\to\YourAssembly.exe"

# Decompile a type to C#
.\binpublish\DnSpyHelper.exe decompile-type "C:\path\to\YourAssembly.exe" YourNamespace.YourClass

# Decompile a single method to C#
.\binpublish\DnSpyHelper.exe decompile-method "C:\path\to\YourAssembly.exe" YourClass YourMethod

# Decompile a method to IL
.\binpublish\DnSpyHelper.exe decompile-method-il "C:\path\to\YourAssembly.exe" YourClass YourMethod

# Find hardcoded strings containing "password"
.\binpublish\DnSpyHelper.exe list-strings "C:\path\to\YourAssembly.exe" password

# Find callers of a method (cross-assembly: target type may be in a different DLL)
.\binpublish\DnSpyHelper.exe find-callers "C:\path\to\CallerAssembly.exe" TargetClass TargetMethod

# Get metadata token + RVA
.\binpublish\DnSpyHelper.exe get-token "C:\path\to\YourAssembly.exe" YourClass YourMethod

# List custom attributes on a type
.\binpublish\DnSpyHelper.exe list-attributes "C:\path\to\YourAssembly.exe" YourClass
```

All commands exit 0 and print to stdout on success. Errors go to stderr and exit 1.

---

## Step 7 — Register with Claude Code

Claude Code reads MCP server configuration from **two locations**:

| File | Scope | When to use |
|---|---|---|
| `.mcp.json` in your project directory | Project-level (recommended) | When you want the MCP available for a specific project |
| `%USERPROFILE%\.claude\settings.json` | User-level (global) | When you want the MCP available in all projects |

### Option A — Project-level `.mcp.json` (recommended)

Create or edit `.mcp.json` in the directory where you launch Claude Code.
Adjust the paths to match your actual directory layout.

```json
{
  "mcpServers": {
    "dnspy": {
      "command": "python",
      "args": [
        "C:\\path\\to\\dnspy-mcp\\bridge_mcp_dnspy.py",
        "--assembly",
        "C:\\path\\to\\YourAssembly.exe",
        "--helper",
        "C:\\path\\to\\dnspy-mcp\\DnSpyHelper\\binpublish\\DnSpyHelper.exe"
      ]
    }
  }
}
```

If the file already exists with other MCP servers, add the `"dnspy"` entry inside
the existing `"mcpServers"` block.

> **Note:** If your project has a `.claude/settings.local.json` with an
> `"enabledMcpjsonServers"` list, you must also add `"dnspy"` to that array.
> Otherwise the server will be silently ignored even if configured in `.mcp.json`.

### Option B — User-level `settings.json` (global)

Edit `%USERPROFILE%\.claude\settings.json`:

```json
{
  "mcpServers": {
    "dnspy": {
      "command": "python",
      "args": [
        "C:\\path\\to\\dnspy-mcp\\bridge_mcp_dnspy.py",
        "--assembly",
        "C:\\path\\to\\YourAssembly.exe",
        "--helper",
        "C:\\path\\to\\dnspy-mcp\\DnSpyHelper\\binpublish\\DnSpyHelper.exe"
      ]
    }
  }
}
```

### Configuration notes

- `--assembly` sets the **default** assembly. Every tool accepts an `assembly_path` parameter to override this at call time.
- `--helper` must point to the `DnSpyHelper.exe` produced in Step 5.
- If the file already has other MCP servers, add a comma between entries and do not introduce a trailing comma after the last entry (the file is strict JSON).

---

## Step 8 — Restart Claude Code and verify

Restart the Claude Code CLI or desktop app so it picks up the new configuration.

Run the following to confirm the server is loaded:

```
claude mcp list
```

You should see `dnspy` listed as `Connected`.

In a new session, ask Claude:

> "List all types in my default assembly using the dnspy MCP."

The tools are available as:

| MCP tool name | What it does |
|---|---|
| `mcp__dnspy__list_types` | All type names in the assembly |
| `mcp__dnspy__decompile_type` | C# source for a type |
| `mcp__dnspy__decompile_type_il` | IL bytecode for all methods in a type |
| `mcp__dnspy__decompile_method` | C# source for one method |
| `mcp__dnspy__decompile_method_il` | IL bytecode for one method |
| `mcp__dnspy__list_strings` | All string literals (filterable) |
| `mcp__dnspy__find_callers` | Who calls a method (cross-assembly) |
| `mcp__dnspy__get_metadata_token` | Hex token + RVA for patching |
| `mcp__dnspy__list_attributes` | Custom attributes on a type |
| `mcp__dnspy__search_in_assembly` | String search across all IL strings |

---

## Architecture note — why not wrap dnSpy.Console.exe directly?

`dnSpy.Console.exe` crashes with `System.IO.IOException: The handle is invalid` whenever
its stdout is piped (the standard subprocess pattern used by all MCP bridges). The crash
happens because it unconditionally calls `Console.OutputEncoding = Encoding.UTF8`, which
calls `SetConsoleOutputCP(65001)`, which requires a real Win32 console handle — absent
when output is redirected.

DnSpyHelper avoids this by checking `Console.IsOutputRedirected` before touching the
encoding, and by using the same underlying libraries (dnlib + ICSharpCode.Decompiler)
that dnSpy itself uses internally.

---

## Architecture note — x86 vs x64 assemblies

The helper EXE is compiled for win-x64 (the host process runs as 64-bit). The **target
assembly** architecture does not matter: dnlib and ICSharpCode.Decompiler perform static
file analysis — they read PE metadata and IL bytes from disk without executing the
assembly. Both x86 and x64 (and AnyCPU) .NET assemblies are analyzed correctly.
