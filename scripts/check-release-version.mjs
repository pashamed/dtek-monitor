import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const root = new URL("../", import.meta.url);
const json = async (path) =>
  JSON.parse(await readFile(new URL(path, root), "utf8"));
const [pkg, lock, manifest, props] = await Promise.all([
  json("package.json"),
  json("package-lock.json"),
  json(".release-please-manifest.json"),
  readFile(new URL("bot/Directory.Build.props", root), "utf8"),
]);

assert.match(pkg.version, /^\d+\.\d+\.\d+$/, "Expected a stable SemVer version");
assert.equal(lock.version, pkg.version, "Lockfile version differs");
assert.equal(lock.packages[""].version, pkg.version, "Root lockfile version differs");
assert.equal(manifest["."], pkg.version, "Release manifest version differs");
assert.equal(
  props.match(/<Version>([^<]+)<\/Version>/)?.[1],
  pkg.version,
  ".NET version differs",
);
if (process.argv[2]) {
  assert.equal(process.argv[2], `v${pkg.version}`, "Release tag differs");
}
console.log(`Release versions agree: ${pkg.version}`);
