# `@k0b3n4irb/mesen2-rpc-client`

TypeScript client for the **mesen2-rpc** JSON-RPC server. Wraps the 26
RPC methods in a typed `Mesen2Client` class with `node:net` as the only
runtime dependency.

This is Phase 4 of the mesen2-rpc MVP — the surface AIs and human users
talk to when driving the headless Mesen2 emulator from Node.

## Install

```sh
npm install @k0b3n4irb/mesen2-rpc-client
```

(Not yet published — install via local path during MVP development:
`npm install file:../path/to/mesen2-rpc/clients/typescript`.)

## Quick start

```ts
import { Mesen2Client } from "@k0b3n4irb/mesen2-rpc-client";

// Launch mesen2-rpc on port 9911 first:
//   $ ./Mesen --rpc-server=9911

const c = new Mesen2Client({ port: 9911 });
await c.connect();

await c.emu.loadRom("/path/to/game.sfc");
await c.emu.runFrames(60);                  // boot
await c.bp.add(0x9655);                     // break at main loop body
const hit = await c.bp.waitForHit(5000);    // resume and wait
console.log(`stopped at PC=$${hit.PC.toString(16)}`);

const i = await c.mem.readWord("cpu", 0x001FE6);
console.log(`i=${i}`);                      // expect 0..11; >=12 would
                                            // catch the A6 acache bug

c.close();
```

## API surface (26 methods)

| Group | Methods |
|---|---|
| `client.emu` | `loadRom`, `reset`, `runFrames`, `isPaused` |
| `client.cpu` | `pc`, `register`, `state`, `step`, `stepN`, `runUntil` |
| `client.mem` | `readByte`, `readWord`, `readDword`, `readRange`, `search` |
| `client.bp`  | `add`, `clear`, `clearAll`, `list`, `waitForHit` |
| `client.ppu` | `register`, `state` |
| `client.snap` | `save`, `restore`, `list`, `discard` |
| `client.disasm` | `at` |

Method names mirror the `[JsonRpcMethod("namespace.action")]` attributes
in `Mesen2/UI/Utilities/RpcServer.cs`. The transport layer
(`JsonRpcTransport`) is exposed separately for users who want to call
methods not yet wrapped here.

## Error handling

Server-side validation errors come back as `RpcException` with the
JSON-RPC error code (`-32602` for invalid params, `-32603` for internal
errors). Example:

```ts
try {
  await c.cpu.stepN(99999);
} catch (err) {
  if (err instanceof RpcException && err.code === -32602) {
    // bounds violation reported by the server (max is 10000)
  }
}
```

Transport-level failures (connection dropped, timeout) come back as
plain `Error` rejections.

## Token discipline

This client is a **thin** pass-through — it deliberately does not
re-implement the response-size caps that the server enforces. The
server is the single source of truth for per-method limits. If you need
to know what those limits are, look at the LocalRpcException messages
in `Mesen2/UI/Utilities/RpcServer.cs`.

## Testing

```sh
npm run build
npm run test:smoke
```

The smoke test spawns a fresh mesen2-rpc against `hello_world.sfc`,
exercises every method, and rewinds via snapshot. Defaults assume the
OpenSNES tree layout; override with env vars `MESEN_RPC_BIN`,
`HELLO_WORLD_SFC`, `MESEN_RPC_PORT`.

## License

GPL-3.0, matching the Mesen2 fork.
