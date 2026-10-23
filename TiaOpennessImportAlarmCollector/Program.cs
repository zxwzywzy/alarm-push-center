using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Blocks;

namespace TiaOpennessImportAlarmCollector;

public static class Program
{
    private sealed class ExportRow
    {
        public string Level = "warning";
        public string AlarmId = "";
        public string InstanceName = "";
        public string DetailText = "";
        public string FbName = "";
        public string FbNumber = "";
        public string PlcName = "";
        public string ProjectName = "";
        public string Identification = "";
        public string Priority = "";
        public string SourceColumns = "";
        public string TextListName = "";
        public string TextListIndex = "";
    }

    [STAThread]
    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var values = ParseArgs(args);
            if (values.ContainsKey("check"))
            {
                Console.WriteLine("READY|TIA Portal Openness V21|ExportProDIAGInfo");
                return 0;
            }

            var useRunningProject = values.ContainsKey("running");
            values.TryGetValue("project", out var projectPath);
            if (!useRunningProject && string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("缺少 --project 参数。");
            if (!values.TryGetValue("output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("缺少 --output 参数。");

            outputPath = Path.GetFullPath(outputPath);
            if (!useRunningProject)
            {
                projectPath = Path.GetFullPath(projectPath!);
                if (!File.Exists(projectPath)) throw new FileNotFoundException("找不到 TIA 项目或归档文件。", projectPath);
                if (!projectPath.EndsWith(".ap21", StringComparison.OrdinalIgnoreCase) &&
                    !projectPath.EndsWith(".zap21", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前扫描器使用本机 TIA Portal V21，请选择 .ap21 或 .zap21 文件。");
            }

            var outputFolder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputFolder)) Directory.CreateDirectory(outputFolder);

            var tempRoot = Path.Combine(Path.GetTempPath(), "AlarmPushCenter-Tia-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                Console.WriteLine(useRunningProject ? "PROGRESS|正在连接当前已打开的 TIA Portal V21 项目…" : "PROGRESS|正在启动 TIA Portal Openness V21…");
                var closeProject = !useRunningProject;
                TiaPortal tia;
                if (useRunningProject)
                {
                    var process = TiaPortal.GetProcesses().FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ProjectPath?.FullName));
                    if (process is null) throw new InvalidOperationException("没有找到已打开项目的 TIA Portal V21。请先在 TIA 中打开项目，再重试。");
                    tia = process.Attach();
                }
                else tia = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                using (tia)
                {
                    Project project;
                    if (useRunningProject)
                    {
                        project = tia.Projects.FirstOrDefault() ?? throw new InvalidOperationException("已连接 TIA Portal，但没有找到打开的项目。");
                    }
                    else if (projectPath!.EndsWith(".zap21", StringComparison.OrdinalIgnoreCase))
                    {
                        var retrieveFolder = new DirectoryInfo(Path.Combine(tempRoot, "project"));
                        retrieveFolder.Create();
                        Console.WriteLine("PROGRESS|正在解压并打开 TIA 项目归档…");
                        project = tia.Projects.Retrieve(new FileInfo(projectPath), retrieveFolder);
                    }
                    else
                    {
                        Console.WriteLine("PROGRESS|正在以只读扫描方式打开 TIA 项目…");
                        project = tia.Projects.Open(new FileInfo(projectPath));
                    }

                    var rows = new List<ExportRow>();
                    var textListFiles = new List<string>();
                    var proDiagBlocks = 0;
                    var skippedBlocks = 0;
                    foreach (var device in ProjectDevices(project))
                    {
                        foreach (var item in Descendants(device.DeviceItems))
                        {
                            var container = item.GetService<SoftwareContainer>();
                            if (!(container?.Software is PlcSoftware plc)) continue;
                            Console.WriteLine("PROGRESS|正在扫描 PLC：" + Safe(plc.Name));
                            TryExportAlarmTextLists(plc, outputPath, textListFiles);
                            ScanGroup(project.Name, plc.Name, plc.BlockGroup, tempRoot, rows, ref proDiagBlocks, ref skippedBlocks);
                        }
                    }

                    var exportedRows = WriteCsv(outputPath, rows);
                    if (closeProject) project.Close();
                    Console.WriteLine("RESULT|" + exportedRows.ToString(CultureInfo.InvariantCulture) + "|" +
                                      proDiagBlocks.ToString(CultureInfo.InvariantCulture) + "|" +
                                      skippedBlocks.ToString(CultureInfo.InvariantCulture) + "|" + outputPath + "|" +
                                      textListFiles.Count.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(";", textListFiles));
                }
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR|" + Flatten(ex));
            return 1;
        }
    }

    private static void TryExportAlarmTextLists(PlcSoftware plc, string alarmCsvPath, List<string> exportedFiles)
    {
        try
        {
            Console.WriteLine("PROGRESS|导出 PLC 报警文本列表：" + Safe(plc.Name));
            var baseName = Path.GetFileNameWithoutExtension(alarmCsvPath);
            var safePlcName = string.Concat(plc.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(Path.GetDirectoryName(alarmCsvPath) ?? Environment.CurrentDirectory,
                baseName + "_报警文本列表_" + safePlcName + ".xlsx");
            var provider = plc.GetService<PlcAlarmTextListProvider>();
            var result = provider.ExportToXlsx(new FileInfo(path));
            if (result.State == TextListXlsxResultState.Error)
                throw new InvalidOperationException("TIA 返回文本列表导出失败：" + result.LogFilePath?.FullName);
            var actualPath = File.Exists(path) ? path : Path.ChangeExtension(path, ".xlsm");
            if (!File.Exists(actualPath)) throw new FileNotFoundException("TIA 未生成报警文本列表文件。", path);
            var csvPath = Path.ChangeExtension(actualPath, ".csv");
            var count = WriteTextListCsv(actualPath, csvPath, plc.Name);
            exportedFiles.Add(csvPath);
            Console.WriteLine("PROGRESS|已导出 " + count.ToString(CultureInfo.InvariantCulture) + " 条报警文本列表内容：" + csvPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROGRESS|未导出 PLC 报警文本列表 [" + Safe(plc.Name) + "]：" + Safe(ex.GetBaseException().Message));
        }
    }

    private static int WriteTextListCsv(string workbookPath, string csvPath, string plcName)
    {
        using (var archive = ZipFile.OpenRead(workbookPath))
        {
            var shared = ReadSharedStrings(archive);
            var sheet = archive.GetEntry("xl/worksheets/sheet2.xml") ?? throw new InvalidDataException("报警文本列表文件缺少 TextListEntry 工作表。");
            List<List<string>> rows;
            using (var stream = sheet.Open()) rows = ReadSheetRows(XDocument.Load(stream), shared);
            if (rows.Count == 0) throw new InvalidDataException("报警文本列表工作表为空。");
            var headers = rows[0];
            Func<string, int> column = name => headers.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            var parent = column("Parent");
            var from = column("From");
            var to = column("To");
            var zh = column("Text [zh-CN]");
            var en = column("Text [en-US]");
            var de = column("Text [de-DE]");
            if (parent < 0 || from < 0 || to < 0) throw new InvalidDataException("报警文本列表工作表列结构无法识别。");
            Func<List<string>, int, string> cell = (row, index) => index >= 0 && index < row.Count ? row[index].Trim() : "";
            Func<string, bool> hasText = value => !string.IsNullOrWhiteSpace(value) && !value.Equals("<No value>", StringComparison.OrdinalIgnoreCase);
            var output = new List<string> { "PLC名称,文本列表,起始值,结束值,中文文本,英文文本,德文文本" };
            var count = 0;
            foreach (var row in rows.Skip(1))
            {
                var listName = cell(row, parent);
                var fromValue = cell(row, from);
                var toValue = cell(row, to);
                var zhText = cell(row, zh);
                var enText = cell(row, en);
                var deText = cell(row, de);
                if (string.IsNullOrWhiteSpace(listName) || string.IsNullOrWhiteSpace(fromValue)) continue;
                var preferred = hasText(zhText) ? zhText : hasText(enText) ? enText : hasText(deText) ? deText : "";
                if (!hasText(preferred)) continue;
                output.Add(string.Join(",", new[] { plcName, listName, fromValue, toValue, preferred, enText, deText }.Select(Csv)));
                count++;
            }
            File.WriteAllLines(csvPath, output, new UTF8Encoding(true));
            return count;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        using (var stream = entry.Open())
        {
            var document = XDocument.Load(stream);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            return document.Descendants(ns + "si").Select(item => string.Concat(item.Descendants(ns + "t").Select(x => x.Value))).ToList();
        }
    }

    private static List<List<string>> ReadSheetRows(XDocument document, IReadOnlyList<string> shared)
    {
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var result = new List<List<string>>();
        foreach (var rowElement in document.Descendants(ns + "row"))
        {
            var row = new List<string>();
            foreach (var cell in rowElement.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? "A1";
                var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
                var column = 0;
                foreach (var letter in letters) column = column * 26 + (char.ToUpperInvariant(letter) - 'A' + 1);
                while (row.Count < Math.Max(0, column - 1)) row.Add("");
                var raw = cell.Element(ns + "v")?.Value ?? string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                if (((string?)cell.Attribute("t")) == "s" && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count)
                    raw = shared[sharedIndex];
                row.Add(raw);
            }
            result.Add(row);
        }
        return result;
    }

    private static void ScanGroup(string projectName, string plcName, PlcBlockGroup group, string tempRoot,
        List<ExportRow> rows, ref int proDiagBlocks, ref int skippedBlocks)
    {
        foreach (var block in group.Blocks.OfType<CodeBlock>())
        {
            if (block.ProgrammingLanguage != ProgrammingLanguage.ProDiag) continue;
            proDiagBlocks++;
            if (!block.IsConsistent)
            {
                skippedBlocks++;
                Console.WriteLine("PROGRESS|跳过不一致的 ProDiag FB：" + Safe(block.Name));
                continue;
            }

            var exportDirectory = new DirectoryInfo(Path.Combine(tempRoot, "export-" + Guid.NewGuid().ToString("N")));
            exportDirectory.Create();
            Console.WriteLine("PROGRESS|导出 ProDiag FB：" + Safe(block.Name));
            try { block.ExportProDIAGInfo(exportDirectory); }
            catch (Exception ex)
            {
                skippedBlocks++;
                Console.WriteLine("PROGRESS|跳过无法导出的 ProDiag FB [" + Safe(block.Name) + "]：" + Safe(ex.GetBaseException().Message));
                continue;
            }
            foreach (var file in exportDirectory.GetFiles("*", SearchOption.AllDirectories))
                rows.AddRange(ReadProDiagFile(file.FullName, projectName, plcName, block.Name, block.Number));
        }
        foreach (var child in group.Groups) ScanGroup(projectName, plcName, child, tempRoot, rows, ref proDiagBlocks, ref skippedBlocks);
    }

    private static IEnumerable<DeviceItem> Descendants(DeviceItemComposition roots)
    {
        foreach (var item in roots)
        {
            yield return item;
            foreach (var child in Descendants(item.DeviceItems)) yield return child;
        }
    }

    private static IEnumerable<Device> ProjectDevices(Project project)
    {
        var seen = new HashSet<Device>();
        foreach (var device in project.Devices) if (seen.Add(device)) yield return device;
        foreach (var device in project.UngroupedDevicesGroup.Devices) if (seen.Add(device)) yield return device;
        foreach (var group in project.DeviceGroups)
            foreach (var device in GroupDevices(group)) if (seen.Add(device)) yield return device;
    }

    private static IEnumerable<Device> GroupDevices(DeviceUserGroup group)
    {
        foreach (var device in group.Devices) yield return device;
        foreach (var child in group.Groups)
            foreach (var device in GroupDevices(child)) yield return device;
    }

    private static IEnumerable<ExportRow> ReadProDiagFile(string path, string projectName, string plcName, string fbName, int fbNumber)
    {
        var lines = ReadAllLines(path);
        if (lines.Length == 0) yield break;
        var delimiter = DetectDelimiter(lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "");
        var parsed = lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Split(x, delimiter)).ToList();
        if (parsed.Count == 0) yield break;
        var headers = parsed[0];
        var hasHeader = headers.Any(x => x.IndexOf("alarm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         x.IndexOf("ident", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         x.Contains("报警") || x.Contains("标识") || x.Contains("文本") || x.Contains("优先"));
        var start = hasHeader ? 1 : 0;
        for (var index = start; index < parsed.Count; index++)
        {
            var cells = parsed[index];
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;
            var identification = cells.ElementAtOrDefault(0)?.Trim() ?? "";
            var priority = cells.ElementAtOrDefault(1)?.Trim() ?? "";
            var instance = FindValue(headers, cells, "instance", "instanz", "实例", "object", "对象", "operand", "操作数");
            var alarmId = FindValue(headers, cells, "alarmid", "alarm id", "报警id", "报警编号", "client alarm");
            if (string.IsNullOrWhiteSpace(alarmId)) alarmId = ExtractAlarmId(identification);
            var details = SelectDetails(headers, cells, hasHeader);
            if (string.IsNullOrWhiteSpace(details)) details = string.Join("；", cells.Skip(2).Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(instance)) instance = ExtractInstanceName(details, fbName);
            if (string.IsNullOrWhiteSpace(alarmId) || string.IsNullOrWhiteSpace(details)) continue;
            yield return new ExportRow
            {
                Level = PriorityToLevel(priority), AlarmId = alarmId, InstanceName = instance, DetailText = details,
                FbName = fbName, FbNumber = fbNumber.ToString(CultureInfo.InvariantCulture), PlcName = plcName, ProjectName = projectName, Identification = identification,
                Priority = priority, SourceColumns = BuildSourceColumns(headers, cells, hasHeader)
            };
        }
    }

    private static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string content;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            content = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { content = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { content = Encoding.Default.GetString(bytes); }
        }
        return content.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None);
    }

    private static char DetectDelimiter(string line)
    {
        var candidates = new[] { ';', '\t', ',' };
        return candidates.OrderByDescending(x => line.Count(c => c == x)).First();
    }

    private static List<string> Split(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted) { result.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString().Trim());
        return result;
    }

    private static string FindValue(IReadOnlyList<string> headers, IReadOnlyList<string> cells, params string[] names)
    {
        for (var i = 0; i < headers.Count && i < cells.Count; i++)
        {
            var header = headers[i].Replace("_", " ").Trim();
            if (names.Any(name => header.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) return cells[i].Trim();
        }
        return "";
    }

    private static string SelectDetails(IReadOnlyList<string> headers, IReadOnlyList<string> cells, bool hasHeader)
    {
        if (!hasHeader) return cells.Count > 2 ? cells[2].Trim() : "";
        for (var i = 0; i < headers.Count && i < cells.Count; i++)
        {
            var header = headers[i].Replace("_", " ").Trim().ToLowerInvariant();
            var isAlarmText = header == "alarm text" || header == "alarmtext" || header.Contains("报警文本") ||
                              header.Contains("报警消息") || header.Contains("specific text") || header.Contains("详细文本");
            if (isAlarmText && !string.IsNullOrWhiteSpace(cells[i])) return cells[i].Trim();
        }
        for (var i = 0; i < headers.Count && i < cells.Count; i++)
        {
            var header = headers[i].ToLowerInvariant();
            if ((header.Contains("message") || header.Contains("description") || header.Contains("描述")) &&
                !header.Contains("information") && !string.IsNullOrWhiteSpace(cells[i])) return cells[i].Trim();
        }
        return "";
    }

    private static string ExtractInstanceName(string details, string fallback)
    {
        var separator = details.IndexOf(" : ", StringComparison.Ordinal);
        if (separator <= 0) return fallback;
        var candidate = details.Substring(0, separator).Trim();
        return candidate.Length is > 0 and <= 160 && !candidate.Contains("@") ? candidate : fallback;
    }

    private static string BuildSourceColumns(IReadOnlyList<string> headers, IReadOnlyList<string> cells, bool hasHeader)
    {
        var pairs = new List<string>();
        for (var i = 0; i < cells.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(cells[i])) continue;
            pairs.Add((hasHeader && i < headers.Count ? headers[i] : "列" + (i + 1)) + "=" + cells[i]);
        }
        return string.Join(" | ", pairs);
    }

    private static string ExtractAlarmId(string identification)
    {
        // 西门子第一列是由 Client Alarm ID 与 Supervision ID 组成的完整事件标识。
        // OPC UA ProDiag 事件中的 ID 使用同一标识，必须原样保留才能精确匹配。
        return identification?.Trim() ?? "";
    }

    private static string PriorityToLevel(string priority)
    {
        if (!int.TryParse(priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return "warning";
        if (value <= 3) return "critical";
        if (value <= 7) return "warning";
        return "info";
    }

    private static int WriteCsv(string path, IEnumerable<ExportRow> rows)
    {
        var selected = rows
            .GroupBy(x => x.PlcName + "\u001f" + x.FbNumber + "\u001f" + x.AlarmId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(TextScore).ThenByDescending(x => x.DetailText.Length).First())
            .OrderBy(x => x.PlcName).ThenBy(x => x.FbNumber).ThenBy(x => x.AlarmId).ToList();
        foreach (var group in selected.Select(row => new { Row = row, List = ExtractTextListName(row.DetailText) })
                     .Where(item => item.List.Length > 0)
                     .GroupBy(item => item.Row.PlcName + "\u001f" + item.Row.FbNumber + "\u001f" + item.Row.InstanceName + "\u001f" + item.List,
                         StringComparer.Ordinal))
        {
            var index = 1;
            foreach (var item in group.OrderBy(item => item.Row.AlarmId, StringComparer.OrdinalIgnoreCase))
            {
                item.Row.TextListName = item.List;
                item.Row.TextListIndex = index.ToString(CultureInfo.InvariantCulture);
                index++;
            }
        }
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("报警级别,AlarmId,实例名,详细文本,FB名称,FB编号,文本列表,文本索引,PLC名称,项目名称,ProDiag标识,优先级,原始字段");
            foreach (var row in selected)
            {
                writer.WriteLine(string.Join(",", new[] { row.Level, row.AlarmId, row.InstanceName, row.DetailText, row.FbName,
                    row.FbNumber, row.TextListName, row.TextListIndex, row.PlcName, row.ProjectName, row.Identification, row.Priority, row.SourceColumns }.Select(Csv)));
            }
        }
        return selected.Count;
    }

    private static int TextScore(ExportRow row)
    {
        var text = row.DetailText ?? "";
        var score = text.Count(c => c >= '\u3400' && c <= '\u9fff') * 10;
        if (text.Contains("�")) score -= 10000;
        if (text.IndexOf("%t#", StringComparison.OrdinalIgnoreCase) >= 0) score += 60;
        if (text.Contains("@5%s@") && text.Contains("@6%t#")) score -= 80;
        if (text.IndexOf("Category ", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Subcategory ", StringComparison.OrdinalIgnoreCase) >= 0) score -= 20;
        if (text.Contains(":")) score += 5;
        return score;
    }

    private static string ExtractTextListName(string text)
    {
        var match = Regex.Match(text ?? "", "@[1-9][0-9]?%t#(?<name>[^@]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value.Trim() : "";
    }

    private static string Csv(string value) => "\"" + (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"") + "\"";
    private static string Safe(string value) => (value ?? "").Replace("|", "／").Replace("\r", " ").Replace("\n", " ");
    private static string Flatten(Exception ex)
    {
        var values = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
            if (!string.IsNullOrWhiteSpace(current.Message)) values.Add(current.Message.Replace("\r", " ").Replace("\n", " "));
        return string.Join(" → ", values.Distinct());
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i].Substring(2);
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            result[key] = value;
        }
        return result;
    }
}
