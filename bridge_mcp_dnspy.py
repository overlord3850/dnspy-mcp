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
    """Run DnSpyHelper.exe and return its stdout, or an error string."""
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


# ── Tools ──────────────────────────────────────────────────────────────────────

@mcp.tool()
def list_types(assembly_path: str = "") -> list[str]:
    """
    List all fully-qualified type names defined in a .NET assembly (via dnlib).
    Returns one name per entry, e.g. 'DVTA.Login', 'DBAccess.DBAccessClass'.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    output = run_helper(["list-types", path])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def decompile_type(type_name: str, assembly_path: str = "") -> str:
    """
    Decompile a .NET type to C# source code using ICSharpCode.Decompiler (same engine as dnSpy).
    type_name accepts short name ('Login') or fully qualified ('DVTA.Login').
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-type", path, type_name])


@mcp.tool()
def decompile_type_il(type_name: str, assembly_path: str = "") -> str:
    """
    Decompile a .NET type to raw IL (CIL) bytecode using dnlib.
    Shows opcodes, offsets, local variables, and maxstack for every method in the type.
    Useful for identifying exact patch points, verifying IL structure, or reverse-engineering obfuscated code.
    type_name accepts short name or fully qualified name.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-type-il", path, type_name])


@mcp.tool()
def decompile_method(type_name: str, method_name: str, assembly_path: str = "") -> str:
    """
    Decompile a single method to C# source. More focused than decompile_type when you know the method.
    type_name accepts short or fully-qualified name; method_name is case-insensitive.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-method", path, type_name, method_name])


@mcp.tool()
def decompile_method_il(type_name: str, method_name: str, assembly_path: str = "") -> str:
    """
    Decompile a single method to raw IL bytecode.
    Returns opcodes, IL offsets, local variable types, and maxstack for the requested method only.
    Essential for patching (finding exact IL offsets), tracing SQL queries, or confirming what IL was actually compiled.
    type_name accepts short or fully-qualified name; method_name is case-insensitive.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    return run_helper(["decompile-method-il", path, type_name, method_name])


@mcp.tool()
def list_strings(assembly_path: str = "", filter: str = "") -> list[str]:
    """
    Extract all string literals embedded in a .NET assembly's IL.
    Each result line is: '<IL_offset>  <Type>::<Method>  <string_value>'
    Invaluable for finding hardcoded credentials, connection strings, API keys, URLs, and SQL queries.
    filter: optional substring to restrict results (e.g. 'password', 'SELECT', 'http').
    Leave assembly_path empty to use the default configured assembly.
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
    Find all methods in the assembly that call a specific method (cross-reference / caller analysis).
    Equivalent to dnSpy's Analyzer → 'Used By'. Works cross-assembly: type_name may be defined in a
    different DLL than assembly_path — the search scans the specified assembly for call sites.
    Returns lines like 'DVTA.Login::btnLogin_Click'.
    type_name accepts short name ('DBAccessClass') or fully qualified ('DBAccess.DBAccessClass').
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    output = run_helper(["find-callers", path, type_name, method_name])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def get_metadata_token(type_name: str, method_name: str = "", assembly_path: str = "") -> str:
    """
    Get the raw metadata token (hex) for a type and optionally a method, plus the method's RVA.
    Metadata tokens are required for IL patching, hooking, and identifying exact binary offsets.
    Returns lines like:
      Type   : 0x02000005  DVTA.Login
      Method : 0x06000019  btnLogin_Click
      RVA    : 0x00002CE8
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    args = ["get-token", path, type_name]
    if method_name:
        args.append(method_name)
    return run_helper(args)


@mcp.tool()
def list_attributes(type_name: str, assembly_path: str = "") -> list[str]:
    """
    List all custom attributes applied to a .NET type (e.g. [Serializable], [WebMethod], security attributes).
    Useful for identifying authentication bypass candidates, serialization attack surfaces, or
    legacy web service methods.
    type_name accepts short or fully-qualified name.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    output = run_helper(["list-attributes", path, type_name])
    if output.startswith("Error"):
        return [output]
    return [line for line in output.splitlines() if line.strip()]


@mcp.tool()
def search_in_assembly(search_term: str, assembly_path: str = "", case_sensitive: bool = False) -> list[str]:
    """
    Search for a term across all string literals in the assembly (case-insensitive by default).
    Shorthand for list_strings with a filter — returns matching lines in the same format.
    Leave assembly_path empty to use the default configured assembly.
    """
    path = resolve_assembly(assembly_path)
    filter_term = search_term if case_sensitive else search_term.lower()
    args = ["list-strings", path, filter_term]
    output = run_helper(args, timeout=120)
    if output.startswith("Error"):
        return [output]
    lines = output.splitlines()
    if not case_sensitive:
        lines = [l for l in lines if search_term.lower() in l.lower()]
    else:
        lines = [l for l in lines if search_term in l]
    return lines if lines else [f"No matches for '{search_term}'"]


# ── Entry point ────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="dnspy-mcp bridge — .NET analysis tools for Claude")
    parser.add_argument(
        "--assembly",
        default="",
        help="Default .NET assembly path to analyze (e.g. C:\\path\\to\\DVTA.exe)"
    )
    parser.add_argument(
        "--helper",
        default="DnSpyHelper.exe",
        help="Path to DnSpyHelper.exe"
    )
    args = parser.parse_args()

    global default_assembly, helper_exe
    default_assembly = args.assembly
    helper_exe = args.helper

    logger.info(f"dnspy-mcp started | default assembly: {default_assembly or '(none)'} | helper: {helper_exe}")
    mcp.run()


if __name__ == "__main__":
    main()
