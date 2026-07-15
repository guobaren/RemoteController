using System.Windows.Forms;

namespace Rc.UiTestApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new UiTestForm());
    }
}
