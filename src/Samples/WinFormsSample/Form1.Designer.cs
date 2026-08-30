namespace WinFormsSample
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;
        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name = "disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

#region Windows Form Designer generated code
        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            dropPanel = new System.Windows.Forms.Panel();
            SuspendLayout();
            //
            // dropPanel
            //
            dropPanel.Location = new System.Drawing.Point(12, 12);
            dropPanel.Name = "dropPanel";
            dropPanel.Size = new System.Drawing.Size(260, 150);
            dropPanel.TabIndex = 0;
            //
            // Form1
            //
            AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(400, 300);
            Controls.Add(dropPanel);
            Name = "Form1";
            Text = "Form1";
            ResumeLayout(false);
            Load += Form1_Load;
            Shown += Form1_Shown;
        }

#endregion
        private System.Windows.Forms.Panel dropPanel;
    }
}