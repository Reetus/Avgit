using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avgit.App;
using Avgit.App.Services;
using Avgit.App.ViewModels;
using Avgit.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Avgit.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Avgit.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true,
                FrameBufferFormat = Avalonia.Platform.PixelFormat.Rgba8888
            });
}

public sealed class FakeDialogService : Avgit.App.Services.IDialogService
{
    public List<string> Messages { get; } = [];
    public List<(string Title, string Message)> Confirmations { get; } = [];

    /// <summary>The answer ConfirmAsync gives (true unless a test cancels).</summary>
    public bool ConfirmResult { get; set; } = true;
    public Queue<string?> PromptResponses { get; } = new();
    public IReadOnlyList<RebasePlanItem>? RebasePlan { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string defaultName) => Task.FromResult<string?>(null);
    public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

    public Task<string?> PromptAsync(string title, string label, string initial = "")
        => Task.FromResult(PromptResponses.Count > 0 ? PromptResponses.Dequeue() : null);

    public Task<(string? Value, bool Flag)?> PromptWithCheckBoxAsync(string title, string label, string initial,
        string checkLabel, bool checkDefault = false)
        => Task.FromResult<(string? Value, bool Flag)?>(null);

    public Task<string?> EditTextAsync(string title, string label, string initial)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<RebasePlanItem>?> EditRebasePlanAsync(
        string title, IReadOnlyList<RebasePlanItem> commits)
        => Task.FromResult(RebasePlan);

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Confirmations.Add((title, message));
        return Task.FromResult(ConfirmResult);
    }

    /// <summary>Answers for ChooseAsync, in order; an empty queue (or null) cancels.</summary>
    public Queue<string?> ChoiceResponses { get; } = new();

    /// <summary>Every ChooseAsync call: its message and the options it offered.</summary>
    public List<(string Message, IReadOnlyList<string> Options)> Choices { get; } = [];

    /// <summary>Answers for PickAsync, in order; an empty queue (or null) cancels.</summary>
    public Queue<string?> PickResponses { get; } = new();

    /// <summary>Every PickAsync call: its message, the items offered and the one preselected.</summary>
    public List<(string Message, IReadOnlyList<string> Items, string? Selected)> Picks { get; } = [];

    public Task<string?> PickAsync(string title, string message, IReadOnlyList<string> items, string? selected = null)
    {
        Picks.Add((message, items, selected));
        return Task.FromResult(PickResponses.Count > 0 ? PickResponses.Dequeue() : null);
    }

    public Task<string?> ChooseAsync(string title, string message, IReadOnlyList<string> options)
    {
        Choices.Add((message, options));
        return Task.FromResult(ChoiceResponses.Count > 0 ? ChoiceResponses.Dequeue() : null);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Answers the trim-branches dialog once its list has loaded: may change the
    /// options or ticks, then returns true for Delete. Null cancels.
    /// </summary>
    public Func<TrimBranchesViewModel, Task<bool>>? TrimBranchesResponse { get; set; }

    /// <summary>Every trim-branches dialog shown, as loaded when it was answered.</summary>
    public List<TrimBranchesViewModel> TrimBranchesDialogs { get; } = [];

    public async Task<bool> TrimBranchesAsync(TrimBranchesViewModel viewModel)
    {
        TrimBranchesDialogs.Add(viewModel);
        await viewModel.Loading;
        return TrimBranchesResponse is { } respond && await respond(viewModel);
    }
}

