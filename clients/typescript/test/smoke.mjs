/**
 * Smoke test for the Mesen2Client. Spawns a fresh mesen2-rpc server,
 * runs every typed method against hello_world.sfc, and prints a
 * pass/fail summary.
 *
 * Usage:
 *   MESEN_RPC_BIN=/path/to/Mesen \
 *   HELLO_WORLD_SFC=/path/to/hello_world.sfc \
 *   node test/smoke.mjs
 *
 * Defaults assume the OpenSNES tree layout one level up.
 */

import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { setTimeout as sleep } from "node:timers/promises";
import * as net from "node:net";
import { Mesen2Client } from "../dist/index.js";

const MESEN_BIN = process.env.MESEN_RPC_BIN
  ?? "/home/kobenairb/workspace/Mesen2/bin/linux-arm64/Release/Mesen";
const ROM = process.env.HELLO_WORLD_SFC
  ?? "/home/kobenairb/workspace/opensnes/examples/text/hello_world/hello_world.sfc";
const PORT = parseInt(process.env.MESEN_RPC_PORT ?? "9911", 10);

let pass = 0, fail = 0;
const failures = [];

function ok(label, value) {
  console.log(`  ✓ ${label} → ${JSON.stringify(value).slice(0, 80)}`);
  pass++;
}
function fail_(label, err) {
  console.log(`  ✗ ${label} → ${err.message}`);
  failures.push({ label, err });
  fail++;
}

async function waitForPort(port, host, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const ok = await new Promise(resolve => {
      const s = net.createConnection(port, host);
      s.once("connect", () => { s.end(); resolve(true); });
      s.once("error", () => resolve(false));
    });
    if (ok) return;
    await sleep(100);
  }
  throw new Error(`port ${host}:${port} did not open within ${timeoutMs}ms`);
}

async function withRunningServer(fn) {
  console.log(`Starting ${MESEN_BIN} --rpc-server=${PORT}`);
  const srv = spawn(MESEN_BIN, [`--rpc-server=${PORT}`], {
    stdio: ["ignore", "pipe", "pipe"],
    detached: false,
  });
  let serverDied = false;
  srv.on("exit", () => { serverDied = true; });

  try {
    await waitForPort(PORT, "127.0.0.1", 5000);
    if (serverDied) throw new Error("server died during startup");
    await fn();
  } finally {
    srv.kill("SIGTERM");
    await sleep(500);
    if (!srv.killed) srv.kill("SIGKILL");
  }
}

async function main() {
  // Sanity-check fixtures up-front so we don't blame the RPC layer for
  // missing files. readFileSync throws a useful message if the path is wrong.
  readFileSync(MESEN_BIN);
  readFileSync(ROM);

  await withRunningServer(async () => {
    const c = new Mesen2Client({ port: PORT });
    await c.connect();
    try {
      // emu.* lifecycle
      ok("emu.loadRom", await c.emu.loadRom(ROM));
      ok("emu.runFrames(5)", await c.emu.runFrames(5));
      ok("emu.isPaused", await c.emu.isPaused());

      // cpu.*
      const s = await c.cpu.state();
      ok("cpu.state.PC", s.PC.toString(16));
      ok("cpu.pc", (await c.cpu.pc()).toString(16));
      ok("cpu.register('A')", await c.cpu.register("A"));
      ok("cpu.step", (await c.cpu.step()).toString(16));
      ok("cpu.stepN(10)", (await c.cpu.stepN(10)).toString(16));

      // mem.*
      ok("mem.readByte('rom', 0)", await c.mem.readByte("rom", 0));
      ok("mem.readWord('rom', 0)", await c.mem.readWord("rom", 0));
      ok("mem.readDword('rom', 0)", await c.mem.readDword("rom", 0));
      ok("mem.readRange('rom', 0, 16)", await c.mem.readRange("rom", 0, 16));
      ok("mem.search HELLO", await c.mem.search("rom", "48454C4C4F", 0, 0, 3));

      // bp.*
      const bpId = await c.bp.add(0x8353);
      ok("bp.add 0x8353", bpId);
      ok("bp.list", await c.bp.list());

      // cpu.runUntil — should fire instantly since CPU hits WaitForVBlank each frame
      const ru = await c.cpu.runUntil(0x8353, 2000);
      ok("cpu.runUntil 0x8353 (composite)", ru);

      // bp.waitForHit (BP already installed from above)
      const wh = await c.bp.waitForHit(2000);
      ok("bp.waitForHit", wh);

      ok("bp.clear", await c.bp.clear(bpId));
      ok("bp.clearAll", await c.bp.clearAll());

      // ppu.*
      ok("ppu.register('bg_mode')", await c.ppu.register("bg_mode"));
      ok("ppu.register('brightness')", await c.ppu.register("brightness"));
      ok("ppu.state", await c.ppu.state());

      // disasm.*
      const lines = await c.disasm.at(0x8353, 4);
      ok("disasm.at 0x8353 (4 lines)", lines.map(l => l.Text).join(" / "));

      // snap.*
      const snapId = await c.snap.save();
      ok("snap.save", snapId);
      ok("snap.list", await c.snap.list());
      await c.emu.runFrames(20);
      const frameAfterAdvance = (await c.ppu.state()).Frame;
      ok("frame after advance", frameAfterAdvance);
      ok("snap.restore", await c.snap.restore(snapId));
      const frameRestored = (await c.ppu.state()).Frame;
      if (frameRestored < frameAfterAdvance) {
        ok("snap.restore rewound frame counter", `${frameAfterAdvance} → ${frameRestored}`);
      } else {
        fail_("snap.restore rewound frame counter", new Error(`expected rewind, got ${frameAfterAdvance} → ${frameRestored}`));
      }
      ok("snap.discard", await c.snap.discard(snapId));

      // Validation: server-side bounds should throw RpcException, not silently truncate
      try {
        await c.cpu.stepN(99999);
        fail_("cpu.stepN(99999) bounds rejection", new Error("did not throw"));
      } catch (e) {
        if (e.code === -32602) ok("cpu.stepN(99999) bounds rejection", e.message);
        else fail_("cpu.stepN(99999) bounds rejection", e);
      }
    } finally {
      c.close();
    }
  });
}

try {
  await main();
} catch (err) {
  fail_("setup", err);
}

console.log(`\n${pass} passed, ${fail} failed`);
if (fail > 0) {
  for (const f of failures) console.log(`  - ${f.label}: ${f.err.message}`);
  process.exit(1);
}
