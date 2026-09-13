import assert from "node:assert/strict";
import fs from "node:fs";
import { rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { inspectFont, collectFontLicenses } from "./font-assets.mjs";
import { selectLicense } from "./license-policy.mjs";

test("font intake is pinned, carries verified upstream notices, and does not widen software licenses", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-font-"));
  t.after(() => rm(root, { recursive: true }));
  assert.throws(() => selectLicense("OFL-1.1", "unreviewed-software@1"));
  const source = fileURLToPath(new URL("../web/src/assets/fonts/", import.meta.url));
  fs.mkdirSync(path.join(root, "font"));
  for (const file of ["PretendardVariable.woff2", "LICENSE.txt", "manifest.json"])
    fs.copyFileSync(path.join(source, file), path.join(root, "font", file));
  assert.equal(inspectFont(path.join(root, "font")).version, "1.3.9");
  const inventory = collectFontLicenses(path.join(root, "licenses"));
  assert.equal(inventory[0].selected, "OFL-1.1");
  assert.equal(inventory[0].evidence.length, 2);
  fs.appendFileSync(path.join(root, "font/LICENSE.txt"), "tampered");
  assert.throws(() => inspectFont(path.join(root, "font")), /hash differs/);
  fs.copyFileSync(path.join(source, "LICENSE.txt"), path.join(root, "font/LICENSE.txt"));
  fs.appendFileSync(path.join(root, "font/PretendardVariable.woff2"), "tampered");
  assert.throws(() => inspectFont(path.join(root, "font")), /hash differs/);
});
