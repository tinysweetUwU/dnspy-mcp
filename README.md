# dnSpy MCP Server

MCP (Model Context Protocol) server extension for [dnSpy](https://github.com/dnSpyEx/dnSpy), enabling AI agents to decompile and analyze .NET assemblies directly through dnSpy.

## How It Works

```
dnSpy loads extension → MCP Server menu → Start → HTTP server on :5150
                                                        ↕ (two transports)
                                          ┌─────────────────────────────┐
                                          │  GET  /sse      ← RooCode   │
                                          │  POST /messages ← Cline     │
                                          │  POST /         ← Claude Code│
                                          │  POST /         ← Cursor    │
                                          └─────────────────────────────┘
```

The server supports **two MCP transports** simultaneously:

| Transport | Endpoint | Works with |
|-----------|----------|-----------|
| **HTTP+SSE** (legacy) | `GET /sse` + `POST /messages` | RooCode, Cline, Continue.dev |
| **Streamable HTTP** | `POST /` | Claude Code, Cursor, most modern clients |

## Tools (36)

### Decompiler
| Tool | Description |
|------|-------------|
| `decompile_method` | Decompile a method to C#. Accepts full name (`Namespace.Class::Method`), metadata token (`0x06000001`), or partial name |
| `decompile_type` | Decompile an entire type (all members) to C# |
| `decompile_assembly` | Decompile all types in the assembly (limited to 10 for brevity) |

### Search
| Tool | Description |
|------|-------------|
| `search_types` | Search types by name pattern. Use `regex:` prefix for regex matching |
| `search_methods` | Search methods by name, optionally scoped to a specific type |
| `search_strings` | Search string literals in method bodies |
| `grep` | Multi-scope search across types, methods, and strings |

### Analysis
| Tool | Description |
|------|-------------|
| `get_method_il` | Raw IL instructions with exception handlers |
| `get_method_signatures` | Method metadata: parameters, return type, flags, generic params |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL bytes with MaxStack/InitLocals info |
| `get_il_opcodes_formatted` | Formatted IL opcodes with offsets and line indices |
| `update_method_body` | Patch a method body using C# statements (dry-run supported) |

### UI & Navigation
| Tool | Description |
|------|-------------|
| `get_selected_node` | Get the currently selected node in dnSpy tree view |
| `refresh_u_i` | Refresh tree view UI after metadata changes |

### Rename
| Tool | Description |
|------|-------------|
| `rename_namespace` | Rename a namespace across matching types (dry-run supported) |
| `rename_class` | Rename one class in an assembly+namespace (dry-run supported) |
| `rename_method` | Rename methods by exact or partial match (dry-run supported) |

### Namespace
| Tool | Description |
|------|-------------|
| `get_global_namespaces` | List all types in the global namespace |

### Type Inspection
| Tool | Description |
|------|-------------|
| `get_type_members` | List all members of a type with optional filter |
| `get_fields` | Detailed field info: type, access, static/const, values |
| `get_properties` | Property details: getter/setter, type, access |

### Custom Attributes
| Tool | Description |
|------|-------------|
| `get_attributes` | Attributes on assembly/type/method/field with filter |
| `get_method_attributes` | Shortcut: attributes on a specific method |

### Constants & Enums
| Tool | Description |
|------|-------------|
| `get_enum_values` | Enum members with name + value (hex + decimal) |
| `search_constants` | Search const/literal fields across assemblies |

### Cross-References
| Tool | Description |
|------|-------------|
| `get_xrefs_to` | Find all references to a method or field |
| `get_callees` | Methods and fields called by a method |

### Assembly
| Tool | Description |
|------|-------------|
| `assembly_overview` | Module info, version, entry point, type count, references |
| `assembly_list_namespaces` | All namespaces in the loaded assembly |
| `assembly_list_types` | Type listing with optional regex filter |
| `assembly_get_references` | Assembly references (DLLs, NuGet packages) |

### Resources & Metadata
| Tool | Description |
|------|-------------|
| `get_resources` | List embedded resources |
| `get_resource_data` | Raw bytes of a specific resource |
| `get_metadata` | PE headers, MVID, runtime version, sections |

---

## Quick Start

### Prerequisites

- [dnSpy](https://github.com/dnSpyEx/dnSpy/releases) (.NET 8.0 build)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- `deps/` folder with these DLLs copied from dnSpy:
  - `dnSpy.Contracts.DnSpy.dll`
  - `dnSpy.Contracts.Logic.dll`
  - `ICSharpCode.Decompiler.dll`
  - `dnlib.dll`

### Build & Deploy

```powershell
pwsh scripts/build.ps1 -Deploy
```

### Usage

1. Start `dnSpy.exe`
2. Open a .NET assembly (.exe/.dll)
3. Menu → **MCP Server** → **Start**
4. Open **View → Output** (Alt+2) → select **MCP Server** to see logs
5. Connect from your AI extension (see below)

---

## Connecting AI Extensions

### RooCode (VS Code)

**RooCode** requires the **SSE endpoint** (`/sse`).

Add to your project's `.vscode/mcp.json` (create if it doesn't exist):

```json
{
  "mcpServers": {
    "dnspy": {
      "url": "http://127.0.0.1:5150/sse"
    }
  }
}
```

Or add to VS Code workspace settings (`settings.json`):

```json
{
  "roo-cline.mcpServers": {
    "dnspy": {
      "url": "http://127.0.0.1:5150/sse"
    }
  }
}
```

### Cline (VS Code)

Same as RooCode — uses SSE endpoint:

```json
{
  "mcpServers": {
    "dnspy": {
      "url": "http://127.0.0.1:5150/sse"
    }
  }
}
```

In Cline's MCP settings UI: click **Add Server** → choose **SSE** → enter `http://127.0.0.1:5150/sse`.

### Continue.dev (VS Code)

In `~/.continue/config.json`:

```json
{
  "mcpServers": [
    {
      "name": "dnspy",
      "transport": {
        "type": "sse",
        "url": "http://127.0.0.1:5150/sse"
      }
    }
  ]
}
```

### Claude Code (CLI)

```bash
# Add server (HTTP transport)
claude mcp add --transport http dnspy http://127.0.0.1:5150

# Or project scope (shared via .mcp.json)
claude mcp add --transport http dnspy --scope project http://127.0.0.1:5150
```

`.mcp.json` format:
```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://127.0.0.1:5150"
    }
  }
}
```

### Cursor

In `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "dnspy": {
      "url": "http://127.0.0.1:5150/"
    }
  }
}
```

---

## Transport Reference

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /sse` | GET | Open SSE stream (RooCode/Cline) — keeps connection alive |
| `POST /messages?sessionId=<id>` | POST | Send messages over active SSE session |
| `POST /` | POST | Streamable HTTP (Claude Code/Cursor) — single request/response |
| `GET /health` | GET | Health check: status, uptime, tool count |
| `OPTIONS *` | OPTIONS | CORS preflight |

---

## Configuration

Via **MCP Server → Settings** in dnSpy:

| Setting | Default | Description |
|---------|---------|-------------|
| Host | `127.0.0.1` | Bind address (`0.0.0.0` for network access) |
| Port | `5150` | TCP port |
| Auto Start | `false` | Start server when dnSpy loads |
| Require Auth | `false` | Enable Bearer token auth |
| API Token | _(empty)_ | Token value (if auth enabled) |
| Allowed Origins | `*` | CORS origin header value |
| Max Concurrency | `4` | Max parallel tool calls |
| Tool Timeout | `30s` | Per-tool execution timeout |

---

## Project Structure

```
dnspy-mcp/
├── src/
│   └── dnSpy.MCP/
│       ├── Mcp/
│       │   ├── McpServerHost.cs     # HTTP+SSE + Streamable HTTP transport
│       │   ├── ToolRegistry.cs      # Reflection-based tool discovery
│       │   └── McpLogger.cs         # Logging
│       ├── Tools/                   # 13 tool classes, 36 tools total
│       ├── Settings/                # Settings UI and persistence
│       ├── TheExtension.cs          # MEF entry point
│       ├── DnSpyContext.cs          # Static service bridge
│       └── MenuCommands.cs          # dnSpy menu items
├── config-examples/
│   ├── roocode-cline.json           # RooCode / Cline config
│   ├── cursor-mcp.json              # Cursor config
│   ├── vscode-mcp.json              # VS Code mcp.json
│   └── continue-config.json         # Continue.dev config
├── deps/                            # dnSpy contract DLLs
├── skills/                          # AI agent workflow guides
└── scripts/
    └── build.ps1                    # Build & deploy script
```

---

## Troubleshooting

### RooCode/Cline cannot connect

1. Verify the server is running: open `http://127.0.0.1:5150/health` in a browser
2. Make sure you're using the **`/sse`** endpoint URL (not `/`)
3. Check that the port matches your MCP Settings (default: 5150)
4. In Cline MCP settings: ensure the server type is **SSE** (not stdio)

### "Tools not showing up"

After adding the server config, restart VS Code or reload the extension. RooCode/Cline only discovers tools on connection open.

### Connection refused

- Start dnSpy first, then **MCP Server → Start**
- Check **MCP Server → Status** to confirm it's running
- Look at **View → Output → MCP Server** for error messages

### Auth errors (401)

If `Require Auth` is enabled in settings, add the token:
```json
{
  "mcpServers": {
    "dnspy": {
      "url": "http://127.0.0.1:5150/sse",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

---

## License

This project is licensed under [GPLv3](https://www.gnu.org/licenses/gpl-3.0.en.html), consistent with dnSpy's license.
