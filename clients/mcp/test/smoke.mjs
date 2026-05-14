/**
 * MCP smoke test. Spawns mesen2-mcp via stdio, walks the JSON-RPC
 * handshake (initialize → tools/list → tools/call …), and reports
 * pass/fail.
 *
 * Doesn't import the SDK client — exercising the bare wire protocol
 * catches framing bugs that an SDK roundtrip would paper over.
 */

import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { setTimeout as sleep } from "node:timers/promises";
import * as readline from "node:readline";

const MCP_LAUNCHER = new URL("../bin/mesen2-mcp", import.meta.url).pathname;
const MESEN_BIN = process.env.MESEN_RPC_BIN
  ?? "/home/kobenairb/workspace/Mesen2/bin/linux-arm64/Release/Mesen";
const ROM = process.env.HELLO_WORLD_SFC
  ?? "/home/kobenairb/workspace/opensnes/examples/text/hello_world/hello_world.sfc";

// Sanity-check fixtures.
readFileSync(MCP_LAUNCHER);
readFileSync(MESEN_BIN);
readFileSync(ROM);

let pass = 0, fail = 0;
const failures = [];

function ok(label, value) {
  const v = typeof value === "string" ? value : JSON.stringify(value);
  console.log(`  ✓ ${label} → ${v.slice(0, 100)}`);
  pass++;
}
function fail_(label, err) {
  console.log(`  ✗ ${label} → ${err?.message ?? err}`);
  failures.push({ label, err });
  fail++;
}

const child = spawn(MCP_LAUNCHER, [], {
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, MESEN_RPC_BIN: MESEN_BIN, MESEN_RPC_PORT: "9912" },
});

child.stderr.on("data", chunk => {
  process.stderr.write(`[mcp:stderr] ${chunk.toString().trimEnd()}\n`);
});

// MCP stdio framing is one JSON object per line (Content-Length framing
// is NOT used over stdio — that's an LSP convention; MCP stdio is
// line-delimited JSON).
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;

rl.on("line", (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg.id !== undefined) {
    const h = pending.get(msg.id);
    if (h) {
      pending.delete(msg.id);
      h.resolve(msg);
    }
  }
});

function rpc(method, params) {
  const id = nextId++;
  const req = JSON.stringify({ jsonrpc: "2.0", id, method, params });
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`rpc timeout: ${method}`));
    }, 30_000);
    pending.set(id, {
      resolve: (r) => {
        clearTimeout(timer);
        if (r.error) reject(new Error(`rpc ${r.error.code}: ${r.error.message}`));
        else resolve(r.result);
      },
    });
    child.stdin.write(req + "\n");
  });
}

async function main() {
  // Give the spawned mesen2-rpc time to start before the first call.
  // (server.ts blocks on spawnIfNeeded before connecting stdio, so this
  // is paranoid — but the bin probe is fast and harmless.)
  await sleep(500);

  // 1) initialize handshake.
  const init = await rpc("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "mcp-smoke-test", version: "0.0.1" },
  });
  ok("initialize", init.protocolVersion);
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");

  // 2) tools/list — should return our 27 tools.
  const tl = await rpc("tools/list", {});
  if (Array.isArray(tl.tools) && tl.tools.length === 27) {
    ok("tools/list count=27", `${tl.tools.length} tools`);
  } else {
    fail_("tools/list count=27", new Error(`expected 27 tools, got ${tl.tools?.length}`));
  }

  // 3) Walk a representative subset end-to-end.
  const callTool = async (name, args) => {
    const r = await rpc("tools/call", { name, arguments: args });
    if (r.isError) throw new Error(`tool ${name} error: ${r.content?.[0]?.text}`);
    return JSON.parse(r.content[0].text);
  };

  ok("snes_load_rom", await callTool("snes_load_rom", { path: ROM }));
  ok("snes_run_frames(5)", await callTool("snes_run_frames", { n: 5 }));
  ok("snes_cpu_pc", (await callTool("snes_cpu_pc")).toString(16));
  ok("snes_cpu_state.PC", (await callTool("snes_cpu_state")).PC.toString(16));
  ok("snes_mem_read_byte rom:0", await callTool("snes_mem_read_byte", { space: "rom", addr: 0 }));
  ok("snes_mem_search HELLO", await callTool("snes_mem_search", { space: "rom", patternHex: "48454C4C4F" }));
  ok("snes_ppu_register bg_mode", await callTool("snes_ppu_register", { name: "bg_mode" }));
  ok("snes_ppu_state.Brightness", (await callTool("snes_ppu_state")).Brightness);
  ok("snes_cpu_run_until 0x8353", await callTool("snes_cpu_run_until", { addr: 0x8353, timeoutMs: 2000 }));
  ok("snes_disasm_at 0x8353", (await callTool("snes_disasm_at", { addr: 0x8353, n: 4 })).map(l => l.Text).join(" / "));

  const snapId = await callTool("snes_snap_save");
  ok("snes_snap_save", snapId);
  await callTool("snes_run_frames", { n: 30 });
  const f1 = (await callTool("snes_ppu_state")).Frame;
  await callTool("snes_snap_restore", { id: snapId });
  const f2 = (await callTool("snes_ppu_state")).Frame;
  if (f2 < f1) ok("snapshot rewinds frame", `${f1} → ${f2}`);
  else fail_("snapshot rewinds frame", new Error(`${f1} → ${f2}, no rewind`));

  // 4) Error surface: server-side bounds rejection should come back as
  // isError:true (tool error), not as MCP protocol error.
  const r = await rpc("tools/call", { name: "snes_cpu_step_n", arguments: { n: 99999 } });
  const errText = r.content?.[0]?.text ?? "";
  const doubled = (errText.match(/RPC -?\d+:/g) ?? []).length > 1;
  if (r.isError && /step_n/.test(errText) && !doubled) {
    ok("bounds rejection surfaces as tool error", errText);
  } else {
    fail_("bounds rejection surfaces as tool error", new Error(`unexpected (doubled=${doubled}): ${JSON.stringify(r)}`));
  }
}

try {
  await main();
} catch (err) {
  fail_("test driver", err);
} finally {
  child.kill("SIGTERM");
  await sleep(500);
  if (!child.killed) child.kill("SIGKILL");
}

console.log(`\n${pass} passed, ${fail} failed`);
if (fail > 0) {
  for (const f of failures) console.log(`  - ${f.label}: ${f.err?.message ?? f.err}`);
  process.exit(1);
}
