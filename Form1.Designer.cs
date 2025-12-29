namespace SharpMIDI
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            Title = new Label();
            button1 = new Button();
            label1 = new Label();
            label2 = new Label();
            label5 = new Label();
            label6 = new Label();
            label7 = new Label();
            radioButton1 = new RadioButton();
            label8 = new Label();
            radioButton2 = new RadioButton();
            radioButton3 = new RadioButton();
            label9 = new Label();
            label10 = new Label();
            comboBox1 = new ComboBox();
            label11 = new Label();
            numericUpDown1 = new NumericUpDown();
            button3 = new Button();
            label13 = new Label();
            label3 = new Label();
            label12 = new Label();
            button4 = new Button();
            button5 = new Button();
            button6 = new Button();
            button7 = new Button();
            label15 = new Label();
            numericUpDown3 = new NumericUpDown();
            checkBox1 = new CheckBox();
            label14 = new Label();
            label16 = new Label();
            label17 = new Label();
            button2 = new Button();
            ((System.ComponentModel.ISupportInitialize)numericUpDown1).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown3).BeginInit();
            SuspendLayout();
            // 
            // Title
            // 
            Title.Dock = DockStyle.Top;
            Title.Font = new Font("Nirmala UI", 15F, FontStyle.Bold, GraphicsUnit.Point);
            Title.Location = new Point(0, 0);
            Title.Name = "Title";
            Title.Size = new Size(496, 40);
            Title.TabIndex = 0;
            Title.Text = "SharpMIDI v3.1.3";
            // 
            // button1
            // 
            button1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button1.Enabled = false;
            button1.Location = new Point(393, 0);
            button1.Name = "button1";
            button1.Size = new Size(103, 25);
            button1.TabIndex = 1;
            button1.Text = "Open MIDI";
            button1.UseVisualStyleBackColor = true;
            button1.Click += button1_Click;
            // 
            // label1
            // 
            label1.Dock = DockStyle.Top;
            label1.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label1.Location = new Point(0, 40);
            label1.Name = "label1";
            label1.Size = new Size(496, 15);
            label1.TabIndex = 2;
            label1.Text = "Selected MIDI: (none)";
            // 
            // label2
            // 
            label2.Dock = DockStyle.Top;
            label2.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label2.Location = new Point(0, 55);
            label2.Name = "label2";
            label2.Size = new Size(496, 15);
            label2.TabIndex = 3;
            label2.Text = "Status: Not Loaded";
            // 
            // label5
            // 
            label5.Dock = DockStyle.Top;
            label5.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label5.Location = new Point(0, 70);
            label5.Name = "label5";
            label5.Size = new Size(496, 15);
            label5.TabIndex = 6;
            label5.Text = "Notes: ??? / ???";
            // 
            // label6
            // 
            label6.Dock = DockStyle.Top;
            label6.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label6.Location = new Point(0, 85);
            label6.Name = "label6";
            label6.Size = new Size(496, 15);
            label6.TabIndex = 7;
            label6.Text = "PPQ: ???";
            // 
            // label7
            // 
            label7.Dock = DockStyle.Top;
            label7.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label7.Location = new Point(0, 100);
            label7.Name = "label7";
            label7.Size = new Size(496, 15);
            label7.TabIndex = 8;
            label7.Text = "GC Heap: 0 B";
            // 
            // radioButton1
            // 
            radioButton1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            radioButton1.AutoSize = true;
            radioButton1.Checked = true;
            radioButton1.Location = new Point(416, 55);
            radioButton1.Name = "radioButton1";
            radioButton1.RightToLeft = RightToLeft.Yes;
            radioButton1.Size = new Size(80, 19);
            radioButton1.TabIndex = 10;
            radioButton1.TabStop = true;
            radioButton1.Text = "OmniMIDI";
            radioButton1.UseVisualStyleBackColor = true;
            // 
            // label8
            // 
            label8.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            label8.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label8.Location = new Point(416, 40);
            label8.Name = "label8";
            label8.Size = new Size(80, 15);
            label8.TabIndex = 11;
            label8.Text = "Select Synth:";
            label8.TextAlign = ContentAlignment.TopRight;
            // 
            // radioButton2
            // 
            radioButton2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            radioButton2.AutoSize = true;
            radioButton2.Location = new Point(428, 73);
            radioButton2.Name = "radioButton2";
            radioButton2.RightToLeft = RightToLeft.Yes;
            radioButton2.Size = new Size(68, 19);
            radioButton2.TabIndex = 12;
            radioButton2.Text = "WinMM";
            radioButton2.UseVisualStyleBackColor = true;
            // 
            // radioButton3
            // 
            radioButton3.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            radioButton3.AutoSize = true;
            radioButton3.Location = new Point(434, 93);
            radioButton3.Name = "radioButton3";
            radioButton3.RightToLeft = RightToLeft.Yes;
            radioButton3.Size = new Size(62, 19);
            radioButton3.TabIndex = 13;
            radioButton3.Text = "XSynth";
            radioButton3.UseVisualStyleBackColor = true;
            // 
            // label9
            // 
            label9.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            label9.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label9.Location = new Point(406, 115);
            label9.Name = "label9";
            label9.Size = new Size(90, 15);
            label9.TabIndex = 14;
            label9.Text = "WinMM Device:";
            label9.TextAlign = ContentAlignment.TopRight;
            // 
            // label10
            // 
            label10.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label10.Location = new Point(0, 115);
            label10.Name = "label10";
            label10.Size = new Size(211, 15);
            label10.TabIndex = 16;
            label10.Text = "Loaded tracks: 0 / 0";
            // 
            // comboBox1
            // 
            comboBox1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            comboBox1.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox1.FormattingEnabled = true;
            comboBox1.Location = new Point(291, 133);
            comboBox1.Name = "comboBox1";
            comboBox1.Size = new Size(205, 23);
            comboBox1.TabIndex = 17;
            // 
            // label11
            // 
            label11.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            label11.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label11.Location = new Point(415, 188);
            label11.Name = "label11";
            label11.Size = new Size(81, 14);
            label11.TabIndex = 19;
            label11.Text = "Note Threshold";
            label11.TextAlign = ContentAlignment.TopRight;
            // 
            // numericUpDown1
            // 
            numericUpDown1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            numericUpDown1.Location = new Point(415, 205);
            numericUpDown1.Maximum = new decimal(new int[] { 127, 0, 0, 0 });
            numericUpDown1.Name = "numericUpDown1";
            numericUpDown1.RightToLeft = RightToLeft.No;
            numericUpDown1.Size = new Size(81, 23);
            numericUpDown1.TabIndex = 20;
            // 
            // button3
            // 
            button3.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button3.Location = new Point(375, 162);
            button3.Name = "button3";
            button3.Size = new Size(121, 23);
            button3.TabIndex = 23;
            button3.Text = "Apply Synth";
            button3.UseVisualStyleBackColor = true;
            button3.Click += button3_Click;
            // 
            // label13
            // 
            label13.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            label13.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label13.Location = new Point(393, 25);
            label13.Name = "label13";
            label13.Size = new Size(103, 15);
            label13.TabIndex = 24;
            label13.Text = "Select a Synth first!";
            label13.TextAlign = ContentAlignment.TopRight;
            // 
            // label3
            // 
            label3.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label3.Location = new Point(0, 156);
            label3.Name = "label3";
            label3.Size = new Size(302, 15);
            label3.TabIndex = 25;
            label3.Text = "Played: 0 / 0";
            // 
            // label12
            // 
            label12.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label12.Location = new Point(0, 141);
            label12.Name = "label12";
            label12.Size = new Size(211, 15);
            label12.TabIndex = 26;
            label12.Text = "FPS ≈ N/A";
            // 
            // button4
            // 
            button4.Enabled = false;
            button4.Location = new Point(0, 219);
            button4.Name = "button4";
            button4.Size = new Size(46, 23);
            button4.TabIndex = 27;
            button4.Text = "Run";
            button4.UseVisualStyleBackColor = true;
            button4.Click += button4_Click;
            // 
            // button5
            // 
            button5.Enabled = false;
            button5.Location = new Point(0, 248);
            button5.Name = "button5";
            button5.Size = new Size(46, 23);
            button5.TabIndex = 28;
            button5.Text = "Stop";
            button5.UseVisualStyleBackColor = true;
            button5.Click += button5_Click;
            // 
            // button6
            // 
            button6.Enabled = false;
            button6.Location = new Point(52, 248);
            button6.Name = "button6";
            button6.Size = new Size(46, 23);
            button6.TabIndex = 29;
            button6.Text = "Pause";
            button6.UseVisualStyleBackColor = true;
            button6.Click += button6_Click;
            // 
            // button7
            // 
            button7.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button7.Location = new Point(375, 279);
            button7.Name = "button7";
            button7.Size = new Size(121, 23);
            button7.TabIndex = 31;
            button7.Text = "Enable Console";
            button7.UseVisualStyleBackColor = true;
            button7.Click += button7_Click;
            // 
            // label15
            // 
            label15.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            label15.Location = new Point(435, 231);
            label15.Name = "label15";
            label15.Size = new Size(61, 16);
            label15.TabIndex = 32;
            label15.Text = "Track limit";
            label15.TextAlign = ContentAlignment.TopRight;
            // 
            // numericUpDown3
            // 
            numericUpDown3.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            numericUpDown3.Location = new Point(435, 250);
            numericUpDown3.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            numericUpDown3.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown3.Name = "numericUpDown3";
            numericUpDown3.RightToLeft = RightToLeft.No;
            numericUpDown3.Size = new Size(61, 23);
            numericUpDown3.TabIndex = 34;
            numericUpDown3.Value = new decimal(new int[] { 65535, 0, 0, 0 });
            // 
            // checkBox1
            // 
            checkBox1.AutoSize = true;
            checkBox1.Checked = true;
            checkBox1.CheckState = CheckState.Checked;
            checkBox1.Location = new Point(0, 277);
            checkBox1.Name = "checkBox1";
            checkBox1.Size = new Size(154, 19);
            checkBox1.TabIndex = 35;
            checkBox1.Text = "Skip events instead of throttling playback on lag";
            checkBox1.UseVisualStyleBackColor = true;
            checkBox1.CheckedChanged += checkBox1_CheckedChanged;
            // 
            // label14
            // 
            label14.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label14.Location = new Point(0, 171);
            label14.Name = "label14";
            label14.Size = new Size(261, 15);
            label14.TabIndex = 36;
            label14.Text = "Tick: 0";
            // 
            // label16
            // 
            label16.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label16.Location = new Point(0, 186);
            label16.Name = "label16";
            label16.Size = new Size(261, 15);
            label16.TabIndex = 37;
            label16.Text = "Ticks/sec: N/A";
            // 
            // label17
            // 
            label17.Font = new Font("Segoe MDL2 Assets", 11.25F, FontStyle.Regular, GraphicsUnit.Point);
            label17.Location = new Point(0, 201);
            label17.Name = "label17";
            label17.Size = new Size(261, 15);
            label17.TabIndex = 38;
            label17.Text = "BPM: 120";
            // 
            // button2
            // 
            button2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button2.Enabled = false;
            button2.Location = new Point(291, 0);
            button2.Name = "button2";
            button2.Size = new Size(103, 25);
            button2.TabIndex = 39;
            button2.Text = "Unload MIDI";
            button2.UseVisualStyleBackColor = true;
            button2.Click += button2_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(496, 410);
            Controls.Add(button2);
            Controls.Add(label17);
            Controls.Add(label16);
            Controls.Add(label14);
            Controls.Add(checkBox1);
            Controls.Add(numericUpDown3);
            Controls.Add(label15);
            Controls.Add(button7);
            Controls.Add(button6);
            Controls.Add(button5);
            Controls.Add(button4);
            Controls.Add(label12);
            Controls.Add(label3);
            Controls.Add(label13);
            Controls.Add(button3);
            Controls.Add(numericUpDown1);
            Controls.Add(label11);
            Controls.Add(comboBox1);
            Controls.Add(label10);
            Controls.Add(label9);
            Controls.Add(radioButton3);
            Controls.Add(radioButton2);
            Controls.Add(label8);
            Controls.Add(radioButton1);
            Controls.Add(label7);
            Controls.Add(label6);
            Controls.Add(label5);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(button1);
            Controls.Add(Title);
            MinimumSize = new Size(438, 386);
            Name = "Form1";
            Text = "SharpMIDI GUI";
            ((System.ComponentModel.ISupportInitialize)numericUpDown1).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown3).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label Title;
        private Button button1;
        private RadioButton radioButton1;
        private Label label8;
        private RadioButton radioButton2;
        private RadioButton radioButton3;
        private Label label9;
        public Label label7;
        private Label label11;
        public Label label1;
        public Label label2;
        public Label label5;
        public Label label6;
        public Label label10;
        public ComboBox comboBox1;
        public NumericUpDown numericUpDown1;
        public Button button3;
        public Label label13;
        public Label label3;
        public Label label12;
        public Button button4;
        public Button button5;
        public Button button6;
        public Button button7;
        private Label label15;
        public NumericUpDown numericUpDown3;
        private CheckBox checkBox1;
        public Label label14;
        public Label label16;
        public Label label17;
        private Button button2;
    }
}
