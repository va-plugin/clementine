using System.IO;
using Clementine.App.Options;
using Clementine.App.Toasts;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.App.Launching;

/// <summary>Asks the user for options (when the operation has any) and returns the values, or null if cancelled.</summary>
public delegate Task<OptionValues?> OptionsPrompt(IOperation operation, IReadOnlyList<InputFile> inputs, OptionValues initial, ScreenPoint? anchor);

/// <summary>
/// The app's <see cref="IOperationLauncher"/>: checks availability, shows the options dialog pre-filled per
/// <see cref="OptionMerging"/>, queues the jobs, remembers the values and starts a toast card for the launch.
/// </summary>
public sealed class UiLauncher : IOperationLauncher
{
    static readonly TimeSpan DynamicDefaultsTimeout = TimeSpan.FromSeconds(6);

    readonly HostServices _services;
    readonly ToastManager? _toasts;
    readonly OptionsPrompt _prompt;

    public UiLauncher(HostServices services, ToastManager? toasts, OptionsPrompt? prompt = null)
    {
        _services = services;
        _toasts = toasts;
        _prompt = prompt ?? ShowDialogAsync;
    }

    public async Task<IReadOnlyList<Job>> LaunchAsync(IOperation operation, IReadOnlyList<InputFile> inputs, LaunchSource source, ScreenPoint? anchor = null)
    {
        if (inputs.Count == 0)
            return [];

        var availability = SafeAvailability(operation);
        if (!availability.IsAvailable)
        {
            _toasts?.ShowInfo(ToastState.Info($"{operation.Info.Title} isn't available", availability.Reason));
            return [];
        }

        OptionValues? options = null;
        if (operation.Options is { } schema)
        {
            var initial = await PrefillAsync(operation, schema, inputs).ConfigureAwait(true);
            options = await _prompt(operation, inputs, initial, anchor).ConfigureAwait(true);
            if (options is null)
            {
                _services.Log.Info($"{operation.Info.Id}: options dialog cancelled");
                return [];
            }
            Remember(operation, schema, options);
        }

        var jobs = _services.Jobs.Enqueue(operation, inputs, options);
        _toasts?.Track(jobs);
        _services.Log.Info($"Launched {operation.Info.Id} on {inputs.Count} file(s) from {source}");
        return jobs;
    }

    /// <summary>Schema defaults → remembered values → dynamic defaults (see <see cref="OptionMerging"/>).</summary>
    public async Task<OptionValues> PrefillAsync(IOperation operation, OptionSchema schema, IReadOnlyList<InputFile> inputs)
    {
        OptionValues? dynamicDefaults = null;
        using var timeout = new CancellationTokenSource(DynamicDefaultsTimeout);
        try
        {
            dynamicDefaults = await Task.Run(() => operation.GetDynamicDefaultsAsync(inputs, timeout.Token), timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _services.Log.Warn($"{operation.Info.Id}: dynamic defaults took too long; using static defaults");
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"{operation.Info.Id}: dynamic defaults failed", ex);
        }

        _services.Settings.Current.RememberedOptions.TryGetValue(operation.Info.Id, out var remembered);
        return OptionMerging.Prefill(schema, remembered, dynamicDefaults);
    }

    void Remember(IOperation operation, OptionSchema schema, OptionValues values)
    {
        var keep = OptionMerging.ToRemember(schema, values);
        try
        {
            _services.Settings.Update(s => s.RememberedOptions[operation.Info.Id] = keep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn("Couldn't save remembered options", ex);
        }
    }

    Availability SafeAvailability(IOperation operation)
    {
        try
        {
            return operation.CheckAvailability();
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"{operation.Info.Id}.CheckAvailability threw", ex);
            return Availability.Unavailable("Couldn't check whether this can run.");
        }
    }

    static Task<OptionValues?> ShowDialogAsync(IOperation operation, IReadOnlyList<InputFile> inputs, OptionValues initial, ScreenPoint? anchor)
    {
        var dialog = new OptionsDialog(operation.Options!, inputs, initial, operation.Info.IconKey, operation as IOptionsPreview);
        return dialog.ShowAsync(anchor);
    }
}
