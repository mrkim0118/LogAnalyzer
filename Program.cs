using System;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("LogAnalyzer")]
[assembly: AssemblyProduct("LogAnalyzer")]
[assembly: AssemblyVersion("1.0.18.0")]
[assembly: AssemblyFileVersion("1.0.18.0")]

namespace LogAnalyzer
{
    internal static class Program
    {
        // Windows 메시지 루프를 시작하고 선택한 폴더 인자를 메인 화면에 전달한다.
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string folder = args.Length > 0 ? args[0] : null;
            Application.Run(new MainForm(folder));
        }
    }
}
