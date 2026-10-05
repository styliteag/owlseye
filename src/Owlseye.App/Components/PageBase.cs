using Microsoft.AspNetCore.Components;
using Owlseye.App.Services;
using Owlseye.Ui;

namespace Owlseye.App.Components;

/// <summary>Pages re-render when the state changes (scan done, pending changes) and share the action helpers.</summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    [Inject] protected State St { get; set; } = null!;
    [Inject] protected Session S { get; set; } = null!;
    [Inject] protected UiState Ui { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected Dialogs Dialogs { get; set; } = null!;

    protected override void OnInitialized() => St.Changed += OnStateChanged;

    protected override void OnParametersSet()
    {
        if (!Ui.Busy) SafeRefresh(); // busy: the state is locked; UiState refreshes all pages when it is done
    }

    void OnStateChanged() => InvokeAsync(() =>
    {
        if (!St.Ready || Ui.Busy) return;
        SafeRefresh();
        StateHasChanged();
    });

    void SafeRefresh()
    {
        try
        {
            Refresh();
            NotFound = null;
        }
        catch (UserError e)
        {
            NotFound = e.Message;
        }
    }

    /// <summary>Set when Refresh threw a UserError (unknown folder or account): the page shows it instead.</summary>
    protected string? NotFound { get; private set; }

    /// <summary>Recompute the view model of the page.</summary>
    protected virtual void Refresh() { }

    public virtual void Dispose()
    {
        St.Changed -= OnStateChanged;
        GC.SuppressFinalize(this);
    }

    protected void Go(Outcome o) => Ui.Go(Nav, o);

    protected Task Busy(Func<Outcome> action) => Ui.RunBusy(Nav, action);

    /// <summary>Run an action; a UserError becomes a red flash on this page.</summary>
    protected bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (UserError e)
        {
            Ui.SetFlash(e.Message, true);
            return false;
        }
    }

    protected static string Enc(string s) => Uri.EscapeDataString(s);

    protected static string Root(string path) => path != "" ? path : "(root)";
}
