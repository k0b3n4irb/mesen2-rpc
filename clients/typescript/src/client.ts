/**
 * Typed JSON-RPC client for the mesen2-rpc server.
 *
 * Method names mirror the [JsonRpcMethod("namespace.action")] attributes in
 * Mesen2/UI/Utilities/RpcServer.cs. Argument order matches the C# method
 * signatures positionally — the server's StreamJsonRpc dispatcher accepts
 * arrays.
 *
 * Token discipline: every wrapper here is a thin pass-through. The server
 * does the cap-checking and small-response shaping; the client must not
 * re-implement those bounds (drift risk).
 */

import { JsonRpcTransport, type TransportOptions } from "./transport.js";
import type {
  CpuState, PpuState, RunUntilResult, BpListEntry, DisasmRow,
  MemorySpace, BpType, Register, PpuField,
} from "./types.js";

export class Mesen2Client {
  private readonly transport: JsonRpcTransport;

  constructor(opts: TransportOptions) {
    this.transport = new JsonRpcTransport(opts);
  }

  // ------------------------------------------------------------------
  // Lifecycle
  // ------------------------------------------------------------------

  async connect(): Promise<void> {
    await this.transport.connect();
  }

  close(): void {
    this.transport.close();
  }

  // ------------------------------------------------------------------
  // emu.*
  // ------------------------------------------------------------------

  readonly emu = {
    loadRom: (path: string): Promise<boolean> =>
      this.transport.call("emu.load_rom", [path]),

    reset: (): Promise<boolean> =>
      this.transport.call("emu.reset"),

    /** Advances exactly n frames via DebugApi.Step(PpuFrame). Capped at 600/call by the server. */
    runFrames: (n: number): Promise<number> =>
      this.transport.call("emu.run_frames", [n], Math.max(30_000, n * 100)),

    isPaused: (): Promise<boolean> =>
      this.transport.call("emu.is_paused"),
  };

  // ------------------------------------------------------------------
  // cpu.*
  // ------------------------------------------------------------------

  readonly cpu = {
    pc: (): Promise<number> => this.transport.call("cpu.pc"),

    register: (name: Register): Promise<number> =>
      this.transport.call("cpu.register", [name]),

    state: (): Promise<CpuState> => this.transport.call("cpu.state"),

    /** Single 65816 instruction step. Returns PC after step. */
    step: (): Promise<number> => this.transport.call("cpu.step"),

    /** N-instruction step via native count. Capped at 10000/call. */
    stepN: (n: number): Promise<number> =>
      this.transport.call("cpu.step_n", [n], Math.max(10_000, n * 5)),

    /**
     * Composite: install temporary exec breakpoint at addr, resume, wait for
     * the BP-source CodeBreak, return state. Throws RpcException on timeout.
     */
    runUntil: (addr: number, timeoutMs = 5000): Promise<RunUntilResult> =>
      this.transport.call("cpu.run_until", [addr, timeoutMs], timeoutMs + 2000),
  };

  // ------------------------------------------------------------------
  // mem.*
  // ------------------------------------------------------------------

  readonly mem = {
    readByte: (space: MemorySpace, addr: number): Promise<number> =>
      this.transport.call("mem.read_byte", [space, addr]),

    readWord: (space: MemorySpace, addr: number): Promise<number> =>
      this.transport.call("mem.read_word", [space, addr]),

    readDword: (space: MemorySpace, addr: number): Promise<number> =>
      this.transport.call("mem.read_dword", [space, addr]),

    /** Returns a hex string (no separator). Capped at 256 bytes/call. */
    readRange: (space: MemorySpace, addr: number, n: number): Promise<string> =>
      this.transport.call("mem.read_range", [space, addr, n]),

    /** Byte-pattern search. patternHex is even-length hex, no separator. */
    search: (
      space: MemorySpace,
      patternHex: string,
      startAddr = 0,
      length = 0,
      maxResults = 10,
    ): Promise<number[]> =>
      this.transport.call("mem.search", [space, patternHex, startAddr, length, maxResults]),
  };

  // ------------------------------------------------------------------
  // bp.*
  // ------------------------------------------------------------------

  readonly bp = {
    /** Returns the new BP id. Address is interpreted in SnesMemory space (CPU-relative). */
    add: (addr: number, type: BpType = "exec"): Promise<number> =>
      this.transport.call("bp.add", [addr, type]),

    clear: (id: number): Promise<boolean> =>
      this.transport.call("bp.clear", [id]),

    /** Returns number of breakpoints removed. */
    clearAll: (): Promise<number> =>
      this.transport.call("bp.clear_all"),

    list: (): Promise<BpListEntry[]> =>
      this.transport.call("bp.list"),

    /**
     * Resumes the emu and blocks until any pre-installed BP fires.
     * Throws RpcException on timeout (also reports PC at timeout).
     */
    waitForHit: (timeoutMs = 10_000): Promise<RunUntilResult> =>
      this.transport.call("bp.wait_for_hit", [timeoutMs], timeoutMs + 2000),
  };

  // ------------------------------------------------------------------
  // ppu.*
  // ------------------------------------------------------------------

  readonly ppu = {
    register: (field: PpuField): Promise<number | boolean> =>
      this.transport.call("ppu.register", [field]),

    state: (): Promise<PpuState> => this.transport.call("ppu.state"),
  };

  // ------------------------------------------------------------------
  // snap.* (file-backed; per-server-instance lifetime)
  // ------------------------------------------------------------------

  readonly snap = {
    save: (): Promise<number> => this.transport.call("snap.save"),
    restore: (id: number): Promise<boolean> =>
      this.transport.call("snap.restore", [id]),
    list: (): Promise<number[]> => this.transport.call("snap.list"),
    discard: (id: number): Promise<boolean> =>
      this.transport.call("snap.discard", [id]),
  };

  // ------------------------------------------------------------------
  // disasm.*
  // ------------------------------------------------------------------

  readonly disasm = {
    /** Returns N disassembled rows starting at addr (SnesMemory). Capped at 64/call. */
    at: (addr: number, n = 10): Promise<DisasmRow[]> =>
      this.transport.call("disasm.at", [addr, n]),
  };
}
