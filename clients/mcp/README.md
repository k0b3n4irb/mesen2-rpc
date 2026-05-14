# `@k0b3n4irb/mesen2-mcp`

MCP server that exposes the 27 mesen2-rpc tools to Claude Code.

This is the Claude-facing bridge: stdin/stdout speaks MCP JSON-RPC,
the back-end speaks the mesen2-rpc TCP protocol. Auto-spawns the
`Mesen` binary on first connect; tears it down on exit.

## Install (development, from this repo)

```sh
# 1. Build the bundled client library
( cd ../typescript && npm install && npm run build )

# 2. Build and link the MCP server
npm install
npm run build
```

## Register with Claude Code

```sh
claude mcp add mesen2 -- node /path/to/Mesen2/clients/mcp/dist/index.js
```

Or, after `npm link`:

```sh
claude mcp add mesen2 -- mesen2-mcp
```

Verify:

```sh
claude mcp list                  # should show "mesen2: connected"
```

## Configuration (env vars)

| Var | Default | Meaning |
|---|---|---|
| `MESEN_RPC_BIN` | `/home/kobenairb/workspace/Mesen2/bin/linux-arm64/Release/Mesen` | Mesen-rpc binary to spawn |
| `MESEN_RPC_PORT` | `9911` | TCP port for the JSON-RPC server |
| `MESEN_RPC_HOST` | `127.0.0.1` | Host to connect to |

If a server is already reachable on `MESEN_RPC_HOST:PORT`, the MCP
wrapper connects to it instead of spawning a fresh one. This lets you
run a long-lived emulator in another terminal and have multiple Claude
sessions share it.

## Tool surface (27)

All tools are prefixed `snes_` to keep them grep-able and to avoid
namespace collisions with other MCP servers in the same Claude session:

```
snes_load_rom        snes_reset           snes_run_frames     snes_is_paused
snes_cpu_pc          snes_cpu_register    snes_cpu_state
snes_cpu_step        snes_cpu_step_n      snes_cpu_run_until
snes_mem_read_byte   snes_mem_read_word   snes_mem_read_dword
snes_mem_read_range  snes_mem_search
snes_bp_add          snes_bp_clear        snes_bp_clear_all
snes_bp_list         snes_bp_wait_for_hit
snes_ppu_register    snes_ppu_state
snes_snap_save       snes_snap_restore    snes_snap_list      snes_snap_discard
snes_disasm_at
```

See `src/tools.ts` for full schemas and one-line descriptions.

## Error model

- Bad arguments / out-of-bounds (caught server-side) → tool returns
  `isError: true` with a human-readable text part. Claude can read this
  and retry. The conversation continues.
- Transport / spawn failures → fatal; the MCP server writes a stack
  trace to stderr and exits non-zero. Claude Code will reconnect on
  next session.

Tool errors are never propagated as MCP protocol errors — those abort
the whole conversation, which is the wrong UX for "you passed n=99999
and the max is 10000".

## Token discipline

The full tool list (≈ 2.5 KB JSON) ships in Claude's context every
turn. Per-tool descriptions are kept to one line for that reason. The
response-size caps that protect the per-turn budget live in the C# RPC
server (`mem.read_range` ≤ 256 bytes, `disasm.at` ≤ 64 rows, etc.);
the MCP wrapper does not re-enforce them.

## Smoke test

```sh
node test/smoke.mjs
```

Spawns the MCP server, walks the protocol handshake, exercises a
representative subset of the 27 tools, validates snapshot time-travel.

## License

GPL-3.0.
