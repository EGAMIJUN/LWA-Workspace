using System;
using System.Drawing;
using System.Windows.Forms;

namespace MiniPortLegacy
{
    public partial class Form1 : Form
    {
        private TabControl? tabControl;
        private TabPage? tabInput;
        private TabPage? tabList;
        private Label? lblNo;
        private TextBox? txtNo;
        private Label? lblType;
        private ComboBox? cmbType;
        private CheckBox? chkDamaged;
        private Button? btnRegister;
        private Label? lblStatus;
        private DataGridView? gridInventory;

        public Form1()
        {
            // ★重要：デザイナーファイルを消したので、ここには何も書かない！
            // InitializeComponent();  <-- この行があったら消せ！
            
            SetupCustomUI();
        }

        private void SetupCustomUI()
        {
            this.Text = "Mini Port Legacy - 統合コンテナ管理システム";
            this.Size = new Size(600, 450);
            this.Controls.Clear();

            // 1. タブ
            tabControl = new TabControl { Dock = DockStyle.Fill, Name = "MainTabControl" };

            // 2. 入力タブ
            tabInput = new TabPage("入庫登録") { Name = "TabInput" };
            
            lblNo = new Label { Text = "コンテナNo:", Location = new Point(20, 30), AutoSize = true };
            txtNo = new TextBox { Location = new Point(120, 27), Width = 150, Name = "txtContainerNo" }; // ID

            lblType = new Label { Text = "サイズ/タイプ:", Location = new Point(20, 70), AutoSize = true };
            cmbType = new ComboBox { Location = new Point(120, 67), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Name = "cmbContainerType" }; // ID
            cmbType.Items.AddRange(new string[] { "20ft Dry", "40ft Dry", "40ft Reefer", "40ft OpenTop" });
            cmbType.SelectedIndex = 0;

            chkDamaged = new CheckBox { Text = "ダメージあり", Location = new Point(120, 110), AutoSize = true, Name = "chkDamaged" }; // ID

            btnRegister = new Button { Text = "在庫登録", Location = new Point(120, 150), Width = 100, Height = 40, Name = "btnRegister" }; // ID
            btnRegister.Click += BtnRegister_Click;

            lblStatus = new Label { Text = "待機中...", Location = new Point(20, 220), AutoSize = true, ForeColor = Color.Blue, Name = "lblStatus" };

            tabInput.Controls.Add(lblNo);
            tabInput.Controls.Add(txtNo);
            tabInput.Controls.Add(lblType);
            tabInput.Controls.Add(cmbType);
            tabInput.Controls.Add(chkDamaged);
            tabInput.Controls.Add(btnRegister);
            tabInput.Controls.Add(lblStatus);

            // 3. 一覧タブ
            tabList = new TabPage("在庫一覧") { Name = "TabList" };
            gridInventory = new DataGridView { Dock = DockStyle.Fill, Name = "gridInventory", ColumnCount = 4 };
            gridInventory.Columns[0].Name = "No";
            gridInventory.Columns[1].Name = "Type";
            gridInventory.Columns[2].Name = "Damaged";
            gridInventory.Columns[3].Name = "Time";
            tabList.Controls.Add(gridInventory);

            tabControl.Controls.Add(tabInput);
            tabControl.Controls.Add(tabList);
            this.Controls.Add(tabControl);
        }

        private void BtnRegister_Click(object? sender, EventArgs e)
        {
            if (txtNo == null || cmbType == null || chkDamaged == null || gridInventory == null || lblStatus == null || tabList == null || tabControl == null) return;
            
            if (string.IsNullOrWhiteSpace(txtNo.Text)) { MessageBox.Show("Noを入力してください"); return; }

            string dmg = chkDamaged.Checked ? "あり" : "なし";
            string type = cmbType.SelectedItem?.ToString() ?? "-";
            gridInventory.Rows.Add(txtNo.Text, type, dmg, DateTime.Now.ToString("HH:mm:ss"));
            
            lblStatus.Text = "登録完了: " + txtNo.Text;
            tabControl.SelectedTab = tabList;
            MessageBox.Show("登録しました！\nコンテナ: " + txtNo.Text + "\nタイプ: " + type, "処理完了");
            txtNo.Clear();
        }
    }
}