const { app, BrowserWindow, shell } = require("electron");
const fs = require("node:fs");
const path = require("node:path");
const { resolveUrl } = require("./url.js");

function savedUrl() {
  try {
    return fs.readFileSync(path.join(app.getPath("userData"), "url.txt"), "utf8").trim() || null;
  } catch {
    return null;
  }
}

app.whenReady().then(() => {
  const target = resolveUrl(process.env, savedUrl());
  const origin = new URL(target).origin;
  const win = new BrowserWindow({ width: 1100, height: 800 });

  // Other origins leave the app for the system browser.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (new URL(url).origin !== origin) shell.openExternal(url);
    return { action: "deny" };
  });
  win.webContents.on("will-navigate", (event, url) => {
    if (new URL(url).origin !== origin) {
      event.preventDefault();
      shell.openExternal(url);
    }
  });

  win.loadURL(target);
});

app.on("window-all-closed", () => app.quit());
