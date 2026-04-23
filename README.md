# dnSpy MCP for Claude Code

A [Claude Code](https://claude.ai/code) MCP that brings dnSpy-equivalent .NET analysis into your AI sessions — no GUI required.

Uses the same libraries dnSpy uses internally:
- **dnlib** — metadata reading, IL inspection, string/token extraction
- **ICSharpCode.Decompiler** — C# decompilation

Exposes 10 tools as `mcp__dnspy__*` that Claude can call autonomously during reverse engineering sessions.

---

## Why this exists

`dnSpy.Console.exe` crashes immediately when its stdout is piped — it calls
`SetConsoleOutputCP(65001)` which requires a real Win32 console handle, absent in any
subprocess/MCP context. Rather than work around a broken binary, this project calls
dnSpy's underlying libraries directly and handles piped I/O correctly.

---

## Tools

| Tool | Description |
|---|---|
| `list_types` | All type names in the assembly |
| `decompile_type` | C# source for a type |
| `decompile_type_il` | IL bytecode for all methods in a type |
| `decompile_method` | C# source for one method |
| `decompile_method_il` | IL bytecode for one method |
| `list_strings` | All string literals — filterable by substring |
| `find_callers` | Who calls a method (cross-assembly) |
| `get_metadata_token` | Hex metadata token + RVA |
| `list_attributes` | Custom attributes on a type |
| `search_in_assembly` | String search across all IL |

All tools accept an optional `assembly_path` parameter to override the default assembly set at startup.

---

## Requirements

- Windows x64
- [.NET 9 runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- Python 3.10+
- `mcp>=1.2.0,<2`

---

## Quick start (pre-built)

1. Download `dnspy-mcp-v1.0.0.zip` from [Releases](../../releases)
2. Unzip — you get `DnSpyHelper.exe`, `bridge_mcp_dnspy.py`, `SETUP.md`
3. Install the MCP package:
   ```
   pip install "mcp>=1.2.0,<2"
   ```
4. Register the MCP server (see [MCP Configuration](#mcp-configuration) for details):

   Add to `.mcp.json` in your **project directory** (the folder you run Claude Code from):
   ```json
   {
     "mcpServers": {
       "dnspy": {
         "command": "python",
         "args": [
           "C:\\path\\to\\bridge_mcp_dnspy.py",
           "--assembly", "C:\\path\\to\\YourAssembly.exe",
           "--helper",   "C:\\path\\to\\DnSpyHelper.exe"
         ]
       }
     }
   }
   ```
5. Restart Claude Code

---

## MCP configuration

Claude Code reads MCP servers from **two locations** (both are checked):

| File | Scope | When to use |
|---|---|---|
| `.mcp.json` in your project directory | Project-level (recommended) | When you want the MCP available for a specific project |
| `%USERPROFILE%\.claude\settings.json` | User-level (global) | When you want the MCP available in all projects |

**Important:** If your project already has a `.mcp.json` file with other servers, add the
`"dnspy"` entry inside the existing `"mcpServers"` block — do not create a second file.

If your project has a `.claude/settings.local.json` with an `"enabledMcpjsonServers"` list,
you must also add `"dnspy"` to that list or the server will be ignored even if configured.

### Project-level (`.mcp.json`)

Create or edit `.mcp.json` in the directory where you launch Claude Code:

```json
{
  "mcpServers": {
    "dnspy": {
      "command": "python",
      "args": [
        "C:\\path\\to\\bridge_mcp_dnspy.py",
        "--assembly", "C:\\path\\to\\YourAssembly.exe",
        "--helper",   "C:\\path\\to\\DnSpyHelper.exe"
      ]
    }
  }
}
```

### User-level (`settings.json`)

Edit `%USERPROFILE%\.claude\settings.json`:

```json
{
  "mcpServers": {
    "dnspy": {
      "command": "python",
      "args": [
        "C:\\path\\to\\bridge_mcp_dnspy.py",
        "--assembly", "C:\\path\\to\\YourAssembly.exe",
        "--helper",   "C:\\path\\to\\DnSpyHelper.exe"
      ]
    }
  }
}
```

### Verify

After restarting Claude Code, run:

```
claude mcp list
```

You should see `dnspy` listed as `Connected`.

---

## Build from source

Requires .NET 9 SDK.

```
git clone https://github.com/overlord3850/dnspy-mcp.git
cd dnspy-mcp/DnSpyHelper
dotnet publish -c Release -r win-x64 --no-self-contained -o binpublish
```

The EXE will be at `DnSpyHelper/binpublish/DnSpyHelper.exe`.

See [SETUP.md](SETUP.md) for the full step-by-step guide.

---

## Usage examples

Claude can call these tools autonomously, or you can run the helper directly:

```
# What types are in this binary?
DnSpyHelper.exe list-types MyApp.exe

# Decompile a class to C#
DnSpyHelper.exe decompile-type MyApp.exe MyNamespace.MyClass

# Decompile one method
DnSpyHelper.exe decompile-method MyApp.exe MyClass MyMethod

# Find hardcoded credentials, connection strings, SQL
DnSpyHelper.exe list-strings MyApp.exe password
DnSpyHelper.exe list-strings MyApp.exe SELECT

# Who calls this method? (searches caller assembly, target can be in a different DLL)
DnSpyHelper.exe find-callers CallerAssembly.exe TargetClass TargetMethod

# Get metadata token + RVA for patching
DnSpyHelper.exe get-token MyApp.exe MyClass MyMethod
```

---

## Architecture

```
Claude Code
    │  stdio (MCP protocol)
    ▼
bridge_mcp_dnspy.py       ← Python FastMCP server
    │  subprocess
    ▼
DnSpyHelper.exe           ← .NET 9 CLI helper
    ├── ICSharpCode.Decompiler  (C# decompilation)
    └── dnlib                   (metadata + IL)
```

**Target assembly architecture:** Both x86 and x64 .NET assemblies are supported.
dnlib and ICSharpCode.Decompiler perform static file analysis — they never execute
the target assembly, so the host/target architecture mismatch is irrelevant.
