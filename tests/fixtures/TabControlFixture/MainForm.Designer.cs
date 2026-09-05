namespace TabControlFixture
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TabPage tabPage2;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.Label label1;
        private void InitializeComponent()
        {
            tabControl1 = new System.Windows.Forms.TabControl();
            tabPage1 = new System.Windows.Forms.TabPage();
            tabPage2 = new System.Windows.Forms.TabPage();
            button1 = new System.Windows.Forms.Button();
            button2 = new System.Windows.Forms.Button();
            label1 = new System.Windows.Forms.Label();
            tabControl1.SuspendLayout();
            tabPage1.SuspendLayout();
            tabPage2.SuspendLayout();
            SuspendLayout();
            //
            // tabControl1
            //
            tabControl1.Controls.Add(tabPage1);
            tabControl1.Controls.Add(tabPage2);
            tabControl1.Location = new System.Drawing.Point(12, 12);
            tabControl1.Name = "tabControl1";
            tabControl1.SelectedIndex = 0;
            tabControl1.Size = new System.Drawing.Size(360, 220);
            tabControl1.TabIndex = 0;
            //
            // tabPage1
            //
            tabPage1.Controls.Add(button1);
            tabPage1.Location = new System.Drawing.Point(4, 24);
            tabPage1.Name = "tabPage1";
            tabPage1.Size = new System.Drawing.Size(352, 192);
            tabPage1.TabIndex = 0;
            tabPage1.Text = "General";
            //
            // tabPage2
            //
            tabPage2.Controls.Add(button2);
            tabPage2.Controls.Add(label1);
            tabPage2.Location = new System.Drawing.Point(4, 24);
            tabPage2.Name = "tabPage2";
            tabPage2.Size = new System.Drawing.Size(352, 192);
            tabPage2.TabIndex = 1;
            tabPage2.Text = "Advanced";
            //
            // button1
            //
            button1.Location = new System.Drawing.Point(20, 20);
            button1.Name = "button1";
            button1.Size = new System.Drawing.Size(100, 30);
            button1.TabIndex = 0;
            button1.Text = "Button on General";
            //
            // button2
            //
            button2.Location = new System.Drawing.Point(20, 20);
            button2.Name = "button2";
            button2.Size = new System.Drawing.Size(100, 30);
            button2.TabIndex = 0;
            button2.Text = "Button on Advanced";
            //
            // label1
            //
            label1.Location = new System.Drawing.Point(20, 60);
            label1.Name = "label1";
            label1.Size = new System.Drawing.Size(200, 23);
            label1.TabIndex = 1;
            label1.Text = "Advanced settings go here";
            //
            // MainForm
            //
            ClientSize = new System.Drawing.Size(400, 260);
            Controls.Add(tabControl1);
            Name = "MainForm";
            Text = "TabControl Fixture";
            tabControl1.ResumeLayout(false);
            tabPage1.ResumeLayout(false);
            tabPage2.ResumeLayout(false);
            ResumeLayout(false);
        }
    }
}