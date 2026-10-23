/* Device-scoped controls; gateway credentials are kept separately from collection config. */
(() => {
  const panel = document.createElement("div");
  panel.className = "device-panel hidden";
  panel.dataset.deviceSection = "phone";
  deviceSections.phone = panel;
  $("#sources").append(panel);
  const results = $("#device-check-result");
  $(".device-subnav").after(results);
  let renderedSource = "", checking = false, gatewayLoaded = false, gatewayState = null;
  const testResults = new Map();
  const gateway = document.createElement("article");
  gateway.className = "card phone-gateway";
  gateway.innerHTML = `<h3>随身 WiFi 电话网关</h3><p class="muted">仅用于连接了支持 AT 拨号的 UFI-TOOLS 随身 WiFi 的 OpenWrt 路由器。这里配置一次，各台设备分别设置提醒条件。</p><form id="phone-gateway-form" class="form-grid">
    ${toggle("启用电话网关", "enabled", false)}${field("随身 WiFi 地址", "address", "http://192.168.0.1:2333", "url", "required")}
    ${field("UFI-TOOLS 登录口令", "token", "", "password", 'autocomplete="new-password" placeholder="留空保留已保存口令"')}
    ${selectField("AT 执行槽位", "slot", "0", [["0", "槽位 0（已验证设备默认）"], ["1", "槽位 1"]])}
    <details class="full-span advanced-section"><summary>呼叫限制 <span>保护设置</span></summary><div class="form-grid">${field("最长呼叫时间（秒）", "ring_seconds", 25, "number", 'min="5" max="60" required')}${field("两次呼叫至少间隔（秒）", "minimum_interval_seconds", 300, "number", 'min="60" max="3600" required')}</div></details>
    <div class="full-span button-row"><button class="primary" type="submit">保存电话网关</button><button class="ghost" type="button" id="probe-phone">检测已保存连接</button></div><p id="phone-gateway-status" class="full-span muted" role="status">尚未检测</p></form>`;
  $("#system .settings-grid")?.append(gateway);
  if (!gateway.isConnected) $("#system").append(gateway);
  const form = $("#phone-gateway-form");
  enhanceSecrets(form);

  async function loadGateway() {
    const data = await api("/api/phone");
    gatewayState = data;
    const value = data.gateway.settings;
    for (const [key, val] of Object.entries(value)) {
      const input = form.elements[key];
      if (input) input.type === "checkbox" ? input.checked = val : input.value = val;
    }
    $("#phone-gateway-status").textContent = data.gateway.router_supported
      ? data.status.message + (data.gateway.token_configured ? " · 口令已保存" : " · 尚未保存口令")
      : "当前为本机预览：实际拨号仅在 OpenWrt 路由器上可用。";
    gatewayLoaded = true;
    const ready = panel.querySelector(".phone-ready");
    if (ready) ready.textContent = gatewayMessage();
  }
  function displayResults(id) {
    const record = testResults.get(id);
    results.classList.toggle("hidden", !record);
    results.innerHTML = record ? `<div class="check-heading"><b>当前设备检查结果</b><small>${esc(record.time)}</small></div>${record.steps.map(s => `<div class="check-step ${s.ok ? "passed" : "failed"}"><b>${s.ok ? "✓" : "!"} ${esc(s.name)}</b><span>${esc(s.message)}</span></div>`).join("")}<small>未保存、未发消息、未拨号；检查使用本页当前填写的参数。</small>` : "";
  }
  function showPhoneStatus(message) {
    $("#phone-gateway-status").textContent = message;
    const ready = panel.querySelector(".phone-ready");
    if (ready) ready.textContent = message;
    if (gatewayState) gatewayState.status.message = message;
  }
  function gatewayMessage() {
    if (!gatewayState) return "请先在系统设置配置随身 WiFi 电话网关";
    if (!gatewayState.gateway.router_supported) return "当前为本机预览，拨号仅支持连接了随身 WiFi 的 OpenWrt 路由器。";
    return gatewayState.status.enabled ? gatewayState.status.message : "电话网关已关闭，请到系统设置启用。";
  }
  function render(source) {
    if (source?.id !== renderedSource) { renderedSource = source?.id; displayResults(source?.id); }
    if (!source) { panel.innerHTML = '<div class="empty">请先添加设备</div>'; return; }
    source.phone_reminder ||= { enabled: false, delay_seconds: 300, number: "" };
    const value = source.phone_reminder;
    panel.innerHTML = `<article class="card phone-device"><div class="phone-intro"><div class="phone-icon" aria-hidden="true"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3h4l2 5-3 2a14 14 0 0 0 6 6l2-3 5 2v4a2 2 0 0 1-2 2C10 21 3 14 3 5a2 2 0 0 1 2-2Z"/></svg></div><div><h3>报警持续未恢复时，电话提醒</h3><p>仅作用于 ${esc(source.name)}。普通消息仍按原报警规则发送。</p></div><span class="state ${value.enabled ? "online" : "disabled"}">${value.enabled ? "此设备已开启" : "此设备未开启"}</span></div>
    <form id="device-phone-form" class="form-grid">${toggle("启用此设备的电话提醒", "enabled", value.enabled)}
    <div class="phone-conditions full-span form-grid ${value.enabled ? "" : "hidden"}">${field("同一报警持续多少秒后呼叫", "delay_seconds", value.delay_seconds, "number", 'min="1" max="86400" required')}${field("接听号码", "number", value.number, "tel", 'pattern="[+]?[0-9]{7,15}" maxlength="16" placeholder="填写手机号码或带国际区号的号码" required')}</div>
    <div class="phone-timeline full-span"><span>报警出现</span><i></i><span>持续 ${esc(value.delay_seconds)} 秒</span><i></i><span>检查随身 WiFi</span><i></i><span>拨号提醒</span></div>
    <p class="full-span muted">恢复、断线或停止采集后取消计时。每条持续报警仅尝试一次；重启或重新应用配置后重新计时。仅拨号提醒，暂不播放语音。</p>
    <div class="phone-ready full-span">${esc(gatewayMessage())}</div>
    <div class="full-span button-row"><button type="button" class="ghost" id="open-phone-gateway">配置随身 WiFi</button><button type="button" class="ghost" id="test-phone-call" ${value.enabled ? "" : "disabled"}>拨打一通测试电话…</button></div></form></article>`;
    const phoneForm = $("#device-phone-form");
    function updateValidity() {
      for (const input of phoneForm.querySelectorAll(".phone-conditions input")) input.disabled = !value.enabled;
    }
    updateValidity();
    phoneForm.onsubmit = e => e.preventDefault();
    phoneForm.oninput = () => {
      value.enabled = phoneForm.elements.enabled.checked;
      value.delay_seconds = Number(phoneForm.elements.delay_seconds.value);
      value.number = phoneForm.elements.number.value.trim();
      phoneForm.querySelector(".phone-conditions").classList.toggle("hidden", !value.enabled);
      $("#test-phone-call").disabled = !value.enabled;
      const badge = panel.querySelector(".phone-intro .state");
      badge.textContent = value.enabled ? "此设备已开启" : "此设备未开启";
      badge.className = "state " + (value.enabled ? "online" : "disabled");
      phoneForm.querySelector(".phone-timeline span:nth-of-type(2)").textContent = `持续 ${value.delay_seconds} 秒`;
      updateValidity(); updateDirty();
    };
  }
  function draft(source) { return { source, rules: config.rules.filter(r => r.source_id === source.id) }; }
  function validDevice() {
    for (const f of [$("#source-form"), $("#rule-form"), $("#device-phone-form")]) {
      if (f && !f.checkValidity()) {
        if (f.id === "device-phone-form") setDevicePanel("phone");
        else if (f.id === "rule-form") setDevicePanel("rules");
        else setDevicePanel("connection");
        for (let p = f.querySelector(":invalid")?.parentElement; p && p !== f; p = p.parentElement) if (p.tagName === "DETAILS") p.open = true;
        f.reportValidity(); return false;
      }
    }
    return true;
  }
  document.addEventListener("input", e => {
    if (e.target.closest("#source-form, #rule-form, #device-phone-form")) {
      const id = config?.sources[selected.source]?.id;
      testResults.delete(id); displayResults(id);
    }
  });
  form.onsubmit = async e => {
    e.preventDefault();
    const button = form.querySelector("button[type=submit]"); button.disabled = true;
    try {
      const data = Object.fromEntries(new FormData(form));
      data.enabled = form.elements.enabled.checked;
      for (const key of ["slot", "ring_seconds", "minimum_interval_seconds"]) data[key] = Number(data[key]);
      const response = await api("/api/phone", { method: "PUT", body: JSON.stringify(data) });
      toast(response.message); await loadGateway();
    } catch (err) { $("#phone-gateway-status").textContent = err.message; toast(err.message, true); }
    finally { button.disabled = false; }
  };
  document.addEventListener("click", async e => {
    const button = e.target.closest("button"); if (!button) return;
    if (button.dataset.page === "system" && !gatewayLoaded) loadGateway().catch(err => toast(err.message, true));
    if (button.dataset.devicePanel === "phone" && !gatewayLoaded) loadGateway().catch(err => toast(err.message, true));
    if (button.id === "open-phone-gateway") { navigate("system"); if (!gatewayLoaded) await loadGateway(); gateway.scrollIntoView({ block: "start", behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? "instant" : "smooth" }); }
    if (button.id === "probe-phone") {
      button.disabled = true;
      try { const data = await api("/api/phone/probe", { method: "POST" }); await loadGateway(); toast(data.message); }
      catch (err) { showPhoneStatus(err.message); toast(err.message, true); }
      finally { button.disabled = false; }
    }
    const source = config?.sources[selected.source];
    if (!source || !["check-device", "save-device", "test-phone-call"].includes(button.id)) return;
    if (!validDevice()) return;
    if (button.id === "test-phone-call") {
      // Test calls are a deliberate action, separate from read-only configuration checks.
      const snapshot = structuredClone(source);
      ask("拨打一通测试电话", `将通过已保存的随身 WiFi 网关拨打 ${snapshot.phone_reminder.number}。测试会真实呼叫，请确认号码属于你或已获接听人同意。`, async () => {
        button.disabled = true;
        try { const r = await api("/api/phone/test", { method: "POST", body: JSON.stringify(snapshot), signal: AbortSignal.timeout(120000) }); toast(r.message); }
        catch (err) { toast(err.message, true); }
        finally { button.disabled = false; }
      });
      return;
    }
    if (checking || saving) return;
    checking = true; button.disabled = true;
    try {
      const snapshot = structuredClone(draft(source));
      if (button.id === "check-device") {
        toast("正在检查当前设备的配置与连接…");
        const data = await api("/api/test/device", { method: "POST", body: JSON.stringify(snapshot), signal: AbortSignal.timeout(60000) });
        testResults.set(source.id, { steps: data.steps, time: new Date().toLocaleTimeString("zh-CN", { hour12: false }) });
        if (config.sources[selected.source]?.id === source.id) displayResults(source.id);
        toast(data.steps.every(s => s.ok) ? "当前设备检查通过" : "检查完成，请查看需要处理的项目", !data.steps.every(s => s.ok));
      } else {
        saving = true;
        const r = await api("/api/devices/" + encodeURIComponent(source.id), { method: "PUT", body: JSON.stringify(snapshot) });
        const applied = await api("/api/config");
        savedConfig = JSON.stringify(applied);
        // Keep drafts belonging to other devices and notification targets.
        const currentSource = config.sources.find(s => s.id === source.id);
        if (currentSource && JSON.stringify(draft(currentSource)) === JSON.stringify(snapshot)) {
          const index = config.sources.findIndex(s => s.id === source.id);
          config.sources[index] = applied.sources.find(s => s.id === source.id);
          config.rules = config.rules.filter(r => r.source_id !== source.id).concat(applied.rules.filter(r => r.source_id === source.id));
        }
        renderAll(); toast(r.message);
      }
    } catch (err) { toast(err.message, true); }
    finally { checking = false; saving = false; button.disabled = false; updateDirty(); }
  });
  window.PhoneUI = { render };
  if (config) render(config.sources[selected.source]);
})();
