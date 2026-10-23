// Run after `dotnet build AlarmPushCenter.OpenWrt -c Release`.
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");
test(
  "Router API uses draft source parameters without saving and returns bounded history and telemetry",
  { timeout: 30000 },
  async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "alarm-console-test-")),
      root = path.resolve(__dirname, ".."),
      password = "isolated-test-password";
    const configPath = path.join(dir, "config.json"),
      passwordPath = path.join(dir, "password");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        sources: [
          {
            id: "test",
            name: "test",
            protocol: "mock",
            enabled: true,
            poll_seconds: 0.2,
            params: { code: "saved", toggle_every: "8" },
          },
        ],
        rules: [{ id: "r", source_id: "test", enabled: false }],
        targets: [],
      }),
    );
    fs.writeFileSync(passwordPath, password);
    fs.writeFileSync(
      path.join(dir, "alarm-history.jsonl"),
      Array.from({ length: 25 }, (_, i) =>
        JSON.stringify({
          time: new Date(Date.now() - i * 1000).toISOString(),
          state: "alarm",
          source_name: "test",
          code: String(i),
          text: "历史文本",
          severity: "warning",
        }),
      ).join("\n"),
    );
    const server = spawn(
      "dotnet",
      [path.join(root, "bin/Release/net10.0/alarm-push-center.dll")],
      {
        env: {
          ...process.env,
          ALARM_CONFIG: configPath,
          ALARM_PASSWORD_FILE: passwordPath,
          ALARM_LISTEN: "http://127.0.0.1:5098",
          ASPNETCORE_CONTENTROOT: root,
        },
        stdio: "ignore",
      },
    );
    let cookie = "";
    const request = async (url, body, method) => {
      const r = await fetch("http://127.0.0.1:5098" + url, {
        headers: { "Content-Type": "application/json", Cookie: cookie },
        method: method || (body === undefined ? "GET" : "POST"),
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(15000),
      });
      if (r.headers.get("set-cookie"))
        cookie = r.headers.get("set-cookie").split(";")[0];
      return { status: r.status, body: await r.json(), headers: r.headers };
    };
    try {
      let ready = false;
      for (let i = 0; i < 80; i++) {
        try {
          await request("/api/health");
          ready = true;
          break;
        } catch {
          await new Promise((r) => setTimeout(r, 100));
        }
      }
      assert.equal(ready, true);
      const disclaimer = await fetch('http://127.0.0.1:5098/disclaimer');
      assert.equal(disclaimer.status,200);
      assert.match(await disclaimer.text(), /责任限制/);
      assert.equal((await request("/api/defaults")).status, 401);
      assert.equal((await request('/api/updates')).status,401);
      assert.equal((await request("/api/auth/recovery")).body.enabled, false);
      assert.equal((await request("/api/auth/recovery/request", {})).status, 400);
      assert.equal((await request('/api/mobile/devices')).status,401);
      assert.equal((await request('/api/auth/mobile/claim',{code:'invalid',name:'test phone'})).status,400);
      assert.equal(
        (await request("/api/auth/login", { password })).status,
        200,
      );
      const pairing=await request('/api/mobile/pairing',{},'POST');
      const updateView=(await request('/api/updates')).body;
      assert.equal(updateView.status.current_version,(await request('/api/health')).body.version);
      assert.equal(updateView.preferences.check_automatically,true);
      assert.equal(updateView.preferences.install_automatically,false);
      assert.equal((await request('/api/updates/settings',{current_password:'wrong',preferences:{check_automatically:false}},'PUT')).status,400);
      assert.equal((await request('/api/updates/settings',{current_password:password,preferences:{maintenance_start_hour:3,maintenance_end_hour:3}},'PUT')).status,400);
      assert.equal((await request('/api/updates/settings',{current_password:password,preferences:{check_automatically:false,install_automatically:false,maintenance_start_hour:22,maintenance_end_hour:2}},'PUT')).status,200);
      assert.equal(JSON.parse(fs.readFileSync(path.join(dir,'update-settings.json'),'utf8')).maintenance_start_hour,22);
      assert.equal((await request('/api/updates/apply',{current_password:password,version:'999.0.0'})).status,400);
      assert.equal(pairing.status,200);
      assert.equal(pairing.body.code.length,48);
      const paired=await request('/api/auth/mobile/claim',{code:pairing.body.code,name:'test phone'});
      assert.equal(paired.status,200);
      assert.equal((await request('/api/auth/mobile/claim',{code:pairing.body.code,name:'replay'})).status,400);
      const bearerRequest=async path=>fetch('http://127.0.0.1:5098'+path,{headers:{Authorization:'Bearer '+paired.body.token},method:path==='/api/mobile/pairing'?'POST':'GET'});
      assert.equal((await bearerRequest('/api/overview')).status,200);
      assert.equal((await bearerRequest('/api/config')).status,403);
      assert.equal((await bearerRequest('/api/phone')).status,403);
      assert.equal((await bearerRequest('/api/updates')).status,403);
      const mobileEdit = (await (await bearerRequest('/api/mobile/sources/test')).json());
      mobileEdit.phone_reminder = {enabled:true,delay_seconds:10,number:'13800138000'};
      const mobileSave = await fetch('http://127.0.0.1:5098/api/mobile/sources/test', {method:'PUT',headers:{Authorization:'Bearer '+paired.body.token,'Content-Type':'application/json'},body:JSON.stringify(mobileEdit)});
      assert.equal(mobileSave.status,200);
      assert.equal((await request('/api/config')).body.sources[0].phone_reminder.enabled,false);
      const phoneGrant = await fetch('http://127.0.0.1:5098/api/phone/test',{method:'POST',headers:{Authorization:'Bearer '+paired.body.token,'Content-Type':'application/json'},body:JSON.stringify(mobileEdit)});
      assert.equal(phoneGrant.status,403);
      assert.equal((await bearerRequest('/api/mobile/sources/test')).status,200);
      assert.equal((await bearerRequest('/api/mobile/pairing')).status,401);
      assert.equal((await bearerRequest('/api/mobile/devices')).status,401);
      assert.equal(JSON.stringify((await request('/api/mobile/devices')).body).includes(paired.body.token),false);
      const storedDevices=fs.readFileSync(path.join(dir,'mobile-devices.json'),'utf8');
      assert.equal(storedDevices.includes(paired.body.token),false);
      assert.equal((await request('/api/mobile/devices/'+paired.body.device_id,undefined,'DELETE')).status,200);
      assert.equal((await bearerRequest('/api/overview')).status,401);
      assert.equal((await request("/api/config")).headers.get("cache-control"), "no-store");
      const missing = await request("/api/does-not-exist");
      assert.equal(missing.status, 404);
      assert.ok(missing.body.error);
      assert.equal((await request("/api/overview")).body.running, false);
      assert.equal((await request("/api/runtime/start", {})).status, 200);
      assert.equal((await request("/api/settings", { current_password: "wrong", settings: {} }, "PUT")).status, 400);
      assert.equal((await request("/api/settings", { current_password: password, settings: {
        start_collection_on_launch: false, recovery_enabled: true,
        recovery_email: "recovery@example.com", smtp_host: "localhost", smtp_port: 465,
        smtp_user: "test", smtp_password: "test-secret", smtp_from: "sender@example.com",
      } }, "PUT")).status, 400);
      assert.equal((await request("/api/settings", { current_password: password, settings: {
        start_collection_on_launch: true, smtp_password: "secret-never-returned",
      } }, "PUT")).status, 200);
      const preferences = (await request("/api/settings")).body;
      assert.equal(preferences.start_collection_on_launch, true);
      assert.equal(preferences.smtp_password, "");
      for (const method of ["POST", "PUT", "DELETE"]) {
        const unknown = await request("/api/does-not-exist", undefined, method);
        assert.equal(unknown.status, 404);
        assert.ok(unknown.body.error);
      }
      const before = fs.readFileSync(configPath, "utf8");
      const testConfig = (await request('/api/config')).body;
      const checked = await request('/api/test/device',{source:testConfig.sources[0],rules:testConfig.rules.filter(r=>r.source_id==='test')});
      assert.equal(checked.status,200);
      assert.equal(checked.body.steps.every(s=>s.ok),true);
      const phoneDraft=structuredClone(testConfig.sources[0]);
      phoneDraft.phone_reminder={enabled:true,delay_seconds:10,number:'13800138000'};
      const checkedPhone=await request('/api/test/device',{source:phoneDraft,rules:testConfig.rules});
      assert.equal(checkedPhone.body.steps.at(-1).ok,false);
      assert.match(checkedPhone.body.steps.at(-1).message,/未启用/);
      assert.equal(fs.readFileSync(configPath,'utf8'),before);
      assert.equal((await request('/api/phone')).body.gateway.router_supported,false);
      assert.equal((await request('/api/phone/probe',{})).status,400);
      assert.equal((await request('/api/phone',{enabled:false,address:'http://8.8.8.8'},'PUT')).status,400);
      assert.equal((await request('/api/phone',{enabled:false,address:'http://192.168.0.1:2333',token:'private-gateway-token'},'PUT')).status,200);
      assert.equal(JSON.stringify((await request('/api/phone')).body).includes('private-gateway-token'),false);
      assert.equal(
        (await request("/api/defaults")).body.modbus_tcp.port,
        "502",
      );
      const source = {
        id: "unsaved",
        name: "draft",
        protocol: "mock",
        enabled: false,
        poll_seconds: 0.2,
        params: { code: "draft-code", toggle_every: "8" },
      };
      const draft = await request("/api/test/source", source);
      assert.equal(draft.status, 200);
      assert.equal(draft.body.signals[0].code, "draft-code");
      assert.equal(fs.readFileSync(configPath, "utf8"), before);
      assert.equal((await request("/api/config")).body.sources.length, 1);
      assert.equal(
        (
          await request("/api/test/source", {
            ...source,
            protocol: "opcua",
            params: { endpoint: "http://bad" },
          })
        ).status,
        400,
      );
      const history = (
        await request("/api/history?source=test&page=99&pageSize=10")
      ).body;
      assert.equal(history.total, 20);
      assert.equal(history.page, 2);
      assert.equal(history.items.length, 10);
      let device;
      for (let i = 0; i < 30; i++) {
        device = (await request("/api/overview")).body.devices[0];
        if (device.connection?.last_success) break;
        await new Promise((r) => setTimeout(r, 100));
      }
      assert.equal(device.connected, true);
      assert.ok(device.connection.connected_since);
      assert.ok(device.connection.last_success);
      let trend;
      for (let i = 0; i < 80; i++) {
        trend = (await request('/api/overview')).body;
        if (trend.telemetry.length >= 2) break;
        await new Promise((r) => setTimeout(r,100));
      }
      assert.equal(trend.sample_seconds,5);
      assert.ok(trend.telemetry.length >= 2);
      assert.equal(trend.telemetry.at(-1).connected,1);
      assert.ok(trend.telemetry.at(-1).memory_mb > 0);
      assert.ok(trend.telemetry.at(-1).reads_per_second > 0);
      const saved = (await request("/api/config")).body;
      const invalid = structuredClone(saved);
      invalid.sources[0].poll_seconds = -1;
      assert.equal((await request("/api/config", invalid, "PUT")).status, 400);
      assert.equal(fs.readFileSync(configPath, "utf8"), before);
      const updates = await Promise.all(Array.from({ length: 12 }, (_, i) => {
        const next = structuredClone(saved);
        next.sources[0].name = `concurrent-${i}`;
        next.sources[0].params.code = `code-${i}`;
        return request("/api/config", next, "PUT");
      }));
      assert.ok(updates.every((r) => r.status === 200));
      const persisted = JSON.parse(fs.readFileSync(configPath, "utf8"));
      const current = (await request("/api/config")).body;
      assert.deepEqual(current, persisted);
      assert.equal((await request("/api/overview")).body.devices[0].name, current.sources[0].name);
      assert.equal((await request("/api/runtime/stop", {})).status, 200);
      assert.equal((await request("/api/overview")).body.running, false);
      assert.equal((await request("/api/config", current, "PUT")).status, 200);
      const newDevice={id:'second',name:'第二台',enabled:false,protocol:'mock',poll_seconds:1,params:{code:'second-code'},phone_reminder:{enabled:false,delay_seconds:300,number:''}};
      assert.equal((await request('/api/devices/second',{source:newDevice,rules:[{id:'second-rule',source_id:'second',enabled:false}]},'PUT')).status,200);
      const deviceDraft=structuredClone(current.sources[0]); deviceDraft.name='仅更新第一台';
      assert.equal((await request('/api/devices/test',{source:deviceDraft,rules:current.rules.filter(r=>r.source_id==='test')},'PUT')).status,200);
      const afterDeviceSave=(await request('/api/config')).body;
      assert.equal(afterDeviceSave.sources.find(s=>s.id==='second').params.code,'second-code');
      assert.ok(afterDeviceSave.rules.some(r=>r.id==='second-rule'));
      assert.deepEqual(afterDeviceSave.targets,current.targets);
      assert.equal((await request('/api/devices/test',{source:deviceDraft,rules:[{id:'bad',source_id:'second'}]},'PUT')).status,400);
      assert.equal((await request("/api/overview")).body.running, false);
      assert.equal((await request("/api/runtime/restart", {})).status, 200);
      assert.equal((await request("/api/overview")).body.running, true);
      // The original device has been renamed: clearing all history must still remove its records.
      assert.equal((await request("/api/history", undefined, "DELETE")).body.deleted, 25);
      assert.equal((await request("/api/history")).body.total, 0);
      assert.equal((await request("/api/security/password", {
        current_password: password, new_password: "changed-test-password",
      })).status, 200);
      assert.equal((await request("/api/config")).status, 401);
      assert.equal((await request("/api/auth/login", { password })).status, 401);
      assert.equal((await request("/api/auth/login", { password: "changed-test-password" })).status, 200);
    } finally {
      server.kill();
      await new Promise((r) =>
        server.exitCode !== null ? r() : server.once("exit", r),
      );
      fs.rmSync(dir, { recursive: true, force: true });
    }
  },
);
