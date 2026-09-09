import assert from "node:assert/strict";
import { test } from "node:test";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { gitEnvironment, assertLocalPath, validateGitStorage } from "../local-security.mjs";

test("Git cannot inherit transports, traces, credentials or Node preloads", () => {
  const env = gitEnvironment({ PATH: "test", NODE_OPTIONS: "--require attacker", GIT_CONFIG_COUNT: "1",
    GIT_TRACE: "remote", GIT_ALLOW_PROTOCOL: "https", HTTPS_PROXY: "external" });
  assert.equal(env.GIT_ALLOW_PROTOCOL, "");
  assert.equal(env.GIT_NO_LAZY_FETCH, "1");
  for (const name of ["NODE_OPTIONS", "GIT_CONFIG_COUNT", "GIT_TRACE", "HTTPS_PROXY"]) assert.equal(env[name], undefined);
});

test("Cloud directories and metadata includes/alternates are rejected before reading Git", () => {
  assert.throws(() => assertLocalPath(path.join(os.tmpdir(), "OneDrive", "repo")), /Cloud/);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-git-security-"));
  fs.mkdirSync(path.join(root, "objects", "info"), { recursive: true });
  fs.writeFileSync(path.join(root, "config"), '[includeIf "gitdir:*"]\n path = //external/config\n');
  assert.throws(() => validateGitStorage(root), /includes/);
  fs.writeFileSync(path.join(root, "config"), "[core]\n bare = true\n");
  fs.writeFileSync(path.join(root, "objects", "info", "alternates"), "//external/objects");
  assert.throws(() => validateGitStorage(root), /alternates/);
});
