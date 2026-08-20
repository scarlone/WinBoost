using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WinBoost.Core;

namespace WinBoost.App;

public class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>Una voce selezionabile di un parametro multi-select.</summary>
public sealed class OptionToggleVm : ObservableBase
{
    private bool _isChecked;

    public OptionToggleVm(ParameterOption option, bool isChecked, Action onChanged)
    {
        Key = option.Key;
        Label = option.Label;
        _isChecked = isChecked;
        OnChanged = onChanged;
    }

    private Action OnChanged { get; }

    public string Key { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) OnChanged(); }
    }
}

public sealed class ParameterVm : ObservableBase
{
    private readonly ParameterOverrides _overrides;
    private readonly string _tweakId;
    private ParameterOption? _selected;

    public ParameterVm(string tweakId, ParameterDef def, ParameterOverrides overrides)
    {
        _tweakId = tweakId;
        _overrides = overrides;
        Def = def;

        foreach (var o in def.Options) Options.Add(o);

        if (def.IsChoice)
        {
            _selected = def.Options.FirstOrDefault(o => o.Key == def.DefaultChoice) ?? def.Options.FirstOrDefault();
        }
        else
        {
            var preselected = def.DefaultList.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in def.Options)
                Toggles.Add(new OptionToggleVm(o, preselected.Contains(o.Key), PushList));
            PushList();
        }
    }

    public ParameterDef Def { get; }
    public string Key => Def.Key;
    public string? Note => Def.Note;
    public bool IsChoice => Def.IsChoice;
    public bool IsMultiSelect => Def.IsMultiSelect;

    public ObservableCollection<ParameterOption> Options { get; } = new();
    public ObservableCollection<OptionToggleVm> Toggles { get; } = new();

    public ParameterOption? SelectedOption
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            _overrides.SetChoice(_tweakId, Def.Key, value.Key);
        }
    }

    public string Summary => IsMultiSelect
        ? $"{Toggles.Count(t => t.IsChecked)} di {Toggles.Count} selezionati"
        : "";

    private void PushList()
    {
        _overrides.SetList(_tweakId, Def.Key, Toggles.Where(t => t.IsChecked).Select(t => t.Key));
        Raise(nameof(Summary));
    }
}

public sealed class TweakVm : ObservableBase
{
    private bool _isSelected;

    public TweakVm(Tweak model, Category? category, Applicability applicability, ParameterOverrides overrides)
    {
        Model = model;
        CategoryName = category?.Name ?? model.Category;
        Applicability = applicability;
        _isSelected = model.IsDefaultOn && applicability.CanApply && model.Risk == RiskLevel.Low;

        foreach (var def in ParameterParser.Parse(model))
            Parameters.Add(new ParameterVm(model.Id, def, overrides));
    }

    public ObservableCollection<ParameterVm> Parameters { get; } = new();
    public bool HasParameters => Parameters.Count > 0;

    public Tweak Model { get; }
    public Applicability Applicability { get; }
    public string CategoryName { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string? Description => Model.Description;
    public string? Warning => Model.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Model.Warning);
    public RiskLevel Risk => Model.Risk;

    public string RiskLabel => Model.Risk switch
    {
        RiskLevel.Low => "BASSO",
        RiskLevel.Medium => "MEDIO",
        _ => "ALTO"
    };

    public string Badges
    {
        get
        {
            var parts = new List<string>();
            if (Model.Admin) parts.Add("admin");
            if (Model.Reboot) parts.Add("riavvio");
            if (!string.IsNullOrWhiteSpace(Model.Vendor)) parts.Add(Model.Vendor.ToUpperInvariant());
            if (!Model.IsDefaultOn) parts.Add("opt-in");
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    public bool CanApply => Applicability.CanApply;
    public string BlockReason => Applicability.Reason ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (CanApply || !value) Set(ref _isSelected, value); }
    }
}

public sealed class MainViewModel : ObservableBase
{
    private readonly TweakEngine _engine;
    private readonly Dispatcher_ _ui;
    private string _selectedCategory = "Tutte";
    private string _search = "";
    private bool _busy;
    private string _status = "";

    /// <summary>Piccolo wrapper per non dipendere da WPF nei test.</summary>
    public sealed class Dispatcher_
    {
        public void Post(Action a) => Application.Current?.Dispatcher.Invoke(a);
    }

    public MainViewModel(TweakEngine engine, LoadedCatalog loaded)
    {
        _engine = engine;
        _ui = new Dispatcher_();

        CatalogOrigin = loaded.Origin;
        IsExternalCatalog = loaded.IsExternal;

        foreach (var t in engine.Catalog.Tweaks)
        {
            var vm = new TweakVm(t, engine.Catalog.FindCategory(t.Category), engine.CheckApplicable(t), engine.Overrides);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TweakVm.IsSelected)) UpdateStatus();
            };
            Tweaks.Add(vm);
        }

        Categories.Add("Tutte");
        foreach (var c in engine.Catalog.Categories) Categories.Add(c.Name);

        foreach (var p in engine.Catalog.Presets) Presets.Add(p);

        TweaksView = CollectionViewSource.GetDefaultView(Tweaks);
        TweaksView.Filter = FilterTweak;
        TweaksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakVm.CategoryName)));

        engine.Log += m => _ui.Post(() => AppendLog(m));

        PreviewCommand = new RelayCommand(_ => RunPreview(), _ => !Busy && SelectedCount > 0);
        ApplyCommand = new RelayCommand(_ => RunApply(), _ => !Busy && SelectedCount > 0 && IsElevated);
        ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as Preset));
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        SelectNoneCommand = new RelayCommand(_ => SetAll(false));
        RollbackCommand = new RelayCommand(s => RunRollback(s as Session), s => !Busy && s is Session { IsRolledBack: false });
        RefreshSessionsCommand = new RelayCommand(_ => LoadSessions());
        ElevateCommand = new RelayCommand(_ => Elevate(), _ => !IsElevated);

        Hardware = engine.Probe.Summary();
        LoadSessions();
        UpdateStatus();
    }

    public ObservableCollection<TweakVm> Tweaks { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<Preset> Presets { get; } = new();
    public ObservableCollection<PlannedChange> PreviewItems { get; } = new();
    public ObservableCollection<Session> Sessions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public ICollectionView TweaksView { get; }
    public string Hardware { get; }
    public bool IsElevated { get; } = HardwareProbe.IsElevated();
    public string ElevationText => IsElevated ? "Amministratore" : "Utente standard - applicazione disattivata";

    /// <summary>Provenienza del catalogo, resa visibile: un catalogo esterno cambia
    /// cosa fa un processo elevato, quindi non deve poter passare inosservato.</summary>
    public string CatalogOrigin { get; } = "";
    public bool IsExternalCatalog { get; }
    public string CatalogWarning => $"CATALOGO ESTERNO: {CatalogOrigin}";

    public ICommand PreviewCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand RefreshSessionsCommand { get; }
    public ICommand ElevateCommand { get; }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value)) RefreshFilter(); }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) RefreshFilter(); }
    }

    private void RefreshFilter()
    {
        TweaksView.Refresh();
        Raise(nameof(VisibleCount));
        Raise(nameof(IsFiltered));
    }

    public bool Busy
    {
        get => _busy;
        private set { Set(ref _busy, value); Raise(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public int SelectedCount => Tweaks.Count(t => t.IsSelected);

    /// <summary>Quanti tweak superano ricerca e filtro categoria: senza questo il
    /// contatore direbbe sempre 76 anche con la lista ridotta a tre righe.</summary>
    public int VisibleCount => TweaksView.Cast<TweakVm>().Count();

    public bool IsFiltered => _selectedCategory != "Tutte" || !string.IsNullOrWhiteSpace(_search);

    private bool FilterTweak(object o)
    {
        if (o is not TweakVm vm) return false;
        if (_selectedCategory != "Tutte" && vm.CategoryName != _selectedCategory) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;

        return vm.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || vm.Id.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || (vm.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void SetAll(bool value)
    {
        foreach (var t in TweaksView.Cast<TweakVm>()) t.IsSelected = value;
        UpdateStatus();
    }

    private void ApplyPreset(Preset? preset)
    {
        if (preset is null) return;

        var resolved = _engine.ResolvePreset(preset);

        // Un preset marcato RequiresConfirmation include anche modifiche irreversibili:
        // la conferma va chiesta prima di selezionarle, non solo scritta nel log.
        if (preset.RequiresConfirmation)
        {
            var alto = resolved.Count(t => t.Risk == RiskLevel.High);
            var messaggio = $"Il preset '{preset.Name}' seleziona {resolved.Count} tweak"
                          + (alto > 0 ? $", di cui {alto} ad ALTO rischio (rimozioni di app e componenti)." : ".")
                          + "\n\nNulla verra' applicato finche' non premi Applica.\n\nSelezionarli?";

            if (MessageBox.Show(messaggio, $"Preset {preset.Name}",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                AppendLog($"Preset '{preset.Name}' annullato dall'utente.");
                return;
            }
        }

        var included = resolved.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tweaks) t.IsSelected = included.Contains(t.Id) && t.CanApply;

        AppendLog($"Preset '{preset.Name}': {SelectedCount} tweak selezionati.");
        UpdateStatus();
    }

    private List<Tweak> Selected() => Tweaks.Where(t => t.IsSelected).Select(t => t.Model).ToList();

    private void RunPreview()
    {
        Busy = true;
        PreviewItems.Clear();
        var selected = Selected();

        Task.Run(() =>
        {
            var changes = _engine.Preview(selected);
            _ui.Post(() =>
            {
                foreach (var c in changes) PreviewItems.Add(c);
                var real = changes.Count(c => c.Kind != "skip");
                AppendLog($"Anteprima: {real} modifiche su {selected.Count} tweak ({changes.Count - real} saltate).");
                Status = $"Anteprima pronta: {real} modifiche";
                Busy = false;
            });
        });
    }

    private void RunApply()
    {
        var selected = Selected();
        var high = selected.Count(t => t.Risk == RiskLevel.High);

        var irreversibili = selected
            .SelectMany(t => t.Ops)
            .Count(o => string.Equals(o.Revert, "none", StringComparison.OrdinalIgnoreCase));

        // Riavviare Explorer chiude le finestre aperte: va detto prima, non scoperto dopo.
        var riavvii = selected.SelectMany(t => t.Restart).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var message = $"Stai per applicare {selected.Count} tweak a questo sistema.\n\n"
                    + (high > 0 ? $"- {high} classificati ad ALTO rischio\n" : "")
                    + (irreversibili > 0 ? $"- {irreversibili} operazioni non annullabili\n" : "")
                    + (riavvii.Count > 0 ? $"- verra' riavviato: {string.Join(", ", riavvii)} (le finestre aperte si chiuderanno)\n" : "")
                    + "\nTutto il resto viene registrato e potra' essere annullato dalla scheda Cronologia.\n\n"
                    + "Procedere?";

        if (MessageBox.Show(message, "Conferma applicazione",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Busy = true;
        Task.Run(() =>
        {
            var session = _engine.Apply(selected);
            _ui.Post(() =>
            {
                LoadSessions();
                Status = $"Sessione {session.Id}: {session.AppliedCount} applicate, {session.FailedCount} fallite";
                Busy = false;

                if (session.RebootRequired)
                    MessageBox.Show("Alcune modifiche richiedono il riavvio del sistema per avere effetto.",
                        "Riavvio necessario", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    private void RunRollback(Session? session)
    {
        if (session is null) return;

        var count = session.Entries.Count(e => e.CanRollback);
        if (MessageBox.Show(
                $"Annullare {count} modifiche della sessione {session.Id}?\n\n" +
                "Le operazioni non reversibili (rimozione app, pulizia file) non verranno ripristinate.",
                "Conferma rollback", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Busy = true;
        Task.Run(() =>
        {
            _engine.Rollback(session);
            _ui.Post(() =>
            {
                LoadSessions();
                Status = $"Rollback della sessione {session.Id} completato";
                Busy = false;
            });
        });
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var s in _engine.Store.LoadAll()) Sessions.Add(s);
    }

    private void Elevate()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"      // richiede l'elevazione UAC
            });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AppendLog("Elevazione annullata dall'utente.");
        }
    }

    public void UpdateStatus()
    {
        Raise(nameof(SelectedCount));
        Status = $"{SelectedCount} tweak selezionati su {Tweaks.Count}";
    }

    private void AppendLog(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

}
