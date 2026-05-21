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

    /** Write one byte. Bypasses CPU memory protection (routes via Debugger MemoryDumper). */
    writeByte: (space: MemorySpace, addr: number, value: number): Promise<boolean> =>
      this.transport.call("mem.write_byte", [space, addr, value]),

    /** Write 2 bytes little-endian (symmetric with readWord). */
    writeWord: (space: MemorySpace, addr: number, value: number): Promise<boolean> =>
      this.transport.call("mem.write_word", [space, addr, value]),

    /**
     * Write up to 256 bytes from a hex string (no separator). Symmetric
     * with readRange — `writeRange(s, a, await readRange(s, a, n))` is a
     * no-op. Server throws on odd-length hex or n > 256.
     */
    writeRange: (space: MemorySpace, addr: number, hex: string): Promise<boolean> =>
      this.transport.call("mem.write_range", [space, addr, hex]),
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

  // ------------------------------------------------------------------
  // input.*
  // ------------------------------------------------------------------

  readonly input = {
    /**
     * Override controller `port` (0..7) to hold the buttons in the mask.
     * Layout matches the SNES joypad register ($4218 hi-byte + $4219
     * lo-byte) and OpenSNES KEY_* constants:
     *   B=0x8000  Y=0x4000  SELECT=0x2000  START=0x1000
     *   UP=0x0800 DOWN=0x0400 LEFT=0x0200   RIGHT=0x0100
     *   A=0x0080  X=0x0040  L=0x0020       R=0x0010
     * Pass `buttons=0` to release all. Persists until next set; cleared
     * on emu.load_rom.
     */
    set: (port: number, buttons: number): Promise<boolean> =>
      this.transport.call("input.set", [port, buttons]),

    /**
     * Override the SnesMouse on `port` with per-frame displacement and
     * button state. The port must currently host a SnesMouse device —
     * call `controller.connect(port, "mouse")` first to switch.
     * Persists until cleared; pass dx=0 dy=0 left=false right=false to
     * neutralise.
     */
    setMouse: (
      port: number,
      dx: number,
      dy: number,
      left: boolean,
      right: boolean,
    ): Promise<boolean> =>
      this.transport.call("input.set_mouse", [port, dx, dy, left, right]),

    /**
     * Override the SuperScope on `port` with absolute screen coordinates
     * (x in 0..255, y in 0..223 NTSC visible) and the four scope buttons.
     * Fire/cursor with valid coords triggers the PPU H/V latch on the
     * next frame. Pass x=-1 or y=-1 to signal off-screen (sets bit 0x40).
     * The port must currently host a SuperScope device — call
     * `controller.connect(port, "scope")` first to switch.
     */
    setScope: (
      port: number,
      x: number,
      y: number,
      fire: boolean,
      cursor: boolean,
      turbo: boolean,
      pause: boolean,
    ): Promise<boolean> =>
      this.transport.call("input.set_scope", [port, x, y, fire, cursor, turbo, pause]),
  };

  // ------------------------------------------------------------------
  // controller.*
  // ------------------------------------------------------------------

  readonly controller = {
    /**
     * Hot-swap the controller type on `port` (0 = Port1, 1 = Port2).
     * Types: "controller" (standard SNES pad), "mouse" (SnesMouse),
     * "scope" (SuperScope), "none" (disconnect). After switching, drive
     * state via input.set / input.setMouse / input.setScope.
     */
    connect: (port: number, type: "controller" | "mouse" | "scope" | "none"): Promise<boolean> =>
      this.transport.call("controller.connect", [port, type]),
  };
}
