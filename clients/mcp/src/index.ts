import { run } from "./server.js";

run().catch(err => {
  process.stderr.write(`mesen2-mcp: fatal: ${err instanceof Error ? err.stack ?? err.message : err}\n`);
  process.exit(1);
});
