using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Utilities
{
	/// <summary>
	/// Headless RPC-server entry point.
	///
	/// Mirrors TestRunner.Run() but, instead of polling-until-timeout,
	/// exposes the debugger surface (CPU/PPU/memory/breakpoints/trace)
	/// over TCP/JSON-RPC for autonomous AI-driven SNES debugging.
	///
	/// Wire format: JSON-RPC 2.0 over TCP via StreamJsonRpc's default
	/// HeaderDelimitedMessageHandler (LSP-style `Content-Length: N\r\n\r\n`
	/// + JSON body). The Node/TypeScript client library will speak this
	/// format directly via the `vscode-jsonrpc` package.
	///
	/// Token discipline (per the plan): every method returns the smallest
	/// useful value. `cpu.pc` returns a single int, not a CPU state struct.
	/// Further methods add fine-grained tools per the surface in
	/// RpcMethods (kept in this file for Phase 1; will move to its own
	/// file as the surface grows).
	/// </summary>
	internal class RpcServer
	{
		internal static int Run(string[] args)
		{
			ConfigManager.DisableSaveSettings = true;
			CommandLineHelper commandLineHelper = new(args, true);

			int port = commandLineHelper.RpcServerPort;
			if(port <= 0) {
				Console.Error.WriteLine("rpc-server: invalid or missing --rpc-server=PORT");
				return -1;
			}

			EmuApi.InitDll();
			ConfigManager.Config.ApplyConfig();
			EmuApi.InitializeEmu(ConfigManager.HomeFolder, IntPtr.Zero, IntPtr.Zero,
				useSoftwareRenderer: true,
				noAudio: true,
				noVideo: true,
				noInput: true);
			EmuApi.Pause();

			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);

			RpcMethods methods = new();

			if(commandLineHelper.FilesToLoad.Count == 1) {
				if(!EmuApi.LoadRom(commandLineHelper.FilesToLoad[0], string.Empty)) {
					Console.Error.WriteLine($"rpc-server: failed to load ROM {commandLineHelper.FilesToLoad[0]}");
					EmuApi.Release();
					return -1;
				}
				DebugWorkspaceManager.Load();
				methods.EnsureDebuggerInitialized();
			}

			TcpListener listener = new(IPAddress.Loopback, port);
			try {
				listener.Start();
			} catch(SocketException ex) {
				Console.Error.WriteLine($"rpc-server: failed to bind 127.0.0.1:{port} — {ex.Message}");
				EmuApi.Release();
				return -1;
			}
			Console.Error.WriteLine($"rpc-server: listening on 127.0.0.1:{port}");

			CancellationTokenSource cts = new();
			Console.CancelKeyPress += (_, e) => {
				e.Cancel = true;
				cts.Cancel();
				listener.Stop();
			};

			try {
				while(!cts.IsCancellationRequested) {
					TcpClient client = listener.AcceptTcpClient();
					_ = Task.Run(() => HandleClient(client, methods, cts.Token));
				}
			} catch(SocketException) {
				//Listener stopped — clean shutdown path
			} catch(ObjectDisposedException) {
				//Listener was disposed on Ctrl-C — also clean
			}

			methods.ShutdownDebugger();
			EmuApi.Stop();
			EmuApi.Release();
			return 0;
		}

		private static async Task HandleClient(TcpClient client, RpcMethods methods, CancellationToken ct)
		{
			try {
				using(client) {
					NetworkStream stream = client.GetStream();
					using JsonRpc rpc = JsonRpc.Attach(stream, methods);
					await rpc.Completion.WaitAsync(ct);
				}
			} catch(OperationCanceledException) {
				//shutdown via cts
			} catch(Exception ex) {
				Console.Error.WriteLine($"rpc-server: client error: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// JSON-RPC method surface. Each `[JsonRpcMethod("dotted.name")]`
	/// method becomes a callable RPC endpoint. Keep methods fine-grained
	/// and small-response per the token-discipline guardrails in the plan.
	/// </summary>
	public class RpcMethods
	{
		[JsonRpcMethod("cpu.pc")]
		public uint CpuPc()
		{
			SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return state.PC;
		}

		[JsonRpcMethod("cpu.register")]
		public uint CpuRegister(string name)
		{
			SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return name.ToUpperInvariant() switch {
				"A" => s.A,
				"X" => s.X,
				"Y" => s.Y,
				"SP" or "S" => s.SP,
				"D" or "DP" => s.D,
				"PC" => s.PC,
				"K" or "PB" or "PBR" => s.K,
				"DBR" or "DB" => s.DBR,
				"P" or "PS" or "FLAGS" => (byte)s.PS,
				_ => throw new LocalRpcException($"unknown register '{name}' (use A/X/Y/SP/D/PC/K/DBR/P)")
					{ ErrorCode = -32602 }
			};
		}

		[JsonRpcMethod("cpu.state")]
		public object CpuState()
		{
			//Compact return: ~80 bytes JSON encoded. All 65816 registers in
			//one shot, but no extra metadata (no descriptions, no flag parse).
			SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return new {
				A = s.A,
				X = s.X,
				Y = s.Y,
				SP = s.SP,
				D = s.D,
				PC = s.PC,
				K = s.K,
				DBR = s.DBR,
				P = (byte)s.PS,
				EmuMode = s.EmulationMode,
				Cycle = s.CycleCount,
			};
		}

		[JsonRpcMethod("mem.read_word")]
		public uint MemReadWord(string space, uint addr)
		{
			MemoryType type = ParseMemorySpace(space);
			byte[] buf = DebugApi.GetMemoryValues(type, addr, addr + 1);
			return (uint)(buf[0] | (buf[1] << 8));
		}

		[JsonRpcMethod("mem.read_dword")]
		public uint MemReadDword(string space, uint addr)
		{
			MemoryType type = ParseMemorySpace(space);
			byte[] buf = DebugApi.GetMemoryValues(type, addr, addr + 3);
			return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
		}

		[JsonRpcMethod("mem.read_range")]
		public string MemReadRange(string space, uint addr, uint n)
		{
			const uint maxPerCall = 256;
			if(n == 0 || n > maxPerCall) {
				throw new LocalRpcException(
					$"mem.read_range: n must be 1..{maxPerCall} (got {n})")
					{ ErrorCode = -32602 };
			}
			MemoryType type = ParseMemorySpace(space);
			byte[] buf = DebugApi.GetMemoryValues(type, addr, addr + n - 1);
			//Compact hex string (no separators, no 0x prefix) — 2 chars per
			//byte. Caller decodes with Buffer.from(s, 'hex') / equivalent.
			return Convert.ToHexString(buf);
		}

		//---------------------------------------------------------------
		// Memory write
		//
		// Writes target the SAME memory spaces accepted by mem.read_*.
		// Backed by DebugApi.SetMemoryValue(s), which routes through
		// MemoryDumper — bypasses CPU memory protection (writes to ROM
		// patch the in-memory image; the .sfc file on disk is untouched).
		// Useful for: injecting input state into pad_keys, skipping a
		// title screen by overwriting game_state, patching a flag mid-run
		// to exercise an else branch.
		//---------------------------------------------------------------

		[JsonRpcMethod("mem.write_byte")]
		public bool MemWriteByte(string space, uint addr, byte value)
		{
			MemoryType type = ParseMemorySpace(space);
			DebugApi.SetMemoryValue(type, addr, value);
			return true;
		}

		[JsonRpcMethod("mem.write_word")]
		public bool MemWriteWord(string space, uint addr, uint value)
		{
			//Little-endian: low byte at addr, high byte at addr+1.
			//Matches mem.read_word's decoding so the round-trip is
			//symmetric. Truncates to 16 bits silently.
			MemoryType type = ParseMemorySpace(space);
			DebugApi.SetMemoryValue(type, addr, (byte)(value & 0xFF));
			DebugApi.SetMemoryValue(type, addr + 1, (byte)((value >> 8) & 0xFF));
			return true;
		}

		[JsonRpcMethod("mem.write_range")]
		public bool MemWriteRange(string space, uint addr, string hex)
		{
			//`hex` is a compact (no separator) lower- or upper-case hex
			//string. Symmetric with mem.read_range's output format, so
			//`mem.write_range(s, a, mem.read_range(s, a, n))` is a no-op.
			//Capped at the same 256 bytes per call for the same token
			//discipline reason.
			const int maxPerCall = 256;
			if(hex.Length % 2 != 0) {
				throw new LocalRpcException(
					$"mem.write_range: hex length must be even (got {hex.Length})")
					{ ErrorCode = -32602 };
			}
			int n = hex.Length / 2;
			if(n == 0 || n > maxPerCall) {
				throw new LocalRpcException(
					$"mem.write_range: byte count must be 1..{maxPerCall} (got {n})")
					{ ErrorCode = -32602 };
			}
			byte[] data;
			try {
				data = Convert.FromHexString(hex);
			} catch(FormatException ex) {
				throw new LocalRpcException(
					$"mem.write_range: invalid hex string ({ex.Message})")
					{ ErrorCode = -32602 };
			}
			MemoryType type = ParseMemorySpace(space);
			DebugApi.SetMemoryValues(type, addr, data, data.Length);
			return true;
		}

		[JsonRpcMethod("emu.load_rom")]
		public bool EmuLoadRom(string path)
		{
			bool ok = EmuApi.LoadRom(path, string.Empty);
			if(ok) {
				DebugWorkspaceManager.Load();
				EnsureDebuggerInitialized();
			}
			return ok;
		}

		//Called once per ROM load to wire up the debugger machinery
		//(SetBreakpoints / Step / memory callbacks). The C++ side crashes
		//if InitializeDebugger fires without a loaded ROM, so we gate it.
		private bool _debuggerInitialized = false;
		private NotificationListener? _notificationListener;
		private readonly ManualResetEventSlim _codeBreakSignal = new(false);

		internal bool IsDebuggerInitialized => _debuggerInitialized;
		internal void EnsureDebuggerInitialized()
		{
			if(_debuggerInitialized) {
				return;
			}
			//Note: ConsoleMode flag causes Emulator.cpp:495 to call
			//InitDebugger() automatically during LoadRom. We don't call
			//DebugApi.InitializeDebugger() here — that would be a
			//redundant init that has historically been the source of
			//SIGSEGV when called pre-ROM.

			//Register a notification listener so we can detect when a
			//breakpoint pauses execution. EmuApi.IsPaused() does NOT reflect
			//the debugger's internal stop state (Debugger::SleepUntilResume
			//in C++ blocks the emu thread but doesn't toggle the "paused"
			//flag visible via the public API). The CodeBreak notification is
			//the canonical hook, fired from the C++ debugger when a BP hits.
			_notificationListener = new NotificationListener();
			_notificationListener.OnNotification += (e) => {
				if(e.NotificationType == ConsoleNotificationType.CodeBreak) {
					//CodeBreak fires for all break sources: real breakpoint hits,
					//but also PpuStep (e.g. from RunSingleFrame / Step(PpuFrame)),
					//Pause (from EmuApi.Pause which Step()s 1 cycle with Pause source),
					//and Step/StepOver/etc. We only want to wake cpu.run_until when
					//the source is a real Breakpoint hit. The discriminator below
					//is what makes cpu.run_until's wait-for-BP semantics correct.
					BreakEvent evt = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
					if(evt.Source == BreakSource.Breakpoint) {
						_codeBreakSignal.Set();
					}
				}
			};

			_debuggerInitialized = true;
		}

		internal void ShutdownDebugger()
		{
			_notificationListener?.Dispose();
			_notificationListener = null;
			//ConsoleMode init pairs with ReleaseDebugger on emu Release —
			//don't call ReleaseDebugger explicitly to avoid double-release.
			_debuggerInitialized = false;

			//Best-effort cleanup of snapshot files. If the server crashes
			//these may remain in /tmp; they're prefixed with mesen-rpc-snap
			//and tagged with PID so they're easy to grep + sweep later.
			if(_snapDir != null) {
				try { Directory.Delete(_snapDir, recursive: true); } catch { /* ignore */ }
				_snapDir = null;
			}
		}

		[JsonRpcMethod("emu.reset")]
		public bool EmuReset()
		{
			EmuApi.ExecuteShortcut(new ExecuteShortcutParams { Shortcut = EmulatorShortcut.Reset });
			return true;
		}

		[JsonRpcMethod("emu.run_frames")]
		public uint EmuRunFrames(uint n)
		{
			//Default: cap at 600 frames (~10s at 60Hz) per call to keep latency
			//bounded. Caller paginates by repeated calls if they need more.
			const uint maxPerCall = 600;
			if(n > maxPerCall) {
				throw new LocalRpcException($"n={n} exceeds max {maxPerCall}; call multiple times")
					{ ErrorCode = -32602 };
			}

			//Earlier implementations used EmulatorShortcut.RunSingleFrame, but
			//ShortcutKeyHandler latches it into auto-repeat mode (20fps press-and-hold
			//emulation) without a paired ReleaseShortcut, which then keeps re-installing
			//PpuStep break requests every 50ms — corrupting any subsequent free-run
			//(e.g. cpu.run_until). DebugApi.Step(PpuFrame) advances exactly one frame
			//via the debugger Step path, no auto-repeat, no latched shortcut state.
			for(uint i = 0; i < n; i++) {
				DebugApi.Step(CpuType.Snes, 1, StepType.PpuFrame);

				//Wait for the resulting pause. Frame at 60Hz = ~16.7ms; give 200ms
				//before treating it as a hang. EmuApi.IsPaused() routes through
				//Debugger::IsPaused which reflects _waitForBreakResume.
				int waitedMs = 0;
				while(!EmuApi.IsPaused() && waitedMs < 200) {
					Thread.Sleep(1);
					waitedMs++;
				}
				if(!EmuApi.IsPaused()) {
					throw new LocalRpcException(
						$"emu.run_frames: timeout waiting for frame {i+1}/{n} to complete")
						{ ErrorCode = -32603 };
				}
			}
			return n;
		}

		[JsonRpcMethod("mem.read_byte")]
		public byte MemReadByte(string space, uint addr)
		{
			MemoryType type = ParseMemorySpace(space);
			byte[] buf = DebugApi.GetMemoryValues(type, addr, addr);
			return buf[0];
		}

		[JsonRpcMethod("emu.is_paused")]
		public bool EmuIsPaused()
		{
			return EmuApi.IsPaused();
		}

		//---------------------------------------------------------------
		// PPU
		//---------------------------------------------------------------

		[JsonRpcMethod("ppu.register")]
		public object PpuRegister(string name)
		{
			//Token-discipline: return ONE value per call, not the whole struct.
			//Field names mirror Mesen2's SnesPpuState (case-insensitive,
			//common short aliases accepted).
			SnesPpuState p = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
			return name.ToLowerInvariant() switch {
				"bgmode" or "bg_mode" => (object)p.BgMode,
				"scanline" => p.Scanline,
				"cycle" or "h_cycle" => p.Cycle,
				"hclock" or "h_clock" => p.HClock,
				"frame" or "frame_count" => p.FrameCount,
				"forcedblank" or "forced_blank" => p.ForcedBlank,
				"brightness" or "screen_brightness" => p.ScreenBrightness,
				"vram_address" or "vram_addr" => p.VramAddress,
				"vram_inc" or "vram_increment" => p.VramIncrementValue,
				"cgram_address" or "cgram_addr" => p.CgramAddress,
				"oam_address" or "oam_addr" => p.OamRamAddress,
				"main_screen" or "main_screen_layers" => p.MainScreenLayers,
				"sub_screen" or "sub_screen_layers" => p.SubScreenLayers,
				"mosaic_size" => p.MosaicSize,
				"mosaic_enabled" => p.MosaicEnabled,
				"hi_res" or "hires" => p.HiResMode,
				"interlace" or "screen_interlace" => p.ScreenInterlace,
				"overscan" or "overscan_mode" => p.OverscanMode,
				_ => throw new LocalRpcException(
					$"unknown PPU register '{name}' (try bg_mode / scanline / vram_address / cgram_address / forced_blank / brightness / main_screen / sub_screen)")
					{ ErrorCode = -32602 }
			};
		}

		//---------------------------------------------------------------
		// Disassembly
		//---------------------------------------------------------------

		[JsonRpcMethod("disasm.at")]
		public object DisasmAt(uint addr, int n = 10)
		{
			//Token discipline: cap n at 64 instructions per call.
			//Each row is ~60 bytes JSON-encoded → max ~4KB response.
			const int maxRows = 64;
			if(n <= 0 || n > maxRows) {
				throw new LocalRpcException(
					$"disasm.at: n must be 1..{maxRows} (got {n})")
					{ ErrorCode = -32602 };
			}

			CodeLineData[] lines = DebugApi.GetDisassemblyOutput(CpuType.Snes, addr, (uint)n);

			//Filter out non-instruction rows (labels, comments, block markers,
			//empty lines) — they pollute the response and waste tokens. Keep
			//only rows with a valid address and non-empty mnemonic text.
			var result = lines
				.Where(l => l.HasAddress && !string.IsNullOrEmpty(l.Text))
				.Select(l => new {
					Addr = (uint)l.Address,
					Bytes = Convert.ToHexString(l.ByteCode, 0, l.OpSize),
					Text = l.Text.Trim(),
					Size = (int)l.OpSize,
				})
				.ToArray();
			return result;
		}

		//---------------------------------------------------------------
		// Memory search
		//---------------------------------------------------------------

		[JsonRpcMethod("mem.search")]
		public object MemSearch(string space, string patternHex,
			uint startAddr = 0, uint length = 0, int maxResults = 10)
		{
			//Search for a byte sequence in the given memory space. patternHex is
			//a hex string ("AABB", "DEADBEEF" — no separators, no 0x prefix).
			//startAddr/length default to the full memory space for that type.
			//maxResults caps how many matches we report (token discipline).
			const int maxResultsCap = 100;
			if(maxResults <= 0 || maxResults > maxResultsCap) {
				throw new LocalRpcException(
					$"mem.search: maxResults must be 1..{maxResultsCap} (got {maxResults})")
					{ ErrorCode = -32602 };
			}

			byte[] pattern;
			try {
				pattern = Convert.FromHexString(patternHex);
			} catch {
				throw new LocalRpcException(
					$"mem.search: invalid hex pattern '{patternHex}' (must be even-length hex, no separators)")
					{ ErrorCode = -32602 };
			}
			if(pattern.Length == 0 || pattern.Length > 64) {
				throw new LocalRpcException(
					$"mem.search: pattern length must be 1..64 bytes (got {pattern.Length})")
					{ ErrorCode = -32602 };
			}

			MemoryType type = ParseMemorySpace(space);
			uint memSize = (uint)DebugApi.GetMemorySize(type);
			if(length == 0) {
				length = memSize - startAddr;
			}
			if(startAddr >= memSize || startAddr + length > memSize) {
				throw new LocalRpcException(
					$"mem.search: range ${startAddr:X}..${startAddr+length:X} exceeds memory size ${memSize:X}")
					{ ErrorCode = -32602 };
			}

			byte[] buf = DebugApi.GetMemoryValues(type, startAddr, startAddr + length - 1);

			//Naive linear scan — fine for typical SNES address spaces (max 16MB ROM).
			//Bails on first `maxResults` matches to stay token-bounded.
			List<uint> hits = new();
			int last = buf.Length - pattern.Length;
			for(int i = 0; i <= last; i++) {
				bool match = true;
				for(int j = 0; j < pattern.Length; j++) {
					if(buf[i + j] != pattern[j]) { match = false; break; }
				}
				if(match) {
					hits.Add(startAddr + (uint)i);
					if(hits.Count >= maxResults) break;
				}
			}
			return hits.ToArray();
		}

		//---------------------------------------------------------------
		// Snapshots (state save/restore — file-backed for unbounded IDs)
		//---------------------------------------------------------------

		private readonly Dictionary<int, string> _snapshots = new();
		private int _nextSnapId = 1;
		private readonly object _snapLock = new();
		private string? _snapDir;

		private string EnsureSnapDir()
		{
			//Lazily allocate a per-server-instance temp dir for snapshot
			//files. Cleanup happens in ShutdownDebugger; clients should not
			//rely on snapshots surviving server restart.
			if(_snapDir == null) {
				_snapDir = Path.Combine(Path.GetTempPath(),
					$"mesen-rpc-snap-{Environment.ProcessId}");
				Directory.CreateDirectory(_snapDir);
			}
			return _snapDir;
		}

		[JsonRpcMethod("snap.save")]
		public int SnapSave()
		{
			int id;
			lock(_snapLock) {
				id = _nextSnapId++;
				string path = Path.Combine(EnsureSnapDir(), $"{id}.mss");
				EmuApi.SaveStateFile(path);
				_snapshots[id] = path;
			}
			return id;
		}

		[JsonRpcMethod("snap.restore")]
		public bool SnapRestore(int id)
		{
			lock(_snapLock) {
				if(!_snapshots.TryGetValue(id, out string? path)) {
					return false;
				}
				EmuApi.LoadStateFile(path);
				return true;
			}
		}

		[JsonRpcMethod("snap.list")]
		public object SnapList()
		{
			lock(_snapLock) {
				return _snapshots.Keys.OrderBy(k => k).ToArray();
			}
		}

		[JsonRpcMethod("snap.discard")]
		public bool SnapDiscard(int id)
		{
			//Explicit cleanup. Not in the original 30-tool plan but cheap
			//to add and very useful — without it, a long session leaks
			//state files in /tmp.
			lock(_snapLock) {
				if(!_snapshots.TryGetValue(id, out string? path)) {
					return false;
				}
				try { File.Delete(path); } catch { /* ignore */ }
				_snapshots.Remove(id);
				return true;
			}
		}

		[JsonRpcMethod("ppu.state")]
		public object PpuState()
		{
			//Compact dump of the diagnostically-useful PPU state fields.
			//~200 bytes JSON-encoded. Mirrors what a human reading Mesen2's
			//PPU Viewer would care about; excludes per-layer arrays and
			//windowing config (use ppu.layer / ppu.window when those land).
			SnesPpuState p = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
			return new {
				Scanline = p.Scanline,
				HClock = p.HClock,
				Frame = p.FrameCount,
				BgMode = p.BgMode,
				ForcedBlank = p.ForcedBlank,
				Brightness = p.ScreenBrightness,
				VramAddr = p.VramAddress,
				CgramAddr = p.CgramAddress,
				OamAddr = p.OamRamAddress,
				MainScreen = p.MainScreenLayers,
				SubScreen = p.SubScreenLayers,
				HiRes = p.HiResMode,
				Interlace = p.ScreenInterlace,
				Overscan = p.OverscanMode,
			};
		}

		//---------------------------------------------------------------
		// Breakpoints
		//---------------------------------------------------------------

		private readonly Dictionary<int, InteropBreakpoint> _breakpoints = new();
		private int _nextBpId = 1;
		private readonly object _bpLock = new();

		[JsonRpcMethod("bp.add")]
		public int BpAdd(uint addr, string? type = "exec")
		{
			BreakpointTypeFlags bpType = (type ?? "exec").ToLowerInvariant() switch {
				"exec" or "execute" or "x" => BreakpointTypeFlags.Execute,
				"read" or "r" => BreakpointTypeFlags.Read,
				"write" or "w" => BreakpointTypeFlags.Write,
				_ => throw new LocalRpcException(
					$"bp.add: unknown type '{type}' (use exec/read/write)")
					{ ErrorCode = -32602 }
			};

			int id;
			lock(_bpLock) {
				id = _nextBpId++;
				_breakpoints[id] = new InteropBreakpoint {
					Id = id,
					CpuType = CpuType.Snes,
					MemoryType = MemoryType.SnesMemory,
					Type = bpType,
					StartAddress = (int)addr,
					EndAddress = (int)addr,
					Enabled = true,
					MarkEvent = false,
					IgnoreDummyOperations = false,
					Condition = new byte[1000],
				};
				SyncBreakpoints();
			}
			return id;
		}

		[JsonRpcMethod("bp.clear")]
		public bool BpClear(int id)
		{
			lock(_bpLock) {
				if(_breakpoints.Remove(id)) {
					SyncBreakpoints();
					return true;
				}
				return false;
			}
		}

		[JsonRpcMethod("bp.clear_all")]
		public int BpClearAll()
		{
			lock(_bpLock) {
				int n = _breakpoints.Count;
				_breakpoints.Clear();
				SyncBreakpoints();
				return n;
			}
		}

		[JsonRpcMethod("bp.list")]
		public object BpList()
		{
			lock(_bpLock) {
				return _breakpoints.Select(kv => new {
					Id = kv.Key,
					Addr = (uint)kv.Value.StartAddress,
					Type = kv.Value.Type.ToString().ToLowerInvariant(),
				}).ToArray();
			}
		}

		private void SyncBreakpoints()
		{
			//Caller must hold _bpLock.
			InteropBreakpoint[] bps = _breakpoints.Values.ToArray();
			DebugApi.SetBreakpoints(bps, (uint)bps.Length);
		}

		[JsonRpcMethod("cpu.step")]
		public uint CpuStep()
		{
			//Single instruction step. Pairs with cpu.state to inspect the
			//resulting register file. Block on the resulting pause so the
			//caller knows the step is complete before the next RPC fires.
			if(!_debuggerInitialized) {
				throw new LocalRpcException(
					"cpu.step: debugger not initialized (load a ROM first)")
					{ ErrorCode = -32603 };
			}

			_codeBreakSignal.Reset();
			DebugApi.Step(CpuType.Snes, 1, StepType.Step);

			//A single Step fires CodeBreak with source=CpuStep (not Breakpoint),
			//so we can't reuse the _codeBreakSignal discriminator. Poll
			//EmuApi.IsPaused() which routes through Debugger::IsPaused →
			//_waitForBreakResume, which is set by SleepUntilResume on the
			//step's pause.
			int waitedMs = 0;
			while(!EmuApi.IsPaused() && waitedMs < 100) {
				Thread.Sleep(1);
				waitedMs++;
			}
			SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return s.PC;
		}

		[JsonRpcMethod("cpu.step_n")]
		public uint CpuStepN(int n)
		{
			//Bulk step. Cap at 10000 to keep response time bounded; caller
			//paginates if they need more. Each step is one 65816 instruction.
			const int maxPerCall = 10000;
			if(n <= 0 || n > maxPerCall) {
				throw new LocalRpcException(
					$"cpu.step_n: n must be 1..{maxPerCall} (got {n})")
					{ ErrorCode = -32602 };
			}
			if(!_debuggerInitialized) {
				throw new LocalRpcException(
					"cpu.step_n: debugger not initialized (load a ROM first)")
					{ ErrorCode = -32603 };
			}

			//Native Debugger::Step supports a count directly — far cheaper than
			//N round trips. SleepUntilResume fires once when StepCount reaches 0.
			DebugApi.Step(CpuType.Snes, n, StepType.Step);
			int waitedMs = 0;
			int maxWaitMs = Math.Max(200, n * 2);
			while(!EmuApi.IsPaused() && waitedMs < maxWaitMs) {
				Thread.Sleep(1);
				waitedMs++;
			}
			SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return s.PC;
		}

		[JsonRpcMethod("bp.wait_for_hit")]
		public object BpWaitForHit(int timeoutMs = 10000)
		{
			//Assumes BPs are already installed via bp.add. Resumes the emu
			//(both pause levels) and blocks until a Breakpoint-source CodeBreak
			//arrives. Returns {pc, hit:true} on success, or throws on timeout
			//with the most recent PC for diagnostic value.
			//
			//Distinction from cpu.run_until: this is for *pre-installed*
			//breakpoints. cpu.run_until is composite (install + wait + clear).
			//Use bp.wait_for_hit when you have multiple BPs and want to know
			//which one fired (combine with cpu.state to read PC).
			if(!_debuggerInitialized) {
				throw new LocalRpcException(
					"bp.wait_for_hit: debugger not initialized (load a ROM first)")
					{ ErrorCode = -32603 };
			}

			_codeBreakSignal.Reset();
			DebugApi.ResumeExecution();
			EmuApi.Resume();
			bool hit = _codeBreakSignal.Wait(timeoutMs);
			if(!hit) {
				EmuApi.Pause();
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
				throw new LocalRpcException(
					$"bp.wait_for_hit: timeout after {timeoutMs}ms; PC=${s.PC:X4}")
					{ ErrorCode = -32603 };
			}
			SnesCpuState s2 = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			return new {
				PC = s2.PC,
				Hit = true,
			};
		}

		[JsonRpcMethod("cpu.run_until")]
		public object CpuRunUntil(uint addr, int timeoutMs = 5000)
		{
			//Composite: install a temporary exec breakpoint at `addr`, resume,
			//wait for the CodeBreak notification to fire, then return state.
			//
			//Two pause levels matter here:
			//  1. EmuApi.IsPaused()        — public pause (set by Pause/Resume).
			//                                If true, the emu thread doesn't run.
			//  2. Debugger::_waitForBreakResume — internal break (set by SleepUntilResume
			//                                on BP hit). The emu thread spins in a
			//                                10ms-sleep loop until cleared.
			//
			//To actually run, BOTH must be cleared: EmuApi.Resume() drives (1),
			//DebugApi.ResumeExecution() drives (2). The CodeBreak notification is
			//then the authoritative "BP hit" signal from the C++ debugger.
			if(!_debuggerInitialized) {
				throw new LocalRpcException(
					"cpu.run_until: debugger not initialized (load a ROM first)")
					{ ErrorCode = -32603 };
			}

			_codeBreakSignal.Reset();
			int bpId = BpAdd(addr, "exec");
			try {
				DebugApi.ResumeExecution();
				EmuApi.Resume();
				bool hit = _codeBreakSignal.Wait(timeoutMs);
				if(!hit) {
					EmuApi.Pause();
					throw new LocalRpcException(
						$"cpu.run_until: timeout after {timeoutMs}ms without CodeBreak at addr ${addr:X4}")
						{ ErrorCode = -32603 };
				}
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
				return new {
					PC = s.PC,
					Hit = s.PC == addr,
				};
			} finally {
				BpClear(bpId);
			}
		}

		//---------------------------------------------------------------
		// Input
		//---------------------------------------------------------------

		[JsonRpcMethod("input.set")]
		public bool InputSet(uint port, uint buttons)
		{
			//Override controller `port` (0..7) to hold the buttons named in
			//the bitmask `buttons`. The mask layout matches the SNES joypad
			//read register ($4218 hi-byte + $4219 lo-byte), so OpenSNES
			//`KEY_*` constants map directly:
			//  $8000=B  $4000=Y  $2000=SELECT $1000=START
			//  $0800=UP $0400=DOWN $0200=LEFT $0100=RIGHT
			//  $0080=A  $0040=X  $0020=L      $0010=R
			//Pass `buttons=0` to release all buttons.
			//
			//Persists until the next input.set call for the same port.
			//Survives reset; cleared on emu.load_rom (Debugger reinit).
			if(port >= 8) {
				throw new LocalRpcException($"port={port} out of range (0..7)")
					{ ErrorCode = -32602 };
			}
			DebugControllerState state = new DebugControllerState {
				B      = (buttons & 0x8000) != 0,
				Y      = (buttons & 0x4000) != 0,
				Select = (buttons & 0x2000) != 0,
				Start  = (buttons & 0x1000) != 0,
				Up     = (buttons & 0x0800) != 0,
				Down   = (buttons & 0x0400) != 0,
				Left   = (buttons & 0x0200) != 0,
				Right  = (buttons & 0x0100) != 0,
				A      = (buttons & 0x0080) != 0,
				X      = (buttons & 0x0040) != 0,
				L      = (buttons & 0x0020) != 0,
				R      = (buttons & 0x0010) != 0,
			};
			DebugApi.SetInputOverrides(port, state);
			return true;
		}

		[JsonRpcMethod("input.set_mouse")]
		public bool InputSetMouse(uint port, int dx, int dy, bool left, bool right)
		{
			//Reserved for the positive-path alt-controller surface (see
			//chantier note mesen2_rpc_input_mem_probes.md). The C++ side
			//backing (SetMouseOverride DllExport + SnesDebugger dispatch)
			//was prototyped but caused heap corruption — disabled pending
			//further investigation. Calling this method returns an error
			//so probes fail loudly instead of silently dropping input.
			throw new LocalRpcException(
				"input.set_mouse: positive-path alt-controller support is " +
				"not yet implemented (the C++ dispatch caused heap corruption; " +
				"see Mesen2 commit history). Use the no-controller probe path " +
				"in the meantime."
			) { ErrorCode = -32601 };
		}

		[JsonRpcMethod("input.set_scope")]
		public bool InputSetScope(uint port, int x, int y, bool fire, bool cursor, bool turbo, bool pause)
		{
			throw new LocalRpcException(
				"input.set_scope: positive-path alt-controller support is " +
				"not yet implemented (see input.set_mouse for context)."
			) { ErrorCode = -32601 };
		}

		[JsonRpcMethod("controller.connect")]
		public bool ControllerConnect(uint port, string type)
		{
			//Hot-swap the controller type on `port` (0 = Port1, 1 = Port2).
			//Mutates the in-memory SnesConfig and refreshes the C++ device
			//list without a full PowerCycle.
			//
			//Accepted types: "controller" (standard SNES pad), "mouse"
			//(SnesMouse), "scope" (SuperScope), "none" (disconnect).
			//
			//After switching, drive state via `input.set` (controller),
			//`input.set_mouse` (mouse), or `input.set_scope` (scope).
			//Existing overrides on the port are NOT cleared by the swap —
			//caller should re-set the appropriate override.
			if(port > 1) {
				throw new LocalRpcException($"port={port} out of range (0..1)")
					{ ErrorCode = -32602 };
			}
			ControllerType ct = type.ToLowerInvariant() switch {
				"controller" or "joypad" or "snescontroller" => ControllerType.SnesController,
				"mouse" or "snesmouse" => ControllerType.SnesMouse,
				"scope" or "superscope" or "lightgun" => ControllerType.SuperScope,
				"none" or "disconnect" => ControllerType.None,
				_ => throw new LocalRpcException($"unknown controller type '{type}' " +
					$"(expected: controller, mouse, scope, none)") { ErrorCode = -32602 }
			};
			SnesConfig cfg = ConfigManager.Config.Snes;
			if(port == 0) cfg.Port1.Type = ct;
			else cfg.Port2.Type = ct;
			cfg.ApplyConfig();
			ConfigApi.RefreshControlDevices();
			return true;
		}

		//---------------------------------------------------------------
		// Helpers
		//---------------------------------------------------------------

		private static MemoryType ParseMemorySpace(string space)
		{
			//Map short user-facing names to Mesen2 MemoryType enum values.
			//Token discipline: accept short names so clients don't have to
			//know Mesen2's internal enum naming.
			return space.ToLowerInvariant() switch {
				"cpu" or "snes" => MemoryType.SnesMemory,
				"wram" => MemoryType.SnesWorkRam,
				"vram" => MemoryType.SnesVideoRam,
				"cgram" => MemoryType.SnesCgRam,
				"oam" => MemoryType.SnesSpriteRam,
				"sram" => MemoryType.SnesSaveRam,
				"rom" or "prgrom" => MemoryType.SnesPrgRom,
				_ => throw new LocalRpcException($"unknown memory space '{space}'")
					{ ErrorCode = -32602 }
			};
		}
	}
}
