namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly TableLayoutPanel _sourceParameterHost = new() { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top };

    // The existing grid remains the configuration model, so imports and JSON round trips keep
    // their original conversion rules. Operators edit ordinary, labelled fields instead of cells.
    private void RefreshSourceParameterEditor()
    {
        _sourceParameterHost.SuspendLayout();
        foreach (Control control in _sourceParameterHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        _sourceParameterHost.Controls.Clear();
        _sourceParameterHost.RowStyles.Clear();
        _sourceParameterHost.RowCount = 0;
        var rows = _sourceParams.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToList();
        var advancedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "username", "password", "publishing_interval_ms", "sampling_interval_ms",
            "event_queue_size", "max_alarm_count", "inactive_code", "security_policy", "security_mode"
        };
        string[] connectionOrder = ["endpoint", "host", "port", "active_count_node_id", "alarm_code_list_node_id"];
        var common = rows.Where(row => !advancedKeys.Contains(row.Tag as string ?? ""))
            .OrderBy(row => { var index = Array.IndexOf(connectionOrder, row.Tag as string ?? ""); return index < 0 ? int.MaxValue : index; }).ToList();
        var advanced = rows.Except(common).ToList();
        AddBlock(_sourceParameterHost, ParameterFields("连接与节点", "直接填写地址和节点，长文本可完整编辑。", common));
        if (advanced.Count > 0)
        {
            var toggle = new CheckBox { Text = "展开认证与高级参数", AutoSize = true, Height = 32,
                Margin = new Padding(4, 0, 0, 8), ForeColor = VisualTheme.Muted, Cursor = Cursors.Hand };
            AddBlock(_sourceParameterHost, toggle);
            var fields = ParameterFields("认证与采集调优", "可选；不确定时保留当前值。", advanced);
            AddBlock(_sourceParameterHost, fields); fields.Visible = false;
            toggle.CheckedChanged += (_, _) =>
            {
                fields.Visible = toggle.Checked;
                toggle.Text = toggle.Checked ? "收起认证与高级参数" : "展开认证与高级参数";
            };
        }
        _sourceParameterHost.ResumeLayout(true);
    }

    private static SurfacePanel ParameterFields(string title, string description, List<DataGridViewRow> rows)
    {
        var card = Surface(title, description, 76 + rows.Count * 78);
        var inputs = new List<Control>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = row.Tag as string ?? "";
            var label = new Label { Text = Convert.ToString(row.Cells[0].Value), AutoSize = true,
                Location = new Point(24, 76 + i * 78), ForeColor = VisualTheme.Ink,
                Font = new Font("Microsoft YaHei UI", 9) };
            var input = Input(key.Contains("password", StringComparison.OrdinalIgnoreCase));
            input.Name = "source-param-" + key;
            input.AccessibleName = label.Text;
            input.Text = Convert.ToString(row.Cells[1].Value) ?? "";
            input.TextChanged += (_, _) => row.Cells[1].Value = input.Text;
            var shell = new InputShell(input) { Location = new Point(24, 100 + i * 78), Width = 600 };
            card.Controls.AddRange([label, shell]); inputs.Add(shell);
        }
        card.Resize += (_, _) => { foreach (var input in inputs) input.Width = Math.Max(140, card.ClientSize.Width - 48); };
        return card;
    }
}
