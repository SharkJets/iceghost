const { test } = require("node:test");
const assert = require("node:assert");
const { resolveUrl } = require("./url.js");

test("env wins over saved", () => {
  assert.strictEqual(resolveUrl({ ICEGHOST_URL: "https://a" }, "https://b"), "https://a");
});

test("saved used when env unset", () => {
  assert.strictEqual(resolveUrl({}, "https://b"), "https://b");
});

test("default when neither set", () => {
  assert.strictEqual(resolveUrl({}, null), "https://example.com");
});
