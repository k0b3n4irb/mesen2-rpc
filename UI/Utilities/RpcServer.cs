using Mesen.Config;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.IO;
using System.Threading;

namespace Mesen.Utilities
{
	/// <summary>
	/// Headless RPC-server entry point — minimal stub.
	///
	/// Mirrors TestRunner.Run() but, instead of polling-until-timeout, will
	/// expose the debugger surface (CPU/PPU/memory/breakpoints/trace) over
	/// TCP/JSON-RPC for autonomous AI-driven SNES debugging.
	///
	/// Phase 1 (this commit): stub that proves the build chain accepts the
	/// new entry point and the headless emulator init works without
	/// Avalonia. No TCP listener yet, no JSON-RPC dispatch yet — just init,
	/// optional ROM load, idle loop until SIGINT.
	///
	/// Phase 2: add System.Net.Sockets TCP listener + StreamJsonRpc dispatch.
	/// Phase 3+: implement the ~30-tool surface per the plan
	/// (k0b3n4irb/opensnes audit doc, .claude/plans/tender-yawning-cake.md).
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

			//Optionally load a ROM at startup. Not required — the RPC `emu.load_rom`
			//method will allow loading later. Useful for testrunner-style one-shot use.
			if(commandLineHelper.FilesToLoad.Count == 1) {
				if(!EmuApi.LoadRom(commandLineHelper.FilesToLoad[0], string.Empty)) {
					Console.Error.WriteLine($"rpc-server: failed to load ROM {commandLineHelper.FilesToLoad[0]}");
					EmuApi.Release();
					return -1;
				}
				DebugWorkspaceManager.Load();
			}

			Console.Error.WriteLine($"rpc-server: ready on port {port} (stub — TCP dispatch not yet implemented)");

			//Idle until SIGINT. Real TCP listener replaces this in Phase 2.
			ManualResetEventSlim exit = new(false);
			Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
			exit.Wait();

			EmuApi.Stop();
			EmuApi.Release();
			return 0;
		}
	}
}
