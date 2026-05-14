/**
 * Auto-spawn the mesen2-rpc binary if no server is reachable on the
 * configured port. The spawned process is killed on MCP server shutdown
 * so we don't leak emulator instances when Claude Code disconnects.
 */

import { spawn, type ChildProcess } from "node:child_process";
import * as net from "node:net";
import { setTimeout as sleep } from "node:timers/promises";

export interface SpawnOptions {
  /** Absolute path to the Mesen2-rpc binary. */
  binary: string;
  /** TCP port for the JSON-RPC server. */
  port: number;
  /** How long to wait for the port to open after spawning. */
  startupTimeoutMs?: number;
}

async function probe(port: number, host: string): Promise<boolean> {
  return new Promise(resolve => {
    const s = net.createConnection(port, host);
    s.once("connect", () => { s.end(); resolve(true); });
    s.once("error", () => resolve(false));
  });
}

/**
 * If a server is already reachable on the port, return null (caller
 * keeps the user-managed instance). Otherwise spawn one, wait for it
 * to bind, and return the child process for later cleanup.
 *
 * Errors from the child process write to stderr — MCP servers
 * communicate over stdio so stdout MUST stay clean for JSON-RPC.
 */
export async function spawnIfNeeded(opts: SpawnOptions): Promise<ChildProcess | null> {
  if (await probe(opts.port, "127.0.0.1")) {
    process.stderr.write(`mesen2-mcp: connecting to existing server on :${opts.port}\n`);
    return null;
  }

  process.stderr.write(`mesen2-mcp: spawning ${opts.binary} --rpc-server=${opts.port}\n`);
  const child = spawn(opts.binary, [`--rpc-server=${opts.port}`], {
    stdio: ["ignore", "ignore", "pipe"],
    detached: false,
  });

  child.stderr?.on("data", (chunk: Buffer) => {
    process.stderr.write(`[mesen2-rpc] ${chunk.toString().trim()}\n`);
  });

  const deadline = Date.now() + (opts.startupTimeoutMs ?? 10_000);
  while (Date.now() < deadline) {
    if (await probe(opts.port, "127.0.0.1")) {
      return child;
    }
    if (child.exitCode !== null) {
      throw new Error(`mesen2-rpc exited with code ${child.exitCode} during startup`);
    }
    await sleep(100);
  }

  child.kill("SIGKILL");
  throw new Error(`mesen2-rpc did not open port :${opts.port} within ${opts.startupTimeoutMs ?? 10_000}ms`);
}
