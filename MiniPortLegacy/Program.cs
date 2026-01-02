using System;
using System.Windows.Forms;

namespace MiniPortLegacy
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // .NET 6 以降の WinForms テンプレの基本形
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
