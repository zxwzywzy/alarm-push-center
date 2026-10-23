const { test } = require("node:test");
const assert = require("node:assert/strict");
const ui = require("../wwwroot/ui-core.js");
test("A 10,000-row dictionary renders only one bounded page, searches all records and keeps original edit indices", () => {
  const rows = Array.from({ length: 10000 }, (_, i) => ({
    code: String(i + 1),
    text: "电机报警",
    instance_name: "Motor",
  }));
  const page = ui.dictionaryPage(rows, "", 999);
  assert.equal(page.items.length, 50);
  assert.equal(page.pages, 200);
  assert.equal(page.page, 200);
  assert.equal(page.items[0].i, 9950);
  const found = ui.dictionaryPage(rows, "10000 MOTOR", 1);
  assert.equal(found.matches, 1);
  assert.equal(found.items[0].i, 9999);
  assert.equal(found.total, 10000);
  assert.equal(ui.dictionaryPage(rows, "不存在", 10).page, 1);
  const draft = { code: "", text: "" },
    drafts = new WeakSet([draft]);
  rows.push(draft);
  assert.equal(ui.dictionaryPage(rows, "", 201, drafts).items[0].a, draft);
});

test("CSV handles BOM, multiline Chinese text, commas and escaped quotes", () => {
  const imported = ui.importDictionary(
    '\ufeffAlarmId,详细文本,实例名,FB编号,报警级别\r\n1001,"电机,温度高\r\n检查""反馈""",Motor,901,警告\r\n',
  );
  assert.equal(imported.rows[0].text, '电机,温度高\r\n检查"反馈"');
  assert.equal(imported.rows[0].fb_number, "901");
  assert.equal(imported.rows[0].category, "warning");
});
test("TSV preserves ProDiag text-list references", () => {
  const row = ui.importDictionary(
    "报警码\t报警文本\t文本列表名称\t文本列表索引\n42\t阀门报警\tAreaGeneral\t8",
  ).rows[0];
  assert.equal(row.text_list_name, "AreaGeneral");
  assert.equal(row.text_list_index, "8");
});
test("PLC text-list import preserves ranges and PLC identity", () => {
  const imported = ui.importDictionary(
    "PLC名称,文本列表,起始值,结束值,中文文本\nPLC1,AreaGeneral,9,12,气压异常",
  );
  assert.equal(imported.kind, "alarm_text_lists");
  assert.deepEqual(imported.rows[0], {
    plc_name: "PLC1",
    list_name: "AreaGeneral",
    from: "9",
    to: "12",
    text: "气压异常",
  });
});
test("Missing required columns and malformed quotes fail without partial import", () => {
  assert.throws(() => ui.importDictionary("code,extra\n1,hello"), /未找到/);
  assert.throws(() => ui.importDictionary('code,text\n1,"未结束'), /引号/);
  assert.throws(() => ui.importDictionary("code,text\n,空行"), /有效记录/);
});
test("Empty-code rows are counted as skipped", () => {
  const result = ui.importDictionary("code,text\n,空码\n 12 ,正常");
  assert.equal(result.skipped, 1);
  assert.equal(result.rows[0].code, "12");
});
test("Search matches all terms across fields, ignores case and treats syntax literally", () => {
  assert.equal(ui.matches("motor 温度", "Motor1", "电机温度高"), true);
  assert.equal(ui.matches("motor 电压", "Motor1", "电机温度高"), false);
  assert.equal(ui.matches("[8]", "Alarm[8]"), true);
  assert.equal(ui.matches("", "任何文本"), true);
});
test("Config preview masks credentials recursively without changing saved data", () => {
  const config = {
    targets: [
      {
        token: "secret",
        recipients: ["123"],
        webhook_url: "https://host/secret",
        params: { bot_client_secret: "secret", command_defs: "[]" },
      },
    ],
    sources: [
      { params: { endpoint: "opc.tcp://host:4840", password: "secret" } },
    ],
  };
  const masked = ui.redact(config);
  assert.equal(masked.targets[0].token, "••••••••");
  assert.equal(masked.targets[0].params.command_defs, "[]");
  assert.equal(masked.sources[0].params.password, "••••••••");
  assert.equal(config.sources[0].params.password, "secret");
});
