using System.Diagnostics;
using System.Text;

namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private async Task ImportTiaProDiagAsync(Button trigger, bool useRunningProject)
    {
        using var projectDialog = new OpenFileDialog
        {
            Title = "选择 TIA Portal V21 项目或归档",
            Filter = "TIA Portal V21 项目 (*.ap21;*.zap21)|*.ap21;*.zap21|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (!useRunningProject && projectDialog.ShowDialog(this) != DialogResult.OK) return;

        using var outputDialog = new SaveFileDialog
        {
            Title = "保存 ProDiag 报警总表",
            Filter = "CSV 表格 (*.csv)|*.csv",
            AddExtension = true,
            DefaultExt = "csv",
            InitialDirectory = useRunningProject ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.GetDirectoryName(projectDialog.FileName),
            FileName = $"ProDiag报警总表_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            OverwritePrompt = true
        };
        if (outputDialog.ShowDialog(this) != DialogResult.OK) return;

        var helper = FindTiaCollector();
        if (helper is null)
        {
            MessageBox.Show(this, "未找到 TIA Openness 扫描器，请重新安装或更新客户端。", "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        trigger.Enabled = false;
        using var progress = new TiaImportProgressDialog();
        progress.Show(this);
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(helper);
            if (useRunningProject) start.ArgumentList.Add("-Running");
            else
            {
                start.ArgumentList.Add("-Project");
                start.ArgumentList.Add(projectDialog.FileName);
            }
            start.ArgumentList.Add("-Output");
            start.ArgumentList.Add(outputDialog.FileName);

            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var output = new StringBuilder();
            var errors = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                output.AppendLine(eventArgs.Data);
                if (eventArgs.Data.StartsWith("PROGRESS|", StringComparison.OrdinalIgnoreCase))
                    progress.SetStatus(eventArgs.Data[9..]);
            };
            process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) errors.AppendLine(eventArgs.Data); };
            if (!process.Start()) throw new InvalidOperationException("TIA Openness 扫描器启动失败。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var detail = errors.ToString().Trim();
                if (detail.StartsWith("ERROR|", StringComparison.OrdinalIgnoreCase)) detail = detail[6..];
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "TIA Openness 扫描失败，请确认 TIA V21 与 Openness 已安装。" : detail);
            }
            if (!File.Exists(outputDialog.FileName)) throw new InvalidOperationException("扫描完成，但没有生成报警总表。");

            var imported = ParseAlarmTextFile(outputDialog.FileName);
            var result = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => x.StartsWith("RESULT|", StringComparison.OrdinalIgnoreCase));
            var resultParts = result?.Split('|') ?? [];
            var textListFiles = resultParts.ElementAtOrDefault(6)?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var textListEntries = textListFiles.Where(File.Exists).SelectMany(ParseAlarmTextListFile)
                .GroupBy(x => $"{x.PlcName}\u001f{x.ListName}\u001f{x.From}\u001f{x.To}", StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
            if (imported.Count == 0 && textListEntries.Count == 0)
            {
                MessageBox.Show(this, "项目扫描完成，但没有找到可导出的 ProDiag 报警。请确认 FB 的编程语言为 ProDiag 且块状态一致。", "TIA ProDiag 导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (imported.Count > 0) MergeAlarmTexts(imported);
            if (textListEntries.Count > 0) _config.Rules[_ruleIndex].AlarmTextLists = textListEntries;
            var blockCount = resultParts.ElementAtOrDefault(2) ?? "-";
            MessageBox.Show(this, $"已扫描 {blockCount} 个 ProDiag FB，导入 {imported.Count} 条报警文本、{textListEntries.Count} 条文本列表内容。\n\n总表：{outputDialog.FileName}\n\n内容已写入当前规则，请点击“保存配置”生效。", "TIA ProDiag 导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TIA ProDiag 导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progress.Close();
            trigger.Enabled = true;
        }
    }

    private static IEnumerable<AlarmTextListEntry> ParseAlarmTextListFile(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) yield break;
        var headers = SplitDelimitedLine(lines[0]);
        int Column(string name) => headers.FindIndex(x => x.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var plc = Column("PLC名称");
        var list = Column("文本列表");
        var from = Column("起始值");
        var to = Column("结束值");
        var text = Column("中文文本");
        if (list < 0 || from < 0 || text < 0) yield break;
        string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : "";
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitDelimitedLine(line);
            var listName = Cell(cells, list);
            var entryText = Cell(cells, text);
            if (listName.Length == 0 || entryText.Length == 0) continue;
            yield return new AlarmTextListEntry
            {
                PlcName = Cell(cells, plc), ListName = listName, From = Cell(cells, from),
                To = Cell(cells, to), Text = entryText
            };
        }
    }

    private static string? FindTiaCollector()
    {
        var name = "AlarmPushCenter.TiaCollector.ps1";
        var deployed = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(deployed)) return deployed;
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TiaOpennessImportAlarmCollector", name));
        return File.Exists(development) ? development : null;
    }
}

internal sealed class TiaImportProgressDialog : Form
{
    private readonly Label _status = new()
    {
        AutoSize = false, Dock = DockStyle.Top, Height = 70, Padding = new Padding(4, 12, 4, 0),
        Text = "正在准备 TIA Openness…", Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
        ForeColor = Color.FromArgb(15, 23, 42), TextAlign = ContentAlignment.MiddleLeft
    };

    public TiaImportProgressDialog()
    {
        Text = "TIA ProDiag 扫描";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 140);
        BackColor = Color.FromArgb(248, 250, 252);
        Padding = new Padding(22, 18, 22, 18);
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 9, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 };
        Controls.Add(_status);
        Controls.Add(bar);
    }

    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status.Text = text;
    }
}
