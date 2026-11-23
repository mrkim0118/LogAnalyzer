using System;
using System.Drawing;
using System.Windows.Forms;

namespace LogAnalyzer
{
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox m_txtFiles = new TextBox();
        private readonly ComboBox m_cmbEncoding = new ComboBox();
        private readonly TextBox m_txtTimeRegex = new TextBox();
        private readonly TextBox m_txtTimeFormats = new TextBox();
        private readonly TextBox m_txtProcessRegex = new TextBox();
        private readonly CheckBox m_chkFolder = new CheckBox();
        private readonly DateTimePicker m_dateBase = new DateTimePicker();
        public LogOptions Options { get; private set; }

        // 샘플에 종속되지 않는 파일, 인코딩, 날짜와 프로세스 규칙을 편집한다.
        public SettingsForm(LogOptions options, bool dark)
        {
            Text = "로그 형식"; Font = new Font("맑은 고딕", 9F);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(850, 495); MinimumSize = new Size(760, 500);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int index = 0; index < 9; index++) { layout.RowStyles.Add(new RowStyle(SizeType.Absolute, index == 3 ? 115 : 41)); }
            Controls.Add(layout);
            m_txtFiles.Text = options.FilePatterns;
            m_cmbEncoding.Items.AddRange(new object[] { "auto", "utf-8", "949", "utf-16", "utf-16BE", "us-ascii", "1252" });
            m_cmbEncoding.Text = options.EncodingName;
            m_txtTimeRegex.Text = options.TimestampPattern;
            m_txtTimeFormats.Multiline = true; m_txtTimeFormats.ScrollBars = ScrollBars.Vertical;
            m_txtTimeFormats.Text = options.TimestampFormats;
            m_txtProcessRegex.Text = options.ProcessPattern;
            m_chkFolder.Text = "상대 폴더 경로 기준"; m_chkFolder.Checked = options.GroupByFolder;
            m_dateBase.Format = DateTimePickerFormat.Custom; m_dateBase.CustomFormat = "yyyy-MM-dd"; m_dateBase.Value = options.BaseDate;
            AddField(layout, 0, "파일 패턴 (; 구분)", m_txtFiles);
            AddField(layout, 1, "인코딩", m_cmbEncoding);
            AddField(layout, 2, "시간 정규식 (?<time>)", m_txtTimeRegex);
            AddField(layout, 3, "시간 형식 (한 줄에 하나)", m_txtTimeFormats);
            AddField(layout, 4, "날짜 없는 로그 기준일", m_dateBase);
            AddField(layout, 5, "프로세스 구분", m_chkFolder);
            AddField(layout, 6, "파일명 정규식 (?<process>)", m_txtProcessRegex);
            m_txtProcessRegex.Enabled = !m_chkFolder.Checked;
            m_chkFolder.CheckedChanged += delegate { m_txtProcessRegex.Enabled = !m_chkFolder.Checked; };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button cancel = MainForm.MakeButton("취소", 90); cancel.DialogResult = DialogResult.Cancel;
            Button apply = MainForm.MakeButton("적용", 90); apply.Tag = "accent";
            Button reset = MainForm.MakeButton("기본값", 90);
            buttons.Controls.AddRange(new Control[] { cancel, apply, reset });
            layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 2);
            CancelButton = cancel; AcceptButton = apply;
            reset.Click += delegate
            {
                LogOptions defaults = new LogOptions();
                m_txtFiles.Text = defaults.FilePatterns; m_cmbEncoding.Text = defaults.EncodingName;
                m_txtTimeRegex.Clear(); m_txtTimeFormats.Clear(); m_txtProcessRegex.Text = defaults.ProcessPattern; m_chkFolder.Checked = false;
            };
            apply.Click += delegate { ApplyOptions(); };
            Theme.Apply(this, dark);
        }

        // 라벨과 편집 항목을 한 행에 정렬한다.
        private static void AddField(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 6, 0, 6);
            layout.Controls.Add(control, 1, row);
        }

        // 검증이 성공하기 전에는 호출자의 설정 객체를 변경하지 않는다.
        private void ApplyOptions()
        {
            LogOptions options = new LogOptions { FilePatterns = m_txtFiles.Text.Trim(), EncodingName = m_cmbEncoding.Text.Trim(), TimestampPattern = m_txtTimeRegex.Text,
                TimestampFormats = m_txtTimeFormats.Text, ProcessPattern = m_txtProcessRegex.Text, GroupByFolder = m_chkFolder.Checked, BaseDate = m_dateBase.Value.Date };
            try { LogReader.ValidateOptions(options); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "로그 형식", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Options = options; DialogResult = DialogResult.OK; Close();
        }
    }
}
