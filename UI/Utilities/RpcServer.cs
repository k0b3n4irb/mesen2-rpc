using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using StreamJsonRpc;
using System;
using System.IO;
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

			if(commandLineHelper.FilesToLoad.Count == 1) {
				if(!EmuApi.LoadRom(commandLineHelper.FilesToLoad[0], string.Empty)) {
					Console.Error.WriteLine($"rpc-server: failed to load ROM {commandLineHelper.FilesToLoad[0]}");
					EmuApi.Release();
					return -1;
				}
				DebugWorkspaceManager.Load();
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

			RpcMethods methods = new();
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
	}
}
