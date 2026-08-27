using System;
using System.Threading;
using System.Windows.Forms;

namespace TcpSerialComm
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 安全约定1：窗体程序只能启动一次，不能二次启动
            using var mutex = new Mutex(true, @"Global\TcpSerialComm_SingleInstance_9F2A1C3D", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("程序已经在运行，不能二次启动。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
