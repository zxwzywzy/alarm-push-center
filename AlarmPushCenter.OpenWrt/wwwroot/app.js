const $ = (s) => document.querySelector(s),
  $$ = (s) => [...document.querySelectorAll(s)];
let config = null,
  overview = null,
  selected = { source: 0, rule: 0, target: 0 },
  historyPage = 1,
  historyPages = 1,
  confirmAction = null;
const draftAlarmRows = new WeakSet();
let checkedAlarmRows = new WeakSet(),
  alarmPage = 1,
  alarmQuery = "",
  alarmRuleId = "",
  alarmView = null;
let defaults = {},
  savedConfig = "",
  saving = false,
  historyRequest = 0,
  polling = false;
const labels = {
  mock: "模拟",
  s7: "Siemens S7",
  modbus_tcp: "Modbus TCP",
  modbus_rtu: "Modbus RTU",
  mqtt: "MQTT",
  opcua: "OPC UA 数组",
  opcua_prodiag: "OPC UA ProDiag",
  http_json: "HTTP JSON",
  tcp_json: "TCP JSON",
  udp_json: "UDP JSON",
  serial_text: "串口文本",
  ads: "Beckhoff ADS",
  telegram: "Telegram",
  dingtalk: "钉钉",
  wecom: "企业微信",
  feishu: "飞书",
  custom_webhook: "自定义 Webhook",
};
const sourceFields = {
  mock: [
    ["toggle_every", "切换周期"],
    ["code", "报警码"],
    ["text", "报警文本"],
  ],
  opcua: [
    ["endpoint", "OPC UA 服务地址"],
    ["active_count_node_id", "有效报警数量节点 ID"],
    ["alarm_code_list_node_id", "报警码数组节点 ID"],
    ["username", "用户名"],
    ["password", "密码", "password"],
    ["publishing_interval_ms", "发布间隔（毫秒）", "number"],
    ["sampling_interval_ms", "采样间隔（毫秒）", "number"],
    ["max_alarm_count", "最大报警数量", "number"],
    ["inactive_code", "忽略的报警码"],
  ],
  opcua_prodiag: [
    ["endpoint", "OPC UA 服务地址"],
    ["event_node_id", "事件节点 ID（可留空自动查找）"],
    ["username", "用户名"],
    ["password", "密码", "password"],
    ["publishing_interval_ms", "发布间隔（毫秒）", "number"],
    ["event_queue_size", "事件队列容量", "number"],
  ],
  s7: [
    ["host", "PLC 地址"],
    ["cpu_type", "CPU 类型"],
    ["rack", "机架号", "number"],
    ["slot", "槽位号", "number"],
    ["db_number", "DB 编号", "number"],
    ["trigger_byte", "触发字节偏移", "number"],
    ["trigger_bit", "触发位", "number"],
    ["code_offset", "报警码偏移", "number"],
    ["text_offset", "文本偏移", "number"],
    ["text_length", "文本长度", "number"],
  ],
  modbus_tcp: [
    ["host", "设备地址"],
    ["port", "端口", "number"],
    ["unit_id", "从站 ID", "number"],
    ["table", "数据表"],
    ["address", "地址", "number"],
    ["active_value", "触发值"],
    ["code", "报警码"],
    ["text", "报警文本"],
  ],
  modbus_rtu: [
    ["port", "串口设备（如 /dev/ttyUSB0）"],
    ["baudrate", "波特率", "number"],
    ["parity", "校验位"],
    ["stopbits", "停止位"],
    ["unit_id", "从站 ID", "number"],
    ["table", "数据表"],
    ["address", "地址", "number"],
    ["active_value", "触发值"],
    ["code", "报警码"],
    ["text", "报警文本"],
  ],
  mqtt: [
    ["host", "Broker 地址"],
    ["port", "端口", "number"],
    ["topic", "主题"],
    ["username", "用户名"],
    ["password", "密码", "password"],
    ["active_key", "激活字段"],
    ["code_key", "报警码字段"],
    ["text_key", "文本字段"],
  ],
  http_json: [
    ["url", "请求地址"],
    ["method", "请求方法"],
    ["headers_json", "请求头 JSON"],
    ["active_key", "激活字段"],
    ["code_key", "报警码字段"],
    ["text_key", "文本字段"],
  ],
  tcp_json: [
    ["host", "设备地址"],
    ["port", "端口", "number"],
    ["request_text", "请求文本"],
    ["delimiter", "结束符"],
    ["active_key", "激活字段"],
    ["code_key", "报警码字段"],
    ["text_key", "文本字段"],
  ],
  udp_json: [
    ["host", "设备地址"],
    ["port", "端口", "number"],
    ["request_text", "请求文本"],
    ["active_key", "激活字段"],
    ["code_key", "报警码字段"],
    ["text_key", "文本字段"],
  ],
  serial_text: [
    ["port", "串口设备"],
    ["baudrate", "波特率", "number"],
    ["request_text", "请求文本"],
    ["active_text", "报警关键字"],
    ["code", "报警码"],
    ["text", "报警文本"],
  ],
  ads: [
    ["net_id", "AMS Net ID"],
    ["port", "ADS 端口", "number"],
    ["symbol", "变量符号"],
    ["code", "报警码"],
    ["text", "报警文本"],
  ],
};
const esc = (v) =>
  String(v ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
const id = (p) => p + "_" + Math.random().toString(36).slice(2, 10);
async function api(url, opt = {}) {
  const r = await fetch(url, {
    ...opt,
    headers: { "Content-Type": "application/json", ...(opt.headers || {}) },
    signal: opt.signal || AbortSignal.timeout(20000),
    cache: "no-store",
  }).catch((error) => {
    if (error.name === "TimeoutError")
      throw Error("请求超时，请检查服务连接后重试。");
    if (error instanceof TypeError)
      throw Error("无法连接服务，请检查网络后重试。");
    throw error;
  });
  const data = await r.json().catch(() => {
    throw Error(`服务返回了无效响应（${r.status}），请刷新页面后重试。`);
  });
  if (r.status === 401) {
    showLogin();
    throw Error(data.error || "登录已失效");
  }
  if (!r.ok) throw Error(data.error || `请求失败 ${r.status}`);
  return data;
}
function toast(msg, error = false) {
  const el = $("#toast");
  el.textContent = msg;
  el.className = "toast show" + (error ? " error" : "");
  clearTimeout(el.t);
  el.t = setTimeout(() => (el.className = "toast"), 3500);
}
function showLogin() {
  $("#app").classList.add("hidden");
  $("#login").classList.remove("hidden");
}
function showApp() {
  $("#login").classList.add("hidden");
  $("#app").classList.remove("hidden");
}
function field(label, key, value, type = "text", extra = "") {
  return `<label class="field"><span>${label}</span><input name="${key}" type="${type}" value="${esc(value)}" ${extra}></label>`;
}
function textareaField(label, key, value, rows = 3) {
  return `<label class="field"><span>${label}</span><textarea name="${key}" rows="${rows}">${esc(value)}</textarea></label>`;
}
function selectField(label, key, value, items) {
  return `<label class="field"><span>${label}</span><select name="${key}">${items.map((x) => `<option value="${esc(x[0])}" ${x[0] === value ? "selected" : ""}>${esc(x[1])}</option>`).join("")}</select></label>`;
}
function toggle(label, key, value) {
  return `<label class="toggle-row"><span>${label}</span><span class="switch"><input name="${key}" type="checkbox" ${value ? "checked" : ""}><i></i></span></label>`;
}
function getParam(obj, key) {
  const value = obj.params?.[key];
  return value === undefined ? "" : value;
}
function setByForm(form, obj, basic, params) {
  const data = new FormData(form);
  for (const key of basic)
    obj[key] =
      form.elements[key]?.type === "checkbox"
        ? form.elements[key].checked
        : data.get(key);
  for (const key of params) obj.params[key] = data.get(key) ?? "";
}

async function boot() {
  const auth = await api("/api/auth/status");
  if (!auth.authenticated) {
    showLogin();
    return;
  }
  showApp();
  await loadAll();
}
async function loadAll(preserveDraft = false) {
  const [applied, status, sourceDefaults] = await Promise.all([
    api("/api/config"),
    api("/api/overview"),
    api("/api/defaults"),
  ]);
  const keepDraft = preserveDraft && isDirty();
  if (!keepDraft) config = applied;
  overview = status;
  defaults = sourceDefaults;
  savedConfig = JSON.stringify(applied);
  renderAll();
  updateDirty();
  if (keepDraft) toast("已重新登录，未保存的修改已保留，请确认后保存。");
}
function renderAll() {
  renderOverview();
  renderSources();
  renderRules();
  renderTargets();
  populateHistorySources();
  renderDeviceContext();
}
function renderOverview() {
  if (!overview) return;
  const total = overview.enabled_count,
    online = overview.connected_count,
    healthy = overview.running && total > 0 && online === total;
  const status = !overview.running
    ? "采集服务已停止"
    : !total
      ? "服务就绪 · 待启用设备"
      : healthy
        ? "全部设备连接正常"
        : `有 ${total - online} 台设备等待连接`;
  $("#hero-state").textContent = status;
  $("#side-state").textContent = status;
  $("#side-device").textContent = `${online} / ${total} 台设备在线`;
  document.body.dataset.health = healthy
    ? "online"
    : overview.running
      ? "warning"
      : "stopped";
  $("#m-online").textContent = online;
  $("#m-total").textContent = `共 ${total} 台已启用`;
  $("#m-alarms").textContent = overview.alarm_count;
  $("#m-uptime").textContent = formatDuration(overview.uptime_seconds);
  $("#m-targets").textContent =
    config?.targets?.filter((x) => x.enabled).length || 0;
  $("#connection-rate").textContent = total
    ? Math.round((online / total) * 100) + "%"
    : "—";
  $("#health-ring").style.setProperty(
    "--rate",
    total ? (online / total) * 100 + "%" : "0%",
  );
  $("#sync-state").textContent = "刚刚更新";
  $("#sync-state").classList.remove("stale");
  $("#device-list").innerHTML = overview.devices.length
    ? overview.devices
        .map((x) => {
          const c = x.connection;
          return `<button type="button" class="device-row" data-open-device="${esc(x.id)}"><span class="device-icon" aria-hidden="true"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8m-4-4v4M6 11h3l2-4 3 7 2-3h2"/></svg></span><div><b>${esc(x.name)}</b><small>${esc(labels[x.protocol] || x.protocol)}${c?.connected_since && x.connected ? " · 已连接 " + formatDuration((Date.now() - new Date(c.connected_since)) / 1000) : ""}</small><small>${x.state === "online" ? `心跳 ${c?.last_success ? Math.max(0, Math.floor((Date.now() - new Date(c.last_success)) / 1000)) + " 秒前" : "等待中"} · 有效报警 ${c?.active_alarm_count || 0}` : esc(c?.detail || (x.enabled ? "等待连接" : "设备已停用"))}</small></div><span class="state ${x.state}">${x.state === "online" ? "在线" : x.state === "disabled" ? "已禁用" : "离线"}</span></button>`;
        })
        .join("")
    : '<div class="empty">尚未配置设备<br>添加设备后即可查看连接状态</div>';
  $("#event-list").innerHTML = overview.events.length
    ? overview.events
        .slice(0, 12)
        .map(
          (x) =>
            `<div class="event ${esc(x.level)}"><i></i><div><b>${esc(x.message)}</b><time>${new Date(x.time).toLocaleString("zh-CN", { hour12: false })}</time></div></div>`,
        )
        .join("")
    : '<div class="empty">暂无运行事件</div>';
  renderSourceDiagnostics();
  if ($("#collection-switch")) $("#collection-switch").checked = overview.running;
  if ($("#device-live")) $("#device-live").textContent = deviceStateLabel();
  renderTelemetry();
}
function formatDuration(sec) {
  sec = Math.max(0, sec | 0);
  const d = Math.floor(sec / 86400),
    h = String(Math.floor((sec % 86400) / 3600)).padStart(2, "0"),
    m = String(Math.floor((sec % 3600) / 60)).padStart(2, "0");
  return d ? `${d}天 ${h}:${m}` : `${h}:${m}`;
}
function renderTelemetry() {
  const all = overview?.telemetry || [];
  const minutes = Number($("#trend-range").value);
  const points = all.filter((p) => new Date(p.time).getTime() >= Date.now() - minutes * 60000);
  $("#alarm-trend").innerHTML = TelemetryUI.chart(points, [
    {key:"new_alarms",label:"新增报警",color:"#147d64"},
    {key:"recoveries",label:"恢复事件",color:"#3d8ba3"},
    {key:"connected",label:"在线设备（右轴）",color:"#7d8273",right:true},
  ], Math.max(1,...points.map((p) => p.enabled)), $("#alarm-trend").clientWidth);
  $("#load-trend").innerHTML = TelemetryUI.chart(points, [
    {key:"average_read_ms",label:"平均采集耗时（毫秒）",color:"#147d64"},
    {key:"cpu_percent",label:"程序 CPU（右轴 %）",color:"#7d8273",right:true},
  ],100,$("#load-trend").clientWidth);
  const latest = all.at(-1);
  const metric = (title,value) => `<div><span>${title}</span><strong>${esc(value)}</strong></div>`;
  $("#telemetry-current").innerHTML = latest ? [
    metric("当前激活报警",latest.active_alarms),
    metric("采集次数 / 秒",latest.reads_per_second),
    metric("平均采集耗时",latest.average_read_ms + " ms"),
    metric("采集成功率",latest.reads_per_second > 0 ? latest.read_success_percent + "%" : "暂无采集"),
    metric("程序 CPU",latest.cpu_percent + "%"),
    metric("程序内存",latest.memory_mb + " MB"),
    metric("待发送通知",latest.queue_depth),
    metric("本周期投递失败",latest.failed_deliveries),
  ].join("") : '<p>等待服务采样…</p>';
}
$("#trend-range").onchange = renderTelemetry;
let trendResizeTimer;
window.addEventListener("resize", () => {
  clearTimeout(trendResizeTimer);
  trendResizeTimer = setTimeout(renderTelemetry, 120);
});

function renderSources() {
  const list = config.sources || [];
  selected.source = Math.min(selected.source, Math.max(0, list.length - 1));
  $("#source-tabs").innerHTML = list
    .map(
      (x, i) =>
        `<button type="button" class="item-card ${i === selected.source ? "active" : ""}" data-source="${i}"><strong>${esc(x.name)}</strong><small>${esc(labels[x.protocol] || x.protocol)} · ${x.enabled ? "已启用" : "已停用"}</small></button>`,
    )
    .join("");
  const x = list[selected.source],
    form = $("#source-form");
  if (!x) {
    form.innerHTML =
      '<div class="empty full-span"><strong>从第一台设备开始</strong><p>选择采集协议，填写地址后，即可单独检查连接、配置报警和查看历史。</p><button type="button" class="primary" data-start="source">添加采集设备</button></div>';
    return;
  }
  const protocols = [
    "mock",
    "opcua",
    "opcua_prodiag",
    "s7",
    "modbus_tcp",
    "modbus_rtu",
    "mqtt",
    "http_json",
    "tcp_json",
    "udp_json",
    "serial_text",
    "ads",
  ];
  const specs = sourceFields[x.protocol] || [];
  form.innerHTML = `<h3>${esc(x.name)} <span class="chip">${esc(labels[x.protocol] || x.protocol)}</span></h3>${field("设备名称", "name", x.name)}${selectField(
    "采集协议",
    "protocol",
    x.protocol,
    protocols.map((p) => [p, labels[p] || p]),
  )}${field("采集间隔（秒）", "poll_seconds", x.poll_seconds, "number", 'min="0.2" step="0.1"')}${toggle("启用采集", "enabled", x.enabled)}<p class="section-title">协议参数</p>${specs
    .map((a) => {
      const f = field(a[1], `p_${a[0]}`, getParam(x, a[0]), a[2] || "text");
      return /node_id|endpoint|url|json|symbol/.test(a[0])
        ? f.replace('class="field"', 'class="field full-span"')
        : f;
    })
    .join(
      "",
    )}<div class="editor-actions"><button type="button" class="danger" data-delete="source">删除设备</button><button type="button" class="ghost" data-test-source="${esc(x.id)}">测试连接</button></div>`;
  form.oninput = form.onchange = () => {
    setByForm(
      form,
      x,
      ["name", "protocol", "poll_seconds", "enabled"],
      specs.map((a) => "p_" + a[0]),
    );
    x.poll_seconds = Number(x.poll_seconds);
    for (const a of specs)
      ((x.params[a[0]] = x.params["p_" + a[0]]), delete x.params["p_" + a[0]]);
    if (x.protocol !== form.dataset.protocol) {
      x.params = {
        ...(defaults[x.protocol] || {}),
        ...Object.fromEntries(
          ["endpoint", "host", "username", "password"]
            .filter((k) => x.params[k] !== undefined)
            .map((k) => [k, x.params[k]]),
        ),
      };
      form.dataset.protocol = x.protocol;
      renderSources();
    } else renderSourceTabOnly();
  };
  form.dataset.protocol = x.protocol;
  organizeSourcePanels(form, x);
  renderDeviceContext(true);
}
function renderSourceTabOnly() {
  const x = config.sources[selected.source],
    b = $(`[data-source="${selected.source}"]`);
  if (b)
    b.innerHTML = `<strong>${esc(x.name)}</strong><small>${esc(labels[x.protocol] || x.protocol)} · ${x.enabled ? "已启用" : "已停用"}</small>`;
  const heading = $("#source-form h3");
  if (heading?.firstChild) heading.firstChild.textContent = x.name + " ";
  renderDeviceContext(true);
}

function renderRules() {
  const device = config.sources[selected.source];
  const list = (config.rules || []).filter((rule) => rule.source_id === device?.id);
  if (!list.some((rule) => rule === config.rules[selected.rule]))
    selected.rule = config.rules.indexOf(list[0]);
  $("#rule-tabs").innerHTML = list
    .map(
      (x) =>
        `<button type="button" class="item-card ${x === config.rules[selected.rule] ? "active" : ""}" data-rule="${config.rules.indexOf(x)}"><strong>${esc(x.name)}</strong><small>${esc({ info: "提示", warning: "警告", critical: "严重", emergency: "紧急" }[x.severity] || x.severity)} · ${x.alarm_texts?.filter((a) => a.code).length || 0} 条文本</small></button>`,
    )
    .join("");
  const x = config.rules[selected.rule],
    form = $("#rule-form");
  if (!x) {
    form.innerHTML =
      '<div class="empty full-span"><strong>为当前设备添加报警规则</strong><p>匹配报警码并关联描述文本，也可以导入已有的报警字典。</p><button type="button" class="primary" data-start="rule">添加报警规则</button></div>';
    return;
  }
  x.alarm_texts = x.alarm_texts || [];
  if (alarmRuleId !== x.id) {
    alarmRuleId = x.id;
    alarmPage = 1;
    alarmQuery = "";
  }
  alarmView = AlarmUI.dictionaryPage(
    x.alarm_texts,
    alarmQuery,
    alarmPage,
    draftAlarmRows,
  );
  alarmPage = alarmView.page;
  const rows = alarmView.items
    .map(
      ({ a, i }) =>
        `<tr><td><input type="checkbox" aria-label="选择报警文本" data-check-row="${i}" ${checkedAlarmRows.has(a) ? "checked" : ""}></td><td><select data-alarm="${i}:category">${[["默认", "跟随规则"], ...Object.entries(AlarmUI.levels), ...(!["默认", ...Object.keys(AlarmUI.levels)].includes(a.category) ? [[a.category, a.category]] : [])].map(([v, t]) => `<option value="${esc(v)}" ${a.category === v ? "selected" : ""}>${esc(t)}</option>`).join("")}</select></td><td><input data-alarm="${i}:code" value="${esc(a.code)}" placeholder="1001"></td><td><input data-alarm="${i}:instance_name" value="${esc(a.instance_name)}" placeholder="实例名"></td><td><input data-alarm="${i}:text" value="${esc(a.text)}" placeholder="报警描述"></td></tr>`,
    )
    .join("");
  form.innerHTML = `<h3>${esc(x.name)}</h3>${field("规则名称", "name", x.name)}${selectField(
    "所属设备",
    "source_id",
    x.source_id,
    device ? [[device.id, device.name]] : [],
  )}${selectField("报警级别", "severity", x.severity, [
    ["info", "提示"],
    ["warning", "警告"],
    ["critical", "严重"],
    ["emergency", "紧急"],
  ])}${field("匹配报警码（留空为全部）", "match_code", x.match_code)}${field("冷却时间（秒）", "cooldown_seconds", x.cooldown_seconds, "number", 'min="0"')}${toggle("启用规则", "enabled", x.enabled)}<label class="field full-span"><span>报警消息模板</span><textarea name="message_template" rows="3">${esc(x.message_template)}</textarea></label><p class="section-title">报警文本库 <span class="chip" id="alarm-row-count"></span></p><label class="field full-span search-field"><input id="alarm-search" value="${esc(alarmQuery)}" type="search" placeholder="搜索报警码、实例名或报警文本…" aria-label="搜索报警文本"></label><div class="import-bar"><button type="button" class="ghost" id="import-csv">导入 CSV / TSV</button><input id="csv-file" type="file" accept=".csv,.tsv,text/csv,text/tab-separated-values" hidden><button type="button" class="ghost" id="add-alarm-row">＋ 添加一行</button><button type="button" class="danger" id="delete-alarm-rows">删除勾选</button><label class="toggle-row" style="margin-left:auto"><span>全选本页</span><input id="check-all" type="checkbox"></label><button type="button" class="ghost" id="select-matching">全选匹配结果</button><button type="button" class="text-btn" id="unselect-alarms">取消选择</button></div><div class="alarm-table"><table><thead><tr><th></th><th>报警级别</th><th>报警码</th><th>实例名</th><th>详细文本</th></tr></thead><tbody>${rows || '<tr><td colspan="5" class="empty">暂无文本，支持导入 CSV</td></tr>'}</tbody></table></div><div class="pagination full-span"><button type="button" id="alarm-prev" ${alarmPage <= 1 ? "disabled" : ""}>← 上一页</button><span>第 ${alarmPage} / ${alarmView.pages} 页 · 每页 50 条</span><button type="button" id="alarm-next" ${alarmPage >= alarmView.pages ? "disabled" : ""}>下一页 →</button></div><div class="editor-actions"><button type="button" class="danger" data-delete="rule">删除规则</button><span class="muted">可导入列：报警码、实例名、详细文本、报警级别</span></div>`;
  form.oninput = form.onchange = (e) => {
    if (e.target.dataset.alarm) {
      const [i, k] = e.target.dataset.alarm.split(":");
      x.alarm_texts[+i][k] = e.target.value;
    } else {
      setByForm(
        form,
        x,
        [
          "name",
          "source_id",
          "severity",
          "match_code",
          "cooldown_seconds",
          "enabled",
          "message_template",
          "match_key",
          "match_value",
          "recovery_template",
        ],
        [],
      );
      x.cooldown_seconds = Number(x.cooldown_seconds);
    }
    renderRuleTabOnly();
    updateAlarmCount();
  };
  enhanceRuleForm(form, x);
}
function renderRuleTabOnly() {
  const x = config.rules[selected.rule],
    b = $(`[data-rule="${selected.rule}"]`);
  if (b)
    b.innerHTML = `<strong>${esc(x.name)}</strong><small>${esc({ info: "提示", warning: "警告", critical: "严重", emergency: "紧急" }[x.severity] || x.severity)} · ${x.alarm_texts?.filter((a) => a.code).length || 0} 条文本</small>`;
}

function renderTargets() {
  const list = config.targets || [];
  selected.target = Math.min(selected.target, Math.max(0, list.length - 1));
  $("#target-tabs").innerHTML = list
    .map(
      (x, i) =>
        `<button type="button" class="item-card ${i === selected.target ? "active" : ""}" data-target="${i}"><strong>${esc(x.name)}</strong><small>${esc(labels[x.channel] || x.channel)} · ${x.enabled ? "已启用" : "已停用"}</small></button>`,
    )
    .join("");
  const x = list[selected.target],
    form = $("#target-form");
  if (!x) {
    form.innerHTML =
      '<div class="empty full-span"><strong>把报警送到你关心的地方</strong><p>接入 Telegram、钉钉、企业微信等通知通道，再按需配置交互按钮。</p><button type="button" class="primary" data-start="target">添加通知目标</button></div>';
    return;
  }
  const isT = x.channel === "telegram",
    interactive = ["telegram", "dingtalk", "wecom"].includes(x.channel),
    cmds = parseCommands(x.params.command_defs);
  form.innerHTML = `<h3>${esc(x.name)} <span class="chip">${esc(labels[x.channel] || x.channel)}</span></h3>${field("目标名称", "name", x.name)}${selectField(
    "通知渠道",
    "channel",
    x.channel,
    ["telegram", "dingtalk", "wecom", "feishu", "custom_webhook"].map((p) => [
      p,
      labels[p],
    ]),
  )}${toggle("启用通知", "enabled", x.enabled)}${
    isT
      ? field("机器人 Token", "token", x.token, "password") +
        textareaField(
          "会话 ID（每行一个）",
          "recipients",
          (x.recipients || []).join("\n"),
          3,
        )
      : field(
          x.channel === "dingtalk"
            ? "群 Webhook（仅自定义机器人需要）"
            : "Webhook 地址",
          "webhook_url",
          x.webhook_url,
        ) +
        (x.channel === "dingtalk"
          ? field(
              "应用 Client ID",
              "p_bot_client_id",
              getParam(x, "bot_client_id"),
            ) +
            field(
              "应用 Client Secret",
              "p_bot_client_secret",
              getParam(x, "bot_client_secret"),
              "password",
            ) +
            selectField(
              "发送给",
              "p_ding_message_target",
              getParam(x, "ding_message_target") || "group",
              [
                ["users", "个人（企业 userId）"],
                ["group", "群聊（openConversationId）"],
              ],
            ) +
            (getParam(x, "ding_message_target") === "users"
              ? textareaField(
                  "接收人员 userId（每行一个）",
                  "recipients",
                  (x.recipients || []).join("\n"),
                  3,
                )
              : field(
                  "目标群 openConversationId",
                  "p_ding_conversation_id",
                  getParam(x, "ding_conversation_id"),
                )) +
            '<p class="muted full-span">企业应用机器人无需 Webhook。个人通知填写企业成员 userId，不能填手机号或 unionId；群通知填写 openConversationId，或保存并开启命令接收后在群内 @机器人发送 /status 自动记录，再刷新页面。</p>'
          : "")
  }${
    interactive
      ? `<p class="section-title">机器人交互</p>${toggle("启用命令与按钮", "p_receive_enabled", yes(getParam(x, "receive_enabled")))}${toggle("报警消息显示操作按钮", "p_alarm_show_buttons", yes(getParam(x, "alarm_show_buttons")))}${toggle("允许机器人写入 PLC", "p_allow_plc_write", yes(getParam(x, "allow_plc_write")))}${field("管理员用户 ID（逗号分隔）", "p_admin_ids", getParam(x, "admin_ids"))}${x.channel === "wecom" ? field("应用 Client ID", "p_bot_client_id", getParam(x, "bot_client_id")) + field("应用 Client Secret", "p_bot_client_secret", getParam(x, "bot_client_secret"), "password") : ""}${x.channel === "dingtalk" ? field("互动卡片模板 ID", "p_card_template_id", getParam(x, "card_template_id")) : ""}<div class="full-span"><div class="card-head"><div><p class="section-title">交互按钮</p><span class="muted">配置按钮文字、命令和动作</span></div><button type="button" class="ghost" id="add-command">＋ 添加按钮</button></div><div class="alarm-table command-table"><table><thead><tr><th>启用</th><th>按钮文字</th><th>命令</th><th>动作</th><th>回复或链接</th><th></th></tr></thead><tbody>${
          cmds
            .map(
              (c, i) =>
                `<tr><td><input type="checkbox" data-command="${i}:enabled" ${c.enabled !== false ? "checked" : ""}></td><td><input data-command="${i}:button" value="${esc(c.button || "")}" placeholder="查看状态"></td><td><input data-command="${i}:command" value="${esc(c.command || "")}" placeholder="/status"></td><td><select data-command="${i}:action">${[
                  ["reply", "文字回复"],
                  ["status", "设备状态"],
                  ["history", "历史报警"],
                  ["menu", "返回菜单"],
                  ["link", "打开链接"],
                  ["plc_write", "PLC 写入"],
                ]
                  .map(
                    (a) =>
                      `<option value="${a[0]}" ${c.action === a[0] ? "selected" : ""}>${a[1]}</option>`,
                  )
                  .join(
                    "",
                  )}</select></td><td><input data-command="${i}:reply" value="${esc(c.reply || "")}" placeholder="回复内容或 URL"></td><td><button type="button" class="danger" data-delete-command="${i}">删除</button></td></tr>`,
            )
            .join("") ||
          '<tr><td colspan="6" class="empty">尚未配置交互按钮</td></tr>'
        }</tbody></table></div><details><summary>高级 JSON 配置（完整命令定义）</summary><label class="field"><textarea name="p_command_defs" rows="7">${esc(JSON.stringify(cmds, null, 2))}</textarea></label></details></div>`
      : ""
  }<div class="editor-actions"><button type="button" class="danger" data-delete="target">删除目标</button><button type="button" class="ghost" data-test-target="${esc(x.id)}">发送测试消息</button></div>`;
  form.oninput = form.onchange = (e) => {
    if (e.target.dataset.command) {
      const [i, k] = e.target.dataset.command.split(":");
      cmds[+i][k] =
        e.target.type === "checkbox"
          ? e.target.checked
          : e.target.type === "number"
            ? Number(e.target.value)
            : e.target.value;
      x.params.command_defs = JSON.stringify(cmds);
      form.elements.p_command_defs.value = JSON.stringify(cmds, null, 2);
      form.elements.p_command_defs.setCustomValidity("");
      if (["action", "source_id"].includes(k)) {
        renderTargets();
      }
      return;
    }
    const base = ["name", "channel", "enabled", isT ? "token" : "webhook_url"];
    setByForm(form, x, base, []);
    if (form.elements.recipients)
      x.recipients = String(new FormData(form).get("recipients") || "")
        .split(/\r?\n/)
        .map((v) => v.trim())
        .filter(Boolean);
    for (const key of [
      "receive_enabled",
      "alarm_show_buttons",
      "allow_plc_write",
    ])
      if (form.elements["p_" + key])
        x.params[key] = form.elements["p_" + key].checked ? "是" : "否";
    for (const key of [
      "admin_ids",
      "bot_client_id",
      "bot_client_secret",
      "ding_message_target",
      "ding_conversation_id",
      "card_template_id",
      "command_defs",
    ])
      if (form.elements["p_" + key])
        x.params[key] = new FormData(form).get("p_" + key) || "";
    if (e.target.name === "p_command_defs") {
      try {
        const parsed = JSON.parse(x.params.command_defs);
        if (!Array.isArray(parsed)) throw Error();
        cmds.splice(0, cmds.length, ...parsed);
        e.target.setCustomValidity("");
      } catch {
        e.target.setCustomValidity("交互配置必须是有效的 JSON 数组");
      }
    }
    if (
      x.channel !== form.dataset.channel ||
      e.target.name === "p_ding_message_target"
    ) {
      form.dataset.channel = x.channel;
      renderTargets();
    } else renderTargetTabOnly();
  };
  form.dataset.channel = x.channel;
  organizeTargetPanels(form, interactive);
  enhanceCommands(form, cmds);
  enhanceSecrets(form);
}
function yes(v) {
  return ["是", "true", "1", "yes"].includes(String(v).toLowerCase());
}
function parseCommands(v) {
  try {
    const commands = JSON.parse(v || "[]");
    return Array.isArray(commands) ? commands : [];
  } catch {
    return [];
  }
}
function renderTargetTabOnly() {
  const x = config.targets[selected.target],
    b = $(`[data-target="${selected.target}"]`);
  if (b)
    b.innerHTML = `<strong>${esc(x.name)}</strong><small>${esc(labels[x.channel] || x.channel)} · ${x.enabled ? "已启用" : "已停用"}</small>`;
}

function populateHistorySources() {
  const old = $("#history-source").value;
  $("#history-source").innerHTML =
    '<option value="">选择设备</option>' +
    (config.sources || [])
      .map(
        (x) =>
          `<option value="${esc(x.name)}">${esc(x.name)}${overview?.devices.find((d) => d.id === x.id)?.connected ? " · 在线" : ""}</option>`,
      )
      .join("");
  $("#history-source").value = old;
}
async function loadHistory() {
  const request = ++historyRequest,
    source = $("#history-source").value;
  $("#history-prev").disabled = true;
  $("#history-next").disabled = true;
  $("#clear-history").disabled = !source;
  if (!source) {
    $("#history-body").innerHTML =
      '<tr><td colspan="6" class="empty">先选择设备，再查看最近的报警与恢复记录</td></tr>';
    $("#history-page").textContent = "请选择设备";
    return;
  }
  try {
    const data = await api(
      `/api/history?source=${encodeURIComponent(source)}&page=${historyPage}&pageSize=10`,
    );
    if (request !== historyRequest) return;
    historyPages = Math.max(1, Math.ceil(data.total / 10));
    historyPage = data.page;
    $("#history-body").innerHTML = data.items.length
      ? data.items
          .map(
            (x) =>
              `<tr><td><span class="state ${x.state === "recovery" ? "online" : "offline"}">${x.state === "recovery" ? "已恢复" : "报警"}</span></td><td>${new Date(x.time).toLocaleString("zh-CN", { hour12: false })}</td><td>${esc(x.source_name)}</td><td><code>${esc(x.code)}</code></td><td>${esc(x.text)}</td><td><span class="severity ${esc(x.severity)}">${esc(AlarmUI.levels[x.severity] || x.severity)}</span></td></tr>`,
          )
          .join("")
      : '<tr><td colspan="6" class="empty">该设备暂无报警记录</td></tr>';
    $("#history-page").textContent =
      `第 ${historyPage} / ${historyPages} 页 · 最近 ${data.total} 条`;
    $("#history-prev").disabled = historyPage <= 1;
    $("#history-next").disabled = historyPage >= historyPages;
  } catch (e) {
    if (request !== historyRequest) return;
    $("#history-body").innerHTML =
      `<tr><td colspan="6" class="empty form-error">${esc(e.message)}</td></tr>`;
    $("#history-page").textContent = "读取失败";
    toast(e.message, true);
  }
}

function navigate(page) {
  if (page === "rules" || page === "history") {
    setDevicePanel(page);
    page = "sources";
  }
  $$("nav button").forEach((x) =>
    x.classList.toggle("active", x.dataset.page === page),
  );
  $$(".page").forEach((x) => x.classList.toggle("active", x.id === page));
  $("#page-title").textContent =
    $(`[data-page="${page}"]`)?.lastChild?.textContent.trim() || "";
  $(".sidebar").classList.remove("open");
  if (page === "dashboard") renderTelemetry();
  if (page === "history") {
    populateHistorySources();
    loadHistory();
  }
  if (page === "system")
    $("#raw-config").value = JSON.stringify(AlarmUI.redact(config), null, 2);
  if (page === "system") loadConsoleSettings().catch((e) => toast(e.message, true));
}
function ask(title, text, action) {
  $("#modal-title").textContent = title;
  $("#modal-text").textContent = text;
  confirmAction = action;
  $("#modal").classList.remove("hidden");
}
async function save() {
  if (saving) return;
  for (const f of $$(".editor"))
    if (!f.checkValidity()) {
      navigate(
        f.id.split("-")[0] === "source"
          ? "sources"
          : f.id.split("-")[0] === "rule"
            ? "rules"
            : "targets",
      );
      const invalid = f.querySelector(":invalid");
      for (let p = invalid?.parentElement; p && p !== f; p = p.parentElement)
        if (p.tagName === "DETAILS") p.open = true;
      const panel = invalid?.closest("[data-panel]");
      if (panel)
        f.querySelector(
          `[data-target-panel="${panel.dataset.panel}"]`,
        )?.click();
      f.reportValidity();
      return;
    }
  try {
    for (const target of config.targets) {
      const value = target.params.command_defs;
      if (value && !Array.isArray(JSON.parse(value)))
        throw Error("交互按钮配置必须是 JSON 数组。");
    }
  } catch {
    toast("交互按钮 JSON 格式有误，请在“通知与交互”中修正后再保存。", true);
    return;
  }
  const snapshot = JSON.stringify(config);
  saving = true;
  $("#save").disabled = true;
  $("#save").textContent = "正在应用…";
  try {
    await api("/api/config", { method: "PUT", body: snapshot });
    const applied = await api("/api/config");
    savedConfig = JSON.stringify(applied);
    if (JSON.stringify(config) === snapshot) {
      config = applied;
      renderAll();
    }
    toast("配置已保存并应用，停止状态的采集保持停止");
    overview = await api("/api/overview");
    renderOverview();
  } catch (e) {
    toast(e.message, true);
  } finally {
    saving = false;
    $("#save").disabled = false;
    updateDirty();
  }
}

document.addEventListener("click", async (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  if (b.dataset.page) navigate(b.dataset.page);
  if (b.dataset.goto) navigate(b.dataset.goto);
  if (b.dataset.source !== undefined) {
    selected.source = +b.dataset.source;
    renderSources();
    renderRules();
    renderDeviceContext();
  }
  if (b.dataset.rule !== undefined) {
    selected.rule = +b.dataset.rule;
    renderRules();
  }
  if (b.dataset.target !== undefined) {
    selected.target = +b.dataset.target;
    renderTargets();
  }
  if (b.dataset.runtime) {
    b.disabled = true;
    try {
      await api("/api/runtime/" + b.dataset.runtime, { method: "POST" });
      toast("服务操作已完成");
      overview = await api("/api/overview");
      renderOverview();
    } catch (x) {
      toast(x.message, true);
    } finally {
      b.disabled = false;
    }
  }
  if (b.dataset.testSource) {
    b.disabled = true;
    try {
      const source = config.sources.find((x) => x.id === b.dataset.testSource);
      toast("正在测试当前填写的连接参数…");
      const r = await api("/api/test/source", {
        method: "POST",
        body: JSON.stringify(source),
      });
      toast(r.message);
    } catch (x) {
      toast(x.message, true);
    } finally {
      b.disabled = false;
    }
  }
  if (b.dataset.testTarget) {
    b.disabled = true;
    try {
      const target = config.targets.find((x) => x.id === b.dataset.testTarget);
      if (!target) throw new Error("请选择要测试的通知目标。");
      toast("正在发送测试消息…");
      const r = await api("/api/test/target", {
        method: "POST",
        body: JSON.stringify(target),
      });
      toast(r.message);
    } catch (x) {
      toast(x.message, true);
    } finally {
      b.disabled = false;
    }
  }
  if (b.dataset.delete) {
    const kind = b.dataset.delete,
      arr =
        config[
          kind === "source" ? "sources" : kind === "rule" ? "rules" : "targets"
        ];
    const idx = selected[kind];
    ask(
      "确认删除",
      `确定删除当前${kind === "source" ? "设备" : kind === "rule" ? "规则" : "通知目标"}吗？`,
      () => {
        const removed = arr.splice(idx, 1)[0];
        if (kind === "source")
          config.rules = config.rules.filter((r) => r.source_id !== removed.id);
        renderAll();
        updateDirty();
      },
    );
  }
  if (b.id === "add-source") {
    config.sources.push({
      id: id("src"),
      name: "新设备",
      protocol: "opcua",
      enabled: false,
      poll_seconds: 1,
      params: {
        endpoint: "opc.tcp://192.168.1.10:4840",
        active_count_node_id: "ns=4;s=Alarm.ActiveAlarmCount",
        alarm_code_list_node_id: "ns=4;s=Alarm.AlarmCodeList",
        publishing_interval_ms: "250",
        sampling_interval_ms: "250",
        max_alarm_count: "1000",
        inactive_code: "0",
        timeout: "5",
      },
    });
    selected.source = config.sources.length - 1;
    renderSources();
    renderRules();
    renderDeviceContext();
  }
  if (b.id === "add-rule") {
    config.rules.push({
      id: id("rule"),
      name: "新报警规则",
      source_id: config.sources[selected.source]?.id || "",
      enabled: true,
      severity: "warning",
      match_code: "",
      match_key: "",
      match_value: "",
      cooldown_seconds: 0,
      alarm_texts: [],
      alarm_text_lists: [],
      message_template:
        "【{severity}】{source_name}\n报警编号：{code}\n报警内容：{text}\n时间：{time}",
      recovery_template:
        "【恢复】{source_name}\n报警编号：{code}\n时间：{time}",
      note: "",
    });
    selected.rule = config.rules.length - 1;
    renderRules();
  }
  if (b.id === "add-command") {
    const x = config.targets[selected.target],
      cmds = parseCommands(x.params.command_defs);
    if (cmds.length >= 80) {
      toast("每个机器人最多配置 80 个按钮", true);
      return;
    }
    let next = "/status",
      n = 1;
    while (cmds.some((c) => c.command === next)) next = "/action_" + ++n;
    cmds.push({
      enabled: true,
      command: next,
      button: n === 1 ? "查看状态" : "新按钮 " + n,
      action: "status",
      reply: "",
      source_id: "",
      db_number: 10,
      address: 0,
      bit: 0,
      value: 1,
      write_type: "bit",
      pulse_ms: 0,
      opc_node_id: "",
      opc_data_type: "Boolean",
      opc_value: "true",
      opc_reset_value: "false",
    });
    x.params.command_defs = JSON.stringify(cmds);
    renderTargets();
  }
  if (b.dataset.deleteCommand !== undefined) {
    const x = config.targets[selected.target],
      cmds = parseCommands(x.params.command_defs);
    cmds.splice(+b.dataset.deleteCommand, 1);
    x.params.command_defs = JSON.stringify(cmds);
    renderTargets();
  }
  if (b.id === "add-target") {
    config.targets.push({
      id: id("target"),
      name: "新通知目标",
      channel: "telegram",
      enabled: false,
      token: "",
      webhook_url: "",
      recipients: [],
      params: {
        receive_enabled: "否",
        allow_plc_write: "否",
        alarm_show_buttons: "否",
        admin_ids: "",
        command_defs: "[]",
        button_columns: "2",
      },
    });
    selected.target = config.targets.length - 1;
    renderTargets();
  }
  if (b.id === "add-alarm-row") {
    const draft = {
      category: "warning",
      code: "",
      instance_name: "",
      fb_number: "",
      text_list_name: "",
      text_list_index: "",
      text: "",
    };
    draftAlarmRows.add(draft);
    config.rules[selected.rule].alarm_texts.push(draft);
    alarmQuery = "";
    alarmPage = Math.ceil(
      AlarmUI.dictionaryPage(
        config.rules[selected.rule].alarm_texts,
        "",
        1,
        draftAlarmRows,
      ).total / 50,
    );
    renderRules();
  }
  if (b.id === "delete-alarm-rows") {
    const checks = config.rules[selected.rule].alarm_texts
      .map((a, i) => (checkedAlarmRows.has(a) ? i : -1))
      .filter((i) => i >= 0);
    if (!checks.length) {
      toast("请先勾选要删除的文本");
      return;
    }
    const rule = config.rules[selected.rule];
    ask(
      "删除报警文本",
      `将删除 ${checks.length} 条勾选的文本，保存后生效。`,
      () => {
        rule.alarm_texts = rule.alarm_texts.filter(
          (a) => !checkedAlarmRows.has(a),
        );
        renderRules();
        updateDirty();
      },
    );
  }
  if (b.id === "import-csv") $("#csv-file").click();
});
$("#login-form").onsubmit = async (e) => {
  e.preventDefault();
  const button = e.currentTarget.querySelector('button[type="submit"]');
  button.disabled = true;
  button.setAttribute("aria-busy", "true");
  button.textContent = "正在登录…";
  $("#login-error").textContent = "";
  try {
    await api("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ password: $("#password").value }),
    });
    showApp();
    await loadAll(true);
  } catch (x) {
    $("#login-error").textContent = x.message;
  } finally {
    button.disabled = false;
    button.removeAttribute("aria-busy");
    button.textContent = "登录工作台 →";
  }
};
$("#save").onclick = save;
$("#logout").onclick = () => {
  const action = async () => {
    try {
      await api("/api/auth/logout", { method: "POST" });
      config = null;
      savedConfig = "";
      showLogin();
    } catch (e) {
      toast(e.message, true);
    }
  };
  if (isDirty())
    ask("退出登录", "有未保存的修改，退出后这些修改将丢失。", action);
  else action();
};
$("#menu").onclick = () => $(".sidebar").classList.toggle("open");
$("#modal-cancel").onclick = () => $("#modal").classList.add("hidden");
$("#modal-ok").onclick = async () => {
  const action = confirmAction;
  confirmAction = null;
  $("#modal").classList.add("hidden");
  try {
    await action?.();
  } catch (e) {
    toast(e.message, true);
  }
};
$("#refresh-history").onclick = () => {
  historyPage = 1;
  loadHistory();
};
$("#history-source").onchange = () => {
  historyPage = 1;
  loadHistory();
};
$("#history-prev").onclick = () => {
  if (historyPage > 1) {
    historyPage--;
    loadHistory();
  }
};
$("#history-next").onclick = () => {
  if (historyPage < historyPages) {
    historyPage++;
    loadHistory();
  }
};
$("#clear-history").onclick = () => {
  if (!$("#history-source").value) {
    toast("请先选择设备", true);
    return;
  }
  const source = $("#history-source").value;
  ask(
    "清除历史报警",
    `将删除“${source}”的历史报警，此操作无法撤销。`,
    async () => {
      try {
        const s = encodeURIComponent(source);
        const r = await api("/api/history?source=" + s, { method: "DELETE" });
        toast(`已删除 ${r.deleted} 条记录`);
        loadHistory();
      } catch (x) {
        toast(x.message, true);
      }
    },
  );
};
document.addEventListener("change", async (e) => {
  if (e.target.id === "check-all")
    $$("[data-check-row]").forEach((input) => {
      input.checked = e.target.checked;
      const row =
        config.rules[selected.rule].alarm_texts[+input.dataset.checkRow];
      if (input.checked) checkedAlarmRows.add(row);
      else checkedAlarmRows.delete(row);
    });
  if (e.target.dataset.checkRow !== undefined) {
    const row =
      config.rules[selected.rule].alarm_texts[+e.target.dataset.checkRow];
    if (e.target.checked) checkedAlarmRows.add(row);
    else checkedAlarmRows.delete(row);
  }
  if (e.target.dataset.checkRow !== undefined || e.target.id === "check-all")
    updateAlarmCount();
  if (e.target.id === "csv-file") {
    const f = e.target.files[0],
      rule = config.rules[selected.rule];
    if (!f) return;
    try {
      const result = AlarmUI.importDictionary(await f.text());
      rule[result.kind] ??= [];
      rule[result.kind].push(...result.rows);
      if (config.rules[selected.rule] === rule) renderRules();
      updateDirty();
      toast(
        `已导入 ${result.rows.length} 条${result.kind === "alarm_text_lists" ? "文本列表" : "报警文本"}${result.skipped ? "，跳过 " + result.skipped + " 条空记录" : ""}，保存后生效`,
      );
    } catch (error) {
      toast(error.message, true);
    }
  }
});

setInterval(
  () =>
    ($("#clock").textContent = new Date().toLocaleString("zh-CN", {
      hour12: false,
    })),
  1000,
);
setInterval(async () => {
  if (
    polling ||
    !config ||
    document.hidden ||
    $("#app").classList.contains("hidden")
  )
    return;
  polling = true;
  try {
    overview = await api("/api/overview");
    renderOverview();
  } catch {
    $("#sync-state").textContent = "连接中断 · 正在重试";
    $("#sync-state").classList.add("stale");
    document.body.dataset.health = "stopped";
  } finally {
    polling = false;
  }
}, 5000);
boot().catch((e) => {
  showLogin();
  $("#login-error").textContent = e.message;
});

$("#luci-link").href =
  location.hostname === "alarm.zxzyzy.cn" ? "https://lu.zxzyzy.cn/cgi-bin/luci/"
    : location.protocol + "//" + location.hostname + "/cgi-bin/luci/";
$("#password-form").onsubmit = async (e) => {
  e.preventDefault();
  const form = e.currentTarget,
    data = new FormData(form),
    button = form.querySelector('button[type="submit"]'),
    error = $("#password-error");
  error.textContent = "";
  if (data.get("new_password") !== data.get("confirm_password")) {
    error.textContent = "两次输入的新密码不一致。";
    return;
  }
  button.disabled = true;
  try {
    await api("/api/security/password", {
      method: "POST",
      body: JSON.stringify({
        current_password: data.get("current_password"),
        new_password: data.get("new_password"),
      }),
    });
    form.reset();
    $("#password").value = "";
    showLogin();
    $("#login-error").textContent = "密码已修改，请使用新密码登录。";
    toast("密码已修改，所有会话已退出");
  } catch (x) {
    error.textContent = x.message;
  } finally {
    button.disabled = false;
  }
};
// Prevent the browser from submitting configuration forms on Enter.
for (const form of $$(".editor"))
  form.addEventListener("submit", (e) => e.preventDefault());

let targetPanel = "notify";
function organizeTargetPanels(form, interactive) {
  if (!interactive) return;
  const children = [...form.children],
    header = children.shift(),
    actions = children.pop(),
    split = children.findIndex((x) => x.classList.contains("section-title"));
  if (split < 0) return;
  const tabs = document.createElement("div");
  tabs.className = "full-span target-tabs";
  const sections = [
    ["notify", "通知通道", children.slice(0, split)],
    ["interaction", "机器人交互", children.slice(split)],
  ];
  const panels = [];
  for (const [key, title, nodes] of sections) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = title;
    button.dataset.targetPanel = key;
    tabs.append(button);
    const panel = document.createElement("div");
    panel.className = "full-span form-grid target-panel";
    panel.dataset.panel = key;
    for (const node of nodes) panel.append(node);
    panels.push(panel);
    button.onclick = () => {
      targetPanel = key;
      update();
    };
  }
  form.replaceChildren(header, tabs, ...panels, actions);
  function update() {
    for (const panel of panels)
      panel.classList.toggle("hidden", panel.dataset.panel !== targetPanel);
    for (const button of tabs.children)
    {
      const active = button.dataset.targetPanel === targetPanel;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
    }
  }
  update();
}

function isDirty() {
  return config && JSON.stringify(config) !== savedConfig;
}
function updateDirty() {
  const dirty = isDirty();
  $("#draft-state").textContent = dirty ? "有未保存修改" : "配置已同步";
  $("#draft-state").classList.toggle("dirty", !!dirty);
  if (!saving) $("#save").textContent = dirty ? "保存并应用" : "应用配置";
}
document.addEventListener("input", () => updateDirty());
document.addEventListener("change", () => updateDirty());
document.addEventListener("click", () => queueMicrotask(updateDirty));
window.addEventListener("beforeunload", (e) => {
  if (isDirty()) {
    e.preventDefault();
    e.returnValue = "";
  }
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    $("#modal").classList.add("hidden");
    confirmAction = null;
    $(".sidebar").classList.remove("open");
  }
  if ((e.ctrlKey || e.metaKey) && e.key === "s") {
    e.preventDefault();
    if (config) save();
  }
});
$("#modal-cancel").onclick = () => {
  $("#modal").classList.add("hidden");
  confirmAction = null;
};
document.addEventListener("click", (e) => {
  const button = e.target.closest("[data-open-device]");
  if (button) {
    selected.source = Math.max(
      0,
      config.sources.findIndex((x) => x.id === button.dataset.openDevice),
    );
    renderSources();
    renderRules();
    renderDeviceContext();
    navigate("sources");
  }
});
function renderSourceDiagnostics() {
  const panel = $("#source-diagnostics"),
    source = config?.sources[selected.source];
  if (!panel || !source) return;
  const device = overview?.devices.find((x) => x.id === source.id),
    c = device?.connection;
  panel.innerHTML = `<span class="state ${device?.state || "disabled"}">${deviceStateLabel()}</span><span>${esc(!overview?.running ? "请到系统设置开启采集" : c?.detail || "保存并启用后开始采集")}</span>${c?.last_success ? `<small>最近心跳 ${new Date(c.last_success).toLocaleTimeString("zh-CN", { hour12: false })}</small>` : ""}`;
}
function organizeSourcePanels(form, source) {
  const box = document.createElement("div");
  box.id = "source-diagnostics";
  box.className = "full-span source-diagnostics";
  form.children[0].after(box);
  const hint = document.createElement("div");
  hint.className = "full-span protocol-hint";
  hint.innerHTML =
    source.protocol === "opcua"
      ? "<b>报警码数组订阅</b><span>同一订阅读取有效数量与 DInt 数组，按 0～数量 − 1 匹配本地报警文本。</span>"
      : source.protocol === "opcua_prodiag"
        ? "<b>ProDiag 事件订阅</b><span>事件节点可留空自动发现。建议导入完整 AlarmId 字典及 PLC 报警文本列表。</span>"
        : source.protocol === "s7"
          ? "<b>S7 使用绝对地址</b><span>DB 必须关闭优化块访问，并在 PLC 中启用 PUT/GET 通信。</span>"
          : "<b>先测试，再应用</b><span>测试使用当前填写的参数，不会保存配置或发送报警。</span>";
  box.after(hint);
  const keys = [
    "username",
    "password",
    "publishing_interval_ms",
    "sampling_interval_ms",
    "max_alarm_count",
    "inactive_code",
    "event_queue_size",
  ];
  const advanced = document.createElement("details");
  advanced.className = "full-span advanced-section";
  advanced.innerHTML =
    '<summary>认证与采集调优 <span>可选参数</span></summary><div class="form-grid"></div>';
  for (const key of keys) {
    const input = form.elements["p_" + key];
    if (input)
      advanced.querySelector(".form-grid").append(input.closest(".field"));
  }
  if (advanced.querySelector(".form-grid").children.length)
    form.querySelector(".editor-actions").before(advanced);
  enhanceSecrets(form);
  renderSourceDiagnostics();
}
function enhanceSecrets(form) {
  for (const input of form.querySelectorAll("input[type=password]")) {
    if (input.parentElement.classList.contains("secret-input")) continue;
    const wrap = document.createElement("div");
    wrap.className = "secret-input";
    input.before(wrap);
    wrap.append(input);
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secret-toggle";
    button.textContent = "显示";
    button.setAttribute(
      "aria-label",
      "显示或隐藏" +
        (input.closest("label")?.querySelector("span")?.textContent || "密码"),
    );
    button.onclick = () => {
      input.type = input.type === "password" ? "text" : "password";
      button.textContent = input.type === "password" ? "显示" : "隐藏";
    };
    wrap.append(button);
  }
}
function updateAlarmCount() {
  const rows = $$("[data-check-row]"),
    checked = (config?.rules[selected.rule]?.alarm_texts || []).filter((a) =>
      checkedAlarmRows.has(a),
    );
  if ($("#alarm-row-count"))
    $("#alarm-row-count").textContent =
      `匹配 ${alarmView?.matches || 0} / ${alarmView?.total || 0} 条`;
  if ($("#delete-alarm-rows")) {
    $("#delete-alarm-rows").disabled = !checked.length;
    $("#delete-alarm-rows").textContent = checked.length
      ? `删除勾选 (${checked.length})`
      : "删除勾选";
  }
  if ($("#check-all")) {
    $("#check-all").checked = !!rows.length && rows.every((x) => x.checked);
    $("#check-all").indeterminate =
      rows.some((x) => x.checked) && !$("#check-all").checked;
  }
}
function enhanceRuleForm(form, rule) {
  const search = $("#alarm-search");
  const applySearch = () => {
    const start = search.selectionStart,
      end = search.selectionEnd;
    alarmQuery = search.value;
    alarmPage = 1;
    renderRules();
    const updated = $("#alarm-search");
    updated.focus();
    if (start !== null && end !== null) updated.setSelectionRange(start, end);
  };
  search.oninput = (event) => {
    if (!event.isComposing) applySearch();
  };
  search.oncompositionend = applySearch;
  updateAlarmCount();
  const details = document.createElement("details");
  details.className = "full-span advanced-section";
  details.innerHTML = `<summary>恢复消息与附加匹配 <span>可选参数</span></summary><div class="form-grid">${field("匹配字段", "match_key", rule.match_key)}${field("字段匹配值", "match_value", rule.match_value)}${textareaField("恢复消息模板", "recovery_template", rule.recovery_template)}</div>`;
  form.querySelector(".editor-actions").before(details);
  const lists = rule.alarm_text_lists || [];
  if (lists.length) {
    const panel = document.createElement("details");
    panel.className = "full-span advanced-section";
    panel.innerHTML = `<summary>PLC 报警文本列表 <span>${lists.length} 条 · 预览前 50 条，可搜索完整列表</span></summary><label class="field"><input type="search" class="text-list-search" placeholder="搜索 PLC、文本列表或报警文本"></label><div class="alarm-table"><table><thead><tr><th>PLC</th><th>文本列表</th><th>范围</th><th>文本</th></tr></thead><tbody>${lists
      .slice(0, 50)
      .map(
        (row) =>
          `<tr><td>${esc(row.plc_name)}</td><td>${esc(row.list_name)}</td><td>${esc(row.from)}～${esc(row.to)}</td><td>${esc(row.text)}</td></tr>`,
      )
      .join("")}</tbody></table></div>`;
    panel.querySelector(".text-list-search").oninput = (e) => {
      const matches = lists.filter((row) =>
        AlarmUI.matches(
          e.target.value,
          row.plc_name,
          row.list_name,
          row.from,
          row.to,
          row.text,
        ),
      );
      panel.querySelector("tbody").innerHTML = matches
        .slice(0, 50)
        .map(
          (row) =>
            `<tr><td>${esc(row.plc_name)}</td><td>${esc(row.list_name)}</td><td>${esc(row.from)}～${esc(row.to)}</td><td>${esc(row.text)}</td></tr>`,
        )
        .join("");
    };
    form.querySelector(".editor-actions").before(panel);
  }
}

function enhanceCommands(form, commands) {
  const table = form.querySelector(".command-table");
  if (!table) return;
  const input = (i, key, label, value, type = "text") =>
    `<label class="field"><span>${esc(label)}</span><input data-command="${i}:${key}" type="${type}" value="${esc(value)}" ${type === "number" ? 'min="0"' : ""}></label>`;
  const sources = (config.sources || []).filter((x) =>
    [
      "mock",
      "s7",
      "modbus_tcp",
      "modbus_rtu",
      "opcua",
      "opcua_prodiag",
    ].includes(x.protocol),
  );
  const sourceSelect = (i, c) =>
    `<label class="field"><span>控制设备</span><select data-command="${i}:source_id"><option value="">请选择设备</option>${sources.map((x) => `<option value="${esc(x.id)}" ${x.id === c.source_id ? "selected" : ""}>${esc(x.name)} · ${esc(labels[x.protocol])}</option>`).join("")}</select></label>`;
  const rows = [...table.querySelectorAll("tbody>tr")];
  commands.forEach((c, i) => {
    if (c.action !== "plc_write") return;
    const source = sources.find((x) => x.id === c.source_id),
      opc = source?.protocol.startsWith("opcua");
    const row = document.createElement("tr");
    row.className = "plc-command-row";
    row.innerHTML = `<td colspan="6"><div class="plc-command-fields"><div class="full-span protocol-hint"><b>PLC 控制参数 · ${esc(c.button || c.command)}</b><span>执行前机器人会要求二次确认。脉冲为 0 时保持写入值。</span></div>${sourceSelect(i, c)}${input(i, "pulse_ms", "脉冲时间（毫秒）", c.pulse_ms || 0, "number")}${source ? (opc ? `${input(i, "opc_node_id", "OPC UA 节点 ID", c.opc_node_id)}<label class="field"><span>节点数据类型</span><select data-command="${i}:opc_data_type">${["Boolean", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String"].map((t) => `<option ${c.opc_data_type === t ? "selected" : ""}>${t}</option>`).join("")}</select></label>${input(i, "opc_value", "写入值", c.opc_value ?? "true")}${input(i, "opc_reset_value", "脉冲恢复值", c.opc_reset_value ?? "false")}` : `${source.protocol === "s7" ? input(i, "db_number", "S7 DB 编号", c.db_number ?? 10, "number") + input(i, "bit", "S7 位号（0～7）", c.bit || 0, "number") : ""}<label class="field"><span>写入类型</span><select data-command="${i}:write_type"><option value="bit" ${c.write_type !== "register" ? "selected" : ""}>位 / 线圈</option>${source.protocol.startsWith("modbus") ? `<option value="register" ${c.write_type === "register" ? "selected" : ""}>寄存器</option>` : ""}</select></label>${input(i, "address", "字节 / 寄存器地址", c.address || 0, "number")}${input(i, "value", "写入值", c.value ?? 1, "number")}`) : ""}</div></td>`;
    rows[i].after(row);
  });
}
if (new URLSearchParams(location.search).has("embedded"))
  document.body.classList.add("embedded");

document.addEventListener("click", (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  if (b.id === "alarm-prev" || b.id === "alarm-next") {
    alarmPage += b.id === "alarm-prev" ? -1 : 1;
    renderRules();
  }
  if (b.id === "select-matching") {
    for (const { a } of alarmView.filtered) checkedAlarmRows.add(a);
    renderRules();
  }
  if (b.id === "unselect-alarms") {
    checkedAlarmRows = new WeakSet();
    renderRules();
  }
});

// Each device owns its connection, rules and history workspace.
let devicePanel = "connection";
const deviceSections = {};
for (const [key, page] of [["connection", $("#sources")], ["rules", $("#rules")], ["history", $("#history")]]) {
  const panel = document.createElement("div");
  panel.className = "device-panel";
  panel.dataset.deviceSection = key;
  const children = [...page.children].filter((x) => x.id !== "device-context" && !x.classList.contains("device-subnav"));
  for (const child of children) panel.append(child);
  $("#sources").append(panel);
  deviceSections[key] = panel;
}
function setDevicePanel(panel) {
  devicePanel = panel;
  for (const [key, element] of Object.entries(deviceSections)) element.classList.toggle("hidden", key !== panel);
  for (const button of $$("[data-device-panel]")) {
    button.classList.toggle("active", button.dataset.devicePanel === panel);
    button.setAttribute("aria-pressed", String(button.dataset.devicePanel === panel));
  }
  if (panel === "history") { populateHistorySources(); renderDeviceContext(); loadHistory(); }
}
function renderDeviceContext(preserveControls = false) {
  const source = config?.sources[selected.source];
  const context = $("#device-context");
  if (preserveControls && context.dataset.sourceId === source?.id && $("#device-picker")) {
    context.querySelector("h3").textContent = source.name;
    const meta = context.querySelector("h3 + span");
    if (meta?.firstChild) meta.firstChild.textContent = (labels[source.protocol] || source.protocol) + " ";
    const option = $("#device-picker").options[selected.source];
    if (option) option.textContent = source.name;
    $("#device-live").textContent = deviceStateLabel();
    return;
  }
  context.dataset.sourceId = source?.id || "";
  $("#device-context").innerHTML = `<div><p>当前设备工作区</p><h3>${esc(source?.name || "添加你的第一台设备")}</h3><span>${esc(labels[source?.protocol] || "")} <b id="device-live">${deviceStateLabel()}</b></span></div><div class="device-context-controls"><label class="field"><span>切换设备</span><select id="device-picker" aria-label="切换设备">${(config?.sources || []).map((x, i) => `<option value="${i}" ${i === selected.source ? "selected" : ""}>${esc(x.name)}</option>`).join("")}</select></label><div class="button-row"><button type="button" class="ghost" id="check-device" ${source ? "" : "disabled"}>一键检查此设备</button><button type="button" class="primary" id="save-device" ${source ? "" : "disabled"}>保存此设备</button></div></div>`;
  $("#device-picker").onchange = (event) => {
    selected.source = Number(event.target.value);
    renderSources(); renderRules(); renderDeviceContext();
    if (devicePanel === "history") { historyPage = 1; loadHistory(); }
  };
  $("#add-rule").disabled = !source;
  $("#history-source").value = source?.name || "";
  $("#history-source").disabled = true;
  if (window.PhoneUI) PhoneUI.render(source);
}
function deviceStateLabel() {
  const source = config?.sources[selected.source];
  if (!source) return "";
  if (!source.enabled) return "设备已停用";
  if (!overview?.running) return "采集已停止";
  return overview.devices.find((x) => x.id === source.id)?.connected ? "连接正常" : "等待连接";
}
for (const button of $$("[data-device-panel]")) button.onclick = () => setDevicePanel(button.dataset.devicePanel);
setDevicePanel("connection");

$("#collection-switch").onchange = async (event) => {
  const control = event.target;
  control.disabled = true;
  try {
    await api("/api/runtime/" + (control.checked ? "start" : "stop"), { method: "POST" });
    overview = await api("/api/overview"); renderOverview();
  } catch (error) { toast(error.message, true); control.checked = overview?.running || false; }
  finally { control.disabled = false; }
};
async function loadConsoleSettings() {
  const values = await api("/api/settings");
  const form = $("#console-settings-form");
  for (const [key, value] of Object.entries(values)) {
    const input = form.elements[key];
    if (!input) continue;
    if (input.type === "checkbox") input.checked = value;
    else input.value = value;
  }
}
$("#console-settings-form").onsubmit = async (event) => {
  event.preventDefault();
  const form = event.currentTarget, data = new FormData(form), settings = {};
  for (const key of ["recovery_email", "smtp_host", "smtp_user", "smtp_password", "smtp_from"]) settings[key] = data.get(key);
  settings.smtp_port = Number(data.get("smtp_port"));
  settings.recovery_enabled = form.elements.recovery_enabled.checked;
  settings.start_collection_on_launch = form.elements.start_collection_on_launch.checked;
  const button = form.querySelector('button[type="submit"]'); button.disabled = true;
  try {
    const result = await api("/api/settings", { method: "PUT", body: JSON.stringify({ settings, current_password: data.get("current_password") }) });
    form.elements.current_password.value = ""; form.elements.smtp_password.value = "";
    $("#settings-error").textContent = ""; toast(result.message);
  } catch (error) { $("#settings-error").textContent = error.message; }
  finally { button.disabled = false; }
};
let recoveryRequestId = "";
$("#forgot-password").onclick = async () => {
  try {
    const info = await api("/api/auth/recovery");
    $("#recovery-panel").classList.remove("hidden");
    $("#recovery-hint").textContent = info.enabled ? `验证码将发到 ${info.email}，10 分钟内有效。` : "尚未绑定找回邮箱。请在 OpenWrt 后台查看 /etc/alarm-push-center/web-password。";
    $("#recovery-send").disabled = !info.enabled;
    $("#recovery-reset").disabled = !info.enabled;
  } catch (error) { $("#login-error").textContent = error.message; }
};
$("#recovery-send").onclick = async () => {
  const button = $("#recovery-send"); button.disabled = true;
  try {
    const info = await api("/api/auth/recovery/request", { method: "POST" });
    recoveryRequestId = info.request_id;
    $("#recovery-hint").textContent = info.message;
    $("#recovery-error").textContent = "";
  } catch (error) { $("#recovery-error").textContent = error.message; }
  finally { setTimeout(() => { button.disabled = false; }, 60000); }
};
$("#recovery-reset").onclick = async () => {
  if ($("#recovery-password").value !== $("#recovery-confirm").value) {
    $("#recovery-error").textContent = "两次输入的新密码不一致。";
    return;
  }
  const button = $("#recovery-reset"); button.disabled = true;
  try {
    const info = await api("/api/auth/recovery/reset", { method: "POST", body: JSON.stringify({ request_id: recoveryRequestId, code: $("#recovery-code").value, new_password: $("#recovery-password").value }) });
    $("#recovery-panel").classList.add("hidden"); $("#recovery-password").value = ""; $("#recovery-confirm").value = ""; $("#recovery-code").value = "";
    $("#login-error").textContent = info.message;
  } catch (error) { $("#recovery-error").textContent = error.message; }
  finally { button.disabled = false; }
};
