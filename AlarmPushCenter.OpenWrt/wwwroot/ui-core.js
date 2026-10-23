/* Shared, dependency-free helpers for the router console. */
(function (root) {
  const levels = {
    info: "提示",
    warning: "警告",
    critical: "严重",
    emergency: "紧急",
  };
  function severity(value) {
    const v = String(value || "").trim();
    return (
      { 提示: "info", 警告: "warning", 严重: "critical", 紧急: "emergency" }[
        v
      ] ||
      v ||
      "warning"
    );
  }
  function parseDelimited(text) {
    text = String(text).replace(/^\ufeff/, "");
    const delimiter = text.split(/\r?\n/, 1)[0].includes("\t") ? "\t" : ",";
    const rows = [];
    let row = [],
      value = "",
      quoted = false;
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (c === '"') {
        if (quoted && text[i + 1] === '"') {
          value += '"';
          i++;
        } else quoted = !quoted;
      } else if (c === delimiter && !quoted) {
        row.push(value);
        value = "";
      } else if ((c === "\n" || c === "\r") && !quoted) {
        if (c === "\r" && text[i + 1] === "\n") i++;
        row.push(value);
        if (row.some((x) => x.trim())) rows.push(row);
        row = [];
        value = "";
      } else value += c;
    }
    if (quoted) throw Error("CSV 引号未闭合，请检查文件格式。");
    row.push(value);
    if (row.some((x) => x.trim())) rows.push(row);
    return rows;
  }
  function importDictionary(text) {
    const table = parseDelimited(text),
      headers = (table.shift() || []).map((x) => x.trim().toLowerCase());
    const column = (...names) =>
      headers.findIndex((h) => names.map((x) => x.toLowerCase()).includes(h));
    const ci = column("报警码", "报警编号", "AlarmId", "code"),
      ti = column("详细文本", "详细报警文本", "报警文本", "text", "中文文本");
    const li = column("文本列表", "文本列表名称", "list_name"),
      fi = column("起始值", "from"),
      ei = column("结束值", "to");
    const lists = ci < 0 && li >= 0 && fi >= 0;
    if (ti < 0 || (!lists && ci < 0))
      throw Error(
        "未找到报警码和报警文本列。支持 AlarmId、详细文本，或文本列表、起始值、中文文本。",
      );
    const get = (row, ...names) => {
      const index = column(...names);
      return index < 0 ? "" : row[index] || "";
    };
    let skipped = 0;
    const rows = [];
    for (const row of table) {
      if (lists) {
        if (!row[li]?.trim() || !row[fi]?.trim()) {
          skipped++;
          continue;
        }
        rows.push({
          plc_name: get(row, "PLC名称", "plc_name"),
          list_name: row[li].trim(),
          from: row[fi].trim(),
          to: ei < 0 ? row[fi].trim() : (row[ei] || row[fi]).trim(),
          text: row[ti] || "",
        });
      } else {
        if (!row[ci]?.trim()) {
          skipped++;
          continue;
        }
        rows.push({
          category: severity(get(row, "报警级别", "category", "severity")),
          code: row[ci].trim(),
          instance_name: get(row, "实例名", "instance_name"),
          fb_number: get(row, "FB编号", "fb_number"),
          text_list_name: get(row, "文本列表名称", "text_list_name"),
          text_list_index: get(row, "文本列表索引", "text_list_index"),
          text: row[ti] || "",
        });
      }
    }
    if (!rows.length) throw Error("文件中没有可导入的有效记录。");
    return { kind: lists ? "alarm_text_lists" : "alarm_texts", rows, skipped };
  }
  function matches(query, ...values) {
    const terms = String(query)
        .trim()
        .toLocaleLowerCase()
        .split(/\s+/)
        .filter(Boolean),
      text = values.flat().join(" ").toLocaleLowerCase();
    return terms.every((term) => text.includes(term));
  }
  function dictionaryPage(rows, query, page = 1, drafts = new WeakSet()) {
    const populated = rows
      .map((a, i) => ({ a, i }))
      .filter(
        ({ a }) =>
          a.code || a.text || a.instance_name || a.fb_number || drafts.has(a),
      );
    const filtered = populated.filter(({ a }) =>
      matches(
        query,
        a.code,
        a.text,
        a.instance_name,
        a.fb_number,
        a.text_list_name,
        a.text_list_index,
      ),
    );
    const pages = Math.max(1, Math.ceil(filtered.length / 50));
    page = Math.max(1, Math.min(page, pages));
    return {
      page,
      pages,
      total: populated.length,
      matches: filtered.length,
      filtered,
      items: filtered.slice((page - 1) * 50, page * 50),
    };
  }
  function redact(value, key = "") {
    if (
      /password|secret|token|webhook_url|admin_ids|recipients|conversation_id|^number$/i.test(
        key,
      )
    )
      return value ? "••••••••" : value;
    if (Array.isArray(value)) return value.map((v) => redact(v));
    if (value && typeof value === "object")
      return Object.fromEntries(
        Object.entries(value).map(([k, v]) => [k, redact(v, k)]),
      );
    return value;
  }
  const api = {
    levels,
    severity,
    parseDelimited,
    importDictionary,
    matches,
    dictionaryPage,
    redact,
  };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.AlarmUI = api;
})(typeof window === "undefined" ? globalThis : window);
