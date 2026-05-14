/**
 * Response shapes for typed RPC methods. These mirror the C# server's
 * anonymous-record returns in Mesen2/UI/Utilities/RpcServer.cs — keep in
 * sync when the server surface changes.
 */

export type MemorySpace =
  | "cpu" | "snes" | "wram" | "vram" | "cgram" | "oam" | "sram" | "rom" | "prgrom";

export type BpType = "exec" | "execute" | "x" | "read" | "r" | "write" | "w";

export type Register =
  | "A" | "X" | "Y" | "SP" | "S" | "D" | "DP" | "PC"
  | "K" | "PB" | "PBR" | "DBR" | "DB" | "P" | "PS" | "FLAGS";

export type PpuField =
  | "bg_mode" | "bgmode"
  | "scanline" | "cycle" | "h_cycle" | "hclock" | "h_clock"
  | "frame" | "frame_count"
  | "forced_blank" | "forcedblank"
  | "brightness" | "screen_brightness"
  | "vram_address" | "vram_addr"
  | "vram_inc" | "vram_increment"
  | "cgram_address" | "cgram_addr"
  | "oam_address" | "oam_addr"
  | "main_screen" | "main_screen_layers"
  | "sub_screen" | "sub_screen_layers"
  | "mosaic_size" | "mosaic_enabled"
  | "hi_res" | "hires"
  | "interlace" | "screen_interlace"
  | "overscan" | "overscan_mode";

export interface CpuState {
  A: number;
  X: number;
  Y: number;
  SP: number;
  D: number;
  PC: number;
  K: number;
  DBR: number;
  P: number;
  EmuMode: boolean;
  Cycle: number;
}

export interface PpuState {
  Scanline: number;
  HClock: number;
  Frame: number;
  BgMode: number;
  ForcedBlank: boolean;
  Brightness: number;
  VramAddr: number;
  CgramAddr: number;
  OamAddr: number;
  MainScreen: number;
  SubScreen: number;
  HiRes: boolean;
  Interlace: boolean;
  Overscan: boolean;
}

export interface RunUntilResult {
  PC: number;
  Hit: boolean;
}

export interface BpListEntry {
  Id: number;
  Addr: number;
  Type: string;
}

export interface DisasmRow {
  Addr: number;
  /** Hex string, no separator. e.g. "A901" for `LDA #$01`. */
  Bytes: string;
  /** Disassembled mnemonic with symbol resolution from any loaded .sym file. */
  Text: string;
  Size: number;
}
