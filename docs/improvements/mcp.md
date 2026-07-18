# MCP (Model Context Protocol)

✅ **MCP server implemented** using the C# SDK (`ModelContextProtocol.AspNetCore` v1.4.1) with HTTP (Streamable HTTP) transport at `/mcp`.

## Exposed Tools

- `get_jobs` — list jobs with optional status filter, `offset`, and `limit` pagination.
- `get_logs` — read job log files with `offset` (line number) and `pageSize` for efficient browsing of long logs.
- `get_config` — returns current ARM Sharp configuration (API key presence is shown as booleans, values are never exposed).

## Future Tools (🔲)

- `update_config`
- `eject_drive`
- `trigger_identify`
- Log streaming via SSE
