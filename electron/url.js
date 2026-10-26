const DEFAULT_URL = "https://example.com";

// Precedence: ICEGHOST_URL env, then the saved url.txt contents, then the default.
function resolveUrl(env, saved) {
  return env.ICEGHOST_URL || saved || DEFAULT_URL;
}

module.exports = { resolveUrl };
