using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LogAnalyzer
{
    internal sealed class KeywordForm : Form
    {
        private readonly DataGridView m_grid = new DataGridView();
        private readonly DataGridViewCellStyle m_disabledLinkStyle = new DataGridViewCellStyle();
        public List<KeywordRule> Rules { get; private set; }

        // 원본 목록을 복사하여 취소 시 기존 차트 설정을 유지한다.
        public KeywordForm(List<KeywordRule> rules, bool dark)
        {
            Text = "타이밍 키워드"; Font = new Font("맑은 고딕", 9F);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1450, 500); MinimumSize = new Size(1050, 380);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            m_grid.Dock = DockStyle.Fill; m_grid.AllowUserToAddRows = false; m_grid.AllowUserToDeleteRows = false;
            m_grid.AllowUserToResizeRows = false; m_grid.RowHeadersVisible = false; m_grid.RowTemplate.Height = 33;
            m_grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; m_grid.MultiSelect = false;
            m_grid.BorderStyle = BorderStyle.None; m_grid.ColumnHeadersHeight = 34; m_grid.ScrollBars = ScrollBars.Both;
            m_grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "사용", Width = 58 });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "포함 문자열", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
            m_grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Case", HeaderText = "대소문자 구분", Width = 112 });
            m_grid.Columns.Add(new DataGridViewButtonColumn { Name = "Color", HeaderText = "색상", Width = 100, FlatStyle = FlatStyle.Flat });
            m_grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "CheckIntervals", HeaderText = "이상 감지", Width = 85 });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ExpectedInterval", HeaderText = "로그 간격 (ms)", Width = 155 });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tolerance", HeaderText = "로그 간격 허용 편차 (%)", Width = 168 });
            m_grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "LinkedKeywordNumber", HeaderText = "연동할 키워드 순번", Width = 165,
                DisplayMember = "Value", ValueMember = "Key", ValueType = typeof(int), FlatStyle = FlatStyle.Flat,
                ToolTipText = "등록된 순번을 선택합니다. Task/종류와 관계없이 통합 로그에서 현재 키워드 → 선택 키워드를 발생 순서대로 1:1 연결합니다." });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LinkedExpectedInterval", HeaderText = "연동 로그 간격 (ms)", Width = 165, ToolTipText = "0은 완성된 양수 간격 5개 이상의 중앙값으로 자동 계산합니다." });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LinkedTolerance", HeaderText = "연동 로그 간격 허용 편차 (%)", Width = 196 });
            m_grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "순번", Width = 52, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            m_grid.Columns["Number"].DisplayIndex = 0;
            foreach (DataGridViewColumn column in m_grid.Columns) { column.SortMode = DataGridViewColumnSortMode.NotSortable; }
            m_grid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 3) { return; }
                using (ColorDialog dialog = new ColorDialog { Color = (Color)m_grid.Rows[e.RowIndex].Tag, FullOpen = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                    SetRowColor(m_grid.Rows[e.RowIndex], dialog.Color);
                }
            };
            m_grid.CurrentCellDirtyStateChanged += delegate { if (m_grid.IsCurrentCellDirty) { m_grid.CommitEdit(DataGridViewDataErrorContexts.Commit); } };
            layout.Controls.Add(m_grid, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            Button add = MainForm.MakeButton("추가", 76);
            Button remove = MainForm.MakeButton("삭제", 76);
            Button up = MainForm.MakeButton("위로", 68);
            Button down = MainForm.MakeButton("아래로", 68);
            Button confirm = MainForm.MakeButton("적용", 86); confirm.Tag = "accent";
            Button cancel = MainForm.MakeButton("취소", 86); cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.AddRange(new Control[] { add, remove, up, down, confirm, cancel });
            layout.Controls.Add(actions, 0, 1);
            CancelButton = cancel;
            add.Click += delegate
            {
                if (m_grid.Rows.Count >= KeywordSettings.MaximumRules) { return; }
                AddRule(new KeywordRule { Color = Color.FromArgb(32, 155, 190) });
                UpdateKeywordNumbers(null);
                m_grid.CurrentCell = m_grid.Rows[m_grid.Rows.Count - 1].Cells[1]; m_grid.BeginEdit(true);
            };
            remove.Click += delegate { RemoveRule(); };
            up.Click += delegate { MoveRule(-1); };
            down.Click += delegate { MoveRule(1); };
            confirm.Click += delegate { ApplyRules(); };
            Theme.Apply(this, dark);
            Color background = m_grid.ColumnHeadersDefaultCellStyle.BackColor;
            Color foreground = Theme.Mix(background, m_grid.DefaultCellStyle.ForeColor, 0.65);
            m_disabledLinkStyle.BackColor = background; m_disabledLinkStyle.ForeColor = foreground;
            m_disabledLinkStyle.SelectionBackColor = background; m_disabledLinkStyle.SelectionForeColor = foreground;
            m_grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex != m_grid.Columns["LinkedKeywordNumber"].Index) { return; }
                DataGridViewRow row = m_grid.Rows[e.RowIndex];
                object number = row.Cells["LinkedKeywordNumber"].Value;
                // 순번 목록 재구성 중의 임시 null은 건너뛰고 최종 순번만 반영한다.
                if (number == null) { return; }
                bool disabled = Convert.ToInt32(number) == 0;
                SetLinkCellState(row.Cells["LinkedExpectedInterval"], disabled, KeywordRule.MaximumExpectedIntervalMs);
                SetLinkCellState(row.Cells["LinkedTolerance"], disabled, KeywordRule.MaximumTolerancePercent);
            };
            foreach (KeywordRule rule in rules) { AddRule(rule); }
            UpdateKeywordNumbers(null);
        }

        // 키워드와 간격 검사 설정을 그리드의 독립 행으로 복사한다.
        private void AddRule(KeywordRule rule)
        {
            int index = m_grid.Rows.Add(rule.Enabled, rule.Text, rule.CaseSensitive, ColorTranslator.ToHtml(rule.Color), rule.CheckIntervals,
                rule.ExpectedIntervalMs.ToString(CultureInfo.CurrentCulture), rule.TolerancePercent.ToString(CultureInfo.CurrentCulture), rule.LinkedKeywordNumber,
                rule.LinkedExpectedIntervalMs.ToString(CultureInfo.CurrentCulture), rule.LinkedTolerancePercent.ToString(CultureInfo.CurrentCulture), m_grid.Rows.Count + 1);
            m_grid.Rows[index].Cells[7].ToolTipText = m_grid.Columns[7].ToolTipText;
            m_grid.Rows[index].Cells[8].ToolTipText = m_grid.Columns[8].ToolTipText;
            m_grid.Rows[index].Cells["LinkedExpectedInterval"].Tag = rule.LinkedExpectedIntervalMs;
            m_grid.Rows[index].Cells["LinkedTolerance"].Tag = rule.LinkedTolerancePercent;
            SetRowColor(m_grid.Rows[index], rule.Color);
        }

        // 연동이 없으면 셀을 잠그고, 편집 중 잘못 입력한 값은 마지막 유효값으로 복원한다.
        private void SetLinkCellState(DataGridViewCell cell, bool disabled, double maximum)
        {
            cell.ReadOnly = disabled;
            cell.Style = disabled ? m_disabledLinkStyle : null;
            if (!disabled) { return; }
            double value;
            if (Double.TryParse(Convert.ToString(cell.Value), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && value >= 0 && value <= maximum)
            { cell.Tag = value; }
            else if (cell.Tag != null) { cell.Value = Convert.ToString(cell.Tag, CultureInfo.CurrentCulture); }
        }

        // 색상 값과 팔레트 버튼의 미리보기를 동시에 변경한다.
        private static void SetRowColor(DataGridViewRow row, Color color)
        {
            row.Tag = color; row.Cells[3].Value = ColorTranslator.ToHtml(color);
            row.Cells[3].Style.BackColor = color; row.Cells[3].Style.ForeColor = Theme.TextOn(color);
            row.Cells[3].Style.SelectionBackColor = color; row.Cells[3].Style.SelectionForeColor = Theme.TextOn(color);
        }

        // 키워드 순서와 차트의 레인 순서를 함께 변경한다.
        private void MoveRule(int direction)
        {
            m_grid.EndEdit();
            if (m_grid.CurrentRow == null) { return; }
            int index = m_grid.CurrentRow.Index;
            int target = index + direction;
            if (target < 0 || target >= m_grid.Rows.Count) { return; }
            Dictionary<DataGridViewRow, DataGridViewRow> targets = CaptureLinkedRows();
            DataGridViewRow row = m_grid.Rows[index];
            m_grid.Rows.RemoveAt(index); m_grid.Rows.Insert(target, row);
            UpdateKeywordNumbers(targets); m_grid.CurrentCell = row.Cells[1];
        }

        // 행 순서를 바꾸기 전에 연동 번호를 실제 행 참조로 보관한다.
        private Dictionary<DataGridViewRow, DataGridViewRow> CaptureLinkedRows()
        {
            Dictionary<DataGridViewRow, DataGridViewRow> targets = new Dictionary<DataGridViewRow, DataGridViewRow>();
            foreach (DataGridViewRow row in m_grid.Rows)
            {
                int number = Convert.ToInt32(row.Cells["LinkedKeywordNumber"].Value);
                if (number > 0 && number <= m_grid.Rows.Count) { targets.Add(row, m_grid.Rows[number - 1]); }
            }
            return targets;
        }

        // 선택 행을 삭제하고 해당 행을 가리키던 연결만 해제한다.
        private void RemoveRule()
        {
            m_grid.EndEdit();
            if (m_grid.CurrentRow == null) { return; }
            Dictionary<DataGridViewRow, DataGridViewRow> targets = CaptureLinkedRows();
            m_grid.Rows.RemoveAt(m_grid.CurrentRow.Index);
            UpdateKeywordNumbers(targets);
        }

        // 순번과 드롭다운을 함께 갱신하며 재정렬 후에도 기존 대상 행을 유지한다.
        private void UpdateKeywordNumbers(Dictionary<DataGridViewRow, DataGridViewRow> targets)
        {
            List<KeyValuePair<int, string>> choices = new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(0, "없음") };
            List<int> numbers = new List<int>();
            foreach (DataGridViewRow row in m_grid.Rows)
            {
                int number = Convert.ToInt32(row.Cells["LinkedKeywordNumber"].Value);
                if (targets != null)
                {
                    DataGridViewRow target;
                    if (targets.TryGetValue(row, out target) && target.Index >= 0) { number = target.Index + 1; }
                    else { number = 0; }
                }
                if (number > m_grid.Rows.Count) { number = 0; }
                numbers.Add(number); row.Cells["LinkedKeywordNumber"].Value = null;
                row.Cells["Number"].Value = row.Index + 1;
                choices.Add(new KeyValuePair<int, string>(row.Index + 1, (row.Index + 1).ToString(CultureInfo.CurrentCulture)));
            }
            ((DataGridViewComboBoxColumn)m_grid.Columns["LinkedKeywordNumber"]).DataSource = choices;
            for (int index = 0; index < m_grid.Rows.Count; index++) { m_grid.Rows[index].Cells["LinkedKeywordNumber"].Value = numbers[index]; }
        }

        // 반복 검사와 연동 검사의 독립 파라미터 및 등록 순번을 검증한 뒤 적용한다.

        private void ApplyRules()
        {
            m_grid.EndEdit();
            List<KeywordRule> rules = new List<KeywordRule>();
            foreach (DataGridViewRow row in m_grid.Rows)
            {
                string text = Convert.ToString(row.Cells[1].Value);
                if (String.IsNullOrWhiteSpace(text))
                { MessageBox.Show(this, "빈 키워드를 입력하거나 유지할 수 없습니다.", "타이밍 키워드", MessageBoxButtons.OK, MessageBoxIcon.Warning); m_grid.CurrentCell = row.Cells[1]; return; }
                KeywordRule rule = new KeywordRule { Text = text, Enabled = Convert.ToBoolean(row.Cells[0].Value), CaseSensitive = Convert.ToBoolean(row.Cells[2].Value), Color = (Color)row.Tag, CheckIntervals = Convert.ToBoolean(row.Cells[4].Value),
                    LinkedKeywordNumber = Convert.ToInt32(row.Cells["LinkedKeywordNumber"].Value) };
                if (!Double.TryParse(Convert.ToString(row.Cells[5].Value), NumberStyles.Float, CultureInfo.CurrentCulture, out rule.ExpectedIntervalMs)
                    || !Double.TryParse(Convert.ToString(row.Cells[6].Value), NumberStyles.Float, CultureInfo.CurrentCulture, out rule.TolerancePercent)
                    || !Double.TryParse(Convert.ToString(row.Cells["LinkedExpectedInterval"].Value), NumberStyles.Float, CultureInfo.CurrentCulture, out rule.LinkedExpectedIntervalMs)
                    || !Double.TryParse(Convert.ToString(row.Cells["LinkedTolerance"].Value), NumberStyles.Float, CultureInfo.CurrentCulture, out rule.LinkedTolerancePercent))
                { MessageBox.Show(this, "기준 간격과 허용 편차를 숫자로 입력하세요.", "타이밍 키워드", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                try { KeywordSettings.ValidateIntervals(rule); }
                catch (FormatException ex) { MessageBox.Show(this, ex.Message, "타이밍 키워드", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                rules.Add(rule);
            }
            try { KeywordSettings.ValidateLinks(rules); }
            catch (FormatException ex) { MessageBox.Show(this, ex.Message, "타이밍 키워드", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Rules = rules; DialogResult = DialogResult.OK; Close();
        }
    }
}
