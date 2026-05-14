/**
 * MVP validation demo — the Phase 5 criterion from
 * .claude/plans/tender-yawning-cake.md:
 *
 *   "Reproduire la détection de l'acache bug d'A6+A7 automatiquement,
 *    sans assistance humaine, en moins d'une seconde via API/MCP."
 *
 * What this script proves:
 *   - Claude (or any autonomous client) can install a BP, advance the
 *     emulator past boot, hit the BP, read a stack-local variable, and
 *     assert a bound on it — all without opening Mesen2 GUI.
 *   - The bound-violation shape is what would catch a memory-corruption
 *     bug like the A6+A7 acache regression (i_loop drifting beyond
 *     0..12 because something else stomped on its stack slot).
 *
 * Target program: examples/text/hello_world/hello_world.sfc
 *   main()'s message-writing loop iterates i from 0 to 12, then sees
 *   message[12] = 0xFF and breaks. Any i > 12 at loop exit, or i out
 *   of [0..12] during iteration, indicates corruption.
 *
 * Address derivation (from hello_world.sym + main.c.asm):
 *   - 0x9619  = main@while_cond.27  (BP target: hit once per iter)
 *   - 0x9663  = main@while_join.29  (loop exit point)
 *   - i is at SP+8 on the data stack. SP at the BP is 0x1FDE, so
 *     i lives at CPU address 0x001FE6 — exactly the value the plan
 *     predicted three sessions ago.
 */

import { Mesen2Client } from "../dist/index.js";
import { strict as assert } from "node:assert";

const PORT = parseInt(process.env.MESEN_RPC_PORT ?? "9911", 10);
const ROM = process.env.HELLO_WORLD_SFC
  ?? "/home/kobenairb/workspace/opensnes/examples/text/hello_world/hello_world.sfc";

const BP_WHILE_COND = 0x9619;
const BP_WHILE_JOIN = 0x9663;

const c = new Mesen2Client({ port: PORT });
await c.connect();

const t0 = process.hrtime.bigint();
try {
  // 1. Load ROM. Emulator is paused (ConsoleMode).
  await c.emu.loadRom(ROM);

  // 2. Install BP at the loop-condition check BEFORE running.
  //    If we ran first, main() would have already finished its loop
  //    (it runs once at boot then sits in WaitForVBlank forever) and
  //    the BP would never fire.
  await c.bp.add(BP_WHILE_COND);

  // 3. Walk the first few iterations and verify i increments cleanly.
  const observed = [];
  for (let iter = 0; iter < 5; iter++) {
    const hit = await c.bp.waitForHit(2000);
    assert.equal(hit.PC, BP_WHILE_COND, `BP didn't fire on iter ${iter}`);

    // i is at SP+8 on the data stack. SP changes per call frame, so
    // we re-derive it each hit — anything else would be brittle.
    const s = await c.cpu.state();
    const iAddr = s.SP + 8;
    const i = await c.mem.readWord("cpu", iAddr);

    observed.push(i);
    assert.equal(i, iter, `iter ${iter}: expected i=${iter}, got i=${i}`);
    assert.ok(i >= 0 && i <= 12, `i=${i} out of expected range 0..12`);
  }

  // 4. Skip past the rest of the loop with run_until at while_join.
  //    This composite tool installs a one-shot BP, resumes, waits, and
  //    clears — perfect for "fast-forward to this label".
  await c.bp.clearAll();
  const exit = await c.cpu.runUntil(BP_WHILE_JOIN, 2000);
  assert.equal(exit.PC, BP_WHILE_JOIN, "didn't reach while_join");

  // 5. Verify the final i. Message "HELLO WORLD!" is 12 chars + 0xFF;
  //    loop terminates when message[12] = 0xFF. So i MUST be exactly 12.
  const sExit = await c.cpu.state();
  const iFinal = await c.mem.readWord("cpu", sExit.SP + 8);
  assert.equal(iFinal, 12,
    `i=${iFinal} at loop exit — expected 12 (acache-bug detection criterion)`);

  const elapsedMs = Number(process.hrtime.bigint() - t0) / 1e6;
  console.log(`✓ MVP validation passed in ${elapsedMs.toFixed(1)}ms`);
  console.log(`  iterations observed: ${observed.join(", ")}`);
  console.log(`  i at loop exit:      ${iFinal}`);
} finally {
  c.close();
}
