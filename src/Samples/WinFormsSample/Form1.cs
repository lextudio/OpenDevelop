namespace WinFormsSample
{
    public partial class Form1 : System.Windows.Forms.Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(System.Object sender, System.EventArgs e)
        {
        }

        private void Form1_Shown(System.Object sender, System.EventArgs e)
        {
        }
    }

    /// <summary>A subclass of <see cref="Form1"/>, so Find Derived Classes has something to find.</summary>
    public class Form1Derived : Form1
    {
    }
}
