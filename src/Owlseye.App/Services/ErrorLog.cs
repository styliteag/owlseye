namespace Owlseye.App.Services;

/// <summary>Unexpected errors go to error.log in the data folder (with the stack), so a report can say what happened.</summary>
public static class ErrorLog
{
    static readonly Lock Gate = new();

    public static string PathOf => Path.Combine(Paths.DataDir(), "error.log");

    public static void Write(string where, Exception e)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir());
                File.AppendAllText(PathOf, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {e}\n\n");
            }
        }
        catch (Exception)
        {
            // nothing left to report to
        }
    }
}

/// <summary>ErrorBoundary that also writes the error to error.log.</summary>
public sealed class LoggingErrorBoundary : Microsoft.AspNetCore.Components.Web.ErrorBoundary
{
    protected override Task OnErrorAsync(Exception exception)
    {
        ErrorLog.Write("page", exception);
        return Task.CompletedTask;
    }
}
