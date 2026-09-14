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
            // Safety rule 1: the WinForms application may only be started once; a second instance is rejected.
            using var mutex = new Mutex(true, @"Global\TcpSerialComm_SingleInstance_9F2A1C3D", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("The application is already running. Multiple instances are not allowed.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
