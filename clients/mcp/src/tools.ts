/**
 * MCP tool registry. Each entry pairs a JSON-Schema input with a handler
 * that dispatches to the typed Mesen2Client.
 *
 * Token discipline matters here: the FULL tool list ships in the LLM
 * context every turn, so descriptions are kept to one terse line. The
 * server-side bounds (cap on n, maxResults, etc.) live in the C# RPC
 * server — the schemas here just hint at the limits; rejection is
 * authoritative server-side and surfaces as a tool error.
 */

import type { Mesen2Client } from "@k0b3n4irb/mesen2-rpc-client";

export interface ToolDef {
  name: string;
  description: string;
  inputSchema: {
    type: "object";
    properties: Record<string, unknown>;
    required?: string[];
    additionalProperties?: boolean;
  };
  handler: (client: Mesen2Client, args: Record<string, unknown>) => Promise<unknown>;
}

function asNum(v: unknown, name: string): number {
  if (typeof v !== "number" || !Number.isFinite(v)) {
    throw new Error(`${name} must be a number, got ${typeof v}`);
  }
  return v;
}
function asStr(v: unknown, name: string): string {
  if (typeof v !== "string") {
    throw new Error(`${name} must be a string, got ${typeof v}`);
  }
  return v;
}
function optNum(v: unknown, name: string, fallback: number): number {
  if (v === undefined || v === null) return fallback;
  return asNum(v, name);
}

export const TOOLS: ToolDef[] = [
  // ---------- emu ----------
  {
    name: "snes_load_rom",
    description: "Load a SNES ROM (.sfc/.smc) and start the emulator. Path must be absolute.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string", description: "Absolute path to .sfc/.smc" } },
      required: ["path"],
      additionalProperties: false,
    },
    handler: (c, a) => c.emu.loadRom(asStr(a.path, "path")),
  },
  {
    name: "snes_reset",
    description: "Reset the loaded ROM (power-cycle equivalent).",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.emu.reset(),
  },
  {
    name: "snes_run_frames",
    description: "Advance the emulator exactly N frames (1–600). Use to boot a ROM or skip ahead.",
    inputSchema: {
      type: "object",
      properties: { n: { type: "integer", minimum: 1, maximum: 600 } },
      required: ["n"],
      additionalProperties: false,
    },
    handler: (c, a) => c.emu.runFrames(asNum(a.n, "n")),
  },
  {
    name: "snes_is_paused",
    description: "True if the emulator is currently paused (debugger break or explicit pause).",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.emu.isPaused(),
  },

  // ---------- cpu ----------
  {
    name: "snes_cpu_pc",
    description: "Read the 65816 program counter. Cheapest way to find out where execution stopped.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.cpu.pc(),
  },
  {
    name: "snes_cpu_register",
    description: "Read one 65816 register by name. Valid: A X Y SP D PC K DBR P.",
    inputSchema: {
      type: "object",
      properties: { name: { type: "string", description: "A/X/Y/SP/D/PC/K/DBR/P" } },
      required: ["name"],
      additionalProperties: false,
    },
    handler: (c, a) => c.cpu.register(asStr(a.name, "name") as never),
  },
  {
    name: "snes_cpu_state",
    description: "Full 65816 register file in one call (A X Y SP D PC K DBR P EmuMode Cycle). Use when you need the whole snapshot.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.cpu.state(),
  },
  {
    name: "snes_cpu_step",
    description: "Single 65816 instruction step. Returns the new PC. Note: stuck at WAI until NMI fires.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.cpu.step(),
  },
  {
    name: "snes_cpu_step_n",
    description: "Step N instructions (1–10000) via native bulk count. Returns the new PC.",
    inputSchema: {
      type: "object",
      properties: { n: { type: "integer", minimum: 1, maximum: 10000 } },
      required: ["n"],
      additionalProperties: false,
    },
    handler: (c, a) => c.cpu.stepN(asNum(a.n, "n")),
  },
  {
    name: "snes_cpu_run_until",
    description: "Install a temporary exec breakpoint at addr, resume, wait for the hit. Returns {PC, Hit}. Throws on timeout.",
    inputSchema: {
      type: "object",
      properties: {
        addr: { type: "integer", description: "CPU-relative 24-bit address (e.g. 0x009586)" },
        timeoutMs: { type: "integer", minimum: 100, maximum: 60000, default: 5000 },
      },
      required: ["addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.cpu.runUntil(asNum(a.addr, "addr"), optNum(a.timeoutMs, "timeoutMs", 5000)),
  },

  // ---------- mem ----------
  {
    name: "snes_mem_read_byte",
    description: "Read 1 byte from a memory space. space ∈ {cpu, wram, vram, cgram, oam, sram, rom}.",
    inputSchema: {
      type: "object",
      properties: {
        space: { type: "string" },
        addr: { type: "integer" },
      },
      required: ["space", "addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.mem.readByte(asStr(a.space, "space") as never, asNum(a.addr, "addr")),
  },
  {
    name: "snes_mem_read_word",
    description: "Read 2 bytes little-endian. Same memory spaces as snes_mem_read_byte.",
    inputSchema: {
      type: "object",
      properties: { space: { type: "string" }, addr: { type: "integer" } },
      required: ["space", "addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.mem.readWord(asStr(a.space, "space") as never, asNum(a.addr, "addr")),
  },
  {
    name: "snes_mem_read_dword",
    description: "Read 4 bytes little-endian.",
    inputSchema: {
      type: "object",
      properties: { space: { type: "string" }, addr: { type: "integer" } },
      required: ["space", "addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.mem.readDword(asStr(a.space, "space") as never, asNum(a.addr, "addr")),
  },
  {
    name: "snes_mem_read_range",
    description: "Read up to 256 bytes. Returns a hex string (no separator).",
    inputSchema: {
      type: "object",
      properties: {
        space: { type: "string" },
        addr: { type: "integer" },
        n: { type: "integer", minimum: 1, maximum: 256 },
      },
      required: ["space", "addr", "n"],
      additionalProperties: false,
    },
    handler: (c, a) => c.mem.readRange(asStr(a.space, "space") as never, asNum(a.addr, "addr"), asNum(a.n, "n")),
  },
  {
    name: "snes_mem_search",
    description: "Find a hex byte pattern in a memory space. Returns up to maxResults addresses.",
    inputSchema: {
      type: "object",
      properties: {
        space: { type: "string" },
        patternHex: { type: "string", description: "Even-length hex, no separator (e.g. '48454C4C4F' for HELLO)" },
        startAddr: { type: "integer", default: 0 },
        length: { type: "integer", default: 0, description: "0 = to end of space" },
        maxResults: { type: "integer", minimum: 1, maximum: 100, default: 10 },
      },
      required: ["space", "patternHex"],
      additionalProperties: false,
    },
    handler: (c, a) => c.mem.search(
      asStr(a.space, "space") as never,
      asStr(a.patternHex, "patternHex"),
      optNum(a.startAddr, "startAddr", 0),
      optNum(a.length, "length", 0),
      optNum(a.maxResults, "maxResults", 10),
    ),
  },

  // ---------- bp ----------
  {
    name: "snes_bp_add",
    description: "Install an exec/read/write breakpoint at addr (CPU-relative). Returns the bp id.",
    inputSchema: {
      type: "object",
      properties: {
        addr: { type: "integer" },
        type: { type: "string", enum: ["exec", "read", "write"], default: "exec" },
      },
      required: ["addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.bp.add(asNum(a.addr, "addr"), (a.type as never) ?? "exec"),
  },
  {
    name: "snes_bp_clear",
    description: "Remove one breakpoint by id.",
    inputSchema: {
      type: "object",
      properties: { id: { type: "integer" } },
      required: ["id"],
      additionalProperties: false,
    },
    handler: (c, a) => c.bp.clear(asNum(a.id, "id")),
  },
  {
    name: "snes_bp_clear_all",
    description: "Remove all breakpoints. Returns the number removed.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.bp.clearAll(),
  },
  {
    name: "snes_bp_list",
    description: "List all currently-installed breakpoints (id, addr, type).",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.bp.list(),
  },
  {
    name: "snes_bp_wait_for_hit",
    description: "Resume the emulator and block until any pre-installed bp fires. Returns {PC, Hit:true}. Throws on timeout.",
    inputSchema: {
      type: "object",
      properties: { timeoutMs: { type: "integer", minimum: 100, maximum: 60000, default: 10000 } },
      additionalProperties: false,
    },
    handler: (c, a) => c.bp.waitForHit(optNum(a.timeoutMs, "timeoutMs", 10000)),
  },

  // ---------- ppu ----------
  {
    name: "snes_ppu_register",
    description: "Read one PPU state field. Useful names: bg_mode, scanline, brightness, vram_addr, cgram_addr, main_screen, frame.",
    inputSchema: {
      type: "object",
      properties: { name: { type: "string" } },
      required: ["name"],
      additionalProperties: false,
    },
    handler: (c, a) => c.ppu.register(asStr(a.name, "name") as never),
  },
  {
    name: "snes_ppu_state",
    description: "Compact PPU state bundle (~250 bytes): Scanline, Frame, BgMode, ForcedBlank, Brightness, VramAddr, MainScreen, etc.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.ppu.state(),
  },

  // ---------- snap ----------
  {
    name: "snes_snap_save",
    description: "Save the current emulator state to a server-side snapshot. Returns the snapshot id.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.snap.save(),
  },
  {
    name: "snes_snap_restore",
    description: "Restore a previously-saved snapshot by id. Returns false for unknown ids.",
    inputSchema: {
      type: "object",
      properties: { id: { type: "integer" } },
      required: ["id"],
      additionalProperties: false,
    },
    handler: (c, a) => c.snap.restore(asNum(a.id, "id")),
  },
  {
    name: "snes_snap_list",
    description: "List active snapshot ids.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    handler: c => c.snap.list(),
  },
  {
    name: "snes_snap_discard",
    description: "Delete a snapshot by id (frees the server-side state file).",
    inputSchema: {
      type: "object",
      properties: { id: { type: "integer" } },
      required: ["id"],
      additionalProperties: false,
    },
    handler: (c, a) => c.snap.discard(asNum(a.id, "id")),
  },

  // ---------- disasm ----------
  {
    name: "snes_disasm_at",
    description: "Disassemble up to N (≤64) instructions at addr. Returns [{Addr, Bytes, Text, Size}]; Text includes symbol resolution.",
    inputSchema: {
      type: "object",
      properties: {
        addr: { type: "integer" },
        n: { type: "integer", minimum: 1, maximum: 64, default: 10 },
      },
      required: ["addr"],
      additionalProperties: false,
    },
    handler: (c, a) => c.disasm.at(asNum(a.addr, "addr"), optNum(a.n, "n", 10)),
  },
];
