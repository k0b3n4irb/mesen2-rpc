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

			if(methods.IsDebuggerInitialized) {
				DebugApi.ReleaseDebugger();
			}
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
		internal bool IsDebuggerInitialized => _debuggerInitialized;
		internal void EnsureDebuggerInitialized()
		{
			if(!_debuggerInitialized) {
				DebugApi.InitializeDebugger();
				_debuggerInitialized = true;
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

			//RunSingleFrame schedules a pause AFTER the next frame, which only
			//advances state when the emulator is actually running. So we Resume
			//first, request a frame advance, wait for the resulting pause, and
			//repeat — that loop advances exactly `n` frames deterministically.
			for(uint i = 0; i < n; i++) {
				EmuApi.Resume();
				EmuApi.ExecuteShortcut(new ExecuteShortcutParams { Shortcut = EmulatorShortcut.RunSingleFrame });

				//Wait for the pause to take effect. Frame at 60Hz = ~16.7ms; give
				//200ms before treating it as a hang.
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

		[JsonRpcMethod("cpu.run_until")]
		public object CpuRunUntil(uint addr, int timeoutMs = 5000)
		{
			//Composite: install a temporary exec breakpoint at `addr`, resume
			//the emulator, wait for it to pause (= breakpoint hit OR timeout),
			//remove the breakpoint, and return the post-pause state. Caller
			//gets either {PC: addr, Hit: true} or a timeout error.
			int bpId = BpAdd(addr, "exec");
			try {
				EmuApi.Resume();
				int waited = 0;
				while(!EmuApi.IsPaused() && waited < timeoutMs) {
					Thread.Sleep(1);
					waited++;
				}
				if(!EmuApi.IsPaused()) {
					throw new LocalRpcException(
						$"cpu.run_until: timeout after {timeoutMs}ms without hitting addr ${addr:X4}")
						{ ErrorCode = -32603 };
				}
				SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
				return new {
					PC = s.PC,
					Hit = s.PC == addr,
					WaitedMs = waited,
				};
			} finally {
				BpClear(bpId);
			}
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
