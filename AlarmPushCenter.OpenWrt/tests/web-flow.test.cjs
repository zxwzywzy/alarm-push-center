const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(path.join(__dirname, "../wwwroot/app.js"), "utf8");

// Run the actual page functions with isolated transport and rendering boundaries.
function pageFunction(start, end, globals) {
  const context = vm.createContext(globals);
  vm.runInContext(source.slice(source.indexOf(start), source.indexOf(end)), context);
  return context;
}

test("Web requests reject HTML responses and report network, timeout and expired-session errors", async () => {
  let loginShown = false, options;
  const page = pageFunction("async function api(", "function toast(", {
    AbortSignal, TypeError,
    showLogin: () => { loginShown = true; },
    fetch: async (_, opt) => {
      options = opt;
      return { status: 502, ok: false, json: async () => { throw new SyntaxError("HTML"); } };
    },
  });
  await assert.rejects(page.api("/api/config"), /无效响应.*502/);
  assert.equal(options.cache, "no-store");
  assert.ok(options.signal);
  page.fetch = async () => { throw new TypeError("Failed to fetch"); };
  await assert.rejects(page.api("/api/config"), /无法连接服务/);
  page.fetch = async () => { throw Object.assign(new Error(), { name: "TimeoutError" }); };
  await assert.rejects(page.api("/api/config"), /请求超时/);
  page.fetch = async () => ({ status: 401, ok: false, json: async () => ({ error: "请先登录" }) });
  await assert.rejects(page.api("/api/config"), /请先登录/);
  assert.equal(loginShown, true);
});

test("Reauthentication keeps unsaved edits while a clean session loads current server config", async () => {
  const applied = { sources: [{ name: "已保存设备" }] };
  const draft = { sources: [{ name: "正在编辑设备" }] };
  const notices = [];
  const page = pageFunction("async function loadAll(", "function renderAll(", {
    config: draft, savedConfig: JSON.stringify(applied), overview: null, defaults: {},
    isDirty: () => JSON.stringify(page.config) !== page.savedConfig,
    api: async (url) => url === "/api/config" ? applied : {},
    renderAll: () => {}, updateDirty: () => {}, toast: (message) => notices.push(message),
  });
  await page.loadAll(true);
  assert.equal(page.config, draft);
  assert.equal(page.savedConfig, JSON.stringify(applied));
  assert.match(notices[0], /修改已保留/);
  page.config = applied;
  const updated = { sources: [{ name: "服务器的新配置" }] };
  page.api = async (url) => url === "/api/config" ? updated : {};
  await page.loadAll(true);
  assert.equal(page.config, updated);
  assert.equal(page.savedConfig, JSON.stringify(updated));
});
