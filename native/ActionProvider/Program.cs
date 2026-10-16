using System.Windows;

namespace EdgeGoogleActions.ActionProvider;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var runtime = Microsoft.AI.Actions.Helpers.ActionRuntimeFactory.CreateActionRuntime();
        Microsoft.AI.Actions.Helpers.ActionProviderFactory.RegisterActionProvider<
            GoogleActionProvider, Windows.AI.Actions.Provider.IActionProvider>(new GoogleActionProvider());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!Environment.GetCommandLineArgs().Any(arg =>
                arg.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
                arg.Contains("activat", StringComparison.OrdinalIgnoreCase)))
        {
            var window = new Window
            {
                Title = "Google Actions for Edge",
                Width = 620, Height = 260,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Windows App Actions provider is running.\n\nOpen the Google Actions extension in Microsoft Edge to connect your Google account and review requests.\n\nOther apps can discover four actions: send email, list contacts, list meetings, and move one meeting.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24)
                }
            };
            app.MainWindow = window;
            window.Closed += (_, _) => app.Shutdown();
            window.Show();
        }
        app.Run();
    }
}
