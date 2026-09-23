namespace FileSetuDesktop;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? explorerPath = null;
        if (args.Length >= 2 && args[0].Equals("--send", StringComparison.OrdinalIgnoreCase))
            explorerPath = args[1];
        Application.Run(new MainForm(explorerPath));
    }
}
