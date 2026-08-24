using System;
using System.Windows.Forms;

namespace AudioBridge.Server;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
